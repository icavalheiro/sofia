import { watch } from 'vue'

import { useSettings } from '@/composables/useSettings'

// ---------------------------------------------------------------------------
// Applies the Appearance settings to the document: accent colour tokens, base
// font size (scales every rem-based size) and the compact-sidebar dimensions.
// Runs as a module-level watcher so importing it once (from App.vue) is enough.
// ---------------------------------------------------------------------------

const ACCENT_PRESETS: Record<string, string> = {
  brown: '#a9744f',
  beige: '#c9b489',
  slate: '#7b7b84',
}

function hexToRgb(hex: string): { r: number; g: number; b: number } {
  const clean = hex.replace('#', '')
  const full = clean.length === 3 ? clean.split('').map((c) => c + c).join('') : clean
  const num = parseInt(full, 16)
  return { r: (num >> 16) & 255, g: (num >> 8) & 255, b: num & 255 }
}

function relativeLuminance(r: number, g: number, b: number): number {
  return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255
}

function applyAccent(baseHex: string): void {
  const root = document.documentElement
  const { r, g, b } = hexToRgb(baseHex)

  // Lighten toward white for the "strong" hover state.
  const lighten = (amount: number) => {
    const lr = Math.round(r + (255 - r) * amount)
    const lg = Math.round(g + (255 - g) * amount)
    const lb = Math.round(b + (255 - b) * amount)
    return `rgb(${lr}, ${lg}, ${lb})`
  }

  const ink = relativeLuminance(r, g, b) > 0.55 ? '#171719' : '#ffffff'

  root.style.setProperty('--sofia-primary', baseHex)
  root.style.setProperty('--sofia-primary-strong', lighten(0.12))
  root.style.setProperty('--sofia-primary-ink', ink)
  root.style.setProperty('--sofia-primary-soft', `rgba(${r}, ${g}, ${b}, 0.16)`)
  root.style.setProperty('--sofia-primary-soft-strong', `rgba(${r}, ${g}, ${b}, 0.26)`)
}

function applyAppearance(): void {
  if (typeof document === 'undefined') return
  const { settings } = useSettings()
  const root = document.documentElement
  const { accent, customColor, fontSize, compactSidebar } = settings.appearance

  const base = accent === 'custom' ? customColor : (ACCENT_PRESETS[accent] ?? ACCENT_PRESETS.brown)
  applyAccent(base)

  root.style.fontSize = fontSize === 'sm' ? '14px' : fontSize === 'lg' ? '18px' : '16px'

  root.style.setProperty('--sofia-sidebar-width', compactSidebar ? '236px' : '288px')
  root.style.setProperty('--sofia-row-pad', compactSidebar ? '0.2rem' : '0.4rem')
  root.style.setProperty('--sofia-brand-pad', compactSidebar ? '0.65rem' : '1rem')
}

export function useAppearance(): void {
  const { settings } = useSettings()
  // Apply once on startup, then whenever any appearance field changes.
  watch(() => settings.appearance, applyAppearance, { deep: true, immediate: true })
}
