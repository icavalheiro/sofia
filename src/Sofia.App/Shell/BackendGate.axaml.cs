using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Shell;

/// <summary>
/// Connect screen (ported from <c>BackendGate.vue</c>): probe state with a spinner, the connection log, the server URL and
/// access token fields, pairing and retry. It replaces the shell until a backend answers; in fixture mode it is never
/// shown.
/// </summary>
public partial class BackendGate : UserControl
{
    private BackendConnection? _connection;
    private Func<Task>? _retry;

    public BackendGate()
    {
        InitializeComponent();
        using (var logo = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Sofia.App/Assets/Logos/logo-128.png")))
        {
            Logo.Source = new Avalonia.Media.Imaging.Bitmap(logo);
        }
        RetryButton.Click += async (_, _) =>
        {
            SoundService.Current.Play(SoundKey.Click);
            if (_connection is not null) _connection.Log.Add("retrying...");
            // A manually typed URL/token wins over the stored connection on the next probe.
            if (_connection is not null)
            {
                if (!string.IsNullOrWhiteSpace(UrlBox.Text)) _connection.ServerUrl = UrlBox.Text!.Trim();
                _connection.AccessToken = string.IsNullOrWhiteSpace(TokenBox.Text) ? null : TokenBox.Text!.Trim();
                _connection.PersistConnection();
            }
            if (_retry is not null) await _retry();
        };
        PairButton.Click += async (_, _) =>
        {
            if (_connection is null) return;
            SoundService.Current.Play(SoundKey.Click);
            PairStatus.Text = "pairing...";
            var ok = await _connection.PairAsync(CodeBox.Text ?? "");
            PairStatus.Text = ok ? "paired" : "invalid or expired code";
            if (ok && _retry is not null)
            {
                // The paired token is stored: re-resolve so every request carries it, then load the shell.
                await _retry();
            }
        };
    }

    /// <summary>Binds the gate to a connection and retry action.</summary>
    public void Attach(BackendConnection connection, Func<Task> retry)
    {
        _connection = connection;
        _retry = retry;
        connection.PropertyChanged += (_, _) => Refresh();
        connection.Log.CollectionChanged += (_, _) => RefreshLog();
        UrlBox.Text = connection.ServerUrl;
        TokenBox.Text = connection.AccessToken ?? "";
        Refresh();
        RefreshLog();
    }

    /// <summary>Phase line + spinner state; also called by tests.</summary>
    public void Refresh()
    {
        if (_connection is null) return;
        var (text, color, paused) = _connection.Phase switch
        {
            BackendPhase.Probing => (Localization.T("gate.probing"), "BrushPrimary", false),
            BackendPhase.Ready => (Localization.T("gate.ready"), "BrushOk", true),
            BackendPhase.Pairing => (Localization.T("gate.pairingRequired"), "BrushWarn", true),
            BackendPhase.Failed => (_connection.LastError ?? Localization.T("gate.failed"), "BrushErr", true),
            _ => (Localization.T("gate.title"), "BrushTextMuted", true),
        };
        PhaseText.Text = text;
        PhaseText.Foreground = Brush(color);
        PhaseIcon.Foreground = Brush(color);
        PhaseIcon.Paused = paused;
        RetryButton.IsVisible = _connection.Phase == BackendPhase.Failed;
        PairSection.IsVisible = _connection.Phase is BackendPhase.Ready or BackendPhase.Pairing;
    }

    private void RefreshLog()
    {
        if (_connection is null) return;
        LogList.Children.Clear();
        foreach (var line in _connection.Log)
        {
            LogList.Children.Add(new TextBlock
            {
                Text = line,
                Classes = { "mono" },
                FontSize = 11,
                Foreground = Brush("BrushTextFaint"),
            });
        }
        LogScroll.ScrollToEnd();
    }

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : Brushes.Gray;
}


