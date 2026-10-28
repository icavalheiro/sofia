import { reactive, watch } from 'vue'

import { useSettings } from '@/composables/useSettings'

// ---------------------------------------------------------------------------
// UI sound effects via Web Audio API — no external files. Soft wood / marimba
// voices (sine partials, short tail) on an A minor pentatonic note set, all
// routed through one shared bus:
//
//   voice(s) -> bus input -> low-pass 3.8 kHz -> master gain (volume)
//            -> limiter (DynamicsCompressor) -> destination
// ---------------------------------------------------------------------------

export type SoundKey =
  | 'send'
  | 'select'
  | 'done'
  | 'error'
  | 'permission'
  | 'click'

interface SoundDef {
  play: (ctx: BaseAudioContext, out: AudioNode) => void
}

export const SOUND_KEYS: SoundKey[] = ['click', 'select', 'send', 'done', 'permission', 'error']

// --- note set (A minor pentatonic) -------------------------------------------

const A3 = 220
const E4 = 329.63
const G4 = 392
const A4 = 440
const C5 = 523.25
const E5 = 659.25

const SILENT = 0.0001
const MAX_VOICES = 6
const RETRIGGER_MS = 45

// --- voice tracking ------------------------------------------------------------

let activeVoices = 0

function track(src: AudioScheduledSourceNode, durSec: number): void {
  activeVoices += 1
  let released = false
  const release = () => {
    if (released) return
    released = true
    activeVoices = Math.max(0, activeVoices - 1)
  }
  src.addEventListener('ended', release)
  // `ended` can be skipped (suspended/throttled context); never leak a voice slot.
  setTimeout(release, (durSec + 0.3) * 1000)
}

// --- voice helpers -------------------------------------------------------------

interface NoteOpts {
  freq: number
  /** Start offset in seconds from now. */
  at?: number
  /** Tail duration in seconds. */
  dur: number
  peak: number
  /** Detune in cents. */
  cents?: number
}

/** Marimba-like note: sine fundamental + quiet 3rd partial, soft attack, long tail. */
function note(ctx: BaseAudioContext, out: AudioNode, o: NoteOpts): void {
  const t = ctx.currentTime + (o.at ?? 0)
  const attack = 0.018
  const gain = ctx.createGain()
  gain.gain.setValueAtTime(0, t)
  gain.gain.linearRampToValueAtTime(o.peak, t + attack)
  gain.gain.exponentialRampToValueAtTime(SILENT, t + o.dur)
  gain.gain.linearRampToValueAtTime(0, t + o.dur + 0.02)
  gain.connect(out)

  const partials: [number, number][] = [[1, 1], [3, 0.12]]
  partials.forEach(([mult, amp], i) => {
    const osc = ctx.createOscillator()
    osc.type = 'sine'
    osc.frequency.setValueAtTime(o.freq * mult, t)
    if (o.cents) osc.detune.setValueAtTime(o.cents, t)
    if (amp === 1) {
      osc.connect(gain)
    } else {
      const g = ctx.createGain()
      g.gain.value = amp
      osc.connect(g)
      g.connect(gain)
    }
    osc.start(t)
    osc.stop(t + o.dur + 0.03)
    if (i === 0) track(osc, (o.at ?? 0) + o.dur)
  })
}

/** Short band-passed noise burst (mallet knock / click tap). */
function knock(
  ctx: BaseAudioContext,
  out: AudioNode,
  peak: number,
  dur: number,
  freq = 1500,
  q = 0.9,
): void {
  const t = ctx.currentTime
  const size = Math.max(1, Math.floor(ctx.sampleRate * dur))
  const buffer = ctx.createBuffer(1, size, ctx.sampleRate)
  const data = buffer.getChannelData(0)
  for (let i = 0; i < size; i++) {
    data[i] = (Math.random() * 2 - 1) * (1 - i / size)
  }
  const src = ctx.createBufferSource()
  src.buffer = buffer
  const filter = ctx.createBiquadFilter()
  filter.type = 'bandpass'
  filter.frequency.value = freq
  filter.Q.value = q
  const gain = ctx.createGain()
  gain.gain.setValueAtTime(peak, t)
  gain.gain.linearRampToValueAtTime(0, t + dur)
  src.connect(filter)
  filter.connect(gain)
  gain.connect(out)
  src.start(t)
  track(src, dur)
}

// --- sound definitions -----------------------------------------------------------

const clickSound: SoundDef = {
  play(ctx, out) {
    knock(ctx, out, 0.045, 0.022)
  },
}

const selectSound: SoundDef = {
  play(ctx, out) {
    note(ctx, out, { freq: A4, dur: 0.16, peak: 0.13 })
    knock(ctx, out, 0.03, 0.02)
  },
}

const sendSound: SoundDef = {
  play(ctx, out) {
    note(ctx, out, { freq: E4, dur: 0.18, peak: 0.11 })
    note(ctx, out, { freq: A4, at: 0.055, dur: 0.18, peak: 0.11 })
  },
}

