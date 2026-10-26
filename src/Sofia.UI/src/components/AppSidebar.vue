<script setup lang="ts">
import { computed, nextTick, reactive, ref } from 'vue'

import logoUrl from '@/assets/logo.png'
import { useHarness } from '@/composables/useHarness'
import { useSound } from '@/composables/useSound'
import { sessionBadge as liveBadge } from '@/composables/useSessionStatus'
import { triggerShortcut, useShortcut } from '@/composables/useShortcuts'
import { useI18n } from '@/i18n'
import SettingsPanel, { type SettingsTabId } from '@/components/SettingsPanel.vue'
import SofiaIcon from '@/components/SofiaIcon.vue'
import WorkspaceSettings from '@/components/WorkspaceSettings.vue'

const {
  state,
  activeWorkspace,
  activeConversation,
  selectConversation,
  toggleWorkspace,
  addWorkspace,
  addChatGroup,
  newConversation,
  newConversationIn,
  archiveConversation,
  pinConversation,
  reorderConversations,
  restoreConversation,
  deleteConversation,
} = useHarness()

const { playSound } = useSound()
const { t } = useI18n()

const showAdd = ref(false)
const addPath = ref('')
const addError = ref('')
const showSettings = ref(false)
const settingsTab = ref<SettingsTabId | undefined>(undefined)
const addInput = ref<HTMLInputElement | null>(null)

function openSettings(initialTab?: SettingsTabId): void {
  settingsTab.value = initialTab
  showSettings.value = true
}

const STATUS_ICON: Record<string, string> = {
  working: 'zap',
  done: 'check',
  permission: 'alert',
  question: 'help',
  error: 'x',
}

const STATUS_TITLE: Record<string, string> = {
  working: 'Sofia is working on a task…',
  done: 'Last task finished',
  permission: 'Waiting for your permission',
  question: 'Asking you a question',
  error: 'The last run failed',
}

type Ws = (typeof state.workspaces)[number]
type Conv = Ws['conversations'][number]

/** Subagent sessions (with a parent) open from their tool card, not the list. Pinned rows come first. */
function visibleConvs(ws: Ws): Conv[] {
  return ws.conversations
    .filter((c) => !c.parentId && !c.archived)
    .sort((a, b) => Number(!!b.pinned) - Number(!!a.pinned))
}

/** Archived conversations of a workspace — the "Archived" sub-folder's rows. */
function archivedConvs(ws: Ws): Conv[] {
  return ws.conversations.filter((c) => !c.parentId && c.archived)
}

/** Which workspaces have their "Archived" sub-folder open (UI-only state). */
const archiveOpen = reactive<Record<string, boolean>>({})

function toggleArchive(ws: Ws): void {
  playSound('click')
  archiveOpen[ws.id] = !archiveOpen[ws.id]
}

// --- conversation row actions -------------------------------------------------

/** Conversation waiting for the delete confirmation (null = modal closed). */
const pendingDelete = ref<{ conv: Conv; workspace: Ws } | null>(null)
const deleteBtn = ref<HTMLButtonElement | null>(null)
const deleting = ref(false)

function askDelete(conv: Conv, ws: Ws): void {
  playSound('click')
  pendingDelete.value = { conv, workspace: ws }
  void nextTick(() => deleteBtn.value?.focus())
}

function closeDelete(): void {
  if (deleting.value) return
  pendingDelete.value = null
}

async function confirmDelete(): Promise<void> {
  const target = pendingDelete.value
  if (!target || deleting.value) return
  deleting.value = true
  const err = await deleteConversation(target.conv.id)
  deleting.value = false
  if (!err) playSound('select')
  pendingDelete.value = null
}

async function archive(conv: Conv): Promise<void> {
  playSound('select')
  const err = await archiveConversation(conv.id)
  if (!err && conv.workspaceId) archiveOpen[conv.workspaceId] = true
}

async function restore(conv: Conv): Promise<void> {
  playSound('select')
  await restoreConversation(conv.id)
}

async function togglePin(conv: Conv): Promise<void> {
  playSound('select')
  await pinConversation(conv.id, !conv.pinned)
}

// --- drag to reorder (mouse) --------------------------------------------------

