using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sofia.App.Controls;
using Sofia.App.Features.Panels;
using Sofia.App.Features.Settings;
using Sofia.App.Services;
using Sofia.App.Theme;
using Sofia.Contracts.Domain;

namespace Sofia.App.Shell;

/// <summary>
/// The shell frame: sidebar, header (title/crumbs/status/language/session menus), transcript, composer, right panel, the
/// floating task card and the overlay ladder (lightbox, diff modal, settings, destructive modals). The right panel is
/// animated from the shared clock (motion #19) so the frame harness can capture it stop-motion.
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
    private Panel? _settingsLayer;
    private Panel? _pairingLayer;
    private SettingsDialog? _settings;
    private PairingDialog? _pairing;

    public ShellView()
    {
        InitializeComponent();
        _panelAnimator = new PanelAnimator(Root.ColumnDefinitions[2], RightPanelPane);
        LoadArt();
        SidebarPane.UseOverlayHost(OverlayHost);
        TranscriptPane.UseOverlayHost(OverlayHost);
        RightPanelPane.UseOverlayHost(OverlayHost);
        // The composer publishes its measured height (attachment strip + growing textarea + hint row change it);
        // the transcript reserves that many DIPs at the bottom so the last row is never covered.
        ComposerPane.HeightChanged += height => TranscriptPane.SetComposerHeight(height);
        ComposerBand.SizeChanged += (_, _) =>
        {
            if (ComposerPane.MeasuredHeight > 0) TranscriptPane.SetComposerHeight(ComposerPane.MeasuredHeight);
        };
        Root.AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);
        Root.AddHandler(PointerPressedEvent, OnGlobalPointerPressed, RoutingStrategies.Tunnel);
        BuildOverlayLayers();
        WireButtons();
        WireComposerShortcuts();
        MotionService.Current.Attach(_panelAnimator);
        Localization.Changed += OnLocaleChanged;
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

    /// <summary>The right panel (043): changes, processes, actions and the diff modal.</summary>
    public RightPanelView Panel => RightPanelPane;

    /// <summary>The conversation header (043): title, crumbs, status dot, language and session menus.</summary>
    public Features.Header.HeaderView Header => HeaderPane;

    /// <summary>The settings modal (043).</summary>
    public SettingsDialog Settings => _settings!;

    /// <summary>The fast task card (043).</summary>
    public TaskListPanel Tasks => TaskPane;

    /// <summary>The pairing dialog (043).</summary>
    public PairingDialog Pairing => _pairing!;

    /// <summary>Binds a view model: fixture data or a live backend, depending on the head.</summary>
    public void Attach(ShellViewModel viewModel)
    {
        _vm = viewModel;
        DataContext = viewModel;
        SidebarPane.Attach(viewModel.Sidebar);
        TranscriptPane.Attach(viewModel.Transcript);
        ComposerPane.Attach(viewModel.Composer);
        HeaderPane.Attach(viewModel);
        RightPanelPane.Attach(viewModel);
        TaskPane.Attach(viewModel);
        _settings!.Attach(viewModel);
        _pairing!.Attach(viewModel);
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
            SetRightPanel(!_vm.RightPanelOpen, animate: true);
        };
        SidebarPane.ComposerFocusRequested += ComposerPane.FocusInput;
        SidebarPane.ShellSettingsRequested += () => OpenSettings();
        HeaderPane.PanelToggled += open => SetRightPanel(open, animate: true);
        HeaderPane.TasksToggled += open => TaskPane.SetOpen(open, notify: false);
        HeaderPane.SessionImported += sessionId => _ = OpenSessionAsync(sessionId);
        RightPanelPane.Closed += () => SetRightPanel(false, animate: true);
        TaskPane.OpenChanged += open =>
        {
            if (_vm is not null) _vm.TasksOpen = open;
            HeaderPane.Rebuild();
        };
        _settings!.OpenChanged += open => SetLayerOpen(_settingsLayer, open);
        _settings.PairingRequested += () => OpenPairing();
        _pairing!.Closed += () => SetLayerOpen(_pairingLayer, false);
        viewModel.AppearanceChanged += ApplyAppearance;
        ApplyAppearance();
        Rebuild();
        viewModel.PropertyChanged += (_, _) => Rebuild();
        viewModel.Theme.PropertyChanged += (_, _) => ApplyAppearance();
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
        HeaderPane.Rebuild();
        RightPanelPane.RefreshLabels();
        _ = TaskPane.RefreshAsync();
        if (PendingScrollMessageId is { } messageId)
        {
            TranscriptPane.ScrollToMessage(messageId);
            PendingScrollMessageId = null;
        }
    }

    /// <summary>Opens the settings modal on a tab (gear, Ctrl+, Ctrl+/ and the screenshot harness).</summary>
    public void OpenSettings(SettingsDialog.SettingsTab tab = SettingsDialog.SettingsTab.General)
    {
        if (_settings is null) return;
        SetLayerOpen(_settingsLayer, true);
        _settings.Open(tab);
    }

    /// <summary>Opens the pairing dialog above the settings modal.</summary>
    public void OpenPairing()
    {
        if (_pairing is null) return;
        SetLayerOpen(_pairingLayer, true);
        _pairing.Open();
    }

    /// <summary>Fixture/screenshot entry: opens the pairing dialog with a deterministic code.</summary>
    public void PreviewPairing()
    {
        if (_pairing is null) return;
        SetLayerOpen(_pairingLayer, true);
        _pairing.PreviewFixture();
    }

    private void BuildOverlayLayers()
    {
        if (OverlayHost is not Canvas canvas) return;
        _settingsLayer = new Panel { IsVisible = false, ZIndex = OverlayLayer.Settings };
        _settings = new SettingsDialog();
        _settingsLayer.Children.Add(_settings);
        canvas.Children.Add(_settingsLayer);

        _pairingLayer = new Panel { IsVisible = false, ZIndex = OverlayLayer.Modal };
        _pairing = new PairingDialog();
        _pairingLayer.Children.Add(_pairing);
        canvas.Children.Add(_pairingLayer);

        // A Canvas arranges its children at their own size; the modal layers must cover the whole window so their scrim
        // centres the dialog and dims everything behind it.
        void Fit()
        {
            foreach (var layer in new[] { _settingsLayer, _pairingLayer })
            {
                if (layer is null) continue;
                layer.Width = Math.Max(canvas.Bounds.Width, canvas.DesiredSize.Width);
                layer.Height = Math.Max(canvas.Bounds.Height, canvas.DesiredSize.Height);
            }
        }
        canvas.SizeChanged += (_, _) => Fit();
        Fit();
    }

    private void SetLayerOpen(Panel? layer, bool open)
    {
        if (layer is not null) layer.IsVisible = open;
        OverlayHost.IsHitTestVisible = open || _settings?.IsOpen == true || _pairing?.IsVisible == true;
    }

    /// <summary>
    /// One key route for the whole window: Escape closes the settings/pairing/diff dialogs first, then the composer's
    /// palettes/popovers, then the sidebar's and the transcript's transient surfaces, then the shared shortcut table
    /// (Ctrl+K/O, Ctrl+N, Ctrl+Shift+E, Alt+↑/↓, Ctrl+\, Ctrl+, Ctrl+/, and the composer's Ctrl+L/M/B/P/Shift+A/.).
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _settings?.HandleEscape() == true)
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _pairing?.IsVisible == true)
        {
            _pairing.Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && RightPanelPane.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (_settings?.IsOpen == true)
        {
            // The modal owns the keyboard: only its own recorder sees the keys.
            return;
        }
        if (e.Key == Key.Escape && ComposerPane.HandleEscape())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && HeaderPane.HandleEscape())
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
    /// lands outside the composer closes its chip popovers and the pinned context legend, and the header menus close too.
    /// </summary>
    private void OnGlobalPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (!IsInside(source, ComposerPane)) ComposerPane.CloseTransient();
        if (!IsInside(source, HeaderPane)) HeaderPane.CloseTransient();
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
        ShortcutService.Current.Register("settings.open", () => OpenSettings());
        ShortcutService.Current.Register("shortcuts.show", () => OpenSettings(SettingsDialog.SettingsTab.Shortcuts));
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
        RightPanelPane.SetOpen(open);
    }

    /// <summary>Re-applies accent/font-scale/compact tokens after a settings change and rebuilds the chrome.</summary>
    public void RefreshAppearance() => ApplyAppearance();

    private void OnLocaleChanged()
    {
        Rebuild();
        RightPanelPane.RefreshLabels();
        TaskPane.RefreshLabels();
        _settings?.RefreshLabels();
    }

    // --- appearance ---------------------------------------------------------------------------------------

    private void ApplyAppearance()
    {
        var theme = ThemeService.Current;
        Root.ColumnDefinitions[0].Width = new GridLength(theme.SidebarWidth);
        SidebarPane.RefreshAppearance();

        if (!_panelAnimator.IsOpen && _vm?.RightPanelOpen != true)
        {
            Root.ColumnDefinitions[2].Width = new GridLength(0);
            RightPanelPane.Opacity = 0;
        }
        _panelAnimator.SetOpen(_vm?.RightPanelOpen ?? false, animate: false);
        RightPanelPane.SetOpen(_vm?.RightPanelOpen ?? false);
        Rebuild();
    }

    // --- building -----------------------------------------------------------------------------------------

    private void Rebuild()
    {
        if (_vm is null) return;
        HeaderPane.Rebuild();
        TranscriptPane.SetComposerHeight(ComposerPane.MeasuredHeight > 0 ? ComposerPane.MeasuredHeight : 130);
    }

    // --- helpers -------------------------------------------------------------------------------------------

    private void WireButtons()
    {
        // The header owns the panel/tasks toggles; nothing else needs a direct click handler here.
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
    }

    private static Bitmap Load(string uri)
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }

    /// <summary>
    /// Motion contract #19: the right panel opens width 0→300 (.24s), slides translateX 40→0 (.24s) and fades 0→1
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
