using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sofia.App.Features.Sidebar;
using Sofia.Client;
using Sofia.Contracts.Domain;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Shell;

/// <summary>
/// State behind the shell window: the backend connection, the store, appearance/sound settings and the panel flags.
/// The shell itself (sidebar rows, header, composer chrome) is built in <c>ShellView</c> from this state; the feature
/// specs 040-043 replace the placeholder rows with real components.
/// </summary>
public sealed class ShellViewModel : ObservableObject
{
    private string _activeSessionId = "s_1";
    private string _activeWorkspaceId = "ws_sofia";
    private bool _rightPanelOpen;
    private bool _tasksOpen;
    private string _language = "en";
    private string _status = "working";

    public ShellViewModel(bool fixture = false)
    {
        IsFixture = fixture;
        SidebarStore = new SidebarStore(Store);
        Sidebar = new SidebarViewModel(SidebarStore);
        Composer = new Features.Composer.ComposerViewModel(Store);
        SidebarStore.Changed += SyncFromSidebar;
        SidebarStore.OpenSessionRequested += request =>
        {
            ActiveSessionId = request.SessionId;
            ActiveWorkspaceId = SidebarStore.ActiveWorkspaceId;
            Status = StatusFor(request.SessionId);
            OpenSessionRequested?.Invoke(request);
        };
        Store.Applied += Transcript.Apply;
        Store.DeltaFlushed += Transcript.AppendDelta;
        Transcript.Actions.OpenSession = OpenSessionAsync;
        if (fixture) LoadFixture();
        Localization.Changed += () => OnPropertyChanged(nameof(Language));
    }

    /// <summary>Raised when a sidebar row opens a conversation (the transcript scroll seam belongs to 041).</summary>
    public event Action<SessionOpenRequest>? OpenSessionRequested;

    public BackendConnection Connection { get; } = new();

    public HarnessStore Store { get; } = new();

    /// <summary>The sidebar slice: workspace tree, statuses, ordering and every mutation the sidebar performs.</summary>
    public SidebarStore SidebarStore { get; }

    public SidebarViewModel Sidebar { get; }

    /// <summary>The open conversation's transcript (spec 041).</summary>
    public Features.Transcript.TranscriptViewModel Transcript { get; } = new();

    /// <summary>The composer (spec 042): draft, attachments, chips, palettes, delivery mode and metrics.</summary>
    public Features.Composer.ComposerViewModel Composer { get; }

    public ThemeService Theme => ThemeService.Current;

    /// <summary>Raised after the settings dialog changed the accent, font size or compact sidebar.</summary>
    public event Action? AppearanceChanged;

    /// <summary>Asks the shell to re-apply the appearance tokens after a settings change.</summary>
    public void RaiseAppearanceChanged() => AppearanceChanged?.Invoke();

    public SoundService Sound => SoundService.Current;

    /// <summary>True when the shell renders <see cref="Fixture"/> data instead of a backend (screenshots, tests).</summary>
    public bool IsFixture { get; }

    public ObservableCollection<WorkspaceView> Workspaces { get; } = [];

    public Dictionary<string, string> Statuses { get; } = new(StringComparer.Ordinal);

    public string ActiveSessionId
    {
        get => _activeSessionId;
        set => SetProperty(ref _activeSessionId, value);
    }

    public string ActiveWorkspaceId
    {
        get => _activeWorkspaceId;
        set => SetProperty(ref _activeWorkspaceId, value);
    }

    public bool RightPanelOpen
    {
        get => _rightPanelOpen;
        set
        {
            if (!SetProperty(ref _rightPanelOpen, value)) return;
            LocalState.Current.SetFlag(LocalState.RightPanelKey, ActiveSessionId, value);
        }
    }

    public bool TasksOpen
    {
        get => _tasksOpen;
        set
        {
            if (!SetProperty(ref _tasksOpen, value)) return;
            LocalState.Current.SetFlag(LocalState.TasksPanelKey, ActiveSessionId, value);
        }
    }

    public string Language
    {
        get => _language;
        set
        {
            if (!SetProperty(ref _language, value)) return;
            Localization.SetLocale(value);
            LocalState.Current.Set("sofia.language", value);
        }
    }

    /// <summary>Status of the active session (working, waiting, permission, question, error, idle).</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Raw badge of the open conversation, including <c>done</c> (the header shows "last task finished"); unlike
    /// <see cref="Status"/>, which folds <c>done</c> into <c>idle</c> for the composer and the transcript.
    /// </summary>
    public string HeaderStatus
    {
        get
        {
            if (HeaderStatusOverride is { Length: > 0 } forced) return forced;
            if (SidebarStore.Badges.TryGetValue(ActiveSessionId, out var badge)) return badge;
            return FindSession(ActiveSessionId)?.Status ?? "idle";
        }
    }

    /// <summary>Screenshot/frame harness override of the status dot (spec 043 evidence).</summary>
    public string? HeaderStatusOverride { get; set; }

    public string ActiveSessionTitle =>
        Store.Workspaces.SelectMany(w => w.Conversations ?? [])
            .FirstOrDefault(s => s.Id == ActiveSessionId)?.Title ?? "Sofia";

    public string ActiveWorkspaceName =>
        Store.Workspaces.FirstOrDefault(w => w.Id == ActiveWorkspaceId)?.Name ?? "";

