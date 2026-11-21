# AGENTS.md — Sofia

## Project

Sofia is a local AI harness built from scratch (no other harness underneath):
`src/Sofia.Server` is the backend — an ASP.NET Core (.NET 10) host with minimal
APIs, DI, an in-process event bus and SignalR — over `src/Sofia.Core`, the
engine (agent loop, providers, tools, MCP, LSP, browser, SQLite store, events).
`src/Sofia.Contracts` is the dependency-free contract assembly (entities, wire
DTOs, event names, `Json.Options`); `src/Sofia.Client` is the `ISofiaBackend`
transport abstraction with an HTTP+SignalR implementation and
`src/Sofia.Client.InProc` its in-process counterpart. `src/Sofia.UI` (Vue) is
the current web client. The UI is the feature guide for the server: every widget
maps to a server capability. The agent design follows pi.dev (minimal prompt,
small core tool set, AGENTS.md context, skills, steer/follow-up, compaction),
extended with workspaces, permission modes, questions, parallel subagents, MCP,
change tracking/revert, local runtime auto-discovery and OpenAI/Anthropic
compatibility.
Per-platform self-contained `.zip` releases (win-x64, linux-x64, osx-arm64,
osx-x64) are built by `.github/workflows/release.yml` on `v*` tags. The WebView
desktop shell was removed in plan-038; the native Avalonia client is
`src/Sofia.App` (plan 039: theme, controls, shell frame and the screenshot/frame
harness; 040+ add the real sidebar, transcript, composer and settings). A
single-click installer remains future work.

- API contract: `docs/server_api.md` (keep it in sync with both sides).
- Server architecture: `docs/project_init.md`.
- Frontend architecture: `docs/front_end_init.md` (Vue, until 044).
- Native UI architecture: `docs/app_ui.md` (tokens, icons, motion, sounds,
  screenshot harness and the documented web deviations).

## Stack

- Server: `src/Sofia.Server` (net10.0, minimal APIs + SignalR hub + access
  middleware, `Sofia.slnx` at the repo root). SQLite data in `~/.sofia`
  (`SOFIA_HOME` overrides). Listens on `http://127.0.0.1:5199`; remote clients
  must pair (`POST /api/pair` → `/api/pair/connect`).
- Engine: `src/Sofia.Core` (net10.0 class library, no ASP.NET reference — that
  is what keeps the host boundary real). Hosts register it with
  `services.AddSofiaCore(options)`; endpoint behaviours with rules behind them
  (workspaces, sessions, diff, files, attachments, config, provider/MCP/LSP/
  browser admin, host launcher) live in `src/Sofia.Core/Services` so the
  in-process host runs the same code and `Api/*` only binds and translates.
- Contracts: `src/Sofia.Contracts` — entities, wire DTOs and request bodies,
  event names, `Json.Options`. No package references, so a WASM head can use it.
- Clients: `ISofiaBackend` in `src/Sofia.Client` (`HttpSofiaBackend`: REST +
  SignalR over `/hubs/events`, auto-reconnect plus a `resync` signal) and
  `src/Sofia.Client.InProc` (`InProcSofiaBackend`: direct Core calls, events
  straight from `IEventBus`).
- Packages (Core): Microsoft.EntityFrameworkCore.Sqlite, ModelContextProtocol
  (MCP client, stdio + HTTP), DiffPlex, Microsoft.Playwright. LiteDB is kept
  **only** for the one-shot migration of legacy `sofia.db` files (see
  `Data/LiteDbMigration.cs`); remove the package and that file in 1.0 once 0.x
  data no longer needs converting.
- Events: `SessionStore` persists and publishes on `IEventBus`;
  `HubBroadcaster` (server) pushes every event to SignalR (`/hubs/events`,
  client method `event`); `IEventHandler`s react (e.g. `TitleHandler`).
- Message order: the global `Seq` comes from a `counters` table
  (`UPDATE … RETURNING`, see `SofiaDb.NextSeq`), not from an in-memory field, so
  two hosts writing one store never duplicate a value. `DataDirLock`
  (`<dataDir>/sofia.lock`) is advisory: the server only warns when another
  process owns the directory, a host that owns it (`SofiaCoreOptions.ExclusiveDataDir`)
  refuses to start.
- Agent loop: `Agent/SessionRun.cs` (one run per active session) driven by
  `Agent/AgentService.cs`.
