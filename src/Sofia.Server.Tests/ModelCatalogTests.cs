using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Providers;
using Xunit;

namespace Sofia.Server.Tests;

/// <summary>A trimmed models.dev document used as the network answer in every test.</summary>
internal static class CatalogFixture
{
    public const string Raw = """
    {
      "alpha": { "models": {
        "shared-model": { "limit": { "context": 100000, "output": 10000 } },
        "context-only": { "limit": { "context": 40000 } },
        "no-limit": { "name": "No limit" }
      } },
      "beta": { "models": {
        "shared-model": { "limit": { "context": 200000, "output": 20000 } }
      } },
      "google": { "models": {
        "gemini-test": { "limit": { "context": 999000, "output": 64000 } }
      } },
      "opencode-go": { "models": {
        "beam-free": { "limit": { "context": 1048576, "output": 131072 } }
      } },
      "cachetest": { "models": {
        "cache-only": { "limit": { "context": 777777, "output": 88888 } }
      } }
    }
    """;
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls;
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

internal sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public class ModelCatalogTests
{
    private static HttpResponseMessage Json(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (etag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private static ModelCatalog Catalog(SofiaDb db, HttpMessageHandler handler) =>
        new(db, new StubFactory(handler), NullLogger<ModelCatalog>.Instance);

    private static StubHandler Reachable(string body = CatalogFixture.Raw, string? etag = "\"v1\"") =>
        new(_ => Json(body, etag));

    private static StubHandler Unreachable() =>
        new(_ => throw new HttpRequestException("no network"));

    [Fact]
    public void BundledSnapshotCarriesTheLimitsThatMatter()
    {
        using var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var catalog = Catalog(db, Unreachable());

        Assert.True(catalog.ModelCount > 1000, $"the embedded snapshot only has {catalog.ModelCount} models");
        var beam = catalog.Lookup("opencode-go", "beam-free");
        Assert.NotNull(beam);
        Assert.Equal(1_048_576, beam!.Context);
        Assert.Equal(131_072, beam.MaxOutput);

        // The common Claude ids resolve from the catalog, so the old hardcoded 200K is not needed.
        var sonnet = catalog.Lookup("anthropic", "claude-sonnet-4-5");
        Assert.Equal(200_000, sonnet?.Context);
        Assert.Equal(64_000, sonnet?.MaxOutput);
    }

    [Fact]
    public async Task ExactProviderBeatsModelIdOnlyLookup()
    {
        using var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var catalog = Catalog(db, Reachable());
        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(200_000, catalog.Lookup("beta", "shared-model")?.Context);
        Assert.Equal(100_000, catalog.Lookup("alpha", "shared-model")?.Context);
        // Unknown provider: falls back to the model alone, deterministically the first provider in name order.
        Assert.Equal(100_000, catalog.Lookup("gamma", "shared-model")?.Context);
        // 'gemini' is a Sofia flavor, not a catalog provider id.
        Assert.Equal(999_000, catalog.Lookup("gemini", "gemini-test")?.Context);
    }

    [Fact]
    public async Task OutputFallsBackToTheDefaultWhenTheCatalogOnlyKnowsTheWindow()
    {
        using var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var catalog = Catalog(db, Reachable());
        await catalog.RefreshAsync(CancellationToken.None);
        Assert.Equal(40_000, catalog.Lookup("alpha", "context-only")?.Context);
        Assert.Equal(ModelLimits.DefaultMaxOutput(40_000), catalog.Lookup("alpha", "context-only")?.MaxOutput);
        Assert.Null(catalog.Lookup("alpha", "no-limit"));
        Assert.Null(catalog.Lookup("alpha", "unknown-model"));
    }

    [Fact]
    public void TrimKeepsOnlyContextAndOutput()
    {
        var trimmed = ModelCatalog.Trim(CatalogFixture.Raw);
        Assert.NotNull(trimmed);
        var root = JsonNode.Parse(trimmed!)!.AsObject();
        Assert.Equal(1_048_576, (int)root["opencode-go"]!["beam-free"]!["c"]!);
        Assert.Equal(131_072, (int)root["opencode-go"]!["beam-free"]!["o"]!);
        Assert.Null(root["alpha"]!["no-limit"]);
        Assert.False(trimmed!.Contains("\"name\"", StringComparison.Ordinal));
        Assert.Null(ModelCatalog.Trim("{ not json"));
    }

    [Fact]
    public async Task FetchesTheCatalogAndRevalidatesWithTheEtag()
    {
        using var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var handler = Reachable();
        var catalog = Catalog(db, handler);

        await catalog.RefreshAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
        Assert.Equal("\"v1\"", catalog.Etag);
        Assert.True(catalog.UpdatedAt > DateTime.MinValue);
        Assert.Equal(1_048_576, catalog.Lookup("opencode-go", "beam-free")?.Context);

        // A 304 keeps the dictionary and only bumps the timestamp.
        var notModified = new StubHandler(request =>
        {
            Assert.Equal("\"v1\"", request.Headers.IfNoneMatch.Single().Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var revalidating = Catalog(db, notModified);
        await revalidating.RefreshAsync(CancellationToken.None);
        await revalidating.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, notModified.Calls);
        Assert.Equal(1_048_576, revalidating.Lookup("opencode-go", "beam-free")?.Context);
    }

    [Fact]
    public async Task UnreachableCatalogKeepsTheBundledSnapshot()
    {
        using var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var catalog = Catalog(db, Unreachable());

        await catalog.RefreshAsync(CancellationToken.None);
        await catalog.EnsureFreshAsync(CancellationToken.None);

        Assert.Equal(1_048_576, catalog.Lookup("opencode-go", "beam-free")?.Context);
    }

    [Fact]
    public async Task FreshCatalogIsNotRefetched()
    {
        using var db = new SofiaDb(new ConnectionString { Filename = ":memory:" });
        var handler = Reachable();
        var catalog = Catalog(db, handler);

        await catalog.EnsureFreshAsync(CancellationToken.None);
        await catalog.EnsureFreshAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RestartReusesTheCachedCatalogWithoutTheNetwork()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sofia-catalog-{Guid.NewGuid():N}.db");
        try
        {
            using (var db = new SofiaDb(new ConnectionString { Filename = path }))
            {
                var catalog = Catalog(db, Reachable());
                await catalog.RefreshAsync(CancellationToken.None);
                Assert.Equal(777_777, catalog.Lookup("cachetest", "cache-only")?.Context);
            }

            // A new process: the bundled snapshot does not know 'cache-only', the LiteDB cache does, and the
            // network is down.
            using (var db = new SofiaDb(new ConnectionString { Filename = path }))
            {
                var offline = Unreachable();
                var catalog = Catalog(db, offline);
                Assert.Equal(0, offline.Calls);
                Assert.Equal(777_777, catalog.Lookup("cachetest", "cache-only")?.Context);
                Assert.Equal(88_888, catalog.Lookup("cachetest", "cache-only")?.MaxOutput);
            }
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}

public class ProviderServiceLimitsTests
{
    private static SofiaDb Db() => new(new LiteDB.ConnectionString { Filename = ":memory:" });

    private static ProviderService Providers(SofiaDb db, HttpMessageHandler? handler = null) =>
        new(db, new StubFactory(handler ?? new StubHandler(_ => throw new HttpRequestException("no network"))), new EventBus(),
            new ModelCatalog(db, new StubFactory(new StubHandler(_ => throw new HttpRequestException("no network"))), NullLogger<ModelCatalog>.Instance),
            NullLogger<ProviderService>.Instance);

    [Fact]
    public void CatalogSuppliesLimitsForAModelTheProviderDoesNotDescribe()
    {
        using var db = Db();
        db.Providers.Insert(new ProviderConfig { Id = "opencode-go", Name = "OpenCode Go", Flavor = "opencode-go", BaseUrl = "https://opencode.ai/inference/go/openai/v1" });
        var resolved = Providers(db).Resolve("opencode-go/beam-free");

        Assert.NotNull(resolved);
        Assert.Equal(1_048_576, resolved!.Value.Model.ContextLength);
        Assert.Equal(131_072, resolved.Value.Model.MaxOutput);
        Assert.Equal("1M ctx", resolved.Value.Model.Context);
    }

    [Fact]
    public void UnknownModelFallsBackToTheDocumentedConstant()
    {
        using var db = Db();
        db.Providers.Insert(new ProviderConfig { Id = "openai", Name = "OpenAI", BaseUrl = "https://api.openai.com/v1" });

        var resolved = Providers(db).Resolve("openai/a-model-from-the-future");

        Assert.NotNull(resolved);
        Assert.Equal(ModelLimits.FallbackContext, resolved!.Value.Model.ContextLength);
        Assert.Equal(ModelLimits.DefaultMaxOutput(ModelLimits.FallbackContext), resolved.Value.Model.MaxOutput);
    }

    [Fact]
    public async Task ProviderReportedLimitsWinOverTheCatalog()
    {
        using var db = Db();
        var provider = new ProviderConfig { Id = "opencode-go", Name = "OpenCode Go", Flavor = "opencode-go", BaseUrl = "http://127.0.0.1:9/v1" };
        db.Providers.Insert(provider);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[{"id":"beam-free","context_length":12345,"top_provider":{"max_completion_tokens":4321}}]}"""),
        });

        var models = await Providers(db, handler).ListModelsAsync(provider, CancellationToken.None);

        var beam = Assert.Single(models, m => m.Id == "beam-free");
        Assert.Equal(12345, beam.ContextLength);
        Assert.Equal(4321, beam.MaxOutput);
        Assert.Equal(12345, provider.KnownContext["beam-free"]);
    }
}
