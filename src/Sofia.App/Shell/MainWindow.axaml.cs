using Avalonia;
using Avalonia.Controls;
using Sofia.App.Services;

namespace Sofia.App.Shell;

/// <summary>
/// Desktop window: wraps the head-agnostic <see cref="ShellHost"/>, persists its geometry and runs the
/// screenshot/frame harness when the head asked for it. Backend bootstrap and the gate live in the host so the
/// browser head behaves identically.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow() : this(HeadOptions.Current.Fixture)
    {
    }

    public MainWindow(bool fixture)
    {
        InitializeComponent();
        // The host owns the shell view model, the shell frame and the backend bootstrap; building it in code (not in
        // XAML) lets the caller choose fixture data before the view model is created.
        Host = new ShellHost(fixture);
        Content = Host;
        Opened += async (_, _) => await OnOpenedAsync();
        Closing += (_, _) => PersistGeometry();
        RestoreGeometry();
    }

    public ShellHost Host { get; }

    public ShellView ShellView => Host.Shell;

    public ShellViewModel ViewModel => Host.ViewModel;

    private async Task OnOpenedAsync()
    {
        var screenshot = HeadOptions.Current.Screenshot;
        if (screenshot is not null)
        {
            await Task.Delay(250);
            ScreenshotHarness.Apply(screenshot);
            Host.Shell.Attach(Host.ViewModel);
            ApplySidebarState(screenshot);
            ApplyTranscriptState(screenshot);
            Host.Shell.Attach(Host.ViewModel);
            ApplyComposerState(screenshot);
            ApplyPanelsAndSettings(screenshot);
            var frameActionApplied = false;
            var composerFrameApplied = false;
            ScreenshotHarness.FrameBegin = () =>
            {
                if (screenshot.HoverSession is { } row) Host.Shell.Sidebar.HoverConversation(row);
                switch (screenshot.PanelFrame)
                {
                    case "spin":
                        Host.Shell.Panel.PreviewSpinning(true);
                        break;
                    case "diff":
                        Host.Shell.Panel.PreviewDiff();
                        break;
                }
                if (screenshot.PanelOpen)
                {
                    Host.Shell.SetRightPanel(false, animate: false);
                    Host.Shell.SetRightPanel(true, animate: true);
                }
                if (!composerFrameApplied)
                {
                    composerFrameApplied = true;
                    switch (screenshot.ComposerFrame)
                    {
                        case "press":
                            Host.Shell.ComposerView.BeginPressFrame();
                            break;
                        case "chip":
                            Host.Shell.ComposerView.BeginChipHoverFrame();
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
            Host.Shell.Transcript.CloseOverlays();
            Close();
            // The desktop head is a one-shot capture: shut the lifetime down so the CLI process exits (a live
            // dispatcher timer in the store would otherwise keep the window's dispatcher alive).
            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
            return;
        }
    }

    /// <summary>Fixture state for the 040 screenshot gate: empty account, collapsed working row or open quick prompt.</summary>
    private void ApplySidebarState(ScreenshotRequest screenshot)
    {
        var viewModel = Host.ViewModel;
        switch (screenshot.SidebarState)
        {
            case "empty":
                viewModel.LoadFixture(empty: true);
                break;
            case "search":
                viewModel.Sidebar.PreviewSearch("working", Fixture.SearchHits());
                break;
            case "settings":
                Host.Shell.Sidebar.PreviewSettingsMenu(viewModel.SidebarStore.Workspaces.FirstOrDefault()?.Id ?? "");
                break;
            case "delete":
                Host.Shell.Sidebar.PreviewDelete(viewModel.SidebarStore.Workspaces.FirstOrDefault()?.Id ?? "");
                break;
            case "working":
                foreach (var workspace in viewModel.Store.Workspaces)
                {
                    if ((workspace.Conversations ?? []).Any(s =>
                            viewModel.SidebarStore.Badges.GetValueOrDefault(s.Id) is "working" or "waiting"))
                    {
                        workspace.Expanded = false;
                    }
                }
                viewModel.SidebarStore.Rebuild();
                break;
        }
    }

    /// <summary>Loads the 041 transcript fixture state (and opens the lightbox for the lightbox state).</summary>
    private void ApplyTranscriptState(ScreenshotRequest screenshot)
    {
        if (screenshot.SidebarState == "empty")
        {
            Host.ViewModel.Transcript.Detach();
            return;
        }
        Host.ViewModel.LoadTranscriptState(screenshot.TranscriptState);
        Host.Shell.Attach(Host.ViewModel);
        if (screenshot.TranscriptState == "lightbox")
        {
            Host.Shell.Transcript.PreviewLightbox("m_1");
        }
    }

    /// <summary>Loads the 042 composer fixture state (idle, focus, attachments, popovers, running, palettes).</summary>
    private void ApplyComposerState(ScreenshotRequest screenshot)
    {
        if (screenshot.ComposerState is { Length: > 0 } state)
        {
            Host.Shell.ComposerView.ApplyPreviewState(state);
        }
    }

    /// <summary>
    /// Spec 043 evidence: locale, header status dot, settings tab, right panel (changes/processes), diff modal, task card
    /// and pairing dialog. Every state is deterministic fixture data, so the gate never needs a server.
    /// </summary>
    private void ApplyPanelsAndSettings(ScreenshotRequest screenshot)
    {
        var viewModel = Host.ViewModel;
        viewModel.Language = screenshot.Locale;

        if (screenshot.HeaderStatus is { Length: > 0 } status)
        {
            viewModel.HeaderStatusOverride = status;
            Host.Shell.Header.Rebuild();
        }

        if (screenshot.SettingsTab is { Length: > 0 } tab)
        {
            Host.Shell.Settings.LoadFixture();
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
            Host.Shell.OpenSettings(target);
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
            Host.Shell.SetRightPanel(true, animate: false);
            Host.Shell.Panel.LoadFixture(changes, processes);
        }

        if (screenshot.Diff) Host.Shell.Panel.PreviewDiff(animate: false);

        if (screenshot.TaskCard)
        {
            Host.Shell.Tasks.LoadFixture(Fixture.Tasks());
            viewModel.TasksOpen = true;
            Host.Shell.Header.Rebuild();
        }

        if (screenshot.Pairing) Host.Shell.PreviewPairing();

        if (screenshot.SettingsInstall) Host.Shell.Settings.PreviewInstallConfirm("csharp");

        if (screenshot.SettingsScroll > 0 && Host.Shell.Settings.IsOpen)
        {
            Host.Shell.Settings.PreviewScroll(screenshot.SettingsScroll);
        }
    }

    /// <summary>Starts the requested transcript animation at frame 0 (fold, hover, carousel).</summary>
    private void ApplyTranscriptFrame(ScreenshotRequest screenshot)
    {
        var messageId = screenshot.HoverMessage ?? "m_2";
        switch (screenshot.TranscriptFrame)
        {
            case "hover":
                Host.Shell.Transcript.HoverMessage(messageId);
                break;
            case "fold":
                Host.Shell.Transcript.PreviewToggleFold(messageId, 0);
                break;
            case "carousel":
                Host.Shell.Transcript.PreviewCarouselStep(messageId, 1);
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
