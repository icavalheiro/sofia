using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// Motion contract #17: the question carousel's slide. The outgoing page translates to ∓32px and fades out over
/// <c>.22s ease</c>, then the incoming page comes in from ±32px at 0 opacity over another <c>.22s ease</c> (the web's
/// <c>&lt;Transition mode="out-in"&gt;</c>). Under reduced motion both steps are instant.
/// </summary>
public sealed class CarouselStage : Panel, IMotionParticipant
{
    private const double SlidePixels = 32;

    private Control? _outgoing;
    private Control? _incoming;
    private double _start;
    private int _direction = 1;
    private bool _leaving;
    private bool _active;

    public CarouselStage()
    {
        ClipToBounds = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _active = false;
        MotionService.Current.Detach(this);
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Current direction of the last step (1 forward, -1 back) — asserted by the frame harness.</summary>
    public int Direction => _direction;

    public bool IsAnimating => _active;

    /// <summary>Offset of the outgoing page during the leave phase (frame assertion).</summary>
    public double Offset => _incoming?.RenderTransform is TranslateTransform t ? t.X : 0;

    /// <summary>Replaces the content without animating (first render, or a rebuild while idle).</summary>
    public void Show(Control page)
    {
        _active = false;
        MotionService.Current.Detach(this);
        _outgoing = null;
        _incoming = null;
        Children.Clear();
        page.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        Children.Add(page);
    }

    /// <summary>Slides to a new page: leave (±32px), then enter.</summary>
    public void Step(Control next, int direction)
    {
        _direction = direction >= 0 ? 1 : -1;
        next.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top;
        if (MotionService.Current.ReducedMotion || Children.Count == 0)
        {
            Show(next);
            return;
        }

        if (_active) Show(Children[0]);
        _outgoing = Children.Count > 0 ? Children[0] : null;
        _incoming = next;
        next.Opacity = 0;
        next.RenderTransform = new TranslateTransform(_direction * SlidePixels, 0);
        Children.Add(next);
        _leaving = true;
        _start = MotionService.Current.NowMs;
        _active = true;
        MotionService.Current.Attach(this);
        TickMotion(_start);
    }

    public void TickMotion(double nowMs)
    {
        var duration = Math.Max(1, MotionTokens.Carousel.TotalMilliseconds);
        while (_active)
        {
            var progress = Math.Clamp((nowMs - _start) / duration, 0, 1);
            var eased = MotionTokens.Standard.Ease(progress);

            if (_leaving)
            {
                if (_outgoing is not null)
                {
                    _outgoing.Opacity = 1 - eased;
                    _outgoing.RenderTransform = new TranslateTransform(-_direction * SlidePixels * eased, 0);
                }
                if (progress < 1) return;
                if (_outgoing is not null) Children.Remove(_outgoing);
                _outgoing = null;
                _leaving = false;
                // Absolute phase boundary: a frame jump past the leave must not stretch the entry phase.
                _start += duration;
                continue;
            }

            if (_incoming is not null)
            {
                _incoming.Opacity = eased;
                _incoming.RenderTransform = new TranslateTransform(_direction * SlidePixels * (1 - eased), 0);
            }
            if (progress < 1) return;
            if (_incoming is not null)
            {
                _incoming.Opacity = 1;
                _incoming.RenderTransform = null;
            }
            _active = false;
            MotionService.Current.Detach(this);
        }
    }
}
