# Sofia Frontend â€” Initialization Notes

> Working knowledge for the `src/Sofia.UI/` web client. This is the living
> reference for anyone (human or agent) picking up UI work on Sofia.

## What this is

The web interface of the Sofia harness. It started as a visual shell on mock
data; it now talks only to `src/Sofia.Server` (ASP.NET, REST + SignalR) â€” see
`docs/server_api.md` for the contract and `docs/project_init.md` for the
server architecture. `src/services/sofia.ts` is the only backend client and
`src/types.ts` holds the shared shapes.

## Stack

- Vue 3.5 (`<script setup>`) + Vite + TypeScript
- Mantine Vue (`@mantine-vue/core`) â€” **virtual DOM only, Vue Vapor NOT enabled**
- No router, no Pinia â€” state lives in module-level singleton composables
- CSS: custom properties only (no Tailwind, no SCSS)

## Quick commands

```bash
cd src/Sofia.UI
npm install
npm run dev        # http://localhost:5183
npm run build
npm run preview
npm run typecheck  # vue-tsc --noEmit â€” MUST pass before claiming done
```

## Architecture at a glance

```
src/Sofia.UI/src/
â”œâ”€â”€ App.vue                    # shell: sidebar + main + right panel
â”œâ”€â”€ main.ts                    # bootstrap
â”œâ”€â”€ assets/logo.png            # brand mark
â”œâ”€â”€ assets/chat-wallpaper.svg  # seamless doodle tile painted behind the transcript
â”œâ”€â”€ components/
â”‚   â”œâ”€â”€ AppSidebar.vue         # workspace tree, chat groups, brand header + settings gear
â”‚   â”œâ”€â”€ ChatHeader.vue         # slim title bar, language picker, panel toggle
â”‚   â”œâ”€â”€ ChatComposer.vue       # frosted-glass composer: agent/model/perms/reasoning chips,
â”‚   â”‚                          #   "/" command palette, "@" subagent mentions; publishes
â”‚   â”‚                          #   --sofia-composer-height from its own ResizeObserver
â”‚   â”œâ”€â”€ ChatMessages.vue       # scrollable transcript (wallpaper canvas, reserves the
â”‚   â”‚                          #   composer height as bottom padding)
â”‚   â”‚                          #   messages plus every user message and the first reply, so older
â”‚   â”‚                          #   blocks collapse into one centred "(...)" where they were)
â”‚   â”œâ”€â”€ ChatMessageItem.vue    # renders one message: text, reasoning, tool cards,
â”‚   â”‚                          #   permission cards, question cards, error banners,
â”‚   â”‚                          #   provider retry notices, report cards, attachments
â”‚   â”œâ”€â”€ RightPanel.vue         # workspace file changes / diff viewer
â”‚   â”œâ”€â”€ BrowserPanel.vue       # live headless-browser view (canvas + input), see "Browser panel"
â”‚   â”œâ”€â”€ SettingsPanel.vue      # modal: General, Models, MCP, Appearance tabs
â”‚   â”œâ”€â”€ WorkspaceSettings.vue  # hover gear: icon, model, rename, remove
â”‚   â”œâ”€â”€ SofiaIcon.vue          # inline SVG icon set (add entries to PATHS record)
â”‚   â”œâ”€â”€ SofiaPopover.vue       # reusable chip+dropdown (model/perms/reasoning pickers)
â”‚   â”œâ”€â”€ AgentDropdown.vue      # agent picker: own chip+panel, PATCHes the session `agent`
â”‚   â””â”€â”€ DiffViewer.vue         # side-by-side diff
â”œâ”€â”€ composables/
â”‚   â”œâ”€â”€ useHarness.ts          # central store: workspaces, sessions, messages and
â”‚   â”‚                          #   metrics loaded from Sofia.Server, live SignalR
â”‚   â”‚                          #   events, sound triggers
â”‚   â”œâ”€â”€ useSessionStatus.ts    # sidebar/header badges from session.status events
â”‚   â”œâ”€â”€ useSettings.ts         # local UI settings (profile, language, appearance)
â”‚   â”œâ”€â”€ useShortcuts.ts        # keyboard shortcuts: defaults, user overrides, global listener
â”‚   â”œâ”€â”€ useAppearance.ts       # applies accent / font-size / compact-sidebar to document
â”‚   â””â”€â”€ useSound.ts            # Web Audio API sound effects (no external files)
â”œâ”€â”€ services/
â”‚   â”œâ”€â”€ sofia.ts               # backend connection: REST helper, SignalR hub, pairing
â”‚   â”œâ”€â”€ diff.ts                # unified diff â†’ DiffViewer rows
â”‚   â””â”€â”€ worktrees.ts           # git worktree endpoints
â”œâ”€â”€ types.ts                   # shared shapes (mirror docs/server_api.md) + constants
â”œâ”€â”€ i18n/index.ts              # lightweight i18n: en, pt, es, fr, zh, ja
â””â”€â”€ styles/
    â”œâ”€â”€ theme.ts               # Mantine theme override (grey canvas, brown accent)
    â””â”€â”€ global.css             # CSS custom properties (--sofia-*)
```

