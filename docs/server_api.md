# Sofia.Server API

`src/Sofia.Server` is Sofia's own harness backend (ASP.NET Core, .NET 10,
minimal APIs, LiteDB, SignalR). `src/Sofia.UI` is its only client. This file is
the contract between the two: REST for commands and queries, one SignalR hub
for live events.

- Default address: `http://127.0.0.1:5199` (loopback only). Override with
  `--urls` or the `SOFIA_URLS` environment variable.
- JSON is camelCase. Timestamps are ISO-8601 UTC strings.
- Errors: non-2xx with `{ "error": "human readable message" }`.
- The server serves the built UI (`src/Sofia.UI/dist`) at `/` when present. In
  dev, Vite proxies `/api` and `/hubs` to the server, so the UI always uses
  same-origin relative URLs unless the user connects to another server.

## Auth and pairing

Requests from loopback need no credentials. Any other client must send
`Authorization: Bearer <token>` (SignalR: `?access_token=<token>`).

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `POST` | `/api/pair` (loopback only) | – | `{ code, expiresIn }` (seconds) |
| `POST` | `/api/pair/connect` | `{ code }` | `{ token }` (code is single-use) |
| `GET` | `/api/health` | – | `{ status: "ok", name: "sofia", version }` |
| `GET` | `/api/server` | – | `{ version, urls: string[], dataDir, remoteAuth: boolean, networkReachable: boolean }` — `urls` are the machine's network addresses (never loopback); `networkReachable` is false while the server only listens on loopback |

## Live events (SignalR)

Hub: `/hubs/events`. The server pushes every event to every connected client
with the client method **`event`** and one argument:

```ts
interface SofiaEvent {
  type: string
  sessionId?: string
  workspaceId?: string
  data: unknown
}
```

| `type` | `data` | Meaning |
| --- | --- | --- |
| `workspace.updated` | `WorkspaceDto` (without `conversations`) | Created or changed |
| `workspace.deleted` | `{ id }` | |
| `session.created` | `SessionDto` (no `messages`) | New conversation (also subagent children, `parentId` set) |
| `session.updated` | `SessionDto` (no `messages`) | Title, model, usage, skills… changed |
| `session.deleted` | `{ id, workspaceId }` | |
| `session.status` | `{ status: SessionStatus, error?: string }` | Run state changed |
| `session.metrics` | `MetricsDto` | Live run readouts (≈4/s while running) |
| `message.created` | `MessageDto` | Append to the transcript |
| `message.updated` | `MessageDto` | Replace the message with the same `id` |
| `message.delta` | `{ messageId, field: "text" \| "reasoning", delta }` | Append `delta` to that field |
| `message.deleted` | `{ ids: string[] }` | Removed (revert / compaction cleanup) |
| `mcp.updated` | `McpServerDto` | MCP server status changed |
| `models.updated` | `{}` | Re-fetch `/api/models` and `/api/providers` |
| `config.changed` | `{ scope: 'global' \| 'project', directory }` | Command / prompt / skill / agent Markdown or `AGENTS.md` / `CLAUDE.md` changed on disk (debounced per root, ≈400ms). `directory` is the harness home (`~/.sofia`) for `global` or the workspace folder for `project`; re-fetch `/api/commands`, `/api/agents` and — for `global` — `/api/skills` |
| `browser.updated` | `{ open: boolean, agent: boolean, url?: string }` (`sessionId` = root conversation) | The conversation's headless browser opened/closed, or the agent used it (`agent: true`: the UI opens the browser panel) |

`SessionStatus = 'idle' | 'working' | 'waiting' (queued behind another conversation for a single-client model; clients show no spinner) | 'permission' | 'question' | 'error'`.

## Shared DTOs

