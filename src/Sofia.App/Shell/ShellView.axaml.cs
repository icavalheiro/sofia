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
    private SofiaPopover? _modelPopover;

    public ShellView()
    {
        InitializeComponent();
        _panelAnimator = new PanelAnimator(Root.ColumnDefinitions[2], RightPanelInner);
        LoadArt();
        SidebarPane.UseOverlayHost(OverlayHost);
        Root.AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        Root.AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
        WireButtons();
        MotionService.Current.Attach(_panelAnimator);
    }

    public ShellViewModel? ViewModel => _vm;

    /// <summary>Named hosts, exposed for the headless screenshot harness and tests.</summary>
    public Grid Frame => Root;

    public Panel Overlays => OverlayHost;

    public Features.Sidebar.SidebarView Sidebar => SidebarPane;

    public Border Composer => ComposerBand;

    public Border RightPanel => RightPanelHost;

    /// <summary>Binds a view model: fixture data or a live backend, depending on the head.</summary>
    public void Attach(ShellViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        SidebarPane.Attach(viewModel.Sidebar);
        SidebarPane.SessionOpened += request =>
        {
            // The sidebar opened a conversation (or a search hit). The scroll target is kept for 041: the transcript
            // owns rendering, this shell only records the seam and lets the header follow the selection.
            PendingScrollMessageId = request.ScrollToMessage ? request.MessageId : null;
            ApplyAppearance();
        };
        SidebarPane.PanelToggleRequested += () =>
        {
            if (_vm is null) return;
            _vm.RightPanelOpen = !_vm.RightPanelOpen;
            _panelAnimator.SetOpen(_vm.RightPanelOpen, animate: true);
        };
        ApplyAppearance();
        Rebuild();
        viewModel.PropertyChanged += (_, _) => Rebuild();
        viewModel.Theme.PropertyChanged += (_, _) => ApplyAppearance();
        Localization.Changed += Rebuild;
    }

    /// <summary>Message a search hit asked the transcript to scroll to; consumed by spec 041.</summary>
    public string? PendingScrollMessageId { get; private set; }

    /// <summary>
    /// One key route for the whole window: Escape closes the sidebar's transient surfaces first, then the shared
    /// shortcut table runs (Ctrl+K/O, Ctrl+N, Ctrl+Shift+E, Alt+↑/↓, Ctrl+\, Ctrl+,).
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SidebarPane.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        ShortcutService.Current.TryHandle(e);
    }

    /// <summary>Clicking outside the sidebar closes its quick prompt and workspace menu (web document mousedown).</summary>
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (IsInside(source, SidebarPane)) return;
        SidebarPane.CloseTransient();
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
        BuildTranscript();
        BuildComposer();
        BuildRightPanel();
        TaskPanelHost.IsVisible = _vm.TasksOpen;
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

    private void BuildTranscript()
    {
        TranscriptInner.Children.Clear();
        var stack = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 360,
            MaxWidth = 520,
            Margin = new Thickness(0, 40, 0, 0),
        };
        var logo = new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(24),
            ClipToBounds = true,
            BorderBrush = Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 0 0 6 #29A9744F, 0 18 40 0 #66000000"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10),
            Child = new Image { Source = Load("avares://Sofia.App/Assets/Logos/logo-128.png"), Stretch = Stretch.UniformToFill },
        };
        stack.Children.Add(logo);

        var title = Caption(Localization.T("shell.emptyTitle"), 22, "BrushText");
        title.FontWeight = FontWeight.SemiBold;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(title);

        var body = Caption(Localization.T("shell.emptyBody"), 15, "BrushTextMuted");
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.TextAlignment = TextAlignment.Center;
        body.MaxWidth = 460;
        stack.Children.Add(body);

        var working = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };
        working.Children.Add(new SofiaIcon { Glyph = "loader", Size = 25.6, Foreground = Brush("BrushPrimary") });
        working.Children.Add(new TextBlock
        {
            Text = "unsloth/nvidia-nemotron-3.5-bolt-e4b",
            Classes = { "mono", "faint" },
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(working);

        TranscriptInner.Children.Add(stack);
    }

    private void BuildComposer()
    {
        ComposerInner.Children.Clear();
        if (_vm is null) return;

        var box = new Border
        {
            Background = Brush("BrushSurface"),
            BorderBrush = Brush("BrushBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 9, 10, 8),
            BoxShadow = BoxShadow("ShadowSm"),
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto") };

        var input = new TextBox
        {
            PlaceholderText = Localization.T("shell.draft"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 44,
            MaxHeight = 180,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 4, 2, 4),
            FontSize = FontSizeOf("FontSizeMd2"),
            Foreground = Brush("BrushText"),
            CaretBrush = Brush("BrushPrimary"),
            SelectionBrush = Brush("BrushPrimarySoftStrong"),
        };
        Grid.SetRow(input, 0);
        grid.Children.Add(input);

        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(Chip("bot", "build", openModel: false));
        var modelChip = Chip("cpu", ShortModel(), openModel: true);
        modelChip.Name = "ModelChip";
        chips.Children.Add(modelChip);
        chips.Children.Add(Chip("shield", "Ask", openModel: false));
        chips.Children.Add(Chip("sparkles", "Medium", openModel: false));
        Grid.SetColumn(chips, 0);
        toolbar.Children.Add(chips);

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new SofiaIcon { Glyph = "clock", Size = 13, Foreground = Brush("BrushTextFaint") });
        right.Children.Add(new TextBlock { Text = "8.1s", Classes = { "mono", "faint" }, FontSize = FontSizeOf("FontSizeXs") });
        right.Children.Add(new TextBlock { Text = "21.5k", Classes = { "mono", "faint" }, FontSize = FontSizeOf("FontSizeXs") });
        right.Children.Add(new SofiaMeter
        {
            Value = _vm.ContextFill,
            Segments = 10,
            Width = 82,
            BarHeight = 5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var send = new Button
        {
            Theme = ThemeFor("SendButton"),
            Content = new SofiaIcon { Glyph = "send", Size = 17, Foreground = Brush("BrushPrimaryInk") },
        };
        send.Click += (_, _) => SoundService.Current.Play(SoundKey.Send);
        right.Children.Add(send);
        Grid.SetColumn(right, 1);
        toolbar.Children.Add(right);

        Grid.SetRow(toolbar, 1);
        toolbar.Margin = new Thickness(0, 6, 0, 0);
        grid.Children.Add(toolbar);
        box.Child = grid;
        ComposerInner.Children.Add(box);
    }

    private Button Chip(string icon, string text, bool openModel)
    {
        var label = Caption(text, 12, "BrushTextMuted");
        label.MaxWidth = 140;
        label.Classes.Add("truncate");
        label.VerticalAlignment = VerticalAlignment.Center;
        var content = Row(
            new SofiaIcon { Glyph = icon, Size = 13, Foreground = Brush("BrushTextMuted") },
            label,
            new SofiaIcon { Glyph = "chevronUp", Size = 11, Foreground = Brush("BrushTextFaint") });
        var chip = new Button { Theme = ThemeFor("ChipButton"), Content = content };
        chip.Click += (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Select);
            if (openModel) ToggleModelPopover(chip);
        };
        return chip;
    }

    private void ToggleModelPopover(Control anchor)
    {
        if (_modelPopover is null)
        {
            var list = new StackPanel { Spacing = 2, MinWidth = 230 };
            foreach (var (name, detail) in new[]
                     {
                         ("unsloth/nvidia-nemotron-3.5-bolt", "local - 131k ctx"),
                         ("claude-sonnet-4.5", "anthropic - 200k ctx"),
                         ("gpt-5.1-codex", "openai - 400k ctx"),
                     })
            {
                var item = new Button
                {
                    Theme = ThemeFor("RowButton"),
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            Caption(name, 12, "BrushText"),
                            Caption(detail, 11, "BrushTextFaint"),
                        },
                    },
                };
                list.Children.Add(item);
            }
            var menu = new Border
            {
                Background = Brush("BrushSurface2"),
                BorderBrush = Brush("BrushBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(6),
                BoxShadow = BoxShadow("ShadowMenu"),
                Child = list,
            };
            _modelPopover = new SofiaPopover { Content = menu, MinWidth = 240 };
            _modelPopover.ZIndex = OverlayLayer.MenuHigh;
            Overlays.Children.Add(_modelPopover);
            Overlays.IsHitTestVisible = true;
        }

        _modelPopover.IsOpen = !_modelPopover.IsOpen;
        if (!_modelPopover.IsOpen) return;

        var point = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height + 8), Root) ?? default;
        Canvas.SetLeft(_modelPopover, point.X);
        Canvas.SetTop(_modelPopover, Math.Max(8, point.Y - 150));
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

    private string ShortModel()
    {
        var id = _vm?.Workspaces.SelectMany(w => w.Conversations ?? []).FirstOrDefault(s => s.Id == _vm.ActiveSessionId)?.ModelId;
        if (string.IsNullOrEmpty(id)) return "nemotron-bolt";
        var name = id.Split('/').Last();
        return name.Length > 18 ? name[..17] + "..." : name;
    }

    private static ControlTheme? ThemeFor(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as ControlTheme : null;

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Transparent;

    private static BoxShadows BoxShadow(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is BoxShadows shadows
            ? shadows
            : default;

    private static double FontSizeOf(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is double size ? size : 14;

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
    /// Motion contract #19: the right panel opens width 0â†’300 (.24s), slides translateX 40â†’0 (.24s) and fades 0â†’1
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