const dragId = ref<string | null>(null)
/** Row under the pointer and which half of it: the dragged row lands before or after it. */
const dropTarget = ref<{ id: string; after: boolean } | null>(null)

function onDragStart(c: Conv, event: DragEvent): void {
  dragId.value = c.id
  if (!event.dataTransfer) return
  event.dataTransfer.effectAllowed = 'move'
  event.dataTransfer.setData('text/plain', c.id)
}

function canDropOn(ws: Ws, target: Conv): boolean {
  const dragged = dragId.value ? visibleConvs(ws).find((c) => c.id === dragId.value) : undefined
  return !!dragged && dragged.id !== target.id && !!dragged.pinned === !!target.pinned
}

function onDragOver(ws: Ws, c: Conv, event: DragEvent): void {
  if (!canDropOn(ws, c)) return
  event.preventDefault()
  if (event.dataTransfer) event.dataTransfer.dropEffect = 'move'
  const rect = (event.currentTarget as HTMLElement).getBoundingClientRect()
  dropTarget.value = { id: c.id, after: event.clientY > rect.top + rect.height / 2 }
}

function onDrop(ws: Ws, c: Conv, event: DragEvent): void {
  if (!canDropOn(ws, c)) return
  event.preventDefault()
  const after = dropTarget.value?.after ?? false
  const ids = visibleConvs(ws).map((x) => x.id).filter((id) => id !== dragId.value)
  ids.splice(ids.indexOf(c.id) + (after ? 1 : 0), 0, dragId.value as string)
  endDrag()
  playSound('select')
  void reorderConversations(ws.id, ids)
}

function endDrag(): void {
  dragId.value = null
  dropTarget.value = null
}

/** Opens a row. Enter on one of the row's own buttons must not also select it. */
function openRow(ws: Ws, c: Conv, event?: KeyboardEvent): void {
  if (event && event.target !== event.currentTarget) return
  void selectConversation(ws.id, c.id)
}

/** Conversations flattened depth-first: subagent sessions nest under their
 *  parent, but only while running or while the user is viewing them (or one of
 *  their descendants). Subagents whose parent is not in the workspace stay hidden. */
function convTree(ws: Ws): { conv: Conv; depth: number }[] {
  const byParent = new Map<string, Conv[]>()
  for (const c of ws.conversations) {
    if (!c.parentId) continue
    const siblings = byParent.get(c.parentId) ?? []
    siblings.push(c)
    byParent.set(c.parentId, siblings)
  }
  const showChild = (c: Conv, visited: Set<string>): boolean => {
    if (visited.has(c.id)) return false
    visited.add(c.id)
    if (c.id === state.activeConversationId || convStatus(c) === 'working') return true
    return (byParent.get(c.id) ?? []).some((child) => showChild(child, visited))
  }
  const rows: { conv: Conv; depth: number }[] = []
  const seen = new Set<string>()
  const walk = (c: Conv, depth: number): void => {
    if (seen.has(c.id)) return
    seen.add(c.id)
    rows.push({ conv: c, depth })
    for (const child of byParent.get(c.id) ?? []) {
      if (showChild(child, new Set())) walk(child, depth + 1)
    }
  }
  for (const c of visibleConvs(ws)) walk(c, 0)
  return rows
}

/** Folder-backed workspaces vs. chat groups (conversations without a folder). */
const folderWorkspaces = computed(() => state.workspaces.filter((w) => w.kind !== 'chat'))
const chatGroups = computed(() => state.workspaces.filter((w) => w.kind === 'chat'))

/** Sidebar sections rendered in order: workspaces first, then chat groups. */
const sections = computed(() => [
  { key: 'workspaces', items: folderWorkspaces.value },
  { key: 'chats', items: chatGroups.value },
])

/** A workspace row shows the highest-priority state of its conversations. */
function wsStatus(ws: Ws): string {
  const badges = visibleConvs(ws).map(convStatus)
  for (const status of ['permission', 'question', 'error', 'working', 'done']) {
    if (badges.includes(status)) return status
  }
  return 'idle'
}

function statusIcon(status: string): string {
  return STATUS_ICON[status] ?? 'check'
}

function statusTitle(status: string): string {
  return STATUS_TITLE[status] ?? ''
}

/** Per-conversation status shown on the row when its group is expanded.
 *  Several conversations can be working at the same time. The store's status
 *  (events + optimistic sends) wins; the server snapshot fills the gaps. */
