# Sofia.Server — architecture

`src/Sofia.Server` is Sofia's own agent harness: an ASP.NET Core (.NET 10)
service with minimal APIs, LiteDB persistence, an in-process event bus and a
SignalR hub. `src/Sofia.UI` is its web client. The HTTP/SignalR contract lives
in [server_api.md](./server_api.md).

The agent design follows [pi.dev](https://pi.dev): a minimal system prompt, a
small core tool set, context files discovered from the filesystem, skills with
progressive disclosure, steer vs follow-up delivery and automatic compaction.
On top of that it implements everything the UI exposes: workspaces and chat
groups, permission modes, questions, parallel tool calls and subagents, MCP,
change tracking with revert, local runtime discovery, terminal, attachments
and pairing.

## Layout

```
src/Sofia.Server/
├── Program.cs                 # composition root (DI, JSON, SignalR, access, static UI)
├── Infrastructure/            # options/paths, ids, JSON, text helpers, process runner
├── Domain/Entities.cs         # LiteDB entities that double as wire DTOs
├── Data/SofiaDb.cs            # LiteDB collections + indexes
├── Events/EventBus.cs         # bus, dispatcher, IEventHandler, SignalR hub + broadcaster
├── Providers/                 # OpenAI-compatible + Anthropic streaming clients,
│                              #   discovery, model catalog, limits/pricing table
├── Tools/                     # read, write, edit, ls, find, grep, bash/powershell,
│                              #   web_fetch, web_search, browser, question, task, skill, notes, memory, sofia_info
├── Browser/BrowserService.cs  # headless Chromium (Playwright), per-conversation contexts, screencast
├── Agent/                     # AgentService, SessionRun (the loop), prompts,
│                              #   history, tool registry, store, maintenance
├── Changes/ChangeTracker.cs   # per-message file snapshots, session diff, revert
├── Context/                   # AGENTS.md / SYSTEM.md, skills, prompt-template commands
├── Mcp/McpManager.cs          # MCP clients (stdio + HTTP) and tool adapters
├── Vcs/GitService.cs          # branch info, working-tree diff, worktrees
├── Infrastructure/NativeTerminal.cs  # opens the OS terminal in a folder (POST /api/open)
├── Security/Access.cs         # loopback trust, origin check, pairing tokens
└── Api/                       # minimal API endpoint groups
src/Sofia.Server.Tests/        # xUnit: unit tests + in-process agent-loop test
```

Data lives in `~/.sofia` (override with `SOFIA_HOME`): `sofia.db` (LiteDB),
`attachments/`, plus optional `AGENTS.md`, `SYSTEM.md`, `APPEND_SYSTEM.md`,
`skills/`, `commands/` (and legacy `prompts/`) and `agents/`. Settings,
providers and MCP servers live in the database, not in files.

## Event-driven core

Every state change goes through `SessionStore`, which persists it and publishes
a `SofiaEvent` on the `IEventBus`. The payload is serialized at publish time, so
later mutations never leak into an event already in flight.

- `HubBroadcaster` forwards every event to SignalR clients (`/hubs/events`).
- `EventDispatcher` feeds `IEventHandler`s (for example `TitleHandler`, which
  names a conversation after its first user message).

## The agent loop (`Agent/SessionRun.cs`)

One `SessionRun` per active session, owned by `AgentService`:

1. A prompt is queued; if the session is idle a run starts. While a run is
   active, `steer` prompts are injected after the running tool call and skip
   the step's remaining tool calls, and `queue` prompts run once the agent
   finishes (pi semantics). A message typed while the agent waits on a
   question answers it. Steering is replayed to the model as text appended to
   the interrupted step's last tool result (`Message.Steered`, handled in
   `HistoryBuilder`), never as a new user turn: a user turn mid-run makes
   chat templates / thinking-aware APIs re-render earlier steps and bust the
   prompt cache.
2. Each step: resolve the model, auto-compact if the last request used more
   than `compactThreshold` of the window, build the history (`HistoryBuilder`)
   and stream the reply. Text and reasoning go out as `message.delta`, tool
   calls as `message.updated`; `session.metrics` is published about 4 times a
   second.
