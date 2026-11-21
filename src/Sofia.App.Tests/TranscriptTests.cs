using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sofia.App.Features.Transcript;
using Sofia.App.Features.Transcript.CardViewModels;
using Sofia.App.Features.Transcript.Markdown;
using Sofia.App.Services;
using Sofia.App.Shell;
using Sofia.App.Theme;
using Sofia.Contracts.Domain;
using Sofia.Contracts.Events;

namespace Sofia.App.Tests;

/// <summary>
/// Spec 041 gates: the untrusted-markdown pipeline (blocks, escaping, link filtering, literal math), the block model's
/// rendering, the transcript window/stream/status rules and the geometry of the rows the screenshot gate captures.
/// </summary>
public class TranscriptTests
{
    public TranscriptTests()
    {
        // Every animation of this class is asserted on the shared manual clock (the app's normal clock keeps
        // running across tests otherwise, and a stop-motion assertion would read wall time). Participants of a
        // closed headless window are dropped explicitly: the headless platform does not always detach its tree.
        MotionService.Current.ResetParticipants();
        MotionService.Current.EnableManualClock(0);
        MotionService.Current.ReducedMotion = false;
    }

    // --- markdown model ------------------------------------------------------------------------------------

    [Fact]
    public void Markdown_parses_gfm_blocks_in_order()
    {
        var blocks = MarkdownParser.Parse("""
# Title

Paragraph with **strong**, *emphasis*, `code` and a [link](https://example.com).

- one
  - nested
- two

1. first
2. second

> quoted

```csharp
var x = 1;
```

| a | b |
| --- | --- |
| 1 | 2 |

---
""");

        Assert.Collection(blocks,
            block => Assert.IsType<MarkdownHeading>(block),
            block => Assert.IsType<MarkdownParagraph>(block),
            block => Assert.IsType<MarkdownList>(block),
            block => Assert.IsType<MarkdownList>(block),
            block => Assert.IsType<MarkdownQuote>(block),
            block => Assert.IsType<MarkdownCodeBlock>(block),
            block => Assert.IsType<MarkdownTable>(block),
            block => Assert.IsType<MarkdownRule>(block));
    }

    [Fact]
    public void Markdown_keeps_raw_html_as_text_and_never_emits_markup()
    {
        var blocks = MarkdownParser.Parse("""
<div class="x">raw</div>
<script>alert('x')</script>
Inline <img src=x onerror=alert(1)> tag.
""");

        string Text(MarkdownBlock block) => block switch
        {
            MarkdownHtml html => html.Text,
            MarkdownParagraph paragraph => string.Concat(paragraph.Runs.Select(run => run.Text)),
            _ => "",
        };
        var text = string.Join('\n', blocks.Select(Text));
        Assert.Contains("<div", text, StringComparison.Ordinal);
        Assert.Contains("<script>", text, StringComparison.Ordinal);
        Assert.Contains("onerror", text, StringComparison.Ordinal);
        // No block ever carries a URL out of raw HTML.
        Assert.DoesNotContain(blocks.OfType<MarkdownParagraph>().SelectMany(p => p.Runs), run => run.Url is not null);
        Assert.All(blocks, block => Assert.True(
            block is MarkdownHtml or MarkdownParagraph,
            $"unexpected block {block.GetType().Name}"));
    }

    [Theory]
    [InlineData("https://example.com/path", true)]
    [InlineData("http://127.0.0.1:5199/api/x?y=1", true)]
    [InlineData("mailto:ivan@example.com", true)]
    [InlineData("javascript:alert('x')", false)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("vbscript:msgbox(1)", false)]
    [InlineData("/relative/path", false)]
    [InlineData("//evil.example/x", false)]
    [InlineData("", false)]
    public void Only_absolute_http_https_and_mailto_urls_are_navigable(string url, bool safe) =>
        Assert.Equal(safe, MarkdownParser.IsNavigable(url));

