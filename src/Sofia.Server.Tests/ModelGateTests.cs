using Microsoft.Extensions.Logging.Abstractions;
using Sofia.Server.Agent;
using Sofia.Server.Data;
using Sofia.Server.Domain;
using Sofia.Server.Events;
using Sofia.Server.Providers;
using Xunit;

namespace Sofia.Server.Tests;

public class ModelGateTests
{
    [Fact]
    public async Task SubagentsGoFirstAndOwnerKeepsTheModel()
    {
        var gate = new ModelGate();
        await gate.AcquireAsync("m", "A", false, default);
        await gate.AcquireAsync("m", "A", true, default); // re-entrant for the owner
        var b = gate.AcquireAsync("m", "B", false, default);
        var c = gate.AcquireAsync("m", "C", true, default);
        Assert.False(b.IsCompleted);
        gate.Release("A");
        await c.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(b.IsCompleted);
        gate.Release("C");
        await b.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

public class SingleClientModelTests
{
    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static SofiaDb Db() => new(new LiteDB.ConnectionString { Filename = ":memory:" });

    private static ProviderService Providers(SofiaDb db) =>
        new(db, new StubHttpFactory(), new EventBus(), new ModelCatalog(db, new StubHttpFactory(), NullLogger<ModelCatalog>.Instance), NullLogger<ProviderService>.Instance);

    [Fact]
    public void LocalModelsAreSingleClientByDefaultAndCloudModelsStayOptIn()
    {
        using var db = Db();
        db.Providers.Insert(new ProviderConfig { Id = "ollama", Name = "Ollama", Kind = "local", BaseUrl = "http://127.0.0.1:11434/v1" });
        db.Providers.Insert(new ProviderConfig { Id = "openai", Name = "OpenAI", Kind = "cloud", BaseUrl = "https://api.openai.com/v1" });
        var providers = Providers(db);

        Assert.True(providers.IsSingleClient("ollama/llama3"));
        Assert.False(providers.IsSingleClient("openai/gpt-4o-mini"));

        // Switching a local model off records the exception; switching it back on clears it and returns to the default.
        providers.SetSingleClient(["ollama/llama3"], false);
        Assert.False(providers.IsSingleClient("ollama/llama3"));
        Assert.Contains("ollama/llama3", db.GetConfig().MultiClientModels);

        providers.SetSingleClient(["ollama/llama3"], true);
        Assert.True(providers.IsSingleClient("ollama/llama3"));
        Assert.DoesNotContain("ollama/llama3", db.GetConfig().MultiClientModels);

        // Cloud models keep the explicit opt-in.
        providers.SetSingleClient(["openai/gpt-4o-mini"], true);
        Assert.True(providers.IsSingleClient("openai/gpt-4o-mini"));
        Assert.Contains("openai/gpt-4o-mini", db.GetConfig().SingleClientModels);

        providers.SetSingleClient(["openai/gpt-4o-mini"], false);
        Assert.False(providers.IsSingleClient("openai/gpt-4o-mini"));
    }
}