```ts
type WorkspaceKind = 'folder' | 'chat'

interface WorkspaceDto {
  id: string
  name: string
  path: string            // '' for chat groups
  kind: WorkspaceKind
  icon: string
  modelId: string         // 'providerId/modelId' or ''
  expanded: boolean
  order: number
  conversations: ConversationSummaryDto[]   // newest first
}

interface ConversationSummaryDto {
  id: string              // = session id
  workspaceId: string
  title: string
  createdAt: string
  updatedAt: string
  status: SessionStatus
  parentId?: string       // set for subagent sessions
  archived?: boolean      // parked in the workspace's "Archived" folder
  archivedAt?: string     // set while archived
  pinned?: boolean        // kept at the top of the workspace's list
}

interface UsageDto {
  input: number; output: number; reasoning: number
  cacheRead: number; cacheWrite: number; cost: number
}
// `input` is the whole prompt (cached + uncached) for every provider, so
// cache hit % = cacheRead / input.

interface SessionDto extends ConversationSummaryDto {
  modelId: string
  agent: string           // 'build' | 'plan' | custom
  permissionMode: 'read-only' | 'ask' | 'plan' | 'full'
  reasoningMode: 'off' | 'low' | 'medium' | 'high'
  usage: UsageDto
  context: { used: number; limit: number }   // tokens of the last request / model window
  activeSkills: string[]
  queued: number          // follow-ups waiting in the inbox
  queuedMessages: { id: string; text: string; delivery: 'steer' | 'queue'; attachments: AttachmentDto[] }[]
                          // the same prompts, steer first; they become real messages (same id) once sent to the model
  messages?: MessageDto[] // only on GET /api/sessions/{id}
}

interface AttachmentDto {
  id: string
  name: string
  kind: 'image' | 'video' | 'audio' | 'file'
  mime: string
  size: string            // human readable
  bytes: number
  url: string             // '/api/attachments/{id}' (relative to the server)
}

interface ToolCallDto {
  id: string
  name: string
  status: 'running' | 'success' | 'error'
  summary: string         // one line (main argument)
  input?: string          // pretty JSON arguments
  output?: string
  error?: string
  duration: string        // largest unit that has started: '873ms' / '1.2s' / '2.25m' / '1.05h', or 'running…'
  childSessionId?: string // task tool: the subagent session
  images?: AttachmentDto[] // media the tool produced: screenshots, or images/audio/video from `read` (served from /api/attachments/{id})
}

interface MessageDto {
  id: string
  sessionId: string
  role: 'user' | 'assistant'
  text: string
  createdAt: string
  model?: string          // display label of the model
  reasoning?: string
  reasoningMode?: 'off' | 'low' | 'medium' | 'high'
  attachments?: AttachmentDto[]
  toolCalls?: ToolCallDto[]
  permission?: {
    tool: string; description: string; choices: string[]
    resolved?: string; requestId: string
  }
  question?: {
    prompt: string; options: string[]; resolved?: string
    formId: string; fieldKey: string; multi: boolean
  }
  questionSet?: {
    questions: { prompt: string; options: string[]; fieldKey: string; multi: boolean }[]
    answers?: (string | null)[]; confirmed?: boolean; formId: string
  }
  error?: string
  summary?: boolean       // compaction summary card
  compaction?: { status: 'running'|'done'|'failed'|'skipped', auto: boolean, before: number, after?: number, error?: string, result?: string }  // system notice card: compaction progress/result (token counts are estimates)
  retry?: { attempt: number, attempts: number, error?: string }  // transient provider failure being retried (499/408/429/5xx, dropped socket, timeout, empty stream): set while the next attempt runs, cleared by the attempt that succeeds. `attempts` is 4 (one try + three retries); when they run out the failure becomes `error` and the session goes to `status: 'error'`
  knowledge?: KnowledgeEntryDto[]  // card-only message: notes learned from the conversation
  contextNotes?: string[] // user messages only: one line per change (notes, branch, date, mode, language, context files, skills) noticed since the conversation's system prompt was frozen; replayed to the model inside a `<context_updates>` block appended to that user turn
  usage?: UsageDto
  durationMs?: number
}

// The frozen system prompt (Session.FrozenPrompt / FrozenAgent / FrozenTools) and the
// volatile facts it was built from (Session.PromptFacts) are persisted but server-side
// only ([JsonIgnore]): they never appear in SessionDto, MessageDto, exports or SignalR events.

interface KnowledgeEntryDto {
  id: string
  scope: 'workspace' | 'agent'
  text: string
  why?: string            // utility argument the reviewer gave for keeping the note
  deleted?: boolean       // the user removed the note after it was announced
}

interface MetricsDto {
  durationMs: number
  tokensPerSec: number
  usage: UsageDto         // session totals
  cost: number
  costAvailable: boolean  // false for free/local models
  context: { used: number; limit: number }
}
```

