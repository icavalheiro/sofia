using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Infrastructure;

namespace Sofia.Client;

/// <summary>
/// <see cref="ISofiaBackend"/> over REST + SignalR. This is the transport a browser (WASM) has to use, and the one a
/// desktop keeps as a fallback when a server already owns the data directory.
/// </summary>
public sealed class HttpSofiaBackend : ISofiaBackend, IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _baseUrl;
    private HubConnection? _hub;

    public HttpSofiaBackend(SofiaBackendOptions options)
    {
        _baseUrl = options.BaseUrl.TrimEnd('/');
        _ownsClient = options.HttpClient is null;
        _http = options.HttpClient ?? new HttpClient { BaseAddress = new Uri(_baseUrl + "/") };
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(_baseUrl + "/");
        if (!string.IsNullOrWhiteSpace(options.AccessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AccessToken);
        AccessToken = options.AccessToken;
    }

    /// <summary>Bearer token used for attachment URLs (an <c>&lt;img&gt;</c> cannot send headers).</summary>
    public string? AccessToken { get; }

    /// <summary>Raised after the event stream reconnected: state may have changed while it was down, so reload.</summary>
    public event Action? Resync;

    // --- plumbing ------------------------------------------------------------

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json.Options);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadAsync(response, ct);
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadAsync(response, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            var message = text;
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
                    message = error.GetString() ?? text;
            }
            catch (JsonException)
            {
                // Not a JSON error body (a proxy page, an empty 5xx): keep the raw text.
            }
            throw new SofiaApiException((int)response.StatusCode, string.IsNullOrWhiteSpace(message) ? response.ReasonPhrase ?? "Request failed." : message);
        }
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        if (stream.CanSeek && stream.Length == 0) return default;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return default;
        using var parsed = JsonDocument.Parse(json);
        return parsed.RootElement.Clone();
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        var element = await SendAsync(HttpMethod.Get, path, (object?)null, ct);
        return element.Deserialize<T>(Json.Options) ?? throw new SofiaApiException(500, $"Empty response from {path}.");
    }

    private static string Query(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    // --- server --------------------------------------------------------------

    public Task<JsonElement> HealthAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/health", (object?)null, ct);
    public Task<JsonElement> ServerInfoAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/server", (object?)null, ct);
    public Task<JsonElement> CreatePairingCodeAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/pair", (object?)null, ct);

    public async Task<string?> PairAsync(string code, CancellationToken ct = default)
    {
        try
        {
            var result = await SendAsync(HttpMethod.Post, "api/pair/connect", new PairConnectRequest(code), ct);
            return result.TryGetProperty("token", out var token) ? token.GetString() : null;
        }
        catch (SofiaApiException ex) when (ex.IsUnauthorized)
        {
            return null;
        }
    }

    // --- workspaces ----------------------------------------------------------

    public Task<List<WorkspaceView>> ListWorkspacesAsync(CancellationToken ct = default) => GetAsync<List<WorkspaceView>>("api/workspaces", ct);
    public Task<WorkspaceView> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken ct = default) => GetAsync<WorkspaceView>("api/workspaces", ct, request);
    public Task<WorkspaceView> UpdateWorkspaceAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct = default) => GetAsync<WorkspaceView>($"api/workspaces/{id}", ct, request, HttpMethod.Patch);
    public async Task DeleteWorkspaceAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/workspaces/{id}", (object?)null, ct);
    public Task<Session> CreateSessionAsync(string workspaceId, CreateSessionRequest? request = null, CancellationToken ct = default) => GetAsync<Session>($"api/workspaces/{workspaceId}/sessions", ct, request ?? new CreateSessionRequest(null));

    public async Task ReorderWorkspacesAsync(IReadOnlyList<string> ids, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Put, "api/workspaces/order", new ReorderRequest([.. ids]), ct);

    public async Task ReorderSessionsAsync(string workspaceId, IReadOnlyList<string> ids, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Put, $"api/workspaces/{workspaceId}/sessions/order", new ReorderRequest([.. ids]), ct);

    // --- sessions ------------------------------------------------------------

    public Task<Dictionary<string, string>> StatusesAsync(CancellationToken ct = default) => GetAsync<Dictionary<string, string>>("api/sessions/status", ct);
    public Task<JsonElement> SessionAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"api/sessions/{id}", (object?)null, ct);
    public Task<Session> UpdateSessionAsync(string id, UpdateSessionRequest request, CancellationToken ct = default) => GetAsync<Session>($"api/sessions/{id}", ct, request, HttpMethod.Patch);
    public async Task DeleteSessionAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/sessions/{id}", (object?)null, ct);
    public Task<Session> ArchiveSessionAsync(string id, CancellationToken ct = default) => GetAsync<Session>($"api/sessions/{id}/archive", ct, null, HttpMethod.Post);
    public Task<Session> UnarchiveSessionAsync(string id, CancellationToken ct = default) => GetAsync<Session>($"api/sessions/{id}/unarchive", ct, null, HttpMethod.Post);
    public Task<Session> PinSessionAsync(string id, CancellationToken ct = default) => GetAsync<Session>($"api/sessions/{id}/pin", ct, null, HttpMethod.Post);
    public Task<Session> UnpinSessionAsync(string id, CancellationToken ct = default) => GetAsync<Session>($"api/sessions/{id}/unpin", ct, null, HttpMethod.Post);

    public async Task<string> PromptAsync(string id, PromptInput input, CancellationToken ct = default) =>
        MessageId(await SendAsync(HttpMethod.Post, $"api/sessions/{id}/prompt", input, ct));

    public async Task<string> CommandAsync(string id, CommandRequest request, CancellationToken ct = default) =>
        MessageId(await SendAsync(HttpMethod.Post, $"api/sessions/{id}/command", request, ct));

    public async Task AbortAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Post, $"api/sessions/{id}/abort", (object?)null, ct);
    public async Task RemoveQueuedAsync(string id, string messageId, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/sessions/{id}/queued/{messageId}", (object?)null, ct);
    public async Task CompactAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Post, $"api/sessions/{id}/compact", (object?)null, ct);

    public async Task<string> ContinueAsync(string id, CancellationToken ct = default) =>
        MessageId(await SendAsync(HttpMethod.Post, $"api/sessions/{id}/continue", (object?)null, ct));

    public async Task<string> RetryAsync(string id, string messageId, CancellationToken ct = default) =>
        MessageId(await SendAsync(HttpMethod.Post, $"api/sessions/{id}/retry", new RevertRequest(messageId), ct));

    public Task<JsonElement> RevertAsync(string id, string messageId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sessions/{id}/revert", new RevertRequest(messageId), ct);

    public Task<JsonElement> BranchAsync(string id, string messageId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sessions/{id}/branch", new RevertRequest(messageId), ct);

    public async Task ReplyPermissionAsync(string id, string requestId, string decision, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, $"api/sessions/{id}/permissions/{requestId}", new PermissionReplyRequest(decision), ct);

    public async Task AnswerAsync(string id, string formId, IReadOnlyList<JsonElement?> answers, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, $"api/sessions/{id}/questions/{formId}", new AnswerRequest([.. answers]), ct);

    public Task<List<FileDiff>> DiffAsync(string id, CancellationToken ct = default) => GetAsync<List<FileDiff>>($"api/sessions/{id}/diff", ct);
    public Task<Session> AddSkillAsync(string id, string skillId, CancellationToken ct = default) => GetAsync<Session>($"api/sessions/{id}/skills", ct, new SkillRequest(skillId), HttpMethod.Post);
    public async Task<Session> RemoveSkillAsync(string id, string skillId, CancellationToken ct = default) => await GetAsync<Session>($"api/sessions/{id}/skills/{skillId}", ct, null, HttpMethod.Delete);
    public Task<List<TaskItem>> TasksAsync(string id, CancellationToken ct = default) => GetAsync<List<TaskItem>>($"api/sessions/{id}/tasks", ct);
    public async Task ClearTasksAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/sessions/{id}/tasks", (object?)null, ct);
    public Task<JsonElement> AddSessionMcpAsync(string id, SessionMcpRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/sessions/{id}/mcp", request, ct);
    public async Task RemoveSessionMcpAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/sessions/{id}/mcp", (object?)null, ct);
    public Task<JsonElement> ExportAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"api/sessions/{id}/export", (object?)null, ct);
    public Task<Session> ImportAsync(ImportRequest request, CancellationToken ct = default) => GetAsync<Session>("api/sessions/import", ct, request, HttpMethod.Post);

    // --- search and stats ----------------------------------------------------

    public Task<JsonElement> SearchAsync(string query, string? workspaceId = null, bool includeArchived = false, bool includeSubagents = false, int limit = 20, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/search" + Query(
            ("q", query),
            ("workspaceId", workspaceId),
            ("includeArchived", includeArchived ? "true" : null),
            ("includeSubagents", includeSubagents ? "true" : null),
            ("limit", limit.ToString())), (object?)null, ct);

    public async Task<StatsView> StatsAsync(CancellationToken ct = default)
    {
        var element = await SendAsync(HttpMethod.Get, "api/stats", (object?)null, ct);
        return element.Deserialize<StatsView>(Json.Options) ?? new StatsView(0, 0, 0, new StatsTokens(0, 0), 0, 0, 0);
    }

    // --- models and providers ------------------------------------------------

    public Task<JsonElement> ModelsAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/models", (object?)null, ct);

    public async Task SetModelEnabledAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "api/models/enabled", new SetModelEnabledRequest(null, [.. ids], enabled), ct);

    public async Task SetModelSingleClientAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "api/models/single-client", new SetModelEnabledRequest(null, [.. ids], enabled), ct);

    public Task<JsonElement> ProvidersAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/providers", (object?)null, ct);
    public Task<JsonElement> ProviderPresetsAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/providers/presets", (object?)null, ct);
    public Task<JsonElement> CreateProviderAsync(CreateProviderRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/providers", request, ct);
    public Task<JsonElement> UpdateProviderAsync(string id, UpdateProviderRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, $"api/providers/{id}", request, ct);
    public async Task DeleteProviderAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/providers/{id}", (object?)null, ct);
    public Task<JsonElement> RefreshProvidersAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/providers/refresh", (object?)null, ct);
    public Task<JsonElement> ProbeProviderAsync(ProbeRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/providers/probe", request, ct);
    public Task<JsonElement> CopilotLoginAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/providers/github-copilot/login", (object?)null, ct);
    public Task<JsonElement> CopilotPollAsync(string deviceCode, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/providers/github-copilot/login/poll", new CopilotPollRequest(deviceCode), ct);

    // --- MCP and LSP ---------------------------------------------------------

    public Task<JsonElement> McpServersAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/mcp", (object?)null, ct);
    public Task<JsonElement> AddMcpAsync(CreateMcpRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/mcp", request, ct);
    public Task<JsonElement> ConnectMcpAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/mcp/{id}/connect", (object?)null, ct);
    public Task<JsonElement> DisconnectMcpAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/mcp/{id}/disconnect", (object?)null, ct);
    public async Task DeleteMcpAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/mcp/{id}", (object?)null, ct);
    public Task<JsonElement> LspServersAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/lsp", (object?)null, ct);
    public Task<JsonElement> RestartLspAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/lsp/{id}/restart", (object?)null, ct);
    public Task<JsonElement> InstallLspAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/lsp/{id}/install", (object?)null, ct);

    // --- catalogues, files and VCS ------------------------------------------

    public Task<JsonElement> AgentsAsync(string? directory, CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/agents" + Query(("directory", directory)), (object?)null, ct);
    public Task<JsonElement> CommandsAsync(string? directory, CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/commands" + Query(("directory", directory)), (object?)null, ct);
    public Task<JsonElement> SkillsAsync(string? directory, CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/skills" + Query(("directory", directory)), (object?)null, ct);
    public Task<List<string>> FilesAsync(string directory, CancellationToken ct = default) => GetAsync<List<string>>("api/files" + Query(("directory", directory)), ct);
    public Task<JsonElement> VcsAsync(string directory, CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/vcs" + Query(("directory", directory)), (object?)null, ct);
    public Task<List<FileDiff>> VcsDiffAsync(string directory, CancellationToken ct = default) => GetAsync<List<FileDiff>>("api/vcs/diff" + Query(("directory", directory)), ct);
    public Task<JsonElement> WorktreesAsync(string directory, CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/worktrees" + Query(("directory", directory)), (object?)null, ct);
    public Task<JsonElement> CreateWorktreeAsync(CreateWorktreeRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/worktrees", request, ct);

    /// <summary>Deleting a worktree needs a body, so this one goes out as a hand-built request.</summary>
    public async Task DeleteWorktreeAsync(DeleteWorktreeRequest request, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Delete, "api/worktrees", JsonContent.Create(request, options: Json.Options), ct);

    // --- attachments and host actions ---------------------------------------

    public async Task<Attachment> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        // The endpoint binds IFormFile, whose name has to be "file".
        form.Add(file, "file", fileName);
        var element = await SendAsync(HttpMethod.Post, "api/attachments", form, ct);
        return element.Deserialize<Attachment>(Json.Options) ?? throw new SofiaApiException(500, "Empty attachment response.");
    }

    public string AttachmentUrl(string id) =>
        string.IsNullOrWhiteSpace(AccessToken)
            ? $"{_baseUrl}/api/attachments/{id}"
            : $"{_baseUrl}/api/attachments/{id}?access_token={Uri.EscapeDataString(AccessToken)}";

    public async Task OpenAsync(string path, string? app = null, CancellationToken ct = default) =>
        await SendAsync(HttpMethod.Post, "api/open", new OpenRequest(path, app), ct);

    // --- processes and browser ----------------------------------------------

    public Task<JsonElement> ProcessesAsync(string? sessionId, CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/processes" + Query(("sessionId", sessionId)), (object?)null, ct);
    public Task<JsonElement> ProcessOutputAsync(string id, int tail = 200, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"api/processes/{id}/output?tail={tail}", (object?)null, ct);
    public Task<JsonElement> KillProcessAsync(string id, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/processes/{id}/kill", (object?)null, ct);
    public Task<JsonElement> ClearProcessesAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/processes/clear", (object?)null, ct);
    public Task<JsonElement> BrowsersAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/browsers", (object?)null, ct);
    public Task<JsonElement> SessionBrowserAsync(string sessionId, CancellationToken ct = default) => SendAsync(HttpMethod.Get, $"api/sessions/{sessionId}/browser", (object?)null, ct);
    public Task<JsonElement> OpenBrowserAsync(string sessionId, string? browser, CancellationToken ct = default) => SendAsync(HttpMethod.Post, $"api/sessions/{sessionId}/browser/open", new { browser }, ct);
    public async Task CloseBrowserAsync(string sessionId, CancellationToken ct = default) => await SendAsync(HttpMethod.Post, $"api/sessions/{sessionId}/browser/close", (object?)null, ct);

    // --- config and knowledge -----------------------------------------------

    public Task<JsonElement> ConfigAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "api/config", (object?)null, ct);
    public Task<JsonElement> PatchConfigAsync(ConfigPatch patch, CancellationToken ct = default) => SendAsync(HttpMethod.Patch, "api/config", patch, ct);
    public Task<JsonElement> KnowledgeAsync(string? workspaceId = null, string? agent = null, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "api/knowledge" + Query(("workspaceId", workspaceId), ("agent", agent)), (object?)null, ct);
    public async Task DeleteKnowledgeAsync(string id, CancellationToken ct = default) => await SendAsync(HttpMethod.Delete, $"api/knowledge/{id}", (object?)null, ct);

    // --- live events ---------------------------------------------------------

    /// <summary>
    /// Streams the hub events. The connection reconnects on its own; when it does, <see cref="Resync"/> fires so the
    /// caller can reload whatever it may have missed, and subscribers get an event of type <c>resync</c>.
    /// </summary>
    public async IAsyncEnumerable<SofiaEventDto> EventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<SofiaEventDto>(new UnboundedChannelOptions { SingleReader = true });
        var hub = new HubConnectionBuilder()
            .WithUrl(_baseUrl + "/hubs/events", o =>
            {
                if (!string.IsNullOrWhiteSpace(AccessToken)) o.AccessTokenProvider = () => Task.FromResult<string?>(AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();
        hub.On<JsonElement>("event", data => channel.Writer.TryWrite(ToDto(data)));
        hub.Reconnected += _ =>
        {
            channel.Writer.TryWrite(new SofiaEventDto("resync", null, null, default));
            Resync?.Invoke();
            return Task.CompletedTask;
        };
        await hub.StartAsync(ct);
        _hub = hub;
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(ct)) yield return e;
        }
        finally
        {
            await hub.DisposeAsync();
            _hub = null;
        }
    }

    /// <summary>The hub payload is <c>{ type, sessionId, workspaceId, data }</c>; a malformed one becomes a no-op event.</summary>
    private static SofiaEventDto ToDto(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return new SofiaEventDto("unknown", null, null, default);
        var type = payload.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
        var sessionId = payload.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var workspaceId = payload.TryGetProperty("workspaceId", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
        var data = payload.TryGetProperty("data", out var d) ? d.Clone() : default;
        return new SofiaEventDto(type, sessionId, workspaceId, data);
    }

    private static string MessageId(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty("messageId", out var id)
            ? id.GetString() ?? throw new SofiaApiException(500, "The server returned no message id.")
            : throw new SofiaApiException(500, "The server returned no message id.");

    private async Task<T> GetAsync<T>(string path, CancellationToken ct, object? body, HttpMethod? method = null)
    {
        var element = await SendAsync(method ?? HttpMethod.Post, path, body, ct);
        return element.Deserialize<T>(Json.Options) ?? throw new SofiaApiException(500, $"Empty response from {path}.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        if (_ownsClient) _http.Dispose();
    }
}
