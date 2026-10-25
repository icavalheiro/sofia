<script setup lang="ts">
// Hover gear for a workspace row: pick an icon, choose the workspace's default
// model, or remove the workspace. The panel is `position: fixed` (measured from
// the gear) so the sidebar's scroll container can't clip it.
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useSound } from '@/composables/useSound'
import { useI18n } from '@/i18n'
import { WORKSPACE_ICON_CHOICES, type Workspace } from '@/types'
import SofiaIcon from '@/components/SofiaIcon.vue'
import WorktreeSection from '@/components/WorktreeSection.vue'

const props = defineProps<{ workspace: Workspace }>()

const { models, setWorkspaceIcon, setWorkspaceModel, setWorkspaceName, removeWorkspace } = useHarness()
const { playSound } = useSound()
const { t } = useI18n()

const open = ref(false)
const root = ref<HTMLElement | null>(null)
const gearBtn = ref<HTMLButtonElement | null>(null)
const menuEl = ref<HTMLElement | null>(null)
const menuStyle = ref<Record<string, string>>({})

function measure(): void {
  const btn = gearBtn.value
  const menu = menuEl.value
  if (!btn || !menu) return
  const r = btn.getBoundingClientRect()
  const mw = menu.offsetWidth
  const mh = menu.offsetHeight
  let left = r.right - mw
  let top = r.bottom + 6
  if (top + mh > window.innerHeight - 8) top = Math.max(8, r.top - mh - 6)
  if (left < 8) left = 8
  if (left + mw > window.innerWidth - 8) left = window.innerWidth - mw - 8
  menuStyle.value = { top: `${top}px`, left: `${left}px` }
}

async function toggle(): Promise<void> {
  open.value = !open.value
  playSound('click')
  if (open.value) await nextTick(measure)
}

function chooseIcon(icon: string): void {
  setWorkspaceIcon(props.workspace.id, icon)
  playSound('select')
}

function chooseModel(modelId: string): void {
  setWorkspaceModel(props.workspace.id, modelId)
  playSound('select')
}

/** Live-rename the workspace or chat group from the menu's name field. */
function rename(event: Event): void {
  setWorkspaceName(props.workspace.id, (event.target as HTMLInputElement).value)
}

function remove(): void {
  open.value = false
  void removeWorkspace(props.workspace.id)
  playSound('click')
}

function onDocumentPointer(event: MouseEvent): void {
  // The menu is teleported to <body>, so it lives outside `root`. Count clicks
  // inside either the trigger or the menu as "inside" so interacting with the
  // menu (icon / model pick) doesn't close it.
  const target = event.target as Node
  const inRoot = root.value?.contains(target) ?? false
  const inMenu = menuEl.value?.contains(target) ?? false
  if (!inRoot && !inMenu) open.value = false
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') open.value = false
}

function onViewportChange(): void {
  if (open.value) measure()
}

onMounted(() => {
  document.addEventListener('mousedown', onDocumentPointer)
  document.addEventListener('keydown', onKeydown)
  window.addEventListener('resize', onViewportChange)
})

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocumentPointer)
  document.removeEventListener('keydown', onKeydown)
  window.removeEventListener('resize', onViewportChange)
})
</script>

