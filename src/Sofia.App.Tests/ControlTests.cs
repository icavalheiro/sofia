using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Sofia.App.Controls;
using Sofia.App.I18n;
using Sofia.App.Services;
using Sofia.App.Theme;

namespace Sofia.App.Tests;

/// <summary>
/// Control behaviour that the later UI specs depend on: the Lucide table, the three spinner variants, the working-border
/// stop sets, the collapsible fold and the switch. Every assertion is on a value derived from the motion clock, so the
/// numbers double as the frame-sequence contract.
/// </summary>
public class ControlTests
{
    [AvaloniaFact]
    public void Icon_table_matches_SofiaIcon_vue()
    {
        // 66 semantic names in the web map minus `loader` (a spinner) = 65 Lucide glyphs.
        var icons = new HashSet<string>();
        foreach (var key in new[]
                 {
                     "folder", "folderOpen", "files", "file", "plus", "chat", "send", "paperclip", "image", "video",
                     "sliders", "chevronRight", "chevronLeft", "chevronDown", "chevronUp", "sparkles", "shield", "zap",
                     "stop", "trash", "archive", "restore", "pin", "unpin", "search", "x", "user", "gear", "check",
                     "code", "copy", "book", "palette", "flask", "briefcase", "globe", "cloud", "wind", "terminal",
                     "database", "server", "star", "music", "camera", "layers", "cpu", "box", "bell", "info",
                     "refresh", "retry", "undo", "link", "list", "plug", "clock", "dollar", "panelRight", "bot",
                     "alert", "help", "upload", "languages", "keyboard", "branch",
                 })
        {
            if (Application.Current!.TryFindResource("Icon." + key, out var geometry) && geometry is Geometry)
            {
                icons.Add(key);
            }
        }

        Assert.Equal(65, icons.Count);
        Assert.DoesNotContain("loader", icons);
        // `circle` is missing from the web map too: the app renders nothing for it, deliberately.
        Assert.False(Application.Current!.TryFindResource("Icon.circle", out _));
    }

    [AvaloniaFact]
    public void Icons_draw_at_the_web_size_and_stroke()
    {
        var icon = new SofiaIcon { Glyph = "pin" };
        Assert.Equal(18d, icon.Size);
        Assert.Equal(1.75, icon.StrokeWidth);
        Assert.NotNull(icon.Geometry);

        icon.Size = 14;
        Assert.Equal(14d, icon.Width);

        var unknown = new SofiaIcon { Glyph = "circle" };
        Assert.Null(unknown.Geometry);
    }

    [AvaloniaFact]
    public void Loader_routes_to_the_three_spinner_variants()
    {
        var icon = new SofiaIcon { Glyph = "loader", Size = 18 };
        Assert.NotNull(icon.Loader);
        Assert.Equal("mug", icon.Loader!.Variant);

        icon.Size = 12;
        Assert.Equal("bean", icon.Loader.Variant);

        icon.Robot = true;
        Assert.Equal("robot", icon.Loader.Variant);
        Assert.Equal(12d, icon.Loader.Size);
    }

    [AvaloniaFact]
    public void Spinner_steam_follows_the_2_2s_keyframes()
    {
        MotionService.Current.EnableManualClock(0);
        MotionService.Current.ReducedMotion = false;
        var spinner = new CoffeeSpinner { Size = 18 };

        // 25% -> .95 opacity, 70% -> .6, 100% -> 0 (first path; the other two are delayed by .45s / .9s).
        spinner.TickMotion(550);
        Assert.Equal(0.95, spinner.SteamOpacities.First, 3);
        spinner.TickMotion(1540);
        Assert.Equal(0.60, spinner.SteamOpacities.First, 3);
        spinner.TickMotion(2199);
        Assert.InRange(spinner.SteamOpacities.First, 0, 0.01);

        // The delayed paths run the same curve, shifted by 450ms.
        spinner.TickMotion(1000);
        Assert.Equal(0.95, spinner.SteamOpacities.Second, 3);
    }

