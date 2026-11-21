using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;
using Sofia.Contracts.Infrastructure;
using Sofia.Core.Agent;
using Sofia.Core.Changes;
using Sofia.Core.Context;
using Sofia.Core.Data;
using Sofia.Core.Events;
using Sofia.Core.Infrastructure;
using Sofia.Core.Providers;
using Sofia.Core.Search;
using Sofia.Core.Security;
using Sofia.Core.Services;
using Sofia.Core.Vcs;

namespace Sofia.Client.InProc;

/// <summary>
/// <see cref="ISofiaBackend"/> that calls the engine directly. A desktop head that owns the data directory uses this
/// instead of HTTP: same results, no port, no token.
/// </summary>
/// <remarks>
/// Events are delivered on whatever thread published them (the agent loop), exactly like the bus: marshalling to the
/// UI dispatcher is the caller's job. Values the HTTP client receives as JSON are serialized here with
/// <see cref="Json.Options"/>, so both transports hand the UI identical shapes.
/// </remarks>
public sealed class InProcSofiaBackend(IServiceProvider services) : ISofiaBackend
{
    private static JsonElement Element(object? value) =>
        value is null ? default : JsonSerializer.SerializeToElement(value, value.GetType(), Json.Options);

    private static JsonElement Element<T>(T value) =>
        value is null ? default : JsonSerializer.SerializeToElement(value, typeof(T), Json.Options);

    private T Get<T>() where T : notnull => services.GetRequiredService<T>();

    private SofiaDb Db => Get<SofiaDb>();
    private AgentService Agents => Get<AgentService>();
    private SessionStore Store => Get<SessionStore>();

    // --- server --------------------------------------------------------------

    public Task<JsonElement> HealthAsync(CancellationToken ct = default) =>
        Task.FromResult(Element(new { status = "ok", name = "sofia", version = ProviderIdentity.Version }));

    public Task<JsonElement> ServerInfoAsync(CancellationToken ct = default) =>
        Task.FromResult(Element(new
        {
            version = ProviderIdentity.Version,
            urls = Array.Empty<string>(),
            dataDir = Get<SofiaOptions>().DataDir,
            remoteAuth = false,
            networkReachable = false,
        }));

    public Task<JsonElement> CreatePairingCodeAsync(CancellationToken ct = default)
    {
        var (code, expiresIn) = Get<PairingService>().CreateCode();
        return Task.FromResult(Element(new { code, expiresIn }));
    }

    public Task<string?> PairAsync(string code, CancellationToken ct = default) =>
        Task.FromResult(Get<PairingService>().Redeem(code));

    // --- workspaces ----------------------------------------------------------

    public Task<List<WorkspaceView>> ListWorkspacesAsync(CancellationToken ct = default) =>
        Task.FromResult(Get<WorkspaceService>().List());

