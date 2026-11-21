using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Controls;

/// <summary>
/// Vector icon facade over the Lucide set, keyed exactly like <c>SofiaIcon.vue</c> (66 semantic names). The glyph is
/// drawn from the 24x24 viewBox scaled to <see cref="Size"/> (default 18 DIP, the web's <c>1.125rem</c>) with round
/// caps/joins and <see cref="StrokeWidth"/> 1.75, so the visible weight matches the SVG at every size. <c>loader</c>
/// renders <see cref="CoffeeSpinner"/> and names the web leaves unmapped (e.g. <c>circle</c>) draw nothing — deliberate
/// parity, not a bug.
/// </summary>
/// <remarks>
/// The web prop is called <c>name</c>; Avalonia reserves <c>Name</c> for <c>x:Name</c>, so the glyph key is
/// <see cref="Glyph"/>. Everything else (size/stroke/spin/paused/robot) keeps the web's names.
/// </remarks>
public class SofiaIcon : Decorator, IMotionParticipant
{
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<SofiaIcon, string?>(nameof(Glyph));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<SofiaIcon, double>(nameof(Size), 18);

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<SofiaIcon, double>(nameof(StrokeWidth), 1.75);

    public static readonly StyledProperty<bool> SpinProperty =
        AvaloniaProperty.Register<SofiaIcon, bool>(nameof(Spin));

    public static readonly StyledProperty<bool> PausedProperty =
        AvaloniaProperty.Register<SofiaIcon, bool>(nameof(Paused));

    public static readonly StyledProperty<bool> RobotProperty =
        AvaloniaProperty.Register<SofiaIcon, bool>(nameof(Robot));

    /// <summary>Inherited like CSS <c>currentColor</c>, so a parent Foreground colours the glyph.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<SofiaIcon>();

    private Geometry? _geometry;
    private CoffeeSpinner? _spinner;
    private double _angle;

    public SofiaIcon()
    {
        IsHitTestVisible = false;
        Rebuild();
    }

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public bool Spin
    {
        get => GetValue(SpinProperty);
        set => SetValue(SpinProperty, value);
    }

    public bool Paused
    {
        get => GetValue(PausedProperty);
        set => SetValue(PausedProperty, value);
    }

    public bool Robot
    {
        get => GetValue(RobotProperty);
        set => SetValue(RobotProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The geometry currently drawn, or null when the glyph has no Lucide path (the web renders nothing).</summary>
    public Geometry? Geometry => _geometry;

    /// <summary>The spinner shown for <c>loader</c>; null for regular glyphs.</summary>
    public CoffeeSpinner? Loader => _spinner;

    /// <summary>Current spin angle in degrees (frame-harness assertion for motion #8).</summary>
    public double SpinAngle => _angle;

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Resource lookup needs an Application/tree: the constructor runs before the control is hosted.
        if (_geometry is null && Glyph != "loader") Rebuild();
        if (Spin) MotionService.Current.Attach(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionService.Current.Detach(this);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlyphProperty || change.Property == SizeProperty ||
            change.Property == PausedProperty || change.Property == RobotProperty)
        {
            Rebuild();
        }
        else if (change.Property == SpinProperty)
        {
            if (Spin)
            {
                MotionService.Current.Attach(this);
                TickMotion(MotionService.Current.NowMs);
            }
            else
            {
                MotionService.Current.Detach(this);
                _angle = 0;
            }
            InvalidateVisual();
        }
        else if (change.Property == ForegroundProperty)
        {
            if (_spinner is not null) _spinner[!ForegroundProperty] = this[!ForegroundProperty];
            InvalidateVisual();
        }
        else if (change.Property == StrokeWidthProperty)
        {
            InvalidateVisual();
        }
    }

    public void TickMotion(double nowMs)
    {
        if (_spinner is not null || !Spin) return;
        var phase = nowMs % MotionTokens.IconSpin / MotionTokens.IconSpin;
        _angle = phase * 360;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_spinner is not null || _geometry is null || Size <= 0) return;

        var scale = Size / 24.0;
        var matrix = Spin && _angle != 0
            ? Rotation(_angle, 12, 12) * Matrix.CreateScale(scale, scale)
            : Matrix.CreateScale(scale, scale);
        using var _ = context.PushTransform(matrix);
        var pen = new Pen(Foreground ?? Brushes.White, StrokeWidth)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, pen, _geometry);
    }

    private void Rebuild()
    {
        Width = Size;
        Height = Size;

        if (Glyph == "loader")
        {
            if (_spinner is null)
            {
                _spinner = new CoffeeSpinner();
                Child = _spinner;
                MotionService.Current.Detach(this);
            }
            _spinner.Size = Size;
            _spinner.Paused = Paused;
            _spinner.Robot = Robot;
            _spinner[!ForegroundProperty] = this[!ForegroundProperty];
            _geometry = null;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        if (_spinner is not null)
        {
            Child = null;
            _spinner = null;
        }

        _geometry = Sofia.App.Theme.Icons.KeyFor(Glyph) is { } key &&
                    Application.Current?.TryFindResource(key, out var geometry) == true
            ? geometry as Geometry
            : null;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private static Matrix Rotation(double degrees, double cx, double cy)
    {
        var radians = degrees * Math.PI / 180;
        return Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(radians) * Matrix.CreateTranslation(cx, cy);
    }
}
