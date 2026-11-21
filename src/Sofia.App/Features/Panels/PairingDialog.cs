using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Sofia.App.Services;
using Sofia.App.Shell;

namespace Sofia.App.Features.Panels;

/// <summary>
/// PairingDialog.vue ported: mints a one-time pairing code (<c>POST /api/pair</c>, loopback only), counts the expiry down,
/// lists the addresses the server answers on (<c>GET /api/server</c>) and lets the user mint a fresh code. z2100, above
/// the settings modal, and instant on open like the web.
/// </summary>
public sealed class PairingDialog : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 9.6 };
    private readonly TextBlock _countdown = new();
    private readonly DispatcherTimer _ticker;
    private ShellViewModel? _vm;
    private string _code = "";
    private DateTime _expiresAt;
    private string? _error;
    private bool _loading;
    private bool _copied;
    private bool _localOnly;
    private List<string> _urls = [];

    public PairingDialog()
    {
        IsVisible = false;
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick());
        Content = BuildShell();
    }

    /// <summary>Opens the dialog and mints a code; the dialog closes itself through <see cref="Closed"/>.</summary>
    public event Action? Closed;

    public void Attach(ShellViewModel viewModel) => _vm = viewModel;

    /// <summary>Minted/displayed pairing code (tests and the fixture gate).</summary>
    public string Code => _code;

    /// <summary>True while the countdown label is mounted.</summary>
    public bool HasCountdown => _countdown.Parent is not null;

    /// <summary>Fixture/screenshot entry: a deterministic code, countdown and addresses without a server.</summary>
    public void PreviewFixture(string code = "SOFIA-7Q4K-P2XD", int secondsLeft = 96)
    {
        IsVisible = true;
        _loading = false;
        _error = null;
        _code = code;
        _copied = false;
        _expiresAt = DateTime.UtcNow.AddSeconds(secondsLeft);
        _urls = ["http://127.0.0.1:5199", "http://192.168.1.24:5199"];
        _localOnly = false;
        Rebuild();
        _ticker.Start();
    }

    public async void Open()
    {
        IsVisible = true;
        SoundService.Current.Play(SoundKey.Select);
        await GenerateAsync();
    }

    public void Close()
    {
        _ticker.Stop();
        IsVisible = false;
        _code = "";
        _urls = [];
        _localOnly = false;
        _error = null;
        _copied = false;
        Closed?.Invoke();
    }

    private Control BuildShell()
    {
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)),
            Padding = new Thickness(24),
        };
        var modal = new Border
        {
            Child = new StackPanel { Children = { Head(), _body } },
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Brush("BrushBorderStrong"),
            Background = Ui.Brush("BrushSurface"),
            BoxShadow = (BoxShadows?)Application.Current?.FindResource("ShadowModal") ?? default,
            ClipToBounds = true,
        };
        overlay.Child = modal;
        overlay.PointerPressed += (_, args) =>
        {
            if (ReferenceEquals(args.Source, overlay)) Close();
        };
        return overlay;
    }

    private Control Head()
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7.2, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Ui.Icon("link", 16, "BrushPrimary"));
        var label = Ui.Text(Localization.T("pair.title"), 14, "BrushPrimary", Avalonia.Media.FontWeight.SemiBold);
        title.Children.Add(label);
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14.4, 12, 14.4, 12) };
        head.Children.Add(title);
        var close = Ui.IconButton("x", 15.2, Localization.T("pair.close"), Close, 28);
        Grid.SetColumn(close, 1);
        head.Children.Add(close);
        return new Border
        {
            Child = head,
            BorderBrush = Ui.Brush("BrushBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private void Rebuild()
    {
        _body.Children.Clear();
        var intro = Ui.Text(Localization.T("pair.intro"), 12, "BrushTextMuted");
        intro.TextWrapping = TextWrapping.Wrap;
        _body.Children.Add(intro);

        if (_error is { Length: > 0 })
        {
            var error = Ui.Text(_error, 12, "BrushErr");
            error.TextWrapping = TextWrapping.Wrap;
            _body.Children.Add(error);
        }
        else if (_loading)
        {
            _body.Children.Add(Ui.Text(Localization.T("pair.loading"), 11, "BrushTextFaint"));
        }
        else if (_code.Length > 0)
        {
            _body.Children.Add(CodeBox());

            var expired = RemainingSeconds() <= 0;
            var expiry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5.6 };
            expiry.Children.Add(Ui.Icon(expired ? "alert" : "clock", 13.6, expired ? "BrushErr" : "BrushTextMuted"));
            _countdown.Text = expired
                ? Localization.T("pair.expired")
                : $"{Localization.T("pair.expiresIn")} {Countdown()}";
            _countdown.FontSize = 12;
            _countdown.Foreground = Ui.Brush(expired ? "BrushErr" : "BrushTextMuted");
            expiry.Children.Add(_countdown);
            _body.Children.Add(expiry);

            if (_urls.Count > 0)
            {
                var urls = new StackPanel { Spacing = 1.6 };
                var label = Ui.Text(Localization.T("pair.address").ToUpperInvariant(), 10, "BrushTextFaint", Avalonia.Media.FontWeight.SemiBold);
                label.LetterSpacing = 0.6;
                urls.Children.Add(label);
                foreach (var url in _urls)
                {
                    var text = Ui.Text(url, 10.5, "BrushTextMuted");
                    text.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
                    text.TextWrapping = TextWrapping.Wrap;
                    urls.Children.Add(text);
                }
                _body.Children.Add(urls);
            }

            if (_localOnly)
            {
                var local = Ui.Text(Localization.T("pair.localOnly"), 12, "BrushErr");
                local.TextWrapping = TextWrapping.Wrap;
                _body.Children.Add(local);
            }

            var hint = Ui.Text(Localization.T("pair.hint"), 10.6, "BrushTextFaint");
            hint.TextWrapping = TextWrapping.Wrap;
            _body.Children.Add(hint);
            _body.Children.Add(Ui.MiniButton(Localization.T("pair.newCode"), () => _ = GenerateAsync(), "refresh"));
        }

        _body.Margin = new Thickness(14.4);
    }

    private Control CodeBox()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        row.Children.Add(Ui.Text(_code, 14, "BrushText"));
        var copy = Ui.MiniButton(
            Localization.T(_copied ? "pair.copied" : "pair.copy"),
            () => _ = CopyAsync(), primary: true);
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        return new Border
        {
            Child = row,
            Padding = new Thickness(11.2, 9.6),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Brush("BrushBorderStrong"),
            Background = Ui.Brush("BrushSurface2"),
        };
    }

    private int RemainingSeconds() => (int)Math.Max(0, Math.Ceiling((_expiresAt - DateTime.UtcNow).TotalSeconds));

    private string Countdown()
    {
        var seconds = RemainingSeconds();
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    private void Tick()
    {
        if (!IsVisible) return;
        if (RemainingSeconds() <= 0)
        {
            _ticker.Stop();
            Rebuild();
            return;
        }
        // Only the label changes; rebuilding the body every tick would fight the countdown's own text.
        if (_countdown.Parent is not null)
            _countdown.Text = $"{Localization.T("pair.expiresIn")} {Countdown()}";
    }

    private async Task GenerateAsync()
    {
        if (_vm?.Connection.Backend is not { } backend)
        {
            _error = Localization.T("pair.error");
            Rebuild();
            return;
        }
        _loading = true;
        _error = null;
        _copied = false;
        _ticker.Stop();
        Rebuild();
        try
        {
            var pair = await backend.CreatePairingCodeAsync();
            _code = Json(pair, "code") ?? "";
            var expires = Number(pair, "expiresIn") ?? 0;
            _expiresAt = DateTime.UtcNow.AddSeconds(expires);
            if (_code.Length == 0)
            {
                _error = Localization.T("pair.error");
                return;
            }
            _ticker.Start();
            try
            {
                var info = await backend.ServerInfoAsync();
                if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("urls", out var urls)
                    && urls.ValueKind == JsonValueKind.Array)
                {
                    _urls = [.. urls.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String)
                        .Select(u => u.GetString() ?? "")];
                }
                _localOnly = info.ValueKind == JsonValueKind.Object
                    && info.TryGetProperty("networkReachable", out var reachable)
                    && reachable.ValueKind == JsonValueKind.False;
            }
            catch
            {
                _urls = [];
                _localOnly = false;
            }
        }
        catch (Exception ex)
        {
            _code = "";
            _error = string.IsNullOrWhiteSpace(ex.Message) ? Localization.T("pair.error") : ex.Message;
        }
        finally
        {
            _loading = false;
            Rebuild();
        }
    }

    private async Task CopyAsync()
    {
        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null) throw new InvalidOperationException();
            await clipboard.SetValueAsync(DataFormat.Text, _code);
            _copied = true;
            Rebuild();
        }
        catch
        {
            _error = Localization.T("pair.copyFailed");
            Rebuild();
        }
    }

    private static string? Json(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}
