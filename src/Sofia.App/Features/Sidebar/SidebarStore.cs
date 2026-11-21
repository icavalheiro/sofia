using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.App.Services;
using Sofia.Client;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Features.Sidebar;

/// <summary>A request to open a conversation, optionally scrolling to one message (the transcript seam owned by 041).</summary>
public sealed record SessionOpenRequest(string SessionId, string? MessageId, bool ScrollToMessage);

/// <summary>
/// Sidebar slice over <see cref="ISofiaBackend"/>: consumes the workspace tree and statuses cached by
/// <see cref="HarnessStore"/>, applies the sidebar's own rules (pinned-first ordering, the client-side <c>done</c>
/// badge, expansion, archive folder, optimistic reorder) and performs every mutating call the sidebar exposes. All
/// events are applied on the UI thread by <see cref="HarnessStore.ListenAsync"/>, so the rows can be rebuilt directly.
/// </summary>
public sealed class SidebarStore : ObservableObject
{
    private readonly HarnessStore _harness;
    private readonly Dictionary<string, string> _badges = new(StringComparer.Ordinal);
    private readonly List<ModelChoice> _models = [];

    private ISofiaBackend? _backend;
    private bool _loading;

    public SidebarStore(HarnessStore harness)
    {
        _harness = harness;
        _harness.Applied += OnApplied;
    }

    public ISofiaBackend? Backend => _backend;

    public bool IsConnected => _backend is not null;

    /// <summary>Workspace rows in tree order: folders first, then chat groups (the server's manual order).</summary>
    public ObservableCollection<WorkspaceRowViewModel> Workspaces { get; } = [];

    /// <summary>Display status per session (server status plus the client-side <c>done</c> badge).</summary>
    public IReadOnlyDictionary<string, string> Badges => _badges;

    public IReadOnlyList<ModelChoice> Models => _models;

    public string ActiveSessionId { get; private set; } = "";

    public string ActiveWorkspaceId { get; private set; } = "";

    public bool IsLoading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    /// <summary>Last failed call, rendered under the add-workspace form and the workspace settings menu.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised whenever the row projection changed and the view must rebuild.</summary>
    public event Action? Changed;

    /// <summary>Raised when a row (or search hit) asks to open a conversation; the shell forwards it to the transcript.</summary>
    public event Action<SessionOpenRequest>? OpenSessionRequested;

