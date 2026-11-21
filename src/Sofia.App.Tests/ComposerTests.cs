using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Sofia.App.Features.Composer;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.Client;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Tests;

/// <summary>
/// Spec 042 gates: the composer's formatting and context-meter rules, the Enter/Shift+Enter/IME key policy, the
/// palette and mention triggers, per-conversation drafts on <see cref="LocalState"/>, the prompt/command payloads,
/// the single-open popover lock and the measured height the transcript pads for.
/// </summary>
public class ComposerTests
{
    public ComposerTests()
    {
        MotionService.Current.ResetParticipants();
        MotionService.Current.EnableManualClock(0);
        MotionService.Current.ReducedMotion = false;
        ScreenshotHarness.FrameHook = null;
    }

    // --- formatting and the context meter -------------------------------------------------------------------

    [Fact]
    public void Formatting_matches_the_web_helpers()
    {
        Assert.Equal("900 B", ComposerFormat.Bytes(900));
        Assert.Equal("1.2 KB", ComposerFormat.Bytes(1229));
        Assert.Equal("2.3 MB", ComposerFormat.Bytes(2_411_724));

        Assert.Equal("999", ComposerFormat.Tokens(999));
        Assert.Equal("21.5K", ComposerFormat.Tokens(21_500));
        Assert.Equal("125K", ComposerFormat.Tokens(125_000));
        Assert.Equal("1.2M", ComposerFormat.Tokens(1_200_000));

        Assert.Equal("12.4s", ComposerFormat.Duration(12_400));
        Assert.Equal("2.25m", ComposerFormat.Duration(135_000));
        Assert.Equal("1.05h", ComposerFormat.Duration(3_780_000));

        Assert.Equal(52, ComposerFormat.HitPercent(9_612, 18_420));
        Assert.Equal("52", ComposerFormat.HitPercentLabel(9_612, 18_420));
        Assert.Equal(99.999, ComposerFormat.HitPercent(1_000, 1_000), 3);

        Assert.Equal("image", ComposerFormat.KindOf("image/png"));
        Assert.Equal("video", ComposerFormat.KindOf("video/webm"));
        Assert.Equal("audio", ComposerFormat.KindOf("audio/mpeg"));
        Assert.Equal("file", ComposerFormat.KindOf("text/markdown"));
        Assert.Equal("music", ComposerFormat.IconForKind("audio"));
    }

    [Fact]
    public void Context_meter_builds_the_web_segments_and_scales_overflow()
    {
        var split = ContextMeter.Build(41_800, 131_072, 3_900, 7_420);
        Assert.NotNull(split);
        Assert.Equal(32, split!.Percent);
        Assert.True(split.HasSplit);
        Assert.Collection(split.Segments,
            system =>
            {
                Assert.Equal("system", system.Key);
                Assert.Equal("#94949B", system.Color);
                Assert.Equal(3_900, system.Value);
            },
            tools =>
            {
                Assert.Equal("tools", tools.Key);
                Assert.Equal("primary", tools.Color);
                Assert.Equal(7_420, tools.Value);
            },
            messages =>
            {
                Assert.Equal("messages", messages.Key);
                Assert.Equal("#5A9BD8", messages.Color);
                Assert.Equal(41_800 - 3_900 - 7_420, messages.Value);
            });
        Assert.InRange(split.Segments.Sum(s => s.Width), 31.8, 31.9);

        // No split from the server: one accent segment over used/limit.
        var plain = ContextMeter.Build(500, 1_000, 0, 0);
        Assert.NotNull(plain);
        Assert.False(plain!.HasSplit);
        Assert.Single(plain.Segments);
        Assert.Equal("total", plain.Segments[0].Key);
        Assert.Equal(50, plain.Segments[0].Width, 3);
        Assert.Equal(50, plain.Percent);

        // Usage above the window (right before a compact) scales down instead of overflowing the track.
        var overflow = ContextMeter.Build(2_000, 1_000, 500, 500);
        Assert.NotNull(overflow);
        Assert.Equal(100, overflow!.Segments.Sum(s => s.Width), 3);

        // Nothing to read before the first request.
        Assert.Null(ContextMeter.Build(0, 0, 0, 0));
        Assert.Equal(0, ContextMeter.Build(0, 1_000, 0, 0)!.Percent);
        Assert.Equal(1, ContextMeter.Build(1, 100_000, 0, 0)!.Percent);
    }

