using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
        if (fixture) LoadFixture();
        Localization.Changed += () => OnPropertyChanged(nameof(Language));
    }

    public BackendConnection Connection { get; } = new();

    public HarnessStore Store { get; } = new();

    public ThemeService Theme => ThemeService.Current;

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

    public string ActiveSessionTitle =>
        Workspaces.SelectMany(w => w.Conversations ?? [])
            .FirstOrDefault(s => s.Id == ActiveSessionId)?.Title ?? "Sofia";

    public string ActiveWorkspaceName =>
        Workspaces.FirstOrDefault(w => w.Id == ActiveWorkspaceId)?.Name ?? "";

    public string ActiveWorkspacePath =>
        Workspaces.FirstOrDefault(w => w.Id == ActiveWorkspaceId)?.Path ?? "";

    /// <summary>Context window usage of the active session, 0..1 (the composer meter).</summary>
    public double ContextFill { get; private set; } = 0.42;

    public void LoadFixture()
    {
        Workspaces.Clear();
        foreach (var workspace in Fixture.Workspaces()) Workspaces.Add(workspace);
        Statuses.Clear();
        foreach (var (id, status) in Fixture.Statuses()) Statuses[id] = status;
        Status = Statuses.GetValueOrDefault(ActiveSessionId, "idle");
        ContextFill = 0.42;
        OnPropertyChanged(nameof(ContextFill));
    }

    /// <summary>Loads the live workspace tree once a backend is ready.</summary>
    public async Task LoadAsync(ISofiaBackend backend, CancellationToken ct = default)
    {
        await Store.LoadAsync(backend, ct);
        Workspaces.Clear();
        foreach (var workspace in Store.Workspaces) Workspaces.Add(workspace);
        Statuses.Clear();
        foreach (var (id, status) in Store.Statuses) Statuses[id] = status;

        if (Workspaces.FirstOrDefault() is { } first)
        {
            ActiveWorkspaceId = first.Id;
            ActiveSessionId = first.Conversations?.FirstOrDefault()?.Id ?? "";
        }
        Status = Statuses.GetValueOrDefault(ActiveSessionId, "idle");
        OnPropertyChanged(nameof(ActiveSessionTitle));
        OnPropertyChanged(nameof(ActiveWorkspaceName));
        OnPropertyChanged(nameof(ActiveWorkspacePath));
    }
}
