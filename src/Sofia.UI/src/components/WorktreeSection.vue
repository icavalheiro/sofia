<script setup lang="ts">
// Worktree switcher inside a folder workspace's gear menu.
//
// Lists the repository's worktrees, creates and removes them
// (`/api/worktrees`) and switches to one by focusing the workspace that points
// at its directory — creating that workspace when the worktree is new.
import { onMounted, ref } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useSound } from '@/composables/useSound'
import { useI18n } from '@/i18n'
import {
  createWorktree,
  listWorktrees,
  removeWorktree,
  samePath,
  worktreeName,
  type WorktreeEntry,
} from '@/services/worktrees'
import SofiaIcon from '@/components/SofiaIcon.vue'

import type { Workspace } from '@/types'

const props = defineProps<{ workspace: Workspace }>()
const emit = defineEmits<{ (e: 'close'): void }>()

const { openWorkspaceAt, removeWorkspace, state } = useHarness()
const { playSound } = useSound()
const { t } = useI18n()

const entries = ref<WorktreeEntry[]>([])
const name = ref('')
const loading = ref(true)
const creating = ref(false)
/** Directory of the worktree being removed right now. */
const removing = ref('')
/** Worktree whose removal needs `force` (modified or untracked files). */
const forceEntry = ref<WorktreeEntry | null>(null)
const error = ref<string | null>(null)

function failureMessage(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    const res = await listWorktrees(props.workspace.path)
    entries.value = res.worktrees
    if (!res.root && !res.worktrees.length) error.value = t('worktree.noProject')
  } catch (e) {
    error.value = failureMessage(e)
  } finally {
    loading.value = false
  }
}

/** Focuses the workspace for this worktree and closes the gear menu. */
function switchTo(entry: WorktreeEntry): void {
  playSound('select')
  void openWorkspaceAt(entry.directory)
  emit('close')
}

async function create(): Promise<void> {
  const wanted = name.value.trim()
  if (!wanted || creating.value) return
  creating.value = true
  error.value = null
  try {
    const created = await createWorktree(props.workspace.path, wanted)
    name.value = ''
    await load()
    switchTo(created)
  } catch (e) {
    error.value = failureMessage(e)
  } finally {
    creating.value = false
  }
}

async function remove(entry: WorktreeEntry, force = false): Promise<void> {
  if (removing.value) return
  removing.value = entry.directory
  error.value = null
  forceEntry.value = null
  try {
    await removeWorktree(props.workspace.path, entry.directory, force)
    // The directory is gone, so drop any workspace still pointing at it —
    // otherwise the tree offers a folder that no longer exists.
    const stale = state.workspaces.find((w) => w.kind !== 'chat' && samePath(w.path, entry.directory))
    if (stale) await removeWorkspace(stale.id)
    await load()
  } catch (e) {
    const text = failureMessage(e)
    // The server refuses to drop a dirty worktree and says so; offer the
    // destructive retry instead of hiding the reason.
    if (/force/i.test(text)) forceEntry.value = entry
    error.value = text
  } finally {
    removing.value = ''
  }
}

async function refresh(): Promise<void> {
  await load()
}

onMounted(() => void load())
</script>

