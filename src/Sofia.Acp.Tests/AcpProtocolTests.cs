using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sofia.Acp;

public class FakeSofiaClient : ISofiaClient
{
    public string BaseUrl => "http://localhost";
    public SessionDto Session { get; set; } = new("s1", "w1", PermissionMode: "ask", ModelId: "m1", ReasoningMode: "medium");
    public List<ModelDto> Models { get; set; } = new() { new("m1", "Model 1") };
    public List<MessageDto> Messages { get; set; } = new();
    public Func<SofiaEvent, Task>? EventHandler { get; private set; }
    public bool Aborted { get; private set; }
    public string? LastPermissionDecision { get; private set; }
    public UpdateSessionRequestDto? LastSettings { get; private set; }

    public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(true);
    public Task<WorkspaceDto> FindOrCreateWorkspaceAsync(string cwd, CancellationToken ct) => Task.FromResult(new WorkspaceDto("w1", cwd, "folder"));
    public Task<SessionDto> CreateSessionAsync(string workspaceId, string? title, string? origin, CancellationToken ct) => Task.FromResult(Session);
    public Task<SessionDto> GetSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult(Session);
    public Task<List<MessageDto>> GetMessagesAsync(string sessionId, CancellationToken ct) => Task.FromResult(Messages);
    public Task<string> PromptAsync(string sessionId, PromptInputDto input, CancellationToken ct)
    {
        // emit a delta + idle status asynchronously
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            if (EventHandler is not null)
            {
                await EventHandler(new SofiaEvent("message.delta", sessionId, null, JsonSerializer.SerializeToElement(new { messageId = "m", field = "text", delta = "hello" })));
                await EventHandler(new SofiaEvent("session.status", sessionId, null, JsonSerializer.SerializeToElement(new { status = "idle" })));
            }
        });
        return Task.FromResult("msg1");
    }
    public Task AbortAsync(string sessionId, CancellationToken ct) { Aborted = true; _ = Task.Run(async () => { await Task.Delay(10); if (EventHandler is not null) await EventHandler(new SofiaEvent("session.status", sessionId, null, JsonSerializer.SerializeToElement(new { status = "idle" }))); }); return Task.CompletedTask; }
    public Task<SessionDto> ApplySettingsAsync(string sessionId, UpdateSessionRequestDto req, CancellationToken ct) { LastSettings = req; return Task.FromResult(Session); }
    public Task ReplyPermissionAsync(string sessionId, string requestId, string decision, CancellationToken ct) { LastPermissionDecision = decision; return Task.CompletedTask; }
    public Task AnswerQuestionAsync(string sessionId, string formId, List<JsonElement?> answers, CancellationToken ct) => Task.CompletedTask;
    public Task<List<ModelDto>> GetModelsAsync(CancellationToken ct) => Task.FromResult(Models);
    public Task AddSessionMcpAsync(string sessionId, SessionMcpRequestDto req, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveSessionMcpAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public IDisposable SubscribeEvents(Func<SofiaEvent, Task> handler) { EventHandler = handler; return new Unsub(() => EventHandler = null); }
    private sealed class Unsub(Action a) : IDisposable { public void Dispose() => a(); }
}

public class AcpProtocolTests
{
    private static async Task<List<string>> RunAsync(string input, FakeSofiaClient client)
    {
        var inStream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var outStream = new MemoryStream();
        var conn = new AcpConnection(inStream, outStream);
        var agent = new AcpAgent(conn, client);
        conn.Start(CancellationToken.None);
        await conn.ReadTask;
        await conn.DisposeAsync();
        outStream.Position = 0;
        using var r = new StreamReader(outStream);
        var lines = new List<string>();
        string? line;
        while ((line = await r.ReadLineAsync()) is not null) if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
        return lines;
    }

    [Fact]
    public async Task Initialize_Handshake()
    {
        var client = new FakeSofiaClient();
        var req = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":1,"clientInfo":{"name":"Zed"},"capabilities":{"fs":{"readTextFile":true},"terminal":true}}}""" + "\n";
        var lines = await RunAsync(req, client);
        Assert.True(lines.Count >= 1);
        var resp = JsonNode.Parse(lines[0])!;
        Assert.Equal(1, resp["id"]!.GetValue<int>());
        Assert.NotNull(resp["result"]!["agentCapabilities"]);
    }

    [Fact]
    public async Task SessionNew_Prompt_EndTurn()
    {
        var client = new FakeSofiaClient();
        var sb = new StringBuilder();
        sb.AppendLine("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""");
        sb.AppendLine("""{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"C:/repo"}}""");
        sb.AppendLine("""{"jsonrpc":"2.0","id":3,"method":"session/prompt","params":{"sessionId":"s1","prompt":[{"type":"text","text":"hi"}]}}""");
        var lines = await RunAsync(sb.ToString(), client);
        // find prompt response
        var promptResp = lines.Select(l => JsonNode.Parse(l)!).FirstOrDefault(n => n["id"]?.GetValue<int>() == 3);
        Assert.NotNull(promptResp);
        Assert.Equal("end_turn", promptResp!["result"]!["stopReason"]!.GetValue<string>());
        // ensure a chunk notification was sent
        Assert.Contains(lines, l => l.Contains("agent_message_chunk"));
    }

    [Fact]
    public async Task SetMode_Patches()
    {
        var client = new FakeSofiaClient();
        var sb = new StringBuilder();
        sb.AppendLine("""{"jsonrpc":"2.0","id":1,"method":"session/set_mode","params":{"sessionId":"s1","modeId":"full"}}""");
        var lines = await RunAsync(sb.ToString(), client);
        Assert.Equal("full", client.LastSettings?.PermissionMode);
    }
}
