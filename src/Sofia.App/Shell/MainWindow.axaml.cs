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
            var frameActionApplied = false;
            ScreenshotHarness.FrameBegin = () =>
            {
                if (screenshot.HoverSession is { } row) Shell.Sidebar.HoverConversation(row);
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

