<script setup lang="ts">
// Session popover: actions on the active session over a plain header chip —
// compact, export / import JSON and server-wide stats — in the same visual
// language as the header's language menu.
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useSound } from '@/composables/useSound'
import { api } from '@/services/sofia'
import type { MessageDto, SessionDto, StatsDto } from '@/types'
import SofiaIcon from '@/components/SofiaIcon.vue'

const { activeConversation, activeWorkspace, openConversation } = useHarness()
const { playSound } = useSound()

const open = ref(false)
const root = ref<HTMLElement | null>(null)
const note = ref('')
const busy = ref<string | null>(null)

const session = computed(() => activeConversation.value)
const sessionId = computed(() => session.value?.id ?? null)
const shortId = computed(() => (sessionId.value ?? '').slice(0, 14))

function failure(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}

// --- stats (server aggregate) ---------------------------------------------------
const stats = ref<StatsDto | null>(null)
const statsOpen = ref(false)

async function loadStats(): Promise<void> {
  if (stats.value && statsOpen.value) {
    statsOpen.value = false
    return
  }
  busy.value = 'stats'
  try {
    stats.value = await api<StatsDto>('GET', '/api/stats')
    statsOpen.value = true
  } catch (e) {
    note.value = failure(e)
  } finally {
    busy.value = null
  }
}

// --- compact ----------------------------------------------------------------------
// The summary lands in the transcript as a `message.created` with summary: true.
async function compact(): Promise<void> {
  if (!sessionId.value || busy.value) return
  busy.value = 'compact'
  note.value = ''
  try {
    await api('POST', `/api/sessions/${sessionId.value}/compact`)
    note.value = 'Compaction started — the summary lands in this conversation.'
  } catch (e) {
    note.value = failure(e)
  } finally {
    busy.value = null
  }
}

// --- export -----------------------------------------------------------------------
// Downloads the full session (info + messages) as JSON the importer can read.
async function exportSession(): Promise<void> {
  if (!sessionId.value || busy.value) return
  busy.value = 'export'
  note.value = ''
  try {
    const data = await api<unknown>('GET', `/api/sessions/${sessionId.value}/export`)
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `session-${sessionId.value.slice(0, 12)}.json`
    a.click()
    URL.revokeObjectURL(url)
    note.value = 'Session exported as JSON.'
  } catch (e) {
    note.value = failure(e)
  } finally {
    busy.value = null
  }
}

// --- import -----------------------------------------------------------------------
// Reads an exported JSON ({ info, messages }) into the active workspace.
const fileInput = ref<HTMLInputElement | null>(null)

function pickImport(): void {
  fileInput.value?.click()
}

async function onImportFile(): Promise<void> {
  const file = fileInput.value?.files?.[0]
  if (!file) return
  busy.value = 'import'
  note.value = ''
  try {
    const payload = JSON.parse(await file.text()) as { info?: SessionDto; messages?: MessageDto[] }
    if (!payload?.info) throw new Error('Not an exported Sofia session (missing "info").')
    const imported = await api<SessionDto>('POST', '/api/sessions/import', {
      workspaceId: activeWorkspace.value?.id,
      info: payload.info,
      messages: payload.messages ?? [],
    })
    note.value = `Imported session ${String(imported?.id ?? '').slice(0, 14)}.`
    if (imported?.id) await openConversation(imported.id)
  } catch (e) {
    note.value = failure(e)
  } finally {
    busy.value = null
    if (fileInput.value) fileInput.value.value = ''
  }
}
// Open/close with outside-click + Escape, mirroring the language menu.
function toggleOpen(): void {
  open.value = !open.value
  playSound('click')
}

