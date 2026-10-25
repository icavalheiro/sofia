<script setup lang="ts">
// Reusable "chip + dropdown" selector. Used in the composer for model,
// permission and reasoning. Plain elements (not Mantine Menu) so behaviour and
// styling stay predictable; click-outside and Escape both close it. When open
// it is also keyboard driven (arrows + Enter) and can be opened from outside
// through the exposed `open()` (keyboard shortcuts).
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

import SofiaIcon from '@/components/SofiaIcon.vue'
import { useSound } from '@/composables/useSound'
import { usePopoverLock } from '@/composables/usePopoverLock'

export interface PopoverItem {
  value: string
  label: string
  description?: string
  icon?: string
  badge?: string
}

const props = defineProps<{
  icon?: string
  /** Small uppercase caption rendered above the value. */
  label?: string
  items: PopoverItem[]
  modelValue: string
  align?: 'left' | 'right'
  width?: number
  /** Smaller chip without the caption (the label becomes the tooltip). */
  compact?: boolean
}>()

const emit = defineEmits<{
  (e: 'update:modelValue', value: string): void
  /** Closed with Escape or by picking an item (not by clicking elsewhere). */
  (e: 'dismiss'): void
}>()

const { playSound } = useSound()
const { isOpen, claim, release } = usePopoverLock()

const open = computed({
  get: isOpen,
  set: (value: boolean) => (value ? claim() : release()),
})
const root = ref<HTMLElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const focusIndex = ref(0)

const selected = computed(
  () => props.items.find((i) => i.value === props.modelValue) ?? props.items[0],
)

function toggle(): void {
  playSound('click')
  open.value = !open.value
}

/** Opens the menu with the current selection highlighted (shortcut entry point). */
function openMenu(): void {
  if (open.value) return
  playSound('click')
  open.value = true
}

function pick(item: PopoverItem): void {
  emit('update:modelValue', item.value)
  open.value = false
  playSound('select')
  emit('dismiss')
}

watch(open, (isOpen) => {
  if (!isOpen) return
  const current = props.items.findIndex((i) => isActive(i))
  focusIndex.value = Math.max(current, 0)
  void nextTick(scrollFocusedIntoView)
})

function scrollFocusedIntoView(): void {
  menu.value?.querySelectorAll('.sp-item')[focusIndex.value]?.scrollIntoView({ block: 'nearest' })
}

defineExpose({ open: openMenu, close })

/** True when the item is the bound value. */
function isActive(item: PopoverItem): boolean {
  return item.value === props.modelValue
}

function onDocumentPointer(event: MouseEvent): void {
  if (root.value && !root.value.contains(event.target as Node)) open.value = false
}

function onKeydown(event: KeyboardEvent): void {
  if (!open.value) return
  const count = props.items.length
  if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    open.value = false
    emit('dismiss')
  } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    event.stopPropagation()
    const step = event.key === 'ArrowDown' ? 1 : -1
    focusIndex.value = (focusIndex.value + step + count) % count
    void nextTick(scrollFocusedIntoView)
  } else if (event.key === 'Enter' && !event.shiftKey) {
    // Capture phase: keep the composer textarea from also sending the message.
    event.preventDefault()
    event.stopPropagation()
    const item = props.items[focusIndex.value]
    if (item) pick(item)
  }
}

onMounted(() => {
  document.addEventListener('mousedown', onDocumentPointer)
  window.addEventListener('keydown', onKeydown, true)
})

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocumentPointer)
  window.removeEventListener('keydown', onKeydown, true)
  open.value = false
})
</script>