<template>
  <div ref="root" class="ws-settings">
    <button
      ref="gearBtn"
      class="gear-btn"
      type="button"
      aria-label="Workspace settings"
      :aria-expanded="open"
      @click.stop="toggle"
    >
      <SofiaIcon name="gear" :size="'0.9rem'" />
    </button>

    <Teleport to="body">
      <div v-if="open" ref="menuEl" class="gear-menu" :style="menuStyle" @click.stop>
        <div class="gear-section">
          <div class="gear-label">{{ t('gear.name') }}</div>
          <input
            class="gear-name"
            type="text"
            :value="workspace.name"
            :placeholder="t('gear.name')"
            @input="rename"
            @keyup.enter="open = false"
          />
        </div>

        <div class="gear-divider" />

        <div class="gear-section">
          <div class="gear-label">{{ t('gear.icon') }}</div>
          <div class="icon-grid">
            <button
              v-for="ic in WORKSPACE_ICON_CHOICES"
              :key="ic"
              class="icon-cell"
              :class="{ 'icon-cell--active': workspace.icon === ic }"
              type="button"
              :aria-label="ic"
              @click="chooseIcon(ic)"
            >
              <SofiaIcon :name="ic" :size="'1rem'" />
            </button>
          </div>
        </div>

        <div class="gear-divider" />

        <div class="gear-section">
          <div class="gear-label">{{ t('gear.model') }}</div>
          <div class="model-list">
            <p v-if="!models.length" class="model-empty">No models available — add a provider in Settings.</p>
            <button
              v-for="m in models"
              :key="m.id"
              class="model-row"
              :class="{ 'model-row--active': workspace.modelId === m.id }"
              type="button"
              @click="chooseModel(m.id)"
            >
              <SofiaIcon :name="m.local ? 'cpu' : 'globe'" :size="'0.9rem'" />
              <span class="model-label sofia-truncate">{{ m.label }}</span>
              <SofiaIcon v-if="workspace.modelId === m.id" class="model-check" name="check" :size="'0.85rem'" />
            </button>
          </div>
        </div>

        <div class="gear-divider" />

        <!-- SOF-16: worktrees only exist for folder workspaces (a chat group has
             no directory and therefore no project). -->
        <WorktreeSection
          v-if="workspace.kind !== 'chat' && workspace.path"
          :workspace="workspace"
          @close="open = false"
        />

        <div v-if="workspace.kind !== 'chat' && workspace.path" class="gear-divider" />

        <button class="remove-row" type="button" @click="remove">
          <SofiaIcon name="trash" :size="'0.9rem'" />
          {{ workspace.kind === 'chat' ? t('gear.removeGroup') : t('gear.removeWorkspace') }}
        </button>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.ws-settings {
  position: relative;
  display: inline-flex;
}

.gear-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.gear-btn:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}
</style>

<style>
/* Unscoped: the menu is teleported to <body>. */
.gear-menu {
  position: fixed;
  z-index: 1000;
  width: 264px;
  padding: 0.4rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 20px 50px rgba(0, 0, 0, 0.5);
}

.gear-menu .gear-section {
  padding: 0.25rem 0.35rem;
}

.gear-menu .gear-label {
  font-size: 0.625rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
  margin-bottom: 0.4rem;
}

.gear-menu .gear-name {
  width: 100%;
  padding: 0.35rem 0.5rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text);
  font-size: 0.8125rem;
  outline: none;
}

.gear-menu .gear-name:focus {
  border-color: var(--sofia-primary);
}

.gear-menu .gear-name::placeholder {
  color: var(--sofia-text-faint);
}

.gear-menu .gear-divider {
  height: 1px;
  margin: 0.35rem 0;
  background: var(--sofia-border);
}

.gear-menu .icon-grid {
  display: grid;
  grid-template-columns: repeat(8, 1fr);
  gap: 0.25rem;
}

.gear-menu .icon-cell {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  aspect-ratio: 1;
  border: 1px solid transparent;
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.gear-menu .icon-cell:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.gear-menu .icon-cell--active {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

.gear-menu .model-list {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  max-height: 190px;
  overflow-y: auto;
}

.gear-menu .model-row {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  width: 100%;
  padding: 0.35rem 0.45rem;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--sofia-text-muted);
  text-align: left;
  cursor: pointer;
}

.gear-menu .model-row:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.gear-menu .model-row--active {
  background: var(--sofia-primary-soft);
  color: var(--sofia-text);
}

.gear-menu .model-label {
  flex: 1;
  min-width: 0;
  font-size: 0.8125rem;
}

.gear-menu .model-empty {
  margin: 0;
  padding: 0.2rem 0.45rem;
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}

.gear-menu .model-check {
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.gear-menu .remove-row {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  width: 100%;
  padding: 0.4rem 0.45rem;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--sofia-err);
  text-align: left;
  font-size: 0.8125rem;
  cursor: pointer;
}

.gear-menu .remove-row:hover {
  background: var(--sofia-err-soft);
}
</style>
