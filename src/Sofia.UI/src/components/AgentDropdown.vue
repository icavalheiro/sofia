<script setup lang="ts">
// Dedicated agent picker: its own chip + upward panel, independent from the
// model dropdown (agents change the session `agent`, models change `modelId`).
// Plain elements like the other chips so styling stays predictable; click
// outside and Escape close it, arrows + Enter drive it while open, and `open()`
// is exposed so a keyboard shortcut can show it.
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

import type { AgentDto } from '@/types'
import SofiaIcon from '@/components/SofiaIcon.vue'
import { useI18n } from '@/i18n'
import { useSound } from '@/composables/useSound'
import { usePopoverLock } from '@/composables/usePopoverLock'

const props = defineProps<{
  /** Agent catalog for the current workspace (every mode; subagents are not selectable here). */
  agents: AgentDto[]
  /** Session agent id ('' when the session never picked one). */
  modelValue: string
  align?: 'left' | 'right'
  width?: number
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: string): void
  /** The panel opened: agents are plain files, so the caller re-reads them. */
  (e: 'refresh'): void
  /** Closed with Escape or by picking an agent (not by clicking elsewhere). */
  (e: 'dismiss'): void
}>()

const { t } = useI18n()
const { playSound } = useSound()
const { isOpen, claim, release } = usePopoverLock()

/** Agent used when the session has none (the server's built-in default). */
const DEFAULT_AGENT_ID = 'build'

const open = computed(isOpen)
const root = ref<HTMLElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const focusIndex = ref(0)

/** Only primary agents are selectable here; `@name` hands work to a subagent. */
const items = computed(() => props.agents.filter((a) => a.mode !== 'subagent'))

const currentId = computed(() => props.modelValue || DEFAULT_AGENT_ID)

const current = computed(
  () => items.value.find((a) => a.id === currentId.value) ?? items.value.find((a) => a.id === DEFAULT_AGENT_ID),
)

/** Chip caption: the agent name, or the raw id when its file is gone. */
const chipLabel = computed(() => current.value?.name || currentId.value)

function toggle(): void {
  if (open.value) close()
  else openMenu()
}

/** Opens the panel and asks for a fresh catalog (highlight follows the selection). */
function openMenu(): void {
  if (!claim()) return
  playSound('click')
  emit('refresh')
}

function close(): void {
  release()
}

function pick(agent: AgentDto): void {
  close()
  playSound('select')
  emit('update:modelValue', agent.id)
  emit('dismiss')
}

function isActive(agent: AgentDto): boolean {
  return agent.id === currentId.value
}

function scrollFocusedIntoView(): void {
  menu.value?.querySelectorAll('.ad-item')[focusIndex.value]?.scrollIntoView({ block: 'nearest' })
}

watch(open, (showing) => {
  if (!showing) return
  focusIndex.value = Math.max(items.value.findIndex(isActive), 0)
  void nextTick(scrollFocusedIntoView)
})

defineExpose({ open: openMenu })

function onDocumentPointer(event: MouseEvent): void {
  if (root.value && !root.value.contains(event.target as Node)) close()
}

function onKeydown(event: KeyboardEvent): void {
  if (!open.value) return
  const count = items.value.length
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    close()
    emit('dismiss')
  } else if (!count) {
    return
  } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    event.stopPropagation()
    const step = event.key === 'ArrowDown' ? 1 : -1
    focusIndex.value = (focusIndex.value + step + count) % count
    void nextTick(scrollFocusedIntoView)
  } else if (event.key === 'Enter' && !event.shiftKey) {
    // Capture phase: keep the composer textarea from also sending a message.
    event.preventDefault()
    event.stopPropagation()
    const agent = items.value[focusIndex.value]
    if (agent) pick(agent)
  }
}

onMounted(() => {
  document.addEventListener('mousedown', onDocumentPointer)
  window.addEventListener('keydown', onKeydown, true)
})

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocumentPointer)
  window.removeEventListener('keydown', onKeydown, true)
  release()
})