    [Fact]
    public void Markdown_keeps_only_safe_links_and_alt_text_for_images()
    {
        var blocks = MarkdownParser.Parse(
            "[ok](https://example.com) [bad](javascript:alert(1)) ![alt text](https://example.com/a.png) <https://auto.example>");
        var runs = blocks.OfType<MarkdownParagraph>().Single().Runs;

        var links = runs.Where(run => run.Kind == MarkdownRunKind.Link).ToList();
        Assert.Equal(["https://example.com", "https://auto.example"], links.Select(run => run.Url));
        Assert.Contains(runs, run => run.Kind == MarkdownRunKind.Image && run.Text == "alt text");
        Assert.DoesNotContain(runs, run => run.Url is not null && !run.Url.StartsWith("http", StringComparison.Ordinal));
        // The bad link's label survives as plain text (the web renders the inner content, not the anchor).
        Assert.Contains(runs, run => run.Kind == MarkdownRunKind.Text && run.Text.Contains("bad", StringComparison.Ordinal));
    }

    [Fact]
    public void Markdown_leaves_math_literal()
    {
        var blocks = MarkdownParser.Parse("Vector $\\rightarrow$ and $x^2 + y^2 = z^2$.");
        var text = string.Concat(blocks.OfType<MarkdownParagraph>().Single().Runs.Select(run => run.Text));
        Assert.Contains("$\\rightarrow$", text, StringComparison.Ordinal);
        Assert.Contains("$x^2 + y^2 = z^2$", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Markdown_renders_a_table_and_a_nested_list()
    {
        var blocks = MarkdownParser.Parse("| a | b |\n| --- | --- |\n| 1 | 2 |\n\n- one\n  - nested");
        var table = Assert.IsType<MarkdownTable>(blocks[0]);
        Assert.Equal(2, table.Header.Count);
        Assert.Single(table.Rows);
        var list = Assert.IsType<MarkdownList>(blocks[1]);
        Assert.Single(list.Items);
        Assert.Contains(list.Items[0].Blocks, block => block is MarkdownList);
    }

    [AvaloniaFact]
    public void Markdown_prose_renders_wrapped_runs_and_caches_the_parse()
    {
        MarkdownCache.Clear();
        var prose = new MarkdownProse { MaxWidth = 320 };
        var source = "A paragraph with `inline code` and a [safe link](https://example.com) that must wrap across lines " +
                     "when the measure is narrow enough for it.";
        prose.SetSource(source);
        prose.Measure(new Size(320, double.PositiveInfinity));

        Assert.True(prose.Children.Count > 0);
        // A wrapped paragraph must report every line box (the inline-code overlap regression from the first capture).
        Assert.True(prose.DesiredSize.Height > 24, $"height {prose.DesiredSize.Height}");
        Assert.Equal(1, MarkdownCache.Count);

        prose.SetSource(source);
        Assert.Equal(1, MarkdownCache.Count);
    }

    [AvaloniaFact]
    public void Injected_markdown_cannot_forge_a_navigation_target()
    {
        var prose = new MarkdownProse();
        prose.SetSource("[click](javascript:alert(1)) [also](data:text/html,<b>x</b>) <a href=\"file:///c:/x\">raw</a>");
        prose.Measure(new Size(600, double.PositiveInfinity));

        var blocks = prose.Blocks;
        Assert.DoesNotContain(blocks.OfType<MarkdownParagraph>().SelectMany(p => p.Runs), run => run.Url is not null);
        // Raw HTML is text, so no HyperlinkButton (or any other control) can appear in the rendered tree.
        Assert.DoesNotContain(Descendants(prose), control => control is HyperlinkButton);
    }

    [AvaloniaFact]
    public void Pill_renders_its_white_label_on_the_fill()
    {
        var pill = TranscriptUi.Pill("Confirm", 12, "#FFFFFF", ThemeLookup.Brush("BrushPrimary"));
        var label = Assert.IsType<TextBlock>(pill.Content);
        Assert.Equal("Confirm", label.Text);
        Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);
        Assert.True(label.FontSize > 0, $"font {label.FontSize}");
        // The Button template is not applied outside a visual tree, so the label is what gets measured here.
        label.Measure(Size.Infinity);
        Assert.True(label.DesiredSize.Height > 8, $"height {label.DesiredSize.Height}");
        Assert.True(label.DesiredSize.Width > 20, $"width {label.DesiredSize.Width}");
    }

    // --- view model rules ------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Transcript_windows_at_seventy_messages_with_a_gap()
    {
        var vm = new TranscriptViewModel();
        var messages = Enumerable.Range(0, 90)
            .Select(i => new Message
            {
                Id = $"m_{i}",
                SessionId = "s_1",
                Role = i % 2 == 0 ? "user" : "assistant",
                Text = "message " + i,
            })
            .ToList();
        vm.Load(messages, "s_1", "idle");

        var rows = vm.Rows;
        Assert.Contains(rows, row => row.IsGap);
        // The window keeps the newest 70 messages plus every user prompt and the first reply (web ChatMessages.vue).
        var visible = rows.Where(row => row.Message is not null).Select(row => row.Message!).ToList();
        var expected = messages.Where((message, index) =>
            index >= messages.Count - TranscriptViewModel.MessageWindow || message.Role == "user" || index == 1).Count();
        Assert.Equal(expected, visible.Count);
        Assert.Equal(messages.Count(m => m.Role == "user"), visible.Count(message => message.Role == "user"));
    }

    [AvaloniaFact]
    public void Streaming_appends_text_and_reasoning_to_the_bound_message()
    {
        var vm = new TranscriptViewModel();
        vm.Load([], "s_1", "working");
        vm.Apply(Created("m_1", ""));
        vm.AppendDelta("m_1", "reasoning", "thinking ");
        vm.AppendDelta("m_1", "reasoning", "hard");
        vm.AppendDelta("m_1", "text", "Hello ");

        var message = vm.Messages.Single();
        Assert.Equal("thinking hard", message.Reasoning);
        Assert.Equal("Hello ", message.Text);
        Assert.True(vm.Answering);
        Assert.False(vm.ShowSpinner);
    }

    [AvaloniaFact]
    public void Spinner_covers_only_the_first_output_of_a_run()
    {
        var vm = new TranscriptViewModel();
        vm.Load([], "s_1", "working");
        vm.Apply(Created("m_1", ""));
        Assert.True(vm.ShowSpinner);

        vm.AppendDelta("m_1", "text", "…");
        Assert.False(vm.ShowSpinner);

        vm.Apply(new SofiaEventDto(Contracts.Events.EventTypes.SessionStatus, "s_1", "ws_1",
            System.Text.Json.JsonSerializer.SerializeToElement(new { status = "idle" })));
        Assert.Equal("idle", vm.Status);
    }

    [AvaloniaFact]
    public void Message_updated_preserves_tool_fold_and_question_picks()
    {
        var vm = new TranscriptViewModel();
        var message = new Message
        {
            Id = "m_1",
            SessionId = "s_1",
            Role = "assistant",
            Text = "hi",
            ToolCalls = [new ToolCall { Id = "tc_1", Name = "read", Status = "success", Summary = "read", Duration = "1ms" }],
            QuestionSet = new QuestionSetCard
            {
                FormId = "f_1",
                Questions = [new QuestionItem { Prompt = "q1", Options = ["a"], FieldKey = "a" }],
            },
        };
        vm.Load([message], "s_1", "idle");
        var model = vm.Messages.Single();
        model.Tools[0].Expanded = false;
        model.Cards.QuestionSet!.Pick(0, "a");

        vm.Apply(new SofiaEventDto(Contracts.Events.EventTypes.MessageUpdated, "s_1", "ws_1",
            System.Text.Json.JsonSerializer.SerializeToElement(message, Contracts.Infrastructure.Json.Options)));

        var updated = vm.Messages.Single();
        Assert.False(updated.Tools[0].Expanded);
        Assert.Contains("a", updated.Cards.QuestionSet!.Picks[0]);
    }

    [AvaloniaFact]
    public void Permission_and_question_answers_post_the_web_payloads()
    {
        var backend = new RecordingBackend();
        var actions = new TranscriptActions { Backend = backend };
        var vm = new TranscriptViewModel();
        vm.Actions.Backend = backend;

        var permissionMessage = new Message
        {
            Id = "m_1",
            SessionId = "s_1",
            Role = "assistant",
            Permission = new PermissionCard
            {
                Tool = "command",
                Description = "run it",
                Choices = ["Allow once", "Allow for this session", "Deny"],
                RequestId = "req_1",
            },
        };
        var vm2 = new TranscriptViewModel { };
        vm2.Actions.Backend = backend;
        vm2.Load([permissionMessage], "s_1", "permission");
        var model = vm2.Messages.Single();

        Assert.Equal("always", PermissionCardViewModel.Decision("Allow for this session"));
        Assert.Equal("reject", PermissionCardViewModel.Decision("Deny"));
        Assert.Equal("once", PermissionCardViewModel.Decision("Allow once"));
        model.ResolvePermissionAsync("Deny");
        Assert.Equal("s_1|req_1|reject", backend.LastPermission);

        var questionMessage = new Message
        {
            Id = "m_2",
            SessionId = "s_1",
            Role = "assistant",
            Question = new QuestionCard
            {
                Prompt = "pick",
                Options = ["a", "b"],
                Multi = true,
                FormId = "form_1",
            },
        };
        vm2.Load([questionMessage], "s_1", "question");
        var question = vm2.Messages.Single();
        question.AnswerQuestionAsync(["a", "b"]);
        Assert.Equal("s_1|form_1|[a,b]", backend.LastAnswer);
    }

    [AvaloniaFact]
    public void Cache_health_flags_a_low_hit_step_after_the_first()
    {
        var messages = new List<Message>
        {
            new() { Id = "m_1", Role = "user", Text = "x" },
            new() { Id = "m_2", Role = "assistant", Text = "a", Usage = new Usage { Input = 1000, CacheRead = 800 } },
            new() { Id = "m_3", Role = "assistant", Text = "b", Model = "other", Usage = new Usage { Input = 1000, CacheRead = 5 } },
        };
        var breaks = CacheHealth.Annotate(messages);
        Assert.Null(breaks[1]);
        Assert.NotNull(breaks[2]);
        Assert.Contains("cache.reasonModel", breaks[2]!.Reasons);
    }

    [AvaloniaFact]
    public void Store_flush_feeds_the_transcript_in_order()
    {
        var store = new HarnessStore();
        var transcript = new TranscriptViewModel();
        transcript.Load([new Message { Id = "m_1", SessionId = "s_1", Role = "assistant", Text = "" }], "s_1", "working");
        store.DeltaFlushed += transcript.AppendDelta;

        store.Apply(new SofiaEventDto(EventTypes.MessageDelta, "s_1", "ws_1",
            System.Text.Json.JsonSerializer.SerializeToElement(new { messageId = "m_1", field = "reasoning", delta = "thinking" })));
        store.Apply(new SofiaEventDto(EventTypes.MessageDelta, "s_1", "ws_1",
            System.Text.Json.JsonSerializer.SerializeToElement(new { messageId = "m_1", field = "text", delta = "Hello " })));
        Assert.Equal(2, store.PendingDeltas.Count);
        store.FlushDeltas();

        Assert.Equal("thinking", transcript.Messages.Single().Reasoning);
        Assert.Equal("Hello ", transcript.Messages.Single().Text);
        Assert.Empty(store.PendingDeltas);
    }

    [AvaloniaFact]
    public async Task Session_snapshot_loads_messages_status_and_the_queue()
    {
        var backend = new RecordingBackend
        {
            SessionJson = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                id = "s_1",
                status = "working",
                messages = new object[]
                {
                    new { id = "m_1", sessionId = "s_1", role = "user", text = "hi" },
                    new { id = "m_2", sessionId = "s_1", role = "assistant", text = "ok", reasoning = "hmm" },
                },
                queuedMessages = new object[]
                {
                    new { id = "q_1", text = "later", delivery = "steer", attachments = Array.Empty<object>() },
                },
            }, Contracts.Infrastructure.Json.Options),
        };
        var vm = new TranscriptViewModel();
        await vm.LoadSessionAsync(backend, "s_1");

