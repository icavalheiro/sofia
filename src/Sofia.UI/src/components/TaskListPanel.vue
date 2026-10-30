<script setup lang="ts">
// Floating task list: shows the current conversation's task list (managed by the
// `tasks` tool) as a draggable card restricted to the chat viewspace. Its
// position is remembered across sessions in localStorage.
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useI18n } from '@/i18n'
import { api, onServerEvent } from '@/services/sofia'
import type { TaskDto, SofiaEvent } from '@/types'
import SofiaIcon from '@/components/SofiaIcon.vue'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'close'): void; (e: 'open'): void }>()

const { activeConversation } = useHarness()
const { t } = useI18n()

const STORAGE_POS = 'sofia.tasks.pos'

const panel = ref<HTMLElement | null>(null)
const tasks = ref<TaskDto[]>([])
const pos = ref({ x: 24, y: 24 })
let hasStoredPos = false

// --- loading ----------------------------------------------------------------

async function loadTasks (): Promise<void>
{
  const id = activeConversation.value?.id
  if ( !id ) { tasks.value = []; return }
  try
  {
    tasks.value = await api<TaskDto[]>( 'GET', `/api/sessions/${ id }/tasks` )
  } catch
  {
    tasks.value = []
  }
}

watch( () => activeConversation.value?.id, () => { void loadTasks() }, { immediate: true } )

function onServer ( event: SofiaEvent ): void
{
  const sid = event.sessionId ?? ( event.data as { sessionId?: string } | null )?.sessionId
  if ( event.type !== 'tasks.updated' || sid !== activeConversation.value?.id ) return
  const data = event.data as { tasks?: TaskDto[] } | null
  if ( data?.tasks )
  {
    const wasEmpty = tasks.value.length === 0
    tasks.value = data.tasks
    if ( wasEmpty && data.tasks.length > 0 && !props.open ) emit( 'open' )
  }
}
onServerEvent( onServer )

// --- positioning -------------------------------------------------------------

function clamp (): void
{
  const el = panel.value
  const parent = el?.offsetParent as HTMLElement | null
  if ( !el || !parent ) return
  const w = el.offsetWidth || 280
  const h = el.offsetHeight || 200
  pos.value.x = Math.max( 0, Math.min( parent.clientWidth - w, pos.value.x ) )
  pos.value.y = Math.max( 0, Math.min( parent.clientHeight - h, pos.value.y ) )
}

function loadPos (): void
{
  try
  {
    const raw = localStorage.getItem( STORAGE_POS )
    if ( !raw ) return
    const parsed = JSON.parse( raw ) as { x?: number; y?: number }
    if ( typeof parsed.x === 'number' ) pos.value.x = parsed.x
    if ( typeof parsed.y === 'number' ) pos.value.y = parsed.y
    hasStoredPos = true
  } catch
  {
    /* ignore */
  }
}

function persistPos (): void
{
  try { localStorage.setItem( STORAGE_POS, JSON.stringify( pos.value ) ) } catch { /* ignore */ }
}

let dragStart = { x: 0, y: 0, px: 0, py: 0 }
let dragging = false

function startDrag ( event: PointerEvent ): void
{
  const target = event.target as HTMLElement
  if ( target.closest( 'button' ) ) return
  dragging = true
  dragStart = { x: event.clientX, y: event.clientY, px: pos.value.x, py: pos.value.y }
  window.addEventListener( 'pointermove', onDragMove )
  window.addEventListener( 'pointerup', onDragEnd )
}

function onDragMove ( event: PointerEvent ): void
{
  if ( !dragging ) return
  pos.value.x = dragStart.px + ( event.clientX - dragStart.x )
  pos.value.y = dragStart.py + ( event.clientY - dragStart.y )
  clamp()
}

function onDragEnd (): void
{
  dragging = false
  window.removeEventListener( 'pointermove', onDragMove )
  window.removeEventListener( 'pointerup', onDragEnd )
  persistPos()
}

