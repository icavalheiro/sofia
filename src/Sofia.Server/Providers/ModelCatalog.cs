using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Server.Data;
using Sofia.Server.Domain;

namespace Sofia.Server.Providers;

/// <summary>Context window and output limit of one model, as reported by the catalog.</summary>
public sealed record ModelLimit(int Context, int MaxOutput);

/// <summary>
/// Model metadata read at runtime from <c>models.dev</c> — the catalog the surrounding ecosystem uses —
/// so Sofia never ships a hardcoded context/output table. The service boots from an embedded snapshot
/// (so a first start without network still knows limits), then refreshes itself at most once per
/// <see cref="MaxAge"/> with an ETag-conditional GET, caching the trimmed result in LiteDB.
/// Lookups prefer the exact <c>(provider, model)</c> pair and only then any provider serving that model id.
/// </summary>
public sealed class ModelCatalog
{
    public const string DefaultUrl = "https://models.dev/api.json";

    /// <summary>How long a fetched catalog is considered fresh; older than this and <see cref="EnsureFreshAsync"/> refetches.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    internal const string SnapshotResource = "Sofia.Server.Providers.ModelsDevSnapshot.json";
    internal const string CacheId = "models";

    /// <summary>Sofia flavor/preset ids that differ from the catalog's provider ids.</summary>
    private static readonly FrozenDictionary<string, string> ProviderAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["copilot"] = "github-copilot",
            ["gemini"] = "google",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly SofiaDb _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ModelCatalog> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile Catalog _catalog = Catalog.Empty;
    private string? _etag;
    private DateTime _updatedAt;

    public ModelCatalog(SofiaDb db, IHttpClientFactory httpFactory, ILogger<ModelCatalog> log)
    {
        _db = db;
        _httpFactory = httpFactory;
        _log = log;

        _catalog = Build(ReadEmbeddedSnapshot());
        if (_catalog.Count == 0)
            _log.LogWarning("Bundled model catalog snapshot is empty; models fall back to {Context} tokens until the first fetch", ModelLimits.FallbackContext);

        // A trimmed catalog fetched by a previous process beats the snapshot: it may be newer.
        try
        {
            if (db.Catalog.FindById(CacheId) is { Body.Length: > 0 } cache)
            {
                var cached = Build(cache.Body);
                if (cached.Count > 0)
                {
                    _catalog = cached;
                    _etag = cache.Etag;
                    _updatedAt = cache.UpdatedAt;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Ignoring the cached model catalog");
        }
    }

    /// <summary>Model limits the catalog knows about, all providers together.</summary>
    public int ModelCount => _catalog.Count;

    /// <summary>When the catalog last came from the network (or was revalidated); <see cref="DateTime.MinValue"/> for the snapshot.</summary>
    public DateTime UpdatedAt => _updatedAt;

    /// <summary>ETag sent with the next conditional GET, when the last response carried one.</summary>
    public string? Etag => _etag;

    /// <summary>Catalog URL; <c>SOFIA_MODELS_URL</c> overrides the default.</summary>
    public static string Url()
    {
        var custom = Environment.GetEnvironmentVariable("SOFIA_MODELS_URL");
        return string.IsNullOrWhiteSpace(custom) ? DefaultUrl : custom.Trim();
    }

    /// <summary>Context window and output limit of a model, or null when the catalog does not know it.</summary>
    public ModelLimit? Lookup(string providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var catalog = _catalog;
        foreach (var candidate in Candidates(providerId))
            if (catalog.ByProvider.TryGetValue(candidate, out var models) && models.TryGetValue(modelId, out var exact))
                return exact;
        return catalog.ByModelId.TryGetValue(modelId, out var loose) ? loose : null;
    }

    /// <summary>The provider key to try: the id itself, then its catalog alias.</summary>
    private static IEnumerable<string> Candidates(string providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId)) yield break;
        var id = providerId.Trim();
        yield return id;
        if (ProviderAliases.TryGetValue(id, out var alias)) yield return alias;
    }

    /// <summary>Refetches when the current catalog is older than <see cref="MaxAge"/>. Never throws.</summary>
    public async Task EnsureFreshAsync(CancellationToken ct = default)
    {
        if (DateTime.UtcNow - _updatedAt < MaxAge) return;
        try
        {
            await RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Model catalog refresh failed");
        }
    }