function convStatus(c: Conv): string {
  if (c.working) return 'working'
  if (c.status !== 'idle') return c.status
  return liveBadge(c.id) ?? 'idle'
}

/** Absolute path: Windows drive (D:/…) or POSIX root (/…). */
function isValidPath(path: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(path) || /^[\\/]/.test(path)
}

async function submitAdd(): Promise<void> {
  const value = addPath.value.trim()
  if (!value) {
    addError.value = t('nav.invalidPath')
    return
  }
  if (!isValidPath(value)) {
    addError.value = t('nav.invalidPath')
    return
  }
  addError.value = ''
  const err = await addWorkspace(value)
  if (err) {
    addError.value = err
    return
  }
  addPath.value = ''
  showAdd.value = false
}

// --- keyboard shortcuts -------------------------------------------------------
useShortcut('session.new', () => {
  void newConversation().then(() => triggerShortcut('composer.focus'))
})

useShortcut('session.archive', () => {
  const conv = activeConversation.value
  if (conv && !conv.archived) void archive(conv)
})

useShortcut('workspace.add', () => {
  showAdd.value = true
  addError.value = ''
  void nextTick(() => addInput.value?.focus())
})

useShortcut('settings.open', () => openSettings())
useShortcut('shortcuts.show', () => openSettings('shortcuts'))

/** Moves the selection through the sidebar's visible sessions, wrapping around. */
function stepSession(step: 1 | -1): void {
  const flat = sections.value.flatMap((s) => s.items.flatMap((ws) => visibleConvs(ws).map((c) => ({ ws, c }))))
  if (!flat.length) return
  const at = flat.findIndex((x) => x.c.id === state.activeConversationId)
  const next = at < 0 ? (step > 0 ? 0 : flat.length - 1) : (at + step + flat.length) % flat.length
  const target = flat[next]
  if (target) void selectConversation(target.ws.id, target.c.id)
}

useShortcut('session.prev', () => stepSession(-1))
useShortcut('session.next', () => stepSession(1))
</script>

