using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Header;

/// <summary>
/// The conversation header (ChatHeader.vue + SessionPopover.vue): title, workspace crumbs with the chat tag, the LLM
/// status dot with its per-state tint and tooltip, and the four panel toggles. The language menu and the session menu
/// are in-window surfaces drawn on the header's own canvas (never OS popups) and open instantly.
/// </summary>
public partial class HeaderView : UserControl
{
    private enum MenuKind
    {
        None,
        Language,
        Session,
    }

    private ShellViewModel? _vm;
    private Button? _languageButton;
    private Button? _sessionButton;
    private Button? _tasksButton;
    private Button? _panelButton;
    private Border? _menu;
    private MenuKind _open;
    private string _sessionNote = "";
    private bool _sessionBusy;
    private StatsView? _stats;
    private bool _statsOpen;

    public HeaderView()
    {
        InitializeComponent();
        Root.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
    }

    /// <summary>Raised after an imported session was created, so the shell can open it.</summary>
    public event Action<string>? SessionImported;

    public void Attach(ShellViewModel viewModel)
    {
        _vm = viewModel;
        Rebuild();
    }

    /// <summary>Rebuilds every label from the current state/locale (the shell calls this on data and locale changes).</summary>
    public void Rebuild()
    {
        if (_vm is null) return;
        var vm = _vm;
        var title = vm.ActiveSessionTitle;
        Title.Text = string.IsNullOrEmpty(title) || title == "Sofia" ? Localization.T("header.newConversation") : title;

        var workspace = vm.Store.Workspaces.FirstOrDefault(w => w.Id == vm.ActiveWorkspaceId);
        var isChat = workspace is { Kind: "chat" };
        CrumbIcon.Glyph = isChat ? (string.IsNullOrEmpty(workspace?.Icon) ? "chat" : workspace!.Icon!) : "folder";
        CrumbName.Text = workspace?.Name ?? "";
        CrumbTag.IsVisible = isChat;
        CrumbTagText.Text = Localization.T("nav.chatTag");
        CrumbPath.Text = isChat ? "" : workspace?.Path ?? "";
        CrumbPath.IsVisible = !isChat;

        var status = vm.HeaderStatus;
        var labels = new Dictionary<string, (string Glyph, string Tint)>
        {
            ["working"] = ("loader", "BrushPrimary"),
            ["waiting"] = ("loader", "BrushPrimary"),
            ["done"] = ("check", "BrushOk"),
            ["permission"] = ("alert", "BrushWarn"),
            ["question"] = ("help", "BrushPrimary"),
            ["error"] = ("x", "BrushErr"),
        };
        StatusDot.IsVisible = labels.ContainsKey(status);
        if (labels.TryGetValue(status, out var look))
        {
            StatusIcon.Glyph = look.Glyph;
            StatusIcon.Paused = status == "waiting";
            StatusIcon.Spin = status is "working" or "waiting";
            StatusIcon.Foreground = Ui.Brush(look.Tint);
            StatusDot.BorderBrush = Ui.Brush(look.Tint);
            ToolTip.SetTip(StatusDot, Localization.T($"header.status.{status}"));
        }

        BuildActions();
        if (_open != MenuKind.None) BuildMenu();
    }

    private void BuildActions()
    {
        var vm = _vm!;
        Actions.Children.Clear();
        _languageButton = Ui.PanelToggle("languages", 16, Localization.T("header.language"), () => Toggle(MenuKind.Language));
        _sessionButton = Ui.PanelToggle("layers", 16, Localization.T("header.sessionActions"), () => Toggle(MenuKind.Session));
        _tasksButton = Ui.PanelToggle("list", 16, Localization.T("header.tasks"), () =>
        {
            vm.TasksOpen = !vm.TasksOpen;
            TasksToggled?.Invoke(vm.TasksOpen);
            BuildActions();
        });
        _panelButton = Ui.PanelToggle("panelRight", 16, Localization.T("header.panel"), () =>
        {
            vm.RightPanelOpen = !vm.RightPanelOpen;
            PanelToggled?.Invoke(vm.RightPanelOpen);
            BuildActions();
        });
        Ui.SetToggleActive(_languageButton, _open == MenuKind.Language);
        Ui.SetToggleActive(_sessionButton, _open == MenuKind.Session);
        Ui.SetToggleActive(_tasksButton, vm.TasksOpen);
        Ui.SetToggleActive(_panelButton, vm.RightPanelOpen);
        Actions.Children.Add(_languageButton);
        Actions.Children.Add(_sessionButton);
        Actions.Children.Add(_tasksButton);
        Actions.Children.Add(_panelButton);
    }

