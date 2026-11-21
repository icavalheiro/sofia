using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Sofia.App.Features.Sidebar;

/// <summary>
/// View state of the sidebar: the two sections, the past-session quick prompt, the add-workspace form, the inline
/// rename, the archive folders that are open, the delete confirmation and which workspace settings menu is open. All
/// backend rules live in <see cref="SidebarStore"/>; this class only owns what the web keeps in component refs.
/// </summary>
public sealed class SidebarViewModel : ObservableObject
{
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _renameTimer;
    private readonly DispatcherTimer _workspaceNameTimer;

    private bool _searchOpen;
    private string _searchQuery = "";
    private List<SearchHitRow> _searchRows = [];
    private int _searchIndex;
    private bool _searching;
    private bool _addOpen;
    private string _addPath = "";
    private string? _addError;
    private string? _editingSessionId;
    private string _editText = "";
    private ConversationRowViewModel? _pendingDelete;
    private WorkspaceRowViewModel? _pendingDeleteWorkspace;
    private bool _deleting;
    private string? _settingsWorkspaceId;
    private string? _openArchiveId;
    private int _searchSeq;
    private string? _pendingWorkspaceNameId;
    private string? _pendingRenameSessionId;

    public SidebarViewModel(SidebarStore store)
    {
        Store = store;
        _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(180), DispatcherPriority.Background, (_, _) =>
        {
            _searchTimer!.Stop();
            _ = RunSearchAsync();
        });
        _renameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _renameTimer!.Stop();
            if (_pendingRenameSessionId is { } id) _ = Store.RenameSessionAsync(id, EditText);
        });
        _workspaceNameTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) =>
        {
            _workspaceNameTimer!.Stop();
            if (_pendingWorkspaceNameId is { } id) _ = Store.SetWorkspaceNameAsync(id, WorkspaceNameDraft, debounced: true);
        });
        Store.Changed += RefreshSections;
        RefreshSections();
    }

    public SidebarStore Store { get; }

    public ObservableCollection<WorkspaceRowViewModel> FolderWorkspaces { get; } = [];

    public ObservableCollection<WorkspaceRowViewModel> ChatGroups { get; } = [];

    /// <summary>False when the account has no folder workspace and no chat group (the empty-account state).</summary>
    public bool HasWorkspaces => FolderWorkspaces.Count > 0 || ChatGroups.Count > 0;

    // --- past-session quick prompt ------------------------------------------------------------------------

    public bool IsSearchOpen
    {
        get => _searchOpen;
        private set => SetProperty(ref _searchOpen, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (!SetProperty(ref _searchQuery, value)) return;
            _searchIndex = 0;
            _searchTimer.Stop();
            if (value.Trim().Length == 0)
            {
                _searchSeq++;
                _searchRows = [];
                IsSearching = false;
                OnPropertyChanged(nameof(SearchRows));
                return;
            }
            _searchTimer.Start();
        }
    }

    public IReadOnlyList<SearchHitRow> SearchRows
    {
        get => _searchRows;
        private set => SetProperty(ref _searchRows, value.ToList());
    }

    public bool IsSearching
    {
        get => _searching;
        private set => SetProperty(ref _searching, value);
    }

    public int SearchIndex
    {
        get => _searchIndex;
        set
        {
            var max = Math.Max(0, _searchRows.Count - 1);
            if (!SetProperty(ref _searchIndex, Math.Clamp(value, 0, max))) return;
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>A row was highlighted/opened; the view repaints the active search row.</summary>
    public event Action? SelectionChanged;

    public void OpenSearch()
    {
        IsSearchOpen = true;
        _ = RunSearchAsync();
    }

    /// <summary>Seeds the quick prompt with fixed hits (fixture mode and the screenshot gate).</summary>
    public void PreviewSearch(string query, IReadOnlyList<SearchHitRow> rows)
    {
        _searchQuery = query;
        _searchRows = rows.ToList();
        _searchIndex = 0;
        _searching = false;
        OnPropertyChanged(nameof(SearchQuery));
        OnPropertyChanged(nameof(SearchRows));
        OnPropertyChanged(nameof(IsSearching));
        IsSearchOpen = true;
    }

    public void CloseSearch()
    {
        IsSearchOpen = false;
        _searchSeq++;
        _searchTimer.Stop();
    }

    public void ToggleSearch()
    {
        if (IsSearchOpen) CloseSearch();
        else OpenSearch();
    }

    public void MoveSearch(int step)
    {
        if (_searchRows.Count == 0) return;
        SearchIndex = _searchIndex + step;
    }

    /// <summary>Enter: opens the highlighted hit and hands the scroll target to the transcript (041).</summary>
    public async Task OpenSearchHitAsync()
    {
        if (_searchRows.Count == 0) return;
        var row = _searchRows[Math.Clamp(_searchIndex, 0, _searchRows.Count - 1)];
        CloseSearch();
        var workspaceId = row.WorkspaceId.Length > 0
            ? row.WorkspaceId
            : Store.Workspaces.FirstOrDefault(w => w.Tree.Any(r => r.Id == row.SessionId))?.Id
              ?? Store.Workspaces.FirstOrDefault(w => w.Archived.Any(r => r.Id == row.SessionId))?.Id
              ?? "";
        await Store.OpenSessionAsync(workspaceId, row.SessionId, row.Key.Split(':').Last());
    }

    private async Task RunSearchAsync()
    {
        var query = SearchQuery.Trim();
        var seq = ++_searchSeq;
        if (query.Length == 0)
        {
            SearchRows = [];
            IsSearching = false;
            return;
        }
        IsSearching = true;
        try
        {
            var rows = await Store.SearchAsync(query);
            if (seq != _searchSeq) return; // a newer query superseded this one
            SearchRows = rows;
            if (_searchIndex >= rows.Count) SearchIndex = Math.Max(0, rows.Count - 1);
        }
        finally
        {
            if (seq == _searchSeq) IsSearching = false;
        }
    }

    // --- add workspace form -------------------------------------------------------------------------------

    public bool IsAddOpen
    {
        get => _addOpen;
        set
        {
            if (!SetProperty(ref _addOpen, value)) return;
            if (!value) AddError = null;
        }
    }

    public string AddPath
    {
        get => _addPath;
        set
        {
            if (!SetProperty(ref _addPath, value)) return;
            AddError = null;
        }
    }

    public string? AddError
    {
        get => _addError;
        private set => SetProperty(ref _addError, value);
    }

    /// <summary>The web's <c>isValidPath</c>: a Windows drive or a POSIX root.</summary>
    public static bool IsValidPath(string path) =>
        System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") || path.StartsWith('/') || path.StartsWith('\\');

    public async Task<bool> SubmitAddAsync()
    {
        var value = AddPath.Trim();
        if (value.Length == 0 || !IsValidPath(value))
        {
            AddError = "Enter an absolute folder path.";
            return false;
        }
        var error = await Store.AddWorkspaceAsync(value);
        if (error is not null)
        {
            AddError = error;
            return false;
        }
        AddPath = "";
        IsAddOpen = false;
        return true;
    }

    public void OpenAdd()
    {
        IsAddOpen = true;
        AddError = null;
    }

    // --- inline rename ------------------------------------------------------------------------------------

    public string? EditingSessionId
    {
        get => _editingSessionId;
        private set => SetProperty(ref _editingSessionId, value);
    }

    public string EditText
    {
        get => _editText;
        set
        {
            if (!SetProperty(ref _editText, value)) return;
            if (EditingSessionId is null) return;
            _pendingRenameSessionId = EditingSessionId;
            _renameTimer.Stop();
            _renameTimer.Start();
        }
    }

    public void BeginRename(ConversationRowViewModel row)
    {
        EditingSessionId = row.Id;
        _editText = row.Title;
        OnPropertyChanged(nameof(EditText));
    }

    public void CancelRename()
    {
        _renameTimer.Stop();
        _pendingRenameSessionId = null;
        EditingSessionId = null;
    }

    public void CommitRename()
    {
        _renameTimer.Stop();
        var id = EditingSessionId;
        _pendingRenameSessionId = null;
        EditingSessionId = null;
        if (id is not null) _ = Store.RenameSessionAsync(id, EditText);
    }

    // --- archive folder and delete confirmation -----------------------------------------------------------

    /// <summary>Workspace whose "Archived" sub-folder is open (UI-only state, like the web's `archiveOpen`).</summary>
    public string? OpenArchiveId
    {
        get => _openArchiveId;
        private set => SetProperty(ref _openArchiveId, value);
    }

    public void ToggleArchive(string workspaceId)
    {
        OpenArchiveId = _openArchiveId == workspaceId ? null : workspaceId;
    }

    public ConversationRowViewModel? PendingDelete
    {
        get => _pendingDelete;
        private set => SetProperty(ref _pendingDelete, value);
    }

    public WorkspaceRowViewModel? PendingDeleteWorkspace
    {
        get => _pendingDeleteWorkspace;
        private set => SetProperty(ref _pendingDeleteWorkspace, value);
    }

    public bool Deleting
    {
        get => _deleting;
        private set => SetProperty(ref _deleting, value);
    }

    public bool IsDeleteOpen => PendingDelete is not null;

    public void AskDelete(ConversationRowViewModel row)
    {
        PendingDelete = row;
        PendingDeleteWorkspace = Store.Workspaces.FirstOrDefault(w => w.Tree.Any(r => r.Id == row.Id) || w.Archived.Any(r => r.Id == row.Id));
        OnPropertyChanged(nameof(IsDeleteOpen));
    }

    public void CloseDelete()
    {
        if (Deleting) return;
        PendingDelete = null;
        OnPropertyChanged(nameof(IsDeleteOpen));
    }

    public async Task ConfirmDeleteAsync()
    {
        var target = PendingDelete;
        if (target is null || Deleting) return;
        Deleting = true;
        var error = await Store.DeleteSessionAsync(target.Id);
        Deleting = false;
        if (error is null) Services.SoundService.Current.Play(Services.SoundKey.Select);
        PendingDelete = null;
        OnPropertyChanged(nameof(IsDeleteOpen));
    }

    // --- workspace settings menu --------------------------------------------------------------------------

    public string? SettingsWorkspaceId
    {
        get => _settingsWorkspaceId;
        set
        {
            if (SetProperty(ref _settingsWorkspaceId, value)) OnPropertyChanged(nameof(SettingsWorkspace));
        }
    }

    public WorkspaceRowViewModel? SettingsWorkspace =>
        _settingsWorkspaceId is null ? null : Store.Workspaces.FirstOrDefault(w => w.Id == _settingsWorkspaceId);

    /// <summary>Name typed into the settings menu; persisted with the web's 400ms debounce.</summary>
    public string WorkspaceNameDraft { get; private set; } = "";

    public void BeginWorkspaceName(string workspaceId)
    {
        _pendingWorkspaceNameId = workspaceId;
        WorkspaceNameDraft = Store.Workspaces.FirstOrDefault(w => w.Id == workspaceId)?.Name ?? "";
    }

    public void SetWorkspaceName(string workspaceId, string name)
    {
        WorkspaceNameDraft = name;
        Store.PreviewWorkspaceName(workspaceId, name);
        _pendingWorkspaceNameId = workspaceId;
        _workspaceNameTimer.Stop();
        _workspaceNameTimer.Start();
    }

    public void CommitWorkspaceName()
    {
        _workspaceNameTimer.Stop();
        if (_pendingWorkspaceNameId is { } id) _ = Store.SetWorkspaceNameAsync(id, WorkspaceNameDraft, debounced: false);
    }

    // --- sections -----------------------------------------------------------------------------------------

    public void RefreshSections()
    {
        FolderWorkspaces.Clear();
        ChatGroups.Clear();
        foreach (var workspace in Store.Workspaces)
        {
            if (workspace.IsChat) ChatGroups.Add(workspace);
            else FolderWorkspaces.Add(workspace);
        }
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(SettingsWorkspace));
    }
}
