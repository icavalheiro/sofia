using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Theme;
using Sofia.Contracts.Domain;
using Path = Avalonia.Controls.Shapes.Path;

namespace Sofia.App.Shell;

/// <summary>
/// The shell frame: builds the sidebar tree, header, transcript host, composer chrome and right panel from
/// <see cref="ShellViewModel"/>, keeps the overlay ladder, and animates the right panel from the shared clock. Feature
/// specs 040-043 replace the placeholder rows and the empty transcript with the real components; the geometry, tokens
/// and timings here are final.
/// </summary>
public partial class ShellView : UserControl
{
    /// <summary>Web z-index ladder, mapped to the overlay canvas (one host, no OS popups).</summary>
    public static class OverlayLayer
    {
        public const int Composer = 10;
        public const int Menu = 30;
        public const int MenuHigh = 60;
        public const int Lightbox = 200;
        public const int Diff = 300;
        public const int Settings = 2000;
        public const int Modal = 2100;
    }

    private ShellViewModel? _vm;
    private readonly PanelAnimator _panelAnimator;

    public ShellView()
    {
        InitializeComponent();
        _panelAnimator = new PanelAnimator(Root.ColumnDefinitions[2], RightPanelInner);
        LoadArt();
        SidebarPane.UseOverlayHost(OverlayHost);
        TranscriptPane.UseOverlayHost(OverlayHost);
        // The composer publishes its measured height (attachment strip + growing textarea + hint row change it);
        // the transcript reserves that many DIPs at the bottom so the last row is never covered.
        ComposerPane.HeightChanged += height => TranscriptPane.SetComposerHeight(height);
        ComposerBand.SizeChanged += (_, _) =>
        {
            if (ComposerPane.MeasuredHeight > 0) TranscriptPane.SetComposerHeight(ComposerPane.MeasuredHeight);
        };
        Root.AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        Root.AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
        WireButtons();
        WireComposerShortcuts();
        MotionService.Current.Attach(_panelAnimator);
    }

    public ShellViewModel? ViewModel => _vm;

    /// <summary>Named hosts, exposed for the headless screenshot harness and tests.</summary>
    public Grid Frame => Root;

    public Panel Overlays => OverlayHost;

    public Features.Sidebar.SidebarView Sidebar => SidebarPane;

    /// <summary>The transcript host (spec 041): rows, scrolling and its overlays.</summary>
    public Features.Transcript.TranscriptView Transcript => TranscriptPane;

    public Border Composer => ComposerBand;

    /// <summary>The composer (spec 042): input, strips, chips, palettes and the popup layer.</summary>
    public Features.Composer.ComposerView ComposerView => ComposerPane;

    public Border RightPanel => RightPanelHost;

    /// <summary>Binds a view model: fixture data or a live backend, depending on the head.</summary>
    public void Attach(ShellViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        SidebarPane.Attach(viewModel.Sidebar);
        TranscriptPane.Attach(viewModel.Transcript);
        ComposerPane.Attach(viewModel.Composer);
        if (viewModel.Connection.Backend is { } backend)
        {
            TranscriptPane.Attachments.UrlResolver = backend.AttachmentUrl;
            viewModel.Composer.SetBackend(backend);
        }
        TranscriptPane.SessionOpenRequested += sessionId => _ = viewModel.OpenSessionAsync(sessionId);
        SidebarPane.SessionOpened += request =>
        {
            // The sidebar opened a conversation (or a search hit): the transcript loads it and scrolls to the hit.
            PendingScrollMessageId = request.ScrollToMessage ? request.MessageId : null;
            ApplyAppearance();
            _ = OpenSessionAsync(request.SessionId);
        };
        SidebarPane.PanelToggleRequested += () =>
        {
            if (_vm is null) return;
            _vm.RightPanelOpen = !_vm.RightPanelOpen;
            _panelAnimator.SetOpen(_vm.RightPanelOpen, animate: true);
        };
        SidebarPane.ComposerFocusRequested += ComposerPane.FocusInput;
        ApplyAppearance();
        Rebuild();
        viewModel.PropertyChanged += (_, _) => Rebuild();
        viewModel.Theme.PropertyChanged += (_, _) => ApplyAppearance();
        Localization.Changed += Rebuild;
    }

