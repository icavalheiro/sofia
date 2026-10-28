<script setup lang="ts">
// Right-side workspace panel: the files changed in the workspace — the union of
// the active session's edits (`/api/sessions/{id}/diff`) and the git working
// tree (`/api/vcs/diff`), reloaded whenever a tool call finishes —
// plus quick actions on the workspace folder (open in the file manager /
// VS Code, terminal, copy path).
import { computed, onBeforeUnmount, ref, watch } from 'vue'

import type { FileChange, FileDiffDto } from '@/types'
import { fileDiffToChange } from '@/services/diff'
import { api } from '@/services/sofia'
import { useI18n } from '@/i18n'
import { useHarness } from '@/composables/useHarness'
import SofiaIcon from '@/components/SofiaIcon.vue'
import DiffViewer from '@/components/DiffViewer.vue'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'close'): void }>()
const { t } = useI18n()

const { state, activeConversation, activeWorkspace, toolActivity } = useHarness()

const changes = ref<FileChange[]>([])
const loading = ref(false)
const loadError = ref<string | null>(null)
// Which diffs contributed to the current list.
const source = ref<'session' | 'working' | 'both'>('working')

const sessionId = computed(() => activeConversation.value?.id ?? null)
/** Folder of the active workspace ('' for chat groups). */
const directory = computed(() => (activeWorkspace.value?.kind === 'folder' ? activeWorkspace.value.path : ''))

// Signature of the last applied list, so identical reloads don't re-render.
let lastSignature = ''
let loadSeq = 0

const pathKey = (p: string): string => p.replace(/\\/g, '/').toLowerCase()

/**
 * Union of the session diff (edits made through Sofia's tools) and the git
 * working-tree diff (anything changed externally). For files in both, git's
 * diff wins: it is cumulative against HEAD, so it already contains the
 * session's edits plus the external ones.
 */
function mergeDiffs(session: FileDiffDto[], working: FileDiffDto[]): FileDiffDto[] {
  const merged = new Map<string, FileDiffDto>()
  for (const d of session) merged.set(pathKey(d.file), d)
  for (const d of working) merged.set(pathKey(d.file), d)
  return [...merged.values()].sort((a, b) => a.file.localeCompare(b.file))
}