<template>
  <div ref="root" class="sp-root">
    <button
      class="sp-chip"
      :class="{ 'sp-chip--compact': compact }"
      type="button"
      :title="compact ? label : undefined"
      :aria-expanded="open"
      @click="toggle"
    >
      <SofiaIcon v-if="icon" class="sp-chip-icon" :name="icon" :size="compact ? '0.8rem' : '0.85rem'" />
      <span v-if="label && !compact" class="sp-chip-label">{{ label }}</span>
      <span class="sp-chip-value sofia-truncate">{{ selected?.label }}</span>
      <SofiaIcon class="sp-caret" :name="open ? 'chevronDown' : 'chevronRight'" :size="'0.7rem'" />
    </button>

    <div
      v-if="open"
      ref="menu"
      class="sp-menu"
      :class="`sp-menu--${align ?? 'left'}`"
      :style="width ? { minWidth: `${width}px` } : undefined"
    >
      <button
        v-for="(it, i) in items"
        :key="it.value"
        class="sp-item"
        :class="{ 'sp-item--active': isActive(it), 'sp-item--focus': i === focusIndex }"
        type="button"
        @mouseenter="focusIndex = i"
        @click="pick(it)"
      >
        <SofiaIcon v-if="it.icon" class="sp-item-icon" :name="it.icon" :size="'0.95rem'" />
        <span class="sp-item-body">
          <span class="sp-item-label">
            {{ it.label }}
            <span v-if="it.badge" class="sp-item-badge">{{ it.badge }}</span>
          </span>
          <span v-if="it.description" class="sp-item-desc">{{ it.description }}</span>
        </span>
        <SofiaIcon v-if="isActive(it)" class="sp-check" name="check" :size="'0.9rem'" />
      </button>
    </div>
  </div>
</template>

<style scoped>
.sp-root {
  position: relative;
  display: inline-flex;
}

.sp-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  max-width: 15rem;
  padding: 0.3rem 0.55rem;
  background: color-mix(in srgb, var(--sofia-surface-2) 74%, transparent);
  border: 1px solid var(--sofia-border);
  border-radius: 999px;
  color: var(--sofia-text);
  font-size: 0.8125rem;
  cursor: pointer;
  transition: border-color 0.12s ease, background 0.12s ease;
}

.sp-chip:hover {
  border-color: var(--sofia-border-strong);
  background: color-mix(in srgb, var(--sofia-surface-3) 82%, transparent);
}

.sp-chip--compact {
  gap: 0.3rem;
  max-width: 10rem;
  padding: 0.15rem 0.5rem;
  font-size: 0.6875rem;
}

.sp-chip-icon {
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.sp-chip-label {
  font-size: 0.6875rem;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.sp-chip-value {
  color: var(--sofia-text);
  font-weight: 550;
}

.sp-caret {
  color: var(--sofia-text-faint);
  flex-shrink: 0;
}

/* --- dropdown ----------------------------------------------------------- */
/* Opens upward: these chips sit in the composer near the bottom of the screen. */
.sp-menu {
  position: absolute;
  bottom: calc(100% + 6px);
  z-index: 30;
  min-width: 14rem;
  padding: 0.25rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.45);
}

.sp-menu--left {
  left: 0;
}

.sp-menu--right {
  right: 0;
}

/* Cap very tall option lists so the upward menu never overflows the top. */
.sp-menu {
  max-height: min(60vh, 420px);
  overflow-y: auto;
}

.sp-item {
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

.sp-item:hover,
.sp-item--focus {
  background: var(--sofia-surface-3);
}

.sp-item--active {
  background: var(--sofia-primary-soft);
}

.sp-item--focus {
  box-shadow: inset 0 0 0 1px var(--sofia-border-strong);
}

.sp-item-icon {
  color: var(--sofia-text-muted);
  margin-top: 0.1rem;
  flex-shrink: 0;
}

.sp-item--active .sp-item-icon {
  color: var(--sofia-primary);
}

.sp-item-body {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  min-width: 0;
  flex: 1;
}

.sp-item-label {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.8125rem;
  font-weight: 550;
}

.sp-item-badge {
  font-size: 0.625rem;
  font-weight: 600;
  padding: 0.05rem 0.3rem;
  border-radius: 999px;
  background: var(--sofia-surface);
  color: var(--sofia-text-faint);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.sp-item-desc {
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
  line-height: 1.4;
}

.sp-check {
  color: var(--sofia-primary);
  margin-top: 0.1rem;
  flex-shrink: 0;
}
</style>