    [AvaloniaFact]
    public void Reduced_motion_slows_the_steam_to_six_seconds()
    {
        MotionService.Current.EnableManualClock(0);
        MotionService.Current.ReducedMotion = true;
        var spinner = new CoffeeSpinner { Size = 18 };

        // 1500ms is 25% of 6s -> .95; without reduced motion that instant would be mid-decay.
        spinner.TickMotion(1500);
        Assert.Equal(0.95, spinner.SteamOpacities.First, 3);

        MotionService.Current.ReducedMotion = false;
        spinner.TickMotion(1500);
        Assert.NotEqual(0.95, spinner.SteamOpacities.First, 3);
    }

    [AvaloniaFact]
    public void Icon_spin_is_360_degrees_in_900ms()
    {
        MotionService.Current.EnableManualClock(0);
        var icon = new SofiaIcon { Glyph = "loader", Size = 18 };
        icon.Glyph = "refresh";
        icon.Spin = true;
        icon.TickMotion(225);
        Assert.Equal(90d, icon.SpinAngle, 3);
        icon.TickMotion(450);
        Assert.Equal(180d, icon.SpinAngle, 3);
        icon.TickMotion(900);
        Assert.Equal(0d, icon.SpinAngle, 3);
    }

    [AvaloniaFact]
    public void Working_border_sweeps_at_1_1s_and_1_4s()
    {
        MotionService.Current.EnableManualClock(0);
        var workspace = new WorkingRowBorder { Kind = WorkingRowKind.Workspace, IsWorking = true };

        workspace.TickMotion(275);
        Assert.Equal(90d, workspace.SweepAngle, 3);
        workspace.TickMotion(550);
        Assert.Equal(180d, workspace.SweepAngle, 3);
        workspace.TickMotion(1100);
        Assert.Equal(0d, workspace.SweepAngle, 3);

        var conversation = new WorkingRowBorder { Kind = WorkingRowKind.Conversation, IsWorking = true };
        conversation.TickMotion(350);
        Assert.Equal(90d, conversation.SweepAngle, 3);
        conversation.TickMotion(1400);
        Assert.Equal(0d, conversation.SweepAngle, 3);
    }

    [AvaloniaFact]
    public void Collapsible_panel_folds_in_200ms_and_keeps_content_mounted()
    {
        MotionService.Current.EnableManualClock(0);
        var content = new Avalonia.Controls.TextBlock { Text = "reasoning", Height = 100 };
        var panel = new CollapsiblePanel { Child = content, IsExpanded = true };
        panel.Measure(new Size(400, 400));
        panel.Arrange(new Rect(0, 0, 400, 100));

        panel.IsExpanded = false;
        panel.TickMotion(100);
        Assert.InRange(panel.MaxHeight, 1, 100);
        Assert.Same(content, panel.Child);
        panel.TickMotion(200);
        Assert.Equal(0d, panel.MaxHeight);
        Assert.Same(content, panel.Child);
    }

    [AvaloniaFact]
    public void Toggle_switch_knob_travels_16px_in_150ms()
    {
        MotionService.Current.EnableManualClock(0);
        var toggle = new Sofia.App.Controls.ToggleSwitch();
        toggle.IsChecked = true;
        toggle.TickMotion(75);
        var knob = (Avalonia.Controls.Border)toggle.Children[1];
        Assert.InRange(knob.Margin.Left, 2.5, 15.5);
        toggle.TickMotion(150);
        Assert.Equal(16d, knob.Margin.Left, 3);
    }

    [AvaloniaFact]
    public void Localization_covers_every_locale()
    {
        Assert.Equal(6, Localization.Languages.Length);
        foreach (var language in Localization.Languages)
        {
            Localization.SetLocale(language);
            foreach (var key in Localization.Keys)
            {
                var text = Localization.T(key);
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}:{key}");
                Assert.NotEqual(key, text);
            }
        }
        Localization.SetLocale("en");
    }
}
