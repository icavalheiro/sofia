using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sofia.App.Features.Panels;
using Sofia.App.Features.Settings;
using Sofia.App.I18n;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.App.Theme;

namespace Sofia.App.Tests;

/// <summary>
/// Spec 043 behaviour: the six-locale catalog with the web's keys and interpolation, the shortcut editor (rebind,
/// conflicts, reset, browser-reserved, recording/suspension), the right panel's diff merge and numbered rows, the task
/// card clamp/persistence, every settings tab from fixture data, the header status dot and the pairing dialog.
/// </summary>
public class SettingsPanelsTests
{
    public SettingsPanelsTests()
    {
        ScreenshotHarness.FrameHook = () => AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        MotionService.Current.ResetParticipants();
        MotionService.Current.EnableManualClock(0);
        HeadOptions.Current.Screenshot = null;
        Localization.SetLocale("en");
    }

    [AvaloniaFact]
    public void Localization_ships_the_web_catalog_and_interpolates_params()
    {
        Assert.Equal(6, Localization.Languages.Length);
        // Keys that exist in the Vue catalog must exist here with the same English text.
        Assert.Equal("Sofia is working…", Localization.T("chat.working"));
        Assert.Equal("Permission requested", Localization.T("perm.title"));
        Assert.Equal("Workspace", Localization.T("panel.title"));
        Assert.StartsWith("Browsers reserve this combo", Localization.T("shortcuts.reserved"));

        Localization.SetLocale("pt");
        Assert.Equal("Permissão solicitada", Localization.T("perm.title"));
        Assert.Equal("Compactando…", Localization.T("session.compacting"));
        Localization.SetLocale("ja");
        Assert.Equal("許可のリクエスト", Localization.T("perm.title"));
        Localization.SetLocale("zh");
        Assert.Equal("请求权限", Localization.T("perm.title"));
        Localization.SetLocale("fr");
        Assert.Equal("Permission demandée", Localization.T("perm.title"));
        Localization.SetLocale("es");
        Assert.Equal("Permiso solicitado", Localization.T("perm.title"));
        Localization.SetLocale("en");

        // {param} interpolation keeps unknown placeholders visible, exactly like the web's t().
        Assert.Equal("Earlier messages hidden to keep the chat light (12)", Localization.T("chat.omitted", ("count", 12)));
        Assert.Equal("Leave {missing} alone", Localization.Format("Leave {missing} alone", new Dictionary<string, object?>()));
        Assert.Equal("5 tools", Localization.T("settings.mcp.tools", ("count", 5)));
        Assert.Equal("settings.unknown.key", Localization.T("settings.unknown.key"));
    }

