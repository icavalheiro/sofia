using System.Text.Json;
using Sofia.Contracts.Domain;

namespace Sofia.Client;

/// <summary>
/// Everything a Sofia UI needs from a backend, independent of where the engine runs. Two implementations exist:
/// <c>HttpSofiaBackend</c> (REST + SignalR, the only transport a browser can use) and <c>InProcSofiaBackend</c>
/// (direct calls into Sofia.Core for a desktop head that owns the data directory).
/// </summary>
/// <remarks>
/// Entities and request bodies come from <c>Sofia.Contracts</c>. Views the engine computes on the fly — models,
/// providers, MCP/LSP/process/browser lists, search results, config — are transported as <see cref="JsonElement"/>:
/// they are presentation shapes of the engine, and a second typed copy here would drift from them.
/// </remarks>
public interface ISofiaBackend
{
    // --- server -------------------------------------------------------------

    Task<JsonElement> HealthAsync(CancellationToken ct = default);

    /// <summary>Version, listening URLs, data directory and remote-access state.</summary>
    Task<JsonElement> ServerInfoAsync(CancellationToken ct = default);

    /// <summary>Creates a one-time pairing code (loopback only).</summary>
    Task<JsonElement> CreatePairingCodeAsync(CancellationToken ct = default);

    /// <summary>Redeems a pairing code; returns the bearer token, or null when it is invalid/expired.</summary>
    Task<string?> PairAsync(string code, CancellationToken ct = default);

    // --- workspaces ---------------------------------------------------------