    public async Task LoadAsync(ISofiaBackend backend, CancellationToken ct = default)
    {
        _backend = backend;
        IsLoading = true;
        try
        {
            await _harness.LoadAsync(backend, ct);
            // The web creates one "Quick chats" group on an empty account (useHarness.loadWorkspaces).
            if (_harness.Workspaces.Count == 0)
            {
                try
                {
                    var quick = await backend.CreateWorkspaceAsync(new CreateWorkspaceRequest("chat", null, "Quick chats", "chat", null), ct);
                    _harness.Workspaces.Add(quick);
                }
                catch
                {
                    // Offline or a read-only account: leave the tree empty, the UI shows the empty state.
                }
            }
            LoadModels();
            _badges.Clear();
            foreach (var (sessionId, status) in _harness.Statuses) _badges[sessionId] = status;
            EnsureSelection();
            Rebuild();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Seeds the store with deterministic data (fixture mode and the screenshot harness).</summary>
    public void Seed(IEnumerable<WorkspaceView> workspaces, IReadOnlyDictionary<string, string> statuses)
    {
        _harness.Workspaces.Clear();
        foreach (var workspace in workspaces) _harness.Workspaces.Add(workspace);
        _badges.Clear();
        foreach (var (sessionId, status) in statuses) _badges[sessionId] = status;
        EnsureSelection();
        Rebuild();
    }

    // --- projection ---------------------------------------------------------------------------------------

    /// <summary>Rebuilds every row from the cached tree and statuses.</summary>
    public void Rebuild()
    {
        Workspaces.Clear();
        foreach (var workspace in _harness.Workspaces.OrderBy(w => w.Order))
        {
            Workspaces.Add(BuildWorkspaceRow(workspace));
        }
        Changed?.Invoke();
    }

    private WorkspaceRowViewModel BuildWorkspaceRow(WorkspaceView workspace)
    {
        var sessions = (workspace.Conversations ?? [])
            .OrderByDescending(s => s.Pinned)
            .ThenBy(s => s.Order)
            .ThenByDescending(s => s.UpdatedAt)
            .ToList();

        var roots = sessions.Where(s => s.ParentId is null && !s.Archived).ToList();
        var archived = sessions.Where(s => s.ParentId is null && s.Archived).ToList();
        var byParent = sessions
            .Where(s => s.ParentId is not null)
            .GroupBy(s => s.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Subagent rows nest under their parent, but only while running or while the user is viewing one of them
        // (or a descendant) — the web's `convTree`/`showChild` rule.
        bool Shows(Session child, HashSet<string> visited)
        {
            if (!visited.Add(child.Id)) return false;
            if (child.Id == ActiveSessionId || StatusOf(child) == "working") return true;
            return byParent.TryGetValue(child.Id, out var children) && children.Any(c => Shows(c, visited));
        }

        var tree = new List<ConversationRowViewModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Walk(Session session, int depth)
        {
            if (!seen.Add(session.Id)) return;
            tree.Add(Row(session, depth));
            if (!byParent.TryGetValue(session.Id, out var children)) return;
            foreach (var child in children)
            {
                if (Shows(child, new HashSet<string>(StringComparer.Ordinal))) Walk(child, depth + 1);
            }
        }

        foreach (var root in roots) Walk(root, 0);

        var statuses = roots.Select(StatusOf).ToList();
        var status = SidebarStatus.Idle;
        foreach (var candidate in SidebarStatus.Priority)
        {
            if (!statuses.Contains(candidate)) continue;
            status = candidate;
            break;
        }

        return new WorkspaceRowViewModel(workspace)
        {
            Tree = tree,
            Archived = archived.Select(s => Row(s, 0)).ToList(),
            Count = roots.Count,
            Status = status,
        };
    }

    private ConversationRowViewModel Row(Session session, int depth) => new(session, depth)
    {
        Status = StatusOf(session),
        IsActive = session.Id == ActiveSessionId,
    };

    private string StatusOf(Session session)
    {
        if (_badges.TryGetValue(session.Id, out var badge) && badge.Length > 0) return badge;
        return session.Status != SessionStatus.Idle ? session.Status : SidebarStatus.Idle;
    }

    private void EnsureSelection()
    {
        var first = _harness.Workspaces
            .OrderBy(w => w.Order)
            .SelectMany(w => (w.Conversations ?? [])
                .Where(s => s.ParentId is null && !s.Archived)
                .OrderByDescending(s => s.Pinned)
                .ThenBy(s => s.Order)
                .ThenByDescending(s => s.UpdatedAt)
                .Select(s => (Workspace: w, Session: s)))
            .FirstOrDefault();

        var known = _harness.Workspaces
            .SelectMany(w => w.Conversations ?? [])
            .Any(s => s.Id == ActiveSessionId);
        if (known && ActiveSessionId.Length > 0) return;
        ActiveSessionId = first.Session?.Id ?? "";
        ActiveWorkspaceId = first.Workspace?.Id ?? _harness.Workspaces.FirstOrDefault()?.Id ?? "";
    }

    // --- selection ----------------------------------------------------------------------------------------

    /// <summary>Opens a conversation; a collapsed workspace expands (persisted through PATCH, like the web).</summary>
    public async Task OpenSessionAsync(string workspaceId, string sessionId, string? messageId = null)
    {
        var workspace = Find(workspaceId) ?? FindFor(sessionId);
        if (workspace is not null && !workspace.Expanded)
        {
            workspace.Expanded = true;
            Rebuild();
            await PatchWorkspaceAsync(workspace.Id, new UpdateWorkspaceRequest(null, null, null, true, null));
        }

        ActiveWorkspaceId = workspace?.Id ?? workspaceId;
        ActiveSessionId = sessionId;
        // Opening a finished conversation clears its "done" badge (web: selectConversation).
        if (_badges.TryGetValue(sessionId, out var badge) && badge == SidebarStatus.Done) _badges[sessionId] = SidebarStatus.Idle;
        SoundService.Current.Play(SoundKey.Select);
        Rebuild();
        OpenSessionRequested?.Invoke(new SessionOpenRequest(sessionId, messageId, messageId is not null));
    }

    /// <summary>Moves the selection through the visible sessions, wrapping around (Alt+↑/↓).</summary>
    public void StepSession(int step)
    {
        var flat = Workspaces
            .SelectMany(w => w.Tree.Where(row => !row.IsSubagent).Select(row => (Workspace: w.Id, Session: row.Id)))
            .ToList();
        if (flat.Count == 0) return;
        var at = flat.FindIndex(x => x.Session == ActiveSessionId);
        var next = at < 0 ? (step > 0 ? 0 : flat.Count - 1) : (at + step + flat.Count) % flat.Count;
        var target = flat[next];
        _ = OpenSessionAsync(target.Workspace, target.Session);
    }

    // --- workspace actions --------------------------------------------------------------------------------

    public WorkspaceRowViewModel? ActiveWorkspace => Workspaces.FirstOrDefault(w => w.Id == ActiveWorkspaceId);

    /// <summary>The open conversation row, if it is in the tree.</summary>
    public ConversationRowViewModel? ActiveSession => Workspaces
        .SelectMany(w => w.Tree.Concat(w.Archived))
        .FirstOrDefault(r => r.Id == ActiveSessionId);

    public async Task ToggleWorkspaceAsync(string workspaceId)
    {
        var workspace = Find(workspaceId);
        if (workspace is null) return;
        workspace.Expanded = !workspace.Expanded;
        ActiveWorkspaceId = workspaceId;
        SoundService.Current.Play(SoundKey.Click);
        Rebuild();
        await PatchWorkspaceAsync(workspaceId, new UpdateWorkspaceRequest(null, null, null, workspace.Expanded, null));
    }

    public async Task SetWorkspaceIconAsync(string workspaceId, string icon)
    {
        var workspace = Find(workspaceId);
        if (workspace is null) return;
        workspace.Icon = icon;
        Rebuild();
        await PatchWorkspaceAsync(workspaceId, new UpdateWorkspaceRequest(null, icon, null, null, null));
    }

    public async Task SetWorkspaceModelAsync(string workspaceId, string modelId)
    {
        var workspace = Find(workspaceId);
        if (workspace is null) return;
        workspace.ModelId = modelId;
        Rebuild();
        await PatchWorkspaceAsync(workspaceId, new UpdateWorkspaceRequest(null, null, modelId, null, null));
    }

    /// <summary>Live rename; <paramref name="debounced"/> is false when the user pressed Enter (send immediately).</summary>
    public async Task SetWorkspaceNameAsync(string workspaceId, string name, bool debounced)
    {
        var trimmed = name.Trim();
        var workspace = Find(workspaceId);
        if (workspace is null || trimmed.Length == 0) return;
        workspace.Name = trimmed;
        Rebuild();
        if (!debounced) await PatchWorkspaceAsync(workspaceId, new UpdateWorkspaceRequest(trimmed, null, null, null, null));
    }

    /// <summary>Updates the row name while the user types; the debounced PATCH follows (web `setWorkspaceName`).</summary>
    public void PreviewWorkspaceName(string workspaceId, string name)
    {
        var workspace = Find(workspaceId);
        if (workspace is null || name.Trim().Length == 0) return;
        workspace.Name = name.Trim();
        Rebuild();
    }

    public async Task<string?> RemoveWorkspaceAsync(string workspaceId)
    {
        if (_backend is not null)
        {
            try
            {
                await _backend.DeleteWorkspaceAsync(workspaceId);
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }
        var workspace = Find(workspaceId);
        if (workspace is not null) _harness.Workspaces.Remove(workspace);
        EnsureSelection();
        Rebuild();
        return null;
    }

    /// <summary>Adds a folder workspace server-side (desktop picker or a typed path in the browser head).</summary>
    public async Task<string?> AddWorkspaceAsync(string path)
    {
        var trimmed = path.Trim().Trim('"');
        trimmed = trimmed.TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':') trimmed += "\\";
        if (trimmed.Length == 0) return "Enter a folder path.";
        if (_backend is null) return "Not connected.";
        try
        {
            var created = await _backend.CreateWorkspaceAsync(new CreateWorkspaceRequest("folder", trimmed, null, null, DefaultModelId()));
            _harness.Workspaces.Add(created);
            await FocusWorkspaceAsync(created);
            return null;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    public async Task AddChatGroupAsync(string? name = null)
    {
        if (_backend is null) return;
        try
        {
            var created = await _backend.CreateWorkspaceAsync(new CreateWorkspaceRequest("chat", null, string.IsNullOrWhiteSpace(name) ? "New group" : name.Trim(), "chat", DefaultModelId()));
            _harness.Workspaces.Add(created);
            await FocusWorkspaceAsync(created);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public async Task NewConversationAsync()
    {
        var workspaceId = ActiveWorkspaceId.Length > 0 ? ActiveWorkspaceId : Workspaces.FirstOrDefault()?.Id;
        if (workspaceId is not null) await NewConversationInAsync(workspaceId);
    }

    public async Task NewConversationInAsync(string workspaceId)
    {
        if (_backend is null) return;
        try
        {
            var workspace = Find(workspaceId);
            var session = await _backend.CreateSessionAsync(workspaceId, new CreateSessionRequest(null));
            if (workspace is not null)
            {
                workspace.Conversations ??= [];
                workspace.Conversations.Insert(0, session);
                workspace.Expanded = true;
            }
            await OpenSessionAsync(workspaceId, session.Id);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    public async Task ReorderWorkspacesAsync(IReadOnlyList<string> orderedIds)
    {
        ApplyWorkspaceOrder(orderedIds);
        Rebuild();
        if (_backend is null) return;
        try
        {
            await _backend.ReorderWorkspacesAsync(orderedIds);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void ApplyWorkspaceOrder(IReadOnlyList<string> orderedIds)
    {
        var byId = _harness.Workspaces.ToDictionary(w => w.Id);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            if (byId.TryGetValue(orderedIds[i], out var workspace)) workspace.Order = i + 1;
        }
    }

    private async Task FocusWorkspaceAsync(WorkspaceView workspace)
    {
        ActiveWorkspaceId = workspace.Id;
        var first = (workspace.Conversations ?? []).FirstOrDefault(s => s.ParentId is null && !s.Archived);
        if (first is not null) await OpenSessionAsync(workspace.Id, first.Id);
        else await NewConversationInAsync(workspace.Id);
        Rebuild();
    }

    private async Task PatchWorkspaceAsync(string workspaceId, UpdateWorkspaceRequest request)
    {
        if (_backend is null) return;
        try
        {
            await _backend.UpdateWorkspaceAsync(workspaceId, request);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private string DefaultModelId() =>
        _models.FirstOrDefault(m => m.Id.Length > 0)?.Id ?? "";

    // --- conversation actions -----------------------------------------------------------------------------

    public async Task RenameSessionAsync(string sessionId, string title)
    {
        var session = FindSession(sessionId);
        var next = title.Trim();
        if (session is null || next.Length == 0 || next == session.Title) return;
        var previous = session.Title;
        session.Title = next;
        Rebuild();
        if (_backend is null) return;
        try
        {
            await _backend.UpdateSessionAsync(sessionId, new UpdateSessionRequest(next, null, null, null, null));
        }
        catch (Exception ex)
        {
            session.Title = previous;
            Rebuild();
            Fail(ex);
        }
    }

    public async Task ArchiveSessionAsync(string sessionId)
    {
        var session = FindSession(sessionId);
        if (session is null) return;
        session.Archived = true;
        session.ArchivedAt = DateTime.UtcNow;
        // The open conversation cannot stay in the archive: move to the next one (web: ensureSelection).
        if (sessionId == ActiveSessionId) EnsureSelection();
        Rebuild();
        if (_backend is null) return;
        try
        {
            await _backend.ArchiveSessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            session.Archived = false;
            Rebuild();
            Fail(ex);
        }
    }

    public async Task RestoreSessionAsync(string sessionId)
    {
        var session = FindSession(sessionId);
        if (session is null) return;
        session.Archived = false;
        Rebuild();
        if (_backend is null) return;
        try
        {
            await _backend.UnarchiveSessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            session.Archived = true;
            Rebuild();
            Fail(ex);
        }
    }

    public async Task PinSessionAsync(string sessionId, bool pinned)
    {
        var session = FindSession(sessionId);
        if (session is null) return;
        session.Pinned = pinned;
        Rebuild();
        if (_backend is null) return;
        try
        {
            if (pinned) await _backend.PinSessionAsync(sessionId);
            else await _backend.UnpinSessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            session.Pinned = !pinned;
            Rebuild();
            Fail(ex);
        }
    }

    public async Task<string?> DeleteSessionAsync(string sessionId)
    {
        if (_backend is not null)
        {
            try
            {
                await _backend.DeleteSessionAsync(sessionId);
            }
            catch (Exception ex)
            {
                return Fail(ex);
            }
        }
        foreach (var workspace in _harness.Workspaces)
        {
            workspace.Conversations?.RemoveAll(s => s.Id == sessionId);
        }
        _badges.Remove(sessionId);
        if (sessionId == ActiveSessionId) EnsureSelection();
        Rebuild();
        return null;
    }

    public async Task ReorderSessionsAsync(string workspaceId, IReadOnlyList<string> orderedIds)
    {
        var workspace = Find(workspaceId);
        if (workspace?.Conversations is { } conversations)
        {
            var byId = conversations.ToDictionary(s => s.Id);
            var ordered = orderedIds.Select(id => byId.GetValueOrDefault(id)).OfType<Session>().ToList();
            var moved = ordered.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var next = 0;
            for (var i = 0; i < conversations.Count; i++)
            {
                if (moved.Contains(conversations[i].Id)) conversations[i] = ordered[next++];
            }
            for (var i = 0; i < orderedIds.Count; i++)
            {
                if (byId.TryGetValue(orderedIds[i], out var session)) session.Order = i + 1;
            }
        }
        Rebuild();
        if (_backend is null) return;
        try
        {
            await _backend.ReorderSessionsAsync(workspaceId, orderedIds);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    // --- search -------------------------------------------------------------------------------------------

    /// <summary>Runs <c>GET /api/search</c> exactly like the web's quick prompt (archived and subagents included).</summary>
    public async Task<List<SearchHitRow>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (_backend is null || query.Trim().Length == 0) return [];
        try
        {
            var json = await _backend.SearchAsync(query.Trim(), null, true, true, 30, ct);
            return ParseHits(json);
        }
        catch (Exception ex)
        {
            Fail(ex);
            return [];
        }
    }

    public static List<SearchHitRow> ParseHits(JsonElement json)
    {
        var rows = new List<SearchHitRow>();
        // GET /api/search answers `{ results: [...] }`; accept a bare array too (the in-process host serializes the
        // same object, but a transport may hand over the array itself).
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("results", out var results)) json = results;
        if (json.ValueKind != JsonValueKind.Array) return rows;
        foreach (var result in json.EnumerateArray())
        {
            var sessionId = Str(result, "sessionId") ?? "";
            var workspaceId = Str(result, "workspaceId") ?? "";
            var title = Str(result, "title") ?? "";
            if (!result.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;
            foreach (var hit in hits.EnumerateArray())
            {
                var messageId = Str(hit, "messageId") ?? "";
                var snippet = Str(hit, "snippet") ?? "";
                var marks = new List<(int, int)>();
                if (hit.TryGetProperty("highlights", out var highlights) && highlights.ValueKind == JsonValueKind.Array)
                {
                    foreach (var range in highlights.EnumerateArray())
                    {
                        if (range.ValueKind != JsonValueKind.Array) continue;
                        var pair = range.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0).ToArray();
                        if (pair.Length >= 2) marks.Add((pair[0], pair[1]));
                    }
                }
                rows.Add(new SearchHitRow
                {
                    Key = $"{sessionId}:{messageId}",
                    SessionId = sessionId,
                    WorkspaceId = workspaceId,
                    Title = title,
                    Snippet = snippet,
                    Highlights = marks,
                });
            }
        }
        return rows;
    }

    // --- worktrees ----------------------------------------------------------------------------------------

    public async Task<(string Root, List<WorktreeEntry> Entries)> ListWorktreesAsync(string directory)
    {
        if (_backend is null) return ("", []);
        var json = await _backend.WorktreesAsync(directory);
        return ParseWorktrees(json);
    }

    public static (string Root, List<WorktreeEntry> Entries) ParseWorktrees(JsonElement json)
    {
        var root = Str(json, "root") ?? "";
        var entries = new List<WorktreeEntry>();
        if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("worktrees", out var list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
            {
                var directory = Str(entry, "directory") ?? "";
                if (directory.Length == 0) continue;
                var isMain = entry.TryGetProperty("isMain", out var main) && main.ValueKind == JsonValueKind.True;
                entries.Add(new WorktreeEntry(directory, Str(entry, "branch") ?? "", isMain));
            }
        }
        return (root, entries);
    }

    public async Task<WorktreeEntry?> CreateWorktreeAsync(string directory, string name)
    {
        if (_backend is null) return null;
        var json = await _backend.CreateWorktreeAsync(new CreateWorktreeRequest(directory, name));
        var entry = json.ValueKind == JsonValueKind.Object
            ? new WorktreeEntry(
                Str(json, "directory") ?? "",
                Str(json, "branch") ?? "",
                json.TryGetProperty("isMain", out var main) && main.ValueKind == JsonValueKind.True)
            : null;
        return entry is { Directory.Length: > 0 } ? entry : null;
    }

    public async Task DeleteWorktreeAsync(string directory, string worktree, bool force)
    {
        if (_backend is null) return;
        await _backend.DeleteWorktreeAsync(new DeleteWorktreeRequest(directory, worktree, force));
    }

    /// <summary>Opens (or creates) the workspace pointing at a worktree directory, the web's <c>openWorkspaceAt</c>.</summary>
    public async Task SwitchToWorktreeAsync(string directory)
    {
        var existing = _harness.Workspaces.FirstOrDefault(w => w.Kind != "chat" && WorktreeEntry.SamePath(w.Path, directory));
        if (existing is not null)
        {
            await FocusWorkspaceAsync(existing);
            return;
        }
        await AddWorkspaceAsync(directory);
    }

    // --- models -------------------------------------------------------------------------------------------

    private void LoadModels()
    {
        _models.Clear();
        var json = _harness.Models;
        if (json is not { ValueKind: JsonValueKind.Array }) return;
        foreach (var model in json.Value.EnumerateArray())
        {
            var id = Str(model, "id") ?? "";
            if (id.Length == 0) continue;
            var enabled = !model.TryGetProperty("enabled", out var e) || e.ValueKind != JsonValueKind.False;
            if (!enabled) continue;
            var local = model.TryGetProperty("local", out var l) && l.ValueKind == JsonValueKind.True;
            _models.Add(new ModelChoice(id, Str(model, "label") ?? id, local, Str(model, "providerIcon") ?? "cpu"));
        }
    }

    // --- events -------------------------------------------------------------------------------------------

    private void OnApplied(SofiaEventDto evt)
    {
        switch (evt.Type)
        {
            case EventTypes.SessionStatus:
            {
                var status = Str(evt.Data, "status") ?? SessionStatus.Idle;
                if (evt.SessionId is null) break;
                var session = FindSession(evt.SessionId);
                var prev = session is null ? SidebarStatus.Idle : StatusOf(session);
                if (status == SessionStatus.Idle)
                {
                    // A run that ends while its conversation is not open shows "done" until the user opens it.
                    _badges[evt.SessionId] = evt.SessionId != ActiveSessionId &&
                                             (SidebarStatus.IsBusy(prev) || prev == SidebarStatus.Done)
                        ? SidebarStatus.Done
                        : SidebarStatus.Idle;
                }
                else
                {
                    _badges[evt.SessionId] = status;
                }
                Rebuild();
                break;
            }
            case EventTypes.SessionDeleted:
                if (evt.SessionId is not null) _badges.Remove(evt.SessionId);
                EnsureSelection();
                Rebuild();
                break;
            case EventTypes.WorkspaceUpdated:
            case EventTypes.WorkspaceDeleted:
            case EventTypes.SessionCreated:
            case EventTypes.SessionUpdated:
            case EventTypes.TasksUpdated:
                if (evt.Type == EventTypes.WorkspaceUpdated) EnsureSelection();
                Rebuild();
                break;
        }
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private WorkspaceView? Find(string workspaceId) => _harness.Workspaces.FirstOrDefault(w => w.Id == workspaceId);

    private WorkspaceView? FindFor(string sessionId) =>
        _harness.Workspaces.FirstOrDefault(w => (w.Conversations ?? []).Any(s => s.Id == sessionId));

    private Session? FindSession(string sessionId) =>
        _harness.Workspaces.SelectMany(w => w.Conversations ?? []).FirstOrDefault(s => s.Id == sessionId);

    private string Fail(Exception ex)
    {
        LastError = ex.Message;
        OnPropertyChanged(nameof(LastError));
        return ex.Message;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
