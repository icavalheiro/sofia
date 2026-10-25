import { onScopeDispose, reactive, watch } from 'vue'

// ---------------------------------------------------------------------------
// Keyboard shortcuts. Actions are declared here with a default combo; users can
// rebind them in Settings → Shortcuts (overrides persist in localStorage).
// A single capture-phase listener (see `useShortcutListener`) matches key
// events and calls preventDefault/stopPropagation so the browser's own binding
// (Ctrl+N, Ctrl+L, Ctrl+P…) never fires. Components attach behaviour to an
// action with `useShortcut(id, handler)`.
// ---------------------------------------------------------------------------

const STORAGE_KEY = 'sofia.shortcuts.v1'

export type ShortcutGroup = 'composer' | 'session' | 'navigation' | 'app'

export interface ShortcutDef {
  id: string
  /** Default combo, e.g. "Ctrl+Shift+N". */
  combo: string
  group: ShortcutGroup
  /** Icon name from SofiaIcon. */
  icon: string
}

export const SHORTCUTS: readonly ShortcutDef[] = [
  { id: 'agent.pick', combo: 'Ctrl+Shift+A', group: 'composer', icon: 'bot' },
  { id: 'model.pick', combo: 'Ctrl+M', group: 'composer', icon: 'sparkles' },
  { id: 'reasoning.pick', combo: 'Ctrl+B', group: 'composer', icon: 'zap' },
  { id: 'permission.pick', combo: 'Ctrl+P', group: 'composer', icon: 'shield' },
  { id: 'composer.focus', combo: 'Ctrl+L', group: 'composer', icon: 'chat' },
  { id: 'session.stop', combo: 'Ctrl+.', group: 'composer', icon: 'stop' },
  { id: 'session.new', combo: 'Ctrl+N', group: 'session', icon: 'plus' },
  { id: 'session.archive', combo: 'Ctrl+Shift+E', group: 'session', icon: 'archive' },
  { id: 'session.prev', combo: 'Alt+ArrowUp', group: 'navigation', icon: 'chevronUp' },
  { id: 'session.next', combo: 'Alt+ArrowDown', group: 'navigation', icon: 'chevronDown' },
  { id: 'workspace.add', combo: 'Ctrl+O', group: 'navigation', icon: 'folder' },
  { id: 'panel.toggle', combo: 'Ctrl+\\', group: 'navigation', icon: 'panelRight' },
  { id: 'settings.open', combo: 'Ctrl+,', group: 'app', icon: 'gear' },
  { id: 'shortcuts.show', combo: 'Ctrl+/', group: 'app', icon: 'keyboard' },
]

export const SHORTCUT_GROUPS: readonly ShortcutGroup[] = ['composer', 'session', 'navigation', 'app']

/** Combos browsers reserve in regular tabs: page scripts usually cannot override them. */
const BROWSER_RESERVED = new Set([
  'Ctrl+N', 'Ctrl+T', 'Ctrl+W', 'Ctrl+Shift+N', 'Ctrl+Shift+T', 'Ctrl+Shift+W',
  'Ctrl+Tab', 'Ctrl+Shift+Tab', 'Meta+N', 'Meta+T', 'Meta+W', 'Meta+Shift+N',
])

const CODE_KEYS: Record<string, string> = {
  Backquote: '`', Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\',
  Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/',
}

const MODIFIER_KEYS = new Set(['Control', 'Shift', 'Alt', 'AltGraph', 'Meta'])

/** Canonical combo for a key event, or null for a bare modifier press. The
 *  physical key (`code`) is used for letters, digits and punctuation so Shift /
 *  Alt / keyboard layout do not change the combo. */
export function comboFromEvent(e: KeyboardEvent): string | null {
  if (MODIFIER_KEYS.has(e.key)) return null
  let key: string
  if (/^Key[A-Z]$/.test(e.code)) key = e.code.slice(3)
  else if (/^Digit\d$/.test(e.code)) key = e.code.slice(5)
  else if (CODE_KEYS[e.code]) key = CODE_KEYS[e.code]!
  else if (e.key === ' ') key = 'Space'
  else if (e.key.length === 1) key = e.key.toUpperCase()
  else key = e.key
  const parts: string[] = []
  if (e.ctrlKey) parts.push('Ctrl')
  if (e.altKey) parts.push('Alt')
  if (e.shiftKey) parts.push('Shift')
  if (e.metaKey) parts.push('Meta')
  parts.push(key)
  return parts.join('+')
}