onMounted( () =>
{
  loadPos()
  // Default to the bottom-right of the chat viewspace when nothing was stored yet.
  if ( !hasStoredPos )
  {
    const parent = panel.value?.offsetParent as HTMLElement | null
    if ( parent )
    {
      pos.value.x = Math.max( 16, parent.clientWidth - 300 )
      pos.value.y = Math.max( 16, parent.clientHeight - 320 )
    }
  }
  clamp()
  window.addEventListener( 'resize', clamp )
} )

onBeforeUnmount( () =>
{
  window.removeEventListener( 'resize', clamp )
  window.removeEventListener( 'pointermove', onDragMove )
  window.removeEventListener( 'pointerup', onDragEnd )
} )

// Re-clamp when the list grows/shrinks.
watch( tasks, () => { requestAnimationFrame( clamp ) }, { deep: true } )

const statusIcon: Record<string, string> = {
  pending: 'circle',
  in_progress: 'loader',
  completed: 'check',
  cancelled: 'x',
}

const completed = computed( () => tasks.value.filter( ( t: TaskDto ) => t.status === 'completed' ).length )

// Auto-open is handled in the server-event handler below, so loading an existing
// task list while switching chats does not reopen a panel the user closed.
</script>

<template>
  <div
    v-if="open"
    ref="panel"
    class="task-float"
    :style="{ left: `${pos.x}px`, top: `${pos.y}px` }"
  >
    <header class="tf-head" @pointerdown="startDrag">
      <SofiaIcon name="list" :size="'0.9rem'" />
      <span class="tf-title">Tasks</span>
      <span v-if="tasks.length" class="tf-count">{{ completed }}/{{ tasks.length }}</span>
      <button class="tf-close" type="button" :aria-label="t( 'panel.close' )" @click="emit( 'close' )">
        <SofiaIcon name="x" :size="'0.8rem'" />
      </button>
    </header>

    <div class="tf-body">
      <p v-if="!tasks.length" class="tf-empty">No tasks yet.</p>
      <ul v-else class="tf-list">
        <li v-for="task in tasks" :key="task.id" class="tf-item">
          <SofiaIcon :name="statusIcon[ task.status ] || 'circle'" :size="'0.85rem'" :class="`tf-status tf-status--${ task.status }`" />
          <span class="tf-text" :class="{ 'tf-text--done': task.status === 'completed' }">{{ task.title }}</span>
        </li>
      </ul>
    </div>
  </div>
</template>

<style scoped>
.task-float {
  position: absolute;
  z-index: 40;
  width: 280px;
  max-height: 360px;
  display: flex;
  flex-direction: column;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgba( 0, 0, 0, 0.45 );
  overflow: hidden;
  user-select: none;
}

.tf-head {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.55rem 0.7rem;
  border-bottom: 1px solid var(--sofia-border);
  cursor: move;
  background: var(--sofia-surface-2);
}

.tf-title {
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--sofia-text);
}

.tf-count {
  padding: 0.05rem 0.4rem;
  border-radius: 999px;
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
  font-size: 0.6875rem;
  font-weight: 600;
}

.tf-close {
  margin-left: auto;
  border: none;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: 6px;
}

.tf-close:hover { background: var(--sofia-surface-3); color: var(--sofia-text); }

.tf-body {
  padding: 0.5rem 0.7rem 0.6rem;
  overflow-y: auto;
  min-height: 0;
}

.tf-empty {
  margin: 0;
  color: var(--sofia-text-faint);
  font-size: 0.75rem;
}

.tf-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.35rem;
}

.tf-item {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.75rem;
  color: var(--sofia-text);
}

.tf-text--done {
  text-decoration: line-through;
  color: var(--sofia-text-muted);
}

.tf-status { flex-shrink: 0; }
.tf-status--pending { color: var(--sofia-text-faint); }
.tf-status--in_progress { color: var(--sofia-primary); }
.tf-status--completed { color: var(--sofia-ok); }
.tf-status--cancelled { color: var(--sofia-err); }
</style>