    Task<List<WorkspaceView>> ListWorkspacesAsync(CancellationToken ct = default);
    Task<WorkspaceView> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken ct = default);
    Task<WorkspaceView> UpdateWorkspaceAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct = default);
    Task DeleteWorkspaceAsync(string id, CancellationToken ct = default);
    Task<Session> CreateSessionAsync(string workspaceId, CreateSessionRequest? request = null, CancellationToken ct = default);
    Task ReorderWorkspacesAsync(IReadOnlyList<string> ids, CancellationToken ct = default);
    Task ReorderSessionsAsync(string workspaceId, IReadOnlyList<string> ids, CancellationToken ct = default);

    // --- sessions -----------------------------------------------------------

    /// <summary>Non-idle sessions and their status.</summary>
    Task<Dictionary<string, string>> StatusesAsync(CancellationToken ct = default);

    /// <summary>A session with its transcript (<c>messages</c>).</summary>
    Task<JsonElement> SessionAsync(string id, CancellationToken ct = default);

    Task<Session> UpdateSessionAsync(string id, UpdateSessionRequest request, CancellationToken ct = default);
    Task DeleteSessionAsync(string id, CancellationToken ct = default);
    Task<Session> ArchiveSessionAsync(string id, CancellationToken ct = default);
    Task<Session> UnarchiveSessionAsync(string id, CancellationToken ct = default);
    Task<Session> PinSessionAsync(string id, CancellationToken ct = default);
    Task<Session> UnpinSessionAsync(string id, CancellationToken ct = default);
    Task<string> PromptAsync(string id, PromptInput input, CancellationToken ct = default);
    Task<string> CommandAsync(string id, CommandRequest request, CancellationToken ct = default);
    Task AbortAsync(string id, CancellationToken ct = default);
    Task RemoveQueuedAsync(string id, string messageId, CancellationToken ct = default);
    Task CompactAsync(string id, CancellationToken ct = default);
    Task<string> ContinueAsync(string id, CancellationToken ct = default);
    Task<string> RetryAsync(string id, string messageId, CancellationToken ct = default);
    Task<JsonElement> RevertAsync(string id, string messageId, CancellationToken ct = default);
    Task<JsonElement> BranchAsync(string id, string messageId, CancellationToken ct = default);
    Task ReplyPermissionAsync(string id, string requestId, string decision, CancellationToken ct = default);
    Task AnswerAsync(string id, string formId, IReadOnlyList<JsonElement?> answers, CancellationToken ct = default);
    Task<List<FileDiff>> DiffAsync(string id, CancellationToken ct = default);
    Task<Session> AddSkillAsync(string id, string skillId, CancellationToken ct = default);
    Task<Session> RemoveSkillAsync(string id, string skillId, CancellationToken ct = default);
    Task<List<TaskItem>> TasksAsync(string id, CancellationToken ct = default);
    Task ClearTasksAsync(string id, CancellationToken ct = default);
    Task<JsonElement> AddSessionMcpAsync(string id, SessionMcpRequest request, CancellationToken ct = default);
    Task RemoveSessionMcpAsync(string id, CancellationToken ct = default);
    Task<JsonElement> ExportAsync(string id, CancellationToken ct = default);
    Task<Session> ImportAsync(ImportRequest request, CancellationToken ct = default);

    // --- search and stats ---------------------------------------------------

    Task<JsonElement> SearchAsync(string query, string? workspaceId = null, bool includeArchived = false, bool includeSubagents = false, int limit = 20, CancellationToken ct = default);
    Task<StatsView> StatsAsync(CancellationToken ct = default);

    // --- models and providers -----------------------------------------------

    Task<JsonElement> ModelsAsync(CancellationToken ct = default);
    Task SetModelEnabledAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default);
    Task SetModelSingleClientAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default);
    Task<JsonElement> ProvidersAsync(CancellationToken ct = default);
    Task<JsonElement> ProviderPresetsAsync(CancellationToken ct = default);
    Task<JsonElement> CreateProviderAsync(CreateProviderRequest request, CancellationToken ct = default);
    Task<JsonElement> UpdateProviderAsync(string id, UpdateProviderRequest request, CancellationToken ct = default);
    Task DeleteProviderAsync(string id, CancellationToken ct = default);
    Task<JsonElement> RefreshProvidersAsync(CancellationToken ct = default);
    Task<JsonElement> ProbeProviderAsync(ProbeRequest request, CancellationToken ct = default);
    Task<JsonElement> CopilotLoginAsync(CancellationToken ct = default);
    Task<JsonElement> CopilotPollAsync(string deviceCode, CancellationToken ct = default);

    // --- MCP and LSP --------------------------------------------------------

    Task<JsonElement> McpServersAsync(CancellationToken ct = default);
    Task<JsonElement> AddMcpAsync(CreateMcpRequest request, CancellationToken ct = default);
    Task<JsonElement> ConnectMcpAsync(string id, CancellationToken ct = default);
    Task<JsonElement> DisconnectMcpAsync(string id, CancellationToken ct = default);
    Task DeleteMcpAsync(string id, CancellationToken ct = default);
    Task<JsonElement> LspServersAsync(CancellationToken ct = default);
    Task<JsonElement> RestartLspAsync(string id, CancellationToken ct = default);
    Task<JsonElement> InstallLspAsync(string id, CancellationToken ct = default);

    // --- catalogues, files and VCS ------------------------------------------

    Task<JsonElement> AgentsAsync(string? directory, CancellationToken ct = default);
    Task<JsonElement> CommandsAsync(string? directory, CancellationToken ct = default);
    Task<JsonElement> SkillsAsync(string? directory, CancellationToken ct = default);
    Task<List<string>> FilesAsync(string directory, CancellationToken ct = default);
    Task<JsonElement> VcsAsync(string directory, CancellationToken ct = default);
    Task<List<FileDiff>> VcsDiffAsync(string directory, CancellationToken ct = default);
    Task<JsonElement> WorktreesAsync(string directory, CancellationToken ct = default);
    Task<JsonElement> CreateWorktreeAsync(CreateWorktreeRequest request, CancellationToken ct = default);
    Task DeleteWorktreeAsync(DeleteWorktreeRequest request, CancellationToken ct = default);

    // --- attachments and host actions ---------------------------------------

    Task<Attachment> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);

    /// <summary>URL an image/video tag can point at, carrying the access token when the session is paired.</summary>
    string AttachmentUrl(string id);

    /// <summary>Opens a folder or file on the host (Explorer/Finder/xdg-open, a terminal or VS Code).</summary>
    Task OpenAsync(string path, string? app = null, CancellationToken ct = default);

    // --- processes and browser ----------------------------------------------

    Task<JsonElement> ProcessesAsync(string? sessionId, CancellationToken ct = default);
    Task<JsonElement> ProcessOutputAsync(string id, int tail = 200, CancellationToken ct = default);
    Task<JsonElement> KillProcessAsync(string id, CancellationToken ct = default);
    Task<JsonElement> ClearProcessesAsync(CancellationToken ct = default);
    Task<JsonElement> BrowsersAsync(CancellationToken ct = default);
    Task<JsonElement> SessionBrowserAsync(string sessionId, CancellationToken ct = default);
    Task<JsonElement> OpenBrowserAsync(string sessionId, string? browser, CancellationToken ct = default);
    Task CloseBrowserAsync(string sessionId, CancellationToken ct = default);

    // --- config and knowledge -----------------------------------------------

    Task<JsonElement> ConfigAsync(CancellationToken ct = default);
    Task<JsonElement> PatchConfigAsync(ConfigPatch patch, CancellationToken ct = default);
    Task<JsonElement> KnowledgeAsync(string? workspaceId = null, string? agent = null, CancellationToken ct = default);
    Task DeleteKnowledgeAsync(string id, CancellationToken ct = default);

    // --- live events --------------------------------------------------------

    /// <summary>
    /// Every <c>event</c> pushed on <c>/hubs/events</c>. The stream reconnects on its own; a transport that can miss
    /// events while disconnected (HTTP) is expected to make the caller resync afterwards.
    /// </summary>
    IAsyncEnumerable<SofiaEventDto> EventsAsync(CancellationToken ct = default);
}
