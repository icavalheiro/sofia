using Avalonia.Controls;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Features.Transcript;

/// <summary>
/// Motion contract #4: the message row's Copy/Branch/Retry/Revert actions fade in while the row is hovered (the web's
/// <c>.msg-revert</c> <c>opacity 0 → 1</c> over <c>.15s ease</c>). Driven by the shared clock so the frame harness can
/// stop-motion it; the targets stop receiving clicks while hidden, exactly like a zero-opacity element with the web's
/// <c>pointer-events</c> behaviour on a hovered row.
/// </summary>
public sealed class HoverReveal : IMotionParticipant
{
    private readonly List<Control> _targets = [];
    private double _from;
    private double _start;
    private bool _active;

    public HoverReveal(params Control[] targets)
    {
        foreach (var target in targets)
        {
            target.Opacity = 0;
            target.IsHitTestVisible = false;
            _targets.Add(target);
        }
    }

    public double Opacity => _targets.Count > 0 ? _targets[0].Opacity : 0;

    public bool Revealed { get; private set; }

    public void Add(Control target)
    {
        target.Opacity = Revealed ? 1 : 0;
        target.IsHitTestVisible = Revealed;
        _targets.Add(target);
    }

    public void Begin(bool reveal)
    {
        Begin(reveal, MotionService.Current.NowMs);
    }

    public void Begin(bool reveal, double nowMs)
    {
        Revealed = reveal;
        _from = Opacity;
        _start = nowMs;
        _active = true;
        foreach (var target in _targets) target.IsHitTestVisible = reveal;
        MotionService.Current.Attach(this);
        TickMotion(nowMs);
    }

    public void TickMotion(double nowMs)
    {
        if (!_active) return;
        var duration = Math.Max(1, MotionTokens.Reveal.TotalMilliseconds);
        var progress = Math.Clamp((nowMs - _start) / duration, 0, 1);
        var target = Revealed ? 1d : 0d;
        var value = _from + (target - _from) * MotionTokens.Standard.Ease(progress);
        foreach (var control in _targets) control.Opacity = value;
        if (progress < 1) return;
        _active = false;
        MotionService.Current.Detach(this);
    }

    public void ResetForHarness()
    {
        _active = false;
        MotionService.Current.Detach(this);
    }

    /// <summary>Puts every target at its final opacity without animating (used after a row rebuild while hovered).</summary>
    public void SetInstant(bool revealed)
    {
        Revealed = revealed;
        _active = false;
        MotionService.Current.Detach(this);
        foreach (var target in _targets)
        {
            target.Opacity = revealed ? 1 : 0;
            target.IsHitTestVisible = revealed;
        }
    }
}
