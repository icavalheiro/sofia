using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.App.Theme;
using Sofia.Contracts.Domain;
using DiffRow = Sofia.App.Features.Transcript.DiffRow;

namespace Sofia.App.Features.Panels;

/// <summary>
/// RightPanel.vue ported: the changed files of the open conversation (session diff ∪ git working tree), the background
/// processes started by the agent (polled every 3 s while the panel is open, killable and clearable) and the workspace
/// actions. Clicking a file opens the z300 side-by-side diff modal; the folder/VS Code/terminal actions are
/// desktop-only and hidden behind <see cref="HeadOptions.HostActions"/>.
/// </summary>
public partial class RightPanelView : UserControl
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private ShellViewModel? _vm;
    private Panel? _overlayHost;
    private DispatcherTimer? _poll;
    private bool _open;
    private bool _loading;
    private string _note = "";
    private string _source = "working";
    private int _loadSeq;
    private string _lastSignature = "";
    private List<PanelChange> _changes = [];
    private List<PanelProcess> _processes = [];
    private readonly DiffModal _diffModal = new();

    public RightPanelView()
    {
        InitializeComponent();
        PanelTitle.Text = Localization.T("panel.title");
        ToolTip.SetTip(RefreshButton, Localization.T("panel.reload"));
        RefreshButton.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            _ = ReloadAsync();
        };
        CloseButton.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            Closed?.Invoke();
        };
    }

    /// <summary>Raised when the head's close button was pressed.</summary>
    public event Action? Closed;

    /// <summary>The changed files currently listed (screenshot evidence and tests).</summary>
    public IReadOnlyList<PanelChange> Changes => _changes;

    public IReadOnlyList<PanelProcess> Processes => _processes;

    /// <summary>Hosts the diff modal on the shell's overlay ladder (z300), never inside the 300px column.</summary>
    public void UseOverlayHost(Panel host)
    {
        _overlayHost = host;
        if (host is Canvas canvas)
        {
            var layer = new Panel { IsVisible = false, ZIndex = ShellView.OverlayLayer.Diff };
            canvas.Children.Add(layer);
            _diffModal.Host(layer);
            // The Canvas does not stretch its children: keep the modal layer at the window size so the scrim covers it.
            void Fit()
            {
                layer.Width = Math.Max(canvas.Bounds.Width, canvas.DesiredSize.Width);
                layer.Height = Math.Max(canvas.Bounds.Height, canvas.DesiredSize.Height);
            }
            canvas.SizeChanged += (_, _) => Fit();
            Fit();
        }
    }

    public void Attach(ShellViewModel viewModel)
    {
        _vm = viewModel;
        _ = ReloadAsync();
    }

    /// <summary>Opens or closes the polling loop; the panel loads on open and never polls while closed.</summary>
    public void SetOpen(bool open)
    {
        _open = open;
        if (open)
        {
            _ = ReloadAsync();
            if (_vm?.Connection.Backend is not null)
            {
                _poll ??= new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => _ = LoadProcessesAsync());
                _poll.Start();
            }
            return;
        }
        _poll?.Stop();
        _diffModal.Close();
    }

    public void RefreshLabels()
    {
        PanelTitle.Text = Localization.T("panel.title");
        ToolTip.SetTip(RefreshButton, Localization.T("panel.reload"));
        ToolTip.SetTip(CloseButton, Localization.T("panel.close"));
        Build();
    }

    /// <summary>Fixture/screenshot entry: deterministic changes and processes without a backend.</summary>
    public void LoadFixture(IEnumerable<PanelChange> changes, IEnumerable<PanelProcess> processes, bool open = true)
    {
        _changes = [.. changes];
        _processes = [.. processes];
        _loading = false;
        _note = "";
        _source = "both";
        Build();
        if (open) SetOpen(open);
    }

    /// <summary>Frame harness: starts/stops the refresh glyph spin (motion #9, .9s) without a request.</summary>
    public void PreviewSpinning(bool spinning) => SetSpinning(spinning);

    /// <summary>Fixture/screenshot entry: opens the diff modal for the first listed change.</summary>
    public bool PreviewDiff(string? path = null, bool animate = true)
    {
        var change = path is null ? _changes.FirstOrDefault() : _changes.FirstOrDefault(c => c.Path == path);
        if (change is null) return false;
        _diffModal.Open(change, animate);
        return true;
    }

    /// <summary>True while the diff modal is open (tests and the harness).</summary>
    public bool DiffOpen => _diffModal.IsVisible;

    /// <summary>Escape closes the diff modal; returns true when it consumed the key.</summary>
    public bool HandleEscape() => _diffModal.HandleEscape();

    // --- loading -------------------------------------------------------------------------------------------

    private string Directory =>
        _vm?.Store.Workspaces.FirstOrDefault(w => w.Id == _vm.ActiveWorkspaceId) is { Kind: "folder" } workspace
            ? workspace.Path
            : "";

    private string SessionId => _vm?.ActiveSessionId ?? "";

    private async Task ReloadAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        var seq = ++_loadSeq;
        _loading = true;
        _note = "";
        SetSpinning(true);
        Build();
        var directory = Directory;
        var sessionId = SessionId;
        try
        {
            var sessionTask = sessionId.Length > 0 ? backend.DiffAsync(sessionId) : Task.FromResult(new List<FileDiff>());
            var workingTask = directory.Length > 0
                ? backend.VcsDiffAsync(directory)
                : Task.FromResult(new List<FileDiff>());
            var sessionOk = true;
            var workingOk = true;
            List<FileDiff> session = [];
            List<FileDiff> working = [];
            try
            {
                session = await sessionTask;
            }
            catch
            {
                sessionOk = false;
            }
            try
            {
                working = await workingTask;
            }
            catch
            {
                workingOk = false;
            }
            if (seq != _loadSeq) return;
            if (!sessionOk && !workingOk)
                _note = Localization.T("panel.noChanges");
            _source = session.Count > 0 && working.Count > 0 ? "both" : session.Count > 0 ? "session" : "working";
            var merged = Merge(session, working);
            var signature = string.Join('\n', merged.Select(d => $"{d.File}|{d.Status}|{d.Additions}|{d.Deletions}|{d.Patch}"));
            if (signature != _lastSignature)
            {
                _lastSignature = signature;
                _changes = merged.Select(PanelChange.FromDiff).ToList();
            }
            await LoadProcessesAsync();
        }
        catch (Exception ex)
        {
            if (seq != _loadSeq) return;
            _lastSignature = "";
            _changes = [];
            _note = ex.Message;
        }
        finally
        {
            if (seq == _loadSeq)
            {
                _loading = false;
                SetSpinning(false);
                Build();
            }
        }
    }

    private async Task LoadProcessesAsync()
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            var data = await backend.ProcessesAsync(null);
            _processes = PanelProcess.FromJson(data);
        }
        catch
        {
            _processes = [];
        }
        Build();
    }

    /// <summary>
    /// Union of the session diff and the git working tree; for files in both the working-tree diff wins because it is
    /// cumulative against HEAD (the same rule as <c>mergeDiffs</c> in RightPanel.vue).
    /// </summary>
    public static List<FileDiff> Merge(IEnumerable<FileDiff> session, IEnumerable<FileDiff> working)
    {
        var merged = new Dictionary<string, FileDiff>(StringComparer.Ordinal);
        foreach (var diff in session) merged[Key(diff.File)] = diff;
        foreach (var diff in working) merged[Key(diff.File)] = diff;
        return [.. merged.Values.OrderBy(d => d.File, StringComparer.OrdinalIgnoreCase)];
    }

    private static string Key(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private void SetSpinning(bool spinning)
    {
        RefreshIcon.Spin = spinning;
        RefreshIcon.SpinMs = MotionTokens.PanelSpinner;
    }

    // --- building ------------------------------------------------------------------------------------------

    private void Build()
    {
        if (_vm is null) return;
        Body.Children.Clear();
        Body.Children.Add(ChangesSection());
        Body.Children.Add(ProcessesSection());
        Body.Children.Add(ActionsSection());
    }

    private Control ChangesSection()
    {
        var section = new StackPanel { Spacing = 0 };
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6.4 };
        labelRow.Children.Add(Ui.SectionLabel($"{Localization.T("panel.modified")} · {_changes.Count}"));
        var source = new Border
        {
            Child = Ui.Text(Localization.T($"panel.source.{_source}"), 10, "BrushTextFaint", Avalonia.Media.FontWeight.Medium),
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(5.6, 0.8),
            Background = Ui.Brush("BrushSurface3"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };
        labelRow.Children.Add(source);
        section.Children.Add(labelRow);

        if (_note.Length > 0)
        {
            section.Children.Add(Note(_note));
            return section;
        }
        if (_loading && _changes.Count == 0)
        {
            section.Children.Add(Note(Localization.T("panel.loadingChanges")));
            return section;
        }
        if (_changes.Count == 0)
        {
            section.Children.Add(Note(Directory.Length > 0 || SessionId.Length > 0
                ? Localization.T("panel.noChanges")
                : Localization.T("panel.noFolder")));
            return section;
        }

        foreach (var change in _changes)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
                ColumnSpacing = 7.2,
                Margin = new Thickness(5.6, 4.8),
                Background = Brushes.Transparent,
            };
            row.Children.Add(Badge(change.Status, 18, 5, 10));
            var icon = Ui.Icon("file", 14.4, "BrushTextMuted");
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var path = Ui.Truncate(change.Path, 12, "BrushText");
            Grid.SetColumn(path, 2);
            row.Children.Add(path);
            var delta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4.8 };
            var add = Ui.Text($"+{change.Additions}", 10, "BrushOk");
            add.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
            var del = Ui.Text($"-{change.Deletions}", 10, "BrushErr");
            del.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
            delta.Children.Add(add);
            delta.Children.Add(del);
            Grid.SetColumn(delta, 3);
            row.Children.Add(delta);
            var button = new Button
            {
                Theme = (Avalonia.Styling.ControlTheme?)Application.Current?.FindResource("ComposerMenuRow"),
                Content = row,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ToolTip.SetTip(button, change.Path);
            var captured = change;
            button.Click += (_, _) =>
            {
                SoundService.Current.Play(SoundKey.Select);
                _diffModal.Open(captured);
            };
            section.Children.Add(button);
        }
        return section;
    }

    private Control ProcessesSection()
    {
        var section = new StackPanel();
        var running = _processes.Count(p => p.Status == "running");
        var finished = _processes.Count - running;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(Ui.SectionLabel($"{Localization.T("panel.processes")} · {running}"));
        if (finished > 0)
        {
            var clear = new Button
            {
                Content = ClearContent(finished),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4.8, 1.6),
                CornerRadius = new CornerRadius(6),
            };
            ToolTip.SetTip(clear, Localization.T("panel.processesClear"));
            clear.Click += (_, _) => _ = ClearProcessesAsync(finished);
            Grid.SetColumn(clear, 1);
            clear.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(clear);
        }
        section.Children.Add(head);

        if (_processes.Count == 0)
        {
            section.Children.Add(Note(Localization.T("panel.processesNone")));
            return section;
        }

        foreach (var process in _processes)
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                ColumnSpacing = 7.2,
                Margin = new Thickness(5.6, 4.8),
            };
            row.Children.Add(Badge(process.Status switch
            {
                "running" => "r",
                "exited" => "e",
                _ => "k",
            }, 18, 5, 9, process.Status == "running" ? "BrushOk" : "BrushTextFaint"));
            var command = Ui.Truncate(process.Command, 12, "BrushText");
            ToolTip.SetTip(command, process.Command);
            Grid.SetColumn(command, 1);
            row.Children.Add(command);
            if (process.Status == "running")
            {
                var kill = Ui.IconButton("stop", 12, Localization.T("panel.kill"), () => _ = KillAsync(process.Id), 22);
                Grid.SetColumn(kill, 2);
                row.Children.Add(kill);
            }
            section.Children.Add(row);
        }
        return section;
    }

    private Control ClearContent(int finished)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3.2 };
        row.Children.Add(Ui.Icon("trash", 12.8, "BrushTextFaint"));
        var count = Ui.Text(finished.ToString(), 10, "BrushTextFaint", Avalonia.Media.FontWeight.SemiBold);
        count.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
        row.Children.Add(count);
        return row;
    }

    private Control ActionsSection()
    {
        var section = new StackPanel { Spacing = 4.8 };
        section.Children.Add(Ui.SectionLabel(Localization.T("panel.actions")));
        var directory = Directory;
        section.Children.Add(Action("folderOpen", Localization.T("panel.openFolder"), directory, "folder"));
        section.Children.Add(Action("code", Localization.T("panel.openVscode"), directory, "vscode"));
        section.Children.Add(Action("terminal", Localization.T("panel.terminal"), directory, "terminal"));
        section.Children.Add(Action("folder", Localization.T("panel.copyPath"), directory, "copy"));
        if (_note.Length > 0) section.Children.Add(Note(_note));
        return section;
    }

    private Control Action(string glyph, string label, string directory, string app)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8.8 };
        content.Children.Add(Ui.Icon(glyph, 15.2, "BrushText"));
        content.Children.Add(Ui.Text(label, 13, "BrushText"));
        var button = new Button
        {
            Content = content,
            Padding = new Thickness(9.6, 8),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Brush("BrushBorderStrong"),
            Background = Ui.Brush("BrushSurface2"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            IsEnabled = directory.Length > 0 && (app == "copy" || HeadOptions.Current.HostActions),
            IsVisible = app == "copy" || HeadOptions.Current.HostActions,
        };
        button.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            if (app == "copy") _ = CopyPathAsync(directory);
            else _ = OpenAsync(directory, app);
        };
        return button;
    }

    private static Control Note(string text)
    {
        var note = Ui.Text(text, 12, "BrushTextFaint");
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 8, 0, 0);
        return note;
    }

    private static Border Badge(string text, double size, double radius, double fontSize, string brush = "")
    {
        var color = brush.Length > 0
            ? brush
            : text switch
            {
                "A" => "BrushOk",
                "D" => "BrushErr",
                _ => "BrushWarn",
            };
        var background = color switch
        {
            "BrushOk" => "BrushOkSoft",
            "BrushErr" => "BrushErrSoft",
            _ => "BrushWarnSoft",
        };
        var label = Ui.Text(text.ToUpperInvariant(), fontSize, color, Avalonia.Media.FontWeight.Bold);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
        return new Border
        {
            Child = label,
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(radius),
            Background = Ui.Brush(background),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // --- mutations -----------------------------------------------------------------------------------------

    private async Task KillAsync(string id)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.KillProcessAsync(id);
        }
        catch (Exception ex)
        {
            _note = ex.Message;
        }
        await LoadProcessesAsync();
    }

    private async Task ClearProcessesAsync(int finished)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            var result = await backend.ClearProcessesAsync();
            var removed = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("removed", out var value)
                ? value.GetInt32()
                : finished;
            _note = Localization.T("panel.processesCleared", ("count", removed));
        }
        catch (Exception ex)
        {
            _note = ex.Message;
        }
        await LoadProcessesAsync();
    }

    private async Task OpenAsync(string directory, string app)
    {
        if (_vm?.Connection.Backend is not { } backend) return;
        try
        {
            await backend.OpenAsync(directory, app);
            _note = Localization.T(app switch
            {
                "vscode" => "panel.openedVscode",
                "terminal" => "panel.openedTerminal",
                _ => "panel.openedFolder",
            });
        }
        catch (Exception ex)
        {
            _note = ex.Message;
        }
        Build();
        ClearNoteLater();
    }

    private async Task CopyPathAsync(string directory)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        try
        {
            if (clipboard is null) throw new InvalidOperationException(Localization.T("panel.copyFailed"));
            await clipboard.SetValueAsync(DataFormat.Text, directory);
            _note = Localization.T("panel.pathCopied");
        }
        catch
        {
            _note = Localization.T("panel.copyFailed");
        }
        Build();
        ClearNoteLater();
    }

    private void ClearNoteLater()
    {
        var note = _note;
        DispatcherTimer.RunOnce(() =>
        {
            if (_note != note) return;
            _note = "";
            Build();
        }, TimeSpan.FromMilliseconds(2600));
    }
}

/// <summary>One changed file of the panel: status badge, counts and the parsed side-by-side rows.</summary>
public sealed record PanelChange(
    string Path,
    string Status,
    int Additions,
    int Deletions,
    IReadOnlyList<DiffRow> Rows)
{
    public static PanelChange FromDiff(FileDiff diff) => new(
        diff.File,
        diff.Status,
        diff.Additions,
        diff.Deletions,
        Features.Transcript.UnifiedDiff.Parse(diff.Patch ?? ""));
}

/// <summary>One background process row (GET /api/processes).</summary>
public sealed record PanelProcess(string Id, string Command, int? Pid, string Status, int? ExitCode)
{
    public static List<PanelProcess> FromJson(JsonElement data)
    {
        var list = new List<PanelProcess>();
        if (data.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in data.EnumerateArray())
        {
            var status = Text(item, "status") ?? "running";
            list.Add(new PanelProcess(
                Text(item, "id") ?? "",
                Text(item, "command") ?? "",
                Number(item, "pid"),
                status,
                Number(item, "exitCode")));
        }
        return list;
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Number(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;
}
