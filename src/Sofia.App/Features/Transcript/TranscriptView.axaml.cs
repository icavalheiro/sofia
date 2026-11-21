using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sofia.App.Controls;
using Sofia.App.Services;
using Sofia.App.Theme;
using Sofia.Contracts.Domain;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// The transcript (web <c>ChatMessages.vue</c>): the scroll host with the 1232px centred column, the 70-message window
/// with its "(…)" gap, auto-follow while parked at the bottom, scroll-to-message for search hits, the single working
/// spinner, the queued steer/follow-up rows, the lightbox and the retry confirmation on the shell's overlay ladder.
/// </summary>
public partial class TranscriptView : UserControl
{
    private const double BottomEpsilon = 4;

    private TranscriptViewModel? _vm;
    private Panel? _overlayHost;
    private Lightbox? _lightbox;
    private Border? _retryModal;
    private double _lastOffset;
    private double _lastExtent;
    private readonly Dictionary<string, MessageRow> _rows = new(StringComparer.Ordinal);

    public TranscriptView()
    {
        InitializeComponent();
        Scroller.ScrollChanged += OnScrollChanged;
        Scroller.SizeChanged += (_, _) => UpdateColumnWidth();
        Content.SizeChanged += (_, _) =>
        {
            if (Following) PostScrollToBottom();
        };
        Attachments = new AttachmentLoader();
        UpdateColumnWidth();
    }

    /// <summary>
    /// The content column is exactly <c>min(viewport, 1232px)</c>: a Stretch child inside the ScrollViewer would shrink
    /// to its widest row and get centred, which would move every row when a transcript happens to have narrow cards.
    /// </summary>
    private void UpdateColumnWidth()
    {
        var available = Math.Max(0, Scroller.Bounds.Width - 48); // 1.5rem padding on each side
        ColumnHost.Width = Math.Min(1232, available);
    }

    /// <summary>Attachment media cache (also used by hover previews and the harness).</summary>
    public AttachmentLoader Attachments { get; }

    /// <summary>Message rows of the last rebuild, in render order (screenshot harness and tests).</summary>
    public IReadOnlyDictionary<string, MessageRow> Rows => _rows;

    /// <summary>The scroll viewer (geometry assertions).</summary>
    public ScrollViewer ScrollerHost => Scroller;

    /// <summary>The 1232px content column.</summary>
    public Grid ColumnHostControl => ColumnHost;

    /// <summary>True while the transcript follows new content (parked at the bottom).</summary>
    public bool Following { get; private set; } = true;

    /// <summary>Width available to a row, used for the user bubble's <c>min(952px, 82%)</c> cap.</summary>
    public double ContentWidth => ColumnHost.Bounds.Width > 0 ? ColumnHost.Bounds.Width : 1232;

    /// <summary>Raised when a tool card's subagent link asks for the child conversation.</summary>
    public event Action<string>? SessionOpenRequested;

    public void UseOverlayHost(Panel host) => _overlayHost = host;

