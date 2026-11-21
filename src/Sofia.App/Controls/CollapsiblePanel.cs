using Avalonia;
using Avalonia.Controls;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Controls;

/// <summary>
/// The web's <c>grid-rows: 1fr â†” 0fr</c> fold (reasoning blocks, tool cards): Avalonia has no grid-rows animation, so
/// the content is measured and <see cref="Layoutable.MaxHeight"/> is animated instead. The child stays mounted for the
/// whole transition - streaming text must not be re-created - and once expanded the panel removes the cap so content can
/// grow naturally.
/// </summary>
public class CollapsiblePanel : Decorator, IMotionParticipant
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<CollapsiblePanel, bool>(nameof(IsExpanded), true);

    private double _from;
    private double _to;
    private double _start;
    private bool _animating;

    public CollapsiblePanel()
    {
        ClipToBounds = true;
        MaxHeight = double.PositiveInfinity;
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsExpanded) MaxHeight = double.PositiveInfinity;
        else MaxHeight = 0;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MotionService.Current.Detach(this);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExpandedProperty) Begin();
    }

    public void TickMotion(double nowMs)
    {
        if (!_animating) return;
        var duration = Math.Max(1, MotionTokens.Fold.TotalMilliseconds);
        var progress = Math.Clamp((nowMs - _start) / duration, 0, 1);
        var eased = MotionTokens.InOut.Ease(progress);
        MaxHeight = _from + (_to - _from) * eased;
        if (progress < 1) return;

        _animating = false;
        MotionService.Current.Detach(this);
        MaxHeight = IsExpanded ? double.PositiveInfinity : 0;
        InvalidateMeasure();
    }

    private void Begin()
    {
        var current = Bounds.Height > 0 ? Bounds.Height : MaxHeight;
        if (double.IsInfinity(current) || double.IsNaN(current)) current = 0;

        var width = Bounds.Width > 0 ? Bounds.Width : 1200; Child?.Measure(new Size(width, double.PositiveInfinity));
        var target = IsExpanded ? Child?.DesiredSize.Height ?? 0 : 0;

        _from = IsExpanded ? 0 : current;
        _to = target;
        _start = MotionService.Current.NowMs;
        MaxHeight = _from;
        _animating = true;
        MotionService.Current.Attach(this);
        TickMotion(_start);
    }
}