function onDocPointer(event: MouseEvent): void {
  if (root.value && !root.value.contains(event.target as Node)) open.value = false
}
function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') open.value = false
}
onMounted(() => {
  document.addEventListener('mousedown', onDocPointer)
  document.addEventListener('keydown', onKeydown)
})
onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocPointer)
  document.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <div ref="root" class="sess-wrap">
    <button
      class="sess-toggle"
      :class="{ 'sess-toggle--active': open }"
      type="button"
      title="Session actions (compact, stats, export)"
      aria-label="Session actions"
      @click="toggleOpen"
    >
      <SofiaIcon name="layers" :size="'1rem'" />
    </button>

    <div v-if="open" class="sess-menu" role="menu">
      <div class="sess-head">
        <span class="sess-title sofia-truncate">{{ session?.title || 'Session' }}</span>
        <span class="sess-id sofia-mono" :title="sessionId ?? ''">{{ shortId || 'not connected' }}</span>
      </div>

      <button class="sess-item" type="button" role="menuitem" :disabled="!sessionId || busy !== null" @click="compact">
        <SofiaIcon name="code" :size="'0.95rem'" />
        <span class="sess-item-label">{{ busy === 'compact' ? 'Compacting…' : 'Compact session' }}</span>
      </button>


      <button class="sess-item" type="button" role="menuitem" :disabled="!sessionId || busy !== null" @click="exportSession">
        <SofiaIcon name="box" :size="'0.95rem'" />
        <span class="sess-item-label">{{ busy === 'export' ? 'Exporting…' : 'Export JSON' }}</span>
      </button>

      <button class="sess-item" type="button" role="menuitem" :disabled="busy !== null" @click="pickImport">
        <SofiaIcon name="folderOpen" :size="'0.95rem'" />
        <span class="sess-item-label">{{ busy === 'import' ? 'Importing…' : 'Import JSON' }}</span>
      </button>
      <input ref="fileInput" class="sess-file" type="file" accept="application/json,.json" @change="onImportFile" />

      <button class="sess-item" :class="{ 'sess-item--open': statsOpen }" type="button" role="menuitem" :disabled="busy === 'stats'" @click="loadStats">
        <SofiaIcon name="check" :size="'0.95rem'" />
        <span class="sess-item-label">{{ busy === 'stats' ? 'Loading…' : 'Full stats' }}</span>
      </button>

      <div v-if="statsOpen && stats" class="sess-stats">
        <div class="sess-stat"><span>Sessions</span><b>{{ stats.sessions }}</b></div>
        <div class="sess-stat"><span>Prompts</span><b>{{ stats.prompts }}</b></div>
        <div class="sess-stat"><span>Steps</span><b>{{ stats.steps }}</b></div>
        <div class="sess-stat"><span>Tokens in</span><b>{{ (stats.tokens?.input ?? 0).toLocaleString() }}</b></div>
        <div class="sess-stat"><span>Tokens out</span><b>{{ (stats.tokens?.output ?? 0).toLocaleString() }}</b></div>
        <div class="sess-stat"><span>Cost</span><b>${{ (stats.cost ?? 0).toFixed(2) }}</b></div>
        <div class="sess-stat"><span>Active days</span><b>{{ stats.activeDays }}</b></div>
        <div class="sess-stat"><span>Streak</span><b>{{ stats.streak }}</b></div>
        <p class="sess-stat-note">All sessions on this server (aggregate).</p>
      </div>

      <p v-if="note" class="sess-note">{{ note }}</p>
    </div>
  </div>
</template>

<style scoped>
.sess-wrap {
  position: relative;
}

/* Mirrors the header's .panel-toggle (scoped there, so it can't reach this child). */
.sess-toggle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 30px;
  height: 30px;
  padding: 0 0.3rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.sess-toggle:hover {
  color: var(--sofia-text);
  background: var(--sofia-surface-3);
}

.sess-toggle--active {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

.sess-menu {
  position: absolute;
  top: calc(100% + 0.4rem);
  right: 0;
  z-index: 60;
  width: 240px;
  padding: 0.55rem;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 42px rgba(0, 0, 0, 0.45);
}

.sess-head {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  padding: 0.1rem 0.35rem 0.5rem;
  margin-bottom: 0.35rem;
  border-bottom: 1px solid var(--sofia-border);
}

.sess-title {
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--sofia-text);
}

.sess-id {
  font-size: 0.625rem;
  color: var(--sofia-text-faint);
}

.sess-item {
  display: flex;
  align-items: center;
  gap: 0.55rem;
  width: 100%;
  padding: 0.45rem 0.5rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text);
  font-size: 0.8125rem;
  text-align: left;
  cursor: pointer;
}

.sess-item:hover:not(:disabled) {
  background: var(--sofia-surface-2);
  color: var(--sofia-primary);
}

.sess-item:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.sess-item svg {
  color: var(--sofia-text-muted);
}

.sess-item--open svg {
  color: var(--sofia-primary);
}

.sess-file {
  display: none;
}

.sess-stats {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.3rem 0.6rem;
  margin-top: 0.45rem;
  padding: 0.5rem;
  border: 1px solid var(--sofia-border);
  border-radius: 8px;
  background: var(--sofia-surface-2);
}

.sess-stat {
  display: flex;
  justify-content: space-between;
  gap: 0.35rem;
  font-size: 0.6875rem;
  color: var(--sofia-text-muted);
}

.sess-stat b {
  font-family: var(--sofia-font-mono);
  font-weight: 600;
  color: var(--sofia-text);
}

.sess-stat-note {
  grid-column: 1 / -1;
  margin: 0.1rem 0 0;
  font-size: 0.625rem;
  color: var(--sofia-text-faint);
}

.sess-note {
  margin: 0.45rem 0.35rem 0;
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
}
</style>