<template>
  <div class="gear-section">
    <div class="gear-label wt-head">
      <span>{{ t('worktree.title') }}</span>
      <button
        class="wt-refresh"
        type="button"
        :title="t('worktree.refresh')"
        :aria-label="t('worktree.refresh')"
        @click="refresh"
      >
        <SofiaIcon name="refresh" :size="'0.8rem'" />
      </button>
    </div>

    <p v-if="loading" class="wt-note">{{ t('worktree.loading') }}</p>
    <p v-else-if="!entries.length" class="wt-note">{{ t('worktree.empty') }}</p>

    <div v-else class="wt-list">
      <div
        v-for="entry in entries"
        :key="entry.directory"
        class="wt-row"
        :class="{ 'wt-row--current': samePath(entry.directory, workspace.path) }"
      >
        <button
          class="wt-pick"
          type="button"
          :title="entry.branch ? `${entry.directory} · ${entry.branch}` : entry.directory"
          @click="switchTo(entry)"
        >
          <SofiaIcon :name="entry.isMain ? 'folder' : 'branch'" :size="'0.85rem'" />
          <span class="wt-name sofia-truncate">{{ worktreeName(entry.directory) }}</span>
          <span v-if="entry.isMain" class="wt-tag">{{ t('worktree.main') }}</span>
          <SofiaIcon
            v-else-if="samePath(entry.directory, workspace.path)"
            class="wt-check"
            name="check"
            :size="'0.8rem'"
          />
        </button>
        <button
          v-if="!entry.isMain"
          class="wt-remove"
          type="button"
          :title="t('worktree.remove')"
          :aria-label="t('worktree.remove')"
          :disabled="!!removing"
          @click="remove(entry)"
        >
          <SofiaIcon v-if="removing === entry.directory" name="loader" spin :size="'0.75rem'" />
          <SofiaIcon v-else name="trash" :size="'0.8rem'" />
        </button>
      </div>
    </div>

    <div class="wt-create">
      <input
        v-model="name"
        class="wt-input"
        type="text"
        :placeholder="t('worktree.placeholder')"
        @keyup.enter="create"
      />
      <button
        class="wt-add"
        type="button"
        :title="t('worktree.create')"
        :aria-label="t('worktree.create')"
        :disabled="!name.trim() || creating"
        @click="create"
      >
        <SofiaIcon name="plus" :size="'0.85rem'" />
      </button>
    </div>

    <p v-if="error" class="wt-error">{{ error }}</p>
    <button v-if="forceEntry" class="wt-force" type="button" @click="remove(forceEntry, true)">
      {{ t('worktree.force') }}
    </button>
  </div>
</template>

<style scoped>
.wt-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.wt-refresh {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: none;
  border-radius: 5px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.wt-refresh:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.wt-note {
  margin: 0;
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}

.wt-list {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  max-height: 168px;
  overflow-y: auto;
}

.wt-row {
  display: flex;
  align-items: center;
  gap: 0.15rem;
  border-radius: 7px;
}

.wt-row--current {
  background: var(--sofia-primary-soft);
}

.wt-pick {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  flex: 1;
  min-width: 0;
  padding: 0.35rem 0.45rem;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--sofia-text-muted);
  text-align: left;
  cursor: pointer;
}

.wt-pick:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.wt-name {
  flex: 1;
  min-width: 0;
  font-size: 0.8125rem;
}

.wt-tag {
  flex-shrink: 0;
  font-size: 0.625rem;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.wt-check {
  flex-shrink: 0;
  color: var(--sofia-primary);
}

.wt-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--sofia-text-faint);
  cursor: pointer;
}

.wt-remove:hover:not(:disabled) {
  background: var(--sofia-err-soft);
  color: var(--sofia-err);
}

.wt-remove:disabled {
  cursor: default;
}

.wt-create {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  margin-top: 0.35rem;
}

.wt-input {
  flex: 1;
  min-width: 0;
  padding: 0.3rem 0.45rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text);
  font-size: 0.8125rem;
  outline: none;
}

.wt-input:focus {
  border-color: var(--sofia-primary);
}

.wt-input::placeholder {
  color: var(--sofia-text-faint);
}

.wt-add {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  flex-shrink: 0;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.wt-add:not(:disabled):hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.wt-add:disabled {
  opacity: 0.5;
  cursor: default;
}

.wt-error {
  margin: 0.35rem 0 0;
  font-size: 0.7rem;
  color: var(--sofia-err);
  overflow-wrap: anywhere;
}

.wt-force {
  width: 100%;
  margin-top: 0.3rem;
  padding: 0.3rem 0.45rem;
  border: 1px solid var(--sofia-err);
  border-radius: 7px;
  background: var(--sofia-err-soft);
  color: var(--sofia-err);
  font-size: 0.75rem;
  cursor: pointer;
}
</style>
