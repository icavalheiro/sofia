using Avalonia.Controls;
using Avalonia.Interactivity;
using Sofia.Desktop.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sofia.Desktop;

public partial class MainWindow : Window
{
    private readonly ServerProcess _server = new();
    private CancellationTokenSource? _cts;
    private bool _ownsServer;

    public MainWindow()
    {
        InitializeComponent();
        Opened += async (_, _) => await StartAsync();
        Closing += (_, _) => Shutdown();
    }

    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        SetStatus("Starting server…");
        ShowLoader(true);
        ShowError(false);
        try
        {
            Console.WriteLine("[sofia.desktop] Starting server…");
            await _server.StartAsync(_cts.Token);
            _ownsServer = _server.IsOwned;
            var status = _server.Status == "reused" ? "Reusing running server…" : "Server ready. Loading UI…";
            Console.WriteLine("[sofia.desktop] " + status);
            SetStatus(status);
            WebView.IsVisible = true;
            WebView.NavigationCompleted += (_, e) =>
            {
                Console.WriteLine("[sofia.desktop] WebView navigation completed. Success=" + e.IsSuccess);
                if (!e.IsSuccess)
                {
                    ShowError(true);
                    ErrorText.Text = "Failed to load Sofia UI.";
                }
            };
            ShowLoader(false);
            Console.WriteLine("[sofia.desktop] Loading UI from " + ServerProcess.BaseUrl);
            WebView.Source = new Uri(ServerProcess.BaseUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[sofia.desktop] ERROR: " + ex);
            ShowLoader(false);
            ShowError(true);
            ErrorText.Text = ex.Message;
        }
    }

    private void Shutdown()
    {
        try { _cts?.Cancel(); } catch { }
        if (_ownsServer) _server.Stop();
        _server.Dispose();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowLoader(bool show) => Loader.IsVisible = show;
    private void ShowError(bool show) => ErrorPanel.IsVisible = show;

    private async void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        await StartAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        Shutdown();
        base.OnClosed(e);
    }
}
