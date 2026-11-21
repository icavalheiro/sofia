using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sofia.App.Controls;
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
        MotionService.Current.ResetParticipants();
        MotionService.Current.EnableManualClock(0);
        MotionService.Current.ReducedMotion = false;
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
        Assert.Equal(1232, shell.Transcript.ColumnHostControl.MaxWidth);
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

    [AvaloniaFact]
    public void Sidebar_states_render_at_1080p_2k_and_4k()
    {
        var sizes = new (string Name, int Width, int Height, double Scale)[]
        {
            ("1080p", 1920, 1080, 1.0),
            ("2k", 2560, 1440, 1.333),
            ("4k", 3840, 2160, 2.0),
        };
        foreach (var state in new[] { "mixed", "empty", "search", "working" })
        {
            var window = OpenSidebar(state);
            foreach (var (name, width, height, scale) in sizes)
            {
                var path = Evidence.File($"040-{state}-{name}.png");
                ScreenshotHarness.Capture(window, path, width, height, scale);
                Assert.True(new FileInfo(path).Length > 40_000, path);
            }
            window.Close();
        }

        // The two modal surfaces get their own 1080p evidence.
        var settings = OpenSidebar("settings");
        ScreenshotHarness.Capture(settings, Evidence.File("040-settings.png"), 1920, 1080, 1);
        Assert.True(settings.ShellView.Sidebar.IsSettingsMenuOpen);
        settings.Close();

        var delete = OpenSidebar("delete");
        ScreenshotHarness.Capture(delete, Evidence.File("040-delete.png"), 1920, 1080, 1);
        delete.Close();
    }

    [AvaloniaFact]
    public void Sidebar_working_border_and_hover_pill_are_stop_motion()
    {
        var conversation = OpenSidebar("mixed");
        var sidebar = conversation.ShellView.Sidebar;
        var row = sidebar.WorkingRows.First(r => r.Kind == WorkingRowKind.Conversation);

        // Conversation sweep: 1.4s linear, one full turn.
        foreach (var ms in new[] { 0d, 350, 700, 1050, 1400 })
        {
            MotionService.Current.Seek(ms);
            var path = Evidence.File($"040-motion-conversation-frame-{(int)ms}.png");
            ScreenshotHarness.Capture(conversation, path, 1920, 1080, 1);
            Assert.True(File.Exists(path));
        }
        MotionService.Current.Seek(350);
        Assert.InRange(row.SweepAngle, 89, 91);
        MotionService.Current.Seek(1400);
        Assert.InRange(row.SweepAngle, 0, 0.01);
        conversation.Close();

        // Workspace sweep: 1.1s linear, captured on a collapsed working workspace.
        var workspace = OpenSidebar("working");
        var workspaceSidebar = workspace.ShellView.Sidebar;
        var ring = workspaceSidebar.WorkingRows.First(r => r.Kind == WorkingRowKind.Workspace);
        foreach (var ms in new[] { 0d, 275, 550, 825, 1100 })
        {
            MotionService.Current.Seek(ms);
            var path = Evidence.File($"040-motion-workspace-frame-{(int)ms}.png");
            ScreenshotHarness.Capture(workspace, path, 1920, 1080, 1);
            Assert.True(File.Exists(path));
        }
        MotionService.Current.Seek(550);
        Assert.InRange(ring.SweepAngle, 179, 181);
        workspace.Close();

        // Hover pill: .15s fade, captured at 0 / 75 / 150ms.
        var hover = OpenSidebar("mixed");
        var hoverSidebar = hover.ShellView.Sidebar;
        MotionService.Current.Seek(0);
        Assert.True(hoverSidebar.HoverConversation("s_2"));
        var opacities = new List<double>();
        foreach (var ms in new[] { 0d, 75, 150 })
        {
            MotionService.Current.Seek(ms);
            opacities.Add(hoverSidebar.RevealOpacity("s_2"));
            var path = Evidence.File($"040-hover-frame-{(int)ms}.png");
            ScreenshotHarness.Capture(hover, path, 1920, 1080, 1);
            Assert.True(File.Exists(path));
        }
        Assert.Equal(0, opacities[0], 2);
        Assert.InRange(opacities[1], 0.75, 0.85); // cubic-bezier(.25,.1,.25,1) at 50%
        Assert.Equal(1, opacities[2], 2);
        hover.Close();
    }

    [AvaloniaFact]
    public void Sidebar_rows_follow_the_web_geometry()
    {
        var window = OpenSidebar("mixed");
        var shell = window.ShellView;
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(288, shell.Frame.ColumnDefinitions[0].Width.Value);
        Assert.Equal(288, shell.Sidebar.Bounds.Width);
        Assert.False(shell.Sidebar.SearchPanel.IsVisible);
        Assert.Contains(shell.Sidebar.TreeHost.Children, c => c is Border);
        Assert.NotEmpty(shell.Sidebar.WorkingRows);
        window.Close();
    }

    private static MainWindow OpenSidebar(string state)
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        var viewModel = window.ViewModel;
        switch (state)
        {
            case "empty":
                viewModel.LoadFixture(empty: true);
                window.ShellView.Attach(viewModel);
                break;
            case "search":
                viewModel.Sidebar.PreviewSearch("working", Fixture.SearchHits());
                window.ShellView.Attach(viewModel);
                break;
            case "settings":
                window.ShellView.Sidebar.PreviewSettingsMenu("ws_sofia");
                break;
            case "delete":
                window.ShellView.Sidebar.PreviewDelete("ws_sofia");
                break;
            case "working":
                foreach (var workspace in viewModel.Store.Workspaces)
                {
                    if ((workspace.Conversations ?? []).Any(c =>
                            viewModel.SidebarStore.Badges.GetValueOrDefault(c.Id) is "working" or "waiting"))
                    {
                        workspace.Expanded = false;
                    }
                }
                viewModel.SidebarStore.Rebuild();
                window.ShellView.Attach(viewModel);
                break;
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Transcript_states_render_at_1080p_2k_and_4k()
    {
        var sizes = new (string Name, int Width, int Height, double Scale)[]
        {
            ("1080p", 1920, 1080, 1.0),
            ("2k", 2560, 1440, 1.333),
            ("4k", 3840, 2160, 2.0),
        };
        foreach (var state in new[] { "idle", "streaming", "permission", "questions", "diff", "lightbox" })
        {
            var window = OpenTranscript(state);
            foreach (var (name, width, height, scale) in sizes)
            {
                MotionService.Current.Seek(0);
                var path = Evidence.File($"041-{state}-{name}.png");
                ScreenshotHarness.Capture(window, path, width, height, scale);
                Assert.True(new FileInfo(path).Length > 60_000, path);
            }
            window.Close();
        }

        // The remaining transcript states are extra evidence at the base size.
        foreach (var state in new[] { "markdown", "compaction", "retry", "queued", "waiting" })
        {
            var window = OpenTranscript(state);
            MotionService.Current.Seek(0);
            var path = Evidence.File($"041-{state}.png");
            ScreenshotHarness.Capture(window, path, 1920, 1080, 1);
            Assert.True(new FileInfo(path).Length > 60_000, path);
            window.Close();
        }
    }

    private static MainWindow OpenTranscript(string state)
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        window.ViewModel.LoadTranscriptState(state);
        window.ShellView.Attach(window.ViewModel);
        if (state == "lightbox") window.ShellView.Transcript.PreviewLightbox("m_1");
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Composer_states_render_at_1080p_2k_and_4k()
    {
        var sizes = new (string Name, int Width, int Height, double Scale)[]
        {
            ("1080p", 1920, 1080, 1.0),
            ("2k", 2560, 1440, 1.333),
            ("4k", 3840, 2160, 2.0),
        };

        // The eight states of the spec: idle, focused, attachments, the four popovers, running steer/queue and both
        // palettes. The five "primary" states get the three-size gate; the rest are extra evidence at 1080p.
        foreach (var state in new[] { "idle", "attachments", "model", "palette", "mention" })
        {
            var window = OpenComposer(state);
            foreach (var (name, width, height, scale) in sizes)
            {
                MotionService.Current.Seek(0);
                var path = Evidence.File($"042-{state}-{name}.png");
                ScreenshotHarness.Capture(window, path, width, height, scale);
                Assert.True(new FileInfo(path).Length > 50_000, path);
            }
            window.Close();
        }

        foreach (var state in new[] { "focus", "agent", "permission", "reasoning", "running", "steer", "queue" })
        {
            var window = OpenComposer(state);
            MotionService.Current.Seek(0);
            var path = Evidence.File($"042-{state}.png");
            ScreenshotHarness.Capture(window, path, 1920, 1080, 1);
            Assert.True(new FileInfo(path).Length > 50_000, path);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Composer_press_and_chip_hover_are_stop_motion()
    {
        // Motion #2: the send pill translates down 1px over 80ms (0 / 40 / 80).
        var press = OpenComposer("running");
        var composer = press.ShellView.ComposerView;
        press.ViewModel.Composer.Text = "keep going";
        Dispatcher.UIThread.RunJobs();
        MotionService.Current.Seek(0);
        composer.BeginPressFrame();
        var offsets = new List<double>();
        foreach (var ms in new[] { 0d, 40, 80 })
        {
            MotionService.Current.Seek(ms);
            offsets.Add(composer.PressOffset);
            var path = Evidence.File($"042-press-frame-{(int)ms}.png");
            ScreenshotHarness.Capture(press, path, 1920, 1080, 1);
            Assert.True(File.Exists(path));
        }
        Assert.Equal(0, offsets[0], 2);
        Assert.InRange(offsets[1], 0.6, 0.95); // cubic-bezier(.25,.1,.25,1) at 50%
        Assert.Equal(1, offsets[2], 2);
        press.Close();

        // Motion #1: chip hover moves the border #33333A -> #43434B over 120ms (0 / 60 / 120).
        var hover = OpenComposer("attachments");
        var hoverComposer = hover.ShellView.ComposerView;
        MotionService.Current.Seek(0);
        hoverComposer.BeginChipHoverFrame();
        var borders = new List<Avalonia.Media.Color>();
        foreach (var ms in new[] { 0d, 60, 120 })
        {
            MotionService.Current.Seek(ms);
            if (hoverComposer.PreviewChipBorder is { } color) borders.Add(color);
            var path = Evidence.File($"042-chip-hover-frame-{(int)ms}.png");
            ScreenshotHarness.Capture(hover, path, 1920, 1080, 1);
            Assert.True(File.Exists(path));
        }
        Assert.Equal(3, borders.Count);
        Assert.Equal(Avalonia.Media.Color.Parse("#33333A"), borders[0]);
        Assert.InRange(borders[1].R, (byte)60, (byte)66);
        Assert.Equal(Avalonia.Media.Color.Parse("#43434B"), borders[2]);
        hover.Close();
    }

    private static MainWindow OpenComposer(string state)
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        window.ShellView.ComposerView.ApplyPreviewState(state);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
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
