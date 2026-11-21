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
            ScreenshotHarness.Run(this, screenshot);
            Close();
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