## Composable pattern

Every composable is a **module-level singleton**. Call `useHarness()` from any
component and you get the same reactive store â€” no provide/inject needed.

```ts
// In any component:
const { state, activeWorkspace, selectConversation, sendMessage } = useHarness()
```

## CSS conventions

All colors are custom properties on `:root` (set in `global.css`):

| Variable | Use |
|----------|-----|
| `--sofia-bg` | canvas background |
| `--sofia-surface` | sidebar / panels |
| `--sofia-surface-2` | cards / rows / inputs |
| `--sofia-surface-3` | hover states |
| `--sofia-border` / `--sofia-border-strong` | hairlines / borders |
| `--sofia-text` / `--sofia-text-muted` / `--sofia-text-faint` | text ramp |
| `--sofia-primary` / `--sofia-primary-strong` / `--sofia-primary-ink` | brown accent |
| `--sofia-primary-soft` / `--sofia-primary-soft-strong` | translucent brown wash |
| `--sofia-ok` / `--sofia-warn` / `--sofia-err` (+ `-soft` variants) | status colors |
| `--sofia-detail` | beige detail (reasoning blocks) |
| `--sofia-composer-height` | live height of the overlay composer, published by `ChatComposer` |

Use `color-mix(in srgb, var(--sofia-surface-2) 72%, transparent)` for
translucent surfaces. Use `var(--sofia-primary-soft)` for active/selected rows.

The composer is `position: absolute` over the bottom of `.messages`, so the
transcript must reserve room for it. Never hard-code that reservation:
`ChatComposer` measures its own footer with a `ResizeObserver` and writes
`--sofia-composer-height`, and `ChatMessages` pads with
`calc(var(--sofia-composer-height) + 1.25rem)` â€” the gap stays constant whether
the box is one line, multi-line or carrying an attachment strip.

The transcript canvas paints `assets/chat-wallpaper.svg` (a seamless doodle
tile, white line art at ~3â€“5% opacity) under a faint paper-grain data-URI at
0.03. Keep both that quiet â€” anything stronger makes the white transcript text
hard to read. The
tile is plain hand-editable SVG: motifs are `<use>` references to the symbols in
its `<defs>`, and every one of them sits fully inside the 400Ã—400 viewBox, which
is what keeps the repeat seamless â€” never move a motif across an edge.

Font: system stack. Monospace for code/branch names via
`var(--font-family-monospace)`.

## Sound effects

All sounds are **synthesised** via Web Audio API — no external audio files.
They live in `src/composables/useSound.ts`. Character: soft wood / marimba
(sine fundamental + quiet 3rd partial, ~18 ms attack, short exponential tail)
on one A minor pentatonic note set (A3 220 · C4 261.63 · E4 329.63 · G4 392 ·
A4 440 · C5 523.25 · E5 659.25) so overlapping sounds stay consonant.

| Key | Event | Character |
|-----|-------|-----------|
| `click` | Generic UI click / toggle | band-passed (1.5 kHz) noise tap, 22 ms, peak 0.045 — quietest voice |
| `select` | Conversation select / archive / restore / pin / delete | single A4 marimba tap + faint knock |
| `send` | Message sent | ascending dyad E4 → A4 |
| `done` | Agent finished a task (final reply) | dyad C5 over A4, 0.42 s tail |
| `permission` | Permission requested / question | bell dyad A4 + E5, 0.5 s tail, slight shimmer |
| `error` | Task error / connection failure | descending G4 → E4 → A3 (low note −8 cents) |

Signal chain (shared, created lazily per `AudioContext`):

```
voice(s) → bus input → low-pass 3.8 kHz (Q 0.5) → master gain (volume) → limiter → destination
           limiter = DynamicsCompressor: threshold −12 dB, knee 8, ratio 12, attack 2 ms, release 120 ms
```

Sounds fire on status transitions of the active conversation (`session.status`
events): `done` when a run goes idle, `permission` when it waits on an approval
or a question, `error` when it fails. Intermediate steps never play a sound.

Settings → General: "Sound effects" toggle, a 0–100 **Volume** slider
(`general.soundVolume`, default 60, mapped to master gain `(v/100)^1.6 * 0.9`
and ramped over 50 ms) and a **Preview sounds** button that plays all six keys
~420 ms apart through the normal bus. Both persist to `sessionStorage`
(`sofia.settings.v1`) and are disabled when effects are off.

Guards: 45 ms retrigger floor per key and a cap of 6 concurrent voices (extra
sounds are dropped). Lifecycle: no `AudioContext` is created while disabled; a
suspended context is resumed and the voice scheduled ~50 ms later so the first
sound is not lost. One module-level watcher syncs enabled + volume.

