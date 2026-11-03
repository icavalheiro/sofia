using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;

namespace Sofia.Acp;

public sealed record WorkspaceDto(string Id, string? Path = null, string? Kind = null);
public sealed record SessionDto(
    string Id,
    string WorkspaceId,
    string? ParentId = null,
    string? Title = null,
    string? ModelId = null,
    string? Agent = null,
    string? PermissionMode = null,
    string? ReasoningMode = null,
    string? Status = null,
    string? Origin = null);
public sealed record MessageDto(
    string Id,
    string SessionId,
    string Role,
    string Text,
    string? Reasoning = null,
    List<ToolCallDto>? ToolCalls = null,
    PermissionCardDto? Permission = null,
    QuestionCardDto? Question = null,
    QuestionSetCardDto? QuestionSet = null,
    bool? Summary = null);
public sealed record ToolCallDto(string Id, string Name, string Status, string? Input = null, string? Output = null, string? Error = null);
public sealed record PermissionCardDto(string Tool, string Description, List<string> Choices, string? Resolved, string RequestId);
public sealed record QuestionCardDto(string Prompt, List<string> Options, string? Resolved, string FormId, string FieldKey, bool Multi);
public sealed record QuestionSetCardDto(List<QuestionItemDto> Questions, List<string?>? Answers, bool? Confirmed, string FormId);
public sealed record QuestionItemDto(string Prompt, List<string> Options, string FieldKey, bool Multi);
public sealed record ModelDto(string Id, string Name);
public sealed record SofiaEvent(string Type, string? SessionId, string? WorkspaceId, JsonElement Data);
public sealed record PromptInputDto(string Text, List<string>? Attachments = null, string? Delivery = null, string? ModelId = null, string? Agent = null, string? PermissionMode = null, string? ReasoningMode = null, string? Language = null);
public sealed record UpdateSessionRequestDto(string? Title = null, string? ModelId = null, string? Agent = null, string? PermissionMode = null, string? ReasoningMode = null);
public sealed record SessionMcpRequestDto(string? Name, string? Transport = null, string? Command = null, List<string>? Args = null, string? Url = null, string? Token = null, Dictionary<string, string>? Env = null);

public interface ISofiaClient
{
    string BaseUrl { get; }
    Task<bool> IsHealthyAsync(CancellationToken ct);
    Task<WorkspaceDto> FindOrCreateWorkspaceAsync(string cwd, CancellationToken ct);
    Task<SessionDto> CreateSessionAsync(string workspaceId, string? title, string? origin, CancellationToken ct);
    Task<SessionDto> GetSessionAsync(string sessionId, CancellationToken ct);
    Task<List<MessageDto>> GetMessagesAsync(string sessionId, CancellationToken ct);
    Task<string> PromptAsync(string sessionId, PromptInputDto input, CancellationToken ct);
    Task AbortAsync(string sessionId, CancellationToken ct);
    Task<SessionDto> ApplySettingsAsync(string sessionId, UpdateSessionRequestDto req, CancellationToken ct);
    Task ReplyPermissionAsync(string sessionId, string requestId, string decision, CancellationToken ct);
    Task AnswerQuestionAsync(string sessionId, string formId, List<JsonElement?> answers, CancellationToken ct);
    Task<List<ModelDto>> GetModelsAsync(CancellationToken ct);
    Task AddSessionMcpAsync(string sessionId, SessionMcpRequestDto req, CancellationToken ct);
    Task RemoveSessionMcpAsync(string sessionId, CancellationToken ct);
    IDisposable SubscribeEvents(Func<SofiaEvent, Task> handler);
}

