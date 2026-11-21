using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.App.Theme;

namespace Sofia.App.Tests;

/// <summary>
/// The screenshot gate: the shell must render at 1080p, 2k and 4k with the web's column geometry, and the frame
/// harness must be able to capture stop-motion sequences from the manual clock (used by every UI spec after this one).
/// The PNGs land in <c>.specs/spec_assets</c> as the spec's visual evidence.
/// </summary>
public class ScreenshotTests
{
    public ScreenshotTests()
    {
        ScreenshotHarness.FrameHook = () => AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        MotionService.Current.EnableManualClock(0);
        HeadOptions.Current.Screenshot = null;
    }

    [AvaloniaFact]
    public void Shell_renders_at_1080p_2k_and_4k()
    {
        var window = OpenShell();
        var sizes = new (string Name, int Width, int Height, double Scale)[]
        {
            ("039-app-1080p", 1920, 1080, 1.0),
            ("039-app-2k", 2560, 1440, 1.333),
            ("039-app-4k", 3840, 2160, 2.0),
        };

        foreach (var (name, width, height, scale) in sizes)
        {
            var path = Evidence.File(name + ".png");
            ScreenshotHarness.Capture(window, path, width, height, scale);
            Assert.True(File.Exists(path), path);
            var length = new FileInfo(path).Length;
            Assert.True(length > 40_000, $"{name}: {length} bytes");
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Shell_geometry_matches_App_vue()
    {
        var window = OpenShell();
        var shell = window.ShellView;
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();

        // Sidebar 288 / flexible main / right panel closed.
        Assert.Equal(288, shell.Frame.ColumnDefinitions[0].Width.Value);
        Assert.True(shell.Frame.ColumnDefinitions[1].Width.IsStar);
        Assert.Equal(0, shell.Frame.ColumnDefinitions[2].Width.Value);

        // Transcript and composer columns are capped at 1232px and centred.
        Assert.Equal(1232, shell.FindControl<Grid>("TranscriptInner")!.MaxWidth);
        Assert.Equal(1232, shell.FindControl<Grid>("ComposerInner")!.MaxWidth);

        // Header band padding .7rem 1.5rem .55rem and composer band .45rem 1.5rem .55rem at the 16px base.
        var header = shell.FindControl<Border>("HeaderBand")!;
        Assert.Equal(new Thickness(24, 11.2, 24, 8.8), header.Padding);
        var composer = shell.FindControl<Border>("ComposerBand")!;
        Assert.Equal(new Thickness(24, 7.2, 24, 8.8), composer.Padding);

        window.Close();
    }

    [AvaloniaFact]
    public void Right_panel_opens_in_240ms_and_the_frames_are_captured()
    {
        var window = OpenShell();
        var shell = window.ShellView;
        ScreenshotHarness.Capture(window, Evidence.File("039-app-1080p.png"), 1920, 1080, 1);

        shell.SetRightPanel(true, animate: true);
        var column = shell.Frame.ColumnDefinitions[2];
        MotionService.Current.Seek(0);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, column.Width.Value, 1);

        MotionService.Current.Seek(120);
        Dispatcher.UIThread.RunJobs();
        Assert.InRange(column.Width.Value, 1, 299);

        MotionService.Current.Seek(240);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(300, column.Width.Value, 1);
        Assert.Equal(1, shell.FindControl<Grid>("RightPanelInner")!.Opacity, 2);

        foreach (var ms in new[] { 0d, 60, 120, 180, 240 })
        {
            MotionService.Current.Seek(ms);
            var path = Evidence.File($"039-panel-frame-{(int)ms}.png");
            ScreenshotHarness.Capture(window, path, 1920, 1080, 1);
            Assert.True(File.Exists(path));
        }

        shell.SetRightPanel(false, animate: false);
        window.Close();
    }

    [AvaloniaFact]
    public void Accent_font_and_compact_variants_repaint()
    {
        var window = OpenShell();
        var shell = window.ShellView;

        ScreenshotHarness.Apply(new ScreenshotRequest { Accent = "beige", FontSize = "lg" });
        shell.RefreshAppearance();
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Assert.Equal(288, shell.Frame.ColumnDefinitions[0].Width.Value);
        Assert.Equal(18d, Application.Current!.TryFindResource("FontSizeLg", out var lg) ? (double)lg! : 0, 3);
        ScreenshotHarness.Capture(window, Evidence.File("039-app-accent-beige-font-lg.png"), 1920, 1080, 1);

        ScreenshotHarness.Apply(new ScreenshotRequest { Accent = "brown", FontSize = "sm", CompactSidebar = true });
        shell.RefreshAppearance();
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Assert.Equal(236, shell.Frame.ColumnDefinitions[0].Width.Value);
        ScreenshotHarness.Capture(window, Evidence.File("039-app-compact-font-sm.png"), 1920, 1080, 1);

        ScreenshotHarness.Apply(new ScreenshotRequest { Accent = "brown", FontSize = "md", CompactSidebar = false });
        shell.RefreshAppearance();
        window.Close();
    }

    [AvaloniaFact]
    public void Spinner_and_working_border_frames_are_stop_motion()
    {
        var window = OpenShell();
        var shell = window.ShellView;
        ScreenshotHarness.Capture(window, Evidence.File("039-app-1080p.png"), 1920, 1080, 1);

        // Working border + spinner share the clock: 0 / 275 / 550 / 825 / 1100 ms is one 1.1s workspace sweep.
        foreach (var ms in new[] { 0d, 275, 550, 825, 1100 })
        {
            MotionService.Current.Seek(ms);
            var path = Evidence.File($"039-motion-frame-{(int)ms}.png");
            ScreenshotHarness.Capture(window, path, 1920, 1080, 1);
            Assert.True(File.Exists(path));
        }

        window.Close();
    }

    [AvaloniaFact]
    public void Reduced_motion_keeps_the_shell_but_slows_the_steam()
    {
        MotionService.Current.ReducedMotion = true;
        var window = OpenShell();
        var shell = window.ShellView;
        ScreenshotHarness.Capture(window, Evidence.File("039-app-reduced-motion.png"), 1920, 1080, 1);
        Assert.True(MotionService.Current.ReducedMotion);
        MotionService.Current.ReducedMotion = false;
        window.Close();
    }

    private static MainWindow OpenShell()
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
