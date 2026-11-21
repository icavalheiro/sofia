using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Controls;

/// <summary>
/// The web's <c>CoffeeSpinner.vue</c>, animation for animation: a mug with three rising steam lines at 18px and up, a
/// coffee bean that spins and pauses below 16px, and the eight-keyframe robot shake for subagents. The 24x24 Lucide box
/// is scaled to <see cref="Size"/> (so strokes scale like the SVG's viewBox) and every animation value is derived from
/// the shared motion clock, which is what makes the frame-sequence captures reproducible.
/// </summary>
public class CoffeeSpinner : Control, IMotionParticipant
{
    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<CoffeeSpinner, double>(nameof(Size), 18);

    public static readonly StyledProperty<bool> PausedProperty =
        AvaloniaProperty.Register<CoffeeSpinner, bool>(nameof(Paused));

    public static readonly StyledProperty<bool> RobotProperty =
        AvaloniaProperty.Register<CoffeeSpinner, bool>(nameof(Robot));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<CoffeeSpinner>();

    private const string Cup = "M4 11H18V16A5 5 0 0 1 13 21H9A5 5 0 0 1 4 16Z";
    private const string Handle = "M18 12.5H19.5A2.5 2.5 0 0 1 19.5 17.5H17.6";

    private static readonly string[] SteamStarts =
    [
        "M8 8C6.5 6.5 9.5 5.5 8 4C7.4 3.4 7.4 2.6 8 2",
        "M12 8C10.5 6.5 13.5 5.5 12 4C11.4 3.4 11.4 2.6 12 2",
        "M16 8C14.5 6.5 17.5 5.5 16 4C15.4 3.4 15.4 2.6 16 2",
    ];

    private static readonly string[] RobotPaths =
    [
        "M12 8V4H8",
        "M4 8H20A2 2 0 0 1 22 10V18A2 2 0 0 1 20 20H4A2 2 0 0 1 2 18V10A2 2 0 0 1 4 8Z",
        "M2 14h2M20 14h2M15 13v2M9 13v2",
    ];

    private static readonly string BeanCrease = "M9.5 5.5C14 9 10 15 14.5 18.5";

    /// <summary>Robot shake keyframes from the CSS: translate (%) + rotate (deg) at every eighth of the cycle.</summary>
    private static readonly (double Dx, double Dy, double Rotate)[] RobotFrames =
    [
        (0, 0, 0),
        (-0.08, 0.06, -6),
        (0.07, -0.08, 5),
        (-0.06, -0.06, -4),
        (0.08, 0.05, 7),
        (-0.07, 0.08, -7),
        (0.06, -0.05, 4),
        (-0.05, 0.04, -5),
        (0, 0, 0),
    ];

    private static readonly Geometry CupGeometry = Geometry.Parse(Cup);
    private static readonly Geometry HandleGeometry = Geometry.Parse(Handle);
    private static readonly Geometry[] SteamGeometry = SteamStarts.Select(Geometry.Parse).ToArray();
    private static readonly Geometry[] RobotGeometry = RobotPaths.Select(Geometry.Parse).ToArray();
    private static readonly Geometry BeanGeometry = new EllipseGeometry(new Rect(5.5, 2.5, 13, 19));
    private static readonly Geometry BeanCreaseGeometry = Geometry.Parse(BeanCrease);

    private double _steamOpacity1;
    private double _steamOpacity2;
    private double _steamOpacity3;

