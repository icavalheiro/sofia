using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.App.Theme;

namespace Sofia.App.Features.Sidebar;

/// <summary>
/// The sidebar (web <c>AppSidebar.vue</c>): brand header, New chat / Workspace actions, the Ctrl+K quick prompt, the
/// workspace tree with conversation rows, status badges, hover actions, inline rename, archive folder, delete
/// confirmation, drag reorder, the workspace settings menu (icon, model, name, worktrees, remove) and the shortcuts the
/// sidebar owns. State comes from <see cref="SidebarViewModel"/>/<see cref="SidebarStore"/>; the geometry is the web's.
/// </summary>
public partial class SidebarView : UserControl
{
    private SidebarViewModel? _vm;
    private Panel? _overlayHost;
    private Border? _settingsMenu;
    private string? _settingsWorkspaceId;
    private Panel? _modalHost;

    // Drag-to-reorder state, shared by workspace and conversation rows (web `dragging`/`dropTarget`).
    private Control? _pressRow;
    private string? _pressKind;
    private string? _pressId;
    private Point _pressPoint;
    private bool _dragging;
    private string? _dragKind;
    private string? _dragId;
    private Border? _dropRow;
    private string? _dropKind;
    private string? _dropId;
    private bool _dropAfter;
    private readonly Dictionary<string, RowReveal> _reveals = new(StringComparer.Ordinal);
    private Action<SessionOpenRequest>? _storeOpenHandler;

    public SidebarView()
    {
        InitializeComponent();
        LoadArt();
        WireChrome();
        Root.SizeChanged += (_, _) =>
        {
            PositionSearchPop();
            PositionSettingsMenu();
        };
        Root.AddHandler(PointerPressedEvent, OnRootPointerPressed, RoutingStrategies.Tunnel);
        Root.AddHandler(PointerReleasedEvent, OnRootPointerReleased, RoutingStrategies.Tunnel);
        Root.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The tree host, exposed for the headless tests and the screenshot harness.</summary>
    public StackPanel TreeHost => Tree;

    public Border SearchPanel => SearchPop;

    public TextBox SearchFieldBox => SearchInput;

    /// <summary>True while the workspace settings menu is rendered (tests).</summary>
    public bool IsSettingsMenuOpen => _settingsMenu is not null;

    /// <summary>
    /// Starts a conversation row's hover reveal on the shared clock (motion #3): the frame harness calls this before
    /// seeking 0/75/150ms so the pill fade is captured stop-motion instead of on the wall clock.
    /// </summary>
    public bool HoverConversation(string sessionId)
    {
        if (!_reveals.TryGetValue(sessionId, out var reveal)) return false;
        reveal.Begin(true, MotionService.Current.NowMs);
        return true;
    }

    /// <summary>Current pill opacity of a conversation row's hover reveal (frame assertions).</summary>
    public double RevealOpacity(string sessionId) =>
        _reveals.TryGetValue(sessionId, out var reveal) ? reveal.ActionsOpacity : 0;

    /// <summary>Current count opacity of a workspace row's hover reveal (frame assertions).</summary>
    public double WorkspaceCountOpacity(string workspaceId) =>
        _reveals.TryGetValue(workspaceId, out var reveal) ? reveal.CountOpacity : 0;

    /// <summary>Opens a workspace's settings menu for the screenshot gate (the shell has no headless pointer).</summary>
    public void PreviewSettingsMenu(string workspaceId) => OpenSettingsMenu(workspaceId, SettingsGear);

    /// <summary>Opens the delete confirmation for the first conversation of a workspace (screenshot evidence).</summary>
    public bool PreviewDelete(string workspaceId)
    {
        if (_vm is null) return false;
        var workspace = _vm.Store.Workspaces.FirstOrDefault(w => w.Id == workspaceId) ?? _vm.Store.Workspaces.FirstOrDefault();
        var row = workspace?.Tree.FirstOrDefault();
        if (row is null) return false;
        _vm.AskDelete(row);
        RebuildModal();
        return true;
    }

    /// <summary>Motion participant rows of the last rebuild (the frame harness checks the sweeps).</summary>
    public List<WorkingRowBorder> WorkingRows { get; } = [];

    /// <summary>Raised when the sidebar needs the composer focused (New chat; the composer itself is 041).</summary>
    public event Action? ComposerFocusRequested;

    /// <summary>Attaches the view model; called by the shell after fixture/live data is ready.</summary>
    public void Attach(SidebarViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.Store.Changed -= OnStoreChanged;
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.SelectionChanged -= OnSearchSelectionChanged;
            if (_storeOpenHandler is not null) _vm.Store.OpenSessionRequested -= _storeOpenHandler;
        }
        _vm = viewModel;
        DataContext = viewModel;
        viewModel.Store.Changed += OnStoreChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.SelectionChanged += OnSearchSelectionChanged;
        _storeOpenHandler = request => SessionOpened?.Invoke(request);
        viewModel.Store.OpenSessionRequested += _storeOpenHandler;
        RegisterShortcuts();
        Rebuild();
    }

    /// <summary>Forwarded selection for the shell/transcript (the scroll target seam is owned by 041).</summary>
    public event Action<SessionOpenRequest>? SessionOpened;

    /// <summary>Hosts the delete confirmation on the shell's overlay ladder (z2100) instead of inside the column.</summary>
    public void UseOverlayHost(Panel host)
    {
        _overlayHost = host;
    }

    /// <summary>Re-applies width/padding after a theme change and rebuilds the rows.</summary>
    public void RefreshAppearance()
    {
        ApplyAppearance();
        Rebuild();
    }

    /// <summary>Escape from anywhere: close the delete confirmation, the quick prompt or the settings menu.</summary>
    public bool HandleEscape()
    {
        if (_vm?.IsDeleteOpen == true)
        {
            _vm.CloseDelete();
            RebuildModal();
            return true;
        }
        if (_vm?.IsSearchOpen == true)
        {
            _vm.CloseSearch();
            Rebuild();
            return true;
        }
        if (_settingsMenu is not null)
        {
            CloseSettingsMenu();
            return true;
        }
        return false;
    }

    /// <summary>Closes the search pop and the settings menu when the user clicks outside the sidebar.</summary>
    public void CloseTransient()
    {
        var changed = false;
        if (_vm?.IsSearchOpen == true)
        {
            _vm.CloseSearch();
            changed = true;
        }
        if (_settingsMenu is not null)
        {
            CloseSettingsMenu();
            changed = true;
        }
        if (changed) Rebuild();
    }

    // --- chrome (buttons that never change) ---------------------------------------------------------------