    [Fact]
    public void Shortcuts_rebind_report_conflicts_and_reset()
    {
        var state = new LocalState(TempStatePath("shortcuts"));
        var service = new ShortcutService(state);
        Assert.Equal(15, service.Bindings.Count);
        Assert.Equal("Ctrl+K", service.ComboOf("search"));
        Assert.False(service.IsCustomized("search"));

        // Rebind and persist across instances (the web's sofia.shortcuts.v1).
        service.SetBinding("search", "Ctrl+Shift+K");
        Assert.True(service.IsCustomized("search"));
        Assert.Equal("Ctrl+Shift+K", new ShortcutService(state).ComboOf("search"));
        Assert.NotNull(service.ConflictFor("search", "Ctrl+N"));
        Assert.Equal("session.new", service.ConflictFor("search", "Ctrl+N")!.Id);
        Assert.Null(service.ConflictFor("search", "Ctrl+Shift+K"));

        // Browser-reserved combos are a warning, never a block, and need a modifier.
        Assert.True(ShortcutService.IsBrowserReserved("Ctrl+N"));
        Assert.False(ShortcutService.IsBrowserReserved("Ctrl+Shift+K"));
        Assert.False(ShortcutService.IsValidCombo("K"));
        Assert.True(ShortcutService.IsValidCombo("Ctrl+K"));
        Assert.True(ShortcutService.IsValidCombo("F5"));

        // Reset one and reset all.
        service.ResetBinding("search");
        Assert.Equal("Ctrl+K", service.ComboOf("search"));
        Assert.False(service.IsCustomized("search"));
        service.SetBinding("model.pick", "Ctrl+Shift+M");
        service.SetBinding("agent.pick", "");
        service.ResetAllBindings();
        Assert.Equal("Ctrl+M", service.ComboOf("model.pick"));
        Assert.Equal("Ctrl+Shift+A", service.ComboOf("agent.pick"));

        // Ctrl and Cmd are interchangeable, like the web's matchShortcut.
        Assert.Equal("search", service.MatchFor(Avalonia.Input.Key.K, Avalonia.Input.KeyModifiers.Control)?.Id);
        Assert.Equal("search", service.MatchFor(Avalonia.Input.Key.K, Avalonia.Input.KeyModifiers.Meta)?.Id);
        Assert.Null(service.MatchFor(Avalonia.Input.Key.K, Avalonia.Input.KeyModifiers.Alt));

        // ComboFromKey emits the canonical string the editor stores.
        Assert.Equal("Ctrl+Shift+A", ShortcutService.ComboFromKey(Avalonia.Input.Key.A,
            Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift));
        Assert.Equal("Alt+ArrowUp", ShortcutService.ComboFromKey(Avalonia.Input.Key.Up, Avalonia.Input.KeyModifiers.Alt));
        Assert.Equal(@"Ctrl+\", ShortcutService.ComboFromKey(Avalonia.Input.Key.OemBackslash, Avalonia.Input.KeyModifiers.Control));
    }

    [AvaloniaFact]
    public void Shortcut_recording_and_modal_suspension_silence_the_global_route()
    {
        var state = new LocalState(TempStatePath("shortcuts-route"));
        var service = new ShortcutService(state);
        var fired = 0;
        service.Register("search", () => fired++);
        var args = new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent,
            Key = Avalonia.Input.Key.K,
            KeyModifiers = Avalonia.Input.KeyModifiers.Control,
        };
        service.Recording = true;
        Assert.False(service.TryHandle(args));
        Assert.Equal(0, fired);
        service.Recording = false;
        service.Suspended = true;
        Assert.False(service.TryHandle(args));
        service.Suspended = false;
        Assert.True(service.TryHandle(args));
        Assert.Equal(1, fired);
    }

    [AvaloniaFact]
    public void Right_panel_merges_the_working_tree_over_the_session_and_numbers_the_rows()
    {
        var session = new[]
        {
            new Sofia.Contracts.Domain.FileDiff("a.cs", "@@ -1,2 +1,2 @@\n-old\n+new\n ctx", 1, 1, "M"),
            new Sofia.Contracts.Domain.FileDiff("session-only.cs", "@@ -1 +1 @@\n-a\n+b", 1, 1, "M"),
        };
        var working = new[]
        {
            new Sofia.Contracts.Domain.FileDiff("a.cs", "@@ -1 +1 @@\n-one\n+two", 1, 1, "M"),
            new Sofia.Contracts.Domain.FileDiff("working-only.cs", "@@ -1 +1 @@\n-x\n+y", 1, 1, "A"),
        };
        var merged = RightPanelView.Merge(session, working);
        Assert.Equal(3, merged.Count);
        Assert.Equal("a.cs", merged[0].File);
        Assert.Contains("two", merged.Single(diff => diff.File == "a.cs").Patch);

        var rows = Sofia.App.Features.Transcript.UnifiedDiff.Parse(merged.Single(diff => diff.File == "a.cs").Patch);
        Assert.Equal("one", rows[0].Text);
        Assert.Equal(Sofia.App.Features.Transcript.DiffRowKind.Delete, rows[0].Kind);
        Assert.Equal(1, rows[0].OldNumber);
        Assert.Equal(Sofia.App.Features.Transcript.DiffRowKind.Add, rows[1].Kind);
        Assert.Equal(1, rows[1].NewNumber);

        var panel = new RightPanelView();
        var pane = Host(panel);
        panel.LoadFixture(Sofia.App.Shell.Fixture.PanelDiffs().Select(PanelChange.FromDiff), [], open: false);
        Assert.Equal(4, panel.Changes.Count);
        Assert.All(panel.Changes, change => Assert.NotEmpty(change.Rows));
        Assert.True(panel.PreviewDiff());
        Assert.True(panel.DiffOpen);
        panel.HandleEscape();
        Assert.False(panel.DiffOpen);
        pane.Children.Clear();
    }