    /// <summary>
    /// One conditional GET of the catalog. A 304, a network error or a body that does not parse leaves the
    /// current dictionary untouched: callers always see a catalog, never a missing one.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var url = Url();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(_etag)) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            using var response = await _httpFactory.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                _updatedAt = DateTime.UtcNow;
                Persist(body: null, _etag, _updatedAt);
                _log.LogDebug("Model catalog is unchanged (304)");
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Model catalog fetch failed: HTTP {Status} from {Url}", (int)response.StatusCode, url);
                return;
            }

            var raw = await response.Content.ReadAsStringAsync(cts.Token);
            var body = Trim(raw);
            var fetched = body is null ? Catalog.Empty : Build(body);
            if (fetched.Count == 0)
            {
                _log.LogWarning("Model catalog at {Url} carried no model limits; keeping the previous catalog", url);
                return;
            }

            _catalog = fetched;
            _etag = response.Headers.ETag?.Tag;
            _updatedAt = DateTime.UtcNow;
            Persist(body, _etag, _updatedAt);
            _log.LogInformation("Model catalog refreshed: {Models} model limits from {Url}", fetched.Count, url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Offline, DNS failure, timeout, garbage body: keep serving what we have.
            _log.LogWarning("Model catalog fetch failed: {Error}", ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Trims a raw models.dev document to <c>{"provider":{"model":{"c":context,"o":output}}}</c>, the shape
    /// the snapshot and the LiteDB cache use. Returns null when the document is not a JSON object.
    /// </summary>
    internal static string? Trim(string rawJson)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(rawJson);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is not JsonObject providers) return null;

        var trimmed = new JsonObject();
        foreach (var providerId in providers.Select(kv => kv.Key).Order(StringComparer.Ordinal))
        {
            if (providers[providerId]?["models"] is not JsonObject models) continue;
            var kept = new JsonObject();
            foreach (var modelId in models.Select(kv => kv.Key).Order(StringComparer.Ordinal))
            {
                var context = Int(models[modelId]?["limit"]?["context"]);
                if (context is not > 0) continue;
                var output = Int(models[modelId]?["limit"]?["output"]);
                kept[modelId] = new JsonObject { ["c"] = context.Value, ["o"] = output is > 0 ? output.Value : 0 };
            }
            if (kept.Count > 0) trimmed[providerId] = kept;
        }
        return trimmed.ToJsonString();
    }

    /// <summary>Parses the trimmed shape into a provider lookup plus a model-id-only lookup.</summary>
    internal static Catalog Build(string json)
    {
        var byProvider = Parse(json);
        var byModel = new Dictionary<string, ModelLimit>(StringComparer.OrdinalIgnoreCase);
        // Deterministic second pass: providers in name order, first one wins.
        foreach (var providerId in byProvider.Keys.Order(StringComparer.Ordinal))
            foreach (var (modelId, limit) in byProvider[providerId])
                byModel.TryAdd(modelId, limit);
        return new Catalog(byProvider, byModel.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static FrozenDictionary<string, FrozenDictionary<string, ModelLimit>> Parse(string json)
    {
        var result = new Dictionary<string, FrozenDictionary<string, ModelLimit>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        if (root is not JsonObject providers) return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        foreach (var (providerId, providerNode) in providers)
        {
            if (providerNode is not JsonObject modelsNode) continue;
            var models = new Dictionary<string, ModelLimit>(StringComparer.OrdinalIgnoreCase);
            foreach (var (modelId, modelNode) in modelsNode)
            {
                var context = Int(modelNode?["c"]);
                if (context is not > 0) continue;
                var output = Int(modelNode?["o"]);
                var maxOutput = output is > 0 ? output.Value : ModelLimits.DefaultMaxOutput(context.Value);
                models[modelId] = new ModelLimit(context.Value, Math.Min(maxOutput, context.Value));
            }
            if (models.Count > 0) result[providerId] = models.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        int? parsed = value.TryGetValue<int>(out var i) ? i
            : value.TryGetValue<long>(out var l) ? (int)Math.Min(l, int.MaxValue)
            : value.TryGetValue<double>(out var d) ? (int)d
            : null;
        return parsed > 0 ? parsed : null;
    }

    /// <summary>Catalog values in memory; swapped as one immutable reference so readers never see a half update.</summary>
    internal sealed record Catalog(
        FrozenDictionary<string, FrozenDictionary<string, ModelLimit>> ByProvider,
        FrozenDictionary<string, ModelLimit> ByModelId)
    {
        public static readonly Catalog Empty = new(
            FrozenDictionary<string, FrozenDictionary<string, ModelLimit>>.Empty,
            FrozenDictionary<string, ModelLimit>.Empty);

        public int Count => ByProvider.Sum(provider => provider.Value.Count);
    }

    private static string ReadEmbeddedSnapshot()
    {
        try
        {
            using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(SnapshotResource);
            if (stream is null) return "";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Stores the last fetched body (or only the new timestamp/ETag when <paramref name="body"/> is null).</summary>
    private void Persist(string? body, string? etag, DateTime updatedAt)
    {
        try
        {
            var cache = _db.Catalog.FindById(CacheId) ?? new CatalogCache { Id = CacheId };
            if (!string.IsNullOrEmpty(body)) cache.Body = body;
            cache.Etag = etag;
            cache.UpdatedAt = updatedAt;
            _db.Catalog.Upsert(cache);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not persist the model catalog cache");
        }
    }
}