    /// <summary>Raised when the header's tasks toggle changed (the shell shows/hides the floating card).</summary>
    public event Action<bool>? TasksToggled;

    /// <summary>Raised when the header's panel toggle changed (the shell animates the column).</summary>
    public event Action<bool>? PanelToggled;

    /// <summary>Visible label of the title (tests and the localization gate).</summary>
    public string TitleText => Title.Text ?? "";

    /// <summary>Status dot state: visible + the glyph it draws (working → loader, done → check, …).</summary>
    public (bool Visible, string Glyph, string Tooltip) StatusDotState =>
        (StatusDot.IsVisible, StatusIcon.Glyph ?? "", ToolTip.GetTip(StatusDot) as string ?? "");

    /// <summary>Number of entries in the open menu (language: six, session: actions + stats).</summary>
    public int OpenMenuEntries => _menu?.Child is StackPanel stack ? stack.Children.Count : 0;

    /// <summary>Opens the language or session menu programmatically (tests and the harness).</summary>
    public void PreviewMenu(string menu)
    {
        _open = menu == "language" ? MenuKind.Language : MenuKind.Session;
        MenuLayer.Children.Clear();
        _menu = null;
        BuildMenu();
        BuildActions();
    }

    private void Toggle(MenuKind kind)
    {
        _open = _open == kind ? MenuKind.None : kind;
        _sessionNote = "";
        _statsOpen = false;
        MenuLayer.Children.Clear();
        _menu = null;
        if (_open != MenuKind.None) BuildMenu();
        BuildActions();
    }

    /// <summary>Escape closes the open menu; returns true when it consumed the key.</summary>
    public bool HandleEscape()
    {
        if (_open == MenuKind.None) return false;
        Toggle(_open);
        return true;
    }

