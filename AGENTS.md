# AGENTS.md — Sofia

## Project

Sofia is a local AI harness built from scratch (no other harness underneath):
`src/Sofia.Server` is the backend — an ASP.NET Core (.NET 10) agent server with
minimal APIs, LiteDB, DI, an in-process event bus and SignalR — and
`src/Sofia.UI` (Vue) is its web client. The UI is the feature guide for the
server: every widget maps to a server capability. The agent design follows
pi.dev (minimal prompt, small core tool set, AGENTS.md context, skills,
steer/follow-up, compaction), extended with workspaces, permission modes,
questions, parallel subagents, MCP, change tracking/revert, local runtime
auto-discovery and OpenAI/Anthropic compatibility.
Desktop packaging (server + UI in one app) is out of scope for now.

- API contract: `docs/server_api.md` (keep it in sync with both sides).
- Server architecture: `docs/project_init.md`.
- Frontend architecture: `docs/front_end_init.md`.

## Stack

- Server: `src/Sofia.Server` (net10.0, minimal APIs, `Sofia.slnx` at the repo
  root, tests in `src/Sofia.Server.Tests`). LiteDB data in `~/.sofia`
  (`SOFIA_HOME` overrides). Listens on `http://127.0.0.1:5199`; remote clients
  must pair (`POST /api/pair` → `/api/pair/connect`).
- Packages: LiteDB, ModelContextProtocol (MCP client, stdio + HTTP), DiffPlex.
- Events: `SessionStore` persists and publishes on `IEventBus`;
  `HubBroadcaster` pushes every event to SignalR (`/hubs/events`, client
  method `event`); `IEventHandler`s react (e.g. `TitleHandler`).
- Agent loop: `Agent/SessionRun.cs` (one run per active session) driven by
  `Agent/AgentService.cs`.
- Providers: OpenAI Chat Completions + Anthropic Messages streaming clients;
  local discovery of Unsloth (`:8888`), Ollama (`:11434`), LM Studio (`:1234`),
  llama.cpp (`:8080`), vLLM (`:8000`); cloud keys from env or Settings.
- UI: Vue 3.5 + Vite + TypeScript, Mantine Vue (`@mantine-vue/core`),
  `@microsoft/signalr`. Backend client in `src/Sofia.UI/src/services/sofia.ts`,
  shared shapes in `src/Sofia.UI/src/types.ts`, store in
  `src/Sofia.UI/src/composables/useHarness.ts`, connect panel in
  `src/Sofia.UI/src/components/BackendGate.vue`. Vite proxies `/api` and
  `/hubs` to the server (`SOFIA_SERVER_URL` overrides the target).
- Grey canvas, brown accent (`#a9744f`), beige detail on reasoning blocks.
  Theme: `src/Sofia.UI/src/styles/theme.ts` + `src/Sofia.UI/src/styles/global.css`.
- Vue Vapor is **not enabled** (Mantine Vue is virtual-DOM, Vapor still RC).
  Switch stubbed in `vite.config.ts` + `src/main.ts`, documented in
  `src/Sofia.UI/README.md`.
- Test model: Unsloth Studio `unsloth/NVIDIA-Nemotron-3.5-Bolt-E4B-GGUF`
  (UD-Q8_K_XL, 131072 ctx) is auto-discovered when Unsloth runs locally.

## Commands

```bash
dotnet build Sofia.slnx
dotnet test Sofia.slnx
dotnet run --project src/Sofia.Server     # http://127.0.0.1:5199, serves src/Sofia.UI/dist when built
dotnet publish src/Sofia.Server -c Release -o out   # also builds Sofia.UI into out/wwwroot (-p:BuildUiOnPublish=false to skip)

cd src/Sofia.UI
npm install
npm run dev        # http://localhost:5183 (proxies /api and /hubs to :5199)
npm run build
npm run preview
npm run typecheck  # vue-tsc --noEmit
```

## Conventions

- Comments and docs in code: English. Semantic HTML, minimal custom CSS.
- Prefer editing existing files over creating new ones.
- Server: minimal APIs grouped in `Api/`; state changes go through
  `SessionStore` so the UI hears about them; never block the agent loop on the
  UI. Map errors with `NotFoundException` / `ConflictException` /
  `ArgumentException` (404 / 409 / 400).
- When an endpoint or event changes, update `docs/server_api.md`,
  `src/Sofia.UI/src/types.ts` and the consumer in the same change.
- Platforms: server and UI target Windows, macOS and Linux. Keep OS-specific
  code behind `OperatingSystem.Is*()` checks (shell selection and PATH lookup in
  `Infrastructure/ProcessRunner.cs`, path helpers in `Infrastructure/Common.cs`,
  open-folder in `Api/PlatformEndpoints.cs`); never hard-code `\` separators or
  Windows-only executables. Only Windows has been exercised so far.
- Edit only `.ts`/`.vue` sources in `Sofia.UI`; sibling `.js`/`.d.ts`
  files are `tsc` emit noise — never author or commit them
  (`typecheck` is `--noEmit`).
- Verify changes with `dotnet test` and `npm run typecheck` / `build`; keep
  the server, the dev server and any background processes shut down at the
  end of the session.

## Reference material

- `docs/project_init.md` — server architecture: agent loop, permissions,
  subagents, compaction, providers, context engineering, security.
- `docs/front_end_init.md` — frontend architecture, component map, CSS
  conventions, sound system, and TypeScript pitfalls (start here for UI work).
## Future investigations

### Token savings: RTK + Headroom

- Look into adding [RTK](https://github.com/rtk-ai/rtk) (filters common command
  output) and [Headroom](https://github.com/headroomlabs-ai/headroom)
  (compresses tool results) to cut token usage — reportedly up to ~90% lower
  usage when combined. Evaluate how each plugs into the server's tool-result
  path (`Agent/SessionRun.cs` → `Complete`) and measure before/after on
  representative sessions.

## Plan specs

- Work is tracked as Markdown specs in `.specs/<state>/NNN-slug.md`. The **folder
  is the state**: `to-do/`, `in-progress/`, `blocked/`, `done/`, `cancelled/`.
  There is no `status` field. `done/` is an archive: those specs are not listed
  in `INDEX.md`.
- `.specs/` is **local and git-ignored**. Never `git add` (or `git add -f`)
  anything inside it, and never assume specs exist in a fresh clone; run
  `/init-plan` to recreate the structure.
- `.specs/README.md` is the format contract. `.specs/INDEX.md` is generated and
  must never be edited by hand.
- Images, videos, sample files and any other binary evidence live in
  `.specs/spec_assets/`, named `<spec-id>-<short-slug>.<ext>` and referenced from
  the spec by relative path. Never inline binary data in a spec and never place
  assets inside a state folder.
- Before starting any user-requested work, look for an existing spec describing
  it and reuse/update that spec instead of creating a duplicate. If none
  matches, ask the user whether to create one (or create it when the user already
  asked for it) with `/plan-spec`, giving it clear context, steps and acceptance
  criteria. New specs start in `to-do/`.
- Change state by moving the file
  (`node .specs/scripts/plan-index.mjs --move <id> <state>`), never by copying it
  or adding a status field. Record progress, decisions and verification in
  `## Progress log` / `## Open questions`.
- When work is complete, tick the acceptance criteria, bump `updated`, move the
  spec to `done/` and regenerate `INDEX.md` with
  `node .specs/scripts/plan-index.mjs`.
- Commit the code the spec produced, referencing the spec id
  (`type(plan-<id>): <summary>`), but never the spec itself.
