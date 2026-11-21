using Avalonia.Controls;
using Sofia.App.Services;

namespace Sofia.App.Shell;

/// <summary>
/// Head-agnostic shell host: owns the <see cref="ShellViewModel"/>, the <see cref="ShellView"/> frame and the
/// backend bootstrap (gate → probe → load → event stream). The desktop window wraps it; the browser (WASM) head
/// installs it as the single view (spec 044). No platform API is used here, so both heads share every behaviour.
/// </summary>
public sealed class ShellHost : UserControl
{
    private readonly ShellViewModel _viewModel;
    private CancellationTokenSource? _listen;
    private bool _started;

    public ShellHost() : this(HeadOptions.Current.Fixture)
    {
    }

    public ShellHost(bool fixture)
    {
        _viewModel = new ShellViewModel(fixture);
        LocalPreferences.Current.Apply();
        _viewModel.Language = Localization.Current;
        Shell = new ShellView();
        Content = Shell;
        Shell.Attach(_viewModel);
        _viewModel.RightPanelOpen = HeadOptions.Current.Screenshot?.RightPanel ?? false;
        _viewModel.Theme.CompactSidebar = HeadOptions.Current.Screenshot?.CompactSidebar ?? false;
        _viewModel.Theme.ApplyAll();
        Shell.Attach(_viewModel);
        AttachedToVisualTree += async (_, _) => await StartAsync();
    }

    public ShellView Shell { get; }

    public ShellViewModel ViewModel => _viewModel;

    /// <summary>Run bootstraps when fixture data is off; the screenshot harness (desktop only) drives the view directly.</summary>
    private async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        if (_viewModel.IsFixture) return;
        await ConnectAsync(HeadOptions.Current.Resolver);
    }

    /// <summary>
    /// Shows the connect gate and probes: on success the gate hides, the workspace tree and the open conversation load
    /// and the SignalR event stream starts. Called by the gate itself to retry after pairing.
    /// </summary>
    public async Task ConnectAsync(IBackendResolver? resolver)
    {
        if (resolver is null)
        {
            Shell.ShowGate(_viewModel.Connection, () => ConnectAsync(resolver));
            return;
        }

        Shell.ShowGate(_viewModel.Connection, () => ConnectAsync(resolver));
        await _viewModel.Connection.ConnectAsync(resolver);
        if (_viewModel.Connection.Phase == BackendPhase.Pairing)
        {
            // The gate stays up with the pairing section; its Pair button retries through this same path.
            return;
        }
        if (!_viewModel.Connection.IsReady || _viewModel.Connection.Backend is null) return;

        Shell.HideGate();
        await Task.Delay(120);
        await _viewModel.LoadAsync(_viewModel.Connection.Backend);
        Shell.Attach(_viewModel);

        _listen?.Cancel();
        _listen = new CancellationTokenSource();
        _ = _viewModel.Store.ListenAsync(_viewModel.Connection.Backend, _listen.Token);
    }
}
