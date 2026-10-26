import { reactive, watch } from 'vue'

import { useSettings } from '@/composables/useSettings'

// ---------------------------------------------------------------------------
// Lightweight sound effects via Web Audio API — no external files needed.
// All sounds are synthesised on the fly: soft sine/triangle tones with fast
// decay so they feel like gentle UI feedback rather than notification beeps.
// ---------------------------------------------------------------------------

export type SoundKey =
  | 'send'
  | 'select'
  | 'done'
  | 'error'
  | 'permission'
  | 'click'

interface SoundDef {
  play: (ctx: AudioContext, destination: AudioNode) => void
}

// --- helpers ---------------------------------------------------------------

function envelope(
  ctx: AudioContext,
  destination: AudioNode,
  duration: number,
  peak = 0.15,
): GainNode {
  const gain = ctx.createGain()
  gain.gain.setValueAtTime(0, ctx.currentTime)
  gain.gain.linearRampToValueAtTime(peak, ctx.currentTime + 0.008)
  gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + duration)
  gain.connect(destination)
  return gain
}

function tone(
  ctx: AudioContext,
  destination: AudioNode,
  freq: number,
  duration: number,
  type: OscillatorType = 'sine',
  peak = 0.12,
): void {
  const osc = ctx.createOscillator()
  const gain = envelope(ctx, destination, duration, peak)
  osc.type = type
  osc.frequency.setValueAtTime(freq, ctx.currentTime)
  osc.connect(gain)
  osc.start(ctx.currentTime)
  osc.stop(ctx.currentTime + duration + 0.01)
}

function sweep(
  ctx: AudioContext,
  destination: AudioNode,
  fromFreq: number,
  toFreq: number,
  duration: number,
  type: OscillatorType = 'sine',
  peak = 0.12,
): void {
  const osc = ctx.createOscillator()
  const gain = envelope(ctx, destination, duration, peak)
  osc.type = type
  osc.frequency.setValueAtTime(fromFreq, ctx.currentTime)
  osc.frequency.exponentialRampToValueAtTime(toFreq, ctx.currentTime + duration * 0.7)
  osc.connect(gain)
  osc.start(ctx.currentTime)
  osc.stop(ctx.currentTime + duration + 0.01)
}

// --- sound definitions -----------------------------------------------------

// Shared peak level so every effect has a similar perceived loudness.
const LEVEL = 0.12

const sendSound: SoundDef = {
  play(ctx, dest) {
    sweep(ctx, dest, 500, 900, 0.1, 'sine', LEVEL)
  },
}

const selectSound: SoundDef = {
  play(ctx, dest) {
    tone(ctx, dest, 1100, 0.06, 'sine', LEVEL * 0.8)
  },
}

const doneSound: SoundDef = {
  play(ctx, dest) {
    // Single soft "bloop" — short, low, gentle confirmation
    tone(ctx, dest, 520, 0.12, 'sine', LEVEL)
  },
}

const errorSound: SoundDef = {
  play(ctx, dest) {
    sweep(ctx, dest, 330, 200, 0.22, 'triangle', LEVEL)
  },
}

const permissionSound: SoundDef = {
  play(ctx, dest) {
    // Soft "ding" — single note with a gentle upward sweep, like a subtle notification
    sweep(ctx, dest, 740, 880, 0.18, 'sine', LEVEL)
  },
}

const clickSound: SoundDef = {
  play(ctx, dest) {
    const bufferSize = ctx.sampleRate * 0.025
    const buffer = ctx.createBuffer(1, bufferSize, ctx.sampleRate)
    const data = buffer.getChannelData(0)
    for (let i = 0; i < bufferSize; i++) {
      data[i] = (Math.random() * 2 - 1) * (1 - i / bufferSize)
    }
    const source = ctx.createBufferSource()
    source.buffer = buffer
    const filter = ctx.createBiquadFilter()
    filter.type = 'bandpass'
    filter.frequency.value = 3000
    filter.Q.value = 1
    const gain = envelope(ctx, dest, 0.03, LEVEL * 2)
    source.connect(filter)
    filter.connect(gain)
    source.start(ctx.currentTime)
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

// --- composable --------------------------------------------------------------

let audioCtx: AudioContext | null = null

function getAudioContext(): AudioContext | null {
  if (audioCtx) return audioCtx
  try {
    audioCtx = new AudioContext()
  } catch {
    return null
  }
  return audioCtx
}

// Resume the context on first user gesture (browsers block autoplay).
let resumeBound = false
function ensureResume(): void {
  if (resumeBound) return
  resumeBound = true
  const handler = () => {
    const ctx = getAudioContext()
    if (ctx && ctx.state === 'suspended') ctx.resume()
    document.removeEventListener('pointerdown', handler)
    document.removeEventListener('keydown', handler)
  }
  document.addEventListener('pointerdown', handler)
  document.addEventListener('keydown', handler)
}

ensureResume()

const enabled = reactive({ value: true })

/** Wire the sound toggle into settings so it persists. */
export function useSound() {
  const { settings } = useSettings()
  enabled.value = settings.general.soundEffects

  watch(
    () => settings.general.soundEffects,
    (v) => { enabled.value = v },
  )

  function playSound(key: SoundKey): void {
    if (!enabled.value) return
    const ctx = getAudioContext()
    if (!ctx || ctx.state !== 'running') return
    try {
      SOUND_DEFS[key].play(ctx, ctx.destination)
    } catch {
      /* audio not available — silently ignore */
    }
  }

  return { playSound, enabled }
}