<template>
  <aside class="sidebar">
    <header class="brand">
      <img class="brand-logo" :src="logoUrl" alt="Sofia logo" />
      <div class="brand-text">
        <div class="brand-name">Sofia</div>
        <div class="brand-tag">{{ t('brand.tag') }}</div>
      </div>
      <button
        class="brand-gear"
        type="button"
        :aria-label="t('nav.settings')"
        :title="t('nav.settings')"
        @click="playSound('click'); openSettings()"
      >
        <SofiaIcon name="gear" :size="'1rem'" />
      </button>
    </header>

    <div class="actions">
      <button class="action-btn action-btn--primary" type="button" @click="playSound('click'); newConversation()">
        <SofiaIcon name="chat" :size="'0.95rem'" />
        {{ t('nav.newChat') }}
      </button>
      <button class="action-btn" type="button" @click="playSound('click'); showAdd = !showAdd; addError = ''">
        <SofiaIcon name="plus" :size="'0.95rem'" />
        {{ t('nav.workspace') }}
      </button>
    </div>

    <div v-if="showAdd" class="add-form">
      <input
        ref="addInput"
        v-model="addPath"
        class="add-input"
        :class="{ 'add-input--error': !!addError }"
        :placeholder="t('nav.addPlaceholder')"
        @input="addError = ''"
        @keyup.enter="submitAdd"
      />
      <button class="add-btn" type="button" @click="playSound('click'); submitAdd()">{{ t('nav.add') }}</button>
    </div>
    <p v-if="addError" class="add-error">{{ addError }}</p>

    <div class="workspaces">
      <template v-for="section in sections" :key="section.key">
        <div class="section-label" :class="{ 'section-label--split': section.key === 'chats' }">
          <span>{{ section.key === 'workspaces' ? t('nav.workspaces') : t('nav.chats') }}</span>
          <button
            v-if="section.key === 'chats'"
            class="section-add"
            type="button"
            :title="t('nav.newGroup')"
            :aria-label="t('nav.newGroup')"
            @click="playSound('click'); addChatGroup()"
          >
            <SofiaIcon name="plus" :size="'0.85rem'" />
          </button>
        </div>

        <div v-for="ws in section.items" :key="ws.id" class="workspace">
          <div
            class="ws-row"
            :class="{ 'ws-row--active': ws.id === activeWorkspace?.id, 'ws-row--working': wsStatus(ws) === 'working' && !ws.expanded }"
            role="button"
            tabindex="0"
            @click="playSound('click'); toggleWorkspace(ws.id)"
            @keydown.enter="toggleWorkspace(ws.id)"
          >
            <SofiaIcon class="ws-caret" :name="ws.expanded ? 'chevronDown' : 'chevronRight'" />
            <!-- The workspace's state replaces the folder icon; hovering shows the row actions instead. -->
            <span class="ws-slot">
              <span
                v-if="wsStatus(ws) !== 'idle'"
                class="ws-status"
                :class="`ws-status--${wsStatus(ws)}`"
                :title="statusTitle(wsStatus(ws))"
              >
                <SofiaIcon v-if="wsStatus(ws) === 'working'" name="loader" spin :size="'0.8rem'" />
                <SofiaIcon v-else :name="statusIcon(wsStatus(ws))" :size="'0.8rem'" />
              </span>
              <SofiaIcon v-else class="ws-icon" :name="ws.icon ?? 'folder'" />
            </span>
            <span class="ws-name sofia-truncate">{{ ws.name }}</span>
            <span class="ws-trail">
              <span class="ws-count">{{ visibleConvs(ws).length }}</span>
              <span class="ws-actions">
                <button
                  class="ws-add"
                  type="button"
                  aria-label="New chat in this workspace"
                  title="New chat in this workspace"
                  @click.stop="playSound('click'); newConversationIn(ws.id)"
                >
                  <SofiaIcon name="plus" :size="'0.875rem'" />
                </button>

                <span class="ws-gear-wrap">
                  <WorkspaceSettings :workspace="ws" />
                </span>
              </span>
            </span>
          </div>

          <div v-if="ws.expanded" class="ws-convs">
            <div
              v-for="{ conv: c, depth } in convTree(ws)"
              :key="c.id"
              class="conv-row"
              :class="{
                'conv-row--active': c.id === state.activeConversationId,
                'conv-row--working': convStatus(c) === 'working',
                'conv-row--sub': depth > 0,
                'conv-row--dragging': c.id === dragId,
                'conv-row--drop-before': dropTarget?.id === c.id && !dropTarget.after,
                'conv-row--drop-after': dropTarget?.id === c.id && dropTarget.after,
              }"
              :style="depth > 0 ? { marginLeft: `${depth * 0.875}rem`, width: `calc(100% - ${depth * 0.875}rem)` } : undefined"
              :draggable="depth === 0"
              role="button"
              tabindex="0"
              @click="selectConversation(ws.id, c.id)"
              @keydown.enter="openRow(ws, c, $event)"
              @dragstart="depth === 0 && onDragStart(c, $event)"
              @dragover="depth === 0 && onDragOver(ws, c, $event)"
              @dragleave="dropTarget?.id === c.id && (dropTarget = null)"
              @drop="depth === 0 && onDrop(ws, c, $event)"
              @dragend="endDrag"
            >
              <!-- The conversation's state replaces the chat icon; hovering shows the row actions instead. -->
              <span class="conv-slot">
                <span
                  v-if="convStatus(c) !== 'idle'"
                  class="conv-status"
                  :class="`conv-status--${convStatus(c)}`"
                  :title="statusTitle(convStatus(c))"
                >
                  <SofiaIcon v-if="convStatus(c) === 'working'" name="loader" spin :size="'0.75rem'" />
                  <SofiaIcon v-else :name="statusIcon(convStatus(c))" :size="'0.75rem'" />
                </span>
                <SofiaIcon v-else class="conv-icon" :name="depth > 0 ? 'bot' : 'chat'" :size="'0.875rem'" />
              </span>
              <span class="conv-title sofia-truncate">{{ c.title }}</span>
              <span class="conv-trail">
                <SofiaIcon v-if="c.pinned" class="conv-pin" name="pin" :size="'0.75rem'" />
                <span class="conv-actions">
                  <button
                    v-if="depth === 0"
                    class="conv-action"
                    type="button"
                    :aria-label="c.pinned ? t('conv.unpin') : t('conv.pin')"
                    :title="c.pinned ? t('conv.unpin') : t('conv.pin')"
                    @click.stop="togglePin(c)"
                  >
                    <SofiaIcon :name="c.pinned ? 'unpin' : 'pin'" :size="'0.8rem'" />
                  </button>
                  <button
                    class="conv-action"
                    type="button"
                    :aria-label="t('conv.archive')"
                    :title="t('conv.archive')"
                    @click.stop="archive(c)"
                  >
                    <SofiaIcon name="archive" :size="'0.8rem'" />
                  </button>
                  <button
                    class="conv-action conv-action--danger"
                    type="button"
                    :aria-label="t('conv.delete')"
                    :title="t('conv.delete')"
                    @click.stop="askDelete(c, ws)"
                  >
                    <SofiaIcon name="trash" :size="'0.8rem'" />
                  </button>
                </span>
              </span>
            </div>
            <div v-if="visibleConvs(ws).length === 0" class="conv-empty">{{ t('nav.noConvs') }}</div>

            <!-- Archived conversations: a sub-folder of this workspace / chat group. -->
            <template v-if="archivedConvs(ws).length">
              <div
                class="conv-row conv-row--folder"
                role="button"
                tabindex="0"
                :aria-expanded="!!archiveOpen[ws.id]"
                @click="toggleArchive(ws)"
                @keydown.enter="toggleArchive(ws)"
              >
                <SofiaIcon
                  class="conv-icon"
                  :name="archiveOpen[ws.id] ? 'chevronDown' : 'chevronRight'"
                  :size="'0.75rem'"
                />
                <SofiaIcon class="conv-icon conv-icon--archive" name="archive" :size="'0.875rem'" />
                <span class="conv-title sofia-truncate">{{ t('nav.archived') }}</span>
                <span class="conv-count">{{ archivedConvs(ws).length }}</span>
              </div>

              <div v-if="archiveOpen[ws.id]" class="conv-archived-list">
                <div
                  v-for="c in archivedConvs(ws)"
                  :key="`arch-${c.id}`"
                  class="conv-row conv-row--archived"
                  role="button"
                  tabindex="0"
                  @click="selectConversation(ws.id, c.id)"
                  @keydown.enter="openRow(ws, c, $event)"
                >
                  <SofiaIcon class="conv-icon" name="chat" :size="'0.875rem'" />
                  <span class="conv-title sofia-truncate">{{ c.title }}</span>
                  <span class="conv-trail">
                    <span class="conv-actions">
                      <button
                        class="conv-action"
                        type="button"
                        :aria-label="t('conv.restore')"
                        :title="t('conv.restore')"
                        @click.stop="restore(c)"
                      >
                        <SofiaIcon name="restore" :size="'0.8rem'" />
                      </button>
                      <button
                        class="conv-action conv-action--danger"
                        type="button"
                        :aria-label="t('conv.delete')"
                        :title="t('conv.delete')"
                        @click.stop="askDelete(c, ws)"
                      >
                        <SofiaIcon name="trash" :size="'0.8rem'" />
                      </button>
                    </span>
                  </span>
                </div>
              </div>
            </template>
          </div>
        </div>

        <div v-if="section.key === 'chats' && section.items.length === 0" class="conv-empty">
          {{ t('nav.noGroups') }}
        </div>
      </template>
    </div>

    <SettingsPanel :open="showSettings" :initial-tab="settingsTab" @close="showSettings = false" />

    <!-- Delete confirmation: destructive, so it needs an explicit yes. -->
    <Teleport to="body">
      <div
        v-if="pendingDelete"
        class="conv-del-overlay"
        @click.self="closeDelete"
        @keydown.esc="closeDelete"
      >
        <div class="conv-del-modal" role="alertdialog" aria-modal="true" aria-labelledby="conv-del-title">
          <h2 id="conv-del-title" class="conv-del-title">{{ t('conv.deleteTitle') }}</h2>
          <p class="conv-del-name sofia-truncate">{{ pendingDelete.conv.title }}</p>
          <p class="conv-del-text">{{ t('conv.deleteBody') }}</p>
          <div class="conv-del-actions">
            <button class="conv-del-btn" type="button" :disabled="deleting" @click="closeDelete">
              {{ t('conv.cancel') }}
            </button>
            <button
              ref="deleteBtn"
              class="conv-del-btn conv-del-btn--danger"
              type="button"
              :disabled="deleting"
              @click="confirmDelete"
            >
              {{ t('conv.deleteConfirm') }}
            </button>
          </div>
        </div>
      </div>
    </Teleport>
  </aside>
