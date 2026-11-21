# Sofia.App — native Avalonia UI

`Sofia.App` is the shared Avalonia (AXAML) client introduced by plan 039. It reproduces the Vue UI's identity —
tokens, type scale, Lucide glyphs, wallpaper, sounds and motion — and is the foundation specs 040–043 build the
real sidebar, transcript, composer, settings and panels on. The Vue UI stays the reference until the browser head
lands (044).

## Project map

| Project | Target | Owns |
| --- | --- | --- |
| `src/Sofia.App` | `net10.0` class library | `App.axaml`, `Theme/`, `Controls/`, `Features/Sidebar/`, `Features/Transcript/`, `Shell/`, `Services/`, `Assets/`. References `Sofia.Client` + `Sofia.Contracts` only, so the WASM head can use it (no SQLite, Playwright, SoundFlow). |
| `src/Sofia.App.Desktop` | `net10.0` `WinExe` | `Program.cs` (flags, font options), `SoundFlowSoundPlayer`, `DesktopBackendResolver` (HTTP → in-process Core). References Core and `Sofia.Client.InProc`. |
| `src/Sofia.App.Browser` | `net10.0-browser1.0` | WASM boot smoke. Static host, HTTP backend and WebAudio sounds land in 044. |
| `src/Sofia.App.Tests` | `net10.0` | `Avalonia.Headless.XUnit` + `Avalonia.Skia`: token/motion/control tests and the screenshot + frame harness. |

Run the app (fixture data, no server):

```bash
dotnet run --project src/Sofia.App.Desktop -- --fixture
```

Without `--fixture` the desktop head probes `http://127.0.0.1:5199`; if nothing answers it takes the `~/.sofia`
lock and runs the engine in-process, and otherwise shows the connect gate.

The shell follows `App.vue`/`AppSidebar.vue`: brand header (logo, name, gear), the primary **New chat** and
secondary **New workspace** actions, the past-session search entry with its `Ctrl+K` badge (the local filter is a
scaffold placeholder; 040 wires the API-backed results panel), `Workspaces`/`Chats` section labels, and an empty
sidebar footer like the web. The workspace tree, status badges and conversation rows come from fixture data in
`--fixture` mode and from `HarnessStore` against a live backend. Plan 040 replaces the scaffold rows with
`Features/Sidebar/` (see below).

## Sidebar (plan 040)

`Features/Sidebar/` is the Vue `AppSidebar.vue` port: `SidebarView.axaml` + code-behind build every row,
`SidebarViewModel` owns the component-ref state (search query, add form, inline rename, open archive folder, open
settings menu, pending delete), `SidebarStore` is the slice over `ISofiaBackend` and `WorkspaceRowViewModel` /
`ConversationRowViewModel` are the row projections. `HarnessStore` stays the event cache (workspace tree, raw
statuses, models, coalesced deltas); the sidebar listens to its `Applied` event, applies its own rules and raises
`Changed` for one full rebuild.

Feature map (every row of the inventory is implemented; deferred items are in the deviations table):

