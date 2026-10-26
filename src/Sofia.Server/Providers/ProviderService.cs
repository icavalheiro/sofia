using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Infrastructure;

namespace Sofia.Server.Providers;

public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Provider { get; set; } = "";
    public bool Local { get; set; }
    public int ContextLength { get; set; }
    public int MaxOutput { get; set; }
    public string Context { get; set; } = "";
    public bool Connected { get; set; }
    public bool Reasoning { get; set; }
    public bool Vision { get; set; }
    public bool? Loaded { get; set; }
    /// <summary>False when the user switched this model off in Settings.</summary>
    public bool Enabled { get; set; } = true;
    [JsonIgnore] public double InputPrice { get; set; }
    [JsonIgnore] public double OutputPrice { get; set; }

    public double Cost(Usage usage) =>
        (Math.Max(0, usage.Input - usage.CacheRead) * InputPrice + usage.CacheRead * InputPrice * 0.1 + usage.Output * OutputPrice) / 1_000_000;
}

public sealed class ProviderView
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "cloud";
    public string Type { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string Status { get; set; } = "disconnected";
    public string Detail { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Custom { get; set; }
    public bool Discovered { get; set; }
    public bool HasKey { get; set; }
    public int Models { get; set; }
}

public sealed record ProviderPreset(string Id, string Name, string Type, string BaseUrl, string? EnvKey, string Flavor, string? Auth = null, string? Note = null);

public sealed record LocalRuntime(string Id, string Name, string BaseUrl, string Flavor);

/// <summary>Provider registry: presets, local auto-discovery, model catalog and client factory.</summary>
public sealed class ProviderService(SofiaDb db, IHttpClientFactory httpFactory, IEventBus bus, ILogger<ProviderService> log)
{
    public static readonly ProviderPreset[] Presets =
    [
        new("openai", "OpenAI", "openai", "https://api.openai.com/v1", "OPENAI_API_KEY", "openai"),
        new("anthropic", "Anthropic", "anthropic", "https://api.anthropic.com", "ANTHROPIC_API_KEY", "anthropic"),
        new("openrouter", "OpenRouter", "openai", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY", "openrouter"),
        new("google", "Google Gemini", "openai", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY", "gemini"),
        new("deepseek", "DeepSeek Platform", "openai", "https://api.deepseek.com", "DEEPSEEK_API_KEY", "deepseek", Note: "Pay-as-you-go API key from platform.deepseek.com."),
        new("groq", "Groq", "openai", "https://api.groq.com/openai/v1", "GROQ_API_KEY", "groq"),
        new("mistral", "Mistral", "openai", "https://api.mistral.ai/v1", "MISTRAL_API_KEY", "mistral"),
        new("xai", "xAI", "openai", "https://api.x.ai/v1", "XAI_API_KEY", "xai"),
        new("opencode-go", "OpenCode Go", "openai", "https://opencode.ai/inference/go/openai/v1", "OPENCODE_API_KEY", "opencode-go",
            Note: "Subscription (Go / Go Plus). API key from the OpenCode Console."),
        new("qwencloud", "QwenCloud Token Plan", "openai", "https://token-plan.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1", "QWENCLOUD_API_KEY", "qwencloud",
            Note: "Subscription (Token Plan, Singapore region). Use the Token Plan API key, not a pay-as-you-go key."),
        new("github-copilot", "GitHub Copilot", "openai", "https://api.githubcopilot.com", "COPILOT_GITHUB_TOKEN", "copilot", Auth: "github-device",
            Note: "Subscription. Sign in with GitHub, or paste a GitHub token."),
    ];

    // Flavors billed through a flat subscription: per-token prices would be misleading.
    private static readonly HashSet<string> SubscriptionFlavors = ["copilot", "opencode-go", "qwencloud"];

    private readonly CopilotAuth _copilot = new(httpFactory);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, ProviderState> _states = new();
    private readonly object _gate = new();

    private sealed class ProviderState
    {
        public string Status = "disconnected";
        public string Detail = "";
        public List<ModelInfo> Models = [];
    }

    public static IReadOnlyList<LocalRuntime> LocalRuntimes()
    {
        var list = new List<LocalRuntime>
        {
            new("unsloth", "Unsloth Studio", "http://127.0.0.1:8888/v1", "unsloth"),
            new("ollama", "Ollama", OllamaBase(), "ollama"),
            new("lmstudio", "LM Studio", "http://127.0.0.1:1234/v1", "lmstudio"),
            new("llamacpp", "llama.cpp", "http://127.0.0.1:8080/v1", "llamacpp"),
            new("vllm", "vLLM", "http://127.0.0.1:8000/v1", "vllm"),
        };
        // SOFIA_LOCAL_ENDPOINTS="name=http://host:port/v1,other=…" adds extra OpenAI-compatible runtimes.
        var extra = Environment.GetEnvironmentVariable("SOFIA_LOCAL_ENDPOINTS");
        if (!string.IsNullOrWhiteSpace(extra))
        {
            foreach (var entry in extra.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = entry.Split('=', 2);
                var url = parts.Length == 2 ? parts[1] : parts[0];
                var name = parts.Length == 2 ? parts[0] : new Uri(url).Authority;
                list.Add(new($"local-{Slug(name)}", name, url, "local"));
            }
        }
        return list;
    }

    private static string OllamaBase()
    {
        var host = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (string.IsNullOrWhiteSpace(host)) return "http://127.0.0.1:11434/v1";
        if (!host.StartsWith("http", StringComparison.OrdinalIgnoreCase)) host = "http://" + host;
        return host.TrimEnd('/').Replace("0.0.0.0", "127.0.0.1") + "/v1";
    }

    private static string Slug(string value) =>
        new string(value.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    /// <summary>Creates provider rows for cloud presets whose API key is in the environment.</summary>
    public void SeedFromEnvironment()
    {
        foreach (var preset in Presets)
        {
            if (preset.EnvKey is null || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(preset.EnvKey))) continue;
            if (db.Providers.FindById(preset.Id) is not null) continue;
            db.Providers.Insert(new ProviderConfig
            {
                Id = preset.Id,
                Name = preset.Name,
                Type = preset.Type,
                Kind = "cloud",
                BaseUrl = preset.BaseUrl,
                ApiKeyEnv = preset.EnvKey,
                PresetId = preset.Id,
                Flavor = preset.Flavor,
            });
        }
    }

    public async Task RefreshAsync(bool discover, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (discover) await DiscoverLocalAsync(ct);
            var providers = db.Providers.FindAll().ToList();
            await Task.WhenAll(providers.Select(p => RefreshProviderAsync(p, ct)));
            lock (_gate)
            {
                foreach (var id in _states.Keys.Where(id => providers.All(p => p.Id != id)).ToList()) _states.Remove(id);
            }
        }
        finally
        {
            _refreshGate.Release();
        }
        bus.Publish(EventTypes.ModelsUpdated, new { });
    }

    private async Task DiscoverLocalAsync(CancellationToken ct)
    {
        await Task.WhenAll(LocalRuntimes().Select(async runtime =>
        {
            var existing = db.Providers.FindById(runtime.Id);
            if (existing is { Discovered: false }) return;
            var reachable = await ProbeReachableAsync(runtime.BaseUrl, ct);
            if (!reachable) return;
            if (existing is null)
            {
                db.Providers.Insert(new ProviderConfig
                {
                    Id = runtime.Id,
                    Name = runtime.Name,
                    Type = "openai",
                    Kind = "local",
                    BaseUrl = runtime.BaseUrl,
                    Discovered = true,
                    Flavor = runtime.Flavor,
                });
                log.LogInformation("Discovered local runtime {Name} at {Url}", runtime.Name, runtime.BaseUrl);
            }
        }));
    }

    private async Task<bool> ProbeReachableAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient("probe");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            using var response = await http.GetAsync($"{baseUrl.TrimEnd('/')}/models", cts.Token);
            if (!response.IsSuccessStatusCode) return false;
            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            return json?["data"] is JsonArray;
        }
        catch (Exception ex)
        {
            log.LogDebug("Probe of {Url} failed: {Error}", baseUrl, ex.Message);
            return false;
        }
    }

    private async Task RefreshProviderAsync(ProviderConfig provider, CancellationToken ct)
    {
        var state = new ProviderState();
        if (!provider.Enabled)
        {
            state.Status = "disconnected";
            state.Detail = "Disabled";
        }
        else if (provider.Kind == "cloud" && string.IsNullOrWhiteSpace(provider.ResolveKey()))
        {
            state.Status = "disconnected";
            state.Detail = "No API key";
        }
        else
        {
            try
            {
                var models = await ListModelsAsync(provider, ct);
                if (provider.KnownContext.Count > 0 && db.Providers.FindById(provider.Id) is { } fresh)
                {
                    // Re-read so a concurrent settings change is not overwritten.
                    fresh.KnownContext = provider.KnownContext;
                    db.Providers.Update(fresh);
                }
                state.Models = models.Select(m => ToInfo(provider, m)).ToList();
                state.Status = "connected";
                var loaded = state.Models.Count(m => m.Loaded == true);
                state.Detail = provider.Kind == "local"
                    ? $"{state.Models.Count} models{(loaded > 0 ? $" · {loaded} loaded" : "")} · {new Uri(provider.BaseUrl).Authority}"
                    : $"{(string.IsNullOrEmpty(provider.ApiKey) && provider.ApiKeyEnv is not null ? $"Key from {provider.ApiKeyEnv}" : "API key set")} · {state.Models.Count} models";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                state.Status = provider.Kind == "local" ? "disconnected" : "error";
                state.Detail = provider.Kind == "local" ? "Not running" : Text.FirstLine(ex.Message, 160);
                // Keep declared models selectable even when listing fails.
                state.Models = provider.Models.Select(id => ToInfo(provider, Fallback(id))).ToList();
                foreach (var m in state.Models) m.Connected = false;
            }
        }
        lock (_gate) _states[provider.Id] = state;
    }

    private static DiscoveredModel Fallback(string id)
    {
        var known = ModelLimits.Known(id);
        var ctx = known?.Context ?? ModelLimits.FallbackContext;
        return new DiscoveredModel(id, id, ctx, known?.MaxOutput ?? ModelLimits.DefaultMaxOutput(ctx),
            known?.Reasoning ?? ModelLimits.LooksReasoning(id), known?.Vision ?? ModelLimits.LooksVision(id), null);
    }

    private static ModelInfo ToInfo(ProviderConfig provider, DiscoveredModel m)
    {
        var known = ModelLimits.Known(m.Id);
        return new ModelInfo
        {
            Id = $"{provider.Id}/{m.Id}",
            ProviderId = provider.Id,
            ModelId = m.Id,
            Label = m.Label,
            Provider = provider.Name,
            Local = provider.Kind == "local",
            ContextLength = m.ContextLength,
            MaxOutput = m.MaxOutput,
            Context = Text.ContextLabel(m.ContextLength),
            // Ollama, LM Studio and Unsloth Studio load models on demand, so "not loaded" is still usable.
            Connected = provider.Flavor is "ollama" or "lmstudio" or "unsloth" || (m.Loaded ?? true),
            Reasoning = m.Reasoning,
            Vision = m.Vision,
            Loaded = m.Loaded,
            InputPrice = provider.Kind == "local" || SubscriptionFlavors.Contains(provider.Flavor) ? 0 : known?.InputPrice ?? 0,
            OutputPrice = provider.Kind == "local" || SubscriptionFlavors.Contains(provider.Flavor) ? 0 : known?.OutputPrice ?? 0,
        };
    }

    public CopilotAuth Copilot => _copilot;

    /// <summary>Key, base URL and headers for a provider; GitHub Copilot needs a token exchange first.</summary>
    private Task<RequestAuth> AuthAsync(ProviderConfig provider, CancellationToken ct) =>
        provider.Flavor == "copilot" ? _copilot.ResolveAsync(provider, ct) : Task.FromResult(RequestAuth.From(provider));

    /// <summary>Lists models from the endpoint, enriched with runtime-specific context metadata.</summary>
    public async Task<List<DiscoveredModel>> ListModelsAsync(ProviderConfig provider, CancellationToken ct)
    {
        var http = httpFactory.CreateClient("probe");
        var auth = await AuthAsync(provider, ct);
        var baseUrl = (auth.BaseUrl ?? provider.BaseUrl).TrimEnd('/');
        var key = auth.Key;
        var result = new List<DiscoveredModel>();

        if (provider.Type == "anthropic")
        {
            var url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{baseUrl}/models?limit=100" : $"{baseUrl}/v1/models?limit=100";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(key)) req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
            ProviderIdentity.ApplyIdentity(req, provider, null);
            var json = await SendJsonAsync(http, req, ct);
            foreach (var item in json?["data"]?.AsArray() ?? [])
            {
                var id = item?["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                var known = ModelLimits.Known(id);
                result.Add(new DiscoveredModel(id, item?["display_name"]?.GetValue<string>() ?? id, known?.Context ?? 200_000,
                    known?.MaxOutput ?? 8192, known?.Reasoning ?? false, true, null));
            }
            return MergeDeclared(provider, result);
        }

        var modelsUrl = provider.Flavor == "opencode-go" ? OpenCodeGo.ModelsUrl(baseUrl) : $"{baseUrl}/models";
        using (var req = new HttpRequestMessage(HttpMethod.Get, modelsUrl))
        {
            if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new("Bearer", key);
            foreach (var (name, value) in auth.Headers ?? new Dictionary<string, string>()) ProviderIdentity.ApplyHeader(req, name, value);
            ProviderIdentity.ApplyIdentity(req, provider, null);
            var json = await SendJsonAsync(http, req, ct);
            if (provider.Flavor == "copilot") return MergeDeclared(provider, ParseCopilotModels(json));
            foreach (var item in json?["data"]?.AsArray() ?? [])
            {
                if (item is null) continue;
                var id = item["id"]?.GetValue<string>();
                if (string.IsNullOrEmpty(id)) continue;
                if (provider.Flavor == "opencode-go" && OpenCodeGo.WireFor(id) == OpenCodeGo.Wire.Unsupported) continue;
                var task = item["task"]?.GetValue<string>();
                if (task is not null && !task.Contains("text-generation", StringComparison.OrdinalIgnoreCase) && !task.Contains("chat", StringComparison.OrdinalIgnoreCase)) continue;
                if (id.Contains("embed", StringComparison.OrdinalIgnoreCase) || id.Contains("whisper", StringComparison.OrdinalIgnoreCase)
                    || id.Contains("tts", StringComparison.OrdinalIgnoreCase) || id.Contains("dall-e", StringComparison.OrdinalIgnoreCase)) continue;

                var known = ModelLimits.Known(id);
                var runtimeCtx = Int(item["context_length"]) ?? Int(item["max_context_length"]) ?? Int(item["max_model_len"])
                    ?? Int(item["meta"]?["n_ctx_train"]) ?? Int(item["context_window"]) ?? Int(item["inputTokenLimit"]);
                if (runtimeCtx is { } reported) provider.KnownContext[id] = reported;
                var ctx = runtimeCtx ?? (provider.KnownContext.TryGetValue(id, out var remembered) ? remembered : (int?)null)
                    ?? known?.Context ?? ModelLimits.FallbackContext;
                var maxOut = Int(item["top_provider"]?["max_completion_tokens"]) ?? known?.MaxOutput ?? ModelLimits.DefaultMaxOutput(ctx);
                bool? loaded = item["loaded"] is JsonValue lv && lv.TryGetValue<bool>(out var l) ? l : null;
                var label = item["display_name"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? id;
                var reasoning = known?.Reasoning ?? ModelLimits.LooksReasoning(id);
                var supported = item["supported_parameters"] as JsonArray;
                if (supported is not null) reasoning = supported.Any(p => p?.GetValue<string>() is "reasoning" or "include_reasoning");
                var vision = known?.Vision ?? ModelLimits.LooksVision(id);
                if (item["architecture"]?["input_modalities"] is JsonArray modalities) vision = modalities.Any(x => x?.GetValue<string>() == "image");
                result.Add(new DiscoveredModel(id, label, ctx, Math.Min(maxOut, ctx), reasoning, vision, loaded));
            }
        }

        result = provider.Flavor switch
        {
            "llamacpp" => await EnrichLlamaCppAsync(http, baseUrl, result, ct),
            "ollama" => await EnrichOllamaAsync(http, baseUrl, result, ct),
            "lmstudio" => await EnrichLmStudioAsync(http, baseUrl, result, ct),
            _ => result,
        };
        return MergeDeclared(provider, result);
    }

    private static List<DiscoveredModel> MergeDeclared(ProviderConfig provider, List<DiscoveredModel> found)
    {
        foreach (var id in provider.Models)
            if (found.All(m => m.Id != id)) found.Add(Fallback(id));
        return found;
    }

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        int? value = v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<long>(out var l) ? (int)l
            : v.TryGetValue<double>(out var d) ? (int)d
            : null;
        return value > 0 ? value : null;
    }

    private static string RootOf(string baseUrl) =>
        baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^3] : baseUrl;

    private static async Task<JsonNode?> SendJsonAsync(HttpClient http, HttpRequestMessage req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.SendAsync(req, cts.Token);
        if (!response.IsSuccessStatusCode) throw new ProviderException(await Sse.ErrorMessageAsync(response, cts.Token), (int)response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
    }

    private static async Task<List<DiscoveredModel>> EnrichLlamaCppAsync(HttpClient http, string baseUrl, List<DiscoveredModel> models, CancellationToken ct)
    {
        try
        {
            var props = await SendJsonAsync(http, new HttpRequestMessage(HttpMethod.Get, $"{RootOf(baseUrl)}/props"), ct);
            var nctx = Int(props?["default_generation_settings"]?["n_ctx"]) ?? Int(props?["n_ctx"]);
            var vision = props?["modalities"]?["vision"] is JsonValue vv && vv.TryGetValue<bool>(out var v) && v;
            if (nctx is null) return models;
            return models.Select(m => m with { ContextLength = nctx.Value, MaxOutput = Math.Min(m.MaxOutput, nctx.Value), Vision = m.Vision || vision, Loaded = true }).ToList();
        }
        catch
        {
            return models;
        }
    }

    private static async Task<List<DiscoveredModel>> EnrichOllamaAsync(HttpClient http, string baseUrl, List<DiscoveredModel> models, CancellationToken ct)
    {
        var root = RootOf(baseUrl);
        var loaded = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var ps = await SendJsonAsync(http, new HttpRequestMessage(HttpMethod.Get, $"{root}/api/ps"), ct);
            foreach (var m in ps?["models"]?.AsArray() ?? [])
                if (m?["name"]?.GetValue<string>() is { } name) loaded[name] = Int(m["context_length"]);
        }
        catch
        {
            // Older Ollama builds have no /api/ps.
        }

        var enriched = new List<DiscoveredModel>();
        foreach (var model in models)
        {
            var item = model with { Loaded = loaded.ContainsKey(model.Id) };
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{root}/api/show")
                {
                    Content = JsonContent.Create(new { model = model.Id }),
                };
                var show = await SendJsonAsync(http, req, ct);
                var info = show?["model_info"] as JsonObject;
                var ctx = info?.Where(kv => kv.Key.EndsWith(".context_length", StringComparison.Ordinal)).Select(kv => Int(kv.Value)).FirstOrDefault(x => x is not null);
                if (loaded.TryGetValue(model.Id, out var runtimeCtx) && runtimeCtx is not null) ctx = runtimeCtx;
                var caps = show?["capabilities"] as JsonArray;
                var hasVision = caps?.Any(c => c?.GetValue<string>() == "vision") ?? item.Vision;
                var hasThinking = caps?.Any(c => c?.GetValue<string>() == "thinking") ?? item.Reasoning;
                if (ctx is not null) item = item with { ContextLength = ctx.Value, MaxOutput = ModelLimits.DefaultMaxOutput(ctx.Value) };
                item = item with { Vision = hasVision, Reasoning = hasThinking };
            }
            catch
            {
                // Keep the generic metadata.
            }
            enriched.Add(item);
        }
        return enriched;
    }

    private static async Task<List<DiscoveredModel>> EnrichLmStudioAsync(HttpClient http, string baseUrl, List<DiscoveredModel> models, CancellationToken ct)
    {
        try
        {
            var json = await SendJsonAsync(http, new HttpRequestMessage(HttpMethod.Get, $"{RootOf(baseUrl)}/api/v0/models"), ct);
            var byId = (json?["data"]?.AsArray() ?? []).Where(x => x?["id"] is not null).ToDictionary(x => x!["id"]!.GetValue<string>(), x => x!);
            return models
                .Where(m => !byId.TryGetValue(m.Id, out var meta) || meta["type"]?.GetValue<string>() != "embeddings")
                .Select(m =>
                {
                    if (!byId.TryGetValue(m.Id, out var meta)) return m;
                    var ctx = Int(meta["loaded_context_length"]) ?? Int(meta["max_context_length"]) ?? m.ContextLength;
                    return m with
                    {
                        ContextLength = ctx,
                        MaxOutput = ModelLimits.DefaultMaxOutput(ctx),
                        Vision = m.Vision || meta["type"]?.GetValue<string>() == "vlm",
                        Loaded = meta["state"]?.GetValue<string>() == "loaded",
                    };
                }).ToList();
        }
        catch
        {
            return models;
        }
    }

    public List<ModelInfo> Models()
    {
        var disabled = db.GetConfig().DisabledModels.ToHashSet();
        lock (_gate)
        {
            foreach (var m in _states.Values.SelectMany(s => s.Models)) m.Enabled = !disabled.Contains(m.Id);
            return _states.Values.SelectMany(s => s.Models)
                .OrderByDescending(m => m.Connected)
                .ThenByDescending(m => m.Local)
                .ThenBy(m => m.Provider)
                .ThenBy(m => m.Label)
                .ToList();
        }
    }

    /// <summary>Switches models on or off for the harness and tells clients to reload the catalog.</summary>
    public void SetModelsEnabled(IReadOnlyCollection<string> modelIds, bool enabled)
    {
        var config = db.GetConfig();
        config.DisabledModels = config.DisabledModels.Where(id => !modelIds.Contains(id)).ToList();
        if (!enabled) config.DisabledModels.AddRange(modelIds);
        db.SaveConfig(config);
        bus.Publish(EventTypes.ModelsUpdated, new { });
    }

    public List<ProviderView> Providers()
    {
        var configs = db.Providers.FindAll().ToList();
        lock (_gate)
        {
            return configs.Select(p =>
            {
                _states.TryGetValue(p.Id, out var s);
                return new ProviderView
                {
                    Id = p.Id,
                    Name = p.Name,
                    Kind = p.Kind,
                    Type = p.Type,
                    BaseUrl = p.BaseUrl,
                    Status = s?.Status ?? "disconnected",
                    Detail = s?.Detail ?? "Not checked yet",
                    Enabled = p.Enabled,
                    Custom = p.Custom,
                    Discovered = p.Discovered,
                    HasKey = !string.IsNullOrWhiteSpace(p.ResolveKey()),
                    Models = s?.Models.Count ?? 0,
                };
            }).OrderBy(p => p.Kind == "local" ? 0 : 1).ThenBy(p => p.Name).ToList();
        }
    }

    public ProviderView? Provider(string id) => Providers().FirstOrDefault(p => p.Id == id);

    /// <summary>Resolves 'providerId/modelId' (model ids may contain slashes themselves).</summary>
    public (ProviderConfig Provider, ModelInfo Model)? Resolve(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        var slash = modelId.IndexOf('/');
        if (slash <= 0) return null;
        var providerId = modelId[..slash];
        var config = db.Providers.FindById(providerId);
        if (config is null) return null;
        ModelInfo? model;
        lock (_gate)
        {
            model = _states.TryGetValue(providerId, out var s) ? s.Models.FirstOrDefault(m => m.Id == modelId) : null;
        }
        model ??= ToInfo(config, Fallback(modelId[(slash + 1)..]));
        return (config, model);
    }

    public string DefaultModelId()
    {
        var configured = db.GetConfig().DefaultModel;
        var models = Models().Where(m => m.Enabled).ToList();
        if (!string.IsNullOrEmpty(configured) && models.Any(m => m.Id == configured)) return configured;
        return models.FirstOrDefault(m => m is { Connected: true, Local: true, Loaded: true })?.Id
            ?? models.FirstOrDefault(m => m.Connected)?.Id
            ?? configured;
    }

    public IChatClient CreateClient(ProviderConfig provider, string? modelId = null)
    {
        var http = httpFactory.CreateClient("llm");
        if (provider.Flavor == "opencode-go" && modelId is not null && OpenCodeGo.WireFor(modelId) == OpenCodeGo.Wire.Anthropic)
            return new AnthropicChatClient(http, OpenCodeGo.ForAnthropic(provider));
        if (provider.Type == "anthropic") return new AnthropicChatClient(http, provider);
        ProviderAuthResolver? auth = provider.Flavor == "copilot" ? ct => _copilot.ResolveAsync(provider, ct) : null;
        return new OpenAiChatClient(http, provider, auth);
    }

    /// <summary>Copilot's catalog mixes chat models with embeddings, hidden picks and Responses-only models.</summary>
    internal static List<DiscoveredModel> ParseCopilotModels(JsonNode? json)
    {
        var result = new List<DiscoveredModel>();
        foreach (var item in json?["data"]?.AsArray() ?? [])
        {
            var id = item?["id"]?.GetValue<string>();
            if (item is null || string.IsNullOrEmpty(id) || result.Any(m => m.Id == id)) continue;
            var caps = item["capabilities"];
            if (caps?["type"]?.GetValue<string>() is { } type && type != "chat") continue;
            if (item["model_picker_enabled"] is JsonValue picker && picker.TryGetValue<bool>(out var shown) && !shown) continue;
            if (caps?["supports"]?["tool_calls"] is JsonValue tools && tools.TryGetValue<bool>(out var canTool) && !canTool) continue;
            if (item["supported_endpoints"] is JsonArray endpoints
                && !endpoints.Any(e => e?.GetValue<string>() == "/chat/completions")) continue;

            var known = ModelLimits.Known(id);
            var ctx = Int(caps?["limits"]?["max_context_window_tokens"]) ?? known?.Context ?? 128_000;
            var maxOut = Int(caps?["limits"]?["max_output_tokens"]) ?? known?.MaxOutput ?? ModelLimits.DefaultMaxOutput(ctx);
            var vision = caps?["supports"]?["vision"] is JsonValue v && v.TryGetValue<bool>(out var hasVision) ? hasVision : known?.Vision ?? ModelLimits.LooksVision(id);
            var reasoning = known?.Reasoning ?? ModelLimits.LooksReasoning(id);
            result.Add(new DiscoveredModel(id, item["name"]?.GetValue<string>() ?? id, ctx, Math.Min(maxOut, ctx), reasoning, vision, null));
        }
        return result;
    }

    public async Task<List<ModelInfo>> ProbeAsync(string type, string baseUrl, string? apiKey, CancellationToken ct)
    {
        var temp = new ProviderConfig
        {
            Id = "probe",
            Name = "probe",
            Type = type,
            BaseUrl = baseUrl,
            ApiKey = apiKey,
            Kind = IsLoopback(baseUrl) ? "local" : "cloud",
            Flavor = GuessFlavor(baseUrl, type),
        };
        var models = await ListModelsAsync(temp, ct);
        return models.Select(m => ToInfo(temp, m)).ToList();
    }

    public static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host is "localhost" or "0.0.0.0");

    public static string GuessFlavor(string baseUrl, string type)
    {
        if (type == "anthropic") return "anthropic";
        var url = baseUrl.ToLowerInvariant();
        if (url.Contains("openrouter.ai")) return "openrouter";
        if (url.Contains("api.openai.com")) return "openai";
        if (url.Contains("generativelanguage.googleapis.com")) return "gemini";
        if (url.Contains("api.x.ai")) return "xai";
        if (url.Contains("api.groq.com")) return "groq";
        if (url.Contains("api.deepseek.com")) return "deepseek";
        if (url.Contains("opencode.ai/inference/go")) return "opencode-go";
        if (url.Contains(".maas.aliyuncs.com")) return "qwencloud";
        if (url.Contains("githubcopilot.com")) return "copilot";
        if (url.Contains(":11434")) return "ollama";
        if (url.Contains(":8888")) return "unsloth";
        if (url.Contains(":1234")) return "lmstudio";
        return IsLoopback(baseUrl) ? "local" : "generic";
    }
}