</template>

<style scoped>
.sidebar {
  width: var(--sofia-sidebar-width, 288px);
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  background: var(--sofia-surface);
  border-right: 1px solid var(--sofia-border);
  overflow: hidden;
}

/* --- brand -------------------------------------------------------------- */
.brand {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: var(--sofia-brand-pad, 1rem) var(--sofia-brand-pad, 1rem) 0.875rem;
  border-bottom: 1px solid var(--sofia-border);
}

.brand-logo {
  width: 40px;
  height: 40px;
  border-radius: 12px;
  object-fit: cover;
  border: 1px solid var(--sofia-border-strong);
  box-shadow: 0 0 0 3px var(--sofia-primary-soft);
}

.brand-name {
  font-size: 1.0625rem;
  font-weight: 650;
  letter-spacing: -0.01em;
  color: var(--sofia-text);
}

.brand-tag {
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}

.brand-gear {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border: none;
  border-radius: 9px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
  margin-left: auto;
  transition: background 0.12s ease, color 0.12s ease;
}

.brand-gear:hover {
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
}

/* --- actions ------------------------------------------------------------ */
.actions {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0.5rem;
  padding: 0.75rem 0.75rem 0.5rem;
}

.action-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.4rem;
  padding: 0.45rem 0.5rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 9px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-size: 0.8125rem;
  font-weight: 550;
  cursor: pointer;
  transition: background 0.12s ease, border-color 0.12s ease;
}