| Area | Behaviour |
| --- | --- |
| Brand | 40px logo with the `0 0 0 3 rgba(169,116,79,.16)` ring, 17px/650 wordmark, 12px `local harness` tag, 32px gear (opens the settings dialog in 043). |
| Actions | `New chat` (primary) and `New workspace` (secondary), 2-column grid, click sounds. New workspace opens the folder picker on desktop and keeps the typed-path form as the browser-head fallback. |
| Quick prompt | Toggle + `Ctrl+K`, 180ms debounce, `GET /api/search` (`includeArchived`/`includeSubagents`, limit 30), highlighted spans, ↑/↓/Enter/Escape, outside click closes. Enter opens the session and stores the scroll target for 041. |
| Sections | `Workspaces` then `Chats` (split rule + new-group button), expansion persisted through `PATCH /api/workspaces/{id}` (`expanded`). An empty account gets the auto `Quick chats` group. |
| Workspace row | Caret, status badge **replacing** the icon (18px tinted), name 14px/550, count pill; the count cross-fades into `+`/gear on hover. Collapsed working workspaces get the 1.1s sweep. |
| Conversation row | Pinned first, then `Order`, then recency (server order); title truncation; subagents nested by depth × 14px and shown only while running/active; status badge 16px; 1.4s sweep while working; hover pill with pin/unpin, archive, delete. |
| Inline rename | Clicking the already-open conversation swaps the title for a TextBox; Enter/blur commits, Escape cancels, edits persist with the web's 400ms debounce. |
| Archive folder | `Archived` sub-folder with count, nested rows, restore and delete actions; deleting a conversation or the archived list goes through the `z2100` confirmation modal. |
| Delete modal | Full-window `rgba(0,0,0,.45)` scrim, `min(400px,100%)` panel, 14px radius, 16px padding, Cancel/Delete, focus on the destructive button. |
| Reorder | In-window pointer drag (4px threshold, `0 ±2px` primary drop indicator); workspaces only inside their section, conversations only inside their pinned group; optimistic order then `PUT /api/workspaces/order` / `PUT …/sessions/order`. |
| Workspace settings | 264px in-window menu: live name field (400ms debounce), the **24** `WORKSPACE_ICON_CHOICES` icons, model list from `GET /api/models` (enabled only, current pick kept), worktrees and remove. |
| Worktrees | `GET/POST/DELETE /api/worktrees`; list with branch tooltip, switch (opens/creates the workspace at that directory), create field, dirty-tree `force` retry, remove also drops the stale workspace. |
| Shortcuts | `search` Ctrl+K, `workspace.add` Ctrl+O, `session.new` Ctrl+N, `session.archive` Ctrl+Shift+E, `session.prev/next` Alt+↑/↓ and `panel.toggle` Ctrl+\ via `Services/Shortcuts.cs`; click/select sounds follow the web's mapping. |

Geometry (web px at the 16px base):

| Element | Values |
| --- | --- |
| Column | 288 normal / 236 compact, `BrushSurface`, right 1px `BrushBorder`. |
| Brand | padding 16/10.4, 40×40 r12 logo, 12px gap, 17px/650 name, 12px tag, 32×32 r9 gear. |
| Actions | `12,12,12,8`, 2 columns, 8px gap, 9px radius, 7.2px vertical padding, 13px/550 text. |
| Search | field `12,0,12,4`; overlay `left/right 12`, `top = field bottom + 4`, r10, surface-2, `0 12 32` shadow, results `max-height: min(50vh,420px)`. |
| Section label | 11px/600, `.08em`, uppercase, faint; split variant `border-top` + 12px margin. |
| Workspace row | padding 6.4/8 (compact 3.2), r8, name 14px/550, count 11px on surface-3, actions 22×22. |
| Conversation rows | container `margin 2 0 4 22`, `padding-left 8`, 1px left border; row padding 5.6/8 (compact 3.2), r8, title 13px, status 16px, actions pill r999 surface-3 `0 1 3 rgba(0,0,0,.25)`, buttons 20×20, trail `right 4`. |
| Rings | 1.5px conic ring over a primary-soft row; workspace `transparent 0–58% → primary 78% → #fff 90% → detail 96% → transparent`, 1.1s; conversation `transparent → primary 60° → detail 120° → transparent 180°`, 1.4s; both linear/infinite and paused off-screen. Row padding shrinks by the ring width so the ring never changes row height. |

Seams owned by later specs:

- `SidebarStore.OpenSessionRequested` → `ShellView.PendingScrollMessageId` (041 transcript) — search hits and archive
  rows open through it.