- Providers: OpenAI Chat Completions + Anthropic Messages streaming clients;
  local discovery of Unsloth (`:8888`), Ollama (`:11434`), LM Studio (`:1234`),
  llama.cpp (`:8080`), vLLM (`:8000`); cloud keys from env or Settings.
- ACP: `src/Sofia.Acp` (net10.0) stdio bridge for editors (Zed, JetBrains, Neovim); `dotnet run --project src/Sofia.Acp`.
- Native UI: `src/Sofia.App` (Avalonia 12.1.6, `net10.0`, AXAML, CompiledBindings):
  `Theme/` (tokens, type scale, Lucide `Icons.axaml`, motion timings, control themes),
  `Controls/` (SofiaIcon, CoffeeSpinner, WorkingRowBorder, CollapsiblePanel, SofiaPopover,
  ToggleSwitch, SofiaMeter), `Shell/` (MainWindow, ShellHost, ShellView, BackendGate), `Services/`
  (ThemeService, MotionService, SoundService, LocalState, HarnessStore, ScreenshotHarness)
  and `Assets/` (logos, wallpaper tile, six pre-rendered WAVs). Heads:
  `src/Sofia.App.Desktop` (Avalonia.Desktop + SoundFlow + backend resolver) and
  `src/Sofia.App.Browser` (`net10.0-browser1.0` WASM head: `BrowserInterop` + `localStorage` state +
  WebAudio + page-origin/pairing resolver; publishes a `wwwroot` `Sofia.Server` can serve — see
  `docs/app_ui.md`). Keep the shared project free of Core/SQLite/SoundFlow; tests are headless (`Sofia.App.Tests`).
- UI: Vue 3.5 + Vite + TypeScript, Mantine Vue (`@mantine-vue/core`),
  `@microsoft/signalr`. Backend client in `src/Sofia.UI/src/services/sofia.ts`,
  shared shapes in `src/Sofia.UI/src/types.ts`, store in
  `src/Sofia.UI/src/composables/useHarness.ts`, connect panel in
  `src/Sofia.UI/src/components/BackendGate.vue`. Vite proxies `/api` and
  `/hubs` to the server (`SOFIA_SERVER_URL` overrides the target).
- Plan 044 status: `Sofia.App.Browser` builds, is served by `Sofia.Server` at `/` and pairs a remote
  browser (evidence in the spec); the Vue cutover (delete `src/Sofia.UI`, drop npm from CI, Docker,
  release and dependabot) stays blocked on the full feature-map + animation parity gate.
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
dotnet run --project src/Sofia.Server     # http://127.0.0.1:5199, serves the Avalonia browser bundle when built, else src/Sofia.UI/dist
dotnet publish src/Sofia.App.Browser -c Release -r browser-wasm -o out/browser   # -> out/browser/wwwroot (needs wasm-tools)
dotnet run --project src/Sofia.App.Desktop -- --fixture   # native shell with deterministic fixture data
dotnet run --project src/Sofia.App.Desktop -- --fixture --screenshot 039 --all-sizes --out-dir .specs/spec_assets
dotnet test src/Sofia.App.Tests           # headless token/control/motion/screenshot gate
dotnet publish src/Sofia.Server -c Release -o out   # also builds Sofia.UI into out/wwwroot (-p:BuildUiOnPublish=false to skip)
dotnet publish src/Sofia.Server -c Release -r win-x64 --self-contained true -p:InvariantGlobalization=true -o out   # per-platform release build (RIDs: win-x64, linux-x64, osx-arm64, osx-x64)

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
- Server: minimal APIs grouped in `Api/`; anything with a rule behind it
  (cascades, validation, ordering, imports, diff filtering) belongs in
  `Sofia.Core/Services`, not in the endpoint; state changes go through
  `SessionStore` so the UI hears about them; never block the agent loop on the
  UI. Map errors with `NotFoundException` / `ConflictException` /
  `ArgumentException` (404 / 409 / 400).
- When an endpoint or event changes, update `docs/server_api.md`,
  `ISofiaBackend` (`src/Sofia.Client` + its in-process counterpart),
  `src/Sofia.UI/src/types.ts` and the consumer in the same change.
- Layers: `Sofia.Contracts` has no package references; `Sofia.Core` must not
  reference `Microsoft.AspNetCore.*`; `Sofia.Client` references only Contracts
  (so it can run in a WASM head); `Sofia.Client.InProc` may reference Core.
  Endpoint request bodies live in `Sofia.Contracts.Domain` so every transport
  posts the same object.
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