        Assert.Equal(2, vm.Messages.Count);
        Assert.Equal("working", vm.Status);
        Assert.Equal("hmm", vm.Messages[1].Reasoning);
        Assert.Single(vm.Pending);
        Assert.Equal("steer", vm.Pending[0].PendingDelivery);
    }

    // --- geometry ------------------------------------------------------------------------------------------

    [AvaloniaFact]
    public void Transcript_column_is_1232_wide_and_padded_by_the_composer()
    {
        var window = Open("idle");
        var shell = window.ShellView;
        shell.Measure(new Size(1920, 1080));
        shell.Arrange(new Rect(0, 0, 1920, 1080));
        Dispatcher.UIThread.RunJobs();

        var transcript = shell.Transcript;
        Assert.Equal(1232, transcript.ColumnHostControl.MaxWidth);
        Assert.Equal(1232, transcript.ColumnHostControl.Bounds.Width);
        // 1.25rem top, 1.5rem sides and composer height + 1.25rem bottom.
        Assert.Equal(20, transcript.ColumnHostControl.Margin.Top, 1);
        Assert.Equal(24, transcript.ColumnHostControl.Margin.Left, 1);
        // The composer publishes its measured height (spec 042); the padding is that height + 1.25rem.
        var composerHeight = shell.ComposerView.MeasuredHeight;
        Assert.True(composerHeight > 80, $"composer height {composerHeight}");
        Assert.Equal(composerHeight + 20, transcript.ColumnHostControl.Margin.Bottom, 1);

        var row = transcript.Rows["m_2"];
        row.Measure(new Size(transcript.ContentWidth, double.PositiveInfinity));
        Assert.True(row.Bounds.Width > 0);
        window.Close();
    }

    [AvaloniaFact]
    public void Rows_are_hover_revealed_and_folds_are_stop_motion()
    {
        var window = Open("streaming");
        var transcript = window.ShellView.Transcript;
        var row = transcript.Rows["m_2"];

        // Message actions: .15s fade (motion #4).
        MotionService.Current.Seek(0);
        Assert.True(transcript.HoverMessage("m_2"));
        var opacities = new List<double>();
        foreach (var ms in new[] { 0d, 75, 150 })
        {
            MotionService.Current.Seek(ms);
            opacities.Add(transcript.RevealOpacity("m_2"));
        }
        Assert.Equal(0, opacities[0], 2);
        Assert.InRange(opacities[1], 0.75, 0.85);
        Assert.Equal(1, opacities[2], 2);

        // Reasoning fold (.2s ease-in-out, collapsed -> expanded).
        MotionService.Current.Seek(0);
        var panel = row.ReasoningPanel;
        Assert.NotNull(panel);
        Assert.False(panel!.IsExpanded || row.ViewModel.Cards.Reasoning!.Expanded);
        row.ToggleFold(0);
        Assert.True(panel.IsExpanded);
        var heights = new List<double>();
        foreach (var ms in new[] { 0d, 100, 200 })
        {
            MotionService.Current.Seek(ms);
            Dispatcher.UIThread.RunJobs();
            heights.Add(panel.MaxHeight);
        }
        Assert.True(heights[1] > heights[0], $"{heights[0]} -> {heights[1]}");
        window.Close();
    }

    [AvaloniaFact]
    public void Carousel_slides_32px_over_220ms_and_honours_reduced_motion()
    {
        var window = Open("questions");
        var transcript = window.ShellView.Transcript;
        var row = transcript.Rows["m_2"];
        Assert.NotNull(row.Carousel);

        MotionService.Current.Seek(0);
        Assert.True(transcript.PreviewCarouselStep("m_2", 1));
        var carousel = row.Carousel!;
        Assert.True(carousel.Stage.IsAnimating);
        MotionService.Current.Seek(110);
        Assert.Equal(1, carousel.Stage.Direction);
        Assert.NotEqual(0, carousel.ViewModel.Slide);
        MotionService.Current.Seek(220); // leave ends, entry begins
        MotionService.Current.Seek(330);
        MotionService.Current.Seek(440); // entry ends
        Assert.False(carousel.Stage.IsAnimating, $"children {carousel.Stage.Children.Count}");
        window.Close();

        // Reduced motion: the page changes without any slide.
        MotionService.Current.ReducedMotion = true;
        var reduced = Open("questions");
        var reducedRow = reduced.ShellView.Transcript.Rows["m_2"];
        MotionService.Current.Seek(0);
        Assert.True(MotionService.Current.ReducedMotion);
        reduced.ShellView.Transcript.PreviewCarouselStep("m_2", 1);
        Assert.Single(reducedRow.Carousel!.Stage.Children);
        Assert.False(reducedRow.Carousel.Stage.IsAnimating);
        MotionService.Current.ReducedMotion = false;
        reduced.Close();
    }

    [AvaloniaFact]
    public void Compaction_pulse_and_retry_spin_use_the_motion_contract()
    {
        var compaction = Open("compaction");
        var row = compaction.ShellView.Transcript.Rows["m_2"];
        Assert.NotNull(row.CompactionText);
        var opacities = new List<double>();
        foreach (var ms in new[] { 0d, 350, 700, 1050, 1400 })
        {
            MotionService.Current.Seek(ms);
            opacities.Add(row.CompactionText!.Opacity);
        }
        Assert.Equal(1, opacities[0], 2);
        Assert.InRange(opacities[2], 0.5, 0.6);
        Assert.Equal(1, opacities[4], 2);
        compaction.Close();

        var retry = Open("retry");
        var retryRow = retry.ShellView.Transcript.Rows["m_2"];
        Assert.NotNull(retryRow.RetryIcon);
        Assert.True(retryRow.RetryIcon!.Spin);
        Assert.Equal(1800, retryRow.RetryIcon.SpinMs);
        MotionService.Current.Seek(450);
        Assert.InRange(retryRow.RetryIcon.SpinAngle, 89, 91);
        retry.Close();
    }

    [AvaloniaFact]
    public void Lightbox_opens_full_screen_and_closes_with_escape()
    {
        var window = Open("lightbox");
        var transcript = window.ShellView.Transcript;
        Assert.True(transcript.PreviewLightbox("m_1"));
        Assert.True(transcript.HandleEscape());
        Assert.False(transcript.HandleEscape());
        window.Close();
    }

    private static MainWindow Open(string state)
    {
        var window = new MainWindow(fixture: true);
        window.Show();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
        window.ViewModel.LoadTranscriptState(state);
        window.ShellView.Attach(window.ViewModel);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static SofiaEventDto Created(string id, string text) => new(
        Contracts.Events.EventTypes.MessageCreated,
        "s_1",
        "ws_1",
        System.Text.Json.JsonSerializer.SerializeToElement(
            new { id, sessionId = "s_1", role = "assistant", text },
            Contracts.Infrastructure.Json.Options));

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Control control)
            {
                yield return control;
                foreach (var nested in Descendants(control)) yield return nested;
            }
        }
    }

    /// <summary>Minimal backend double recording the permission/question payloads.</summary>
    private sealed class RecordingBackend : Sofia.Client.ISofiaBackend
    {
        public string? LastPermission { get; private set; }

        public string? LastAnswer { get; private set; }

        /// <summary>Snapshot returned by <see cref="SessionAsync"/> (SessionDto shape).</summary>
        public System.Text.Json.JsonElement SessionJson { get; set; }

        public Task ReplyPermissionAsync(string id, string requestId, string decision, CancellationToken ct = default)
        {
            LastPermission = $"{id}|{requestId}|{decision}";
            return Task.CompletedTask;
        }

        public Task AnswerAsync(string id, string formId, IReadOnlyList<System.Text.Json.JsonElement?> answers, CancellationToken ct = default)
        {
            var values = answers.Select(answer => answer?.ValueKind == System.Text.Json.JsonValueKind.Array
                ? "[" + string.Join(',', answer.Value.EnumerateArray().Select(v => v.GetString())) + "]"
                : answer?.ToString());
            LastAnswer = $"{id}|{formId}|{string.Join(',', values)}";
            return Task.CompletedTask;
        }

        // Everything else is unused by these tests.
        public Task<System.Text.Json.JsonElement> HealthAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ServerInfoAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> CreatePairingCodeAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> PairAsync(string code, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<WorkspaceView>> ListWorkspacesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkspaceView> CreateWorkspaceAsync(CreateWorkspaceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WorkspaceView> UpdateWorkspaceAsync(string id, UpdateWorkspaceRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWorkspaceAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> CreateSessionAsync(string workspaceId, CreateSessionRequest? request = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReorderWorkspacesAsync(IReadOnlyList<string> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ReorderSessionsAsync(string workspaceId, IReadOnlyList<string> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Dictionary<string, string>> StatusesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> SessionAsync(string id, CancellationToken ct = default) => Task.FromResult(SessionJson);
        public Task<Session> UpdateSessionAsync(string id, UpdateSessionRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> ArchiveSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> UnarchiveSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> PinSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> UnpinSessionAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> PromptAsync(string id, PromptInput input, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CommandAsync(string id, CommandRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AbortAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveQueuedAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CompactAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> ContinueAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> RetryAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> RevertAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> BranchAsync(string id, string messageId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<FileDiff>> DiffAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> AddSkillAsync(string id, string skillId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> RemoveSkillAsync(string id, string skillId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<TaskItem>> TasksAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearTasksAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> AddSessionMcpAsync(string id, SessionMcpRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveSessionMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ExportAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Session> ImportAsync(ImportRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> SearchAsync(string query, string? workspaceId = null, bool includeArchived = false, bool includeSubagents = false, int limit = 20, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<StatsView> StatsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ModelsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetModelEnabledAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetModelSingleClientAsync(IReadOnlyList<string> ids, bool enabled, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ProvidersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ProviderPresetsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> CreateProviderAsync(CreateProviderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> UpdateProviderAsync(string id, UpdateProviderRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteProviderAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> RefreshProvidersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ProbeProviderAsync(ProbeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> CopilotLoginAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> CopilotPollAsync(string deviceCode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> McpServersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> AddMcpAsync(CreateMcpRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ConnectMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> DisconnectMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteMcpAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> LspServersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> RestartLspAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> InstallLspAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> AgentsAsync(string? directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> CommandsAsync(string? directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> SkillsAsync(string? directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> FilesAsync(string directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> VcsAsync(string directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<FileDiff>> VcsDiffAsync(string directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> WorktreesAsync(string directory, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> CreateWorktreeAsync(CreateWorktreeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteWorktreeAsync(DeleteWorktreeRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Attachment> UploadAsync(Stream content, string fileName, string contentType, CancellationToken ct = default) => throw new NotImplementedException();
        public string AttachmentUrl(string id) => id;
        public Task OpenAsync(string path, string? app = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ProcessesAsync(string? sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ProcessOutputAsync(string id, int tail = 200, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> KillProcessAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ClearProcessesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> BrowsersAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> SessionBrowserAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> OpenBrowserAsync(string sessionId, string? browser, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CloseBrowserAsync(string sessionId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> ConfigAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> PatchConfigAsync(ConfigPatch patch, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<System.Text.Json.JsonElement> KnowledgeAsync(string? workspaceId = null, string? agent = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteKnowledgeAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public IAsyncEnumerable<SofiaEventDto> EventsAsync(CancellationToken ct = default) => Empty();
    }

    private static async IAsyncEnumerable<SofiaEventDto> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }
}
