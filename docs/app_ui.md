# Sofia.App — native Avalonia UI

`Sofia.App` is the shared Avalonia (AXAML) client introduced by plan 039. It reproduces the Vue UI's identity —
tokens, type scale, Lucide glyphs, wallpaper, sounds and motion — and is the foundation specs 040–043 build the
real sidebar, transcript, composer, settings and panels on. The Vue UI stays the reference until the browser head
lands (044).

## Project map

| Project | Target | Owns |
| --- | --- | --- |
| `src/Sofia.App` | `net10.0` class library | `App.axaml`, `Theme/`, `Controls/`, `Shell/`, `Services/`, `Assets/`. References `Sofia.Client` + `Sofia.Contracts` only, so the WASM head can use it (no SQLite, Playwright, SoundFlow). |
| `src/Sofia.App.Desktop` | `net10.0` `WinExe` | `Program.cs` (flags, font options), `SoundFlowSoundPlayer`, `DesktopBackendResolver` (HTTP → in-process Core). References Core and `Sofia.Client.InProc`. |
| `src/Sofia.App.Browser` | `net10.0-browser1.0` | WASM boot smoke. Static host, HTTP backend and WebAudio sounds land in 044. |
| `src/Sofia.App.Tests` | `net10.0` | `Avalonia.Headless.XUnit` + `Avalonia.Skia`: token/motion/control tests and the screenshot + frame harness. |

Run the app (fixture data, no server):

```bash
dotnet run --project src/Sofia.App.Desktop -- --fixture
```

Without `--fixture` the desktop head probes `http://127.0.0.1:5199`; if nothing answers it takes the `~/.sofia`
lock and runs the engine in-process, and otherwise shows the connect gate.

## Theme and tokens

| File | Contents |
| --- | --- |
| `Theme/Tokens.axaml` | Colours and brushes: `Color*` + `Brush*` for surfaces, text ramp, accent, detail and status. Values are copied from `src/Sofia.UI/src/styles/global.css` (1 CSS px = 1 DIP). |
| `Theme/Typography.axaml` | Font families (`FontFamilyUi`, `FontFamilyMono`), the `*Base` sizes in px, and the unscaled keys components use (`FontSizeXs…Hero`, `PadRow`, `PadBrand`). |
| `Theme/Shadows.axaml` | `ShadowSm`, `ShadowMenu`, `ShadowModal`, `ShadowPanel`, `ShadowHero`, `ShadowOverlay`. |
| `Theme/Motion.axaml` | Every duration in milliseconds (`MsHover`, `MsSteam`, …) and the CSS easings (`EaseStandard` = `cubic-bezier(.25,.1,.25,1)`, `EaseInOut`, `EaseLinear`). |
| `Theme/Icons.axaml` | 65 generated Lucide `StreamGeometry` resources, `Icon.<semantic name>`. |
| `Theme/ControlThemes.axaml` | `ActionButton`, `IconButton`, `RowButton`, `SendButton`, `ChipButton`. |
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
