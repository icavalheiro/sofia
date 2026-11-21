using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Sofia.Client;
using Sofia.Client.InProc;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;
using Sofia.Core;
using Sofia.Core.Infrastructure;

namespace Sofia.Core.Tests;

/// <summary>
/// The in-process transport is the desktop's path to the engine: it must behave like the HTTP API. This boots the
/// real DI graph over a temporary data directory and drives <see cref="ISofiaBackend"/> exactly as a UI would.
/// </summary>
public class InProcBackendTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sofia-inproc-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceProvider _provider;
    private readonly ISofiaBackend _backend;

    public InProcBackendTests()
    {
        Directory.CreateDirectory(_dir);
        var services = new ServiceCollection();
        services.AddLogging();
        // A host provides this; the engine only uses it to observe shutdown.
        services.AddSingleton<IHostApplicationLifetime, Lifetime>();
        services.AddSofiaCore(new SofiaOptions { DataDir = _dir });
        _provider = services.BuildServiceProvider();
        _backend = new InProcSofiaBackend(_provider);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            _stopped.Cancel();
        }
    }

    [Fact]
    public async Task HealthAndConfigComeFromTheSameEngineAsTheHttpApi()
    {
        var health = await _backend.HealthAsync();
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.Equal("sofia", health.GetProperty("name").GetString());

        var config = await _backend.ConfigAsync();
        Assert.True(config.GetProperty("autoCompact").GetBoolean());
        Assert.Equal(Path.Combine(_dir, "sofia.sqlite"), config.GetProperty("configPath").GetString());
    }

    [Fact]
    public async Task WorkspaceSessionAndTranscriptRoundTrip()
    {
        var workspace = await _backend.CreateWorkspaceAsync(new CreateWorkspaceRequest("chat", null, "Group", "chat", null));
        Assert.Equal("chat", workspace.Kind);
        Assert.Single(workspace.Conversations!);

        var listed = await _backend.ListWorkspacesAsync();
        Assert.Contains(listed, w => w.Id == workspace.Id);

        var sessionId = workspace.Conversations![0].Id;
        var created = await _backend.CreateSessionAsync(workspace.Id, new CreateSessionRequest("Second"));
        Assert.Equal("Second", created.Title);

        var session = await _backend.SessionAsync(sessionId);
        Assert.Equal(sessionId, session.GetProperty("id").GetString());
        Assert.Equal(0, session.GetProperty("messages").GetArrayLength());

        var patched = await _backend.UpdateSessionAsync(sessionId, new UpdateSessionRequest("Renamed", null, null, null, null));
        Assert.Equal("Renamed", patched.Title);

        Assert.Empty(await _backend.StatusesAsync());
        Assert.Empty(await _backend.TasksAsync(sessionId));
        await _backend.DeleteSessionAsync(created.Id);
        var (_, remaining) = (await _backend.ListWorkspacesAsync(), (await _backend.ListWorkspacesAsync()).Single().Conversations!);
        Assert.DoesNotContain(remaining, s => s.Id == created.Id);
    }

    [Fact]
    public async Task CatalogsAndAdminViewsAreSerializedLikeTheHttpApi()
    {
        Assert.True((await _backend.ProviderPresetsAsync()).GetArrayLength() > 0);
        Assert.True((await _backend.ModelsAsync()).ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.True((await _backend.ProvidersAsync()).ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.True((await _backend.McpServersAsync()).ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.True((await _backend.LspServersAsync()).ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.True((await _backend.BrowsersAsync()).GetProperty("engines").ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.True((await _backend.AgentsAsync(null)).ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.True((await _backend.CommandsAsync(null)).ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.True((await _backend.SkillsAsync(null)).ValueKind == System.Text.Json.JsonValueKind.Array);
        Assert.Equal(0, (await _backend.StatsAsync()).Sessions);
    }

    [Fact]
    public async Task EventsStreamFromTheBus()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var next = Task.Run(async () =>
        {
            await foreach (var e in _backend.EventsAsync(cts.Token)) return e;
            return null;
        });

        // Give the enumerator a moment to attach, then publish through the ordinary API.
        await Task.Delay(200, cts.Token);
        await _backend.CreateWorkspaceAsync(new CreateWorkspaceRequest("chat", null, "Events", "chat", null));

        var received = await next;
        Assert.NotNull(received);
        Assert.Contains(received!.Type, new[] { EventTypes.WorkspaceUpdated, EventTypes.SessionCreated });
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory does not fail the test.
        }
    }
}