    /// <summary>Binds the transcript state; the view rebuilds on structural changes and follows growth while parked.</summary>
    public void Attach(TranscriptViewModel viewModel)
    {
        if (_vm is not null)
        {
            _vm.Changed -= Rebuild;
            _vm.Grew -= OnGrew;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = viewModel;
        _vm.Changed += Rebuild;
        _vm.Grew += OnGrew;
        _vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyBottomPadding();
        _vm.Recompute();
        Rebuild();
        Following = true;
        PostScrollToBottom();
    }

    /// <summary>Publishes the composer height (web <c>--sofia-composer-height</c>); the shell calls it on every resize.</summary>
    public void SetComposerHeight(double height)
    {
        if (_vm is null || height <= 0) return;
        _vm.ComposerHeight = height;
    }

    /// <summary>Marks the row a search hit asked for and scrolls to it once it is rendered.</summary>
    public void ScrollToMessage(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_rows.TryGetValue(messageId, out var row)) return;
            Following = false;
            row.BringIntoView();
        }, DispatcherPriority.Loaded);
    }

    public void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            Scroller.Offset = new Vector(Scroller.Offset.X, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
            _lastOffset = Scroller.Offset.Y;
            _lastExtent = Scroller.Extent.Height;
        }, DispatcherPriority.Loaded);
    }

    public void OpenSession(string sessionId) => SessionOpenRequested?.Invoke(sessionId);

    /// <summary>Starts the hover reveal of one row's actions (motion #4, frame harness).</summary>
    public bool HoverMessage(string messageId)
    {
        if (!_rows.TryGetValue(messageId, out var row)) return false;
        row.Hover();
        return true;
    }

    /// <summary>Opacity of one row's hover actions (frame assertions).</summary>
    public double RevealOpacity(string messageId) => _rows.TryGetValue(messageId, out var row) ? row.ActionsOpacity : 0;

    /// <summary>Opens the first image attachment of a message (screenshot evidence for the lightbox state).</summary>
    public bool PreviewLightbox(string messageId)
    {
        if (!_rows.TryGetValue(messageId, out var row)) return false;
        var attachment = row.ViewModel.ImageAttachments.FirstOrDefault();
        if (attachment is null) return false;
        OpenLightbox(attachment);
        return true;
    }

    /// <summary>Steps the question carousel of a message for the frame harness (motion #17).</summary>
    public bool PreviewCarouselStep(string messageId, int delta)
    {
        if (!_rows.TryGetValue(messageId, out var row) || row.Carousel is null) return false;
        row.Carousel.Step(delta);
        return true;
    }

    /// <summary>Toggles a fold of a message row for the frame harness (motion #14).</summary>
    public bool PreviewToggleFold(string messageId, int index = 0) =>
        _rows.TryGetValue(messageId, out var row) && row.ToggleFold(index);

    /// <summary>Escape closes the lightbox, then the retry confirmation; true when something was closed.</summary>
    public bool HandleEscape()
    {
        if (_lightbox is { IsOpen: true })
        {
            _lightbox.Close();
            return true;
        }
        if (_retryModal is not null)
        {
            CloseRetryConfirm();
            return true;
        }
        return false;
    }

    public void CloseOverlays()
    {
        _lightbox?.Close();
        CloseRetryConfirm();
    }

    /// <summary>Loads an image attachment synchronously when it is an app asset, otherwise asynchronously via the loader.</summary>
    public Bitmap? LoadAttachment(Attachment attachment) => Attachments.BitmapFor(attachment);

    public void OpenLightbox(Attachment attachment)
    {
        var host = _overlayHost;
        if (host is null) return;
        var bitmap = Attachments.BitmapFor(attachment);
        if (bitmap is null) return;
        _lightbox?.Close();
        _lightbox = new Lightbox(host, bitmap, attachment, DownloadAsync, () => _lightbox = null);
        _lightbox.Focus();
    }

    /// <summary>
    /// Shows the retry confirmation (web <c>.retry-overlay</c>, z2100): rewinding the session is destructive, so a
    /// failed message's Retry asks first.
    /// </summary>
    public void ShowRetryConfirm(MessageViewModel message)
    {
        var host = _overlayHost;
        if (host is null) return;
        CloseRetryConfirm();

        var title = TranscriptUi.Text(Localization.T("chat.retryTitle"), TranscriptUi.Rem(0.9375), "BrushPrimary", FontWeight.SemiBold);
        var body = TranscriptUi.Text(Localization.T("chat.retryBody"), TranscriptUi.Rem(0.8125), "BrushTextMuted");
        body.LineHeight = TranscriptUi.Rem(0.8125) * 1.5;

        var cancel = TranscriptUi.Pill(Localization.T("chat.cancel"), TranscriptUi.Rem(0.75), "BrushTextMuted",
            ThemeLookup.Brush("BrushSurface"), ThemeLookup.Brush("BrushBorderStrong"),
            new Thickness(TranscriptUi.Rem(0.8), TranscriptUi.Rem(0.3)));
        cancel.Click += (_, _) => CloseRetryConfirm();
        var retry = TranscriptUi.Pill(Localization.T("chat.retry"), TranscriptUi.Rem(0.75), "BrushPrimary",
            ThemeLookup.Brush("BrushSurface"), ThemeLookup.Brush("BrushPrimary"),
            new Thickness(TranscriptUi.Rem(0.8), TranscriptUi.Rem(0.3)));
        retry.Click += async (_, _) =>
        {
            CloseRetryConfirm();
            await message.RetryAsync();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = TranscriptUi.Rem(0.5), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, TranscriptUi.Rem(0.3), 0, 0) };
        actions.Children.Add(cancel);
        actions.Children.Add(retry);

        var panel = new Border
        {
            Width = Math.Min(400, Math.Max(240, host.Bounds.Width - 64)),
            Background = ThemeLookup.Brush("BrushSurface"),
            BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(TranscriptUi.Rem(1)),
            BoxShadow = ThemeLookup.BoxShadow("ShadowModal"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = TranscriptUi.Rem(0.6),
                Children = { title, body, actions },
            },
        };

        var scrim = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#73000000")),
            Child = panel,
            Width = Math.Max(1, host.Bounds.Width),
            Height = Math.Max(1, host.Bounds.Height),
        };
        scrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, scrim)) CloseRetryConfirm();
        };
        _retryModal = scrim;
        host.Children.Add(scrim);
        Canvas.SetLeft(scrim, 0);
        Canvas.SetTop(scrim, 0);
    }

    private void CloseRetryConfirm()
    {
        if (_retryModal is null) return;
        _overlayHost?.Children.Remove(_retryModal);
        _retryModal = null;
    }

    private async Task DownloadAsync(Attachment attachment)
    {
        var bytes = Attachments.BytesFor(attachment);
        if (bytes is null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = attachment.Name,
            DefaultExtension = Path.GetExtension(attachment.Name).TrimStart('.'),
            Title = Localization.T("img.download"),
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await stream.WriteAsync(bytes);
    }

    // --- scrolling -----------------------------------------------------------------------------------------

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var offset = Scroller.Offset.Y;
        var extent = Scroller.Extent.Height;
        var viewport = Scroller.Viewport.Height;
        var distance = extent - offset - viewport;
        if (distance <= BottomEpsilon) Following = true;
        // Only an upward move releases auto-follow when the content did not shrink (truncation clamps scrollTop down).
        else if (offset < _lastOffset && extent >= _lastExtent) Following = false;
        _lastOffset = offset;
        _lastExtent = extent;
    }

    private void OnGrew()
    {
        if (Following) PostScrollToBottom();
    }

    private void PostScrollToBottom() => Dispatcher.UIThread.Post(() =>
    {
        if (!Following) return;
        Scroller.Offset = new Vector(Scroller.Offset.X, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        _lastOffset = Scroller.Offset.Y;
        _lastExtent = Scroller.Extent.Height;
    }, DispatcherPriority.Loaded);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TranscriptViewModel.BottomPadding)) ApplyBottomPadding();
    }

    private void ApplyBottomPadding()
    {
        if (_vm is null) return;
        ColumnHost.Margin = new Thickness(TranscriptUi.Rem(1.5), TranscriptUi.Rem(1.25), TranscriptUi.Rem(1.5), _vm.BottomPadding);
    }

    // --- rendering -----------------------------------------------------------------------------------------

    private void Rebuild()
    {
        if (_vm is null) return;
        _rows.Clear();
        Content.Children.Clear();
        ApplyBottomPadding();

        if (!_vm.IsLoaded || (_vm.Messages.Count == 0 && !_vm.IsWorking && _vm.Pending.Count == 0))
        {
            Content.Children.Add(EmptyState());
            return;
        }

        foreach (var row in _vm.Rows)
        {
            if (row.IsGap)
            {
                Content.Children.Add(GapRow(row.HiddenCount));
                continue;
            }
            var view = new MessageRow(row.Message!, this);
            _rows[row.Message!.Id] = view;
            Content.Children.Add(view);
        }

        if (_vm.ShowSpinner) Content.Children.Add(Spinner(_vm.Waiting));

        foreach (var pending in _vm.Pending)
        {
            var view = new MessageRow(pending, this);
            _rows[pending.Id] = view;
            Content.Children.Add(view);
        }

        if (Following) PostScrollToBottom();
    }

    private static Control GapRow(int hidden)
    {
        var dots = TranscriptUi.Text("(...)", TranscriptUi.Rem(0.8125), "BrushTextFaint", wrap: false);
        dots.LetterSpacing = TranscriptUi.Rem(0.18);
        var label = TranscriptUi.Text(string.Format(Localization.T("chat.omitted"), hidden), TranscriptUi.Rem(0.75), "BrushTextFaint", wrap: false);
        label.IsVisible = false;
        ToolTip.SetTip(dots, string.Format(Localization.T("chat.omitted"), hidden));
        return new Border
        {
            Padding = new Thickness(0, TranscriptUi.Rem(0.15)),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { dots, label },
            },
        };
    }

    private static Control Spinner(bool waiting)
    {
        var icon = new SofiaIcon
        {
            Glyph = "loader",
            Size = TranscriptUi.Rem(1.6),
            Paused = waiting,
            Foreground = ThemeLookup.Brush("BrushPrimary"),
        };
        return new Border
        {
            Padding = new Thickness(0, TranscriptUi.Rem(1)),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { icon },
            },
        };
    }

    private static Control EmptyState()
    {
        var logo = new Border
        {
            Width = 88,
            Height = 88,
            CornerRadius = new CornerRadius(24),
            ClipToBounds = true,
            BorderBrush = ThemeLookup.Brush("BrushBorderStrong"),
            BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 0 0 6 #29A9744F, 0 18 40 0 #66000000"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, TranscriptUi.Rem(0.75)),
            Child = new Image { Source = LoadAsset("avares://Sofia.App/Assets/Logos/logo-128.png"), Stretch = Stretch.UniformToFill },
        };
        var title = TranscriptUi.Text(Localization.T("shell.emptyTitle"), TranscriptUi.Rem(1.375), "BrushText", FontWeight.SemiBold);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var body = TranscriptUi.Text(Localization.T("shell.emptyBody"), TranscriptUi.Rem(0.9375), "BrushTextMuted");
        body.TextAlignment = TextAlignment.Center;
        body.MaxWidth = 460;
        body.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            MinHeight = 360,
            Padding = new Thickness(0, TranscriptUi.Rem(3), 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = TranscriptUi.Rem(0.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                MaxWidth = 520,
                Children = { logo, title, body },
            },
        };
    }

    /// <summary>Loads one embedded asset PNG (logo, wallpaper) as a bitmap.</summary>
    public static Bitmap LoadAsset(string uri)
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri(uri));
        return new Bitmap(stream);
    }
}
