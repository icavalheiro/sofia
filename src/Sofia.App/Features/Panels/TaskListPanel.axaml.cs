using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Features.Panels;

/// <summary>
/// TaskListPanel.vue ported: the agent's task list as a floating, draggable card clamped to the chat viewspace. The
/// position is remembered in local state under the web's <c>sofia.tasks.pos</c> key; the card opens instantly (no entry
/// animation) and auto-opens when a <c>tasks.updated</c> event brings the first tasks of the open conversation.
/// </summary>
public partial class TaskListPanel : UserControl
{
    private ShellViewModel? _vm;
    private bool _open;
    private bool _dragging;
    private bool _hasStoredPosition;
    private Point _dragOrigin;
    private Point _pressOrigin;
    private List<TaskItem> _tasks = [];

    public TaskListPanel()
    {
        InitializeComponent();
        Title.Text = Localization.T("panel.tasks");
        ToolTip.SetTip(CloseButton, Localization.T("panel.close"));
        ToolTip.SetTip(ClearButton, Localization.T("panel.tasksClear"));
        CloseButton.Click += (_, _) => SetOpen(false, notify: true);
        ClearButton.Click += (_, _) => _ = ClearAsync();
        Head.PointerPressed += OnHeadPressed;
        Head.PointerMoved += OnHeadMoved;
        Head.PointerReleased += OnHeadReleased;
        SizeChanged += (_, _) => Clamp();
    }

    /// <summary>Raised when the card opened or closed itself, so the header toggle follows.</summary>
    public event Action<bool>? OpenChanged;

    public IReadOnlyList<TaskItem> Tasks => _tasks;

    /// <summary>Local state the position is read from/written to (the app singleton; a temp file in tests).</summary>
    public LocalState State { get; set; } = LocalState.Current;

    /// <summary>Card position inside the chat viewspace (tests assert the clamp and the persistence).</summary>
    public Point Position => new(Card.Margin.Left, Card.Margin.Top);

    public void Attach(ShellViewModel viewModel)
    {
        if (_vm is not null) _vm.Store.Applied -= OnStoreEvent;
        _vm = viewModel;
        _vm.Store.Applied += OnStoreEvent;
        RestorePosition();
        SetOpen(_vm.TasksOpen, notify: false);
        _ = LoadAsync();
    }

    /// <summary>Opens/closes the card; opening loads the task list of the open conversation.</summary>
    public void SetOpen(bool open, bool notify = true)
    {
        _open = open;
        IsVisible = open;
        Clamp();
        if (open) _ = LoadAsync();
        if (notify) OpenChanged?.Invoke(open);
    }

    public bool IsOpen => _open;

    /// <summary>Rebuilds the labels after a locale change.</summary>
    public void RefreshLabels()
    {
        Title.Text = Localization.T("panel.tasks");
        ToolTip.SetTip(CloseButton, Localization.T("panel.close"));
        ToolTip.SetTip(ClearButton, Localization.T("panel.tasksClear"));
        Build();
    }

    /// <summary>Fixture/screenshot entry: shows a deterministic list without a backend.</summary>
    public void LoadFixture(IEnumerable<TaskItem> tasks, bool open = true)
    {
        _tasks = tasks.ToList();
        RestorePosition();
        SetOpen(open, notify: false);
        Build();
    }

    /// <summary>Fixture/screenshot entry: places the card at an explicit margin (clamp screenshots).</summary>
    public void PlaceAt(double x, double y, bool persist = false)
    {
        Card.Margin = new Thickness(x, y, 0, 0);
        Clamp();
        if (persist) PersistPosition();
    }

    private async Task LoadAsync()
    {
        if (_vm is null) return;
        if (_vm.Connection.Backend is not { } backend || _vm.ActiveSessionId.Length == 0)
        {
            Build();
            return;
        }
        try
        {
            _tasks = await backend.TasksAsync(_vm.ActiveSessionId);
        }
        catch
        {
            _tasks = [];
        }
        Build();
    }

    /// <summary>Reloads the task list of the open conversation (session switch, panel open).</summary>
    public Task RefreshAsync() => LoadAsync();