3. The request prefix — system prompt (`SystemPromptBuilder`) and tool schemas
   — is built once and **frozen for the whole conversation**. The prompt is
   persisted on the session (`FrozenPrompt`, `FrozenAgent`, `FrozenTools` = a
   fingerprint of the tool schemas, plus `PromptFacts`, the volatile facts baked
   into it); all are `[JsonIgnore]`, server-side only. Every step of every turn
   reuses that string byte-for-byte (`SessionRun.TurnContextAsync`). Providers
   cache on the longest common prefix, so anything changing inside it (a note
   saved through `notes`, a background-review note, a branch switch, midnight,
   a permission-mode or language change, an edited AGENTS.md, a skill toggle)
   would invalidate every cached token of the conversation.
   Instead, at the start of each turn the current facts are diffed against
   `PromptFacts` (`ContextUpdates.Build`) and the changes are attached to the
   **newest user message** as `Message.ContextNotes`. `HistoryBuilder` renders
   them as a `<context_updates>` block appended inside that user turn, after the
   user's own text. They are persisted and replayed identically in every later
   turn, so the transcript stays append-only and the cached prefix never moves.
   Bounds: a changed context file is embedded in full when it is at most 4000
   characters, otherwise the update is a one-line notice with its path; at most
   12 lines per update, ~12 000 characters for the block. Within a turn the
   system string and the delta are cached, so a note saved mid-run changes
   neither (the model saw the save in its tool result).
   The prefix is **re-frozen only when it is doomed anyway**: the session's
   agent changed, or the tool-schema fingerprint changed (MCP server up/down,
   delegable agents, shell). That rebuild resets the facts and emits no delta.
   A permission-mode switch does not re-freeze: the gate reads
   `Session.PermissionMode` live at each call, and the change is also sent as a
   context update. Existing or branched sessions have no frozen prompt and
   freeze once on their next turn.
   Anthropic carries two cache breakpoints (of the four allowed): one on
   `system` and a rolling one on the last content block of the last message, so
   the growing transcript is cached too. OpenAI-compatible and local runtimes
   reuse the common prefix on their own.
4. Tool calls run in parallel when every call in the step is read-only,
   network or `task` (so several subagents run at once); otherwise they run in
   order. Each call is authorized by the permission gate first. The tool set
   executed is exactly the one the model saw for that turn.
5. Loop until the model answers without tool calls, then drain the queue.

A step whose request never got an answer is repeated. `Providers/RetryPolicy`
calls a failure transient when the model never spoke: `499` (nginx' *Client
Closed Request*), `408`/`409`/`425`/`429`, 5xx and the Cloudflare family, a
socket dropped mid-stream, a provider timeout, or a stream that closed without a
single event. `SessionRun.StreamStepAsync` then restarts the step — up to three
retries with ~0.75 s / 1.5 s / 3 s backoff, so four attempts before anything
reaches the chat. What a failed attempt streamed is cleared off the card (its
tool calls never ran) and the card carries a `Message.Retry` notice with the
attempt number and the reason until an attempt succeeds; when they all fail the
error lands on the card and the session goes to `error`, as before. Permanent
failures — bad key, unknown model, context too long, a runtime that is simply
not running — still fail fast.

Abort cancels the run token: streaming stops, running tools are killed and
marked, pending approvals are cancelled.

### Permissions

| Mode | Read / network / question / task | Write (`write`, `edit`) | Execute (shell) | MCP (not read-only) |
| --- | --- | --- | --- | --- |
| `full` | allow | allow | allow | allow |
| `ask` | allow | ask | ask | ask |
| `read-only`, `plan` | allow | blocked | blocked | ask |

`browser` (`ToolKind.Browser`) asks in every mode but `full`, like MCP tools.

"Allow for this session" stores a per-tool rule on the session. Approvals
requested by subagents appear in the parent conversation.

### Subagents