    public string ActiveWorkspacePath =>
        Store.Workspaces.FirstOrDefault(w => w.Id == ActiveWorkspaceId)?.Path ?? "";

    /// <summary>Context window usage of the active session, 0..1 (the composer meter).</summary>
    public double ContextFill { get; private set; } = 0.42;

    public void LoadFixture(bool empty = false)
    {
        SidebarStore.Seed(empty ? [] : Fixture.Workspaces(), empty ? new Dictionary<string, string>() : Fixture.Statuses());
        SyncFromSidebar();
        ContextFill = 0.42;
        OnPropertyChanged(nameof(ContextFill));
        if (empty) Transcript.Detach();
        else LoadTranscriptState("idle");
        SyncComposer();
    }

    /// <summary>
    /// Loads one deterministic transcript fixture (screenshot gate): idle, streaming, permission, questions, diff,
    /// lightbox, markdown, compaction, retry, queued or waiting.
    /// </summary>
    public void LoadTranscriptState(string state)
    {
        var sessionId = ActiveSessionId.Length > 0 ? ActiveSessionId : "s_1";
        var fixture = Fixture.Transcript(state, sessionId);
        Transcript.Load(fixture.Messages, sessionId, fixture.Status, fixture.Queued);
        Transcript.Actions.Backend = null;
        ContextFill = 0.42;
        OnPropertyChanged(nameof(ContextFill));
        SyncComposer();
    }

    /// <summary>Loads the live workspace tree once a backend is ready, then the open conversation's transcript.</summary>
    public async Task LoadAsync(ISofiaBackend backend, CancellationToken ct = default)
    {
        await SidebarStore.LoadAsync(backend, ct);
        ActiveSessionId = SidebarStore.ActiveSessionId;
        ActiveWorkspaceId = SidebarStore.ActiveWorkspaceId;
        SyncFromSidebar();
        Transcript.Actions.Backend = backend;
        Transcript.Actions.OpenSession = OpenSessionAsync;
        Composer.SetBackend(backend);
        await Transcript.LoadSessionAsync(backend, ActiveSessionId, ct);
        SyncComposer();
    }

    /// <summary>Switches the open conversation and loads its transcript (sidebar row, search hit, subagent link, branch).</summary>
    public async Task OpenSessionAsync(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        ActiveSessionId = sessionId;
        ActiveWorkspaceId = Store.Workspaces.FirstOrDefault(w => (w.Conversations ?? []).Any(s => s.Id == sessionId))?.Id
                            ?? ActiveWorkspaceId;
        Status = StatusFor(sessionId);
        OnPropertyChanged(nameof(ActiveSessionTitle));
        // Park the previous conversation's draft before the composer binds to the new one (spec 042 risk).
        Composer.FlushDraft();
        SyncComposer();
        if (Connection.Backend is { } backend)
        {
            await Transcript.LoadSessionAsync(backend, sessionId);
            SyncComposer();
        }
        else
        {
            LoadTranscriptState("idle");
        }
    }

    /// <summary>Mirrors the sidebar slice into the shell header state after every change.</summary>
    private void SyncFromSidebar()
    {
        Workspaces.Clear();
        foreach (var workspace in SidebarStore.Workspaces) Workspaces.Add(workspace.Workspace);
        Statuses.Clear();
        foreach (var (id, status) in SidebarStore.Badges) Statuses[id] = status;
        if (SidebarStore.ActiveSessionId.Length > 0)
        {
            ActiveSessionId = SidebarStore.ActiveSessionId;
            ActiveWorkspaceId = SidebarStore.ActiveWorkspaceId;
        }
        Status = StatusFor(ActiveSessionId);
        OnPropertyChanged(nameof(HeaderStatus));
        Transcript.SetStatus(Status is "done" ? "idle" : Status);
        OnPropertyChanged(nameof(ActiveSessionTitle));
        OnPropertyChanged(nameof(ActiveWorkspaceName));
        OnPropertyChanged(nameof(ActiveWorkspacePath));
        SyncComposer();
    }

    /// <summary>
    /// Binds the composer to the open conversation: the session owns agent/model/permission/reasoning, the workspace's
    /// folder scopes commands, agents, files and the branch chip, and the status drives send vs stop.
    /// </summary>
    private void SyncComposer()
    {
        var session = FindSession(ActiveSessionId);
        var workspace = Store.Workspaces.FirstOrDefault(w => w.Id == (session?.WorkspaceId ?? ActiveWorkspaceId));
        var directory = workspace is { Kind: "folder" } ? workspace.Path : "";
        Composer.SetSession(session, ActiveSessionId, directory, Status);
    }

    /// <summary>The session entity of a conversation, or null when it is not loaded.</summary>
    public Session? FindSession(string? sessionId) =>
        sessionId is null
            ? null
            : Store.Workspaces.SelectMany(w => w.Conversations ?? []).FirstOrDefault(s => s.Id == sessionId);

    private string StatusFor(string sessionId)
    {
        if (SidebarStore.Badges.TryGetValue(sessionId, out var badge)) return badge == "done" ? "idle" : badge;
        var session = Store.Workspaces.SelectMany(w => w.Conversations ?? []).FirstOrDefault(s => s.Id == sessionId);
        return session?.Status ?? "idle";
    }
}