- `SidebarView.ComposerFocusRequested` (New chat / Ctrl+N → 041 composer), `ShellSettingsRequested` (gear / Ctrl+,
  → 043 settings), `PanelToggleRequested` (Ctrl+\ → the shell's right panel).
- `WorkingRowBorder` and `MotionService` stay the only animation owners; the hover reveal is a clock-driven
  participant (`RowReveal`) so the frame harness can stop-motion it.

Deviations from the web (deliberate):

| Web | App |
| --- | --- |
| HTML5 drag-and-drop with OS drag images | In-window pointer drag (same 4px threshold, drop indicator and commit rules); Linux has no OS drag image and the internal reorder does not need one. |
| Hover trail `.12s` CSS transition | Clock-driven `.15s` fade (motion contract #3); the frame harness seeks 0/75/150ms and the 50% stop is the same cubic-bezier value. |
| Teleported 400px delete modal and fixed settings menu | Both live on the shell's in-window overlay ladder (`z2100` / `z60`), never as OS popups. |
| Search snippet `<mark>` | TextBlock runs with a primary-soft `Background` (Avalonia `TextElement.Background`); inline wrapping is identical. |
| 66 workspace icon keys | 24 keys — the actual `WORKSPACE_ICON_CHOICES` list in `types.ts`; the spec's 66 was a miscount. |
| All sidebar labels localized | Primary labels reuse `Localization`; the secondary copy (tooltips, "Archived", worktree notes) is English until 043 lands the full key set. |
| Archived folder "delete all" action | Only single-conversation delete (with the same confirmation); the bulk variant is deferred. |
| Virtualised long lists (risk noted in 040) | One measured pass per rebuild; rows are constant height, so a `VirtualizingStackPanel` can slot in without moving geometry. Not needed for the current tree sizes. |

Evidence (all in `.specs/spec_assets/`): `040-web-1080p/2k/4k.png` (Vue reference captured with Playwright because the
harness browser could not launch), `040-mixed|empty|search-1080p/2k/4k.png`, `040-settings.png`, `040-delete.png`,
`040-motion-conversation-frame-*.png` (1.4s), `040-motion-workspace-frame-*.png` (1.1s) and
`040-hover-frame-0/75/150.png`.

## Transcript (plan 041)

`Features/Transcript/` is the Vue `ChatMessages.vue` + `ChatMessageItem.vue` + `MarkdownText.vue` port:
`TranscriptView.axaml` (+ code-behind) is the scroll host and builds one `MessageRow` per transcript entry;
`TranscriptViewModel` owns the loaded messages, the 70-message window, the queued follow-ups, the session status and
the composer-height padding; `MessageViewModel` is the row projection (text, reasoning, tool cards, the whole card
family, attachments, cache warning, action state); `ToolCardViewModel` + `UnifiedDiff` render tool calls and their
diffs; `CardViewModels/` holds one small view model per card (reasoning, summary, knowledge, context, retry, error,
compaction, permission, question + question set); `Markdown/` holds the parser, the block model, the renderer and the
prose control; `TranscriptActions` is the `ISofiaBackend` mutation surface; `AttachmentLoader`, `Lightbox`,
`QuestionCarousel`, `CarouselStage` and `HoverReveal` are the media/overlay/animation pieces.

Feature map (every row of the inventory is implemented; see the deviations table for the visual gaps):

| Area | Behaviour |
| --- | --- |
| Scroll host | 1232px column centred in the main area, padding `1.25rem 1.5rem` with the bottom `composer height + 1.25rem`; the shell publishes the measured composer height (`SetComposerHeight`). Auto-follow only while parked at the bottom (`ScrollViewer` distance ≤ 4px), released on an upward scroll that is not a truncation clamp; a new user/pending row re-engages it. |
| Window | Above 70 messages the newest 70 stay rendered, plus every user prompt and the first reply, with a centred `(...)` gap (`Earlier messages hidden to keep the chat light ({count})` tooltip). |
| Scroll seam | `TranscriptView.ScrollToMessage(id)` — the sidebar's search hit and `ShellView.PendingScrollMessageId` use it; a subagent tool link raises `SessionOpenRequested`. |
| Row | `flex` geometry: gap 12, padding 12/0, 32px avatar r10 (borderStrong + primary-soft + `logo-31`), meta 12px (author 600, model/time faint), user rows reversed with the meta group right-aligned, bubble `#242428` border r14 padding `.6rem .85rem` up to `min(952px, 82%)`, assistant prose 104ch at 15px/1.6. |
| Markdown | Markdig (GFM + breaks): headings 1.25/1.15/1.05em at 650, `.7em` collapsed block gap, lists `1.5em`, inline code mono `.85em` on surface-2, code blocks 13px/1.55 on the canvas colour with horizontal scroll, muted 3px blockquote, bordered table cells (header surface-2), 1px `hr`, raw HTML as literal text, `$…$` literal, images as alt text, links http(s)/mailto only. The parsed block model is cached (LRU, 96 entries) and a streamed row re-renders at most once per 80ms. |
| Streaming | `message.delta` is coalesced per `(messageId, field)` by `HarnessStore` (33ms flush) and appended to text or reasoning; `message.updated` replaces the row while keeping the user's fold/pick state. Reasoning is live-open while the model is still thinking and folds when the answer starts. |
| Tool cards | Header fold, mono name, status pill (Success/Running/Error), duration, summary, subagent link (+ model + blocking/background), input/output/error blocks (`max-height:200px`, 10px uppercase labels), image gallery, and the +green/−red diff of `edit`/`write`/`apply_patch` (`UnifiedDiff`). |
| Cards | Reasoning (beige, `Reasoning · mode`), compaction notice (pulse, expandable summary), conversation summary, knowledge (view/hide/delete via `DELETE /api/knowledge/{id}`), context updates (collapsed), retry notice (1.8s spin), error banner, prompt-cache warning with the diagnosis prompt. |
| Permission | Warn card until resolved (`POST /api/sessions/{id}/permissions/{requestId}`, once/always/reject from the labels), then a neutral card with the chosen decision. |
| Questions | Single card (single/multi + free text) and the multi-question carousel (one page per question + review, nothing sent before the review confirm; `POST /api/sessions/{id}/questions/{formId}`); picks live in a draft store keyed by form id. |
| Attachments | 240×180 r10 thumbnails (and file chips), click opens the z200 lightbox (`rgba(0,0,0,.88)`, 42px circular close, download pill) on the shell overlay ladder; media loads through `ISofiaBackend.AttachmentUrl` (token included). |
| Actions | Copy source, Branch out, Retry (failed rows, confirmation modal) or Revert (armed on the first click, 2.6s window), revealed on row hover. |

Geometry (web px, rem resolved at the current root size through `TranscriptUi.Rem`):

| Element | Values |
| --- | --- |
| Row | padding 12/0, gap 12, avatar 32 r10, meta 12 with `.3rem` bottom gap, card radius 10, card padding 8/10.4, card gap 8. |
| Prose | 15/1.6, 104ch (`15px × .5 × 104 ≈ 780`), user bubble padding 9.6/13.6, radius 14, `min(952, 82%)`. |
| Cards | 74ch cap (`≈555`), reasoning padding 8.8/12, notice/blocking labels 10px/600/`.06em`, tool body blocks `max-height 200`, diff `max-height 260` at `.78rem`. |
| Permission/question | padding 9.6/11.2, radius 10, option buttons padding 4.8/9.6 r8, primary fill for the chosen option, review chips `> answer`. |
| Lightbox | full viewport, image inset 48, close 42 r21 at 20/20, download pill bottom 24. |

Seams owned by later specs:

- `TranscriptActions.AnswerQuestionAsync`/`ReplyPermissionAsync` are the only question/permission writes; 042's composer
  uses the same `ISofiaBackend` methods.
- Revert restores a user prompt into the composer on the web; here the composer hook lands with 042 (the transcript
  reloads through the server events in the meantime).
- The retry confirmation lives on the transcript's overlay host; 043's settings/modal ladder must keep z2100 for it.

Deviations from the web (deliberate, evidence in `.specs/spec_assets/041-*`):

| Web | App |
| --- | --- |
| Inline `code` chips with 1px border + 5px radius | Mono `.85em` run with the surface-2 background only: Avalonia mis-measures a wrapped line that hosts an `InlineUIContainer` (the first capture showed the overflow overlapping the next block). Links stay runs with the accent underline and are opened by a character-range hit test on the paragraph. |
| KaTeX math | `$…$` literal text (same fallback the web uses for invalid math). |
| Relative/`//host` links | Not navigable: a desktop has no base URL, so only absolute http(s)/mailto URLs become links (the web keeps relative refs). |
| Arbitrary text selection in the prose | Message-level Copy action (the minimum the spec allows): inline link hit-testing replaces a selectable text block. |
| Report card (`message.report`) | Not reachable: `report` exists only in the Vue types; the server never emits it. Deferred with the client-only card. |
| Web `message.delta` applied per token | Coalesced per `(message, field)` on a 33ms flush (the same batching the store already did for text). |
| `(...)` gap is a non-interactive note | Kept non-interactive (the web has no expander; the tooltip carries the count). |
| Solid/dashed inline borders on question inputs | Avalonia `TextBox` draws its own frame; the free-text field uses a solid borderStrong frame instead of the web's dashed rest state. |

Evidence (`.specs/spec_assets/`): `041-web-1080p/2k/4k.png` and `041-web-top-1080p/2k/4k.png` (Vue reference, live
session), `041-idle|streaming|permission|questions|diff|lightbox-1080p/2k/4k.png` (app, three sizes),
`041-markdown|compaction|retry|queued|waiting.png` (extra states), `041-fold.frames/`, `041-hover.frames/`,
`041-pulse.frames/`, `041-retry-spin.frames/`, `041-carousel.frames/` and `041-spinner.frames/` (stop-motion at the
motion-contract timestamps).

## Theme and tokens

| File | Contents |
| --- | --- |
| `Theme/Tokens.axaml` | Colours and brushes: `Color*` + `Brush*` for surfaces, text ramp, accent, detail and status. Values are copied from `src/Sofia.UI/src/styles/global.css` (1 CSS px = 1 DIP). |
| `Theme/Typography.axaml` | Font families (`FontFamilyUi`, `FontFamilyMono`), the `*Base` sizes in px, and the unscaled keys components use (`FontSizeXs…Hero`, `PadRow`, `PadBrand`). |
| `Theme/Shadows.axaml` | `ShadowSm`, `ShadowMenu`, `ShadowModal`, `ShadowPanel`, `ShadowHero`, `ShadowOverlay`. |
| `Theme/Motion.axaml` | Every duration in milliseconds (`MsHover`, `MsSteam`, …) and the CSS easings (`EaseStandard` = `cubic-bezier(.25,.1,.25,1)`, `EaseInOut`, `EaseLinear`). |
| `Theme/Icons.axaml` | 65 generated Lucide `StreamGeometry` resources, `Icon.<semantic name>`. |
| `Theme/ControlThemes.axaml` | `ActionButton`, `PrimaryButton`, `IconButton`, `RowButton`, `SendButton`, `ChipButton`. |
| `Theme/Controls.axaml` | App-wide styles: text classes (`.eyebrow`, `.mono`, `.truncate`, `.muted`, `.faint`, `.heading`), `.input` (surface-2 well, primary focus ring with the 3px soft halo), `.sidebar-row` (+`.active`), `.hairline`, `.card`, `.composer-band`, scrollbars (10px track, 3px inset thumb, border-strong → text-faint on hover). |

**Never hardcode a size, colour or duration.** Colours come from `Brush*`/`Color*`, type and rem-based spacing
from the unscaled keys, and times from `Motion.axaml` (in AXAML via
`Duration="{x:Static theme:MotionTokens.Hover}"`, in code via `MotionTokens`). `MotionTests.No_axaml_file_hardcodes_a_duration`
fails the build when an AXAML file gains a literal duration.

`ThemeService` (`Theme/ThemeService.cs`) is the runtime switch: it rewrites the accent brushes/colours, republishes
the scaled type tokens and the sidebar width. Components must use `DynamicResource` so a change repaints live:

- accent presets `brown` / `beige` / `slate` / `custom`, derivation in `AccentPalette` (`strong` = channel-wise
  lerp to white by .12, `ink` = `#171719` when luminance > .55 else white, softs at .16/.26). The CSS fallback
  `--sofia-primary-strong: #bc8a5e` is overwritten at runtime by the same derivation, exactly like
  `useAppearance.ts`.
- font size `sm` / `md` / `lg` → root scale .875 / 1 / 1.125 (14/16/18px).
- compact sidebar → 288px ↔ 236px, row padding 6.4 ↔ 3.2, brand padding 16 ↔ 10.4.

Tokens seeded by `ThemeService.ApplyAll()` are only defaults in AXAML; call it after changing a setting and
`ShellView.RefreshAppearance()` to rebuild chrome that measured itself.

## Icons

- `SofiaIcon` (`Controls/SofiaIcon.cs`) draws the glyph itself (`Render`) from the 24x24 viewBox scaled to
  `Size` (default 18) with round caps/joins and `StrokeWidth` 1.75, so stroke weight matches the SVG at any size.
- The web prop `name` maps to **`Glyph`**: Avalonia reserves `Name` for `x:Name`.
- `Glyph="loader"` hosts a `CoffeeSpinner`; names the web map lacks (e.g. `circle`) draw nothing — parity.
- Geometry is generated from the Lucide data installed for the Vue UI, so both UIs use the same glyphs:

```bash
node tools/app/gen-icons.mjs   # lucide-vue-next 1.0.0 -> src/Sofia.App/Theme/Icons.axaml
```

## Controls

| Control | Behaviour |
| --- | --- |
| `CoffeeSpinner` | Mug with 3 steam paths (dash `6 10`, 2.2s, delays 0/.45/.9s), bean (1.8s, 360° reached at 45%), robot (8-keyframe shake, .5s); `< 16px` switches mug → bean; `Paused` freezes the steam at .55 with no dash. |
| `WorkingRowBorder` | 1.5px conic-gradient ring sweeping 0→360° (workspace 1.1s / conversation 1.4s) over a primary-soft row; the ring is its own visual and stops when the row leaves the viewport. |
| `CollapsiblePanel` | The web's `grid-rows: 1fr ↔ 0fr`: measured `MaxHeight` animation, .2s ease-in-out, content stays mounted. |
| `SofiaPopover` | In-window menu surface (surface-2, 12px radius, menu shadow) for the overlay ladder — never an OS popup. |
| `ToggleSwitch` | 34×20 track, 16px knob, .15s slide. |
| `SofiaMeter` | Segmented bar (composer context meter): primary → warn at 75% → err at 90%. |

## Motion

`MotionService` owns the one clock every animation reads (`NowMs`), a single 60Hz `DispatcherTimer` in the app and
a manual clock (`EnableManualClock` / `Advance` / `Seek`) for the harness. Controls implement
`IMotionParticipant` and recompute their state from `nowMs`, which is what makes stop-motion captures exact.

| # | Animation | Mechanism | Timing |
| --- | --- | --- | --- |
| 1 | Hover/focus/pressed transitions | `BrushTransition` on control themes/styles | .12s `ease` |
| 2 | Send pill pressed | `TransformOperationsTransition` (translateY 1px) | .08s |
| 3/4 | Hover-action / message-action reveal | `DoubleTransition` (opacity) — consumed by 040/041 | .15s |
| 5 | Toggle knob | clock-driven margin | .15s |
| 6/7 | Working border sweep | `ConicGradientBrush.Angle` animated from the clock | 1.1s / 1.4s linear, infinite |
| 8 | Icon spin | clock-driven rotate | .9s linear, infinite |
| 9 | Panel/settings spinners | same mechanism as #8 | .9s / .7s |
| 10 | Coffee steam | keyframed dash offset/opacity/translate | 2.2s ease-in-out, infinite |
| 11 | Coffee bean | angle reaches 360° at 45% then holds | 1.8s ease-in-out |
| 12 | Coffee robot | 8 keyframes translate+rotate | .5s linear |
| 13 | Coffee paused | no animation, no dash, .55 opacity | static |
| 14 | Collapsible fold | measured `MaxHeight` | .2s ease-in-out |
| 15 | Compaction pulse | opacity 1→.55→1 — consumed by 041 | 1.4s ease-in-out |
| 16 | Retry icon spin | as #8 | 1.8s linear |
| 17 | Question carousel | translateX ±32px + opacity — consumed by 041 | .22s ease |
| 18 | Diff modal enter | opacity + scale(.98→1) — consumed by 041 | .16s ease |
| 19 | Right panel | width 0→300, translateX 40→0, opacity 0→1 | .24s / .24s / .18s ease |

**Reduced motion** mirrors the web's three exceptions only: steam runs at 6s, the retry icon does not spin and the
question carousel does not slide. `MotionService.ReducedMotion` comes from the OS
(`ReducedMotionDetector`: Windows `SPI_GETCLIENTAREAANIMATION`, macOS `defaults read com.apple.universalAccess
reduceMotion`, Linux `gsettings … enable-animations`), failing open.

## Sounds

`useSound.ts` is not re-implemented as an oscillator: the six voices are pre-rendered to WAV with the same
parameters (sine fundamental + 3rd partial at .12, 18ms attack, exponential decay to .0001, lowpass 3.8kHz Q .5,
compressor threshold −12dB / knee 8 / ratio 12 / attack 2ms / release 120ms). The master gain for the default volume
(`(60/100)^1.6 * .9 ≈ .3975`) is baked in, so playback scales by the ratio of the current curve to that default.

```bash
dotnet run tools/app/gen-sounds.cs   # writes src/Sofia.App/Assets/Sounds/*.wav
```

`SoundService` holds the switches: `Enabled`, `Volume` (0–100, default 60), the 45ms retrigger guard, six
simultaneous voices max, the `PreviewAsync` cycle (420ms) and the trigger mapping the shell uses (`click` on
buttons, `select` on picking/opening, `send`/`done`/`permission`/`error` from run state). Heads install an
`ISoundPlayer`: `SoundFlowSoundPlayer` on desktop (lazy device, silent on failure), WebAudio in 044. Assets are
embedded as `avares://Sofia.App/Assets/Sounds/<key>.wav`.

## Assets and wallpaper

- `Assets/Logos/logo-42.png` (sidebar 40×40 r12), `logo-128.png` (empty state 88×88 r24), `logo-31.png` (avatar).
- `Assets/Wallpaper/chat-wallpaper.png` — the 400×400 doodle tile rasterised from `chat-wallpaper.svg`.
- `Assets/Wallpaper/grain.png` — 140×140 procedural stand-in for the SVG `feTurbulence` grain at 2% opacity.
- The transcript paints three layers: canvas colour, wallpaper tile, grain tile (`TileMode=Tile`, `Stretch=None`).

Regenerate with `dotnet run tools/app/gen-assets.cs` (Svg.Skia rasterises the SVG; the grain is generated
in C#).

## Screenshot and frame harness

The desktop head renders deterministic fixture data (`Fixture`), so screenshots never depend on a server:

```bash
# one size
dotnet run --project src/Sofia.App.Desktop -- --fixture --screenshot app.png \
  --width 1920 --height 1080 --scale 1 --out-dir .specs/spec_assets

# the three epic sizes (1080p / 2k / 4k at scale 1 / 1.333 / 2)
dotnet run --project src/Sofia.App.Desktop -- --fixture --screenshot 039 \
  --all-sizes --out-dir .specs/spec_assets

# stop-motion frames at manual-clock readings (0/275/550/825/1100ms) plus appearance variants
dotnet run --project src/Sofia.App.Desktop -- --fixture --screenshot 039-frames \
  --frames 0,275,550,825,1100 --right-panel --accent beige --font-size lg --reduced-motion \
  --out-dir .specs/spec_assets
```

Flags: `--fixture`, `--screenshot <file>`, `--out-dir`, `--width`, `--height`, `--scale`, `--all-sizes`,
`--frames <ms,ms,…>`, `--right-panel`, `--compact-sidebar`, `--accent <preset>`, `--font-size <sm|md|lg>`,
`--reduced-motion`.

`ScreenshotHarness` (`Services/Screenshots.cs`) resizes the window, lays it out, renders it with
`RenderTargetBitmap` at `width*scale` device pixels and, in frame mode, seeks the manual clock between captures
(`frame-<ms>.png`). The headless tests set `ScreenshotHarness.FrameHook` to
`AvaloniaHeadlessPlatform.ForceRenderTimerTick()` and write their evidence to `.specs/spec_assets/039-*.png`:

```bash
dotnet test src/Sofia.App.Tests
```

`039-app-1080p.png` / `-2k` / `-4k` are the size gate, `039-motion-frame-*.png` the working-border and spinner
sequence, `039-panel-frame-*.png` the right-panel opening, and `039-app-accent-beige-font-lg.png` /
`039-app-compact-font-sm.png` the appearance switches. Specs 040–043 reuse the same harness for their parity
evidence.

## Deviations from the web (documented, deliberate)

| Web | App |
| --- | --- |
| `backdrop-filter: blur(14px)` behind the composer | translucent `BrushComposer` (canvas at 66%) + 1px top hairline; no blur (no Avalonia equivalent). |
| `mask-composite` ring for the working border | `ConicGradientBrush` on an outer `Border` with 1.5px padding over an opaque inner panel. |
| `::-webkit-scrollbar` 10px thumb with 3px inset | custom `ScrollBar` template with the same colours and a 3px margin. |
| SVG `feTurbulence` paper grain | procedural 140×140 noise tile with the same 2% weight. |
| CSS `grid-rows: 1fr ↔ 0fr` | `CollapsiblePanel` measured `MaxHeight` animation (content stays mounted). |
| KaTeX math | literal text, the same fallback the web uses for invalid math (transcript work in 041). |
| Icon prop `name` | `SofiaIcon.Glyph` (Avalonia reserves `Name`). |
| `--sofia-primary-strong: #bc8a5e` fallback | runtime derivation `#b38564` (channel-wise lerp by .12), exactly what `useAppearance.ts` writes. |

## Verification

```bash
dotnet build Sofia.slnx
dotnet test src/Sofia.App.Tests      # tokens, controls, motion, store, sounds, screenshots
dotnet test Sofia.slnx               # everything else stays green
```