    [AvaloniaFact]
    public void Task_card_clamps_inside_the_chat_area_and_persists_its_position()
    {
        var state = new LocalState(TempStatePath("tasks"));
        var host = new Panel { Width = 800, Height = 600 };
        var card = new TaskListPanel { State = state };
        host.Children.Add(card);
        var window = new Window { Width = 800, Height = 600, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

        card.LoadFixture(Fixture.Tasks());
        Assert.True(card.IsOpen);
        Assert.Equal(5, card.Tasks.Count);

        card.PlaceAt(2000, 2000, persist: true);
        Assert.InRange(card.Position.X, 0, 520);
        Assert.InRange(card.Position.Y, 0, 600);
        var stored = state.Get<TaskPosition>(LocalState.TasksPositionKey);
        Assert.NotNull(stored);
        Assert.Equal(card.Position.X, stored!.X, 1);

        card.PlaceAt(120, 160, persist: true);
        Assert.Equal(120, card.Position.X, 1);
        Assert.Equal(160, card.Position.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_dialog_opens_every_tab_from_fixture_data()
    {
        var dialog = new SettingsDialog { FixtureMode = true };
        var window = new Window { Width = 1280, Height = 800, Content = dialog };
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        dialog.LoadFixture();

        foreach (var tab in Enum.GetValues<SettingsDialog.SettingsTab>())
        {
            dialog.Open(tab, load: false);
            Assert.Equal(tab, dialog.CurrentTab);
            Assert.True(dialog.IsOpen);
            var rail = dialog.FindControl<StackPanel>("TabRail")!;
            Assert.Equal(7, rail.Children.Count);
            var body = dialog.FindControl<StackPanel>("TabBody")!;
            Assert.NotEmpty(body.Children);
        }
        Assert.True(dialog.HandleEscape());
        Assert.False(dialog.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Header_shows_the_status_dot_crumbs_and_the_language_menu()
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        var shell = window.ShellView;
        var header = shell.Header;

        // Working badge of the fixture's first conversation: loader glyph and the localized tooltip.
        Assert.True(header.StatusDotState.Visible);
        Assert.Equal("loader", header.StatusDotState.Glyph);
        Assert.Equal("Sofia is working on a task…", header.StatusDotState.Tooltip);
        Assert.Equal("Avalonia shell scaffold", header.TitleText);
        Assert.Contains("sofia", window.ViewModel.ActiveWorkspaceName, StringComparison.OrdinalIgnoreCase);

        header.PreviewMenu("language");
        Assert.Equal(6, header.OpenMenuEntries);
        Assert.True(header.HandleEscape());

        window.ViewModel.Language = "ja";
        header.Rebuild();
        Assert.Equal("Sofia がタスクを処理しています…", header.StatusDotState.Tooltip);
        window.ViewModel.Language = "en";
        window.Close();
    }

    [AvaloniaFact]
    public void Pairing_dialog_renders_the_fixture_code_and_counts_down()
    {
        var dialog = new PairingDialog();
        var window = new Window { Width = 800, Height = 600, Content = dialog };
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        dialog.PreviewFixture();
        Assert.True(dialog.IsVisible);
        Assert.Equal("SOFIA-7Q4K-P2XD", dialog.Code);
        Assert.True(dialog.HasCountdown);
        dialog.Close();
        window.Close();
    }

    [AvaloniaFact]
    public void Spec_043_screens_render_at_1080p_2k_and_4k()
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        var shell = window.ShellView;
        var sizes = new (string Name, int Width, int Height, double Scale)[]
        {
            ("043-settings-general-1080p", 1920, 1080, 1.0),
            ("043-settings-general-2k", 2560, 1440, 1.333),
            ("043-settings-general-4k", 3840, 2160, 2.0),
        };
        shell.Settings.LoadFixture();
        shell.OpenSettings(SettingsDialog.SettingsTab.General);
        foreach (var (name, width, height, scale) in sizes)
        {
            var path = Evidence.File(name + ".png");
            ScreenshotHarness.Capture(window, path, width, height, scale);
            Assert.True(File.Exists(path), path);
            Assert.True(new FileInfo(path).Length > 40_000, name);
        }
        window.Close();
    }

    private static Panel Host(Control child)
    {
        var pane = new Panel { Width = 1920, Height = 1080 };
        pane.Children.Add(child);
        return pane;
    }

    private static string TempStatePath(string name) =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sofia-043-{name}-{Guid.NewGuid():N}.json");
}
