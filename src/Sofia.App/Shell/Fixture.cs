using Sofia.Contracts.Domain;
using Sofia.App.Features.Sidebar;

namespace Sofia.App.Shell;

/// <summary>
/// Deterministic sample data for <c>--fixture</c> and the screenshot harness (no server, no <c>~/.sofia</c> reads). It
/// mirrors the shape the engine returns from <c>/api/workspaces</c> so the shell chrome is exercised exactly as it will
/// be with live data: pinned and working sessions, a permission prompt, a question, a chat group and a subagent.
/// </summary>
public static class Fixture
{
    public static readonly DateTime Base = new(2026, 11, 20, 21, 40, 0, DateTimeKind.Utc);

    public static List<WorkspaceView> Workspaces() =>
    [
        new()
        {
            Id = "ws_sofia",
            Name = "sofia",
            Path = @"D:\repos\opensource\sofia",
            Kind = "folder",
            Icon = "code",
            Expanded = true,
            Order = 0,
            Conversations =
            [
                Session("s_1", "ws_sofia", "Avalonia shell scaffold", minutes: 0, pinned: true),
                Session("s_2", "ws_sofia", "Design tokens + Lucide icon port", minutes: 12),
                Session("s_3", "ws_sofia", "Working border motion parity", minutes: 47),
                Session("s_4", "ws_sofia", "Review the SQLite ordering fix", minutes: 180),
            ],
        },
        new()
        {
            Id = "ws_site",
            Name = "website",
            Path = @"D:\repos\ivans\website",
            Kind = "folder",
            Icon = "globe",
            Expanded = true,
            Order = 1,
            Conversations =
            [
                Session("s_5", "ws_site", "Fix the contact form validation", minutes: 320),
                Session("s_6", "ws_site", "Publish the new pricing page", minutes: 1500, pinned: true),
            ],
        },
        new()
        {
            Id = "ws_chat",
            Name = "Chat",
            Path = "",
            Kind = "chat",
            Icon = "chat",
            Expanded = true,
            Order = 2,
            Conversations =
            [
                Session("s_7", "ws_chat", "What can we build next?", minutes: 2000),
            ],
        },
    ];

    /// <summary>Session id → status, exactly like <c>GET /api/sessions/statuses</c> for non-idle sessions.</summary>
    public static Dictionary<string, string> Statuses() => new(StringComparer.Ordinal)
    {
        ["s_1"] = "working",
        ["s_3"] = "waiting",
        ["s_5"] = "permission",
        ["s_7"] = "question",
    };

    /// <summary>Fixed quick-prompt hits for the search overlay screenshots (the real rows come from <c>/api/search</c>).</summary>
    public static List<SearchHitRow> SearchHits() =>
    [
        new()
        {
            Key = "s_3:msg_1",
            SessionId = "s_3",
            Title = "Working border motion parity",
            Snippet = "the conic sweep must advance at the same angular speed as the web",
            Highlights = [(21, 6)],
        },
        new()
        {
            Key = "s_1:msg_2",
            SessionId = "s_1",
            Title = "Avalonia shell scaffold",
            Snippet = "sidebar rows, working border and the shared motion clock",
            Highlights = [(0, 7), (35, 6)],
        },
        new()
        {
            Key = "s_2:msg_3",
            SessionId = "s_2",
            Title = "Design tokens + Lucide icon port",
            Snippet = "every duration comes from Motion.axaml, never a literal",
            Highlights = [(17, 8)],
        },
    ];

    /// <summary>
    /// Deterministic transcripts for the screenshot gate: one per transcript state of spec 041 (idle, streaming,
    /// permission, questions, diff, lightbox, markdown, compaction, retry, queued, waiting).
    /// </summary>
    public static TranscriptFixture Transcript(string state, string sessionId)
    {
        var model = "nemotron-bolt";
        var messages = new List<Message>();
        var status = "idle";
        List<QueuedMessage>? queued = null;

        switch (state)
        {
            case "streaming":
                status = "working";
                messages.Add(User(sessionId, "m_1", "Add the transcript renderer and make streaming follow the bottom", 6));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 5,
                    reasoning: "The transcript needs the web's markdown pipeline first, then the row geometry. I'll read " +
                               "the Vue components and port their measurements literally, because the screenshot gate " +
                               "compares at three sizes.",
                    reasoningMode: "medium",
                    text: "I ported the row geometry first: the 32px avatar, the `min(952px, 82%)` user bubble and the " +
                          "104ch prose measure. Now the tool cards, with the diff rows of `edit` rendered below.\n\n" +
                          "The fold state lives on the view model, so a server update never loses what the user closed.",
                    tools:
                    [
                        Tool("tc_read", "read", "success", "Read ChatMessageItem.vue (2,751 lines)", "160ms",
                            input: "{\"path\":\"src/Sofia.UI/src/components/ChatMessageItem.vue\"}",
                            output: "… 1,393 lines of template + styles read"),
                        Tool("tc_grep", "grep", "success", "12 matches in src/Sofia.App", "24ms",
                            input: "{\"pattern\":\"CollapsiblePanel\"}",
                            output: "CollapsiblePanel.cs:17\nShellView.axaml.cs:42"),
                        Tool("tc_build", "command", "running", "dotnet build Sofia.slnx", "running…",
                            input: "{\"command\":\"dotnet build Sofia.slnx\"}"),
                    ]));
                break;