.action-btn:hover {
  background: var(--sofia-surface-3);
}

.action-btn--primary {
  background: var(--sofia-primary);
  border-color: var(--sofia-primary);
  color: var(--sofia-primary-ink);
}

.action-btn--primary:hover {
  background: var(--sofia-primary-strong);
}

.add-form {
  display: flex;
  gap: 0.4rem;
  padding: 0 0.75rem 0.5rem;
}

.add-input {
  flex: 1;
  min-width: 0;
  padding: 0.4rem 0.6rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-size: 0.8125rem;
  outline: none;
}

.add-input:focus {
  border-color: var(--sofia-primary);
}

.add-input::placeholder {
  color: var(--sofia-text-faint);
}

.add-btn {
  padding: 0.4rem 0.7rem;
  border: none;
  border-radius: 8px;
  background: var(--sofia-primary);
  color: var(--sofia-primary-ink);
  font-size: 0.8125rem;
  font-weight: 600;
  cursor: pointer;
}

.add-btn:hover {
  background: var(--sofia-primary-strong);
}

.add-input--error {
  border-color: var(--sofia-err);
}

.add-error {
  margin: 0;
  padding: 0 0.9rem 0.5rem;
  font-size: 0.75rem;
  color: var(--sofia-err);
}

/* --- workspace list ----------------------------------------------------- */
.workspaces {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 0.25rem 0.5rem 0.75rem;
}