    // --- keys, palette and mention triggers ----------------------------------------------------------------

    [Fact]
    public void Key_policy_sends_breaks_lines_and_respects_ime()
    {
        Assert.Equal(ComposerKeyAction.Send, Decide(Key.Enter));
        Assert.Equal(ComposerKeyAction.None, Decide(Key.Enter, KeyModifiers.Shift));

        // IME: the web reads `isComposing`; Avalonia delivers the composed key as Key.ImeProcessed.
        Assert.Equal(ComposerKeyAction.None, Decide(Key.ImeProcessed));
        Assert.Equal(ComposerKeyAction.None,
            ComposerKeys.Decide(Key.Enter, KeyModifiers.None, false, false, false, false, imeComposing: true));

        // Mention popup wins over the palette; section headers never pick.
        Assert.Equal(ComposerKeyAction.MentionPick, Decide(Key.Enter, mentionVisible: true));
        Assert.Equal(ComposerKeyAction.MentionPick, Decide(Key.Tab, mentionVisible: true));
        Assert.Equal(ComposerKeyAction.MentionDown, Decide(Key.Down, mentionVisible: true));
        Assert.Equal(ComposerKeyAction.MentionClose, Decide(Key.Escape, mentionVisible: true));

        // The palette navigates only when there are matches.
        Assert.Equal(ComposerKeyAction.PaletteDown, Decide(Key.Down, paletteOpen: true, hasCommands: true));
        Assert.Equal(ComposerKeyAction.PalettePick, Decide(Key.Enter, paletteOpen: true, hasCommands: true));
        Assert.Equal(ComposerKeyAction.Send, Decide(Key.Enter, paletteOpen: true, hasCommands: false));
        Assert.Equal(ComposerKeyAction.PaletteClose, Decide(Key.Escape, paletteOpen: true, hasCommands: true));

        // The follow-up menu closes first.
        Assert.Equal(ComposerKeyAction.MenuClose, Decide(Key.Escape, menuOpen: true));
    }

    [Fact]
    public void Palette_and_mention_triggers_match_the_web()
    {
        Assert.True(ComposerKeys.PaletteAllowed("/", false));
        Assert.True(ComposerKeys.PaletteAllowed("/compact", false));
        Assert.False(ComposerKeys.PaletteAllowed("/compact keep", false));
        Assert.False(ComposerKeys.PaletteAllowed("/compact", true));
        Assert.False(ComposerKeys.PaletteAllowed("compact", false));
        Assert.Equal("compact", ComposerKeys.PaletteQuery("/compact keep"));
        Assert.Equal("", ComposerKeys.PaletteQuery("hello"));

        Assert.Equal((4, 5, ""), ComposerKeys.MentionRange("ask @", 5));
        Assert.Equal((4, 14, "src/Sofia"), ComposerKeys.MentionRange("ask @src/Sofia", 14));
        Assert.Equal((0, 6, "build"), ComposerKeys.MentionRange("@build", 6));
        Assert.Null(ComposerKeys.MentionRange("mail@host", 9));
        Assert.Null(ComposerKeys.MentionRange("ask @src Sofia", 14));
    }

