import { reactive, watch } from 'vue'

import type { UserProfile } from '@/types'

// ---------------------------------------------------------------------------
// Local UI preferences: the user profile, language and a handful of general /
// appearance toggles. Persisted to its own sessionStorage slot. Server-side
// settings (providers, MCP, config) live on Sofia.Server, not here.
// ---------------------------------------------------------------------------

const STORAGE_KEY = 'sofia.settings.v1'

export interface SettingsState {
  user: UserProfile
  /** Selected UI language code (see LANGUAGES). */
  language: string
  general: {
    streamResponses: boolean
    confirmDestructive: boolean
    autoUpdate: boolean
    telemetry: boolean
    soundEffects: boolean
    /** Master volume for sound effects, integer 0–100. */
    soundVolume: number
  }
  appearance: {
    accent: 'brown' | 'beige' | 'slate' | 'custom'
    /** Hex colour used when accent === 'custom'. */
    customColor: string
    fontSize: 'sm' | 'md' | 'lg'
    compactSidebar: boolean
  }
}

function defaultState(): SettingsState {
  return {
    user: { name: 'You', handle: '@you', avatar: '🧑‍💻' },
    language: 'en',
    general: {
      streamResponses: true,
      confirmDestructive: true,
      autoUpdate: false,
      telemetry: false,
      soundEffects: true,
      soundVolume: 60,
    },
    appearance: {
      accent: 'brown',
      customColor: '#7c9cff',
      fontSize: 'md',
      compactSidebar: false,
    },
  }
}

const hasStorage = typeof sessionStorage !== 'undefined'

function loadPersisted(): Partial<SettingsState> | null {
  if (!hasStorage) return null
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY)
    return raw ? (JSON.parse(raw) as Partial<SettingsState>) : null
  } catch {
    return null
  }
}

/** Merges persisted values over the defaults so newly added fields still show. */
function buildState(): SettingsState {
  const base = defaultState()
  const saved = loadPersisted()
  if (!saved) return base
  return {
    user: { ...base.user, ...(saved.user ?? {}) },
    language: saved.language ?? base.language,
    general: { ...base.general, ...(saved.general ?? {}) },
    appearance: { ...base.appearance, ...(saved.appearance ?? {}) },
  }
}

const settings = reactive<SettingsState>(buildState())

watch(settings, () => {
  if (!hasStorage) return
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(settings))
  } catch {
    /* storage unavailable */
  }
}, { deep: true })

export function useSettings() {
  function updateUser(patch: Partial<UserProfile>): void {
    Object.assign(settings.user, patch)
  }

  function resetSettings(): void {
    Object.assign(settings, defaultState())
  }

  return { settings, updateUser, resetSettings }
}