/** A combo is acceptable when it cannot be confused with plain typing. */
export function isValidCombo(combo: string): boolean {
  const parts = combo.split('+')
  const hasModifier = parts.some((p) => p === 'Ctrl' || p === 'Alt' || p === 'Meta')
  return hasModifier || /^F\d{1,2}$/.test(parts[parts.length - 1] ?? '')
}

export function isBrowserReserved(combo: string): boolean {
  return BROWSER_RESERVED.has(combo)
}

// --- state -------------------------------------------------------------------------

const hasStorage = typeof localStorage !== 'undefined'

/** Only user changes are stored: id → combo ('' = deliberately unbound). */
function loadOverrides(): Record<string, string> {
  if (!hasStorage) return {}
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    const parsed = raw ? (JSON.parse(raw) as Record<string, unknown>) : {}
    const result: Record<string, string> = {}
    for (const def of SHORTCUTS) {
      const value = parsed[def.id]
      if (typeof value === 'string') result[def.id] = value
    }
    return result
  } catch {
    return {}
  }
}

const overrides = reactive<Record<string, string>>(loadOverrides())

watch(overrides, () => {
  if (!hasStorage) return
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(overrides))
  } catch {
    /* storage unavailable */
  }
}, { deep: true })

/** `recording`: the Shortcuts tab is capturing a new combo. `suspended`: a modal
 *  owns the keyboard. Either one silences the global listener. */
const control = reactive({ recording: false, suspended: false })

const handlers = new Map<string, () => void>()

export function bindingOf(id: string): string {
  if (id in overrides) return overrides[id]!
  return SHORTCUTS.find((s) => s.id === id)?.combo ?? ''
}

function defaultOf(id: string): string {
  return SHORTCUTS.find((s) => s.id === id)?.combo ?? ''
}

/** The other action already using `combo`, if any. */
export function conflictFor(id: string, combo: string): ShortcutDef | undefined {
  if (!combo) return undefined
  return SHORTCUTS.find((s) => s.id !== id && bindingOf(s.id) === combo)
}

export function setBinding(id: string, combo: string): void {
  if (combo === defaultOf(id)) delete overrides[id]
  else overrides[id] = combo
}

export function resetBinding(id: string): void {
  delete overrides[id]
}

export function resetAllBindings(): void {
  for (const key of Object.keys(overrides)) delete overrides[key]
}

export function isCustomized(id: string): boolean {
  return id in overrides
}

export function registerShortcut(id: string, handler: () => void): () => void {
  handlers.set(id, handler)
  return () => {
    if (handlers.get(id) === handler) handlers.delete(id)
  }
}

/** Runs the handler attached to an action (e.g. focus the composer after a new session). */
export function triggerShortcut(id: string): void {
  handlers.get(id)?.()
}

/** Attaches `handler` to a shortcut action for the lifetime of the component. */
export function useShortcut(id: string, handler: () => void): void {
  onScopeDispose(registerShortcut(id, handler))
}

function onKeydown(event: KeyboardEvent): void {
  if (control.recording || control.suspended || event.isComposing) return
  const combo = comboFromEvent(event)
  if (!combo) return
  const def = SHORTCUTS.find((s) => bindingOf(s.id) === combo)
  if (!def) return
  // Swallow the event even when nothing handles it, so the browser never reacts.
  event.preventDefault()
  event.stopPropagation()
  if (!event.repeat) handlers.get(def.id)?.()
}

/** Installs the global capture-phase listener; call once from the app shell. */
export function useShortcutListener(): void {
  window.addEventListener('keydown', onKeydown, true)
  onScopeDispose(() => window.removeEventListener('keydown', onKeydown, true))
}

export function useShortcuts() {
  return {
    shortcuts: SHORTCUTS,
    control,
    bindingOf,
    conflictFor,
    setBinding,
    resetBinding,
    resetAllBindings,
    isCustomized,
  }
}
