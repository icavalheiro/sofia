<script setup lang="ts">
// Fullscreen two-column compare popup (like VS Code's git diff): "Before" on
// the left, "After" on the right, with added/removed lines highlighted.
import { computed } from 'vue'

import SofiaIcon from '@/components/SofiaIcon.vue'
import { useI18n } from '@/i18n'
import type { DiffLine, FileChange } from '@/types'

const props = defineProps<{ open: boolean; file: FileChange | null }>()
defineEmits<{ (e: 'close'): void }>()
const { t } = useI18n()

interface Row {
  left: DiffLine | null
  right: DiffLine | null
}

// Pair each unified line into an aligned left/right row: context shows on both
// sides, deletions only on the left, additions only on the right.
const rows = computed<Row[]>(() => {
  const out: Row[] = []
  for (const line of props.file?.diff ?? []) {
    if (line.type === 'ctx') out.push({ left: line, right: line })
    else if (line.type === 'del') out.push({ left: line, right: null })
    else out.push({ left: null, right: line })
  }
  return out
})

function cellClass(line: DiffLine | null): string {
  return line ? `dv-cell--${line.type}` : 'dv-cell--empty'
}
</script>

<template>
  <Teleport to="body">
    <Transition name="dv">
      <div v-if="open && file" class="dv" role="dialog" aria-modal="true" @click.self="$emit('close')">
        <div class="dv-panel">
          <header class="dv-head">
            <div class="dv-title">
              <span class="dv-badge" :class="`dv-badge--${file.status}`">{{ file.status }}</span>
              <span class="dv-path sofia-mono">{{ file.path }}</span>
            </div>
            <div class="dv-side">
              <span class="dv-stat dv-stat--add">+{{ file.additions ?? 0 }}</span>
              <span class="dv-stat dv-stat--del">-{{ file.deletions ?? 0 }}</span>
              <button class="dv-close" type="button" :aria-label="t('img.close')" @click="$emit('close')">
                <SofiaIcon name="x" />
              </button>
            </div>
          </header>

          <div class="dv-body">
            <div class="dv-colhead">
              <span>{{ t('diff.before') }}</span>
              <span>{{ t('diff.after') }}</span>
            </div>
            <div class="dv-grid">
              <template v-for="(row, i) in rows" :key="i">
                <div class="dv-cell" :class="cellClass(row.left)">
                  <span class="dv-no">{{ row.left?.oldNo ?? '' }}</span>
                  <span class="dv-sign">{{ row.left?.type === 'del' ? '-' : '' }}</span>
                  <pre class="dv-text sofia-mono">{{ row.left ? row.left.text : '' }}</pre>
                </div>
                <div class="dv-cell" :class="cellClass(row.right)">
                  <span class="dv-no">{{ row.right?.newNo ?? '' }}</span>
                  <span class="dv-sign">{{ row.right?.type === 'add' ? '+' : '' }}</span>
                  <pre class="dv-text sofia-mono">{{ row.right ? row.right.text : '' }}</pre>
                </div>
              </template>
            </div>
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.dv {
  position: fixed;
  inset: 0;
  z-index: 300;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.7);
  backdrop-filter: blur(3px);
  padding: 2rem;
}

.dv-panel {
  display: flex;
  flex-direction: column;
  width: min(1100px, 100%);
  height: min(760px, 100%);
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  overflow: hidden;
  box-shadow: 0 24px 70px rgba(0, 0, 0, 0.55);
}

.dv-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  padding: 0.7rem 0.9rem;
  border-bottom: 1px solid var(--sofia-border);
  flex-shrink: 0;
}

.dv-title {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  min-width: 0;
}

.dv-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: 6px;
  font-size: 0.7rem;
  font-weight: 700;
  flex-shrink: 0;
}

.dv-badge--M {
  color: var(--sofia-warn);
  background: var(--sofia-warn-soft);
}

.dv-badge--A {
  color: var(--sofia-ok);
  background: var(--sofia-ok-soft);
}

.dv-badge--D {
  color: var(--sofia-err);
  background: var(--sofia-err-soft);
}

.dv-path {
  font-size: 0.8125rem;
  color: var(--sofia-text);
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dv-side {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  flex-shrink: 0;
}

.dv-stat {
  font-size: 0.75rem;
  font-weight: 600;
}

.dv-stat--add {
  color: var(--sofia-ok);
}

.dv-stat--del {
  color: var(--sofia-err);
}

.dv-close {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.dv-close:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.dv-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.dv-colhead {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1px;
  padding: 0.4rem 0;
  border-bottom: 1px solid var(--sofia-border);
  background: var(--sofia-surface-2);
  flex-shrink: 0;
}

.dv-colhead span {
  padding: 0 0.7rem;
  font-size: 0.6875rem;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.dv-grid {
  flex: 1;
  min-height: 0;
  overflow: auto;
  display: grid;
  grid-template-columns: 1fr 1fr;
  align-content: start;
}

.dv-cell {
  display: grid;
  grid-template-columns: 44px 18px 1fr;
  align-items: start;
  border-bottom: 1px solid color-mix(in srgb, var(--sofia-border) 45%, transparent);
}

/* Two cells per row (left + right). */
.dv-cell:nth-child(odd) {
  border-right: 1px solid var(--sofia-border);
}

.dv-no {
  padding: 0.1rem 0.5rem 0.1rem 0;
  text-align: right;
  font-size: 0.72rem;
  color: var(--sofia-text-faint);
  user-select: none;
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
}

.dv-sign {
  text-align: center;
  font-size: 0.78rem;
  font-weight: 700;
  color: var(--sofia-text-faint);
  user-select: none;
}

.dv-text {
  margin: 0;
  padding: 0.1rem 0.6rem;
  font-size: 0.78rem;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  color: var(--sofia-text-muted);
}

.dv-cell--add {
  background: color-mix(in srgb, var(--sofia-ok) 14%, transparent);
}

.dv-cell--add .dv-sign {
  color: var(--sofia-ok);
}

.dv-cell--del {
  background: color-mix(in srgb, var(--sofia-err) 14%, transparent);
}

.dv-cell--del .dv-sign {
  color: var(--sofia-err);
}

.dv-cell--empty {
  background: repeating-linear-gradient(
    45deg,
    transparent,
    transparent 6px,
    color-mix(in srgb, var(--sofia-border) 22%, transparent) 6px,
    color-mix(in srgb, var(--sofia-border) 22%, transparent) 12px
  );
}

/* --- transition ---------------------------------------------------------- */
.dv-enter-active,
.dv-leave-active {
  transition: opacity 0.16s ease;
}

.dv-enter-active .dv-panel,
.dv-leave-active .dv-panel {
  transition: transform 0.16s ease;
}

.dv-enter-from,
.dv-leave-to {
  opacity: 0;
}

.dv-enter-from .dv-panel,
.dv-leave-to .dv-panel {
  transform: scale(0.98);
}
</style>
