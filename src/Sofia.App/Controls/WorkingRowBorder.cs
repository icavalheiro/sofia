using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Controls;

/// <summary>Which conic stop-set a working row uses (motion contract #6 workspace / #7 conversation).</summary>
public enum WorkingRowKind
{
    Workspace,
    Conversation,
}

/// <summary>
/// The "working" row treatment from the sidebar: a 1.5px conic-gradient ring that sweeps 0â†’360deg behind the row while a
/// session runs (workspace 1.1s, conversation 1.4s, both linear and infinite) over a primary-soft row fill. The ring is
/// its own visual (the inner panel paints the row background), so the animation only repaints the ring and stops as soon
/// as the row leaves the viewport - a long sidebar must not drop frames.
/// </summary>
public class WorkingRowBorder : Panel, IMotionParticipant
{
    public static readonly StyledProperty<bool> IsWorkingProperty =
        AvaloniaProperty.Register<WorkingRowBorder, bool>(nameof(IsWorking));

    public static readonly StyledProperty<WorkingRowKind> KindProperty =
        AvaloniaProperty.Register<WorkingRowBorder, WorkingRowKind>(nameof(Kind));

    public static readonly StyledProperty<double> RingThicknessProperty =
        AvaloniaProperty.Register<WorkingRowBorder, double>(nameof(RingThickness), 1.5);

    public static readonly StyledProperty<CornerRadius> RowCornerRadiusProperty =
        AvaloniaProperty.Register<WorkingRowBorder, CornerRadius>(nameof(RowCornerRadius), new CornerRadius(9));

    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<WorkingRowBorder, object?>(nameof(Content));

    private readonly Border _ring = new();
    private readonly Border _inner = new();
    private readonly Border _fill = new();
    private readonly ContentPresenter _presenter = new();
    private ConicGradientBrush? _brush;

    public WorkingRowBorder()
    {
        // ring (conic sweep) -> inner (opaque row surface) -> fill (primary-soft while working/active) -> content.
        _fill.Child = _presenter;
        _inner.Child = _fill;
        _ring.Child = _inner;
        Children.Add(_ring);
        UpdateChrome();
        UpdateRing();
    }

    public bool IsWorking
    {
        get => GetValue(IsWorkingProperty);
        set => SetValue(IsWorkingProperty, value);
    }

    public WorkingRowKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double RingThickness
    {
        get => GetValue(RingThicknessProperty);
        set => SetValue(RingThicknessProperty, value);
    }

    public CornerRadius RowCornerRadius
    {
        get => GetValue(RowCornerRadiusProperty);
        set => SetValue(RowCornerRadiusProperty, value);
    }

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    /// <summary>Current sweep angle in degrees (0-360); the frame harness asserts this against the web capture.</summary>
    public double SweepAngle => _brush?.Angle ?? 0;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAttachment();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionService.Current.Detach(this);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsWorkingProperty || change.Property == KindProperty)
        {
            UpdateChrome();
            UpdateRing();
            UpdateAttachment();
        }
        else if (change.Property == RingThicknessProperty || change.Property == RowCornerRadiusProperty)
        {
            UpdateChrome();
            UpdateRing();
        }
        else if (change.Property == ContentProperty)
        {
            _presenter.Content = Content;
        }
    }

    public void TickMotion(double nowMs)
    {
        if (_brush is null) return;
        if (!this.IsEffectivelyVisible || !IsWorking)
        {
            _brush.Angle = 0;
            return;
        }
        var duration = Kind == WorkingRowKind.Conversation ? MotionTokens.ConversationSweep : MotionTokens.WorkspaceSweep;
        _brush.Angle = nowMs % duration / duration * 360;
    }

    private void UpdateAttachment()
    {
        if (IsWorking && this.IsAttachedToVisualTree())
        {
            MotionService.Current.Attach(this);
            TickMotion(MotionService.Current.NowMs);
        }
        else
        {
            MotionService.Current.Detach(this);
        }
    }

    private void UpdateChrome()
    {
        var innerRadius = new CornerRadius(Math.Max(0, RowCornerRadius.TopLeft - RingThickness));
        _ring.Padding = new Thickness(RingThickness);
        _ring.CornerRadius = RowCornerRadius;
        _inner.CornerRadius = innerRadius;
        // The inner panel is opaque: the conic sweep must only show through the 1.5px ring, never the whole row.
        _inner[!Border.BackgroundProperty] = this[!RowSurfaceProperty];
        _fill.CornerRadius = innerRadius;
        _fill[!Border.BackgroundProperty] = this[!RowBackgroundProperty];
    }

    private void UpdateRing()
    {
        if (!IsWorking)
        {
            _ring.Background = null;
            _brush = null;
            return;
        }

        _brush = CreateSweep(Kind == WorkingRowKind.Conversation);
        _ring.Background = _brush;
    }

    public static readonly StyledProperty<IBrush?> RowBackgroundProperty =
        AvaloniaProperty.Register<WorkingRowBorder, IBrush?>(nameof(RowBackground));

    public static readonly StyledProperty<IBrush?> RowSurfaceProperty =
        AvaloniaProperty.Register<WorkingRowBorder, IBrush?>(nameof(RowSurface));

    /// <summary>Opaque surface behind the row (the sidebar surface), so the sweep only paints the ring.</summary>
    public IBrush? RowSurface
    {
        get => GetValue(RowSurfaceProperty);
        set => SetValue(RowSurfaceProperty, value);
    }

    /// <summary>Fill of the row itself (primary-soft while working, transparent otherwise).</summary>
    public IBrush? RowBackground
    {
        get => GetValue(RowBackgroundProperty);
        set => SetValue(RowBackgroundProperty, value);
    }

    /// <summary>
    /// Builds the conic brush. Stop sets come verbatim from the web: workspace
    /// <c>transparent 0 58%, primary 78%, #fff 90%, detail 96%, transparent 100%</c>; conversation
    /// <c>transparent 0deg, primary 60deg, detail 120deg, transparent 180deg</c>.
    /// </summary>
    public static ConicGradientBrush CreateSweep(bool conversation)
    {
        var primary = Brush("BrushPrimary", Color.Parse("#A9744F"));
        var detail = Brush("BrushDetail", Color.Parse("#E4D3AC"));
        var transparent = Brush("BrushTransparent", Colors.Transparent);

        var brush = new ConicGradientBrush { Angle = 0 };
        if (conversation)
        {
            brush.GradientStops.Add(new GradientStop(ToColor(transparent), 0));
            brush.GradientStops.Add(new GradientStop(ToColor(primary), 1.0 / 6.0));
            brush.GradientStops.Add(new GradientStop(ToColor(detail), 2.0 / 6.0));
            brush.GradientStops.Add(new GradientStop(ToColor(transparent), 3.0 / 6.0));
            brush.GradientStops.Add(new GradientStop(ToColor(transparent), 1));
        }
        else
        {
            brush.GradientStops.Add(new GradientStop(ToColor(transparent), 0));
            brush.GradientStops.Add(new GradientStop(ToColor(transparent), 0.58));
            brush.GradientStops.Add(new GradientStop(ToColor(primary), 0.78));
            brush.GradientStops.Add(new GradientStop(Colors.White, 0.90));
            brush.GradientStops.Add(new GradientStop(ToColor(detail), 0.96));
            brush.GradientStops.Add(new GradientStop(ToColor(transparent), 1));
        }
        return brush;
    }

    private static IBrush Brush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);

    private static Color ToColor(IBrush brush) => brush is ISolidColorBrush solid ? solid.Color : Colors.Transparent;
}