public sealed class SofiaClient : ISofiaClient, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly List<Func<SofiaEvent, Task>> _handlers = new();
    private readonly object _gate = new();
    private HubConnection? _hub;
    private bool _started;

    public SofiaClient(string? baseUrl = null)
    {
        _baseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("SOFIA_SERVER_URL") ?? "http://127.0.0.1:5199").TrimEnd('/');
        _http = new HttpClient { BaseAddress = new Uri(_baseUrl + "/"), Timeout = TimeSpan.FromSeconds(30) };
    }

    public string BaseUrl => _baseUrl;

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var resp = await _http.GetAsync("api/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task EnsureHubAsync()
    {
        if (_started) return;
        lock (_gate)
        {
            if (_started) return;
            _hub = new HubConnectionBuilder().WithUrl(_baseUrl + "/hubs/events").Build();
            _hub.On<JsonElement>("event", payload =>
            {
                var ev = ParseEvent(payload);
                List<Func<SofiaEvent, Task>> snapshot;
                lock (_gate) snapshot = _handlers.ToList();
                foreach (var h in snapshot) _ = h(ev);
            });
            _started = true;
        }
        if (_hub is not null) await _hub.StartAsync();
    }

    private static SofiaEvent ParseEvent(JsonElement payload)
    {
        var type = payload.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        string? sid = payload.TryGetProperty("sessionId", out var s) && s.ValueKind != JsonValueKind.Null ? s.GetString() : null;
        string? wid = payload.TryGetProperty("workspaceId", out var w) && w.ValueKind != JsonValueKind.Null ? w.GetString() : null;
        var data = payload.TryGetProperty("data", out var d) ? d.Clone() : default;
        return new SofiaEvent(type, sid, wid, data);
    }

    private async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        var resp = await _http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return (await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct))!;
    }

    private async Task<T?> PostJsonAsync<T>(string path, object? body, CancellationToken ct)
    {
        var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(path, content, ct);
        resp.EnsureSuccessStatusCode();
        if (typeof(T) == typeof(string) || resp.Content.Headers.ContentLength == 0) return default;
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
    }

    public async Task<WorkspaceDto> FindOrCreateWorkspaceAsync(string cwd, CancellationToken ct)
    {
        // Try find existing by path among workspaces.
        var all = await GetJsonAsync<List<WorkspaceDto>>("api/workspaces", ct);
        // WorkspaceDto doesn't include path in our minimal record; fetch raw instead.
        // Fall back to create.
        var body = new { Kind = "folder", Path = cwd };
        var resp = await _http.PostAsync("api/workspaces", new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var ws = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
        var id = ws.GetProperty("id").GetString()!;
        return new WorkspaceDto(id, cwd, "folder");
    }

    public async Task<SessionDto> CreateSessionAsync(string workspaceId, string? title, string? origin, CancellationToken ct)
    {
        var body = new { Title = title, Origin = origin };
        var resp = await _http.PostAsync($"api/workspaces/{workspaceId}/sessions", new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
        return ParseSession(el);
    }

    public async Task<SessionDto> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        var el = await GetJsonAsync<JsonElement>($"api/sessions/{sessionId}", ct);
        return ParseSession(el);
    }

    public async Task<List<MessageDto>> GetMessagesAsync(string sessionId, CancellationToken ct)
    {
        var el = await GetJsonAsync<JsonElement>($"api/sessions/{sessionId}", ct);
        if (!el.TryGetProperty("messages", out var msgs)) return new();
        var list = new List<MessageDto>();
        foreach (var m in msgs.EnumerateArray())
        {
            list.Add(ParseMessage(m));
        }
        return list;
    }

    public async Task<string> PromptAsync(string sessionId, PromptInputDto input, CancellationToken ct)
    {
        var body = new
        {
            Text = input.Text,
            Attachments = input.Attachments,
            Delivery = input.Delivery,
            ModelId = input.ModelId,
            Agent = input.Agent,
            PermissionMode = input.PermissionMode,
            ReasoningMode = input.ReasoningMode,
            Language = input.Language,
        };
        var resp = await _http.PostAsync($"api/sessions/{sessionId}/prompt", new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
        return el.GetProperty("messageId").GetString()!;
    }

    public async Task AbortAsync(string sessionId, CancellationToken ct)
    {
        await _http.PostAsync($"api/sessions/{sessionId}/abort", null, ct);
    }

    public async Task<SessionDto> ApplySettingsAsync(string sessionId, UpdateSessionRequestDto req, CancellationToken ct)
    {
        var body = new
        {
            Title = req.Title,
            ModelId = req.ModelId,
            Agent = req.Agent,
            PermissionMode = req.PermissionMode,
            ReasoningMode = req.ReasoningMode,
        };
        var content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        var resp = await _http.PatchAsync($"api/sessions/{sessionId}", content, ct);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        var el = await JsonSerializer.DeserializeAsync<JsonElement>(stream, cancellationToken: ct);
        return ParseSession(el);
    }

    public async Task ReplyPermissionAsync(string sessionId, string requestId, string decision, CancellationToken ct)
    {
        var body = new { Decision = decision };
        var resp = await _http.PostAsync($"api/sessions/{sessionId}/permissions/{requestId}", new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task AnswerQuestionAsync(string sessionId, string formId, List<JsonElement?> answers, CancellationToken ct)
    {
        var body = new { Answers = answers };
        var resp = await _http.PostAsync($"api/sessions/{sessionId}/questions/{formId}", new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<List<ModelDto>> GetModelsAsync(CancellationToken ct)
    {
        var el = await GetJsonAsync<JsonElement>("api/models", ct);
        var list = new List<ModelDto>();
        foreach (var m in el.EnumerateArray())
        {
            var id = m.TryGetProperty("id", out var i) ? i.GetString() ?? "" : m.GetProperty("Id").GetString() ?? "";
            var name = m.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            list.Add(new ModelDto(id, name));
        }
        return list;
    }

    public async Task AddSessionMcpAsync(string sessionId, SessionMcpRequestDto req, CancellationToken ct)
    {
        var body = new
        {
            Name = req.Name,
            Transport = req.Transport,
            Command = req.Command,
            Args = req.Args,
            Url = req.Url,
            Token = req.Token,
            Env = req.Env,
        };
        var resp = await _http.PostAsync($"api/sessions/{sessionId}/mcp", new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task RemoveSessionMcpAsync(string sessionId, CancellationToken ct)
    {
        await _http.DeleteAsync($"api/sessions/{sessionId}/mcp", ct);
    }

    public IDisposable SubscribeEvents(Func<SofiaEvent, Task> handler)
    {
        lock (_gate) _handlers.Add(handler);
        _ = EnsureHubAsync();
        return new Unsubscriber(() => { lock (_gate) _handlers.Remove(handler); });
    }

    private static SessionDto ParseSession(JsonElement el)
    {
        string? Get(string name) => el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? v.GetString() : (el.TryGetProperty(char.ToUpperInvariant(name[0]) + name[1..], out var v2) ? v2.GetString() : null);
        var id = Get("id") ?? el.GetProperty("Id").GetString()!;
        var ws = Get("workspaceId") ?? Get("workspaceid") ?? "";
        return new SessionDto(
            id,
            ws,
            Get("parentId"),
            Get("title"),
            Get("modelId"),
            Get("agent"),
            Get("permissionMode"),
            Get("reasoningMode"),
            Get("status"),
            Get("origin"));
    }

    private static MessageDto ParseMessage(JsonElement el)
    {
        var id = el.GetProperty("id").GetString()!;
        var sid = el.TryGetProperty("sessionId", out var s) ? s.GetString() ?? "" : "";
        var role = el.GetProperty("role").GetString() ?? "assistant";
        var text = el.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var reasoning = el.TryGetProperty("reasoning", out var r) ? r.GetString() : null;
        List<ToolCallDto>? tools = null;
        if (el.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
        {
            tools = new();
            foreach (var x in tc.EnumerateArray())
            {
                tools.Add(new ToolCallDto(
                    x.GetProperty("id").GetString() ?? "",
                    x.GetProperty("name").GetString() ?? "",
                    x.TryGetProperty("status", out var st) ? st.GetString() ?? "running" : "running",
                    x.TryGetProperty("input", out var inp) ? inp.GetString() : null,
                    x.TryGetProperty("output", out var outp) ? outp.GetString() : null,
                    x.TryGetProperty("error", out var err) ? err.GetString() : null));
            }
        }
        PermissionCardDto? perm = null;
        if (el.TryGetProperty("permission", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            perm = new PermissionCardDto(
                p.TryGetProperty("tool", out var tool) ? tool.GetString() ?? "" : "",
                p.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                p.TryGetProperty("choices", out var ch) ? ch.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new(),
                p.TryGetProperty("resolved", out var res) ? res.GetString() : null,
                p.TryGetProperty("requestId", out var rid) ? rid.GetString() ?? "" : "");
        }
        return new MessageDto(id, sid, role, text, reasoning, tools, perm);
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        _http.Dispose();
    }

    private sealed class Unsubscriber(Action action) : IDisposable { public void Dispose() => action(); }
}