.section-label {
  padding: 0.5rem 0.5rem 0.375rem;
  font-size: 0.6875rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

/* The Chats section sits below the workspaces, split off by a rule, with its
   own "new group" affordance on the right. */
.section-label--split {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.4rem;
  margin-top: 0.75rem;
  padding-top: 0.75rem;
  border-top: 1px solid var(--sofia-border);
}

.section-add {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.section-add:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.workspace {
  margin-bottom: 0.125rem;
}

.ws-row {
  position: relative;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  padding: var(--sofia-row-pad, 0.4rem) 0.5rem;
  border-radius: 8px;
  color: var(--sofia-text-muted);
  cursor: pointer;
  transition: background 0.12s ease, color 0.12s ease;
}

.ws-row:hover {
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
}

.ws-row--active {
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
}

.ws-caret {
  color: var(--sofia-text-faint);
  flex-shrink: 0;
}

/* Left slot of a row: the status badge replaces the workspace / conversation
   icon, so it keeps the icon's place instead of trailing the title. */
.ws-slot,
.conv-slot {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
}

.ws-icon {
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.ws-name {
  flex: 1;
  min-width: 0;
  font-size: 0.875rem;
  font-weight: 550;
}

.ws-count {
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
  background: var(--sofia-surface-3);
  border-radius: 999px;
  padding: 0.05rem 0.4rem;
}

/* --- workspace status badge --------------------------------------------- */
.ws-status {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  flex-shrink: 0;
  border-radius: 999px;
}

.ws-status--done {
  background: var(--sofia-ok-soft);
  color: var(--sofia-ok);
}

.ws-status--permission {
  background: var(--sofia-warn-soft);
  color: var(--sofia-warn);
}

.ws-status--question {
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

.ws-status--working {
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

.ws-status--error {
  background: var(--sofia-err-soft);
  color: var(--sofia-err);
}

/* --- "LLM working" animated traveling border ---------------------------- */
@keyframes sofia-working-sweep {
  to {
    --wb-angle: 360deg;
  }
}

.ws-row--working::before {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: 8px;
  padding: 1.5px;
  background: conic-gradient(
    from var(--wb-angle),
    transparent 0 58%,
    var(--sofia-primary) 78%,
    #ffffff 90%,
    var(--sofia-detail) 96%,
    transparent 100%
  );
  -webkit-mask:
    linear-gradient(#000 0 0) content-box,
    linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask-composite: exclude;
  animation: sofia-working-sweep 1.1s linear infinite;
  pointer-events: none;
}

.ws-row--working {
  background: var(--sofia-primary-soft);
  color: var(--sofia-text);
}

/* Count sits at the far right; on hover the actions overlay it (same grid
   cell), so the row never shifts. */
.ws-trail {
  display: inline-grid;
  align-items: center;
  justify-items: end;
  flex-shrink: 0;
  margin-left: auto;
}

.ws-count,
.ws-actions {
  grid-area: 1 / 1;
}

.ws-count {
  transition: opacity 0.12s ease;
}

.ws-actions {
  display: inline-flex;
  align-items: center;
  gap: 0.125rem;
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.12s ease;
}

.ws-row:hover .ws-count,
.ws-row:focus-within .ws-count {
  opacity: 0;
}

.ws-row:hover .ws-actions,
.ws-row:focus-within .ws-actions {
  opacity: 1;
  pointer-events: auto;
}

.ws-add,
.ws-gear-wrap {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
}

.ws-add {
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.ws-add:hover {
  background: var(--sofia-primary-soft-strong);
  color: var(--sofia-primary);
}

.ws-convs {
  margin: 0.125rem 0 0.25rem 1.375rem;
  padding-left: 0.5rem;
  border-left: 1px solid var(--sofia-border);
}

.conv-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  padding: 0.35rem 0.5rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
  text-align: left;
  transition: background 0.12s ease, color 0.12s ease;
}

.conv-row:focus-visible {
  outline: 1px solid var(--sofia-primary);
  outline-offset: -1px;
}

.conv-row:hover {
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
}

.conv-row--active {
  background: var(--sofia-primary-soft);
  color: var(--sofia-text);
}

.conv-icon {
  flex-shrink: 0;
  opacity: 0.75;
}

.conv-title {
  flex: 1;
  min-width: 0;
  font-size: 0.8125rem;
}

/* --- conversation status badge (visible while workspace is expanded) ---- */
.conv-status {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  flex-shrink: 0;
  border-radius: 999px;
}

.conv-status--done {
  background: var(--sofia-ok-soft);
  color: var(--sofia-ok);
}

.conv-status--permission {
  background: var(--sofia-warn-soft);
  color: var(--sofia-warn);
}

.conv-status--question,
.conv-status--working {
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

.conv-status--error {
  background: var(--sofia-err-soft);
  color: var(--sofia-err);
}

/* --- hover action pill on conversation rows ------------------------------- */
/* The actions pill sits at the far right; it appears on hover (or keyboard
   focus inside) over the empty trail, so the row never shifts. */
.conv-trail {
  display: inline-grid;
  align-items: center;
  justify-items: end;
  flex-shrink: 0;
  margin-left: auto;
}

.conv-actions {
  grid-area: 1 / 1;
  display: inline-flex;
  align-items: center;
  gap: 0.1rem;
  padding: 0.1rem;
  border-radius: 999px;
  background: var(--sofia-surface-3);
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.25);
  opacity: 0;
  pointer-events: none;
  transition: opacity 0.12s ease;
}

.conv-row:hover .conv-actions,
.conv-row:focus-within .conv-actions {
  opacity: 1;
  pointer-events: auto;
}

.conv-action {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  border: none;
  border-radius: 999px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.conv-action:hover {
  background: var(--sofia-primary-soft-strong);
  color: var(--sofia-primary);
}

.conv-action--danger:hover {
  background: var(--sofia-err-soft);
  color: var(--sofia-err);
}

/* Pin marker: shares the trail cell with the hover actions and yields to them. */
.conv-pin {
  grid-area: 1 / 1;
  color: var(--sofia-primary);
  opacity: 0.8;
}

.conv-row:hover .conv-pin,
.conv-row:focus-within .conv-pin {
  opacity: 0;
}

/* Drag to reorder. */
.conv-row--dragging {
  opacity: 0.4;
}

.conv-row--drop-before {
  box-shadow: 0 -2px 0 0 var(--sofia-primary);
}

.conv-row--drop-after {
  box-shadow: 0 2px 0 0 var(--sofia-primary);
}

/* --- archived sub-folder of a workspace / chat group ---------------------- */
.conv-row--folder .conv-icon--archive {
  color: var(--sofia-text-faint);
}

.conv-count {
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
  background: var(--sofia-surface-3);
  border-radius: 999px;
  padding: 0.05rem 0.4rem;
}

/* Archived rows nest under the folder row, one level deeper. */
.conv-archived-list {
  margin-left: 0.875rem;
  padding-left: 0.25rem;
  border-left: 1px solid var(--sofia-border);
}

.conv-row--archived {
  opacity: 0.72;
}

.conv-row--archived:hover,
.conv-row--archived:focus-within {
  opacity: 1;
}

.conv-row--archived .conv-icon {
  color: var(--sofia-text-faint);
}

/* Traveling border for the active conversation while replying. */
.conv-row--working {
  position: relative;
}

.conv-row--working::before {
  content: '';
  position: absolute;
  inset: 0;
  border-radius: 8px;
  padding: 1.5px;
  background: conic-gradient(
    from var(--wb-angle),
    transparent 0deg,
    var(--sofia-primary) 60deg,
    var(--sofia-detail) 120deg,
    transparent 180deg
  );
  -webkit-mask:
    linear-gradient(#000 0 0) content-box,
    linear-gradient(#000 0 0);
  -webkit-mask-composite: xor;
  mask-composite: exclude;
  pointer-events: none;
  animation: sofia-working-sweep 1.4s linear infinite;
}

.conv-row--sub .conv-icon {
  color: var(--sofia-primary);
  opacity: 0.9;
}

.conv-empty {
  padding: 0.35rem 0.5rem;
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}

/* --- delete confirmation modal (teleported to <body>) --------------------- */
.conv-del-overlay {
  position: fixed;
  inset: 0;
  z-index: 2100;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  background: rgba(0, 0, 0, 0.45);
}

.conv-del-modal {
  width: min(400px, 100%);
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  padding: 1rem;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 14px;
  box-shadow: 0 24px 60px rgba(0, 0, 0, 0.5);
}

.conv-del-title {
  margin: 0;
  font-size: 0.9375rem;
  font-weight: 600;
  color: var(--sofia-err);
}

.conv-del-name {
  margin: 0;
  font-size: 0.8125rem;
  font-weight: 550;
  color: var(--sofia-text);
}

.conv-del-text {
  margin: 0;
  font-size: 0.8125rem;
  line-height: 1.5;
  color: var(--sofia-text-muted);
}

.conv-del-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 0.3rem;
}

.conv-del-btn {
  padding: 0.3rem 0.8rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
  cursor: pointer;
}

.conv-del-btn:hover:not(:disabled) {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.conv-del-btn--danger {
  border-color: var(--sofia-err);
  background: var(--sofia-err-soft);
  color: var(--sofia-err);
}

.conv-del-btn--danger:hover:not(:disabled) {
  border-color: var(--sofia-err);
  color: var(--sofia-err);
}

.conv-del-btn:disabled {
  cursor: wait;
  opacity: 0.6;
}
</style>
