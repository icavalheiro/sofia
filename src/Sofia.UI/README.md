# Sofia.UI

Harness UI for Sofia — a local desktop + web AI harness. It is the client of
**Sofia.Server** (`src/Sofia.Server`, ASP.NET): REST for commands and queries,
one SignalR hub (`/hubs/events`) for live events. The full contract lives in
[`docs/server_api.md`](../../docs/server_api.md).

## Stack

- **Vue 3.5** (stable) + **Vite** + TypeScript
- **Mantine Vue** (`@mantine-vue/core`) — theme + base components
- **@microsoft/signalr** — live event stream from Sofia.Server
- **Grey canvas, brown accent, beige detail** — the interface stays neutral so
  brown (`#a9744f`) reads as a deliberate primary, not a tint over everything.
  Beige survives only on reasoning blocks. See `src/styles/theme.ts` +
  `global.css`.

Vue Vapor is **not enabled yet**: Mantine Vue is a virtual-DOM library and Vapor
is still in RC. The project is set up so Vapor can be switched on later with a
one-line change — see “Enabling Vapor” below.

## Run

Start the server first, then the UI:

```bash
dotnet run --project src/Sofia.Server   # http://127.0.0.1:5199

cd src/Sofia.UI
npm install
npm run dev      # http://localhost:5183
```

In dev, Vite proxies `/api` and `/hubs` (WebSockets included) to
`SOFIA_SERVER_URL` (default `http://127.0.0.1:5199`), so the UI always talks
same-origin. `npm run build` produces `dist/`, which Sofia.Server serves at `/`
when present.

Other scripts: `npm run build`, `npm run preview`, `npm run typecheck`.

### Connecting

On startup the UI probes `GET /api/health`, trying in order: the connection
saved from the connect panel (localStorage `sofia.backend.v2`), the optional
`VITE_SOFIA_SERVER` (see `.env.example`; only for UIs hosted where the server is
not reachable same-origin), then the same origin. When nothing answers, the
connect panel asks for a server URL — plus a token, or a one-time pairing code
generated under Settings → General → Remote access on the server machine.
Loopback clients need no credentials.

## What's here

- **Sidebar** — folder **workspaces** and **chat groups**, each holding its own
  **conversations** (server sessions; subagent sessions stay hidden and open
  from their tool card). The gear on a row sets its **name**, **icon**,
  **default model** and **worktrees**, or removes it.
- **Chat body** — live transcript: reasoning blocks, tool-call cards, permission
  and question cards, attachments, compaction summaries. Text streams in via
  `message.delta` events.
- **Composer** — chips for **agent / model**, **permissions** and **reasoning**
  (applied to the session), attachments (uploaded on send), a `/` command
  palette, steer / queue while a run is going, and live metrics (duration,
  cost, tok/s, context use).
- **Right panel** — files changed by the session (or the working tree) with a
  side-by-side diff, plus open-folder / VS Code actions.
- **Terminal drawer** — commands run on the server in the workspace folder.
- **Settings** — local toggles + server config, **Models** (providers, presets,
  custom endpoints, discovery), **Skills**, **MCP servers**, Appearance.

Only UI preferences live in the browser (`sofia.harness.v2` selection and
modes, `sofia.settings.v1` local settings, both in sessionStorage); everything
else is Sofia.Server state.

## Structure

```
src/
  main.ts                     App entry (createApp + styles)
  App.vue                     MantineProvider + backend gate + layout
  types.ts                    Server DTOs, view models, static option lists
  services/
    sofia.ts                  Connection, REST helper, uploads, SignalR events
    diff.ts                   Unified diff -> two-column rows
    worktrees.ts              /api/worktrees helpers
  composables/
    useHarness.ts             Server-backed workspace / chat store + events
    useSessionStatus.ts       Live status badges
    useSettings.ts            Local UI preferences + sessionStorage
  components/                 Sidebar, header, transcript, composer, panels,
                              settings, connect gate (BackendGate.vue)
  styles/theme.ts             Grey/brown palette + Mantine theme override
  styles/global.css           Sofia CSS tokens (--sofia-*) + base styles
```
## Enabling Vapor (later)

Vapor needs Vue 3.6 (currently RC) and is a **per-component opt-in**. Because
Mantine Vue is virtual-DOM, the root stays `createApp()` + `vaporInteropPlugin`
and only Sofia’s own SFCs would be compiled in vapor mode. When 3.6 is stable:

1. Bump `vue` to `3.6.x` and set `features: { vapor: true }` in `vite.config.ts`
   (already stubbed there).
2. In `src/main.ts`, switch to `createApp(App).use(vaporInteropPlugin).mount('#app')`.

Mantine components keep working as VDOM islands inside the vapor app.