/** First line of the agent description, trimmed to one row. */
function describe(agent: AgentDto): string {
  return agent.description.split('\n')[0]?.slice(0, 90) ?? ''
}
</script>
<template>
  <div ref="root" class="ad-root">
    <button
      class="ad-chip"
      type="button"
      :title="t('composer.agent')"
      aria-haspopup="listbox"
      :aria-expanded="open"
      @click="toggle"
    >
      <SofiaIcon class="ad-chip-icon" name="bot" :size="'0.8rem'" />
      <span class="ad-chip-value sofia-truncate">{{ chipLabel }}</span>
      <SofiaIcon class="ad-caret" :name="open ? 'chevronDown' : 'chevronRight'" :size="'0.7rem'" />
    </button>

    <div
      v-if="open"
      ref="menu"
      class="ad-menu"
      role="listbox"
      :class="`ad-menu--${align ?? 'right'}`"
      :aria-label="t('composer.agent')"
      :style="width ? { minWidth: `${width}px` } : undefined"
    >
      <button
        v-for="(a, i) in items"
        :key="a.id"
        class="ad-item"
        :class="{ 'ad-item--active': isActive(a), 'ad-item--focus': i === focusIndex }"
        type="button"
        role="option"
        :aria-selected="isActive(a)"
        @mouseenter="focusIndex = i"
        @click="pick(a)"
      >
        <SofiaIcon class="ad-item-icon" name="bot" :size="'0.95rem'" />
        <span class="ad-item-body">
          <span class="ad-item-label">
            {{ a.name }}
            <span v-if="a.source && a.source !== 'builtin'" class="ad-item-badge">{{ a.source }}</span>
          </span>
          <span v-if="describe(a)" class="ad-item-desc">{{ describe(a) }}</span>
        </span>
        <SofiaIcon v-if="isActive(a)" class="ad-check" name="check" :size="'0.9rem'" />
      </button>
      <p v-if="!items.length" class="ad-empty">No primary agents on this server.</p>
    </div>
  </div>
</template>

<style scoped>
/* Matches the other composer chips (same pill, same compact size). */
.ad-root {
  position: relative;
  display: inline-flex;
}

.ad-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  max-width: 10rem;
  padding: 0.15rem 0.5rem;
  background: color-mix(in srgb, var(--sofia-surface-2) 74%, transparent);
  border: 1px solid var(--sofia-border);
  border-radius: 999px;
  color: var(--sofia-text);
  font-size: 0.6875rem;
  font-weight: 550;
  cursor: pointer;
  transition: border-color 0.12s ease, background 0.12s ease;
}

.ad-chip:hover {
  border-color: var(--sofia-border-strong);
  background: color-mix(in srgb, var(--sofia-surface-3) 82%, transparent);
}

.ad-chip-icon {
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.ad-chip-value {
  color: var(--sofia-text);
}

.ad-caret {
  color: var(--sofia-text-faint);
  flex-shrink: 0;
}

/* --- panel ------------------------------------------------------------- */
/* Opens upward: the chip sits in the composer near the bottom of the screen. */
.ad-menu {
  position: absolute;
  bottom: calc(100% + 6px);
  z-index: 30;
  min-width: 15rem;
  max-height: min(60vh, 420px);
  overflow-y: auto;
  padding: 0.25rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.45);
}

.ad-menu--left {
  left: 0;
}

.ad-menu--right {
  right: 0;
}

.ad-item {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  width: 100%;
  padding: 0.45rem 0.5rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text);
  text-align: left;
  cursor: pointer;
}

.ad-item:hover,
.ad-item--focus {
  background: var(--sofia-surface-3);
}

.ad-item--active {
  background: var(--sofia-primary-soft);
}

.ad-item--focus {
  box-shadow: inset 0 0 0 1px var(--sofia-border-strong);
}

.ad-item-icon {
  color: var(--sofia-text-muted);
  margin-top: 0.1rem;
  flex-shrink: 0;
}

.ad-item--active .ad-item-icon {
  color: var(--sofia-primary);
}

.ad-item-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 0.1rem;
  min-width: 0;
}

.ad-item-label {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.8125rem;
  font-weight: 550;
}

.ad-item-badge {
  font-size: 0.625rem;
  font-weight: 600;
  padding: 0.05rem 0.3rem;
  border-radius: 999px;
  background: var(--sofia-surface);
  color: var(--sofia-text-faint);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.ad-item-desc {
  font-size: 0.75rem;
  line-height: 1.4;
  color: var(--sofia-text-muted);
}

.ad-check {
  color: var(--sofia-primary);
  margin-top: 0.1rem;
  flex-shrink: 0;
}

.ad-empty {
  margin: 0.2rem 0.5rem;
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}
</style>
