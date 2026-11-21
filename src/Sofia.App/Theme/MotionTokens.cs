using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;

namespace Sofia.App.Theme;

/// <summary>
/// Typed access to <c>Theme/Motion.axaml</c>: the milliseconds there are exposed as <see cref="TimeSpan"/> for XAML
/// transitions (<c>Duration="{x:Static theme:MotionTokens.Hover}"</c>) and as doubles for the clock-driven animations.
/// Nothing outside that dictionary may name a duration. The fallbacks are the documented web values, used only when the
/// dictionary is not loaded (a control constructed outside an Application, as some unit tests do).
/// </summary>
public static class MotionTokens
{
    private static object? Find(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value : null;

    private static double Ms(string key, double fallback) => Find(key) is double value ? value : fallback;

    private static TimeSpan Span(string key, double fallback) => TimeSpan.FromMilliseconds(Ms(key, fallback));

    // --- one-shot transitions ----------------------------------------------------------------------------

    public static TimeSpan Hover => Span("MsHover", 120);
    public static TimeSpan Press => Span("MsPress", 80);
    public static TimeSpan Reveal => Span("MsReveal", 150);
    public static TimeSpan Fold => Span("MsFold", 200);
    public static TimeSpan Modal => Span("MsModal", 160);
    public static TimeSpan Carousel => Span("MsCarousel", 220);
    public static TimeSpan PanelWidth => Span("MsPanelWidth", 240);
    public static TimeSpan PanelSlide => Span("MsPanelSlide", 240);
    public static TimeSpan PanelFade => Span("MsPanelFade", 180);

    // --- easings -----------------------------------------------------------------------------------------

    public static Easing Standard => Find("EaseStandard") as Easing ?? new SplineEasing(0.25, 0.1, 0.25, 1);
    public static Easing InOut => Find("EaseInOut") as Easing ?? new SplineEasing(0.42, 0, 0.58, 1);
    public static Easing Linear => Find("EaseLinear") as Easing ?? new LinearEasing();

    // --- infinite animations (ms) ------------------------------------------------------------------------

    public static double WorkspaceSweep => Ms("MsWorkspaceSweep", 1100);
    public static double ConversationSweep => Ms("MsConversationSweep", 1400);
    public static double IconSpin => Ms("MsIconSpin", 900);
    public static double PanelSpinner => Ms("MsPanelSpinner", 900);
    public static double SettingsSpinner => Ms("MsSettingsSpinner", 700);
    public static double RetrySpin => Ms("MsRetrySpin", 1800);
    public static double Steam => Ms("MsSteam", 2200);
    public static double SteamReduced => Ms("MsSteamReduced", 6000);
    public static double SteamDelayStep => Ms("MsSteamDelayStep", 450);
    public static double BeanSpin => Ms("MsBeanSpin", 1800);
    public static double RobotShake => Ms("MsRobotShake", 500);
    public static double Pulse => Ms("MsPulse", 1400);
}