Permission choices are always `['Allow once', 'Allow for this session', 'Deny']`.

The `question` tool attaches its `question` / `questionSet` card to the assistant
message that made the call, so the prompt renders inside that reply (next to its
tool-call cards) instead of as a separate message. If that message already hosts
a card (several `question` calls in one step), the extra card gets its own
synthetic message. Such card-only messages are never replayed to the model.

## Workspaces and sessions

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `GET` | `/api/workspaces` | – | `WorkspaceDto[]` (ordered) |
| `POST` | `/api/workspaces` | `{ kind, path?, name?, icon?, modelId? }` | `WorkspaceDto` (folder: path must exist; one empty conversation is created) |
| `PATCH` | `/api/workspaces/{id}` | `{ name?, icon?, modelId?, expanded?, order? }` | `WorkspaceDto` |
| `DELETE` | `/api/workspaces/{id}` | – | `204` (sessions removed, files untouched) |
| `POST` | `/api/workspaces/{id}/sessions` | `{ title? }` | `SessionDto` (the new chat starts on the workspace's base model; permission/reasoning modes are copied from the workspace's most recent chat. A model that is unknown or switched off in Settings falls back to the server default) |
| `GET` | `/api/sessions/status` | – | `{ [sessionId]: SessionStatus }` (non-idle only) |
| `GET` | `/api/sessions/{id}` | – | `SessionDto` with `messages` |
| `PATCH` | `/api/sessions/{id}` | `{ title?, modelId?, agent?, permissionMode?, reasoningMode? }` | `SessionDto` |
| `DELETE` | `/api/sessions/{id}` | – | `204` |
| `POST` | `/api/sessions/{id}/archive` | – | `SessionDto` (aborts a live run, sets `archived: true`; the UI parks it in the workspace's **Archived** folder) |
| `POST` | `/api/sessions/{id}/unarchive` | – | `SessionDto` (`archived` cleared) |
| `POST` | `/api/sessions/{id}/pin` | – | `SessionDto` (`pinned: true`; first among the pinned) |
| `POST` | `/api/sessions/{id}/unpin` | – | `SessionDto` (`pinned` cleared) |
| `PUT` | `/api/workspaces/order` | `{ ids: string[] }` | `204` (manual order of the sidebar tree: first id = top, workspace folders first and chat groups after them; ids the server does not know are ignored and the workspaces it knows but the client omitted keep their place at the end) |
| `PUT` | `/api/workspaces/{id}/sessions/order` | `{ ids: string[] }` | `204` (manual order, first id = top; lists are sorted pinned first, then by this order, then by recency) |
| `POST` | `/api/sessions/{id}/prompt` | `PromptRequest` | `{ messageId }` |
| `POST` | `/api/sessions/{id}/command` | `{ name, arguments?, attachments?: string[] }` | `{ messageId }` |
| `POST` | `/api/sessions/{id}/abort` | – | `204` |
| `POST` | `/api/sessions/{id}/compact` | – | `204` (summary arrives as a `message.created` with `summary: true`) |
| `POST` | `/api/sessions/{id}/revert` | `{ messageId }` | `{ removed: string[], restoredFiles: string[] }` |
| `POST` | `/api/sessions/{id}/branch` | `{ messageId }` | `SessionDto` with `messages` (new session in the same workspace titled "<title> (branch)", holding a copy of the history up to and including `messageId`; subagent conversations and file snapshots are copied too, workspace files are not forked; `409` while a reply is running; the new session also arrives as `session.created`) |
| `POST` | `/api/sessions/{id}/permissions/{requestId}` | `{ decision: 'once' \| 'always' \| 'reject' }` | `204` |
| `POST` | `/api/sessions/{id}/questions/{formId}` | `{ answers: (string \| string[] \| null)[] }` | `204` |
| `GET` | `/api/sessions/{id}/diff` | – | `FileDiffDto[]` (files changed by this session) |
| `POST` | `/api/sessions/{id}/skills` | `{ skillId }` | `SessionDto` |
| `DELETE` | `/api/sessions/{id}/skills/{skillId}` | – | `SessionDto` |
| `GET` | `/api/sessions/{id}/export` | – | `{ version: 1, info: SessionDto, messages: MessageDto[] }` |
| `POST` | `/api/sessions/import` | `{ workspaceId?, info, messages }` | `SessionDto` |
| `GET` | `/api/stats` | – | `{ sessions, prompts, steps, tokens: { input, output }, cost, activeDays, streak }` |

```ts
interface PromptRequest {
  text: string
  attachments?: string[]        // ids from POST /api/attachments
  delivery?: 'prompt' | 'steer' | 'queue'
  modelId?: string              // applied to the session before the run
  agent?: string
  permissionMode?: string
  reasoningMode?: string
}
```

Delivery semantics (pi.dev): `steer` is injected after the tool call that is
running and skips the remaining queued tool calls of that step; `queue` waits
until the agent finishes, then runs as the next turn. `prompt` on a busy session
behaves like `queue`. While waiting, both appear in `SessionDto.queuedMessages`
(carried by `session.updated`); the UI shows them greyed out under the
transcript. A steer is stored as a normal user message, but the model history
folds it into the tool results it interrupted (no new user turn) so the prompt
cache of the earlier steps survives.

## Models and providers

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `GET` | `/api/models` | – | `ModelDto[]` (each with `enabled`; disabled models stay listed so they can be switched back on) |
| `POST` | `/api/models/enabled` | `{ id?, ids?, enabled }` | `204` (`id` and/or `ids` for bulk changes; persisted in the server config; disabled models are hidden from pickers and the available-models list, skipped as default and rejected when chosen for a session — Settings → Models → provider card → "Manage models" switches them back on) |
| `GET` | `/api/providers` | – | `ProviderDto[]` |
| `GET` | `/api/providers/presets` | – | `ProviderPresetDto[]` |
| `POST` | `/api/providers` | `{ name, type, baseUrl, apiKey?, models?: string[], presetId? }` | `ProviderDto` |
| `PATCH` | `/api/providers/{id}` | `{ enabled?, apiKey?, baseUrl?, name?, preserveThinking? }` | `ProviderDto` (`preserveThinking` = resend earlier reasoning as `reasoning_content`; default on for deepseek/opencode-go, off elsewhere) |
| `DELETE` | `/api/providers/{id}` | – | `204` (discovered local providers are disabled instead) |
| `POST` | `/api/providers/refresh` | – | `ProviderDto[]` (re-runs local discovery and model listing) |
| `POST` | `/api/providers/probe` | `{ type, baseUrl, apiKey? }` | `{ models: ModelDto[] }` (does not save) |
| `POST` | `/api/providers/github-copilot/login` | – | `CopilotDeviceFlow` (starts the GitHub OAuth device flow) |
| `POST` | `/api/providers/github-copilot/login/poll` | `{ deviceCode }` | `{ status: 'pending' \| 'success' \| 'expired' \| 'denied', interval?, provider? }` (on `success` the GitHub token is stored on the `github-copilot` provider, which is created/enabled) |

```ts
interface ModelDto {
  id: string               // 'providerId/modelId'
  providerId: string
  modelId: string
  label: string
  provider: string         // provider display name
  local: boolean
  contextLength: number
  maxOutput: number
  context: string          // '128K ctx'
  connected: boolean
  reasoning: boolean
  vision: boolean
  audio: boolean           // accepts audio input (runtime metadata or known family)
  video: boolean           // accepts video input (runtime metadata only)
  loaded?: boolean         // local runtimes that report it
}

interface ProviderDto {
  id: string
  name: string
  kind: 'local' | 'cloud'
  type: 'openai' | 'openai-responses' | 'anthropic'
  baseUrl: string
  status: 'connected' | 'disconnected' | 'error'
  detail: string
  enabled: boolean
  custom: boolean
  discovered: boolean
  hasKey: boolean
  models: number
}

interface ProviderPresetDto {
  id: string; name: string; type: 'openai' | 'openai-responses' | 'anthropic'; baseUrl: string; envKey?: string
  auth?: 'github-device'   // sign in through the GitHub device flow instead of pasting a key
  note?: string
}

interface CopilotDeviceFlow { deviceCode: string; userCode: string; verificationUri: string; interval: number; expiresIn: number }
```

Local discovery probes Unsloth Studio (`:8888`), Ollama (`:11434`), llama.cpp
(`:8080`), vLLM (`:8000`) and LM Studio (`:1234`) on start and on refresh.
Context windows come from each runtime's own metadata (`context_length`,
`max_model_len`, `/props` `n_ctx`, Ollama `/api/show`), then a built-in table
for cloud models, then a 32K fallback. Cloud providers are enabled from
`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `OPENROUTER_API_KEY`, `GEMINI_API_KEY`,
`DEEPSEEK_API_KEY`, `GROQ_API_KEY`, `MISTRAL_API_KEY`, `XAI_API_KEY`,
`OPENCODE_API_KEY`, `QWENCLOUD_API_KEY`, `COPILOT_GITHUB_TOKEN` or keys
saved through the API.

Provider presets with specific behaviour:

- **DeepSeek Platform** (`deepseek`, `https://api.deepseek.com`): thinking is
  toggled with `thinking: { type }` + `reasoning_effort`, and each assistant
  turn's `reasoning_content` is sent back (the API rejects tool conversations
  that drop it).