    // --- drafts ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Drafts_round_trip_per_conversation_and_survive_a_reload()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sofia-composer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        try
        {
            var local = new LocalState(path);
            var vm = new ComposerViewModel(null, local);
            vm.SetSession(Session("s_1"), "s_1", @"D:\work", "idle");
            vm.Text = "draft one";
            vm.AddAttachmentBytes("notes.md", "text/markdown", "hello"u8.ToArray());
            vm.FlushDraft();

            vm.SetSession(Session("s_2"), "s_2", @"D:\work", "idle");
            Assert.Equal("", vm.Text);
            Assert.Empty(vm.Attachments);
            vm.Text = "draft two";
            vm.FlushDraft();

            vm.SetSession(Session("s_1"), "s_1", @"D:\work", "idle");
            Assert.Equal("draft one", vm.Text);
            Assert.Single(vm.Attachments);
            Assert.Equal("notes.md", vm.Attachments[0].Name);

            // Restart: a fresh process reads the same state.json.
            var reloaded = new ComposerViewModel(null, new LocalState(path));
            reloaded.SetSession(Session("s_1"), "s_1", @"D:\work", "idle");
            Assert.Equal("draft one", reloaded.Text);
            reloaded.SetSession(Session("s_2"), "s_2", @"D:\work", "idle");
            Assert.Equal("draft two", reloaded.Text);

            // Delivery mode is UI-local too.
            Assert.Equal("steer", reloaded.Delivery);
            reloaded.Delivery = "queue";
            Assert.Equal("queue", new LocalState(path).Get<string>(LocalState.DeliveryKey));

            // Chip picks are remembered locally as a fallback; the session still owns the value.
            await reloaded.SetPermissionAsync("plan");
            var chips = new LocalState(path).Get<Dictionary<string, string>>(LocalState.ChipsKey);
            Assert.NotNull(chips);
            Assert.Equal("plan", chips!["permission"]);
            Assert.Equal("plan", reloaded.PermissionMode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // --- payloads -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Send_steer_queue_and_command_post_the_web_payloads()
    {
        var backend = new ComposerBackend();
        var vm = new ComposerViewModel(null, TempState());
        vm.SetBackend(backend);
        vm.SetSession(Session("s_1"), "s_1", @"D:\work", "idle");

        vm.Text = "hello";
        await vm.SubmitAsync();
        var prompt = Assert.Single(backend.Prompts);
        Assert.Equal("hello", prompt.Text);
        Assert.Equal("prompt", prompt.Delivery);
        Assert.Equal("build", prompt.Agent);
        Assert.Equal("ask", prompt.PermissionMode);
        Assert.Equal("medium", prompt.ReasoningMode);
        Assert.Equal("unsloth/nvidia-nemotron-3.5-bolt-e4b", prompt.ModelId);
        Assert.Equal("", vm.Text);

        // An attachment uploads once and its server id goes on the prompt.
        var file = Path.Combine(Path.GetTempPath(), "sofia-attach-" + Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(file, "notes");
        vm.AddAttachment(file);
        await vm.SubmitAsync();
        Assert.Equal("a_1", backend.Prompts[^1].Attachments!.Single());
        Assert.Equal(1, backend.Uploads);
        File.Delete(file);

        // While a run is active the delivery mode is steer or queue.
        vm.SetStatus(SessionStatus.Working);
        vm.Delivery = "steer";
        vm.Text = "keep going";
        await vm.SubmitAsync();
        Assert.Equal("steer", backend.Prompts[^1].Delivery);
        vm.Delivery = "queue";
        vm.Text = "and then";
        await vm.SubmitAsync();
        Assert.Equal("queue", backend.Prompts[^1].Delivery);

        // A "/" command posts to /command with its arguments and clears the composer.
        vm.SetStatus(SessionStatus.Idle);
        vm.Text = "/compact keep the plan";
        await vm.SubmitAsync();
        var command = Assert.Single(backend.Commands);
        Assert.Equal("compact", command.Name);
        Assert.Equal("keep the plan", command.Arguments);
        Assert.Equal("", vm.Text);

        // An unknown slash word is plain input, like the web.
        vm.Text = "/not-a-command";
        await vm.SubmitAsync();
        Assert.Equal("/not-a-command", backend.Prompts[^1].Text);

        // Stop aborts the open session.
        await vm.StopAsync();
        Assert.Equal("s_1", backend.Aborted);
    }

    [Fact]
    public async Task Chips_patch_the_session_and_only_one_popover_opens()
    {
        var backend = new ComposerBackend();
        var vm = new ComposerViewModel(null, TempState());
        vm.SetBackend(backend);
        vm.SetSession(Session("s_1"), "s_1", @"D:\work", "idle");

        await vm.SetPermissionAsync("plan");
        Assert.Equal("plan", backend.LastUpdate!.PermissionMode);
        Assert.Equal("plan", vm.PermissionMode);
        await vm.SetReasoningAsync("high");
        Assert.Equal("high", backend.LastUpdate!.ReasoningMode);
        await vm.SetAgentAsync("plan");
        Assert.Equal("plan", backend.LastUpdate!.Agent);
        await vm.SetModelAsync("claude-sonnet-4.5");
        Assert.Equal("claude-sonnet-4.5", backend.LastUpdate!.ModelId);

        vm.ToggleMenu(ComposerMenu.Model);
        Assert.Equal(ComposerMenu.Model, vm.Menu);
        vm.ToggleMenu(ComposerMenu.Permission);
        Assert.Equal(ComposerMenu.Permission, vm.Menu);
        vm.ToggleMenu(ComposerMenu.Permission);
        Assert.Equal(ComposerMenu.None, vm.Menu);
    }

    [Fact]
    public async Task Mentions_group_agents_sessions_and_files_and_references_reach_the_prompt()
    {
        var backend = new ComposerBackend();
        var vm = new ComposerViewModel(null, TempState());
        vm.SetBackend(backend);
        vm.SetSession(Session("s_1"), "s_1", @"D:\work", "idle");
        await vm.RefreshAgentsAsync();
        await vm.RefreshFilesAsync();

        vm.Text = "ask @";
        vm.UpdateSurfaces(vm.Text.Length);
        Assert.True(vm.MentionVisible);
        var headers = vm.MentionRows.Where(r => r.IsHeader).Select(r => r.Header).ToList();
        Assert.Contains("composer.mentionAgents", headers);
        Assert.Contains("composer.mentionFiles", headers);
        Assert.DoesNotContain(vm.MentionRows, r => r.Agent?.Id == "build"); // primary agents are not mentionable

        // File pick inserts "@path " at the mention range.
        var file = vm.MentionRows.First(r => r.File is not null);
        vm.PickMention(file);
        Assert.StartsWith("ask @" + file.File, vm.Text);
        Assert.False(vm.MentionVisible);
        vm.Text = "";
        vm.UpdateSurfaces(0);

        // A session mention attaches a reference instead of typing text, and rides on the prompt.
        vm.Text = "use @visual";
        vm.UpdateSurfaces(vm.Text.Length);
        await vm.RefreshSessionMentionsAsync();
        vm.UpdateSurfaces(vm.Text.Length);
        var sessionRow = vm.MentionRows.First(r => r.Session is not null);
        vm.PickMention(sessionRow);
        Assert.Equal("use @visual", vm.Text);
        Assert.Single(vm.References);

        vm.SetStatus(SessionStatus.Idle);
        vm.Text = "summarise it";
        await vm.SubmitAsync();
        var reference = Assert.Single(backend.Prompts[^1].References!);
        Assert.Equal("s_3", reference.Id);
    }

    // --- geometry and the harness ---------------------------------------------------------------------------

    [AvaloniaFact]
    public void Measured_height_reaches_the_transcript_padding_and_popovers_do_not_change_it()
    {
        var window = OpenFixture();
        var shell = window.ShellView;
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();

        var composer = shell.ComposerView;
        var oneLine = composer.MeasuredHeight;
        Assert.True(oneLine > 80, $"one-line composer {oneLine}");
        Assert.Equal(oneLine + 20, shell.Transcript.ColumnHostControl.Margin.Bottom, 1);

        // The textarea grows to its 220px cap and the padding follows.
        window.ViewModel.Composer.Text = string.Join('\n', Enumerable.Repeat("line", 8));
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();
        Assert.True(composer.MeasuredHeight > oneLine + 40, $"grown composer {composer.MeasuredHeight}");
        Assert.Equal(composer.MeasuredHeight + 20, shell.Transcript.ColumnHostControl.Margin.Bottom, 1);

        // The attachment strip changes it; the popup layer never does (it lives on a canvas).
        var grown = composer.MeasuredHeight;
        window.ViewModel.Composer.AddAttachmentBytes("composer-shot.png", "image/png", new byte[2_048]);
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();
        Assert.True(composer.MeasuredHeight > grown);

        var withStrip = composer.MeasuredHeight;
        window.ViewModel.Composer.ToggleMenu(ComposerMenu.Model);
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(withStrip, composer.MeasuredHeight, 1);
        Assert.NotEmpty(composer.PopupHost.Children);

        // Escape closes the popover before anything else.
        Assert.True(composer.HandleEscape());
        Assert.Equal(ComposerMenu.None, window.ViewModel.Composer.Menu);
        window.Close();
    }

    [AvaloniaFact]
    public void Extended_text_area_caps_at_220_and_enter_sends_shift_enter_inserts_a_line()
    {
        var window = OpenFixture();
        var shell = window.ShellView;
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();

        var input = shell.ComposerView.InputControl;
        Assert.Equal(220, input.MaxHeight);
        Assert.Equal(15, input.FontSize);

        window.ViewModel.Composer.Text = string.Join('\n', Enumerable.Repeat("x", 40));
        Dispatcher.UIThread.RunJobs();
        Assert.True(input.Bounds.Height <= 220, $"textarea {input.Bounds.Height}");

        // Enter is handled by the composer (not inserted) while Shift+Enter is left to the text box.
        var enter = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter };
        input.RaiseEvent(enter);
        Assert.True(enter.Handled);
        window.Close();
    }

    [Fact]
    public void Composer_shortcuts_are_in_the_shared_registry()
    {
        foreach (var (id, combo) in new[]
                 {
                     ("composer.focus", "Ctrl+L"),
                     ("agent.pick", "Ctrl+Shift+A"),
                     ("model.pick", "Ctrl+M"),
                     ("reasoning.pick", "Ctrl+B"),
                     ("permission.pick", "Ctrl+P"),
                     ("session.stop", "Ctrl+."),
                 })
        {
            var binding = ShortcutService.BindingOf(id);
            Assert.NotNull(binding);
            Assert.Equal(combo, binding!.Combo);
        }
    }

    // --- helpers --------------------------------------------------------------------------------------------

    private static ComposerKeyAction Decide(Key key, KeyModifiers modifiers = KeyModifiers.None,
        bool mentionVisible = false, bool paletteOpen = false, bool hasCommands = true, bool menuOpen = false) =>
        ComposerKeys.Decide(key, modifiers, mentionVisible, paletteOpen, hasCommands, menuOpen);

    private static LocalState TempState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sofia-composer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new LocalState(Path.Combine(directory, "state.json"));
    }

    private static Session Session(string id) => new()
    {
        Id = id,
        WorkspaceId = "ws_1",
        Title = "Composer test",
        ModelId = "unsloth/nvidia-nemotron-3.5-bolt-e4b",
        Agent = "build",
        PermissionMode = "ask",
        ReasoningMode = "medium",
        Status = "idle",
    };

    private static MainWindow OpenFixture()
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>Backend double: records prompts, commands, uploads, aborts and session patches.</summary>
    private sealed class ComposerBackend : ISofiaBackend
    {
        public List<PromptInput> Prompts { get; } = [];

        public List<CommandRequest> Commands { get; } = [];

        public UpdateSessionRequest? LastUpdate { get; private set; }

        public string? Aborted { get; private set; }

        public int Uploads { get; private set; }

        private Session? _session;

        public Task<string> PromptAsync(string id, PromptInput input, CancellationToken ct = default)
        {
            Prompts.Add(input);
            return Task.FromResult("m_" + Prompts.Count);
        }

        public Task<string> CommandAsync(string id, CommandRequest request, CancellationToken ct = default)
        {
            Commands.Add(request);
            return Task.FromResult("m_cmd");
        }

        public Task AbortAsync(string id, CancellationToken ct = default)
        {
            Aborted = id;
            return Task.CompletedTask;
        }

        public Task<Session> UpdateSessionAsync(string id, UpdateSessionRequest request, CancellationToken ct = default)
        {
            LastUpdate = request;
            _session ??= Session(id);
            if (request.Agent is { Length: > 0 } agent) _session.Agent = agent;
            if (request.ModelId is { Length: > 0 } model) _session.ModelId = model;
            if (request.PermissionMode is { Length: > 0 } permission) _session.PermissionMode = permission;
            if (request.ReasoningMode is { Length: > 0 } reasoning) _session.ReasoningMode = reasoning;
            return Task.FromResult(_session);
        }

        public Task<Attachment> UploadAsync(Stream content, string fileName, string contentType,
            CancellationToken ct = default)
        {
            Uploads++;
            return Task.FromResult(new Attachment
            {
                Id = "a_" + Uploads,
                Name = fileName,
                Mime = contentType,
                Kind = ComposerFormat.KindOf(contentType),
                Bytes = content.Length,
            });
        }

        public Task<JsonElement> CommandsAsync(string? directory, CancellationToken ct = default) =>
            Task.FromResult(Json("""
                [{"name":"compact","description":"Summarise the conversation","source":"builtin"},
                 {"name":"do","description":"Execute a spec","source":"project"}]
                """));

        public Task<JsonElement> AgentsAsync(string? directory, CancellationToken ct = default) =>
            Task.FromResult(Json("""
                [{"id":"build","name":"build","description":"Default","mode":"primary","source":"builtin"},
                 {"id":"plan","name":"plan","description":"Plans","mode":"primary","source":"builtin"},
                 {"id":"general","name":"general","description":"Handles a task","mode":"subagent","source":"builtin"},
                 {"id":"explore","name":"explore","description":"Maps the code","mode":"subagent","source":"builtin"}]
                """));

        public Task<List<string>> FilesAsync(string directory, CancellationToken ct = default) =>
            Task.FromResult(new List<string>
            {
                "src/Sofia.App/Features/Composer/ComposerView.axaml",
                "docs/app_ui.md",
            });

        public Task<JsonElement> VcsAsync(string directory, CancellationToken ct = default) =>
            Task.FromResult(Json("""{"isRepo":true,"branch":"feature/new_crossplatform","branches":[]}"""));

        public Task<JsonElement> SearchAsync(string query, string? workspaceId = null, bool includeArchived = false,
            bool includeSubagents = false, int limit = 20, CancellationToken ct = default) =>
            Task.FromResult(Json("""
                {"results":[{"sessionId":"s_3","workspaceId":"ws_1","title":"Working border motion parity","archived":false}]}
                """));

        public string AttachmentUrl(string id) => "/api/attachments/" + id;

        private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

        // Unused by these tests.
        public Task<JsonElement> HealthAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ServerInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> CreatePairingCodeAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> PairAsync(string code, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<WorkspaceView>> ListWorkspacesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkspaceView> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkspaceView> UpdateWorkspaceAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWorkspaceAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> CreateSessionAsync(string workspaceId, CreateSessionRequest? request = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReorderWorkspacesAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReorderSessionsAsync(string workspaceId, IReadOnlyList<string> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, string>> StatusesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> SessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> ArchiveSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> UnarchiveSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> PinSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> UnpinSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveQueuedAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompactAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> ContinueAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> RetryAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> RevertAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> BranchAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReplyPermissionAsync(string id, string requestId, string decision, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AnswerAsync(string id, string formId, IReadOnlyList<JsonElement?> answers, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<FileDiff>> DiffAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> AddSkillAsync(string id, string skillId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> RemoveSkillAsync(string id, string skillId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TaskItem>> TasksAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearTasksAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> AddSessionMcpAsync(string id, SessionMcpRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveSessionMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ExportAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> ImportAsync(ImportRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<StatsView> StatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ModelsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetModelEnabledAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetModelSingleClientAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ProvidersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ProviderPresetsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> CreateProviderAsync(CreateProviderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> UpdateProviderAsync(string id, UpdateProviderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteProviderAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> RefreshProvidersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ProbeProviderAsync(ProbeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> CopilotLoginAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> CopilotPollAsync(string deviceCode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> McpServersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> AddMcpAsync(CreateMcpRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ConnectMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> DisconnectMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> LspServersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> RestartLspAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> InstallLspAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> SkillsAsync(string? directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<FileDiff>> VcsDiffAsync(string directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> WorktreesAsync(string directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> CreateWorktreeAsync(CreateWorktreeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWorktreeAsync(DeleteWorktreeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task OpenAsync(string path, string? app = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ProcessesAsync(string? sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ProcessOutputAsync(string id, int tail = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> KillProcessAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ClearProcessesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> BrowsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> SessionBrowserAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> OpenBrowserAsync(string sessionId, string? browser, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseBrowserAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> ConfigAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> PatchConfigAsync(ConfigPatch patch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<JsonElement> KnowledgeAsync(string? workspaceId = null, string? agent = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteKnowledgeAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<SofiaEventDto> EventsAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }
}
