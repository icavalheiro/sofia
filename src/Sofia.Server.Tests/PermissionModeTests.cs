using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Sofia.Server.Agent;
using Sofia.Server.Infrastructure;
using Sofia.Server.Tools;

namespace Sofia.Server.Tests;

/// <summary>
/// The `workspace` permission mode: everything inside the folder runs without a card, a path that resolves outside
/// it asks, MCP tools still ask, and a chat workspace (no folder) behaves exactly like `ask`.
/// </summary>
public class PermissionModeTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "sofia-workspace-mode-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;
    private readonly WebApplicationFactory<Program> _factory;

    public PermissionModeTests()
    {
        _project = Path.Combine(_home, "project");
        Directory.CreateDirectory(_project);
        // Host configuration, not the SOFIA_HOME env var: this class runs in parallel with every other test class.
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("Sofia:DataDir", Path.Combine(_home, "data"))
            .ConfigureServices(services =>
            {
                services.AddSingleton<IStartupFilter, LoopbackStartupFilter>();
                services.AddHttpClient("llm").ConfigurePrimaryHttpMessageHandler(() => new FakeLlmHandler());
                services.AddHttpClient("probe").ConfigurePrimaryHttpMessageHandler(() => new FakeLlmHandler());
            }));
    }

    // --- the boundary helper ---------------------------------------------------

    [Fact]
    public void IsInsideUnderstandsTheWorkspaceBoundary()
    {
        var root = Path.Combine(Path.GetTempPath(), "sofia-inside-root");

        Assert.True(PathUtil.IsInside(root, root));
        Assert.True(PathUtil.IsInside(root, Path.Combine(root, "src", "a.cs")));
        Assert.True(PathUtil.IsInside(root + Path.DirectorySeparatorChar, Path.Combine(root, "a.cs")));
        Assert.False(PathUtil.IsInside(root, root + "-sibling"));                     // shared prefix, not inside
        Assert.False(PathUtil.IsInside(root, Path.GetDirectoryName(root)!));          // parent
        Assert.False(PathUtil.IsInside(root, Path.Combine(root, "..", "elsewhere"))); // resolved by the caller
        Assert.False(PathUtil.IsInside("", Path.Combine(root, "a.cs")));              // no root, no confinement
    }

    [Fact]
    public void LeavesWorkspaceCoversTheBuiltInPathTools()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "sofia-inside-root");
        var write = new WriteTool();

        Assert.False(SessionRun.LeavesWorkspace(write, Args.Parse("""{"path":"src/a.cs","content":"x"}"""), cwd, cwd));
        Assert.True(SessionRun.LeavesWorkspace(write, Args.Parse("""{"path":"../a.cs","content":"x"}"""), cwd, cwd));
        Assert.True(SessionRun.LeavesWorkspace(write, new JsonObject(), cwd, cwd));   // unknown target asks
        // Read tools without a path work on the cwd, which is the workspace itself.
        Assert.False(SessionRun.LeavesWorkspace(new GrepTool(), Args.Parse("""{"pattern":"x"}"""), cwd, cwd));
        Assert.True(SessionRun.LeavesWorkspace(new GrepTool(), Args.Parse("""{"pattern":"x","path":"/"}"""), cwd, cwd));
        // apply_patch has no path argument: every hunk target counts, move destinations included.
        var patch = new ApplyPatchTool();
        Assert.False(SessionRun.LeavesWorkspace(patch, Args.Parse("""{"input":"*** Begin Patch\n*** Add File: src/a.cs\n+x\n*** End Patch"}"""), cwd, cwd));
        Assert.True(SessionRun.LeavesWorkspace(patch, Args.Parse("""{"input":"*** Begin Patch\n*** Add File: ../a.cs\n+x\n*** End Patch"}"""), cwd, cwd));
        Assert.True(SessionRun.LeavesWorkspace(patch, Args.Parse("""{"input":"*** Begin Patch\n*** Update File: src/a.cs\n*** Move to: ../b.cs\n*** End Patch"}"""), cwd, cwd));
    }

    // --- the gate --------------------------------------------------------------

    [Fact]
    public async Task WritingInsideTheWorkspaceNeverAsks()
    {
        var (http, sessionId, modelId) = await NewSession(Folder: true);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "create hello.txt", modelId, permissionMode = "workspace" });

        var session = await WaitFor(http, sessionId, s =>
            s.GetProperty("messages").EnumerateArray().Any(m => m.TryGetProperty("permission", out _))
            || Answered(s));

        Assert.DoesNotContain(session.GetProperty("messages").EnumerateArray(), m => m.TryGetProperty("permission", out _));
        Assert.Equal("idle", session.GetProperty("status").GetString());
        Assert.Equal("hi\n", (await File.ReadAllTextAsync(Path.Combine(_project, "hello.txt"))).Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task WritingOutsideTheWorkspaceAsks()
    {
        var (http, sessionId, modelId) = await NewSession(Folder: true);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "OUTSIDEWRITE leak", modelId, permissionMode = "workspace" });

        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "permission");
        var requestId = await Deny(http, session, "write", "outside.txt");
        Assert.False(File.Exists(Path.Combine(_home, "outside.txt")));

        session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");
        Assert.Equal("error", LastToolCall(session).GetProperty("status").GetString());
        Assert.False(File.Exists(Path.Combine(_home, "outside.txt")));
    }

    [Fact]
    public async Task ReadingOutsideTheWorkspaceAsks()
    {
        await File.WriteAllTextAsync(Path.Combine(_home, "secret.txt"), "top secret\n");
        var (http, sessionId, modelId) = await NewSession(Folder: true);

        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "OUTSIDEREAD look", modelId, permissionMode = "workspace" });

        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "permission");
        await Deny(http, session, "read", "secret.txt");

        session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");
        var tool = LastToolCall(session);
        Assert.Equal("error", tool.GetProperty("status").GetString());
        Assert.DoesNotContain("top secret", ToolText(tool));
    }

    [Fact]
    public async Task AChatWorkspaceFallsBackToAsk()
    {
        var (http, sessionId, modelId) = await NewSession(Folder: false);

        // No folder means no boundary: the same browser call that is free in a folder workspace asks here.
        await Post(http, $"/api/sessions/{sessionId}/prompt", new { text = "CHATBROWSER look", modelId, permissionMode = "workspace" });

        var session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "permission");
        await Deny(http, session, "browser", "snapshot");
        session = await WaitFor(http, sessionId, s => s.GetProperty("status").GetString() == "idle");
        Assert.Equal("error", LastToolCall(session).GetProperty("status").GetString());
    }

    [Fact]
    public async Task PatchAcceptsTheWorkspaceMode()
    {
        var (http, sessionId, _) = await NewSession(Folder: true);

        using var response = await http.PatchAsJsonAsync($"/api/sessions/{sessionId}", new { permissionMode = "workspace" });

        Assert.True(response.IsSuccessStatusCode);
        var patched = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("workspace", patched.GetProperty("permissionMode").GetString());
    }

    // --- helpers ---------------------------------------------------------------

    private async Task<(HttpClient Http, string SessionId, string ModelId)> NewSession(bool Folder)
    {
        var http = _factory.CreateClient();
        var provider = await Post(http, "/api/providers", new { name = "Fake", type = "openai", baseUrl = "http://fake.llm/v1", apiKey = "test" });
        var modelId = $"{provider.GetProperty("id").GetString()}/fake-model";
        var workspace = Folder
            ? await Post(http, "/api/workspaces", new { kind = "folder", path = _project })
            : await Post(http, "/api/workspaces", new { kind = "chat" });
        var sessionId = workspace.GetProperty("conversations")[0].GetProperty("id").GetString()!;
        return (http, sessionId, modelId);
    }

    /// <summary>Answers the pending permission card with a denial and returns the request id.</summary>
    private static async Task<string> Deny(HttpClient http, JsonElement session, string tool, string pathFragment)
    {
        var card = session.GetProperty("messages").EnumerateArray().Last(m => m.TryGetProperty("permission", out _)).GetProperty("permission");
        Assert.Equal(tool, card.GetProperty("tool").GetString());
        Assert.Contains(pathFragment, card.GetProperty("description").GetString());
        var requestId = card.GetProperty("requestId").GetString()!;
        using var response = await http.PostAsJsonAsync($"/api/sessions/{session.GetProperty("id").GetString()}/permissions/{requestId}", new { decision = "reject" });
        Assert.True(response.IsSuccessStatusCode);
        return requestId;
    }

    private static JsonElement LastToolCall(JsonElement session) =>
        session.GetProperty("messages").EnumerateArray()
            .SelectMany(m => m.TryGetProperty("toolCalls", out var calls) ? calls.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>())
            .Last();

    /// <summary>Everything the tool call surfaced in the transcript, for "the model never saw the content" checks.</summary>
    private static string ToolText(JsonElement tool)
    {
        var text = "";
        foreach (var name in new[] { "output", "error" })
            if (tool.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) text += value.GetString();
        return text;
    }

    /// <summary>True once the assistant produced its plain-text answer (the run finished).</summary>
    private static bool Answered(JsonElement session) =>
        session.GetProperty("messages").EnumerateArray().Any(m =>
            m.TryGetProperty("role", out var role) && role.GetString() == "assistant"
            && m.TryGetProperty("text", out var text) && text.GetString() == "Done");

    private static async Task<JsonElement> Post(HttpClient http, string url, object body)
    {
        using var response = await http.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{url} → {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> WaitFor(HttpClient http, string sessionId, Func<JsonElement, bool> predicate, int tries = 100)
    {
        JsonElement last = default;
        for (var i = 0; i < tries; i++)
        {
            last = await http.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}");
            if (predicate(last)) return last;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Session never reached the expected state: {last}");
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_home, true); } catch { /* SQLite may hold the file briefly */ }
    }
}
