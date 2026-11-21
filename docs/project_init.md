# Sofia — architecture

`src/Sofia.Core` is Sofia's own agent harness: the engine (agent loop,
providers, tools, MCP, LSP, browser, SQLite persistence, in-process event bus).
It is a plain `net10.0` class library with **no** `Microsoft.AspNetCore.*`
reference; that is what keeps the host boundary real. `src/Sofia.Server` is the
ASP.NET Core host over it (minimal APIs, SignalR hub, access middleware, static
UI) and `src/Sofia.UI` is its web client. `src/Sofia.Contracts` holds the
entities, wire DTOs, request bodies, event names and `Json.Options` with no
package references, so a browser (WASM) head can use them later.
`src/Sofia.Client` defines `ISofiaBackend` with an HTTP + SignalR implementation,
and `src/Sofia.Client.InProc` implements the same interface straight over Core.
The HTTP/SignalR contract lives in [server_api.md](./server_api.md).

The agent design follows [pi.dev](https://pi.dev): a minimal system prompt, a
small core tool set, context files discovered from the filesystem, skills with
progressive disclosure, steer vs follow-up delivery and automatic compaction.
On top of that it implements everything the UI exposes: workspaces and chat
groups, permission modes, questions, parallel tool calls and subagents, MCP,
change tracking with revert, local runtime discovery, terminal, attachments
and pairing.

## Layout

```
src/Sofia.Contracts/
├── Domain/Entities.cs         # entities that double as wire DTOs
├── Domain/Views.cs            # WorkspaceView, FileDiff, StatsView, SofiaEventDto
├── Domain/Requests.cs         # request bodies (prompt, workspace, provider, config, …)
├── Domain/*.cs                # Counter, local [Bson*] persistence markers
├── Events/EventTypes.cs       # SofiaEvent + event names
└── Infrastructure/            # Json.Options (the wire contract) and Ids

src/Sofia.Core/
├── CoreServiceCollectionExtensions.cs  # AddSofiaCore(options, core => …)
├── Infrastructure/            # options/paths, ids, text helpers, process runner, IBaseUrlProvider
├── Data/SofiaDbContext.cs     # EF Core model: columns, JSON columns, indexes, counters
├── Data/SofiaDb.cs            # store facade, atomic NextSeq, one-shot LiteDB migration
├── Data/DataDirLock.cs        # advisory sofia.lock ownership (exclusive for a host that owns the dir)
├── Events/EventBus.cs         # bus, dispatcher, IEventHandler
├── Providers/                 # OpenAI-compatible + Anthropic streaming clients,
│                              #   discovery, model catalog, limits/pricing table
├── Tools/                     # read, write, edit, apply_patch (Codex-style patches + PatchParser +
│                              #   ReadTracker), ls, find, grep, bash/powershell, web_fetch, web_search,
│                              #   browser, question, task, skill, notes, memory, sofia_info
├── Browser/                   # BrowserCatalog (installed engines), BrowserLauncher (CDP attach /
│                              #   Firefox BiDi), BrowserInstanceStore (re-attach records), session + service
├── Agent/                     # AgentService, SessionRun (the loop), prompts,
│                              #   history, tool registry, store, maintenance
├── Changes/ChangeTracker.cs   # per-message file snapshots, session diff, revert
├── Context/                   # AGENTS.md / SYSTEM.md, skills, prompt-template commands
├── Mcp/McpManager.cs          # MCP clients (stdio + HTTP) and tool adapters
├── Lsp/                       # LspClient (hand-rolled stdio JSON-RPC), LspServerCatalog
│                              #   (built-in servers), LspManager (per-workspace clients,
│                              #   diagnostics cache, idle shutdown; IHostedService)
├── Vcs/GitService.cs          # branch info, working-tree diff, worktrees
├── Infrastructure/NativeTerminal.cs  # opens the OS terminal in a folder
├── Security/PairingService.cs # one-time codes → bearer tokens
└── Services/                  # the rules that used to live in endpoints: WorkspaceService,
                               #   SessionService, DiffService, FileService, AttachmentService,
                               #   ConfigService, HostLauncher, ProviderAdminService,
                               #   BrowserAdminService, McpAdminService, LspAdminService

src/Sofia.Server/
├── Program.cs                 # composition root (JSON, SignalR, CORS, access, static UI)
├── GlobalUsings.cs            # contracts namespaces
├── Events/SofiaHub.cs         # SignalR hub + HubBroadcaster
├── Security/AccessMiddleware.cs  # loopback trust, origin check, pairing tokens
├── Infrastructure/ServerAddressBaseUrl.cs  # IBaseUrlProvider over IServer
└── Api/                       # minimal API endpoint groups (binding + Results only)

src/Sofia.Client/              # ISofiaBackend, HttpSofiaBackend (REST + SignalR)
src/Sofia.Client.InProc/       # InProcSofiaBackend over an in-process Core host
src/Sofia.Core.Tests/          # xUnit over Core services
src/Sofia.Server.Tests/        # xUnit that boots the real ASP.NET app
```

Data lives in `~/.sofia` (override with `SOFIA_HOME`): `sofia.sqlite` (SQLite),
`sofia.lock` (advisory ownership), `attachments/`, plus optional `AGENTS.md`,
`SYSTEM.md`, `APPEND_SYSTEM.md`, `skills/`, `commands/` (and legacy `prompts/`)
and `agents/`. Settings, providers and MCP servers live in the database, not in
files.

## Hosts and transports

A host composes the engine with `services.AddSofiaCore(options, core => …)` and
then exposes it however it wants:

- **Server**: registers `IBaseUrlProvider` (`ServerAddressBaseUrl`), the
  `HubBroadcaster` and the endpoint groups.
- **In-process** (the desktop head planned in 039): registers
  `SofiaCoreOptions.ExclusiveDataDir = true` and serves the UI through
  `InProcSofiaBackend`, with no HTTP port at all.

Both hand the UI the same payloads: `ISofiaBackend` methods return entities and
request bodies from `Sofia.Contracts`, while views the engine computes on the fly
(models, providers, MCP/LSP/process/browser lists, search, config) travel as
`JsonElement` serialized with `Json.Options` in both transports.

## Event-driven core

Every state change goes through `SessionStore`, which persists it and publishes
a `SofiaEvent` on the `IEventBus`. The payload is serialized at publish time, so
later mutations never leak into an event already in flight.

- `HubBroadcaster` forwards every event to SignalR clients (`/hubs/events`).
- `EventDispatcher` feeds `IEventHandler`s (for example `TitleHandler`, which
  names a conversation after its first user message).

## Persistence (`Data/`)

One SQLite database (`~/.sofia/sofia.sqlite`, WAL mode) holds everything:
workspaces, sessions, messages, attachments metadata, providers, MCP servers,
config, knowledge, memory, task lists, file snapshots and read records.

- `SofiaDbContext` is the EF Core model. Scalars are real columns (so `Find`,
  `Count`, `Exists` and `DeleteMany` push down to SQL); nested DTO graphs
  (message attachments/tool calls/cards, usage, provider model lists, LSP
  settings) are JSON columns serialized with `JsonStore.Options`, which
  persists the fields the wire hides (`[JsonIgnore]`) and skips the transient
  ones (`[BsonIgnore]`). The entity shape, and therefore the HTTP/SignalR
  contract, is unchanged from the LiteDB era.
- `SofiaDb` is the store facade: it owns the `PooledDbContextFactory`, one
  write gate (SQLite has a single writer, writes retry on busy) and the message
  sequence. `EntitySet<T>` recreates the familiar collection surface
  (`Find`, `FindById`, `Upsert`, `DeleteMany`, …) on top of short-lived pooled
  contexts.
- **Message ordering is allocated by the database**, not by a process field:
  `NextSeq()` runs `UPDATE counters SET value = value + 1 WHERE name =
  'message_seq' RETURNING value` inside the write gate. `EnsureCreated` only
  builds a schema for a new store, so `SofiaDb` also runs an idempotent bootstrap
  (`CREATE TABLE IF NOT EXISTS counters …` seeded from `MAX(messages.Seq)`) for
  stores created before the counter existed. Two hosts writing one `~/.sofia`
  therefore never hand out the same `Seq`; gaps are harmless.
- **One data directory, several processes**: `DataDirLock`
  (`<dataDir>/sofia.lock`, `FileShare.None`, pid + start time, removed on
  dispose) is advisory. `Sofia.Server` logs a warning and keeps serving; a host
  that owns the directory sets `SofiaCoreOptions.ExclusiveDataDir` and refuses to
  start next to another process. Browser windows, background processes and the
  screen-awake lock remain shared in that situation.
- Schema creation is `EnsureCreated()`: there are no EF migrations yet, so a
  schema change means a new database (fine before 1.0) — except the `counters`
  table, which older stores get from the bootstrap above.
- `LiteDbMigration` converts a legacy LiteDB file once, on first start: it
  streams every collection into SQLite through a staging file, verifies the row
  counts, renames the old `sofia.db` to `sofia.db.migrated` and writes
  `sofia.sqlite.migrated`. Failure never touches the source. The import refuses
  to run while another instance holds the legacy file (a running 0.x server) or
  while another instance is importing, replaces an empty SQLite store left by an
  earlier failed start, and refuses to overwrite a store that already has
  conversations. LiteDB is a package dependency **only** for this; delete
  `LiteDbMigration` and the package reference in 1.0.

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
   calls as `message.updated`; `session.metrics` is published while tokens
   stream and twice a second while a request runs, so the composer clock keeps
   ticking between steps. That clock measures one request, start to finish,
   ignoring the time the user spends answering a question or a permission card,
   and subagent runs count inside their parent's request. The finished value is
   stored on the session (`LastTurnMs` / `LastTurnAt`) so a chat switch or a
   client reload still shows how long the last request took.
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

| Mode | Read / network / question / task | Write (`write`, `edit`, `apply_patch`) | Execute (shell) | MCP (not read-only) |
| --- | --- | --- | --- | --- |
| `full` | allow | allow | allow | allow |
| `ask` | allow | ask | ask | ask |
| `read-only`, `plan` | allow | blocked | blocked | ask |

`browser` (`ToolKind.Browser`) asks in every mode but `full`, like MCP tools.

"Allow for this session" stores a per-tool rule on the session. Approvals
requested by subagents appear in the parent conversation.

### File editing: apply_patch and read-before-edit

`apply_patch` (`Tools/ApplyPatchTool.cs`, `ToolKind.Write`) applies an OpenAI
Codex-style patch to several files atomically: `Tools/PatchParser.cs` parses
the grammar (with Codex's leniencies — heredoc wrapper, markers with extra
whitespace, a first chunk without `@@`, blank context lines) into hunks and
reports errors with line numbers; the tool then validates every hunk and
computes all new content in memory before touching disk. Update chunks are
matched by `TextMatch.SeekSequence` (shared with `edit`'s fallback): four
passes per candidate — exact, trim-end, trim, Unicode-punctuation normalised —
searching from the cursor after the previous chunk; `*** End of File` anchors
at the end. CRLF and a missing final newline are preserved. Every source and
destination path is captured through the change tracker before writing (so
revert restores updated/deleted/moved files and deletes added ones), then the
commit runs with best-effort rollback from the in-memory originals. The result
is `Applied patch: N files (+a -d)` plus a unified diff per file, built by the
shared `FileEditResult` helper (with spec 020's LSP diagnostics appended once
for every touched file). Exposure follows `ServerConfig.ApplyPatch`
(`auto` = OpenAI-family models only — `openai-responses` type, openai flavour
or a gpt-/o-series/codex model id; `always`; `never`) and is filtered in
`SessionRun.TurnContextAsync`, which re-freezes the prompt when the tool set
changes.

The read-before-edit policy (`Tools/ReadTracker.cs`, toggle
`ServerConfig.ReadBeforeEdit`, default on) makes `edit`, `apply_patch`
(Update/Delete/Move) and `write` over an existing file fail with a clear
"read it first" message unless the session read the file and it did not change
on disk since. Records live in the persisted `file_reads` collection (per
session id, so subagents are isolated; deleted with the session), keyed by the
OS-normalised path; change detection compares length + mtime first and falls
back to SHA-256 (touch-only rewrites pass). Partial reads count, the tools'
own writes refresh the state, new files are exempt.

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
the folded messages are kept for the transcript but skipped in model history;
each folded message records the seq of the notice that folded it
(`Message.CompactedBy`). Hiding them is only valid while that summary survives,
so a revert (or a branch) that cuts it away unfolds them again — otherwise the
transcript would stay on screen while the model saw an empty conversation. When
no pointer exists (data written before it), a folded message is unfolded only if
no surviving summary sits after it.

### Tool-result pruning

Before compaction, and cheaper than it, old tool results can be pruned: when the
estimated input crosses `pruneThreshold` (default 0.6, always kept at least 5%
below `compactThreshold`), a persisted watermark (`Session.PrunedThroughSeq`) is
advanced in one batch to just before the last `pruneKeepSteps` assistant steps
that call tools (default 6). Every request after that sends those results as a
one-line `[pruned: …]` placeholder instead of the full output, and their
screenshots/attachments fall back to the "older image" labels. The transcript is
never touched — `ToolCall.Output` still holds the display text and the UI keeps
the full result. Hysteresis: a prune only fires when the estimated savings are
at least 10% of the context window, so each cache invalidation buys real space
and happens rarely. Reverting into the pruned range resets the watermark, so the
restored history reaches the model complete (the next big step prunes again).
Settings: `pruneToolResults`, `pruneThreshold`,
`pruneKeepSteps`, `pruneMinChars` (results shorter than this — default 2000
chars — are never pruned).

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

## Language servers (LSP) — `Lsp/`, `Tools/LspTool.cs`

Code intelligence through the Language Server Protocol: four built-in servers
(`typescript-language-server`, `vscode-html-language-server`,
`vscode-css-language-server`, `csharp-ls`) mapped to file extensions in
`LspServerCatalog`. `LspClient` is a hand-rolled minimal JSON-RPC client over
stdio (`Content-Length` framing, request-id correlation, `$/cancelRequest`,
automatic answers to server→client requests, stderr drained to the logger); no
external dependency.

`LspManager` (singleton + `IHostedService`) owns one client per
(workspace root, server id), started lazily on first use and shut down after
~10 idle minutes (`shutdown`+`exit`, then process-tree kill). Crashes flip the
status to `error` with a short backoff before the next start; status changes
publish an `lsp.status` event so the UI updates live. Documents are synced with
full-text `didOpen`/`didChange`; `publishDiagnostics` is cached per URI with a
timestamp and a ≤1.5 s settle wait after writes.

Tools (folder workspaces only): `lsp` (`ToolKind.Read`) — diagnostics,
definition, references, hover, document_symbols, workspace_symbols, 1-based
line/column in the tool API; and `lsp_rename` (`ToolKind.Write`) — applies the
server's WorkspaceEdit text edits to every affected file (resource operations
are refused), capturing each file through the change tracker so it is
revertible. `edit`/`write` call `LspManager.DiagnosticsAfterWriteAsync`, which
syncs the touched files, waits for fresh diagnostics and appends only errors
(≤20 entries / 2 KB) to the tool result — bounded by a 25 s budget so the agent
loop is never held up; a missing server or timeout simply yields nothing.

Settings: `ServerConfig.Lsp` (`enabled`, `autoDiagnostics`, per-server
`enabled`/`command` overrides). Endpoints: `GET /api/lsp`,
`POST /api/lsp/{id}/install` (runs the install command; the UI confirms first),
`POST /api/lsp/{id}/restart`.

## Browser (`Browser/`, `Tools/BrowserTool.cs`)

The `browser` tool (build and general agents) opens a **real browser window on the
desktop of the machine running the server** — an installed Chrome-family browser
that Sofia starts itself and drives over CDP, or the installed Firefox driven over
WebDriver BiDi. There is no live view: nothing is streamed to the UI, the user
simply uses the window (right-click menu, DevTools, native `<select>`/file pickers,
their own keyboard layout and IME) while the agent reads the page through
snapshots and screenshots. A remote client only sees the chat screenshots.

- **Detection:** `BrowserCatalog` finds the installed engines (`chrome`, `edge`,
  `brave`, `vivaldi`, `opera`, `chromium`, `firefox`) from the platform install
  paths plus `PATH`; `SOFIA_BROWSER_<ID>` overrides one. `auto` (the
  `ServerConfig.DefaultBrowser` default) is the first installed Chromium-family
  engine, else Firefox; asking for a missing engine fails listing what is installed.
- **Launch:** `BrowserLauncher` starts the Chromium binary itself with
  `--user-data-dir=<profile> --remote-debugging-port=<free loopback port>` and
  connects with `ConnectOverCDP(NoDefaults: true, IsLocal: true)`, so the window has
  no Playwright automation flags and the session drives `browser.Contexts[0]`
  (an attached browser cannot create contexts). Firefox uses
  `LaunchPersistentContext(Channel: "moz-firefox")`, which is launch-only: it cannot
  outlive the server.
- **Profiles:** `<SOFIA_HOME>/browsers/<sessionId>/<engine>/` per conversation and
  engine, so logins survive restarts. The daily-driver profile is never touched
  (Chrome 136+ refuses remote debugging on it and a running Chrome locks it).
  `<profile>/instance.json` records engine/executable/pid/port/session; at startup
  `BrowserInstanceStore.Reconcile` attaches windows whose conversation still exists,
  kills windows whose conversation is gone (verifying the pid's executable first) and
  prunes records that point at nothing. Deleting a conversation closes its window and
  removes its profiles (`BrowserCleanupHandler`).
- **Lifetime:** no idle timer. The window closes on the agent's `close` action, when
  the user closes it (`Browser.Disconnected` → publish `open: false`), or when the
  conversation is deleted. Closing the last tab *is* closing the window: the session
  never opens a replacement page on tab close (a target created while the window goes
  away keeps Chromium alive and the window reappears), and the next agent call opens
  a fresh window instead. One Chromium per conversation costs 0.5–1.5 GB, which is
  why opening one is an explicit, visible action.
- **Headless:** `SOFIA_BROWSER_HEADLESS=1`, or no display (Docker/CI, Linux without
  `DISPLAY`/`WAYLAND_DISPLAY`), uses the same engine with `--headless=new`; with no
  engine installed at all the headless path falls back to Playwright's bundled
  Chromium. The debug port is loopback-only and never exposed.
- **Reading pages:** `snapshot` walks the visible DOM and returns a text outline
  where interactive elements carry refs (`[e12] button "Go"`), stored as
  `data-sofia-ref` attributes; click/type/select/hover/press/scroll address those
  refs. Every acting call returns a fresh snapshot, so one call per step is enough.
  The user may navigate between calls, so refs can be stale — the tool answers with
  "take a new snapshot". Other actions: launch, navigate, wait, back/forward/reload
  (history is walked through the page so a back/forward-cache restore does not wait
  for a navigation event that never fires), evaluate, tabs/new_tab/switch_tab/
  close_tab, focus, close.
- **Viewports:** the `viewport` action emulates a layout viewport (Chrome DevTools
  device mode) without touching the OS window: `width`/`height` (200–4096 px) plus
  optional `mobile` and `device_scale_factor` (0.1–4). Chromium applies it with CDP
  `Emulation.setDeviceMetricsOverride`; Firefox applies `width`/`height` through
  `Page.SetViewportSizeAsync` and ignores the other two (WebDriver BiDi has no
  equivalent). The emulation is session state (`BrowserSession.SetViewportAsync`),
  re-applied when the agent switches or opens a tab, and every tool result reports
  the **measured** layout viewport (`innerWidth/innerHeight`, `screen`,
  `devicePixelRatio`) plus the real OS window size from `Browser.getWindowForTarget`
  — never the requested numbers alone, and never `window.outerWidth`, which moves in
  mobile mode. Media queries, flex wrapping and screenshots follow the emulated size.
  `viewport` without a size clears it: Chromium returns to the window, Firefox returns
  to the content size measured before the first override (a fixed Playwright viewport
  cannot go back to following the window). `window.resizeTo` is a silent no-op in this
  window — Chrome only allows it for windows opened by `window.open` — so physical
  window resizing is deliberately not exposed: the user's window stays theirs.
- **Screenshots:** `screenshot` (optionally `ref` for one element or `full_page`, capped at
  6000px) takes a Playwright `page.screenshot` JPEG (works on both engines) and saves it as
  an attachment in `ToolResult.Images`; the tool card shows it and `ToolCall.images`
  carries it. Tool results are text-only, so `HistoryBuilder` sends the image in a user
  turn right after the tool results, only for models not known to be text-only, and only
  the latest 2 screenshots are resent as images (older ones become a text note).
- **Dialogs** are still answered by the `page.Dialog` handler (alerts accepted, the rest
  dismissed) and reported as a note, so an agent-driven page never hangs on a dialog.
- **Safety:** only `http(s)` URLs; the tool asks for approval (`ToolKind.Browser`) — a
  visible window on the user's desktop is more intrusive than a headless one. The CDP
  debug port is an unauthenticated local control surface, bound to `127.0.0.1` and one
  port per session.

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

Notes live in the `knowledge` table. `SystemPromptBuilder` injects
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
the `memory` table (`MemoryEntry`: workspace, key, value, timestamps).
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
- Context windows and output limits are read at runtime, newest source first:
  what the endpoint reports (`context_length`, `max_model_len`,
  `top_provider.max_completion_tokens`, llama.cpp `/props`, Ollama `/api/show`
  and `/api/ps`, LM Studio `/api/v0/models`, Copilot `capabilities.limits`;
  Anthropic reports none), the last value a runtime reported while the model was
  loaded (`ProviderConfig.KnownContext`), the **models.dev catalog**, and finally
  one documented constant (`ModelLimits.FallbackContext`, 32K). No context or
  output limit is hardcoded anywhere else; prices, `Reasoning`, `Vision` and
  `Audio` still come from `Providers/ModelLimits.cs`.
- `Providers/ModelCatalog.cs` is that catalog. It boots from the trimmed snapshot
  committed as `Providers/ModelsDevSnapshot.json` (embedded resource; regenerate
  with `node script/update-model-snapshot.mjs`), replaces it with the copy cached
  in the `catalog` table when a previous process fetched one, and
  refetches at most once every 24 h with an `If-None-Match` GET of
  `https://models.dev/api.json` (`SOFIA_MODELS_URL` overrides it, 30 s timeout; a
  304, a network error or a body with no limits keeps the current data). Lookups
  prefer the exact `(provider, model)` pair, then any provider serving that model
  id. `MaintenanceService` refreshes it at boot and every 90 s (a no-op until the
  cached copy is a day old), and `POST /api/providers/refresh` starts one off the
  request path.
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
- Subfolder instructions (`ContextFiles.SubfolderFiles`): when a file tool
  touches a path below the workspace root, the `AGENTS.md`/`CLAUDE.md` of each
  folder strictly between the root and that file is appended to the model-facing
  tool result as a `<project_instructions>` block — once per conversation per
  file (tracked by hash in `Session.SubContextFiles`, persisted), refreshed when
  the file changes, re-injected after compaction or pruning removed the message
  that carried it. Entries join the volatile context facts as `sub:` keys, so an
  edit or deletion of a loaded file is announced at the next turn start through
  the standard `<context_updates>` path. The transcript keeps the plain output;
  only the model history (the tool result) carries the instructions.
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