    private void WireChrome()
    {
        SettingsGear.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            ShellSettingsRequested?.Invoke();
        };
        NewChatButton.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            if (_vm is null) return;
            _ = _vm.Store.NewConversationAsync();
            ComposerFocusRequested?.Invoke();
        };
        NewWorkspaceButton.Click += async (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            if (_vm is null) return;
            _vm.IsAddOpen = !_vm.IsAddOpen;
            Rebuild();
            if (_vm.IsAddOpen) await PickFolderAsync();
        };
        AddWorkspaceConfirm.Click += async (_, _) => await SubmitAddAsync();
        AddPathBox.TextChanged += (_, _) =>
        {
            if (_vm is null) return;
            _vm.AddPath = AddPathBox.Text ?? "";
        };
        AddPathBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SubmitAddAsync();
        };
        SearchToggle.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            if (_vm is null) return;
            _vm.ToggleSearch();
            if (_vm.IsSearchOpen) Dispatcher.UIThread.Post(() => SearchInput.Focus());
        };
        SearchInput.TextChanged += (_, _) =>
        {
            if (_vm is null) return;
            if (_vm.SearchQuery != SearchInput.Text) _vm.SearchQuery = SearchInput.Text ?? "";
        };
        SearchInput.KeyDown += async (_, e) =>
        {
            if (_vm is null) return;
            switch (e.Key)
            {
                case Key.Down:
                    e.Handled = true;
                    _vm.MoveSearch(1);
                    break;
                case Key.Up:
                    e.Handled = true;
                    _vm.MoveSearch(-1);
                    break;
                case Key.Enter:
                    e.Handled = true;
                    await _vm.OpenSearchHitAsync();
                    break;
                case Key.Escape:
                    e.Handled = true;
                    _vm.CloseSearch();
                    break;
            }
        };
        SearchClear.Click += (_, _) =>
        {
            if (_vm is null) return;
            _vm.SearchQuery = "";
            SearchInput.Text = "";
            SearchInput.Focus();
        };
    }

    /// <summary>The gear opens the settings dialog, which belongs to 043; the sidebar only reports the gesture.</summary>
    public event Action? ShellSettingsRequested;

    private void LoadArt()
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Sofia.App/Assets/Logos/logo-42.png"));
        BrandLogoImage.Source = new Avalonia.Media.Imaging.Bitmap(stream);
    }

    private async Task PickFolderAsync()
    {
        if (_vm is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { CanPickFolder: true } storage) return; // browser head: typed path
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a workspace folder",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        _vm.AddPath = path;
        AddPathBox.Text = path;
        await SubmitAddAsync();
    }

    private async Task SubmitAddAsync()
    {
        if (_vm is null) return;
        if (await _vm.SubmitAddAsync())
        {
            AddPathBox.Text = "";
            SoundService.Current.Play(SoundKey.Select);
        }
        Rebuild();
    }

    // --- shortcuts ----------------------------------------------------------------------------------------

    private void RegisterShortcuts()
    {
        if (_vm is null) return;
        var shortcuts = ShortcutService.Current;
        shortcuts.Register("search", () =>
        {
            _vm.ToggleSearch();
            if (_vm.IsSearchOpen) Dispatcher.UIThread.Post(() => SearchInput.Focus());
            Rebuild();
        });
        shortcuts.Register("workspace.add", () =>
        {
            _vm.OpenAdd();
            Rebuild();
            AddPathBox.Focus();
            _ = PickFolderAsync();
        });
        shortcuts.Register("session.new", async () =>
        {
            await _vm.Store.NewConversationAsync();
            ComposerFocusRequested?.Invoke();
        });
        shortcuts.Register("session.archive", async () =>
        {
            var active = _vm.Store.ActiveSession;
            if (active is not null && !active.Archived) await _vm.Store.ArchiveSessionAsync(active.Id);
        });
        shortcuts.Register("session.prev", () => _vm.Store.StepSession(-1));
        shortcuts.Register("session.next", () => _vm.Store.StepSession(1));
        shortcuts.Register("settings.open", () => ShellSettingsRequested?.Invoke());
        shortcuts.Register("panel.toggle", () => PanelToggleRequested?.Invoke());
    }

    /// <summary>Ctrl+\ — owned by the shell's right panel; the sidebar routes the shared binding to it.</summary>
    public event Action? PanelToggleRequested;

    // --- rebuilding ---------------------------------------------------------------------------------------

    private void OnStoreChanged() => Rebuild();

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SidebarViewModel.SearchQuery):
            case nameof(SidebarViewModel.SearchRows):
            case nameof(SidebarViewModel.IsSearching):
                BuildSearch();
                break;
            case nameof(SidebarViewModel.SearchIndex):
            case nameof(SidebarViewModel.EditText):
                break;
            case nameof(SidebarViewModel.PendingDelete):
            case nameof(SidebarViewModel.Deleting):
                RebuildModal();
                break;
            default:
                Rebuild();
                break;
        }
    }

    private void OnSearchSelectionChanged()
    {
        HighlightSearchRow();
    }

    private void Rebuild()
    {
        if (_vm is null) return;
        ApplyLocalization();
        ApplyAppearance();
        AddForm.IsVisible = _vm.IsAddOpen;
        AddPathBox.Text = _vm.AddPath;
        AddErrorText.IsVisible = _vm.AddError is not null;
        AddErrorText.Text = _vm.AddError ?? "";
        BuildTree();
        BuildSearch();
        RebuildModal();
    }

    private void ApplyLocalization()
    {
        NewChatLabel.Text = Localization.T("shell.newChat");
        NewWorkspaceLabel.Text = Localization.T("shell.newWorkspace");
        SearchLabel.Text = Localization.T("shell.search");
        SearchInput.PlaceholderText = Localization.T("shell.searchPlaceholder");
        SearchHint.Text = ShortcutService.BindingOf("search")?.Combo ?? "Ctrl+K";
        AddPathBox.PlaceholderText = "Folder path";
    }

    private void ApplyAppearance()
    {
        var theme = ThemeService.Current;
        SidebarSurface.Width = theme.SidebarWidth;
        BrandRow.Margin = theme.CompactSidebar
            ? new Thickness(10.4, 12, 10.4, 10.4)
            : new Thickness(16, 16, 16, 14);
        BrandRow.Height = theme.CompactSidebar ? 62 : 70;
    }

    // --- tree ---------------------------------------------------------------------------------------------

    private void BuildTree()
    {
        Tree.Children.Clear();
        _reveals.Clear();
        WorkingRows.Clear();
        if (_vm is null) return;

        AddSectionLabel(Localization.T("shell.workspaces"), split: false);
        foreach (var workspace in _vm.FolderWorkspaces) AddWorkspaceBlock(workspace);

        AddSectionLabel(Localization.T("shell.chats"), split: true, showAdd: true);
        foreach (var workspace in _vm.ChatGroups) AddWorkspaceBlock(workspace);
        if (_vm.ChatGroups.Count == 0) Tree.Children.Add(EmptyNote("No chat groups yet."));
    }

    private void AddSectionLabel(string text, bool split, bool showAdd = false)
    {
        var label = new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            LetterSpacing = 0.88,
            Foreground = Brush("BrushTextFaint"),
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (!showAdd && !split)
        {
            Tree.Children.Add(new Border { Padding = new Thickness(8, 8, 8, 6), Child = label });
            return;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(label);
        if (showAdd)
        {
            var add = IconAction("plus", 13.6, "New chat group", () =>
            {
                SoundService.Current.Play(SoundKey.Click);
                if (_vm is not null) _ = _vm.Store.AddChatGroupAsync();
            }, size: 20, radius: 6);
            Grid.SetColumn(add, 1);
            grid.Children.Add(add);
        }
        var border = new Border { Child = grid, Padding = new Thickness(8, 12, 8, 6), Margin = new Thickness(0, 12, 0, 0) };
        if (split) border.BorderBrush = Brush("BrushBorder");
        if (split) border.BorderThickness = new Thickness(0, 1, 0, 0);
        Tree.Children.Add(border);
    }

    private void AddWorkspaceBlock(WorkspaceRowViewModel workspace)
    {
        Tree.Children.Add(BuildWorkspaceRow(workspace));
        if (!workspace.Expanded) return;

        var conversations = new StackPanel { Margin = new Thickness(22, 2, 0, 4), Spacing = 0 };
        conversations.Children.Add(new Border
        {
            BorderBrush = Brush("BrushBorder"),
            BorderThickness = new Thickness(1, 0, 0, 0),
            Padding = new Thickness(8, 0, 0, 0),
            Child = BuildConversationList(workspace),
        });
        Tree.Children.Add(conversations);
    }

    private Control BuildConversationList(WorkspaceRowViewModel workspace)
    {
        var list = new StackPanel { Spacing = 0 };
        foreach (var row in workspace.Tree) list.Children.Add(BuildConversationRow(workspace, row));
        if (workspace.Count == 0) list.Children.Add(EmptyNote("No conversations yet."));

        if (workspace.HasArchived)
        {
            list.Children.Add(BuildArchiveFolder(workspace));
            if (_vm?.OpenArchiveId == workspace.Id)
            {
                var archived = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
                archived.Children.Add(new Border
                {
                    BorderBrush = Brush("BrushBorder"),
                    BorderThickness = new Thickness(1, 0, 0, 0),
                    Padding = new Thickness(4, 0, 0, 0),
                    Child = BuildArchivedRows(workspace),
                });
                list.Children.Add(archived);
            }
        }
        return list;
    }

    private Control BuildArchivedRows(WorkspaceRowViewModel workspace)
    {
        var rows = new StackPanel();
        foreach (var row in workspace.Archived) rows.Children.Add(BuildConversationRow(workspace, row, archived: true));
        return rows;
    }

    private Control BuildArchiveFolder(WorkspaceRowViewModel workspace)
    {
        var open = _vm?.OpenArchiveId == workspace.Id;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var caret = new SofiaIcon
        {
            Glyph = open ? "chevronDown" : "chevronRight",
            Size = 12,
            Foreground = Brush("BrushTextFaint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6.4, 0),
        };
        grid.Children.Add(caret);
        var icon = new SofiaIcon
        {
            Glyph = "archive",
            Size = 14,
            Foreground = Brush("BrushTextFaint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6.4, 0),
        };
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        var title = Text("Archived", 13, "BrushTextMuted");
        title.Classes.Add("truncate");
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 2);
        grid.Children.Add(title);
        var count = CountPill(workspace.Archived.Count);
        Grid.SetColumn(count, 3);
        count.VerticalAlignment = VerticalAlignment.Center;
        count.Margin = new Thickness(6.4, 0, 0, 0);
        grid.Children.Add(count);

        var rowBorder = new Border
        {
            Classes = { "sidebar-row", "conv-folder" },
            Padding = new Thickness(8, 5.6, 8, 5.6),
            Child = grid,
            Focusable = true,
        };
        rowBorder.PointerReleased += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            _vm?.ToggleArchive(workspace.Id);
            Rebuild();
        };
        rowBorder.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _vm?.ToggleArchive(workspace.Id);
            Rebuild();
        };
        return rowBorder;
    }

    private Control BuildWorkspaceRow(WorkspaceRowViewModel workspace)
    {
        var working = workspace.IsWorking;
        var ring = new WorkingRowBorder
        {
            Kind = WorkingRowKind.Workspace,
            IsWorking = working,
            RingThickness = working ? 1.5 : 0,
            RowSurface = Brush("BrushSurface"),
            RowBackground = working ? Brush("BrushPrimarySoft") : Brushes.Transparent,
            RowCornerRadius = new CornerRadius(8),
        };
        if (working) WorkingRows.Add(ring);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto") };
        var caret = new SofiaIcon
        {
            Glyph = workspace.Expanded ? "chevronDown" : "chevronRight",
            Size = 13,
            Foreground = Brush("BrushTextFaint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        grid.Children.Add(caret);

        Control slot;
        if (workspace.HasStatusBadge)
        {
            slot = StatusBadge(workspace.Status, workspace.StatusTitle, workspaceSize: true);
        }
        else
        {
            slot = new SofiaIcon
            {
                Glyph = workspace.Icon,
                Size = 15,
                Foreground = Brush("BrushPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        slot.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(slot, 1);
        grid.Children.Add(slot);

        var name = Text(workspace.Name, 14, "BrushTextMuted", FontWeight.Medium);
        name.Classes.Add("truncate");
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);

        var trail = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6.4, 0, 0, 0) };
        var count = CountPill(workspace.Count);
        count.Classes.Add("ws-count");
        count.HorizontalAlignment = HorizontalAlignment.Right;
        count.Opacity = 1;
        trail.Children.Add(count);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Classes = { "ws-actions" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var add = IconAction("plus", 14, "New chat in this workspace", () =>
        {
            SoundService.Current.Play(SoundKey.Click);
            _ = _vm!.Store.NewConversationInAsync(workspace.Id);
        }, size: 22, radius: 6);
        var gear = IconAction("gear", 14.4, "Workspace settings", () =>
        {
            SoundService.Current.Play(SoundKey.Click);
            OpenSettingsMenu(workspace.Id, actions);
        }, size: 22, radius: 6);
        actions.Children.Add(add);
        actions.Children.Add(gear);
        actions.Opacity = 0;
        actions.IsHitTestVisible = false;
        trail.Children.Add(actions);
        Grid.SetColumn(trail, 3);
        grid.Children.Add(trail);

        var reveal = new RowReveal(trail: null, actions: actions, count: count, pin: null);
        _reveals[workspace.Id] = reveal;

        var row = new Border
        {
            Classes = { "sidebar-row" },
            Padding = new Thickness(8, LookTheme(6.4, 3.2) - (working ? 1.5 : 0), 8, LookTheme(6.4, 3.2) - (working ? 1.5 : 0)),
            Child = grid,
            Focusable = true,
            Tag = (Action)ToggleWorkspace,
        };
        if (workspace.Status != SidebarStatus.Idle && !working) row.Classes.Add("status-row");
        row.PointerPressed += (_, e) => BeginPress("workspace", workspace.Id, row, e);
        row.PointerMoved += (_, e) => TrackDrag("workspace", workspace.Id, row, e, true);
        row.PointerEntered += (_, _) => reveal.Begin(true, MotionService.Current.NowMs);
        row.PointerExited += (_, _) => reveal.Begin(false, MotionService.Current.NowMs);
        row.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            ToggleWorkspace();
        };
        ring.Content = row;

        void ToggleWorkspace()
        {
            if (_vm is null) return;
            _ = _vm.Store.ToggleWorkspaceAsync(workspace.Id);
        }

        return ring;
    }

    private Control BuildConversationRow(WorkspaceRowViewModel workspace, ConversationRowViewModel row, bool archived = false)
    {
        var working = row.Status == "working" && !archived;
        var ring = new WorkingRowBorder
        {
            Kind = WorkingRowKind.Conversation,
            IsWorking = working,
            RingThickness = working ? 1.5 : 0,
            RowSurface = Brush("BrushSurface"),
            RowBackground = row.IsActive && !working ? Brush("BrushPrimarySoft") : Brushes.Transparent,
            RowCornerRadius = new CornerRadius(8),
        };
        if (working) WorkingRows.Add(ring);

        var layer = new Grid();
        var main = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Control slot;
        if (row.HasStatusBadge)
        {
            slot = StatusBadge(row.Status, WorkspaceRowViewModel.SidebarStatusText(row.Status), workspaceSize: false);
        }
        else
        {
            slot = new SofiaIcon
            {
                Glyph = row.IsSubagent ? "bot" : "chat",
                Size = 14,
                Foreground = Brush(row.IsSubagent ? "BrushPrimary" : "BrushTextFaint"),
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        slot.Margin = new Thickness(0, 0, 8, 0);
        slot.Opacity = row.IsSubagent ? 0.9 : 0.75;
        main.Children.Add(slot);

        var editing = _vm?.EditingSessionId == row.Id;
        if (editing)
        {
            var edit = new TextBox
            {
                Text = _vm!.EditText,
                FontSize = 13,
                Background = Brushes.Transparent,
                BorderBrush = Brush("BrushPrimary"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 0),
                MinHeight = 0,
                Foreground = Brush("BrushText"),
                CaretBrush = Brush("BrushPrimary"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            edit.TextChanged += (_, _) => _vm!.EditText = edit.Text ?? "";
            edit.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    _vm!.CommitRename();
                }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _vm!.CancelRename();
                }
            };
            edit.LostFocus += (_, _) => _vm!.CommitRename();
            Grid.SetColumn(edit, 1);
            main.Children.Add(edit);
            Dispatcher.UIThread.Post(() =>
            {
                edit.Focus();
                edit.SelectAll();
            });
        }
        else
        {
            var title = Text(row.Title, 13, row.IsActive ? "BrushText" : "BrushTextMuted");
            title.Classes.Add("truncate");
            title.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(title, 1);
            main.Children.Add(title);
        }
        layer.Children.Add(main);

        var trail = new Grid
        {
            Classes = { "conv-trail" },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 3, 0),
            Opacity = 0,
            IsHitTestVisible = false,
        };
        SofiaIcon? pin = null;
        if (row.Pinned)
        {
            pin = new SofiaIcon
            {
                Glyph = "pin",
                Size = 12,
                Foreground = Brush("BrushPrimary"),
                Opacity = 0.8,
                Classes = { "conv-pin" },
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            trail.Children.Add(pin);
        }
        var pill = new Border
        {
            Classes = { "conv-actions" },
            Background = Brush("BrushSurface3"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(1.6, 1.6, 1.6, 1.6),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #40000000"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1.6 };
        if (!row.IsSubagent && !archived)
        {
            buttons.Children.Add(IconAction(row.Pinned ? "unpin" : "pin", 12.8, row.Pinned ? "Unpin" : "Pin", () =>
            {
                SoundService.Current.Play(SoundKey.Select);
                _ = _vm!.Store.PinSessionAsync(row.Id, !row.Pinned);
            }, size: 20, radius: 999));
        }
        if (!archived)
        {
            buttons.Children.Add(IconAction("archive", 12.8, "Archive", () =>
            {
                SoundService.Current.Play(SoundKey.Select);
                _ = _vm!.Store.ArchiveSessionAsync(row.Id);
            }, size: 20, radius: 999));
        }
        else
        {
            buttons.Children.Add(IconAction("restore", 12.8, "Restore", () =>
            {
                SoundService.Current.Play(SoundKey.Select);
                _ = _vm!.Store.RestoreSessionAsync(row.Id);
            }, size: 20, radius: 999));
        }
        buttons.Children.Add(IconAction("trash", 12.8, "Delete", () =>
        {
            SoundService.Current.Play(SoundKey.Click);
            _vm!.AskDelete(row);
            RebuildModal();
        }, size: 20, radius: 999, danger: true));
        pill.Child = buttons;
        pill.Opacity = 0;
        pill.IsHitTestVisible = false;
        trail.Children.Add(pill);
        layer.Children.Add(trail);
        var reveal = new RowReveal(trail, pill, null, pin);
        _reveals[row.Id] = reveal;

        var border = new Border
        {
            Classes = { "sidebar-row" },
            Padding = new Thickness(8, LookTheme(5.6, 3.2) - (working ? 1.5 : 0), 8, LookTheme(5.6, 3.2) - (working ? 1.5 : 0)),
            Child = layer,
            Focusable = true,
            Tag = (Action)OpenOrRename,
        };
        if (row.IsActive) border.Classes.Add("active");
        if (archived) border.Classes.Add("archived");
        if (row.IsSubagent) border.Classes.Add("sub");
        border.Margin = new Thickness(row.IndentPx, 0, 0, 0);
        if (!editing)
        {
            border.PointerPressed += (_, e) => BeginPress("conversation", row.Id, border, e);
            border.PointerMoved += (_, e) => TrackDrag("conversation", row.Id, border, e, row.CanDrag && !archived);
            border.PointerEntered += (_, _) => reveal.Begin(true, MotionService.Current.NowMs);
            border.PointerExited += (_, _) => reveal.Begin(false, MotionService.Current.NowMs);
        }
        border.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            OpenOrRename();
        };
        ring.Content = border;

        void OpenOrRename()
        {
            if (_vm is null) return;
            if (archived)
            {
                _ = _vm.Store.OpenSessionAsync(workspace.Id, row.Id);
                return;
            }
            if (row.Id == _vm.Store.ActiveSessionId)
            {
                SoundService.Current.Play(SoundKey.Click);
                _vm.BeginRename(row);
                Rebuild();
                return;
            }
            _ = _vm.Store.OpenSessionAsync(workspace.Id, row.Id);
        }

        return ring;
    }

    private Control StatusBadge(string status, string title, bool workspaceSize)
    {
        var (fill, ink) = status switch
        {
            SidebarStatus.Done => ("BrushOkSoft", "BrushOk"),
            "permission" => ("BrushWarnSoft", "BrushWarn"),
            "question" => ("BrushPrimarySoft", "BrushPrimary"),
            "working" or "waiting" => ("BrushPrimarySoft", "BrushPrimary"),
            "error" => ("BrushErrSoft", "BrushErr"),
            _ => ("BrushSurface3", "BrushTextFaint"),
        };
        var size = workspaceSize ? 18.0 : 16.0;
        Control glyph = status is "working" or "waiting"
            ? new SofiaIcon
            {
                Glyph = "loader",
                Size = size * 0.72,
                Paused = status == "waiting",
                Foreground = Brush(ink),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
            : new SofiaIcon
            {
                Glyph = status switch
                {
                    SidebarStatus.Done => "check",
                    "permission" => "alert",
                    "question" => "help",
                    "error" => "x",
                    _ => "check",
                },
                Size = size * 0.72,
                Foreground = Brush(ink),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        var badge = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(999),
            Background = Brush(fill),
            Child = glyph,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(badge, title);
        return badge;
    }

    private static Border EmptyNote(string text) => new()
    {
        Padding = new Thickness(8, 5.6, 8, 5.6),
        Child = Text(text, 12, "BrushTextFaint"),
    };

    private static Border CountPill(int count)
    {
        var label = Text(count.ToString(), 11, "BrushTextFaint");
        label.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Background = Brush("BrushSurface3"),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(6.4, 0.8, 6.4, 0.8),
            Child = label,
        };
    }

    private Button IconAction(string glyph, double iconSize, string tip, Action onClick, double size = 28,
        double radius = 7, bool danger = false)
    {
        var button = new Button
        {
            Theme = ThemeFor("IconButton"),
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Padding = new Thickness(0),
            Content = new SofiaIcon { Glyph = glyph, Size = iconSize, Foreground = Brush(danger ? "BrushErr" : "BrushTextMuted") },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    // --- search -------------------------------------------------------------------------------------------

    private void BuildSearch()
    {
        if (_vm is null) return;
        var open = _vm.IsSearchOpen;
        SearchPop.IsVisible = open;
        OverlayLayer.IsHitTestVisible = open || _settingsMenu is not null;
        if (!open)
        {
            SearchResults.Children.Clear();
            return;
        }
        PositionSearchPop();
        SearchClear.IsVisible = _vm.SearchQuery.Length > 0;
        if (SearchInput.Text != _vm.SearchQuery) SearchInput.Text = _vm.SearchQuery;
        RefreshSearchResults();
    }

    private void PositionSearchPop()
    {
        if (!SearchPop.IsVisible) return;
        var width = Math.Max(200, Root.Bounds.Width - 24);
        SearchPop.Width = width;
        var point = SearchField.TranslatePoint(new Point(0, SearchField.Bounds.Height + 4), OverlayCanvas);
        Canvas.SetLeft(SearchPop, point?.X ?? 12);
        Canvas.SetTop(SearchPop, point?.Y ?? 120);
        var height = TopLevel.GetTopLevel(this)?.Bounds.Height ?? 800;
        SearchScroll.MaxHeight = Math.Min(420, height * 0.5);
    }

    private void RefreshSearchResults()
    {
        if (_vm is null) return;
        SearchResults.Children.Clear();
        SearchClear.IsVisible = _vm.SearchQuery.Length > 0;
        if (_vm.IsSearching && _vm.SearchRows.Count == 0)
        {
            SearchResults.Children.Add(SearchNote("Searching…"));
            return;
        }
        if (_vm.SearchQuery.Trim().Length > 0 && _vm.SearchRows.Count == 0)
        {
            SearchResults.Children.Add(SearchNote("No matches."));
            return;
        }

        for (var i = 0; i < _vm.SearchRows.Count; i++)
        {
            var index = i;
            var hit = _vm.SearchRows[i];
            var title = Text(hit.Title, 13, "BrushText", FontWeight.Medium);
            title.Classes.Add("truncate");
            var snippet = SnippetBlock(hit);
            var content = new StackPanel { Spacing = 2.4 };
            content.Children.Add(title);
            content.Children.Add(snippet);
            var row = new Border
            {
                Classes = { "search-hit" },
                Padding = new Thickness(8, 7.2, 8, 7.2),
                CornerRadius = new CornerRadius(8),
                Child = content,
                Background = index == _vm.SearchIndex ? Brush("BrushSurface3") : Brushes.Transparent,
                Focusable = true,
            };
            row.PointerPressed += (_, _) =>
            {
                if (_vm is null) return;
                _vm.SearchIndex = index;
                _ = _vm.OpenSearchHitAsync();
            };
            row.PointerEntered += (_, _) =>
            {
                if (_vm is not null) _vm.SearchIndex = index;
            };
            SearchResults.Children.Add(row);
        }
    }

    private void HighlightSearchRow()
    {
        if (_vm is null) return;
        for (var i = 0; i < SearchResults.Children.Count; i++)
        {
            if (SearchResults.Children[i] is not Border row) continue;
            row.Background = i == _vm.SearchIndex ? Brush("BrushSurface3") : Brushes.Transparent;
            if (i == _vm.SearchIndex) row.BringIntoView();
        }
    }

    private static TextBlock SearchNote(string text)
    {
        var note = Text(text, 13, "BrushTextFaint");
        note.Margin = new Thickness(8, 9.6, 8, 9.6);
        return note;
    }

    /// <summary>
    /// Renders a snippet with its <c>[start, len]</c> highlight ranges. Avalonia's <c>TextElement</c> has a Background,
    /// so the marks are ordinary <c>Run</c>s (the web's <c>&lt;mark&gt;</c>) and a highlight inside a word wraps
    /// exactly like the web's span; the block clamps to the web's two lines.
    /// </summary>
    private static TextBlock SnippetBlock(SearchHitRow hit)
    {
        var block = new TextBlock
        {
            FontSize = 12,
            LineHeight = 18.6,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 38,
            ClipToBounds = true,
            Margin = new Thickness(0, 2.4, 0, 0),
        };
        foreach (var (text, mark) in hit.Parts())
        {
            block.Inlines!.Add(new Run
            {
                Text = text,
                Foreground = Brush(mark ? "BrushText" : "BrushTextMuted"),
                Background = mark ? Brush("BrushPrimarySoft") : null,
            });
        }
        return block;
    }

    // --- workspace settings menu --------------------------------------------------------------------------

    private void OpenSettingsMenu(string workspaceId, Control anchor)
    {
        if (_settingsMenu is not null && _settingsWorkspaceId == workspaceId)
        {
            CloseSettingsMenu();
            return;
        }
        CloseSettingsMenu();
        _settingsAnchor = anchor;
        _vm!.SettingsWorkspaceId = workspaceId;
        _vm.BeginWorkspaceName(workspaceId);
        _settingsWorkspaceId = workspaceId;
        var workspace = _vm.SettingsWorkspace!;
        var menu = BuildSettingsMenu(workspace);
        _settingsMenu = menu;
        OverlayCanvas.Children.Add(menu);
        OverlayLayer.IsHitTestVisible = true;
        menu.Measure(Size.Infinity);
        PositionSettingsMenu();
        Dispatcher.UIThread.Post(() => menu.Focus());
    }

    private void PositionSettingsMenu()
    {
        if (_settingsMenu is null || _settingsAnchor is null) return;
        var point = _settingsAnchor.TranslatePoint(new Point(_settingsAnchor.Bounds.Width, _settingsAnchor.Bounds.Height + 6), OverlayCanvas)
                    ?? new Point(8, 8);
        var width = _settingsMenu.Bounds.Width > 0 ? _settingsMenu.Bounds.Width : 264;
        var height = _settingsMenu.Bounds.Height > 0 ? _settingsMenu.Bounds.Height : _settingsMenu.DesiredSize.Height;
        var left = Math.Max(8, Math.Min(point.X - width, Root.Bounds.Width - width - 8));
        var top = point.Y;
        var limit = (TopLevel.GetTopLevel(this)?.Bounds.Height ?? 800) - 8;
        if (top + height > limit) top = Math.Max(8, point.Y - height - _settingsAnchor.Bounds.Height - 12);
        Canvas.SetLeft(_settingsMenu, left);
        Canvas.SetTop(_settingsMenu, top);
    }

    private Control? _settingsAnchor;

    private Border BuildSettingsMenu(WorkspaceRowViewModel workspace)
    {
        var content = new StackPanel { Spacing = 0 };

        // name
        var nameSection = new StackPanel { Spacing = 6.4 };
        nameSection.Children.Add(MenuLabel("NAME"));
        var nameBox = new TextBox
        {
            Text = workspace.Name,
            FontSize = 13,
            Padding = new Thickness(5.6, 4.8, 5.6, 4.8),
            Background = Brush("BrushSurface"),
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Foreground = Brush("BrushText"),
            CaretBrush = Brush("BrushPrimary"),
        };
        nameBox.TextChanged += (_, _) => _vm?.SetWorkspaceName(workspace.Id, nameBox.Text ?? "");
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _vm?.CommitWorkspaceName();
            CloseSettingsMenu();
        };
        nameSection.Children.Add(nameBox);
        content.Children.Add(new Border { Padding = new Thickness(5.6, 4, 5.6, 4), Child = nameSection });

        content.Children.Add(Divider());

        // icons (the web's WORKSPACE_ICON_CHOICES list)
        var iconSection = new StackPanel { Spacing = 6.4 };
        iconSection.Children.Add(MenuLabel("ICON"));
        var grid = new UniformGrid { Columns = 8, Rows = 3 };
        foreach (var icon in WorkspaceIcons.Choices)
        {
            var active = workspace.Icon == icon;
            var button = new Button
            {
                Theme = ThemeFor("IconButton"),
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(1),
                Background = active ? Brush("BrushPrimarySoft") : Brush("BrushSurface"),
                BorderBrush = active ? Brush("BrushPrimary") : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Content = new SofiaIcon
                {
                    Glyph = icon,
                    Size = 15,
                    Foreground = Brush(active ? "BrushPrimary" : "BrushTextMuted"),
                },
            };
            ToolTip.SetTip(button, icon);
            button.Click += (_, _) =>
            {
                SoundService.Current.Play(SoundKey.Select);
                _ = _vm!.Store.SetWorkspaceIconAsync(workspace.Id, icon);
                CloseSettingsMenu();
            };
            grid.Children.Add(button);
        }
        iconSection.Children.Add(grid);
        content.Children.Add(new Border { Padding = new Thickness(5.6, 4, 5.6, 4), Child = iconSection });

        content.Children.Add(Divider());

        // models
        var modelSection = new StackPanel { Spacing = 4 };
        modelSection.Children.Add(MenuLabel("MODEL"));
        var models = new StackPanel { Spacing = 1.6 };
        if (_vm!.Store.Models.Count == 0)
        {
            var note = Text("No models available — add a provider in Settings.", 12, "BrushTextFaint");
            note.TextWrapping = TextWrapping.Wrap;
            note.LineHeight = 18;
            models.Children.Add(note);
        }
        foreach (var model in _vm.Store.Models.Take(24))
        {
            var choice = model;
            var active = workspace.ModelId == choice.Id;
            var rowContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            rowContent.Children.Add(new SofiaIcon { Glyph = choice.Icon, Size = 14, Foreground = Brush("BrushTextMuted"), VerticalAlignment = VerticalAlignment.Center });
            var label = Text(choice.Label, 13, "BrushTextMuted");
            label.Classes.Add("truncate");
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(6.4, 0, 0, 0);
            Grid.SetColumn(label, 1);
            rowContent.Children.Add(label);
            if (active)
            {
                var check = new SofiaIcon { Glyph = "check", Size = 13.6, Foreground = Brush("BrushPrimary"), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 2);
                rowContent.Children.Add(check);
            }
            var button = new Button
            {
                Theme = ThemeFor("RowButton"),
                Content = rowContent,
                Padding = new Thickness(6.4, 5.6, 6.4, 5.6),
                Background = active ? Brush("BrushPrimarySoft") : Brushes.Transparent,
            };
            button.Click += (_, _) =>
            {
                SoundService.Current.Play(SoundKey.Select);
                _ = _vm.Store.SetWorkspaceModelAsync(workspace.Id, choice.Id);
                CloseSettingsMenu();
            };
            models.Children.Add(button);
        }
        var scroll = new ScrollViewer
        {
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = models,
        };
        modelSection.Children.Add(scroll);
        content.Children.Add(new Border { Padding = new Thickness(5.6, 4, 5.6, 4), Child = modelSection });

        if (!workspace.IsChat && workspace.Path.Length > 0)
        {
            content.Children.Add(Divider());
            content.Children.Add(BuildWorktreeSection(workspace));
            content.Children.Add(Divider());
        }

        var remove = new Button
        {
            Theme = ThemeFor("RowButton"),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6.4,
                Children =
                {
                    new SofiaIcon { Glyph = "trash", Size = 14, Foreground = Brush("BrushErr"), VerticalAlignment = VerticalAlignment.Center },
                    Text(workspace.IsChat ? "Remove group" : "Remove workspace", 13, "BrushErr"),
                },
            },
            Padding = new Thickness(6.4, 6.4, 6.4, 6.4),
        };
        remove.Click += async (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            CloseSettingsMenu();
            await _vm.Store.RemoveWorkspaceAsync(workspace.Id);
        };
        content.Children.Add(remove);

        return new Border
        {
            Width = 264,
            Background = Brush("BrushSurface2"),
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6.4),
            BoxShadow = BoxShadows.Parse("0 20 50 0 #80000000"),
            Child = content,
            Focusable = true,
            ZIndex = ShellView.OverlayLayer.MenuHigh,
        };
    }

    private Control BuildWorktreeSection(WorkspaceRowViewModel workspace)
    {
        var list = new StackPanel { Spacing = 1.6 };
        var state = new TextBlock { Text = "Loading…", FontSize = 12, Foreground = Brush("BrushTextFaint") };
        var error = new TextBlock { IsVisible = false, FontSize = 11, Foreground = Brush("BrushErr"), TextWrapping = TextWrapping.Wrap };

        var section = new StackPanel { Spacing = 4 };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        head.Children.Add(MenuLabel("WORKTREES"));
        var refresh = IconAction("refresh", 12.8, "Refresh worktrees", async () => await LoadWorktrees(workspace.Id), size: 20, radius: 5);
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);
        section.Children.Add(head);

        list.Children.Add(state);
        section.Children.Add(list);

        var create = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4, Margin = new Thickness(0, 3.5, 0, 0) };
        var nameBox = new TextBox
        {
            PlaceholderText = "New worktree name",
            FontSize = 13,
            Padding = new Thickness(4.8, 4.8, 4.8, 4.8),
            Background = Brush("BrushSurface"),
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
        };
        create.Children.Add(nameBox);
        var add = IconAction("plus", 13.6, "Create worktree", async () =>
        {
            if (nameBox.Text?.Trim() is not { Length: > 0 } name) return;
            try
            {
                var entry = await _vm!.Store.CreateWorktreeAsync(workspace.Path, name);
                if (entry is null) return;
                await _vm.Store.SwitchToWorktreeAsync(entry.Directory);
                CloseSettingsMenu();
            }
            catch (Exception ex)
            {
                state.Text = ex.Message;
            }
        }, size: 26, radius: 7);
        Grid.SetColumn(add, 1);
        create.Children.Add(add);
        section.Children.Add(create);

        section.Children.Add(error);

        _ = LoadWorktrees(workspace.Id);
        return new Border { Padding = new Thickness(5.6, 4, 5.6, 4), Child = section };

        async Task LoadWorktrees(string workspaceId)
        {
            list.Children.Clear();
            list.Children.Add(state);
            state.Text = "Loading…";
            try
            {
                if (!_vm!.Store.IsConnected)
                {
                    list.Children.Clear();
                    list.Children.Add(Text("Worktrees need a server connection.", 12, "BrushTextFaint"));
                    return;
                }
                var (_, entries) = await _vm.Store.ListWorktreesAsync(workspace.Path);
                list.Children.Clear();
                if (entries.Count == 0)
                {
                    list.Children.Add(Text("Not a Git repository.", 12, "BrushTextFaint"));
                    return;
                }
                foreach (var entry in entries)
                {
                    list.Children.Add(BuildWorktreeRow(workspace, entry, LoadWorktrees, error));
                }
            }
            catch (Exception ex)
            {
                list.Children.Clear();
                list.Children.Add(Text(ex.Message, 12, "BrushErr"));
            }
        }
    }

    private Control BuildWorktreeRow(WorkspaceRowViewModel workspace, WorktreeEntry entry,
        Func<string, Task> reload, TextBlock error)
    {
        var name = Text(entry.Name, 13, "BrushTextMuted");
        name.Classes.Add("truncate");
        name.VerticalAlignment = VerticalAlignment.Center;
        var glyph = new SofiaIcon
        {
            Glyph = entry.IsMain ? "folder" : "branch",
            Size = 13.6,
            Foreground = Brush("BrushTextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6.4, 0),
        };
        var pickGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        pickGrid.Children.Add(glyph);
        Grid.SetColumn(name, 1);
        pickGrid.Children.Add(name);
        var pick = new Button
        {
            Theme = ThemeFor("RowButton"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = pickGrid,
            Padding = new Thickness(6.4, 4.8, 6.4, 4.8),
            Background = WorktreeEntry.SamePath(entry.Directory, workspace.Path) ? Brush("BrushPrimarySoft") : Brushes.Transparent,
        };
        ToolTip.SetTip(pick, string.IsNullOrEmpty(entry.Branch) ? entry.Directory : $"{entry.Directory} · {entry.Branch}");
        pick.Click += async (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Select);
            await _vm!.Store.SwitchToWorktreeAsync(entry.Directory);
            CloseSettingsMenu();
        };

        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        line.Children.Add(pick);
        var row = new StackPanel { Spacing = 2 };
        row.Children.Add(line);
        if (!entry.IsMain)
        {
            var remove = IconAction("trash", 12.8, "Remove worktree", async () =>
            {
                try
                {
                    await _vm!.Store.DeleteWorktreeAsync(workspace.Path, entry.Directory, force: false);
                    await reload(workspace.Id);
                }
                catch (Exception ex)
                {
                    error.Text = ex.Message;
                    error.IsVisible = true;
                    if (!ex.Message.Contains("force", StringComparison.OrdinalIgnoreCase)) return;
                    // The server refuses to drop a dirty worktree and says so: offer the destructive retry.
                    var force = new Button
                    {
                        Theme = ThemeFor("ActionButton"),
                        Content = Text("Remove anyway", 12, "BrushErr"),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                    };
                    force.Click += async (_, _) =>
                    {
                        await _vm!.Store.DeleteWorktreeAsync(workspace.Path, entry.Directory, force: true);
                        await reload(workspace.Id);
                    };
                    row.Children.Add(force);
                }
            }, size: 22, radius: 6, danger: true);
            Grid.SetColumn(remove, 1);
            line.Children.Add(remove);
        }
        return row;
    }

    private void CloseSettingsMenu()
    {
        if (_settingsMenu is not null) OverlayCanvas.Children.Remove(_settingsMenu);
        _settingsMenu = null;
        _settingsWorkspaceId = null;
        if (_vm is not null) _vm.SettingsWorkspaceId = null;
        OverlayLayer.IsHitTestVisible = _vm?.IsSearchOpen ?? false;
    }

    private static TextBlock MenuLabel(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        LetterSpacing = 0.8,
        Foreground = Brush("BrushTextFaint"),
        Margin = new Thickness(0, 0, 0, 6.4),
    };

    private static Border Divider() => new()
    {
        Height = 1,
        Background = Brush("BrushBorder"),
        Margin = new Thickness(0, 5.6, 0, 5.6),
    };

    // --- delete confirmation ------------------------------------------------------------------------------

    private void RebuildModal()
    {
        if (_vm is null) return;
        if (_modalHost is null)
        {
            if (_overlayHost is Canvas canvas)
            {
                _modalHost = new Panel { IsVisible = false, ZIndex = ShellView.OverlayLayer.Modal };
                canvas.Children.Add(_modalHost);
            }
            else
            {
                _modalHost = CreateLocalModalHost();
            }
        }

        _modalHost.Children.Clear();
        if (_vm.PendingDelete is null)
        {
            _modalHost.IsVisible = false;
            return;
        }

        var top = TopLevel.GetTopLevel(this);
        _modalHost.Width = top?.Bounds.Width ?? Root.Bounds.Width;
        _modalHost.Height = top?.Bounds.Height ?? Root.Bounds.Height;
        var scrim = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = new Border
            {
                Width = 400,
                MaxWidth = 400,
                Background = Brush("BrushSurface"),
                BorderBrush = Brush("BrushBorderStrong"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(16),
                BoxShadow = BoxShadows.Parse("0 24 60 0 #80000000"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = BuildModalContent(),
            },
        };
        scrim.PointerPressed += (_, e) =>
        {
            if (!ReferenceEquals(e.Source, scrim)) return;
            _vm.CloseDelete();
            RebuildModal();
        };
        _modalHost.Children.Add(scrim);
        _modalHost.IsVisible = true;
        if (_overlayHost is not null) _overlayHost.IsHitTestVisible = true;
    }

    private Control BuildModalContent()
    {
        var vm = _vm!;
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Text("Delete conversation", 15, "BrushErr", FontWeight.SemiBold));
        var name = Text(vm.PendingDelete?.Title ?? "", 13, "BrushText", FontWeight.Medium);
        name.Classes.Add("truncate");
        stack.Children.Add(name);
        var body = Text("This permanently removes the conversation with its messages and file snapshots. Files on disk are not touched.", 13, "BrushTextMuted");
        body.TextWrapping = TextWrapping.Wrap;
        stack.Children.Add(body);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 4.8, 0, 0),
        };
        var cancel = new Button
        {
            Theme = ThemeFor("ActionButton"),
            Content = Text("Cancel", 12, "BrushTextMuted"),
            Padding = new Thickness(12.8, 4.8, 12.8, 4.8),
            IsEnabled = !vm.Deleting,
        };
        cancel.Click += (_, _) =>
        {
            vm.CloseDelete();
            RebuildModal();
        };
        var confirm = new Button
        {
            Theme = ThemeFor("ActionButton"),
            Content = Text("Delete", 12, "BrushErr", FontWeight.SemiBold),
            Background = Brush("BrushErrSoft"),
            BorderBrush = Brush("BrushErr"),
            Padding = new Thickness(12.8, 4.8, 12.8, 4.8),
            IsEnabled = !vm.Deleting,
        };
        confirm.Click += async (_, _) =>
        {
            await vm.ConfirmDeleteAsync();
            RebuildModal();
        };
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        stack.Children.Add(actions);
        Dispatcher.UIThread.Post(() => confirm.Focus());
        return stack;
    }

    private Panel CreateLocalModalHost()
    {
        var panel = new Panel { IsVisible = false, ZIndex = ShellView.OverlayLayer.Modal };
        Root.Children.Add(panel);
        return panel;
    }

    // --- pointer / drag / dismiss -------------------------------------------------------------------------

    private void BeginPress(string kind, string id, Border row, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) return;
        if (FindAncestor<Button>(e.Source) is not null) return;
        _pressRow = row;
        _pressKind = kind;
        _pressId = id;
        _pressPoint = e.GetPosition(Root);
        _dragging = false;
    }

    private void TrackDrag(string kind, string id, Border row, PointerEventArgs e, bool allowed)
    {
        if (_pressRow is null || _pressKind != kind) return;
        var point = e.GetPosition(Root);
        if (!_dragging)
        {
            var distance = Math.Abs(point.X - _pressPoint.X) + Math.Abs(point.Y - _pressPoint.Y);
            if (distance < 4) return;
            _dragging = true;
            _dragKind = _pressKind;
            _dragId = _pressId;
            if (_pressRow is not null) _pressRow.Opacity = 0.4;
            SoundService.Current.Play(SoundKey.Click);
        }
        if (!allowed || id == _dragId || _dragKind != kind) return;
        var after = e.GetPosition(row).Y > row.Bounds.Height / 2;
        SetDropTarget(kind, id, after, row);
    }

    private void SetDropTarget(string kind, string id, bool after, Border row)
    {
        if (_dropRow == row && _dropKind == kind && _dropId == id && _dropAfter == after) return;
        ClearDropIndicator();
        _dropRow = row;
        _dropKind = kind;
        _dropId = id;
        _dropAfter = after;
        row.BoxShadow = BoxShadows.Parse(after ? "0 2 0 0 #A9744F" : "0 -2 0 0 #A9744F");
    }

    private void ClearDropIndicator()
    {
        if (_dropRow is not null) _dropRow.BoxShadow = default;
        _dropRow = null;
        _dropKind = null;
        _dropId = null;
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            var kind = _dragKind;
            var dragged = _dragId;
            var target = _dropId;
            var after = _dropAfter;
            EndDrag();
            if (kind is not null && dragged is not null && target is not null) CommitDrop(kind, dragged, target, after);
            return;
        }

        if (_pressRow is not null && FindAncestor<Button>(e.Source) is null)
        {
            var distance = Math.Abs(e.GetPosition(Root).X - _pressPoint.X) + Math.Abs(e.GetPosition(Root).Y - _pressPoint.Y);
            if (distance < 4 && _pressRow.Tag is Action action) action();
        }
        _pressRow = null;
        _pressKind = null;
        _pressId = null;
    }

    private void EndDrag()
    {
        if (_pressRow is not null) _pressRow.Opacity = 1;
        _pressRow = null;
        _pressKind = null;
        _pressId = null;
        _dragging = false;
        _dragKind = null;
        _dragId = null;
        ClearDropIndicator();
    }

    private void CommitDrop(string kind, string draggedId, string targetId, bool after)
    {
        if (_vm is null) return;
        if (kind == "conversation")
        {
            var workspace = _vm.Store.Workspaces.FirstOrDefault(w => w.Tree.Any(r => r.Id == draggedId));
            if (workspace is null) return;
            var dragged = workspace.Tree.FirstOrDefault(r => r.Id == draggedId);
            var target = workspace.Tree.FirstOrDefault(r => r.Id == targetId);
            if (dragged is null || target is null || dragged.Pinned != target.Pinned || dragged.IsSubagent) return;
            var ids = workspace.Tree.Where(r => !r.IsSubagent).Select(r => r.Id).ToList();
            MoveId(ids, draggedId, targetId, after);
            SoundService.Current.Play(SoundKey.Select);
            _ = _vm.Store.ReorderSessionsAsync(workspace.Id, ids);
            return;
        }

        var source = _vm.Store.Workspaces.FirstOrDefault(w => w.Id == draggedId);
        var destination = _vm.Store.Workspaces.FirstOrDefault(w => w.Id == targetId);
        if (source is null || destination is null || source.IsChat != destination.IsChat) return;
        var folders = _vm.FolderWorkspaces.Select(w => w.Id).ToList();
        var chats = _vm.ChatGroups.Select(w => w.Id).ToList();
        if (destination.IsChat) MoveId(chats, draggedId, targetId, after);
        else MoveId(folders, draggedId, targetId, after);
        SoundService.Current.Play(SoundKey.Select);
        _ = _vm.Store.ReorderWorkspacesAsync([.. folders, .. chats]);
    }

    private static void MoveId(List<string> ids, string draggedId, string targetId, bool after)
    {
        ids.Remove(draggedId);
        var at = ids.IndexOf(targetId);
        ids.Insert(at < 0 ? ids.Count : at + (after ? 1 : 0), draggedId);
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null) return;
        if (_vm.IsSearchOpen && FindAncestor<Border>(e.Source, "SearchPop") is null &&
            !IsInside(e.Source, SearchField))
        {
            _vm.CloseSearch();
            Rebuild();
            return;
        }
        if (_settingsMenu is not null && e.Source is Visual source &&
            (IsInside(source, _settingsMenu) || IsInside(source, _settingsAnchor)))
        {
            return;
        }
        if (_settingsMenu is not null) CloseSettingsMenu();
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_vm?.IsDeleteOpen == true)
        {
            e.Handled = true;
            _vm.CloseDelete();
            RebuildModal();
        }
        else if (_vm?.IsSearchOpen == true)
        {
            e.Handled = true;
            _vm.CloseSearch();
            Rebuild();
        }
        else if (_settingsMenu is not null)
        {
            e.Handled = true;
            CloseSettingsMenu();
        }
    }

    private void OnModalEscape()
    {
        if (_vm?.IsDeleteOpen != true) return;
        _vm.CloseDelete();
        RebuildModal();
    }

    // --- helpers ------------------------------------------------------------------------------------------

    private double LookTheme(double normal, double compact) =>
        ThemeService.Current.CompactSidebar ? compact : normal;

    private static TextBlock Text(string text, double size, string brush, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        LineHeight = Math.Round(size * 1.55, 1),
        Foreground = Brush(brush),
        FontWeight = weight ?? FontWeight.Normal,
        TextWrapping = TextWrapping.NoWrap,
    };

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private static ControlTheme? ThemeFor(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as ControlTheme : null;

    private static T? FindAncestor<T>(object? source) where T : Visual
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is T match) return match;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    private static Border? FindAncestor<T>(object? source, string name) where T : Visual
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is T match && match.Name == name) return match as Border;
            visual = visual.GetVisualParent();
        }
        return null;
    }

    private static bool IsInside(object? source, Visual? container)
    {
        if (container is null) return false;
        var visual = source as Visual;
        while (visual is not null)
        {
            if (ReferenceEquals(visual, container)) return true;
            visual = visual.GetVisualParent();
        }
        return false;
    }

    /// <summary>
    /// Motion contract #3: the row's hover affordances fade on the shared clock — the conversation trail and action
    /// pill 0→1 over .15s, the workspace count 1→0 and its actions 0→1 over .12s (the web's two CSS transitions).
    /// A participant is attached only while it animates, so a sidebar with many rows costs nothing at rest.
    /// </summary>
    private sealed class RowReveal(Control? trail, Control? actions, Control? count, Control? pin) : IMotionParticipant
    {
        private double _fromTrail;
        private double _fromActions;
        private double _fromCount;
        private double _fromPin;
        private double _start;
        private bool _reveal;
        private bool _active;

        private double Duration => (trail is not null || pin is not null
            ? MotionTokens.Reveal
            : MotionTokens.Hover).TotalMilliseconds;

        public double ActionsOpacity => actions?.Opacity ?? 0;

        public double CountOpacity => count?.Opacity ?? 0;

        public void Begin(bool reveal, double now)
        {
            _fromTrail = trail?.Opacity ?? 0;
            _fromActions = actions?.Opacity ?? 0;
            _fromCount = count?.Opacity ?? 1;
            _fromPin = pin?.Opacity ?? 0.8;
            _reveal = reveal;
            _start = now;
            _active = true;
            if (trail is not null) trail.IsHitTestVisible = reveal;
            if (actions is not null) actions.IsHitTestVisible = reveal;
            MotionService.Current.Attach(this);
            TickMotion(now);
        }

        public void TickMotion(double nowMs)
        {
            if (!_active) return;
            var progress = Math.Clamp((nowMs - _start) / Math.Max(1, Duration), 0, 1);
            var eased = MotionTokens.Standard.Ease(progress);
            var trailTarget = _reveal ? 1 : 0;
            var countTarget = _reveal ? 0 : 1;
            var pinTarget = _reveal ? 0 : 0.8;
            if (trail is not null) trail.Opacity = _fromTrail + (trailTarget - _fromTrail) * eased;
            if (actions is not null) actions.Opacity = _fromActions + (trailTarget - _fromActions) * eased;
            if (count is not null) count.Opacity = _fromCount + (countTarget - _fromCount) * eased;
            if (pin is not null) pin.Opacity = _fromPin + (pinTarget - _fromPin) * eased;
            if (progress < 1) return;
            _active = false;
            MotionService.Current.Detach(this);
        }
    }
}

/// <summary>The web's <c>WORKSPACE_ICON_CHOICES</c>: 24 Lucide keys offered by the workspace settings menu.</summary>
public static class WorkspaceIcons
{
    public static readonly string[] Choices =
    [
        "folder", "code", "book", "palette", "flask", "briefcase",
        "globe", "terminal", "database", "server", "star", "music",
        "camera", "layers", "cpu", "box",
        "chat", "sparkles", "bell", "help", "languages", "user",
        "search", "clock",
    ];
}
