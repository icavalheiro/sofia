using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Sofia.App.Controls;

/// <summary>
/// Segmented meter used by the composer's context indicator (and, later, model quota bars): <see cref="Segments"/>
/// rounded bars, filled proportionally to <see cref="Value"/>. Colours follow the token set, switching to warn/err when
/// the meter is nearly full.
/// </summary>
public class SofiaMeter : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<SofiaMeter, double>(nameof(Value));

    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<SofiaMeter, int>(nameof(Segments), 10);

    public static readonly StyledProperty<double> SegmentGapProperty =
        AvaloniaProperty.Register<SofiaMeter, double>(nameof(SegmentGap), 2);

    public static readonly StyledProperty<double> BarHeightProperty =
        AvaloniaProperty.Register<SofiaMeter, double>(nameof(BarHeight), 6);

    public SofiaMeter()
    {
        Height = BarHeight;
    }

    /// <summary>Fill ratio, 0..1.</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double SegmentGap
    {
        get => GetValue(SegmentGapProperty);
        set => SetValue(SegmentGapProperty, value);
    }

    public double BarHeight
    {
        get => GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var segments = Math.Max(1, Segments);
        var gap = SegmentGap;
        var width = Bounds.Width;
        var height = BarHeight;
        if (width <= 0 || height <= 0) return;

        var segmentWidth = Math.Max(1, (width - gap * (segments - 1)) / segments);
        var filledSegments = Math.Clamp(Value, 0, 1) * segments;
        var tone = Value >= 0.9
            ? Resource("BrushErr", Color.Parse("#C9705F"))
            : Value >= 0.75
                ? Resource("BrushWarn", Color.Parse("#C9A94F"))
                : Resource("BrushPrimary", Color.Parse("#A9744F"));
        var empty = Resource("BrushSurface3", Color.Parse("#2C2C31"));
        var radius = height / 2;

        for (var i = 0; i < segments; i++)
        {
            var x = i * (segmentWidth + gap);
            var fill = Math.Clamp(filledSegments - i, 0, 1);
            var rect = new Rect(x, 0, segmentWidth, height);
            context.DrawRectangle(empty, null, rect, radius, radius);
            if (fill <= 0) continue;
            var filled = new Rect(x, 0, segmentWidth * fill, height);
            context.DrawRectangle(tone, null, filled, radius, radius);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty || change.Property == SegmentsProperty ||
            change.Property == SegmentGapProperty || change.Property == BarHeightProperty)
        {
            Height = BarHeight;
            InvalidateVisual();
        }
    }

    private static IBrush Resource(string key, Color fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}