            case "permission":
                status = "permission";
                messages.Add(User(sessionId, "m_1", "Install the Playwright browsers and run the visual suite", 30));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 29,
                    text: "Installing the browsers needs to run `playwright install` outside the sandbox.",
                    tools:
                    [
                        Tool("tc_shell", "command", "running", "playwright install chromium", "waiting…",
                            input: "{\"command\":\"playwright install chromium\",\"sandbox\":false}"),
                    ],
                    permission: new PermissionCard
                    {
                        Tool = "command",
                        Description = "Run `playwright install chromium` outside the sandbox: it downloads ~140 MB and " +
                                      "writes to the Playwright cache.",
                        Choices = ["Allow once", "Allow for this session", "Deny"],
                        RequestId = "req_1",
                    }));
                break;

            case "questions":
                status = "question";
                messages.Add(User(sessionId, "m_1", "Which parts of the transcript should I verify first?", 45));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 44,
                    text: "Before I write the verification plan, three choices shape it.",
                    questionSet: new QuestionSetCard
                    {
                        FormId = "form_1",
                        Questions =
                        [
                            new QuestionItem { Prompt = "Which transcript states matter most?", FieldKey = "states", Multi = true, Options = ["Streaming", "Tool cards + diffs", "Permission and questions", "Attachments"] },
                            new QuestionItem { Prompt = "Which sizes should the gate cover?", FieldKey = "sizes", Multi = false, Options = ["1080p only", "1080p + 2k", "All three"] },
                            new QuestionItem { Prompt = "How should deviations be recorded?", FieldKey = "deviations", Multi = false, Options = ["Progress log", "docs/app_ui.md", "Both"] },
                        ],
                    },
                    question: new QuestionCard
                    {
                        Prompt = "Should the desktop app reuse the web's 70-message transcript window?",
                        Options = ["Keep 70", "Raise it", "Drop the window"],
                        FormId = "form_2",
                        FieldKey = "window",
                        Multi = true,
                    }));
                break;

            case "diff":
                messages.Add(User(sessionId, "m_1", "Make the transcript match the web's tool cards", 90));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 88,
                    text: "Both file-writing tools now render their hunk with the web's +green/−red rows.",
                    tools:
                    [
                        Tool("tc_edit", "edit", "success", "Edited MessageRow.cs (+3 −1)", "180ms",
                            input: "{\"path\":\"src/Sofia.App/Features/Transcript/MessageRow.cs\"}",
                            output: "Edited MessageRow.cs (+3 −1).\n\n--- a/src/Sofia.App/Features/Transcript/MessageRow.cs\n+++ b/src/Sofia.App/Features/Transcript/MessageRow.cs\n@@ -143,7 +143,9 @@\n     private void Build()\n     {\n         Thumbnails.Clear();\n-        ToolPanels.Clear();\n+        ToolPanels.Clear();\n+        _folds.Clear();\n+        ReasoningPanel = null;\n         CompactionText = null;\n"),
                        Tool("tc_patch", "apply_patch", "success", "Applied 1 patch (+2 −2)", "96ms",
                            input: "{\"patch\":\"*** Begin Patch\"}",
                            output: "Applied 1 patch.\n\n--- a/src/Sofia.App/Theme/Tokens.axaml\n+++ b/src/Sofia.App/Theme/Tokens.axaml\n@@ -12,2 +12,2 @@\n-  <Color x:Key=\"ColorBorder\">#33333A</Color>\n+  <Color x:Key=\"ColorBorder\">#33333A</Color>\n+  <Color x:Key=\"ColorBorderSoft\">#8C33333A</Color>\n"),
                        Tool("tc_browser", "browser", "success", "Captured the transcript at 1920×1080", "1.4s",
                            input: "{\"action\":\"screenshot\"}",
                            output: "screenshot saved",
                            images:
                            [
                                new Attachment
                                {
                                    Id = "att_shot",
                                    Name = "transcript-1080p.png",
                                    Kind = "image",
                                    Mime = "image/png",
                                    Size = "412 KB",
                                    Bytes = 421_888,
                                    Url = "avares://Sofia.App/Assets/Logos/logo-128.png",
                                },
                            ]),
                    ]));
                break;

            case "lightbox":
                messages.Add(User(sessionId, "m_1", "Here is the mock I want the transcript to follow", 120,
                    attachments:
                    [
                        new Attachment
                        {
                            Id = "att_mock",
                            Name = "transcript-mock.png",
                            Kind = "image",
                            Mime = "image/png",
                            Size = "96 KB",
                            Bytes = 98_304,
                            Url = "avares://Sofia.App/Assets/Logos/logo-128.png",
                        },
                    ]));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 118,
                    text: "The mock matches the 1232px column and the 74ch card measure, so the port is a literal copy of " +
                          "the Vue geometry."));
                break;

            case "markdown":
                messages.Add(User(sessionId, "m_1", "Render this markdown torture sample exactly like the web", 150));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 148, text: MarkdownTorture));
                break;

            case "compaction":
                status = "working";
                messages.Add(User(sessionId, "m_1", "Keep going after the compaction", 180));
                messages.Add(CompactionMessage(sessionId, "m_2", "running", 82_400));
                break;

            case "retry":
                status = "working";
                messages.Add(User(sessionId, "m_1", "Summarise the transcript parity work", 200));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 199,
                    text: "",
                    retry: new RetryNotice { Attempt = 2, Attempts = 4, Error = "429 rate limited" }));
                break;

            case "queued":
                status = "working";
                messages.Add(User(sessionId, "m_1", "Port the transcript animations", 210));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: 209,
                    text: "The folds, the pulse and the carousel are wired to the shared clock."));
                queued =
                [
                    new QueuedMessage("q_1", "Also port the CoffeeSpinner states", "steer", []),
                    new QueuedMessage("q_2", "And run the frame harness at 2k", "queue", []),
                ];
                break;

            case "waiting":
                status = "working";
                messages.Add(User(sessionId, "m_1", "Compare the transcript against the web at 4k", 240));
                messages.Add(new Message { Id = "m_2", SessionId = sessionId, Role = "assistant", Text = "", CreatedAt = Base.AddMinutes(-239) });
                break;

            default: // idle
                messages.Add(User(sessionId, "m_1", "Port the transcript so it looks exactly like the web at 1080p, 2k and 4k", 0));
                messages.Add(Assistant(sessionId, "m_2", model, minutes: -1,
                    text: "The transcript is in place: rows, markdown, reasoning, tool cards, diffs, permission and question " +
                          "cards, attachments with the lightbox, the notices and the hover actions.\n\n" +
                          "Every duration comes from the motion contract, and the frame harness captures each fold, pulse, " +
                          "spin and slide stop-motion."));
                break;
        }

        return new TranscriptFixture(messages, status, queued);
    }

    /// <summary>Fixture transcript plus the status and queued follow-ups the transcript needs.</summary>
    public sealed record TranscriptFixture(List<Message> Messages, string Status, List<QueuedMessage>? Queued);

    private const string MarkdownTorture = """
## Markdown torture

A paragraph with **bold**, *italic*, ~~struck~~, `inline code()` and a [safe link](https://example.com/docs) next to a
[bad link](javascript:alert('x')) that must stay plain text. A line break follows here
and continues on the next line (GFM breaks).

- level one
  - level two
    - level three with `code`
- second item

1. ordered one
2. ordered two

| Tool | Status | Rows |
| --- | --- | --- |
| read | ok | 12 |
| edit | running | 3 |
| command | failed | 1 |

> A blockquote that wraps across the measure so the 3px rule and the muted text are both visible in the capture.

```csharp
public static int Add(int a, int b) => a + b; // this line is long enough to force horizontal scrolling in the code block
```

<div class="raw">raw HTML stays text</div>
<script>alert('injection')</script>

Math stays literal: $\rightarrow$ and $x^2 + y^2 = z^2$.

![diagram](https://example.com/diagram.png)

<https://example.com/auto-linked>

---

Last line after the rule.
""";

    private static Message User(string sessionId, string id, string text, int minutes, List<Attachment>? attachments = null) => new()
    {
        Id = id,
        SessionId = sessionId,
        Role = "user",
        Text = text,
        CreatedAt = Base.AddMinutes(-minutes),
        Attachments = attachments,
    };

    private static Message Assistant(string sessionId, string id, string model, int minutes, string text = "",
        string? reasoning = null, string? reasoningMode = null, List<ToolCall>? tools = null,
        PermissionCard? permission = null, QuestionCard? question = null, QuestionSetCard? questionSet = null,
        RetryNotice? retry = null) => new()
    {
        Id = id,
        SessionId = sessionId,
        Role = "assistant",
        Text = text,
        Reasoning = reasoning,
        ReasoningMode = reasoningMode,
        Model = model,
        CreatedAt = Base.AddMinutes(-minutes),
        ToolCalls = tools,
        Permission = permission,
        Question = question,
        QuestionSet = questionSet,
        Retry = retry,
        Usage = new Usage { Input = 18_420, Output = 3_120, CacheRead = 9_600, Cost = 0.0184 },
        DurationMs = 8_140,
    };

    private static ToolCall Tool(string id, string name, string status, string summary, string duration,
        string? input = null, string? output = null, string? error = null, List<Attachment>? images = null,
        string? childSessionId = null, bool childBackground = false) => new()
    {
        Id = id,
        Name = name,
        Status = status,
        Summary = summary,
        Duration = duration,
        Input = input,
        Output = output,
        Error = error,
        Images = images,
        ChildSessionId = childSessionId,
        ChildModelId = childSessionId is null ? null : "unsloth/nvidia-nemotron-3.5-bolt-e4b",
        ChildBackground = childBackground,
    };

    private static Message CompactionMessage(string sessionId, string id, string status, int before) => new()
    {
        Id = id,
        SessionId = sessionId,
        Role = "assistant",
        Text = "",
        CreatedAt = Base.AddMinutes(-179),
        Compaction = new CompactionNotice
        {
            Status = status,
            Auto = true,
            Before = before,
            After = status == "done" ? 12_300 : null,
            Result = status == "done" ? "The conversation covered the transcript port." : null,
        },
    };

    private static Session Session(string id, string workspaceId, string title, int minutes, bool pinned = false) => new()
    {
        Id = id,
        WorkspaceId = workspaceId,
        Title = title,
        CreatedAt = Base.AddDays(-2).AddMinutes(minutes),
        UpdatedAt = Base.AddMinutes(-minutes),
        ModelId = "unsloth/nvidia-nemotron-3.5-bolt-e4b",
        Agent = "build",
        PermissionMode = "ask",
        ReasoningMode = "medium",
        Status = "idle",
        Pinned = pinned,
        Usage = new Usage { Input = 18_420 + minutes, Output = 3_120 + minutes / 2, CacheRead = 9_600, Cost = 0.0184 },
        LastTurnMs = 8_140,
        LastTurnAt = Base.AddMinutes(-minutes),
    };

    // --- composer (spec 042) ------------------------------------------------------------------------------

    /// <summary>Session the composer screenshots bind to: server-shaped chip values plus usage/context readouts.</summary>
    public static Session ComposerSession() => new()
    {
        Id = "s_1",
        WorkspaceId = "ws_sofia",
        Title = "Avalonia shell scaffold",
        CreatedAt = Base.AddDays(-2),
        UpdatedAt = Base,
        ModelId = "unsloth/nvidia-nemotron-3.5-bolt-e4b",
        Agent = "build",
        PermissionMode = "ask",
        ReasoningMode = "medium",
        Status = "idle",
        Usage = new Usage { Input = 18_420, Output = 3_120, CacheRead = 9_612, Cost = 0.0187 },
        Context = new ContextInfo { Used = 41_800, Limit = 131_072, SystemTokens = 3_900, ToolTokens = 7_420 },
        LastTurnMs = 8_140,
        LastTurnAt = Base,
    };

    /// <summary>Model catalogue rows for the model popover (<c>GET /api/models</c> shape, already parsed).</summary>
    public static List<Features.Composer.ComposerModel> ComposerModels() =>
    [
        new("unsloth/nvidia-nemotron-3.5-bolt-e4b", "Nemotron 3.5 Bolt", "unsloth · 131K ctx", "cpu", "local"),
        new("claude-sonnet-4.5", "Claude Sonnet 4.5", "anthropic · 200K ctx", "sparkles"),
        new("gpt-5.1-codex", "GPT-5.1 Codex", "openai · 400K ctx", "sparkles"),
    ];

    /// <summary>Agent catalogue for the agent chip and the <c>@</c> popup (<c>GET /api/agents</c> shape).</summary>
    public static List<Features.Composer.ComposerAgent> ComposerAgents() =>
    [
        new("build", "build", "The default agent: reads, edits and runs the workspace.", "primary", "builtin"),
        new("plan", "plan", "Researches the codebase and proposes a plan without editing.", "primary", "builtin"),
        new("general", "general", "Handles a task end to end in its own context.", "subagent", "builtin"),
        new("explore", "explore", "Maps the codebase and reports back without changing files.", "subagent", "builtin"),
        new("qa", "qa", "Runs the UI checks and returns screenshots.", "subagent", "project"),
    ];

    /// <summary>Server commands for the <c>/</c> palette (<c>GET /api/commands</c> shape).</summary>
    public static List<Features.Composer.ComposerCommand> ComposerCommands() =>
    [
        new("compact", "Summarise the conversation to free context", "builtin"),
        new("init-plan", "Initialise the local specs directory", "project"),
        new("do", "Execute one work spec end to end", "project"),
        new("review", "Review the working tree changes", "global"),
    ];

    /// <summary>Workspace files for the <c>@</c> popup (<c>GET /api/files</c> shape).</summary>
    public static List<string> ComposerFiles() =>
    [
        "src/Sofia.App/Features/Composer/ComposerView.axaml",
        "src/Sofia.App/Features/Composer/ComposerViewModel.cs",
        "src/Sofia.App/Shell/ShellView.axaml.cs",
        "src/Sofia.App/Theme/Tokens.axaml",
        "docs/app_ui.md",
    ];

    /// <summary>Recent conversations offered by the <c>@</c> popup without a query.</summary>
    public static List<Features.Composer.ComposerReference> ComposerSessions() =>
    [
        new("s_2", "Design tokens + Lucide icon port"),
        new("s_3", "Working border motion parity"),
        new("s_4", "Review the SQLite ordering fix"),
    ];

    /// <summary>Changed files of the right panel (spec 043), shaped like <c>GET /api/sessions/{id}/diff</c>.</summary>
    public static List<FileDiff> PanelDiffs() =>
    [
        new(
            "src/Sofia.App/Features/Settings/SettingsDialog.axaml",
            string.Join('\n',
                "@@ -1,6 +1,9 @@",
                " <UserControl",
                "     xmlns=\"https://github.com/avaloniaui\"",
                "+     xmlns:controls=\"using:Sofia.App.Controls\"",
                "     xmlns:x=\"x\">",
                "-  <Border />",
                "+  <Header />",
                "+  <Tabs />",
                "+  <Body />",
                " </UserControl>"),
            7,
            1,
            "M"),
        new(
            "src/Sofia.App/Localization/Localization.cs",
            string.Join('\n',
                "@@ -1,3 +1,4 @@",
                " namespace Sofia.App.I18n;",
                "+// six locales, generated from the web catalog",
                " public static class Localization"),
            1,
            0,
            "M"),
        new(
            "src/Sofia.App/Features/Panels/DiffModal.cs",
            string.Join('\n',
                "@@ -1,2 +1,3 @@",
                "+using Avalonia.Media;",
                " namespace Sofia.App.Features.Panels;"),
            1,
            0,
            "A"),
        new(
            "src/Sofia.App/Features/Panels/OldTaskPanel.cs",
            string.Join('\n',
                "@@ -1,2 +0,0 @@",
                "-namespace Sofia.App.Features.Panels;",
                "-internal static class OldTaskPanel { }"),
            0,
            2,
            "D"),
    ];

    /// <summary>Task list of the floating card (spec 043), shaped like <c>GET /api/sessions/{id}/tasks</c>.</summary>
    public static List<TaskItem> Tasks() =>
    [
        new() { Id = "tsk_1", SessionId = "s_1", Title = "Port the seven settings tabs", Status = TaskItemStatus.Completed, Order = 0 },
        new() { Id = "tsk_2", SessionId = "s_1", Title = "Wire the right panel to the diff endpoints", Status = TaskItemStatus.Completed, Order = 1 },
        new() { Id = "tsk_3", SessionId = "s_1", Title = "Record the panel and modal frames", Status = TaskItemStatus.InProgress, Order = 2 },
        new() { Id = "tsk_4", SessionId = "s_1", Title = "Translate the header copy", Status = TaskItemStatus.Pending, Order = 3 },
        new() { Id = "tsk_5", SessionId = "s_1", Title = "Verify the locales in the screenshots", Status = TaskItemStatus.Pending, Order = 4 },
    ];
}