    /// <summary>Message a search hit asked the transcript to scroll to; consumed by spec 041.</summary>
    public string? PendingScrollMessageId { get; private set; }

    /// <summary>Opens one conversation: the transcript reloads over the backend (or the fixture) and scrolls to a hit.</summary>
    private async Task OpenSessionAsync(string sessionId)
    {
        if (_vm is null) return;
        try
        {
            await _vm.OpenSessionAsync(sessionId);
        }
        catch (Exception ex)
        {
            _vm.Transcript.SetStatus("error");
            Console.Error.WriteLine("open session failed: " + ex.Message);
            return;
        }
        TranscriptPane.Attach(_vm.Transcript);
        if (_vm.Connection.Backend is { } backend) TranscriptPane.Attachments.UrlResolver = backend.AttachmentUrl;
        if (PendingScrollMessageId is { } messageId)
        {
            TranscriptPane.ScrollToMessage(messageId);
            PendingScrollMessageId = null;
        }
    }

    /// <summary>
    /// One key route for the whole window: Escape closes the composer's palettes/popovers first, then the sidebar's and
    /// the transcript's transient surfaces, then the shared shortcut table runs (Ctrl+K/O, Ctrl+N, Ctrl+Shift+E,
    /// Alt+â†‘/â†“, Ctrl+\, Ctrl+, and the composer's Ctrl+L/M/B/P/Shift+A/.).
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && ComposerPane.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && SidebarPane.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && TranscriptPane.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        ShortcutService.Current.TryHandle(e);
    }

    /// <summary>
    /// Clicking outside the sidebar closes its quick prompt and workspace menu (web document mousedown); a click that
    /// lands outside the composer closes its chip popovers and the pinned context legend.
    /// </summary>
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (!IsInside(source, ComposerPane)) ComposerPane.CloseTransient();
        if (IsInside(source, SidebarPane)) return;
        SidebarPane.CloseTransient();
    }

    /// <summary>The composer's shortcut ids (web <c>useShortcut</c> rows) routed through the shared registry.</summary>
    private void WireComposerShortcuts()
    {
        ShortcutService.Current.Register("composer.focus", ComposerPane.FocusInput);
        ShortcutService.Current.Register("agent.pick", () => ComposerPane.ViewModel?.OpenMenu(Features.Composer.ComposerMenu.Agent));
        ShortcutService.Current.Register("model.pick", () => ComposerPane.ViewModel?.OpenMenu(Features.Composer.ComposerMenu.Model));
        ShortcutService.Current.Register("permission.pick", () => ComposerPane.ViewModel?.OpenMenu(Features.Composer.ComposerMenu.Permission));
        ShortcutService.Current.Register("reasoning.pick", () => ComposerPane.ViewModel?.OpenMenu(Features.Composer.ComposerMenu.Reasoning));
        ShortcutService.Current.Register("session.stop", () =>
        {
            if (ComposerPane.ViewModel is { IsReplying: true } composer) _ = composer.StopAsync();
        });
    }

    private static bool IsInside(object? source, Visual container)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (ReferenceEquals(visual, container)) return true;
            visual = visual.GetVisualParent();
        }
        return false;
    }

    /// <summary>Shows the connect screen (no backend yet) with the given connection state.</summary>
    public void ShowGate(BackendConnection connection, Func<Task> retry)
    {
        var gate = new BackendGate();
        gate.Attach(connection, retry);
        GateHost.Child = gate;
        GateHost.IsVisible = true;
        Root.ColumnDefinitions[0].Width = new GridLength(0);
        Root.ColumnDefinitions[2].Width = new GridLength(0);
    }

    public void HideGate()
    {
        GateHost.IsVisible = false;
        ApplyAppearance();
    }

    /// <summary>Opens or closes the right panel through the motion contract (#19); used by the header and 043.</summary>
    public void SetRightPanel(bool open, bool animate)
    {
        if (_vm is not null) _vm.RightPanelOpen = open;
        _panelAnimator.SetOpen(open, animate);
    }

    /// <summary>Re-applies accent/font-scale/compact tokens after a settings change and rebuilds the chrome.</summary>
    public void RefreshAppearance() => ApplyAppearance();

    // --- appearance ---------------------------------------------------------------------------------------

    private void ApplyAppearance()
    {
        var theme = ThemeService.Current;
        Root.ColumnDefinitions[0].Width = new GridLength(theme.SidebarWidth);
        SidebarPane.RefreshAppearance();

        if (!_panelAnimator.IsOpen && !_vm!.RightPanelOpen)
        {
            Root.ColumnDefinitions[2].Width = new GridLength(0);
            RightPanelInner.Opacity = 0;
        }
        _panelAnimator.SetOpen(_vm?.RightPanelOpen ?? false, animate: false);
        Rebuild();
    }

    // --- building -----------------------------------------------------------------------------------------

    private void Rebuild()
    {
        if (_vm is null) return;
        BuildHeader();
        BuildRightPanel();
        TaskPanelHost.IsVisible = _vm.TasksOpen;
        TranscriptPane.SetComposerHeight(ComposerPane.MeasuredHeight > 0 ? ComposerPane.MeasuredHeight : 130);
    }

    private void BuildHeader()
    {
        if (_vm is null) return;
        HeaderTitle.Text = _vm.ActiveSessionTitle;
        HeaderBreadcrumb.Text = string.IsNullOrEmpty(_vm.ActiveWorkspacePath)
            ? _vm.ActiveWorkspaceName
            : _vm.ActiveWorkspacePath;
        StatusText.Text = Localization.T($"status.{_vm.Status}");
        if (StatusText.Text.StartsWith("status.")) StatusText.Text = _vm.Status;
        var (fill, text) = _vm.Status switch
        {
            "working" => ("BrushPrimarySoft", "BrushPrimary"),
            "waiting" => ("BrushSurface2", "BrushTextMuted"),
            "permission" => ("BrushWarnSoft", "BrushWarn"),
            "question" => ("BrushInfoSoft", "BrushInfo"),
            "error" => ("BrushErrSoft", "BrushErr"),
            _ => ("BrushSurface2", "BrushTextMuted"),
        };
        StatusPill.Background = Brush(fill);
        StatusText.Foreground = Brush(text);

        if (StatusPill.Child is StackPanel pill)
        {
            pill.Children.Clear();
            var icon = new SofiaIcon
            {
                Glyph = "loader",
                Size = 14,
                Paused = _vm.Status is not ("working" or "waiting"),
                Foreground = Brush(text),
            };
            pill.Children.Add(icon);
            pill.Children.Add(StatusText);
        }
    }

    private void BuildRightPanel()
    {
        ChangesList.Children.Clear();
        foreach (var (file, add, del) in new[]
                 {
                     ("src/Sofia.App/Theme/Tokens.axaml", 74, 0),
                     ("src/Sofia.App/Controls/SofiaIcon.cs", 190, 0),
                     ("src/Sofia.App/Shell/ShellView.axaml", 154, 0),
                 })
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Height = 26 };
            var name = Caption(file, 12, "BrushTextMuted");
            name.Classes.Add("truncate");
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            var stats = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            stats.Children.Add(Caption($"+{add}", 11, "BrushOk"));
            stats.Children.Add(Caption($"-{del}", 11, "BrushErr"));
            Grid.SetColumn(stats, 1);
            row.Children.Add(stats);
            ChangesList.Children.Add(new Border { Child = row, Padding = new Thickness(4, 0) });
        }

        ProcessList.Children.Clear();
        foreach (var (label, dot) in new[] { ("dotnet run - :5199", "BrushOk"), ("tsc --watch", "BrushWarn") })
        {
            var row = Row(
                new Border
                {
                    Width = 6,
                    Height = 6,
                    CornerRadius = new CornerRadius(3),
                    Background = Brush(dot),
                    VerticalAlignment = VerticalAlignment.Center,
                },
                Caption(label, 12, "BrushTextMuted"));
            ProcessList.Children.Add(row);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private void WireButtons()
    {
        ChangesButton.Click += (_, _) =>
        {
            if (_vm is null) return;
            _vm.RightPanelOpen = !_vm.RightPanelOpen;
            _panelAnimator.SetOpen(_vm.RightPanelOpen, animate: true);
            SoundService.Current.Play(SoundKey.Click);
        };
        ClosePanelButton.Click += (_, _) =>
        {
            if (_vm is null) return;
            _vm.RightPanelOpen = false;
            _panelAnimator.SetOpen(false, animate: true);
            SoundService.Current.Play(SoundKey.Click);
        };
        TasksButton.Click += (_, _) =>
        {
            if (_vm is null) return;
            _vm.TasksOpen = !_vm.TasksOpen;
            TaskPanelHost.IsVisible = _vm.TasksOpen;
            SoundService.Current.Play(SoundKey.Click);
        };
    }

    private void LoadArt()
    {
        WallpaperLayer.Background = new ImageBrush(Load("avares://Sofia.App/Assets/Wallpaper/chat-wallpaper.png"))
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, 400, 400, RelativeUnit.Absolute),
        };
        GrainLayer.Background = new ImageBrush(Load("avares://Sofia.App/Assets/Wallpaper/grain.png"))
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, 140, 140, RelativeUnit.Absolute),
        };
        TaskCount.Text = "2 open";
        TaskList.Children.Add(Caption("Port the design tokens", 12, "BrushTextMuted"));
        TaskList.Children.Add(Caption("Verify reduced motion", 12, "BrushTextMuted"));
    }

    private static Bitmap Load(string uri)
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private static TextBlock Caption(string text, double size, string brush) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(brush),
        TextWrapping = TextWrapping.NoWrap,
    };

    private static StackPanel Row(params Control[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    /// <summary>
    /// Motion contract #19: the right panel opens width 0Ã¢â€ â€™300 (.24s), slides translateX 40Ã¢â€ â€™0 (.24s) and fades 0Ã¢â€ â€™1
    /// (.18s). Driven by the shared clock so the frame harness can capture it stop-motion.
    /// </summary>
    private sealed class PanelAnimator(ColumnDefinition column, Control content) : IMotionParticipant
    {
        private const double OpenWidth = 300;
        private const double Slide = 40;

        private double _fromWidth;
        private double _toWidth;
        private double _fromOpacity;
        private double _toOpacity;
        private double _start;
        private bool _animating;

        public bool IsOpen { get; private set; }

        public void SetOpen(bool open, bool animate)
        {
            IsOpen = open;
            if (!animate)
            {
                column.Width = new GridLength(open ? OpenWidth : 0);
                content.Opacity = open ? 1 : 0;
                content.RenderTransform = new TranslateTransform(open ? 0 : Slide, 0);
                _animating = false;
                return;
            }

            _fromWidth = column.Width.IsAbsolute ? column.Width.Value : content.Bounds.Width;
            _toWidth = open ? OpenWidth : 0;
            _fromOpacity = content.Opacity;
            _toOpacity = open ? 1 : 0;
            _start = MotionService.Current.NowMs;
            _animating = true;
            MotionService.Current.Attach(this);
            TickMotion(_start);
        }

        public void TickMotion(double nowMs)
        {
            if (!_animating) return;
            var widthDuration = Math.Max(1, MotionTokens.PanelWidth.TotalMilliseconds);
            var fadeDuration = Math.Max(1, MotionTokens.PanelFade.TotalMilliseconds);
            var widthProgress = Math.Clamp((nowMs - _start) / widthDuration, 0, 1);
            var fadeProgress = Math.Clamp((nowMs - _start) / fadeDuration, 0, 1);
            var easedWidth = MotionTokens.Standard.Ease(widthProgress);
            var easedFade = MotionTokens.Standard.Ease(fadeProgress);
            var width = _fromWidth + (_toWidth - _fromWidth) * easedWidth;
            column.Width = new GridLength(Math.Max(0, width));
            content.Opacity = _fromOpacity + (_toOpacity - _fromOpacity) * easedFade;
            var slide = IsOpen ? Slide * (1 - easedWidth) : Slide * easedWidth;
            content.RenderTransform = new TranslateTransform(slide, 0);
            if (widthProgress < 1) return;
            _animating = false;
            MotionService.Current.Detach(this);
        }
    }
}