    public Task<WorkspaceView> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken ct = default) =>
        Task.FromResult(Get<WorkspaceService>().Create(request));

    public Task<WorkspaceView> UpdateWorkspaceAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct = default) =>
        Task.FromResult(Get<WorkspaceService>().Update(id, request));

    public Task DeleteWorkspaceAsync(string id, CancellationToken ct = default)
    {
        Get<WorkspaceService>().Delete(id);
        return Task.CompletedTask;
    }

    public Task<Session> CreateSessionAsync(string workspaceId, CreateSessionRequest? request = null, CancellationToken ct = default) =>
        Task.FromResult(Get<WorkspaceService>().CreateSession(workspaceId, request));

    public Task ReorderWorkspacesAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        Get<WorkspaceService>().Reorder(new ReorderRequest([.. ids]));
        return Task.CompletedTask;
    }

    public Task ReorderSessionsAsync(string workspaceId, IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        Get<WorkspaceService>().ReorderSessions(workspaceId, new ReorderRequest([.. ids]));
        return Task.CompletedTask;
    }

    // --- sessions ------------------------------------------------------------

    public Task<Dictionary<string, string>> StatusesAsync(CancellationToken ct = default) =>
        Task.FromResult(Get<SessionService>().Statuses());

    public Task<JsonElement> SessionAsync(string id, CancellationToken ct = default)
    {
        var (session, _) = Agents.Load(id);
        return Task.FromResult(Element(WorkspaceService.WithMessages(session, Db.SessionMessages(id))));
    }

    public Task<Session> UpdateSessionAsync(string id, UpdateSessionRequest request, CancellationToken ct = default)
    {
        var (session, _) = Agents.Load(id);
        return Task.FromResult(Agents.ApplySettings(session, request.ModelId, request.Agent, request.PermissionMode, request.ReasoningMode, request.Title, request.Sandbox));
    }

    public Task DeleteSessionAsync(string id, CancellationToken ct = default) => Get<SessionService>().DeleteAsync(id);
    public Task<Session> ArchiveSessionAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<SessionService>().Archive(id));
    public Task<Session> UnarchiveSessionAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<SessionService>().Unarchive(id));
    public Task<Session> PinSessionAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<SessionService>().Pin(id, true));
    public Task<Session> UnpinSessionAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<SessionService>().Pin(id, false));
    public Task<string> PromptAsync(string id, PromptInput input, CancellationToken ct = default) => Task.FromResult(Agents.Prompt(id, input));
    public Task<string> CommandAsync(string id, CommandRequest request, CancellationToken ct = default) =>
        Agents.CommandAsync(id, request.Name.TrimStart('/'), request.Arguments, request.Attachments, ct);

    public Task AbortAsync(string id, CancellationToken ct = default)
    {
        Agents.Abort(id);
        return Task.CompletedTask;
    }

    public Task RemoveQueuedAsync(string id, string messageId, CancellationToken ct = default)
    {
        Agents.RemoveQueuedMessage(id, messageId);
        return Task.CompletedTask;
    }

    public Task CompactAsync(string id, CancellationToken ct = default)
    {
        Agents.Compact(id);
        return Task.CompletedTask;
    }

    public Task<string> ContinueAsync(string id, CancellationToken ct = default) => Task.FromResult(Agents.Continue(id));
    public Task<string> RetryAsync(string id, string messageId, CancellationToken ct = default) => Task.FromResult(Agents.Retry(id, messageId));

    public Task<JsonElement> RevertAsync(string id, string messageId, CancellationToken ct = default)
    {
        var (removed, restored) = Agents.Revert(id, messageId);
        return Task.FromResult(Element(new { removed, restoredFiles = restored }));
    }

    public Task<JsonElement> BranchAsync(string id, string messageId, CancellationToken ct = default)
    {
        var branch = Agents.Branch(id, messageId);
        return Task.FromResult(Element(WorkspaceService.WithMessages(branch, Db.SessionMessages(branch.Id))));
    }

    public Task ReplyPermissionAsync(string id, string requestId, string decision, CancellationToken ct = default)
    {
        Agents.ReplyPermission(id, requestId, decision);
        return Task.CompletedTask;
    }

    public Task AnswerAsync(string id, string formId, IReadOnlyList<JsonElement?> answers, CancellationToken ct = default)
    {
        Agents.Answer(id, formId, [.. answers]);
        return Task.CompletedTask;
    }

    public Task<List<FileDiff>> DiffAsync(string id, CancellationToken ct = default) => Get<DiffService>().ForSessionAsync(id, ct);

    public Task<Session> AddSkillAsync(string id, string skillId, CancellationToken ct = default)
    {
        var (session, _) = Agents.Load(id);
        if (!session.ActiveSkills.Contains(skillId)) session.ActiveSkills.Add(skillId);
        Store.SaveSession(session);
        return Task.FromResult(session);
    }

    public Task<Session> RemoveSkillAsync(string id, string skillId, CancellationToken ct = default)
    {
        var (session, _) = Agents.Load(id);
        session.ActiveSkills.Remove(skillId);
        Store.SaveSession(session);
        return Task.FromResult(session);
    }

    public Task<List<TaskItem>> TasksAsync(string id, CancellationToken ct = default) => Task.FromResult(Get<SessionService>().Tasks(id));

    public Task ClearTasksAsync(string id, CancellationToken ct = default)
    {
        Get<SessionService>().DeleteTasks(id);
        return Task.CompletedTask;
    }

    public async Task<JsonElement> AddSessionMcpAsync(string id, SessionMcpRequest request, CancellationToken ct = default) =>
        Element(await Get<SessionService>().AddSessionMcpAsync(id, request, ct));

    public Task RemoveSessionMcpAsync(string id, CancellationToken ct = default) => Get<SessionService>().RemoveSessionMcpAsync(id);
    public Task<JsonElement> ExportAsync(string id, CancellationToken ct = default) => Task.FromResult(Element(Get<SessionService>().Export(id)));
    public Task<Session> ImportAsync(ImportRequest request, CancellationToken ct = default) => Task.FromResult(Get<SessionService>().Import(request));

    // --- search and stats ----------------------------------------------------

    public Task<JsonElement> SearchAsync(string query, string? workspaceId = null, bool includeArchived = false, bool includeSubagents = false, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("Query is required.");
        if (workspaceId is not null && Db.Workspaces.FindById(workspaceId) is null) throw new NotFoundException("Workspace not found.");
        var results = Get<ISessionSearch>().Search(Db, query, workspaceId, includeArchived, includeSubagents, limit);
        return Task.FromResult(Element(new { results }));
    }

    public Task<StatsView> StatsAsync(CancellationToken ct = default) => Task.FromResult(Get<SessionService>().Stats());

    // --- models and providers ------------------------------------------------

    public Task<JsonElement> ModelsAsync(CancellationToken ct = default) => Task.FromResult(Element(Get<ProviderAdminService>().Models()));

    public Task SetModelEnabledAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default)
    {
        Get<ProviderAdminService>().SetModelsEnabled(ids, enabled);
        return Task.CompletedTask;
    }

    public Task SetModelSingleClientAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default)
    {
        Get<ProviderAdminService>().SetSingleClient(ids, enabled);
        return Task.CompletedTask;
    }

    public Task<JsonElement> ProvidersAsync(CancellationToken ct = default) => Task.FromResult(Element(Get<ProviderAdminService>().All()));
    public Task<JsonElement> ProviderPresetsAsync(CancellationToken ct = default) => Task.FromResult(Element(ProviderAdminService.Presets()));
    public async Task<JsonElement> CreateProviderAsync(CreateProviderRequest request, CancellationToken ct = default) => Element(await Get<ProviderAdminService>().CreateAsync(request, ct));
    public async Task<JsonElement> UpdateProviderAsync(string id, UpdateProviderRequest request, CancellationToken ct = default) => Element(await Get<ProviderAdminService>().UpdateAsync(id, request, ct));

    public Task DeleteProviderAsync(string id, CancellationToken ct = default) => Get<ProviderAdminService>().DeleteAsync(id, ct);
    public async Task<JsonElement> RefreshProvidersAsync(CancellationToken ct = default) => Element(await Get<ProviderAdminService>().RefreshAsync(Get<ModelCatalog>(), ct));
    public async Task<JsonElement> ProbeProviderAsync(ProbeRequest request, CancellationToken ct = default) => Element(await Get<ProviderAdminService>().ProbeAsync(request, ct));
    public async Task<JsonElement> CopilotLoginAsync(CancellationToken ct = default) => Element(await Get<ProviderAdminService>().CopilotLoginAsync(ct));
    public async Task<JsonElement> CopilotPollAsync(string deviceCode, CancellationToken ct = default) => Element(await Get<ProviderAdminService>().CopilotPollAsync(deviceCode, ct));

    // --- MCP and LSP ---------------------------------------------------------

    public Task<JsonElement> McpServersAsync(CancellationToken ct = default) => Task.FromResult(Element(Get<McpAdminService>().List()));
    public async Task<JsonElement> AddMcpAsync(CreateMcpRequest request, CancellationToken ct = default) => Element(await Get<McpAdminService>().AddAsync(request, ct));
    public async Task<JsonElement> ConnectMcpAsync(string id, CancellationToken ct = default) => Element(await Get<McpAdminService>().ConnectAsync(id, ct));
    public async Task<JsonElement> DisconnectMcpAsync(string id, CancellationToken ct = default) => Element(await Get<McpAdminService>().DisconnectAsync(id));
    public Task DeleteMcpAsync(string id, CancellationToken ct = default) => Get<McpAdminService>().RemoveAsync(id);
    public Task<JsonElement> LspServersAsync(CancellationToken ct = default) => Task.FromResult(Element(Get<LspAdminService>().List()));
    public async Task<JsonElement> RestartLspAsync(string id, CancellationToken ct = default) => Element(await Get<LspAdminService>().RestartAsync(id));
    public async Task<JsonElement> InstallLspAsync(string id, CancellationToken ct = default) => Element(await Get<LspAdminService>().InstallAsync(id, ct));

    // --- catalogues, files and VCS ------------------------------------------

    public Task<JsonElement> AgentsAsync(string? directory, CancellationToken ct = default) => Task.FromResult(Element(Get<AgentCatalog>().List(directory)
        .OrderBy(a => a.Source == "builtin" ? 0 : 1).ThenBy(a => a.Source == "builtin" ? "" : a.Id, StringComparer.OrdinalIgnoreCase)
        .Select(a => new { a.Id, a.Name, a.Description, a.Mode, a.Source, a.Path })));

    public Task<JsonElement> CommandsAsync(string? directory, CancellationToken ct = default) => Task.FromResult(Element(Get<CommandService>().List(directory)));
    public Task<JsonElement> SkillsAsync(string? directory, CancellationToken ct = default) => Task.FromResult(Element(Get<SkillService>().List(directory)));
    public Task<List<string>> FilesAsync(string directory, CancellationToken ct = default) => Task.FromResult(Get<FileService>().List(directory));

    public async Task<JsonElement> VcsAsync(string directory, CancellationToken ct = default) =>
        Element(string.IsNullOrEmpty(directory) ? new VcsInfo(false, "", []) : await Get<GitService>().InfoAsync(directory, ct));

    public async Task<List<FileDiff>> VcsDiffAsync(string directory, CancellationToken ct = default) =>
        string.IsNullOrEmpty(directory) ? [] : await Get<GitService>().DiffAsync(directory, ct);

    public async Task<JsonElement> WorktreesAsync(string directory, CancellationToken ct = default)
    {
        var info = string.IsNullOrEmpty(directory) ? null : await Get<GitService>().WorktreesAsync(directory, ct);
        return Element(new { root = info?.Root ?? "", worktrees = info?.Worktrees ?? [] });
    }

    public async Task<JsonElement> CreateWorktreeAsync(CreateWorktreeRequest request, CancellationToken ct = default)
    {
        try
        {
            return Element(await Get<GitService>().CreateWorktreeAsync(request.Directory, request.Name, ct));
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message);
        }
    }

    public async Task DeleteWorktreeAsync(DeleteWorktreeRequest request, CancellationToken ct = default)
    {
        try
        {
            await Get<GitService>().RemoveWorktreeAsync(request.Directory, request.Worktree, request.Force ?? false, ct);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message);
        }
    }

    // --- attachments and host actions ---------------------------------------

    public Task<Attachment> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) =>
        Get<AttachmentService>().SaveAsync(content, fileName, contentType, content.CanSeek ? content.Length : 0, ct);

    /// <summary>An in-process host has no URL space: the attachment is served straight from its file.</summary>
    public string AttachmentUrl(string id) => new Uri(Get<AttachmentService>().Get(id).StoragePath).AbsoluteUri;

    public Task OpenAsync(string path, string? app = null, CancellationToken ct = default)
    {
        Get<HostLauncher>().Open(path, app);
        return Task.CompletedTask;
    }

    // --- processes and browser ----------------------------------------------

    public Task<JsonElement> ProcessesAsync(string? sessionId, CancellationToken ct = default) => Task.FromResult(Element(Get<ProcessManager>().List(sessionId)));

    public Task<JsonElement> ProcessOutputAsync(string id, int tail = 200, CancellationToken ct = default)
    {
        var (info, output) = Get<ProcessManager>().Output(id, tail);
        return Task.FromResult(Element(new { info, output }));
    }

    public Task<JsonElement> KillProcessAsync(string id, CancellationToken ct = default) => Task.FromResult(Element(Get<ProcessManager>().Kill(id)));

    public Task<JsonElement> ClearProcessesAsync(CancellationToken ct = default)
    {
        var removed = Get<ProcessManager>().Clear();
        return Task.FromResult(Element(new { removed }));
    }

    public Task<JsonElement> BrowsersAsync(CancellationToken ct = default) => Task.FromResult(Element(Get<BrowserAdminService>().Engines()));
    public async Task<JsonElement> SessionBrowserAsync(string sessionId, CancellationToken ct = default) => Element(await Get<BrowserAdminService>().StateAsync(sessionId));
    public async Task<JsonElement> OpenBrowserAsync(string sessionId, string? browser, CancellationToken ct = default) => Element(await Get<BrowserAdminService>().OpenAsync(sessionId, browser, ct));

    public Task CloseBrowserAsync(string sessionId, CancellationToken ct = default) => Get<BrowserAdminService>().CloseAsync(sessionId);

    // --- config and knowledge -----------------------------------------------

    public Task<JsonElement> ConfigAsync(CancellationToken ct = default) => Task.FromResult(Element(Get<ConfigService>().View()));
    public async Task<JsonElement> PatchConfigAsync(ConfigPatch patch, CancellationToken ct = default) => Element(await Get<ConfigService>().PatchAsync(patch, ct));
    public Task<JsonElement> KnowledgeAsync(string? workspaceId = null, string? agent = null, CancellationToken ct = default) =>
        Task.FromResult(Element(Get<KnowledgeService>().List(workspaceId, agent)));

    public Task DeleteKnowledgeAsync(string id, CancellationToken ct = default)
    {
        if (!Get<KnowledgeService>().Delete(id)) throw new NotFoundException("Knowledge note not found.");
        return Task.CompletedTask;
    }

    // --- live events ---------------------------------------------------------

    public async IAsyncEnumerable<SofiaEventDto> EventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var reader = Get<IEventBus>().Subscribe(ct);
        await foreach (var e in reader.ReadAllAsync(ct))
            yield return new SofiaEventDto(e.Type, e.SessionId, e.WorkspaceId, e.Data);
    }
}