### Adding a new sound

1. Add the key to the `SoundKey` union type and to `SOUND_KEYS`
2. Define `const xSound: SoundDef = { play(ctx, out) { ... } }` using
   `note(ctx, out, { freq, at, dur, peak })` (and `knock()` if needed); pick
   pitches only from the pentatonic set and keep peaks ≤ ~0.13 (pre-master)
3. Add it to the `SOUND_DEFS` record
4. Call `playSound('x')` from any component (import `useSound`)
5. Check offline: render `playInto(offlineCtx, offlineCtx.destination, 'x')`
   with an `OfflineAudioContext` and inspect peak / duration
## TypeScript pitfalls

- **`Record<SoundKey, SoundDef>` inference**: Arrow functions in an inline
  object literal get `ctx: any` errors. Fix: define each entry as a separate
  `const xSound: SoundDef = { play(ctx, out) { ... } }` before assembling
  the record.
- **Watcher timing with nulled refs**: `isReplying` and
  `replyingConversationId` change in the same tick. Capture the conv ID in the
  watcher's `isReplying = true` branch, use it in the `false` branch.
- **Streaming deltas**: always mutate the message through the reactive array
  element (`conversation.messages[i]`), never a raw object captured before it
  was pushed, or the transcript will not re-render until a reload.

## i18n

`useI18n()` provides `t(key)` with fallback to English, then to the key itself.
Dictionaries for en/pt/es/fr/zh/ja live in `src/i18n/index.ts`. Add new keys to
every dictionary (or at least English + Portuguese).

## Settings panel

Tabs: **General** (local toggles + server config from `/api/config`),
**Models** (providers incl. auto-discovered local runtimes, models with
context windows, add provider from a preset or a custom OpenAI/Anthropic
endpoint), **Skills**, **MCP** (servers, add/connect/disconnect/remove),
**Shortcuts** (see below) and **Appearance**. Local UI settings persist under
`sofia.settings.v1`.

## Keyboard shortcuts

`composables/useShortcuts.ts` declares every action (`SHORTCUTS`: id, default
combo, group, icon). One capture-phase `keydown` listener on `window`
(`useShortcutListener()`, installed in `App.vue`) matches the combo and calls
`preventDefault` + `stopPropagation`, so browser bindings (Ctrl+L, Ctrl+P,
Ctrl+Oâ€¦) never fire. Components attach behaviour with
`useShortcut(id, handler)`; `SofiaPopover` and `AgentDropdown` expose `open()`
and are arrow/Enter driven while open. Combos are layout-independent
(`event.code` for letters, digits and punctuation), e.g. `Ctrl+Shift+N`.

Defaults: Ctrl+Shift+A agent, Ctrl+M model, Ctrl+B reasoning, Ctrl+P permission,
Ctrl+L focus composer, Ctrl+. stop reply, Ctrl+N new session (same
workspace/chat group),
Ctrl+Shift+E archive session, Alt+â†‘/â†“ previous/next session, Ctrl+O add workspace,
Ctrl+\ toggle changes panel, Ctrl+, settings, Ctrl+/ shortcuts tab.

Settings â†’ Shortcuts lists them and rebinds on click (Esc cancels, Backspace
unbinds, conflicts and modifier-less combos are rejected). Only overrides are
stored, in `localStorage` (`sofia.shortcuts.v1`). To add an action: append it
to `SHORTCUTS`, add `shortcut.<id>` to the i18n dictionaries and call
`useShortcut` where the behaviour lives.

**Browser limits:** Chrome/Edge/Firefox reserve Ctrl+N/T/W (and Shift
variants) in regular tabs â€” pages cannot intercept them there. They work when
Sofia runs as an installed app / app window; the Shortcuts tab flags such
combos with a warning and they can be rebound.

## Right panel

Toggled from the chat header. Shows the files the active session changed
(`/api/sessions/{id}/diff`, falling back to the git working tree) with a
side-by-side diff viewer. Both sources leave out what Git ignores (`.gitignore`,
global excludes), so the list matches what a commit could carry; reverting a
message still restores those files. Actions: open folder, open in VS Code, open
the native terminal, copy path.

## Browser panel

`BrowserPanel.vue`, toggled by the globe button in the chat header, shows the
headless browser of the open conversation (see `docs/server_api.md`, "Live
browser"). It opens a WebSocket only while visible, paints JPEG frames on a
`<canvas>` and forwards mouse, wheel, keyboard and paste to the server, mapping
pointer positions back to page coordinates through the letterboxed viewport.
The panel opens itself on a `browser.updated` event with `agent: true` for the
conversation on screen, shows a ring where the agent clicked, and locks input
while `agentBusy`. "Desktop" renders the page at 1280px wide, "Fit" at the
panel's own size; the panel can be dragged wider (width persisted in
`localStorage`).
