using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sofia.App.Features.Transcript;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Features.Panels;

/// <summary>
/// DiffViewer.vue ported: the z300 modal with "Before" and "After" columns, numbered rows, +green/−red cells and the 45°
/// dashed stripe on the empty side of an added/removed line. Motion contract #18: the overlay fades in `.16s` and the
/// panel scales `.98 → 1` in `.16s`, driven from the shared clock so the frame harness can capture it.
/// </summary>
public sealed class DiffModal : UserControl, IMotionParticipant
{
    private readonly Border _overlay = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xB3, 0, 0, 0)),
        Padding = new Thickness(24),
    };
    private readonly Border _panel = new();
    private readonly StackPanel _rows = new();
    private readonly TextBlock _path = new();
    private readonly Border _badge = new();
    private readonly TextBlock _badgeText = new();
    private readonly TextBlock _additions = new();
    private readonly TextBlock _deletions = new();
    private Panel? _host;
    private double _start;
    private bool _animating;

    public DiffModal()
    {
        IsVisible = false;
        _panel.MaxWidth = 1100;
        _panel.MaxHeight = 760;
        _panel.CornerRadius = new CornerRadius(12);
        _panel.BorderThickness = new Thickness(1);
        _panel.BorderBrush = Ui.Brush("BrushBorderStrong");
        _panel.Background = Ui.Brush("BrushSurface");
        _panel.HorizontalAlignment = HorizontalAlignment.Stretch;
        _panel.VerticalAlignment = VerticalAlignment.Stretch;
        _panel.BoxShadow = (BoxShadows?)Application.Current?.FindResource("ShadowModal") ?? default;
        _panel.ClipToBounds = true;
        _panel.Child = BuildShell();
        _overlay.Child = _panel;
        _overlay.PointerPressed += (_, args) =>
        {
            if (!ReferenceEquals(args.Source, _overlay)) return;
            Close();
            args.Handled = true;
        };
        Content = _overlay;
    }

    /// <summary>The visible file (tests and the frame harness assert on it).</summary>
    public PanelChange? File { get; private set; }

    /// <summary>Installs the modal on the shell's overlay ladder (a z300 layer of the shared canvas).</summary>
    public void Host(Panel host)
    {
        _host = host;
        host.Children.Add(this);
    }

    public void Open(PanelChange file, bool animate = true)
    {
        File = file;
        _path.Text = file.Path;
        _badgeText.Text = file.Status.ToUpperInvariant();
        var (badgeBrush, badgeSoft) = file.Status switch
        {
            "A" => ("BrushOk", "BrushOkSoft"),
            "D" => ("BrushErr", "BrushErrSoft"),
            _ => ("BrushWarn", "BrushWarnSoft"),
        };
        _badge.Background = Ui.Brush(badgeSoft);
        _badgeText.Foreground = Ui.Brush(badgeBrush);
        _additions.Text = $"+{file.Additions}";
        _deletions.Text = $"-{file.Deletions}";
        BuildRows(file.Rows);

        IsVisible = true;
        if (_host is not null) _host.IsVisible = true;
        if (!animate)
        {
            _overlay.Opacity = 1;
            _panel.RenderTransform = new ScaleTransform(1, 1);
            _panel.RenderTransformOrigin = RelativePoint.Center;
            return;
        }
        _overlay.Opacity = 0;
        _panel.RenderTransform = new ScaleTransform(0.98, 0.98);
        _panel.RenderTransformOrigin = RelativePoint.Center;
        _start = MotionService.Current.NowMs;
        _animating = true;
        MotionService.Current.Attach(this);
        TickMotion(_start);
    }

    /// <summary>Escape closes the modal; returns true when it was open.</summary>
    public bool HandleEscape()
    {
        if (!IsVisible) return false;
        Close();
        return true;
    }

    public void Close()
    {
        _animating = false;
        MotionService.Current.Detach(this);
        IsVisible = false;
        File = null;
        if (_host is not null) _host.IsVisible = false;
    }

    /// <summary>Motion #18: opacity 0→1 and scale .98→1 over .16s ease.</summary>
    public void TickMotion(double nowMs)
    {
        if (!_animating) return;
        var duration = Math.Max(1, MotionTokens.Modal.TotalMilliseconds);
        var progress = Math.Clamp((nowMs - _start) / duration, 0, 1);
        var eased = MotionTokens.Standard.Ease(progress);
        _overlay.Opacity = eased;
        var scale = 0.98 + 0.02 * eased;
        _panel.RenderTransform = new ScaleTransform(scale, scale);
        if (progress < 1) return;
        _animating = false;
        MotionService.Current.Detach(this);
    }

    /// <summary>Current scale, so the harness can assert the stop-motion values (0.98 → 1).</summary>
    public double Scale => _panel.RenderTransform is ScaleTransform scale ? scale.ScaleX : 1;

    private Control BuildShell()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };

        // head
        var head = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12,
        };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _badgeText.FontSize = 11.2;
        _badgeText.FontWeight = Avalonia.Media.FontWeight.Bold;
        _badge.Child = _badgeText;
        _badge.Width = 22;
        _badge.Height = 22;
        _badge.CornerRadius = new CornerRadius(6);
        title.Children.Add(_badge);
        _path.FontSize = 13;
        _path.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
        _path.Foreground = Ui.Brush("BrushText");
        _path.TextTrimming = TextTrimming.CharacterEllipsis;
        _path.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(_path);
        head.Children.Add(title);

        var side = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _additions.FontSize = 12;
        _additions.FontWeight = Avalonia.Media.FontWeight.SemiBold;
        _additions.Foreground = Ui.Brush("BrushOk");
        _deletions.FontSize = 12;
        _deletions.FontWeight = Avalonia.Media.FontWeight.SemiBold;
        _deletions.Foreground = Ui.Brush("BrushErr");
        side.Children.Add(_additions);
        side.Children.Add(_deletions);
        var close = Ui.IconButton("x", 16, Localization.T("img.close"), Close, 30);
        side.Children.Add(close);
        Grid.SetColumn(side, 1);
        head.Children.Add(side);
        root.Children.Add(new Border
        {
            Child = head,
            Padding = new Thickness(14.4, 11.2),
            BorderBrush = Ui.Brush("BrushBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        });

        // column headers
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Height = 27.2 };
        columns.Children.Add(ColumnLabel(Localization.T("diff.before"), 0));
        columns.Children.Add(ColumnLabel(Localization.T("diff.after"), 1));
        var columnsHead = new Border
        {
            Child = columns,
            BorderBrush = Ui.Brush("BrushBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Ui.Brush("BrushSurface2"),
        };
        Grid.SetRow(columnsHead, 1);
        root.Children.Add(columnsHead);

        // rows
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _rows,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        return root;
    }

    private static Control ColumnLabel(string text, int column)
    {
        var label = Ui.Text(text.ToUpperInvariant(), 11, "BrushTextFaint", Avalonia.Media.FontWeight.SemiBold);
        label.Margin = new Thickness(11.2, 0, 11.2, 0);
        label.LetterSpacing = 0.55;
        Grid.SetColumn(label, column);
        return label;
    }

    private void BuildRows(IReadOnlyList<DiffRow> rows)
    {
        _rows.Children.Clear();
        foreach (var row in rows)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            var left = row.Kind == DiffRowKind.Add ? null : row;
            var right = row.Kind == DiffRowKind.Delete ? null : row;
            line.Children.Add(Cell(left, odd: true));
            var rightCell = Cell(right, odd: false);
            Grid.SetColumn(rightCell, 1);
            line.Children.Add(rightCell);
            _rows.Children.Add(line);
        }
    }

    private Control Cell(DiffRow? row, bool odd)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("44,18,*"),
            Background = row is null ? EmptyStripe() : Ui.Brush("BrushTransparent"),
            MinHeight = 22,
        };
        if (row is not null)
        {
            var background = row.Kind switch
            {
                DiffRowKind.Add => "#2474A672",
                DiffRowKind.Delete => "#24C9705F",
                _ => null,
            };
            if (background is not null) grid.Background = SolidColorBrush.Parse(background);
        }

        var number = row is null ? "" : (odd ? row.OldNumber : row.NewNumber)?.ToString() ?? "";
        var numberText = Ui.Text(number, 11.5, "BrushTextFaint");
        numberText.HorizontalAlignment = HorizontalAlignment.Right;
        numberText.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
        numberText.Margin = new Thickness(0, 1.6, 8, 1.6);
        numberText.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(numberText);

        var sign = row?.Kind switch
        {
            DiffRowKind.Add => "+",
            DiffRowKind.Delete => "-",
            _ => "",
        } ?? "";
        var signText = Ui.Text(sign, 12.5, row?.Kind == DiffRowKind.Add ? "BrushOk" : "BrushErr", Avalonia.Media.FontWeight.Bold);
        signText.HorizontalAlignment = HorizontalAlignment.Center;
        signText.VerticalAlignment = VerticalAlignment.Top;
        signText.Margin = new Thickness(0, 1.6, 0, 0);
        Grid.SetColumn(signText, 1);
        grid.Children.Add(signText);

        var text = Ui.Text(row?.Text ?? "", 12.48, "BrushTextMuted");
        text.FontFamily = (FontFamily?)Application.Current?.FindResource("FontFamilyMono");
        text.TextWrapping = TextWrapping.Wrap;
        text.LineHeight = 18.72;
        text.Margin = new Thickness(9.6, 1.6, 9.6, 1.6);
        text.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);

        var cell = new Border
        {
            Child = grid,
            BorderThickness = odd ? new Thickness(0, 0, 1, 1) : new Thickness(0, 0, 0, 1),
            BorderBrush = BorderHairline(),
        };
        return cell;
    }

    private static IBrush BorderHairline() => new SolidColorBrush(Color.FromArgb(0x72, 0x33, 0x33, 0x3A));

    /// <summary>
    /// `repeating-linear-gradient(45deg, transparent 0 6px, border 6px 12px)`: a 17×17 tiled bitmap drawn once, since
    /// Avalonia has no repeating gradient brush.
    /// </summary>
    private static IBrush EmptyStripe()
    {
        var tile = new RenderTargetBitmap(new PixelSize(17, 17), new Vector(96, 96));
        using (var context = tile.CreateDrawingContext())
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0x33, 0x33, 0x3A)), 1.6);
            // 45° lines: from (0,17) to (17,0) and the two wrapped halves.
            context.DrawLine(pen, new Point(-1, 18), new Point(18, -1));
            context.DrawLine(pen, new Point(-1, 1), new Point(1, -1));
            context.DrawLine(pen, new Point(16, 18), new Point(18, 16));
        }
        return new ImageBrush(tile)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            DestinationRect = new RelativeRect(0, 0, 17, 17, RelativeUnit.Absolute),
        };
    }
}