async function loadDiff(): Promise<void> {
  const seq = ++loadSeq
  loading.value = true
  loadError.value = null
  try {
    const [session, working] = await Promise.allSettled([
      sessionId.value
        ? api<FileDiffDto[]>('GET', `/api/sessions/${sessionId.value}/diff`)
        : Promise.resolve<FileDiffDto[]>([]),
      directory.value
        ? api<FileDiffDto[]>('GET', `/api/vcs/diff?directory=${encodeURIComponent(directory.value)}`)
        : Promise.resolve<FileDiffDto[]>([]),
    ])
    // A newer reload started meanwhile: let it win.
    if (seq !== loadSeq) return
    if (session.status === 'rejected' && working.status === 'rejected') throw working.reason
    if (session.status === 'rejected') console.warn('session diff failed, showing the working tree only', session.reason)
    if (working.status === 'rejected') console.warn('working-tree diff failed, showing the session only', working.reason)

    const sessionInfos = session.status === 'fulfilled' ? (session.value ?? []) : []
    const workingInfos = working.status === 'fulfilled' ? (working.value ?? []) : []
    source.value = sessionInfos.length && workingInfos.length ? 'both' : sessionInfos.length ? 'session' : 'working'

    const infos = mergeDiffs(sessionInfos, workingInfos)
    const signature = JSON.stringify(infos.map((d) => [d.file, d.status, d.additions, d.deletions, d.patch]))
    if (signature !== lastSignature) {
      lastSignature = signature
      changes.value = infos.map(fileDiffToChange)
    }
  } catch (e) {
    if (seq !== loadSeq) return
    lastSignature = ''
    changes.value = []
    loadError.value = e instanceof Error ? e.message : String(e)
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}

// Refresh whenever the panel opens or the user switches conversation/workspace.
watch(
  () => [props.open, sessionId.value, directory.value] as const,
  ([open]) => {
    lastSignature = ''
    if (open) void loadDiff()
  },
  { immediate: true },
)

// A finished run usually changed files: reload the list.
watch(
  () => state.isReplying,
  (busy, wasBusy) => {
    if (props.open && wasBusy && !busy) void loadDiff()
  },
)

// The list only changes through tool calls, so reload when one finishes in this
// workspace (no polling).
watch(
  () => toolActivity.tick,
  () => {
    if (!props.open) return
    const ws = activeWorkspace.value
    if (!ws || ws.id === toolActivity.workspaceId) void loadDiff()
  },
)

// Background processes started by the agent (dev servers, watchers): listed and killable.
interface BackgroundProcess {
  id: string
  command: string
  pid: number | null
  status: 'running' | 'exited' | 'killed'
  exitCode: number | null
  sessionId: string | null
}
const processes = ref<BackgroundProcess[]>([])
let processTimer: number | undefined

async function loadProcesses(): Promise<void> {
  try {
    processes.value = await api<BackgroundProcess[]>('GET', '/api/processes')
  } catch {
    processes.value = []
  }
}

async function killProcess(p: BackgroundProcess): Promise<void> {
  try {
    await api('POST', `/api/processes/${p.id}/kill`)
  } catch (e) {
    notify(e instanceof Error ? e.message : String(e))
  }
  await loadProcesses()
}

watch(
  () => props.open,
  (open) => {
    window.clearInterval(processTimer)
    if (!open) return
    void loadProcesses()
    processTimer = window.setInterval(() => void loadProcesses(), 3000)
  },
  { immediate: true },
)
onBeforeUnmount(() => window.clearInterval(processTimer))

const lastAction = ref('')

// Fullscreen two-column compare popup for a clicked file.
const diffFile = ref<FileChange | null>(null)

function openDiff(file: FileChange): void {
  diffFile.value = file
}

function notify(text: string): void {
  lastAction.value = text
  window.setTimeout(() => {
    if (lastAction.value === text) lastAction.value = ''
  }, 2600)
}

/** Opens the workspace folder on the server machine (file manager / VS Code / native terminal). */
async function openIn(app: 'folder' | 'vscode' | 'terminal'): Promise<void> {
  if (!directory.value) return
  try {
    await api('POST', '/api/open', { path: directory.value, app })
    notify(
      app === 'folder'
        ? 'Opened the folder in the file manager.'
        : app === 'vscode'
          ? 'Opened the folder in VS Code.'
          : 'Opened the folder in a terminal.',
    )
  } catch (e) {
    notify(e instanceof Error ? e.message : String(e))
  }
}

async function copyPath(): Promise<void> {
  if (!directory.value) return
  try {
    await navigator.clipboard.writeText(directory.value)
    notify('Workspace path copied to the clipboard.')
  } catch {
    notify(directory.value)
  }
}
</script>
<template>
  <Transition name="rp">
    <aside v-if="open" class="right-panel" aria-label="Workspace panel">
    <header class="rp-head">
      <div class="rp-title">
        <SofiaIcon name="panelRight" :size="'1rem'" />
        <span>{{ t('panel.title') }}</span>
      </div>
      <div class="rp-head-side">
        <button class="rp-refresh" type="button" aria-label="Reload diff" :title="'Reload'" @click="loadDiff">
          <SofiaIcon name="refresh" :size="'0.9rem'" :class="{ spinning: loading }" />
        </button>
        <button class="close-btn" type="button" :aria-label="t('panel.close')" @click="emit('close')">
          <SofiaIcon name="x" />
        </button>
      </div>
    </header>

    <div class="rp-body">
      <section class="rp-section">
        <div class="rp-section-label">
          {{ t('panel.modified') }} · {{ changes.length }}
          <!-- Where the list currently comes from: the active session, the git tree, or both. -->
          <span class="rp-source">{{
            source === 'both' ? 'session + working tree' : source === 'session' ? 'session' : 'working tree'
          }}</span>
        </div>
        <p v-if="loadError" class="rp-note">{{ loadError }}</p>
        <p v-else-if="loading && !changes.length" class="rp-note">Loading changes…</p>
        <p v-else-if="!changes.length" class="rp-note">
          {{ directory || sessionId ? 'No changes.' : 'No workspace folder.' }}
        </p>
        <div v-else class="rp-files">
          <button
            v-for="f in changes"
            :key="f.path"
            class="rp-file"
            type="button"
            :title="f.path"
            @click="openDiff(f)"
          >
            <span class="rp-badge" :class="`rp-badge--${f.status}`">{{ f.status }}</span>
            <SofiaIcon name="file" :size="'0.9rem'" />
            <span class="rp-file-path sofia-truncate">{{ f.path }}</span>
            <span v-if="f.additions || f.deletions" class="rp-delta">
              <span class="rp-add">+{{ f.additions ?? 0 }}</span>
              <span class="rp-del">-{{ f.deletions ?? 0 }}</span>
            </span>
          </button>
        </div>
      </section>

      <section class="rp-section">
        <div class="rp-section-label">Background processes · {{ processes.filter((p) => p.status === 'running').length }}</div>
        <p v-if="!processes.length" class="rp-note">None running.</p>
        <div v-for="p in processes" :key="p.id" class="rp-file" :title="p.command">
          <span class="rp-badge">{{ p.status }}</span>
          <span class="rp-file-path sofia-truncate">{{ p.command }}</span>
          <button v-if="p.status === 'running'" class="rp-refresh" type="button" :aria-label="'Kill ' + p.id" title="Kill" @click="killProcess(p)">
            <SofiaIcon name="stop" :size="'0.9rem'" />
          </button>
        </div>
      </section>

      <section class="rp-section">
        <div class="rp-section-label">{{ t('panel.actions') }}</div>
        <button class="rp-action" type="button" :disabled="!directory" @click="openIn('folder')">
          <SofiaIcon name="folderOpen" :size="'0.95rem'" />
          {{ t('panel.openFolder') }}
        </button>
        <button class="rp-action" type="button" :disabled="!directory" @click="openIn('vscode')">
          <SofiaIcon name="code" :size="'0.95rem'" />
          {{ t('panel.openVscode') }}
        </button>
        <button class="rp-action" type="button" :disabled="!directory" @click="openIn('terminal')">
          <SofiaIcon name="terminal" :size="'0.95rem'" />
          {{ t('panel.terminal') }}
        </button>
        <button class="rp-action" type="button" :disabled="!directory" @click="copyPath">
          <SofiaIcon name="folder" :size="'0.95rem'" />
          {{ t('panel.copyPath') }}
        </button>
        <p v-if="lastAction" class="rp-note">{{ lastAction }}</p>
      </section>
    </div>

    <DiffViewer :open="!!diffFile" :file="diffFile" @close="diffFile = null" />
    </aside>
  </Transition>
</template>

<style scoped>
.right-panel {
  display: flex;
  flex-direction: column;
  width: 300px;
  flex-shrink: 0;
  border-left: 1px solid var(--sofia-border);
  background: var(--sofia-surface);
  min-height: 0;
  overflow: hidden;
}

/* Slide / grow in and out of the layout. */
.rp-enter-active,
.rp-leave-active {
  transition: width 0.24s ease, transform 0.24s ease, opacity 0.18s ease;
}

.rp-enter-from,
.rp-leave-to {
  width: 0;
  transform: translateX(40px);
  opacity: 0;
}

.rp-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.7rem 0.9rem;
  border-bottom: 1px solid var(--sofia-border);
  flex-shrink: 0;
}