    /// <summary>A click outside the menus closes them (the shell forwards the document mousedown).</summary>
    public void CloseTransient()
    {
        if (_open == MenuKind.None) return;
        Toggle(_open);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_open == MenuKind.None || e.Source is not Visual source) return;
        if (_menu is not null && IsInside(source, _menu)) return;
        if (_languageButton is not null && IsInside(source, _languageButton)) return;
        if (_sessionButton is not null && IsInside(source, _sessionButton)) return;
        CloseTransient();
    }

    private static bool IsInside(Visual source, Visual container)
    {
        Visual? current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, container)) return true;
            current = current.GetVisualParent();
        }
        return false;
    }

    // --- menus --------------------------------------------------------------------------------------------------

    private void BuildMenu()
    {
        MenuLayer.Children.Clear();
        _menu = null;
        if (_vm is null || _open == MenuKind.None) return;
        var surface = _open == MenuKind.Language ? LanguageMenu() : SessionMenu();
        MenuLayer.Children.Add(surface);
        _menu = surface;
        MenuLayer.IsHitTestVisible = true;
        surface.Measure(Size.Infinity);
        var anchor = _open == MenuKind.Language ? _languageButton : _sessionButton;
        if (anchor is null) return;
        var origin = anchor.TranslatePoint(new Point(0, 0), Root) ?? default;
        var width = surface.DesiredSize.Width;
        Canvas.SetLeft(surface, Math.Max(4, origin.X + anchor.Bounds.Width - width));
        Canvas.SetTop(surface, origin.Y + anchor.Bounds.Height + 6);
    }

    private Border LanguageMenu()
    {
        var surface = Ui.MenuSurface(176, 4);
        var stack = new StackPanel { Spacing = 1 };
        foreach (var code in Localization.Languages)
        {
            var active = code == Localization.Current;
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("22,Auto,*,Auto"),
                ColumnSpacing = 8,
            };
            var codeText = Ui.Text(code.ToUpperInvariant(), 11, "BrushTextFaint");
            codeText.FontFamily = (Avalonia.Media.FontFamily?)Application.Current?.FindResource("FontFamilyMono");
            row.Children.Add(codeText);
            var label = Ui.Text(Localization.LanguageLabel(code), 13, "BrushText");
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            if (active)
            {
                var check = Ui.Icon("check", 13.6, "BrushPrimary");
                Grid.SetColumn(check, 3);
                row.Children.Add(check);
            }
            var item = new Button
            {
                Theme = (Avalonia.Styling.ControlTheme?)Application.Current?.FindResource("ComposerMenuRow"),
                Content = row,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = active ? Ui.Brush("BrushPrimarySoft") : Avalonia.Media.Brushes.Transparent,
                Padding = new Thickness(8.8, 7.2),
            };
            var captured = code;
            item.Click += (_, _) =>
            {
                PickLanguage(captured);
                Toggle(MenuKind.Language);
            };
            stack.Children.Add(item);
        }
        surface.Child = stack;
        return surface;
    }

    private void PickLanguage(string code)
    {
        if (_vm is null) return;
        SoundService.Current.Play(SoundKey.Select);
        _vm.Language = code;
        // Per-session: the conversation keeps the locale it was left in (PATCH /api/sessions/{id} { language }).
        if (_vm.Connection.Backend is { } backend && _vm.ActiveSessionId.Length > 0)
        {
            var sessionId = _vm.ActiveSessionId;
            _ = Run(async () => await backend.UpdateSessionAsync(
                sessionId, new UpdateSessionRequest(null, null, null, null, null, null, code)));
        }
    }

    private Border SessionMenu()
    {
        var vm = _vm!;
        var surface = Ui.MenuSurface(240, 8.8);
        var stack = new StackPanel { Spacing = 1 };
        var session = vm.FindSession(vm.ActiveSessionId);
        var shortId = vm.ActiveSessionId.Length > 14 ? vm.ActiveSessionId[..14] : vm.ActiveSessionId;

        var head = new StackPanel { Spacing = 1.6, Margin = new Thickness(5.6, 1.6, 5.6, 8) };
        head.Children.Add(Ui.Truncate(string.IsNullOrEmpty(session?.Title) ? Localization.T("session.fallback") : session!.Title,
            13, "BrushText", Avalonia.Media.FontWeight.SemiBold));
        head.Children.Add(Ui.Text(shortId.Length == 0 ? Localization.T("session.notConnected") : shortId, 10, "BrushTextFaint"));
        stack.Children.Add(head);
        stack.Children.Add(Ui.Divider(0, 4));

        stack.Children.Add(Entry("code", _sessionBusy ? Localization.T("session.compacting") : Localization.T("session.compact"),
            () => Run(CompactAsync), !_sessionBusy && vm.ActiveSessionId.Length > 0));
        stack.Children.Add(Entry("box", Localization.T("session.export"), () => Run(ExportAsync),
            !_sessionBusy && vm.ActiveSessionId.Length > 0));
        stack.Children.Add(Entry("folderOpen", Localization.T("session.import"), () => Run(ImportAsync), !_sessionBusy));
        stack.Children.Add(Entry("check", Localization.T("session.stats"), () => Run(StatsAsync), !_sessionBusy));

        if (_statsOpen && _stats is { } stats)
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
                ColumnSpacing = 12,
                RowSpacing = 4.8,
            };
            var rows = new (string Key, string Value)[]
            {
                ("session.stats.sessions", stats.Sessions.ToString()),
                ("session.stats.prompts", stats.Prompts.ToString()),
                ("session.stats.steps", stats.Steps.ToString()),
                ("session.stats.tokensIn", stats.Tokens.Input.ToString("N0")),
                ("session.stats.tokensOut", stats.Tokens.Output.ToString("N0")),
                ("session.stats.cost", "$" + stats.Cost.ToString("0.00")),
                ("session.stats.activeDays", stats.ActiveDays.ToString()),
                ("session.stats.streak", stats.Streak.ToString()),
            };
            for (var i = 0; i < rows.Length; i++)
            {
                var cell = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                cell.Children.Add(Ui.Text(Localization.T(rows[i].Key), 11, "BrushTextMuted"));
                var value = Ui.Text(rows[i].Value, 11, "BrushText", Avalonia.Media.FontWeight.SemiBold);
                value.FontFamily = (Avalonia.Media.FontFamily?)Application.Current?.FindResource("FontFamilyMono");
                Grid.SetColumn(value, 1);
                cell.Children.Add(value);
                Grid.SetRow(cell, i / 2);
                Grid.SetColumn(cell, i % 2);
                grid.Children.Add(cell);
            }
            var note = Ui.Text(Localization.T("session.statsNote"), 10, "BrushTextFaint");
            note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            Grid.SetRow(note, 3);
            Grid.SetColumnSpan(note, 2);
            grid.Children.Add(note);
            var box = new Border
            {
                Child = grid,
                Margin = new Thickness(0, 7.2, 0, 0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = Ui.Brush("BrushBorder"),
                Background = Ui.Brush("BrushSurface"),
            };
            stack.Children.Add(box);
        }

        if (_sessionNote.Length > 0)
        {
            var note = Ui.Text(_sessionNote, 11, "BrushTextFaint");
            note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            note.Margin = new Thickness(5.6, 7.2, 5.6, 0);
            stack.Children.Add(note);
        }

        surface.Child = stack;
        return surface;
    }

    private Button Entry(string glyph, string label, Action click, bool enabled)
    {
        var content = Ui.MenuRowContent(glyph, label);
        return Ui.MenuRow(content, click, enabled);
    }

    private async Task CompactAsync()
    {
        if (_vm?.Connection.Backend is not { } backend || _vm.ActiveSessionId.Length == 0) return;
        _sessionBusy = true;
        BuildMenu();
        try
        {
            await backend.CompactAsync(_vm.ActiveSessionId);
            _sessionNote = Localization.T("session.compactStarted");
        }
        catch (Exception ex)
        {
            _sessionNote = ex.Message;
        }
        finally
        {
            _sessionBusy = false;
            BuildMenu();
        }
    }

    private async Task StatsAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        if (_stats is not null && _statsOpen)
        {
            _statsOpen = false;
            BuildMenu();
            return;
        }
        _sessionBusy = true;
        BuildMenu();
        try
        {
            _stats = await backend.StatsAsync();
            _statsOpen = true;
        }
        catch (Exception ex)
        {
            _sessionNote = ex.Message;
        }
        finally
        {
            _sessionBusy = false;
            BuildMenu();
        }
    }

    private async Task ExportAsync()
    {
        if (_vm?.Connection.Backend is not { } backend || _vm.ActiveSessionId.Length == 0) return;
        _sessionBusy = true;
        BuildMenu();
        try
        {
            var data = await backend.ExportAsync(_vm.ActiveSessionId);
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                _sessionNote = Localization.T("common.errorNoFileSystem");
            }
            else
            {
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = Localization.T("session.export"),
                    SuggestedFileName = $"session-{_vm.ActiveSessionId[..Math.Min(12, _vm.ActiveSessionId.Length)]}.json",
                    FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
                });
                if (file is not null)
                {
                    await using var stream = await file.OpenWriteAsync();
                    await JsonSerializer.SerializeAsync(stream, data, Sofia.Contracts.Infrastructure.Json.Options);
                    _sessionNote = Localization.T("session.exported");
                }
            }
        }
        catch (Exception ex)
        {
            _sessionNote = ex.Message;
        }
        finally
        {
            _sessionBusy = false;
            BuildMenu();
        }
    }

    private async Task ImportAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            _sessionNote = Localization.T("common.errorNoFileSystem");
            BuildMenu();
            return;
        }
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.T("session.import"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
        });
        if (files.Count == 0) return;
        _sessionBusy = true;
        BuildMenu();
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("info", out var info))
                throw new ArgumentException(Localization.T("session.importInvalid"));
            var session = info.Deserialize<Session>(Sofia.Contracts.Infrastructure.Json.Options);
            if (session is null) throw new ArgumentException(Localization.T("session.importInvalid"));
            var messages = root.TryGetProperty("messages", out var list)
                ? list.Deserialize<List<Message>>(Sofia.Contracts.Infrastructure.Json.Options)
                : null;
            var imported = await backend.ImportAsync(new ImportRequest(_vm.ActiveWorkspaceId, session, messages));
            _sessionNote = Localization.T("session.imported", ("id", imported.Id[..Math.Min(14, imported.Id.Length)]));
            SessionImported?.Invoke(imported.Id);
        }
        catch (Exception ex)
        {
            _sessionNote = ex.Message;
        }
        finally
        {
            _sessionBusy = false;
            BuildMenu();
        }
    }

    private async Task Run(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _sessionNote = ex.Message;
            BuildMenu();
        }
    }
}