const doneSound: SoundDef = {
  play(ctx, out) {
    note(ctx, out, { freq: A4, dur: 0.42, peak: 0.09 })
    note(ctx, out, { freq: C5, at: 0.04, dur: 0.42, peak: 0.11 })
  },
}

const errorSound: SoundDef = {
  play(ctx, out) {
    note(ctx, out, { freq: G4, dur: 0.26, peak: 0.12 })
    note(ctx, out, { freq: E4, at: 0.09, dur: 0.26, peak: 0.12 })
    note(ctx, out, { freq: A3, at: 0.18, dur: 0.26, peak: 0.12, cents: -8 })
  },
}

const permissionSound: SoundDef = {
  play(ctx, out) {
    note(ctx, out, { freq: A4, dur: 0.5, peak: 0.1 })
    note(ctx, out, { freq: E5, dur: 0.5, peak: 0.07, cents: 6 })
  },
}

const SOUND_DEFS: Record<SoundKey, SoundDef> = {
  send: sendSound,
  select: selectSound,
  done: doneSound,
  error: errorSound,
  permission: permissionSound,
  click: clickSound,
}

/** Plays one sound into any context/node (no dependency on the module bus; used for offline checks). */
export function playInto(ctx: BaseAudioContext, out: AudioNode, key: SoundKey): void {
  SOUND_DEFS[key].play(ctx, out)
}

// --- master bus ---------------------------------------------------------------------

interface Bus {
  input: GainNode
  master: GainNode
}

const buses = new WeakMap<BaseAudioContext, Bus>()

function volumeToGain(v: number): number {
  const clamped = Math.min(100, Math.max(0, Number.isFinite(v) ? v : 0))
  return ((clamped / 100) ** 1.6) * 0.9
}

function getBus(ctx: BaseAudioContext): Bus {
  let bus = buses.get(ctx)
  if (bus) return bus
  const input = ctx.createGain()
  const lowpass = ctx.createBiquadFilter()
  lowpass.type = 'lowpass'
  lowpass.frequency.value = 3800
  lowpass.Q.value = 0.5
  const master = ctx.createGain()
  master.gain.value = volumeToGain(settings.general.soundVolume)
  const limiter = ctx.createDynamicsCompressor()
  limiter.threshold.value = -12
  limiter.knee.value = 8
  limiter.ratio.value = 12
  limiter.attack.value = 0.002
  limiter.release.value = 0.12
  input.connect(lowpass)
  lowpass.connect(master)
  master.connect(limiter)
  limiter.connect(ctx.destination)
  bus = { input, master }
  buses.set(ctx, bus)
  return bus
}

// --- lifecycle ----------------------------------------------------------------------

const { settings } = useSettings()

let audioCtx: AudioContext | null = null

/** Lazily creates the context; never called while effects are disabled. */
function getAudioContext(): AudioContext | null {
  if (audioCtx) return audioCtx
  try {
    audioCtx = new AudioContext()
  } catch {
    return null
  }
  return audioCtx
}

const enabled = reactive({ value: settings.general.soundEffects })

function applyVolume(): void {
  if (!audioCtx) return
  const bus = buses.get(audioCtx)
  if (!bus) return
  const now = audioCtx.currentTime
  bus.master.gain.cancelScheduledValues(now)
  bus.master.gain.setValueAtTime(bus.master.gain.value, now)
  bus.master.gain.linearRampToValueAtTime(volumeToGain(settings.general.soundVolume), now + 0.05)
}

// Single module-level watcher for the global concern (no per-component watchers).
watch(
  () => [settings.general.soundEffects, settings.general.soundVolume] as const,
  ([on]) => {
    enabled.value = on
    applyVolume()
  },
)

const lastPlayed = new Map<SoundKey, number>()

function nowMs(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now()
}

function start(ctx: AudioContext, key: SoundKey): void {
  if (!enabled.value || activeVoices >= MAX_VOICES) return
  try {
    playInto(ctx, getBus(ctx).input, key)
  } catch {
    /* audio not available — silently ignore */
  }
}

function playSound(key: SoundKey): void {
  if (!enabled.value) return
  const t = nowMs()
  const last = lastPlayed.get(key)
  if (last !== undefined && t - last < RETRIGGER_MS) return
  if (activeVoices >= MAX_VOICES) return
  const ctx = getAudioContext()
  if (!ctx) return
  lastPlayed.set(key, t)
  if (ctx.state === 'running') {
    start(ctx, key)
    return
  }
  // Suspended (autoplay policy): resume, then schedule so the first sound is not lost.
  ctx
    .resume()
    .then(() => {
      setTimeout(() => start(ctx, key), 50)
    })
    .catch(() => {
      /* resume blocked — ignore */
    })
}

/** Sound effects API; state is global and backed by settings (persisted). */
export function useSound() {
  return { playSound, enabled }
}