.rp-title {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--sofia-text);
}

.rp-title svg {
  color: var(--sofia-primary);
}

.close-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.close-btn:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.rp-head-side {
  display: inline-flex;
  align-items: center;
  gap: 0.15rem;
}

.rp-refresh {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.rp-refresh:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.spinning {
  animation: spin 0.9s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.rp-source {
  margin-left: 0.4rem;
  padding: 0.05rem 0.35rem;
  border-radius: 999px;
  background: var(--sofia-surface-3);
  color: var(--sofia-text-faint);
  letter-spacing: 0;
  text-transform: none;
  font-weight: 500;
}

.rp-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 0.8rem 0.9rem;
}

.rp-section {
  margin-bottom: 1.1rem;
}

.rp-section-label {
  font-size: 0.625rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
  margin-bottom: 0.5rem;
}

.rp-files {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}

.rp-file {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  width: 100%;
  padding: 0.3rem 0.35rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
  text-align: left;
  cursor: pointer;
}

.rp-file:hover {
  background: var(--sofia-surface-2);
}

.rp-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  flex-shrink: 0;
  border-radius: 5px;
  font-size: 0.625rem;
  font-weight: 700;
  font-family: var(--sofia-font-mono);
}

.rp-badge--M { background: rgba(201, 169, 79, 0.18); color: #c9a94f; }
.rp-badge--A { background: rgba(116, 166, 114, 0.18); color: var(--sofia-ok); }
.rp-badge--D { background: rgba(201, 112, 95, 0.18); color: var(--sofia-err); }

.rp-file-path {
  flex: 1;
  min-width: 0;
  color: var(--sofia-text);
}

.rp-delta {
  display: inline-flex;
  gap: 0.3rem;
  flex-shrink: 0;
  font-family: var(--sofia-font-mono);
  font-size: 0.625rem;
}

.rp-add { color: var(--sofia-ok); }
.rp-del { color: var(--sofia-err); }

.rp-action {
  display: flex;
  align-items: center;
  gap: 0.55rem;
  width: 100%;
  padding: 0.5rem 0.6rem;
  margin-bottom: 0.3rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 9px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-size: 0.8125rem;
  text-align: left;
  cursor: pointer;
}

.rp-action:hover:not(:disabled) {
  background: var(--sofia-surface-3);
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.rp-action:disabled {
  opacity: 0.45;
  cursor: default;
}

.rp-note {
  margin: 0.5rem 0 0;
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}
</style>