    public CoffeeSpinner()
    {
        Width = Size;
        Height = Size;
        IsHitTestVisible = false;
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
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

    /// <summary><c>robot</c>, <c>bean</c> or <c>mug</c>, exactly the three variants of the web component.</summary>
    public string Variant => Robot ? "robot" : Size < 16 ? "bean" : "mug";

    /// <summary>Steam opacities of the three vapor paths at the current clock reading (frame-harness assertions).</summary>
    public (double First, double Second, double Third) SteamOpacities => (_steamOpacity1, _steamOpacity2, _steamOpacity3);

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MotionService.Current.Attach(this);
        TickMotion(MotionService.Current.NowMs);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionService.Current.Detach(this);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SizeProperty)
        {
            Width = Size;
            Height = Size;
            InvalidateMeasure();
            InvalidateVisual();
        }
        else if (change.Property == PausedProperty || change.Property == RobotProperty || change.Property == ForegroundProperty)
        {
            InvalidateVisual();
        }
    }

    public void TickMotion(double nowMs)
    {
        if (Paused) return;
        if (Variant == "mug")
        {
            var duration = MotionService.Current.ReducedMotion ? MotionTokens.SteamReduced : MotionTokens.Steam;
            _steamOpacity1 = SteamOpacity(nowMs, 0, duration);
            _steamOpacity2 = SteamOpacity(nowMs, 1, duration);
            _steamOpacity3 = SteamOpacity(nowMs, 2, duration);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Size <= 0) return;

        var scale = Size / 24.0;
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale));
        var width = StrokeWidth();

        switch (Variant)
        {
            case "robot":
                DrawRobot(context, width, now: MotionService.Current.NowMs);
                break;
            case "bean":
                DrawBean(context, width, now: MotionService.Current.NowMs);
                break;
            default:
                DrawMug(context, width, now: MotionService.Current.NowMs);
                break;
        }
    }

    private void DrawMug(DrawingContext context, double strokeWidth, double now)
    {
        var duration = MotionService.Current.ReducedMotion ? MotionTokens.SteamReduced : MotionTokens.Steam;
        for (var i = 0; i < SteamGeometry.Length; i++)
        {
            var (offset, opacity, dx, dy) = SteamFrame(now, i, duration);
            using (context.PushOpacity(Paused ? 0.55 : opacity))
            using (context.PushTransform(Matrix.CreateTranslation(dx, dy)))
            {
                var pen = new Pen(Foreground ?? Brushes.White, 1.4)
                {
                    LineCap = PenLineCap.Round,
                    LineJoin = PenLineJoin.Round,
                };
                if (!Paused) pen.DashStyle = new DashStyle([6, 10], offset);
                context.DrawGeometry(null, pen, SteamGeometry[i]);
            }
        }

        var solid = new Pen(Foreground ?? Brushes.White, strokeWidth)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, solid, CupGeometry);
        context.DrawGeometry(null, solid, HandleGeometry);
    }

    private void DrawBean(DrawingContext context, double strokeWidth, double now)
    {
        var phase = now % MotionTokens.BeanSpin / MotionTokens.BeanSpin;
        var angle = Paused ? 0 : 360 * MotionTokens.InOut.Ease(Math.Min(phase / 0.45, 1));
        using var __ = context.PushTransform(Rotation(35 + angle, 12, 12));
        var pen = new Pen(Foreground ?? Brushes.White, 2.2)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        context.DrawGeometry(null, pen, BeanGeometry);
        context.DrawGeometry(null, pen, BeanCreaseGeometry);
    }

    private void DrawRobot(DrawingContext context, double strokeWidth, double now)
    {
        var phase = Paused ? 0 : now % MotionTokens.RobotShake / MotionTokens.RobotShake;
        var index = Math.Min((int)(phase * (RobotFrames.Length - 1)), RobotFrames.Length - 2);
        var t = phase * (RobotFrames.Length - 1) - index;
        var from = RobotFrames[index];
        var to = RobotFrames[index + 1];
        var dx = (from.Dx + (to.Dx - from.Dx) * t) * 24;
        var dy = (from.Dy + (to.Dy - from.Dy) * t) * 24;
        var rotate = from.Rotate + (to.Rotate - from.Rotate) * t;

        var matrix = Matrix.CreateTranslation(dx, dy) * Rotation(rotate, 12, 12);
        using var _ = context.PushTransform(matrix);
        var pen = new Pen(Foreground ?? Brushes.White, 2)
        {
            LineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        foreach (var geometry in RobotGeometry) context.DrawGeometry(null, pen, geometry);
    }

    private double StrokeWidth() => Variant == "mug" ? 1.75 : 2;

    /// <summary>Dash offset, opacity and translation of steam path <paramref name="index"/> (keyframes of #10).</summary>
    private static (double Offset, double Opacity, double Dx, double Dy) SteamFrame(double now, int index, double duration)
    {
        var local = (now - index * MotionTokens.SteamDelayStep) % duration;
        if (local < 0) local += duration;
        var p = local / duration;
        var offset = 8 - 16 * p;
        var opacity = p <= 0.25
            ? 0.95 * (p / 0.25)
            : p <= 0.7
                ? 0.95 + (0.6 - 0.95) * ((p - 0.25) / 0.45)
                : 0.6 * (1 - (p - 0.7) / 0.3);
        double dx, dy;
        if (p <= 0.7)
        {
            var t = MotionTokens.InOut.Ease(p / 0.7);
            dx = t;
            dy = 2 - 3 * t;
        }
        else
        {
            var t = MotionTokens.InOut.Ease((p - 0.7) / 0.3);
            dx = 1 - 2 * t;
            dy = -1 - 2 * t;
        }
        return (offset, Math.Clamp(opacity, 0, 1), dx, dy);
    }

    private static double SteamOpacity(double now, int index, double duration) =>
        SteamFrame(now, index, duration).Opacity;

    private static Matrix Rotation(double degrees, double cx, double cy)
    {
        var radians = degrees * Math.PI / 180;
        return Matrix.CreateTranslation(-cx, -cy) * Matrix.CreateRotation(radians) * Matrix.CreateTranslation(cx, cy);
    }
}
