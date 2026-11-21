using Avalonia;
using Avalonia.Controls;
using Sofia.App.Services;

namespace Sofia.App.Shell;

/// <summary>
/// Desktop window: owns the shell view model, resolves the backend (fixture or live), persists its geometry and runs the
/// screenshot/frame harness when the head asked for it.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private CancellationTokenSource? _listen;

    public MainWindow() : this(HeadOptions.Current.Fixture)
    {
    }

    public MainWindow(bool fixture)
    {
        InitializeComponent();
        _viewModel = new ShellViewModel(fixture);
        LocalPreferences.Current.Apply();
        _viewModel.Language = Localization.Current;
        Shell.Attach(_viewModel);
        _viewModel.RightPanelOpen = HeadOptions.Current.Screenshot?.RightPanel ?? false;
        _viewModel.Theme.CompactSidebar = HeadOptions.Current.Screenshot?.CompactSidebar ?? false;
        _viewModel.Theme.ApplyAll();
        Shell.Attach(_viewModel);

        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => PersistGeometry();
        RestoreGeometry();
    }

    public ShellView ShellView => Shell;

    public ShellViewModel ViewModel => _viewModel;

    private async Task OnOpenedAsync()
    {
        var screenshot = HeadOptions.Current.Screenshot;
        if (screenshot is not null)
        {
            await Task.Delay(250);
            ScreenshotHarness.Apply(screenshot);
            Shell.Attach(_viewModel);
            ApplySidebarState(screenshot);
            ApplyTranscriptState(screenshot);
            Shell.Attach(_viewModel);
            ApplyComposerState(screenshot);
            ApplyPanelsAndSettings(screenshot);
            var frameActionApplied = false;
            var composerFrameApplied = false;
            ScreenshotHarness.FrameBegin = () =>
            {
                if (screenshot.HoverSession is { } row) Shell.Sidebar.HoverConversation(row);
                switch (screenshot.PanelFrame)
                {
                    case "spin":
                        Shell.Panel.PreviewSpinning(true);
                        break;
                    case "diff":
                        Shell.Panel.PreviewDiff();
                        break;
                }
                if (screenshot.PanelOpen)
                {
                    Shell.SetRightPanel(false, animate: false);
                    Shell.SetRightPanel(true, animate: true);
                }
                if (!composerFrameApplied)
                {
                    composerFrameApplied = true;
                    switch (screenshot.ComposerFrame)
                    {
                        case "press":
                            Shell.ComposerView.BeginPressFrame();
                            break;
                        case "chip":
                            Shell.ComposerView.BeginChipHoverFrame();
                            break;
                    }
                }
                // Fold/hover/carousel animations must start at clock 0 exactly once (the sizes loop calls this again).
                if (frameActionApplied) return;
                frameActionApplied = true;
                ApplyTranscriptFrame(screenshot);
            };
            ScreenshotHarness.Run(this, screenshot);
            ScreenshotHarness.FrameBegin = null;
            Shell.Transcript.CloseOverlays();
            Close();
            // The desktop head is a one-shot capture: shut the lifetime down so the CLI process exits (a live
            // dispatcher timer in the store would otherwise keep the window's dispatcher alive).
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
            return;
        }

        if (_viewModel.IsFixture) return;

        var resolver = HeadOptions.Current.Resolver;
        if (resolver is null)
        {
            Shell.ShowGate(_viewModel.Connection, () => ConnectAsync(resolver));
            return;
        }
        await ConnectAsync(resolver);
    }

    private async Task ConnectAsync(IBackendResolver? resolver)
    {
        if (resolver is null) return;
        Shell.ShowGate(_viewModel.Connection, () => ConnectAsync(resolver));
        await _viewModel.Connection.ConnectAsync(resolver);
        if (!_viewModel.Connection.IsReady || _viewModel.Connection.Backend is null) return;

        Shell.HideGate();
        await Task.Delay(120);
        await _viewModel.LoadAsync(_viewModel.Connection.Backend);
        Shell.Attach(_viewModel);

        _listen?.Cancel();
        _listen = new CancellationTokenSource();
        _ = _viewModel.Store.ListenAsync(_viewModel.Connection.Backend, _listen.Token);
    }

    /// <summary>Fixture state for the 040 screenshot gate: empty account, collapsed working row or open quick prompt.</summary>
    private void ApplySidebarState(ScreenshotRequest screenshot)
    {
        switch (screenshot.SidebarState)
        {
            case "empty":
                _viewModel.LoadFixture(empty: true);
                break;
            case "search":
                _viewModel.Sidebar.PreviewSearch("working", Fixture.SearchHits());
                break;
            case "settings":
                Shell.Sidebar.PreviewSettingsMenu(_viewModel.SidebarStore.Workspaces.FirstOrDefault()?.Id ?? "");
                break;
            case "delete":
                Shell.Sidebar.PreviewDelete(_viewModel.SidebarStore.Workspaces.FirstOrDefault()?.Id ?? "");
                break;
            case "working":
                foreach (var workspace in _viewModel.Store.Workspaces)
                {
                    if ((workspace.Conversations ?? []).Any(s =>
                            _viewModel.SidebarStore.Badges.GetValueOrDefault(s.Id) is "working" or "waiting"))
                    {
                        workspace.Expanded = false;
                    }
                }
                _viewModel.SidebarStore.Rebuild();
                break;
        }
    }

    /// <summary>Loads the 041 transcript fixture state (and opens the lightbox for the lightbox state).</summary>
    private void ApplyTranscriptState(ScreenshotRequest screenshot)
    {
        if (screenshot.SidebarState == "empty")
        {
            _viewModel.Transcript.Detach();
            return;
        }
        _viewModel.LoadTranscriptState(screenshot.TranscriptState);
        Shell.Attach(_viewModel);
        if (screenshot.TranscriptState == "lightbox")
        {
            Shell.Transcript.PreviewLightbox("m_1");
        }
    }

    /// <summary>Loads the 042 composer fixture state (idle, focus, attachments, popovers, running, palettes).</summary>
    private void ApplyComposerState(ScreenshotRequest screenshot)
    {
        if (screenshot.ComposerState is { Length: > 0 } state)
        {
            Shell.ComposerView.ApplyPreviewState(state);
        }
    }

    /// <summary>
    /// Spec 043 evidence: locale, header status dot, settings tab, right panel (changes/processes), diff modal, task card
    /// and pairing dialog. Every state is deterministic fixture data, so the gate never needs a server.
    /// </summary>
    private void ApplyPanelsAndSettings(ScreenshotRequest screenshot)
    {
        _viewModel.Language = screenshot.Locale;

        if (screenshot.HeaderStatus is { Length: > 0 } status)
        {
            _viewModel.HeaderStatusOverride = status;
            Shell.Header.Rebuild();
        }

        if (screenshot.SettingsTab is { Length: > 0 } tab)
        {
            Shell.Settings.LoadFixture();
            var target = tab switch
            {
                "models" => Features.Settings.SettingsDialog.SettingsTab.Models,
                "skills" => Features.Settings.SettingsDialog.SettingsTab.Skills,
                "mcp" => Features.Settings.SettingsDialog.SettingsTab.Mcp,
                "lsp" => Features.Settings.SettingsDialog.SettingsTab.Lsp,
                "shortcuts" => Features.Settings.SettingsDialog.SettingsTab.Shortcuts,
                "appearance" => Features.Settings.SettingsDialog.SettingsTab.Appearance,
                _ => Features.Settings.SettingsDialog.SettingsTab.General,
            };
            Shell.OpenSettings(target);
        }

        if (screenshot.PanelState is "changes" or "processes")
        {
            var changes = screenshot.PanelState == "processes"
                ? new List<Features.Panels.PanelChange>()
                : Fixture.PanelDiffs().Select(Features.Panels.PanelChange.FromDiff).ToList();
            var processes = screenshot.PanelState == "processes"
                ? new List<Features.Panels.PanelProcess>
                {
                    new("p_1", "dotnet run --project src/Sofia.Server", 30214, "running", null),
                    new("p_2", "npx vite --host 127.0.0.1", 40192, "running", null),
                    new("p_3", "npm run typecheck", 51220, "exited", 0),
                }
                : new List<Features.Panels.PanelProcess>();
            Shell.SetRightPanel(true, animate: false);
            Shell.Panel.LoadFixture(changes, processes);
        }

        if (screenshot.Diff) Shell.Panel.PreviewDiff(animate: false);

        if (screenshot.TaskCard)
        {
            Shell.Tasks.LoadFixture(Fixture.Tasks());
            _viewModel.TasksOpen = true;
            Shell.Header.Rebuild();
        }

        if (screenshot.Pairing) Shell.PreviewPairing();

        if (screenshot.SettingsInstall) Shell.Settings.PreviewInstallConfirm("csharp");

        if (screenshot.SettingsScroll > 0 && Shell.Settings.IsOpen)
        {
            Shell.Settings.PreviewScroll(screenshot.SettingsScroll);
        }
    }

    /// <summary>Starts the requested transcript animation at frame 0 (fold, hover, carousel).</summary>
    private void ApplyTranscriptFrame(ScreenshotRequest screenshot)
    {
        var messageId = screenshot.HoverMessage ?? "m_2";
        switch (screenshot.TranscriptFrame)
        {
            case "hover":
                Shell.Transcript.HoverMessage(messageId);
                break;
            case "fold":
                Shell.Transcript.PreviewToggleFold(messageId, 0);
                break;
            case "carousel":
                Shell.Transcript.PreviewCarouselStep(messageId, 1);
                break;
        }
    }

    private void PersistGeometry()
    {
        if (WindowState == WindowState.Normal)
        {
            LocalState.Current.Set(LocalState.WindowKey, new { Width, Height, X = Position.X, Y = Position.Y });
        }
    }

    private void RestoreGeometry()
    {
        var state = LocalState.Current.Get<WindowGeometry>(LocalState.WindowKey);
        if (state is null) return;
        if (state.Width >= MinWidth) Width = state.Width;
        if (state.Height >= MinHeight) Height = state.Height;
        if (state.X is not null && state.Y is not null)
        {
            Position = new PixelPoint(state.X.Value, state.Y.Value);
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
    }

    public sealed class WindowGeometry
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
    }
}

