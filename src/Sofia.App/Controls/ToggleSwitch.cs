using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Controls;

/// <summary>
/// The Settings toggle (motion contract #5): a 34x20 track with a 16px knob that slides 16px in <c>.15s</c>; the knob
/// position is interpolated from the shared clock so frame captures are exact. Track/knob colours are primary when on,
/// surface-3 and text-faint when off, matching the web switch.
/// </summary>
public class ToggleSwitch : Panel, IMotionParticipant
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ToggleSwitch, bool>(nameof(IsChecked));

    private const double TrackWidth = 34;
    private const double TrackHeight = 20;
    private const double Knob = 16;
    private const double Inset = 2;

    private readonly Border _track = new();
    private readonly Border _knob = new();
    private double _from;
    private double _to;
    private double _start;
    private bool _animating;

    public ToggleSwitch()
    {
        Width = TrackWidth;
        Height = TrackHeight;
        Cursor = new Cursor(StandardCursorType.Hand);
        _track.CornerRadius = new CornerRadius(999);
        _knob.Width = Knob;
        _knob.Height = Knob;
        _knob.CornerRadius = new CornerRadius(999);
        _knob.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        _knob.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        _knob.Margin = new Thickness(Inset, 0, 0, 0);
        Children.Add(_track);
        Children.Add(_knob);
        ApplyState();
    }

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        IsChecked = !IsChecked;
        e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyState();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCheckedProperty) BeginSlide();
    }

    public void TickMotion(double nowMs)
    {
        if (!_animating) return;
        var duration = Math.Max(1, MotionTokens.Reveal.TotalMilliseconds);
        var progress = Math.Clamp((nowMs - _start) / duration, 0, 1);
        var eased = MotionTokens.Standard.Ease(progress);
        var x = _from + (_to - _from) * eased;
        _knob.Margin = new Thickness(Inset + x, 0, 0, 0);
        if (progress < 1) return;
        _animating = false;
        MotionService.Current.Detach(this);
    }

    private void BeginSlide()
    {
        _from = Math.Max(0, _knob.Margin.Left - Inset);
        _to = IsChecked ? TrackWidth - Knob - Inset * 2 : 0;
        _start = MotionService.Current.NowMs;
        _animating = true;
        MotionService.Current.Attach(this);
        ApplyState();
        TickMotion(_start);
    }

    private void ApplyState()
    {
        _track.Background = Resource(IsChecked ? "BrushPrimary" : "BrushSurface3", IsChecked ? Color.Parse("#A9744F") : Color.Parse("#2C2C31"));
        _knob.Background = Resource("BrushText", Colors.White);
        if (MotionService.Current.IsManual || !_animating)
        {
            var x = IsChecked ? TrackWidth - Knob - Inset * 2 : 0;
            if (!_animating) _knob.Margin = new Thickness(Inset + x, 0, 0, 0);
        }
    }

    private static IBrush Resource(string key, Color fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush
            ? brush
            : new SolidColorBrush(fallback);
}
