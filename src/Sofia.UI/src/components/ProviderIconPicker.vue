<script setup lang="ts">
// Provider icon picker: the icon in the Settings → Models provider card is a
// button that opens a grid of Lucide keys (the same key space workspace icons
// use). The first cell clears the override, so the server derives the icon from
// the provider's preset or local runtime again. The panel is teleported and
// fixed-positioned because the settings panel scrolls and clips its children.
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

import SofiaIcon from '@/components/SofiaIcon.vue'
import { useI18n } from '@/i18n'
import { PROVIDER_ICON_CHOICES } from '@/types'
import { usePopoverLock } from '@/composables/usePopoverLock'

defineProps<{
  /** Effective icon currently rendered by the card. */
  icon: string
  /** Icon the user picked; null/absent when the icon is automatic (the server omits nulls). */
  override?: string | null
}>()

const emit = defineEmits<{ (e: 'select', icon: string): void }>()

const { t } = useI18n()
const { isOpen, claim, release } = usePopoverLock()

const open = computed(isOpen)
const root = ref<HTMLElement | null>(null)
const trigger = ref<HTMLElement | null>(null)
const panel = ref<HTMLElement | null>(null)
const position = ref({ top: 0, left: 0 })

/** Panel footprint, used to keep it inside the visible area and to flip it upward. */
const PANEL_WIDTH = 236
const PANEL_HEIGHT = 212

/** Visible vertical band of the nearest scrolling ancestor (the settings tab body), else the viewport. */
function visibleBounds(): { top: number; bottom: number } {
  let el = trigger.value?.parentElement ?? null
  while (el) {
    const style = getComputedStyle(el)
    if (/(auto|scroll)/.test(style.overflowY) && el.clientHeight > 0) {
      const rect = el.getBoundingClientRect()
      return { top: Math.max(8, rect.top), bottom: Math.min(window.innerHeight - 8, rect.bottom) }
    }
    el = el.parentElement
  }
  return { top: 8, bottom: window.innerHeight - 8 }
}

function place(): void {
  const rect = trigger.value?.getBoundingClientRect()
  if (!rect) return
  const bounds = visibleBounds()
  const left = Math.min(Math.max(8, rect.left), Math.max(8, window.innerWidth - PANEL_WIDTH - 8))
  const below = rect.bottom + 6
  const above = rect.top - PANEL_HEIGHT - 6
  // Prefer below the trigger, flip above when the scroll container has no room, and clamp as a last resort.
  const top = below + PANEL_HEIGHT <= bounds.bottom
    ? below
    : above >= bounds.top
      ? above
      : Math.max(bounds.top, Math.min(below, bounds.bottom - PANEL_HEIGHT))
  position.value = { top, left }
}

function toggle(): void {
  if (open.value) {
    release()
    return
  }
  if (!claim()) return
  place()
}

function close(): void {
  release()
}

function choose(icon: string): void {
  close()
  trigger.value?.focus()
  emit('select', icon)
}

function onDocumentPointer(event: MouseEvent): void {
  const target = event.target as Node
  if (root.value?.contains(target) || panel.value?.contains(target)) return
  close()
}

function onKeydown(event: KeyboardEvent): void {
  if (!open.value || event.key !== 'Escape') return
  event.preventDefault()
  event.stopPropagation()
  close()
  trigger.value?.focus()
}

function onViewportChange(): void {
  if (open.value) place()
}

onMounted(() => {
  document.addEventListener('mousedown', onDocumentPointer)
  window.addEventListener('keydown', onKeydown, true)
  window.addEventListener('resize', onViewportChange)
  // Capture phase: the settings panel scrolls its own body, not the window.
  window.addEventListener('scroll', onViewportChange, true)
})

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocumentPointer)
  window.removeEventListener('keydown', onKeydown, true)
  window.removeEventListener('resize', onViewportChange)
  window.removeEventListener('scroll', onViewportChange, true)
  release()
})
</script>

<template>
  <div ref="root" class="pip-root">
    <button
      ref="trigger"
      class="pip-trigger"
      type="button"
      :title="t('settings.models.changeIcon')"
      :aria-label="t('settings.models.changeIcon')"
      aria-haspopup="dialog"
      :aria-expanded="open"
      @click.stop="toggle"
    >
      <SofiaIcon :name="icon" :size="'1rem'" />
    </button>

    <Teleport to="body">
      <div
        v-if="open"
        ref="panel"
        class="pip-panel"
        role="dialog"
        :aria-label="t('settings.models.icon')"
        :style="{ top: `${position.top}px`, left: `${position.left}px` }"
        @click.stop
      >
        <button
          class="pip-auto"
          :class="{ 'pip-auto--active': !override }"
          type="button"
          :aria-pressed="!override"
          @click="choose('')"
        >
          <SofiaIcon name="refresh" :size="'0.85rem'" />
          <span class="pip-auto-label">{{ t('settings.models.iconAuto') }}</span>
        </button>

        <div class="pip-grid">
          <button
            v-for="key in PROVIDER_ICON_CHOICES"
            :key="key"
            class="pip-cell"
            :class="{ 'pip-cell--active': override === key }"
            type="button"
            :aria-label="key"
            :aria-pressed="override === key"
            :title="key"
            @click="choose(key)"
          >
            <SofiaIcon :name="key" :size="'0.95rem'" />
          </button>
        </div>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.pip-root {
  display: inline-flex;
  flex-shrink: 0;
}

/* Same 26px square as `.icon-mini`, but neutral on hover: this is a picker. */
.pip-trigger {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  padding: 0;
  border: 1px solid transparent;
  border-radius: 7px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
  transition: border-color 0.12s ease, background 0.12s ease, color 0.12s ease;
}

.pip-trigger:hover,
.pip-trigger[aria-expanded='true'] {
  border-color: var(--sofia-border-strong);
  background: var(--sofia-surface);
  color: var(--sofia-text);
}

.pip-trigger:focus-visible {
  outline: 2px solid var(--sofia-primary);
  outline-offset: 1px;
}

.pip-panel {
  position: fixed;
  z-index: 2100;
  width: 236px;
  max-height: min(320px, 60vh);
  overflow-y: auto;
  padding: 0.4rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.45);
}

.pip-auto {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  width: 100%;
  padding: 0.35rem 0.45rem;
  margin-bottom: 0.3rem;
  border: 1px solid transparent;
  border-radius: 8px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
  text-align: left;
  cursor: pointer;
}

.pip-auto:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.pip-auto--active {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.pip-auto-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.pip-grid {
  display: grid;
  grid-template-columns: repeat(6, 1fr);
  gap: 0.15rem;
}

.pip-cell {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 30px;
  padding: 0;
  border: 1px solid transparent;
  border-radius: 7px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.pip-cell:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.pip-cell--active {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.pip-cell:focus-visible,
.pip-auto:focus-visible {
  outline: 2px solid var(--sofia-primary);
  outline-offset: 1px;
}
</style>