`task` creates a child session (`parentId` set) with a subagent — the built-in
`explore` (read-only) or `general`, or any custom agent with `mode: subagent|all`
— runs it to completion inside the tool call and returns its final report.
Children inherit the model and permission mode; the parent's abort cancels them.
Subagent sessions never get `question` or `task`. Typing `@name` in a prompt
adds a hidden note (`<agent_mentions>`, stored as the message's `expanded` text)
asking the model to delegate to that agent through `task`; the user's text is
shown unchanged.

### Compaction

When context use crosses the threshold (or on `/compact`), older messages are
summarized by the same model. The summary is stored as a `summary` message and
the folded messages are kept for the transcript but skipped in model history.

## Web search

`web_search` (`Tools/WebSearchTool.cs`, `ToolKind.Network`, so it needs no
permission and runs in parallel with other reads) returns title, URL and snippet
per result. Backends, chosen in Settings: DuckDuckGo's HTML endpoint (default, no
key; ad links are dropped and redirect links resolved) or a SearXNG instance. The
SearXNG path asks for `search?q=…&format=json` first and, when the instance has
the json engine disabled (`403`, the default install) or answers with HTML, reads
the `/search` result page instead (one `<article class="result …">` per hit: title
from its `<h3>` link, snippet from `p.content`). A path prefix in the configured
URL (`/searxng`) is kept. The agent follows up with `web_fetch` to read a page.
Every built-in agent has it.

## Browser (`Browser/BrowserService.cs`, `Tools/BrowserTool.cs`)