- **OpenCode Go** (`opencode-go`, subscription): models are listed from
  `/inference/go/v1/models`; each model is routed to Chat Completions
  (`/inference/go/openai/v1`) or Anthropic Messages (`/inference/go/anthropic`,
  Claude, Qwen and MiniMax M2/M3). Models only served through the Responses or
  Google APIs (GPT, Grok, Muse Spark, Gemini) are not offered. Go rejects
  third-party requests that do not identify themselves: it gets the
  `sofia/<version>` agent user agent and `x-opencode-session`, without which the
  gateway answers `MissingSessionID`.
- **QwenCloud Token Plan** (`qwencloud`, subscription, Singapore region):
  OpenAI-compatible endpoint; reasoning is switched with `enable_thinking`.
- **GitHub Copilot** (`github-copilot`, subscription): signs in with the OAuth
  device flow (client id overridable with `SOFIA_GITHUB_CLIENT_ID`) or a
  pasted GitHub token; the server exchanges it for a short-lived Copilot token
  in memory. Only models that support `/chat/completions` are listed.

Subscription providers report zero per-token cost.

Session identity (`Providers/ProviderIdentity.cs`) applies to **every** provider, so a Sofia conversation keeps one
id through any gateway in front of a model. Each LLM request carries the same conversation id under the generic and
proxy-specific names — `x-session-id`, `x-litellm-trace-id` (LiteLLM's highest-priority session header),
`x-claude-code-session-id` (Claude Code compatible gateways; LiteLLM also auto-detects any
`x-<vendor>-session-id`) and `x-dynamo-session-id` (NVIDIA Dynamo) — plus the vendor's own name where it differs:
`x-opencode-session` for Go, and `x-amzn-bedrock-agentcore-runtime-session-id` (padded to AWS's 33-character
minimum) with `x-amzn-bedrock-agentcore-policy-session-id` for a Bedrock AgentCore endpoint. Names an endpoint does
not know are inert; `x-claude-code-session-id` is omitted when the endpoint is Anthropic itself. Requests with no
conversation (model discovery, probes) fall back to one stable per-process `harness_…` id, so none leaves unidentified.

## Agents, commands, skills

| Method | Path | Response |
| --- | --- | --- |
| `GET` | `/api/agents?directory=` | `{ id, name, description, mode: 'primary' \| 'subagent' \| 'all', source: 'builtin' \| 'global' \| 'project', path? }[]` (built-ins first; `directory` adds the workspace's agents) |
| `GET` | `/api/commands?directory=` | `{ name, description, source: 'builtin' \| 'global' \| 'project', agent?, model?, subtask?, path? }[]` |
| `GET` | `/api/skills?directory=` | `{ id, name, description, path, content, source }[]` (the Settings → Skills tab omits `directory`, so it lists only global skills) |

Agents are the built-ins (`build`, `plan`, `general`, `explore`) plus Markdown
files in `~/.sofia/agents`, `<workspace>/.agents/agents` and
`<workspace>/.sofia/agents` (other harnesses' folders are not read; `.sofia`
wins over `.agents`): frontmatter `description`, `mode` (`subagent` default |
`primary` | `all`), `tools` (comma list, e.g. `read, grep, bash`), `mcp`; the body
is the system-prompt addendum. `primary`/`all` agents show in the agent picker
(`PATCH /api/sessions/{id}` `{ agent }`); `subagent`/`all` ones are `task`
targets and can be called with `@name` in a prompt: the server keeps the text
as typed and gives the model a hidden note to delegate through `task`.

Commands are Markdown prompt templates in `~/.sofia/commands`,
`~/.sofia/prompts`, `<workspace>/.agents/commands`, `.agents/prompts`,
`<workspace>/.sofia/commands`, `.sofia/prompts` (project overrides global, and
`.sofia` overrides `.agents`) plus the
built-ins `compact` and `init`. Sub-folders are part of the name (`git/pr` →
`/git/pr`). Frontmatter: `description`, `agent` (a primary agent takes over the
session; a subagent gets the work delegated), `model` (`providerId/modelId`,
switches the session), `subtask: true` (delegate instead of running inline).
`POST /api/sessions/{id}/command` expands `$ARGUMENTS` / `$1`…`$9`, ``!`cmd` ``
(output injected — only when the session's permission mode is `full`, otherwise
left as text) and `@path` (a file inside the workspace is appended as a
`<file>` block). Unknown command → `404`, unknown `agent` / `model` → `400`.

Skills are `SKILL.md` folders under `~/.sofia/skills`,
`<workspace>/.agents/skills` (the shared agentskills folder) and
`<workspace>/.sofia/skills`.

All of these files are read on every message, so edits need no restart. The
server also watches them — the harness home recursively (filtered to the
folders above, never the database or attachments) plus each workspace's root for
`AGENTS.md` / `CLAUDE.md` and its `.sofia` and `.agents` folders recursively —
and publishes a debounced `config.changed`, which is what makes a command
written seconds ago show up in the composer palette. Other harnesses' folders
are neither read nor watched.

The read-only `sofia_info` tool (topics `overview`, `skills`, `commands`,
`agents`, `mcp`, `providers`, `config`, `context`) tells agents where all of
this lives and how to change it; API keys, MCP tokens/env values and URL
credentials are never included.

## Knowledge notes

Short learnings the harness distils from a finished conversation (see
`docs/project_init.md`). Each note is scoped to a **workspace** (project facts) or
an **agent** (how that agent should behave, e.g. user preferences) and is injected
into the matching system prompts. The reviewer must justify every note as useful —
items it does not mark `"useful": true` with a `why` argument are dropped, so only
notes that earned their place are stored. When notes are generated the session gets
a card-only assistant message with `knowledge: KnowledgeEntryDto[]` (never replayed
to the model); the card shows each note's utility argument. Deleting a note marks
its entry `deleted: true` on that card and publishes `message.updated`.

Agents can also read and write notes through the `notes` tool (actions `list`,
`save`, `delete`). Scope `workspace` uses the same notes as above (so they show up
in `GET /api/knowledge` and the system prompt). Scope `chat` is a per-conversation
scratchpad (`scope: 'chat'`, `sessionId` = the chat, or its parent for subagents)
that is **not** returned by `GET /api/knowledge`, never injected into the prompt
and removed together with the session.

### Memory tool

Agents also have a `memory` tool: a per-workspace key/value store (no HTTP
endpoint, no event, not injected into the prompt). Actions: `list` (default),
`get` (`key`), `set` (`key`, `value`; an empty value deletes the key). Keys are
≤ 100 characters of `[A-Za-z0-9._:/-]`, values ≤ 20 000 characters, ≤ 200 keys per
workspace; violations come back as a tool error. Entries are shared by all chats
and subagents of the workspace and removed with it. Allowed in every permission
mode (`ToolKind.Read`).

| Method | Path | Response |
| --- | --- | --- |
| `GET` | `/api/knowledge?workspaceId=&agent=` | `KnowledgeNote[]`, newest first. `workspaceId` keeps that workspace's notes plus all agent notes; `agent` keeps that agent's notes plus all workspace notes |
| `DELETE` | `/api/knowledge/{id}` | `204` (`404` when unknown) |

```ts
interface KnowledgeNote {
  id: string
  scope: 'workspace' | 'agent'
  workspaceId: string     // workspace the note was learned in
  agent: string           // agent that was running
  text: string
  why: string             // utility argument from the review ('' on notes saved by the `notes` tool)
  sessionId: string
  messageId: string       // the card that announced it
  createdAt: string
}
```

## MCP servers

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `GET` | `/api/mcp` | – | `McpServerDto[]` |
| `POST` | `/api/mcp` | `{ name, transport: 'command' \| 'web', command?, url?, token?, env? }` | `McpServerDto` (connects right away) |
| `POST` | `/api/mcp/{id}/connect` | – | `McpServerDto` |
| `POST` | `/api/mcp/{id}/disconnect` | – | `McpServerDto` |
| `DELETE` | `/api/mcp/{id}` | – | `204` |

```ts
interface McpServerDto {
  id: string; name: string; transport: 'command' | 'web'
  command: string; url: string; hasToken: boolean; enabled: boolean
  status: 'connected' | 'error' | 'stopped' | 'pending'
  error?: string; tools: number; toolNames: string[]
}
```

## Background processes

Long-running commands (dev servers, watchers) started by the agent's processes
tool (actions start, list, output, kill). They are killed with their
conversation and on server shutdown.

- `GET /api/processes?sessionId=` → `[{id, command, cwd, sessionId, pid, status (running|exited|killed), exitCode, startedAt, endedAt}]`
- `GET /api/processes/{id}/output?tail=200` → `{info, output}`
- `POST /api/processes/{id}/kill` → the updated process (404 if unknown)

## Live browser

Each conversation can own one headless Chromium (system Chrome/Edge, else a
one-time Playwright Chromium download), driven by the agent's `browser` tool and
viewable/drivable from the UI. Subagent conversations share their parent's
browser (`parentId` is resolved to the root). Nothing opens on the desktop.

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `GET` | `/api/sessions/{id}/browser` | – | `{ open: boolean, url?: string }` |
| `POST` | `/api/sessions/{id}/browser/close` | – | `204` (closes the context and its tabs) |
| `WS` | `/api/sessions/{id}/browser/stream` | – | live view, below (starts the browser if needed; remote clients pass `?access_token=`) |

Stream messages from the server: **binary** = one JPEG frame of the active tab
(CDP screencast; only while a viewer is connected); **text** = JSON:

```ts
{ type: 'state', url, title, loading, agentBusy,
  viewport: { width, height }, tabs: { index, url, active }[] }
{ type: 'cursor', x, y, action }        // where the agent is acting (viewport px)
{ type: 'error', message, fatal? }      // fatal: the browser could not start
```

Messages from the client (JSON, ignored while `agentBusy` is true):

```ts
{ type: 'navigate', url } | { type: 'back' } | { type: 'forward' } | { type: 'reload' }
{ type: 'resize', width, height }       // viewport in CSS px (320–2560)
{ type: 'mouse', action: 'move'|'down'|'up'|'wheel', x, y, button?, deltaX?, deltaY? }  // viewport px
{ type: 'key', key }                    // 'a', 'Enter', 'Control+a' (Playwright key names)
{ type: 'text', text }                  // paste / IME text
{ type: 'tab', index } | { type: 'closeTab', index }
```

Only `http(s)` pages (and `about:blank`) can be opened; bare hosts get a scheme
and plain text becomes a web search. The browser closes with its conversation
and after 20 idle minutes without viewers.

## Workspace files, VCS, attachments

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `GET` | `/api/vcs?directory=` | – | `{ isRepo, branch, branches: string[] }` |
| `GET` | `/api/vcs/diff?directory=` | – | `FileDiffDto[]` (working tree vs `HEAD`, untracked included) |
| `GET` | `/api/worktrees?directory=` | – | `{ root, worktrees: { directory, branch, isMain }[] }` |
| `POST` | `/api/worktrees` | `{ directory, name }` | `{ directory, branch, isMain }` |
| `DELETE` | `/api/worktrees` | `{ directory, worktree, force? }` | `204` |
| `POST` | `/api/attachments` | multipart `file` | `AttachmentDto` |
| `GET` | `/api/attachments/{id}` | – | file bytes |
| `POST` | `/api/open` | `{ path, app: 'folder' \| 'vscode' \| 'terminal' }` | `204` (loopback only; `terminal` opens the OS-native terminal — Windows Terminal/PowerShell, Terminal.app, or the first Linux emulator found — in the folder) |

```ts
interface FileDiffDto {
  file: string            // path relative to the workspace
  patch: string           // unified diff
  additions: number
  deletions: number
  status: 'added' | 'deleted' | 'modified'
}
```

## Server config

| Method | Path | Body | Response |
| --- | --- | --- | --- |
| `GET` | `/api/config` | – | `ConfigDto` |
| `PATCH` | `/api/config` | partial `ConfigDto` | `ConfigDto` |

```ts
interface ConfigDto {
  defaultModel: string          // '' = first connected model
  shell: string                 // '' = pwsh/powershell on Windows, bash elsewhere
  autoCompact: boolean
  compactThreshold: number      // fraction of the context window, e.g. 0.8
  discovery: boolean            // probe local runtimes
  maxToolOutput: number         // bytes kept per tool result
  keepScreenAwake: boolean      // hold the display awake while any agent run is active (server machine)
  knowledge: boolean            // distil knowledge notes after each finished conversation
  searchProvider: 'duckduckgo' | 'searxng'  // backend of the web_search tool
  searxngUrl: string            // SearXNG instance base URL ('' when unused)
  dataDir: string               // read-only
  configPath: string            // read-only
}
```
