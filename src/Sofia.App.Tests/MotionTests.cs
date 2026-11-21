using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Tests;

/// <summary>
/// The motion contract as executable checks: every timing lives in <c>Theme/Motion.axaml</c>, the easing map matches the
/// CSS curves and the manual clock is the only source of animation time in tests.
/// </summary>
public class MotionTests
{
    [AvaloniaFact]
    public void Timings_come_from_Motion_axaml()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(120), MotionTokens.Hover);
        Assert.Equal(TimeSpan.FromMilliseconds(80), MotionTokens.Press);
        Assert.Equal(TimeSpan.FromMilliseconds(150), MotionTokens.Reveal);
        Assert.Equal(TimeSpan.FromMilliseconds(200), MotionTokens.Fold);
        Assert.Equal(TimeSpan.FromMilliseconds(160), MotionTokens.Modal);
        Assert.Equal(TimeSpan.FromMilliseconds(220), MotionTokens.Carousel);
        Assert.Equal(TimeSpan.FromMilliseconds(240), MotionTokens.PanelWidth);
        Assert.Equal(TimeSpan.FromMilliseconds(180), MotionTokens.PanelFade);

        Assert.Equal(1100d, MotionTokens.WorkspaceSweep);
        Assert.Equal(1400d, MotionTokens.ConversationSweep);
        Assert.Equal(900d, MotionTokens.IconSpin);
        Assert.Equal(2200d, MotionTokens.Steam);
        Assert.Equal(6000d, MotionTokens.SteamReduced);
        Assert.Equal(1800d, MotionTokens.BeanSpin);
        Assert.Equal(500d, MotionTokens.RobotShake);
        Assert.Equal(1400d, MotionTokens.Pulse);
    }

    [AvaloniaFact]
    public void Css_easings_map_onto_SplineEasing()
    {
        var standard = (Avalonia.Animation.Easings.SplineEasing)MotionTokens.Standard;
        Assert.Equal(0.25, standard.X1);
        Assert.Equal(0.1, standard.Y1);
        Assert.Equal(0.25, standard.X2);
        Assert.Equal(1, standard.Y2);

        var inOut = (Avalonia.Animation.Easings.SplineEasing)MotionTokens.InOut;
        Assert.Equal(0.42, inOut.X1);
        Assert.Equal(0, inOut.Y1);
        Assert.Equal(0.58, inOut.X2);
        Assert.Equal(1, inOut.Y2);

        Assert.IsType<Avalonia.Animation.Easings.LinearEasing>(MotionTokens.Linear);
    }

    [AvaloniaFact]
    public void No_axaml_file_hardcodes_a_duration()
    {
        var root = FindRepositoryRoot();
        var theme = Path.Combine(root, "src", "Sofia.App");
        foreach (var file in Directory.EnumerateFiles(theme, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Duration=\"0:", text);
            // Transitions must reference the token table, not a literal time.
            foreach (Match match in Regex.Matches(text, "Duration=\"([^\"]+)\""))
            {
                var value = match.Groups[1].Value;
                Assert.True(
                    value.StartsWith("{x:Static theme:MotionTokens.", StringComparison.Ordinal) ||
                    value.Contains("TemplateBinding", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}: hardcoded duration {value}");
            }
        }
    }

    [AvaloniaFact]
    public void Manual_clock_seek_is_deterministic()
    {
        var motion = MotionService.Current;
        motion.EnableManualClock(0);
        Assert.True(motion.IsManual);
        motion.Seek(550);
        Assert.Equal(550, motion.NowMs);
        motion.Advance(275);
        Assert.Equal(825, motion.NowMs);
    }

    [AvaloniaFact]
    public void Reduced_motion_detector_fails_open()
    {
        // Whatever the machine says, detection must never throw and returns a boolean.
        var value = ReducedMotionDetector.Detect();
        Assert.IsType<bool>(value);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Sofia.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new InvalidOperationException("repository root not found");
    }
}