The `browser` tool (build and general agents) drives a real headless Chromium so
the user can watch the agent work inside Sofia instead of getting a Chrome window
on their desktop. `BrowserService` launches one shared browser through Playwright
(system Chrome, then Edge, then a one-time download of Playwright's Chromium) and
gives every conversation an isolated context (own cookies, tabs; subagents share
their parent's). It closes with the conversation and after 20 idle minutes.

- **Reading pages:** `snapshot` walks the visible DOM and returns a text outline
  where interactive elements carry refs (`[e12] button "Go"`), stored as
  `data-sofia-ref` attributes; click/type/select/hover/press/scroll address those
  refs. Every acting call returns a fresh snapshot, so one call per step is enough.
  Other actions: navigate, wait, back/forward/reload, evaluate, tabs/new_tab/switch_tab/close_tab, close.
- **Screenshots:** `screenshot` (optionally `ref` for one element or `full_page`, capped at
  6000px) saves a JPEG as an attachment and returns it in `ToolResult.Images`; the
  tool card shows it and `ToolCall.images` carries it. Tool results are text-only, so
  `HistoryBuilder` sends the image in a user turn right after the tool results, only
  for models not known to be text-only, and only the latest 2 screenshots are resent
  as images (older ones become a text note).
- **Live view:** a CDP screencast (`Page.startScreencast`, JPEG) is pushed over the
  `/api/sessions/{id}/browser/stream` WebSocket only while a viewer is connected;
  viewers also send mouse/keyboard input, so the user can log in or solve a
  captcha. While a tool call runs the browser is `agentBusy` and user input is
  ignored. Click targets are sent as `cursor` messages so the UI can show them.
- **Safety:** only `http(s)` URLs; the tool asks for approval (`ToolKind.Browser`).

## Knowledge notes (`Agent/KnowledgeService.cs`)

Learning loop. When a top-level run ends without error or abort,
`AgentService.Finished` starts a background review (never blocking the chat, at
most one per session): the session's model reads the transcript added since the
last review (`Session.KnowledgeSeq`) and answers with up to 3 short notes, each
scoped to the **workspace** (project facts) or the **agent** (user preferences,
style).

Utility gate: a note is only permanent when the reviewer argues for it. The review
prompt asks the model to judge every candidate before writing it ("would a future
session act better because of it?") and to answer, per item, `"useful": true` plus
a one-sentence `why` naming the future situation where it helps. `Parse` drops any
item without `"useful": true`, without a `why` of at least 12 characters, or whose
`why` reads like markup or an injected command — unjustified notes are discarded
entirely, never stored in another scope. The `why` is kept on the note and shown on
the chat card (never injected into the system prompt), so the user can judge the
same call. Notes the agent saves itself through the `notes` tool keep their current
behaviour (`why` empty).

Tool results are left out of the transcript so web pages and file
contents cannot inject notes; notes that look like markup, injected instructions
or secrets are refused, and duplicates of existing notes are dropped.

Notes live in the `knowledge` LiteDB collection. `SystemPromptBuilder` injects
them in a `<knowledge>` block: the running agent's notes plus the workspace's, the
newest that fit a 3000-character budget per scope. That block is part of the
request prefix frozen for the whole conversation (see the agent loop above), so
the block is written once and never rewritten: a note saved or deleted later
reaches a running conversation as a line in the `<context_updates>` block of the
next user message (`Message.ContextNotes`), and only new conversations (or a
re-freeze on agent/tool-set change) read the fresh `<knowledge>` block. Inside
the run the model already saw a save in its tool result, and `notes` `list`
reads live. The chat
gets a card-only message (`Message.Knowledge`) listing the new notes; the user can
expand it and delete a note (`DELETE /api/knowledge/{id}`), which strikes it on the
card.
Settings → General → "Learn from conversations" turns generation off; stored
notes keep being injected. Deleting a workspace deletes its workspace notes.

The agent can also manage notes itself with the `notes` tool (`Tools/NotesTool.cs`,
part of every built-in agent's tool set; custom agents list `notes`). Actions
`list` (default), `save` (new note, or `id` to rewrite one) and `delete`, in scope
`workspace` (same collection and prompt injection as above; ≤ 400 characters,
same markup/secret filter) or `chat` (a scratchpad for the current conversation:
`scope: 'chat'` rows keyed by `SessionId`, ≤ 2000 characters, 50 per chat, never
injected into the prompt, hidden from `/api/knowledge`, deleted with the
session). Subagents read and write the chat notes of the session that spawned
them. The tool is `ToolKind.Read`: it only touches Sofia's database, so it is
allowed in every permission mode and in plan/read-only sessions.

The `memory` tool (`Tools/MemoryTool.cs`, in every built-in agent's tool set;
custom agents list `memory`, alias `mem`) is a per-workspace key/value store over
the LiteDB `memory` collection (`MemoryEntry`: workspace, key, value, timestamps).
Actions `list` (default: keys with size and a short preview), `get` (`key`) and
`set` (`key`, `value`; an empty value deletes the key). Keys ≤ 100 characters of
`[A-Za-z0-9._:/-]`, values ≤ 20 000 characters, ≤ 200 keys per workspace. Every
chat and subagent of the workspace shares it. It is **never** injected into the
system prompt (that would break the frozen per-conversation prompt); the model
discovers keys with `list`. `ToolKind.Read`, so it works in every permission mode.
`SofiaDb.DeleteWorkspaceMemory` drops a workspace's entries when it is deleted.

## Keeping the screen awake

`Infrastructure/KeepAwakeService.cs` holds an inhibitor while any run is active
and `keepScreenAwake` is on: `SetThreadExecutionState` on Windows (dedicated
thread), `caffeinate` on macOS, `systemd-inhibit` on Linux (best effort). It
acts on the machine running the server, and is released when the last run
finishes, the setting is turned off or the server stops.

## Providers and models

- Wire protocols: OpenAI Chat Completions (streaming, tools, `reasoning_content`
  / `reasoning`, inline `<think>` splitting) and the Anthropic Messages API
  (streaming, tools, extended thinking with signatures, prompt caching).
- Local discovery (startup and every 90 s): Unsloth Studio `:8888`, Ollama
  `:11434` (`OLLAMA_HOST`), LM Studio `:1234`, llama.cpp `:8080`, vLLM `:8000`,
  plus `SOFIA_LOCAL_ENDPOINTS="name=url,…"`.
- Context windows come from runtime metadata (`context_length`,
  `max_model_len`, llama.cpp `/props`, Ollama `/api/show` and `/api/ps`, LM
  Studio `/api/v0/models`), remembered per model while it is unloaded, then a
  built-in table, then 32K.
- Cloud presets are enabled from environment keys (`OPENAI_API_KEY`,
  `ANTHROPIC_API_KEY`, `OPENROUTER_API_KEY`, `GEMINI_API_KEY`,
  `DEEPSEEK_API_KEY`, `GROQ_API_KEY`, `MISTRAL_API_KEY`, `XAI_API_KEY`,
  `OPENCODE_API_KEY` (OpenCode Go), `QWENCLOUD_API_KEY` (QwenCloud Token Plan),
  `COPILOT_GITHUB_TOKEN` (GitHub Copilot, also available through the Settings
  device-flow sign-in)) or added
  in Settings → Models, alongside custom OpenAI/Anthropic-compatible endpoints.
  `Providers/CopilotAuth.cs` handles the Copilot sign-in and token exchange;
  `Providers/OpenCodeGo.cs` routes OpenCode Go models to the right wire protocol.
- Every LLM request carries the conversation id in session headers for gateways
  (`Providers/ProviderIdentity.cs`): `x-session-id`, `x-litellm-trace-id`,
  `x-claude-code-session-id`, `x-dynamo-session-id`, plus the vendor's own name
  where it differs (`x-opencode-session`, Bedrock AgentCore runtime/policy ids,
  the runtime one padded to AWS's 33-character minimum). OpenCode Go also
  demands an agent user agent (`sofia/<version>`) on both of its wires.
- Reasoning modes map to `reasoning_effort` (OpenAI-style),
  `reasoning.effort` (OpenRouter), `thinking` + `reasoning_effort` (DeepSeek,
  which also needs `reasoning_content` echoed back), `enable_thinking`
  (QwenCloud), thinking budgets (Anthropic) or
  `chat_template_kwargs.enable_thinking` (local runtimes).
- Media input: the `read` tool attaches image, audio and video files (type sniffed
  from magic bytes; 15 MB for images, 20 MB for audio/video) through the same
  `ToolResult.Images` path as screenshots. Images go to models not known to be
  text-only. Audio (`input_audio`, wav/mp3 only) and video (`video_url` data URL)
  go only to OpenAI-compatible models whose `ModelInfo.Audio` / `Video` is set from
  runtime metadata (`input_modalities`, llama.cpp `/props` `modalities.audio`) or,
  for audio, a model-name heuristic; video is never guessed. Anything else becomes a
  text note, because an unsupported part would fail every later request. Anthropic
  has no audio/video input.

## Context engineering

- Context files: `~/.sofia/AGENTS.md`, then `AGENTS.md` (or `CLAUDE.md`) in
  every directory from the filesystem root down to the workspace.
- `.sofia/SYSTEM.md` replaces the preamble; `APPEND_SYSTEM.md` appends to it.
- Skills: `SKILL.md` folders under `~/.sofia/skills` and the workspace's
  `.sofia/skills` (other harnesses' folders such as `.claude`, `.agents` or
  `.opencode` are never read). Only name and description go into the
  prompt; the `skill` tool loads the body. Skills activated from the UI are
  pinned into the prompt.
- Commands: built-ins `compact` and `init`, plus custom `/commands` — Markdown
  prompt templates from `~/.sofia/commands`, `~/.sofia/prompts` and the
  workspace's `.sofia/commands`, `.sofia/prompts` (project wins). Sub-folders are part of the name (`git/pr.md` → `/git/pr`).
  Frontmatter: `description`, `agent`, `model`, `subtask`. Placeholders:
  `$ARGUMENTS`, `$1`…`$9`, ``!`cmd` `` (output injected; runs only in
  full-access sessions, otherwise it stays literal) and `@file` (a workspace
  file is appended as a `<file>` block).
- Agents: Markdown files in `~/.sofia/agents` and the workspace's
  `.sofia/agents` (`Agent/AgentCatalog.cs`). Frontmatter:
  `description`, `mode` (`subagent` default, `primary`, `all`), `tools`
  (comma list; omitted = the mode's default set), `mcp`; the body is the system
  prompt addendum. A file may override a built-in agent.
- `sofia_info` tool (read-only): explains where Sofia keeps its own settings
  (data dir, skills, commands, agents, MCP servers, providers, config, context
  files), their formats and what is installed now, so an agent can help the user
  edit them with the file/shell tools or the HTTP API. Secrets are never shown.

## Security

The server binds `127.0.0.1:5199` by default. Loopback callers are trusted;
everyone else needs a bearer token obtained by redeeming a one-time pairing
code (`POST /api/pair` locally, then `POST /api/pair/connect`). Requests with a
foreign browser `Origin` are rejected, so a web page cannot drive the local
harness. `X-Forwarded-For` from a loopback proxy (the Vite dev server) is
honored, so LAN clients behind it still have to pair.

## Commands

```bash
dotnet build Sofia.slnx
dotnet test Sofia.slnx
dotnet run --project src/Sofia.Server     # http://127.0.0.1:5199 (serves src/Sofia.UI/dist when built)
```