    private void OnStoreEvent(SofiaEventDto evt)
    {
        if (_vm is null || evt.Type != EventTypes.TasksUpdated) return;
        var sessionId = evt.SessionId ?? Str(evt.Data, "sessionId");
        if (sessionId is not null && sessionId != _vm.ActiveSessionId) return;
        var wasEmpty = _tasks.Count == 0;
        var data = evt.Data;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("tasks", out var list))
        {
            try
            {
                _tasks = list.Deserialize<List<TaskItem>>(Sofia.Contracts.Infrastructure.Json.Options) ?? [];
            }
            catch
            {
                return;
            }
        }
        Build();
        // Only a fresh, non-empty list opens the card: loading an existing list never reopens what the user closed.
        if (!wasEmpty || _open || _tasks.Count == 0) return;
        SetOpen(true);
    }

    private static string? Str(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // --- position ------------------------------------------------------------------------------------------

    private void RestorePosition()
    {
        var stored = State.Get<TaskPosition>(LocalState.TasksPositionKey);
        _hasStoredPosition = stored is not null;
        if (stored is not null)
        {
            Card.Margin = new Thickness(stored.X, stored.Y, 0, 0);
            return;
        }
        var parent = Parent as Control;
        var width = parent?.Bounds.Width ?? 0;
        var height = parent?.Bounds.Height ?? 0;
        // The web's default: bottom-right of the chat viewspace, never above 16px from either edge.
        Card.Margin = new Thickness(Math.Max(16, width - 300), Math.Max(16, height - 320), 0, 0);
    }

    /// <summary>Keeps the card fully inside the chat viewspace (the web's <c>clamp</c>).</summary>
    public void Clamp()
    {
        if (Parent is not Control parent) return;
        var bounds = parent.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var width = Card.Bounds.Width > 0 ? Card.Bounds.Width : 280;
        var height = Card.Bounds.Height > 0 ? Card.Bounds.Height : 200;
        var x = Math.Max(0, Math.Min(bounds.Width - width, Card.Margin.Left));
        var y = Math.Max(0, Math.Min(bounds.Height - height, Card.Margin.Top));
        if (Math.Abs(x - Card.Margin.Left) < 0.5 && Math.Abs(y - Card.Margin.Top) < 0.5) return;
        Card.Margin = new Thickness(x, y, 0, 0);
    }

    private void OnHeadPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Button) return;
        _dragging = true;
        _pressOrigin = e.GetPosition(this);
        _dragOrigin = new Point(Card.Margin.Left, Card.Margin.Top);
        e.Pointer.Capture(Head);
        e.Handled = true;
    }

    private void OnHeadMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging) return;
        var point = e.GetPosition(this);
        Card.Margin = new Thickness(
            _dragOrigin.X + (point.X - _pressOrigin.X),
            _dragOrigin.Y + (point.Y - _pressOrigin.Y),
            0,
            0);
        Clamp();
    }

    private void OnHeadReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        PersistPosition();
    }

    private void PersistPosition()
    {
        _hasStoredPosition = true;
        State.Set(LocalState.TasksPositionKey, new TaskPosition(Card.Margin.Left, Card.Margin.Top));
    }

    // --- building ------------------------------------------------------------------------------------------

    private void Build()
    {
        var completed = _tasks.Count(task => task.Status == TaskItemStatus.Completed);
        CountPill.IsVisible = _tasks.Count > 0;
        CountText.Text = $"{completed}/{_tasks.Count}";
        ClearButton.IsVisible = _tasks.Count > 0;
        ClearCount.Text = _tasks.Count.ToString();

        Items.Children.Clear();
        if (_tasks.Count == 0)
        {
            var empty = Ui.Text(Localization.T("panel.tasksEmpty"), 12, "BrushTextFaint");
            empty.TextWrapping = TextWrapping.Wrap;
            Items.Children.Add(empty);
            return;
        }

        foreach (var task in _tasks.OrderBy(task => task.Order))
            Items.Children.Add(Item(task));
    }

    private Control Item(TaskItem task)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 7.2 };
        // The web asks for `circle` on pending tasks; that name is not in the icon map, so nothing is drawn (parity).
        var glyph = task.Status switch
        {
            TaskItemStatus.InProgress => "loader",
            TaskItemStatus.Completed => "check",
            TaskItemStatus.Cancelled => "x",
            _ => "circle",
        };
        var brush = task.Status switch
        {
            TaskItemStatus.InProgress => "BrushPrimary",
            TaskItemStatus.Completed => "BrushOk",
            TaskItemStatus.Cancelled => "BrushErr",
            _ => "BrushTextFaint",
        };
        var icon = new SofiaIcon
        {
            Glyph = glyph,
            Size = 13.6,
            Foreground = Ui.Brush(brush),
            Spin = task.Status == TaskItemStatus.InProgress,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(icon);
        var text = Ui.Text(task.Title, 12, task.Status == TaskItemStatus.Completed ? "BrushTextMuted" : "BrushText");
        text.TextWrapping = TextWrapping.Wrap;
        if (task.Status == TaskItemStatus.Completed) text.TextDecorations = TextDecorations.Strikethrough;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    private async Task ClearAsync()
    {
        if (_vm?.Connection.Backend is { } backend && _vm.ActiveSessionId.Length > 0)
        {
            try
            {
                await backend.ClearTasksAsync(_vm.ActiveSessionId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("clear tasks failed: " + ex.Message);
            }
        }
        _tasks = [];
        Build();
    }
}

/// <summary>The persisted card position (web <c>sofia.tasks.pos</c>).</summary>
public sealed record TaskPosition(double X, double Y);
