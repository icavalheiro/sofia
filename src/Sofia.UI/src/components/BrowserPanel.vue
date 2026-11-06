<script setup lang="ts">
// Live browser panel: the headless Chromium that Sofia's `browser` tool drives,
// streamed as JPEG frames over `/api/sessions/{id}/browser/stream`. The user
// watches the agent work (its clicks show as a ring) and can drive the same
// page between tool calls; input is locked while the agent is acting.
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useI18n } from '@/i18n'
import { api, serverSocketUrl } from '@/services/sofia'
import SofiaIcon from '@/components/SofiaIcon.vue'

interface BrowserTab {
  index: number
  url: string
  active: boolean
}

interface BrowserMessage {
  type: 'state' | 'cursor' | 'error'
  url?: string
  title?: string
  loading?: boolean
  agentBusy?: boolean
  viewport?: { width: number; height: number }
  mode?: string
  tabs?: BrowserTab[]
  x?: number
  y?: number
  action?: string
  message?: string
  fatal?: boolean
}

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'close'): void }>()
const { t } = useI18n()
const { activeConversation } = useHarness()

const MODE_KEY = 'sofia.browser.mode'
const WIDTH_KEY = 'sofia.browser.width'
const POS_KEY = 'sofia.browser.pos'
const SIZE_KEY = 'sofia.browser.size'
const CONTROLS_KEY = 'sofia.browser.controlsOpen'
const DESKTOP_WIDTH = 1280

const sessionId = computed(() => activeConversation.value?.id ?? null)

const status = ref<'idle' | 'connecting' | 'live' | 'error'>('idle')
const errorText = ref('')
const view = reactive({
  url: 'about:blank',
  title: '',
  loading: false,
  agentBusy: false,
  viewport: { width: DESKTOP_WIDTH, height: 800 },
  tabs: [] as BrowserTab[],
})
const address = ref('')
const addressFocused = ref(false)
const storedMode = localStorage.getItem(MODE_KEY)
const initialMode = storedMode === 'fit' || storedMode === 'desktop' || storedMode === 'mobile' || storedMode === 'mobileLandscape' || storedMode === 'desktop1080'
  ? storedMode
  : (localStorage.getItem('sofia.browser.fit') === '1' ? 'fit' : 'desktop')
const mode = ref<'fit' | 'desktop' | 'mobile' | 'mobileLandscape' | 'desktop1080'>(initialMode as 'fit' | 'desktop' | 'mobile' | 'mobileLandscape' | 'desktop1080')
const panel = ref<HTMLElement | null>(null)
const storedSizeW = Number(localStorage.getItem(WIDTH_KEY)) || 560
const pos = ref({ x: 24, y: 24 })
const size = ref({ w: Math.min(900, Math.max(360, storedSizeW)), h: 480 })
let hasStoredPos = false
let hasStoredSize = false
try
{
  const rawPos = localStorage.getItem(POS_KEY)
  if (rawPos)
  {
    const p = JSON.parse(rawPos) as { x?: number; y?: number }
    if (typeof p.x === 'number') { pos.value.x = p.x; hasStoredPos = true }
    if (typeof p.y === 'number') { pos.value.y = p.y; hasStoredPos = true }
  }
  const rawSize = localStorage.getItem(SIZE_KEY)
  if (rawSize)
  {
    const s = JSON.parse(rawSize) as { w?: number; h?: number }
    if (typeof s.w === 'number') { size.value.w = Math.min(900, Math.max(360, s.w)); hasStoredSize = true }
    if (typeof s.h === 'number') { size.value.h = Math.max(240, s.h); hasStoredSize = true }
  }
} catch { /* ignore */ }
const controlsOpen = ref(localStorage.getItem(CONTROLS_KEY) !== '0')

const screen = ref<HTMLElement | null>(null)
const canvas = ref<HTMLCanvasElement | null>(null)
const stageSize = reactive({ width: 0, height: 0 })
const cursor = reactive({ x: 0, y: 0, key: 0, visible: false, action: '' })

let socket: WebSocket | null = null
let reconnectTimer: number | undefined
let resizeTimer: number | undefined
let cursorTimer: number | undefined
let lastMove = 0
let decoding = false
let pendingFrame: ArrayBuffer | null = null
let observer: ResizeObserver | null = null

const isBlank = computed(() => view.url === 'about:blank' || view.url === '')

// --- connection ----------------------------------------------------------------

function disconnect(): void {
  window.clearTimeout(reconnectTimer)
  const ws = socket
  socket = null
  if (ws) {
    ws.onclose = null
    ws.close()
  }
  status.value = 'idle'
}

function connect(): void {
  disconnect()
  const id = sessionId.value
  if (!props.open || !id) return
  status.value = 'connecting'
  errorText.value = ''
  const ws = new WebSocket(serverSocketUrl(`/api/sessions/${id}/browser/stream`))
  ws.binaryType = 'arraybuffer'
  socket = ws
  ws.onopen = () => sendResize()
  ws.onmessage = (event) => {
    if (typeof event.data === 'string') handleMessage(JSON.parse(event.data) as BrowserMessage)
    else drawFrame(event.data as ArrayBuffer)
  }
  ws.onclose = () => {
    if (socket !== ws) return
    socket = null
    if (status.value === 'error') return
    status.value = 'connecting'
    reconnectTimer = window.setTimeout(connect, 2000)
  }
}

function send(message: Record<string, unknown>): void {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message))
}

function handleMessage(msg: BrowserMessage): void {
  if (msg.type === 'state') {
    const wasBusy = view.agentBusy
    view.url = msg.url ?? view.url
    view.title = msg.title ?? ''
    view.loading = !!msg.loading
    view.agentBusy = !!msg.agentBusy
    if (msg.viewport) view.viewport = msg.viewport
    view.tabs = msg.tabs ?? []
    status.value = 'live'
    if (!addressFocused.value) address.value = isBlank.value ? '' : view.url
    // Resizes sent while the agent was acting were ignored by the server.
    if (wasBusy && !view.agentBusy) sendResize()
  } else if (msg.type === 'cursor') {
    window.clearTimeout(cursorTimer)
    cursor.x = msg.x ?? 0
    cursor.y = msg.y ?? 0
    cursor.action = msg.action ?? ''
    cursor.key += 1
    cursor.visible = true
    cursorTimer = window.setTimeout(() => (cursor.visible = false), 2500)
  } else if (msg.type === 'error') {
    const text = msg.message ?? ''
    errorText.value = text
    if (msg.fatal) {
      disconnect()
      status.value = 'error'
    } else {
      window.setTimeout(() => {
        if (errorText.value === text) errorText.value = ''
      }, 4000)
    }
  }
}

// --- drawing -------------------------------------------------------------------

function drawFrame(buffer: ArrayBuffer): void {
  pendingFrame = buffer
  if (decoding) return
  void drain()
}

async function drain(): Promise<void> {
  decoding = true
  try {
    while (pendingFrame) {
      const data = pendingFrame
      pendingFrame = null
      const bitmap = await createImageBitmap(new Blob([data], { type: 'image/jpeg' }))
      const el = canvas.value
      const ctx = el?.getContext('2d')
      if (el && ctx) {
        if (el.width !== bitmap.width || el.height !== bitmap.height) {
          el.width = bitmap.width
          el.height = bitmap.height
        }
        ctx.drawImage(bitmap, 0, 0)
      }
      bitmap.close()
    }
  } catch {
    // A corrupt frame is skipped; the next one repaints.
  } finally {
    decoding = false
  }
}

// --- layout --------------------------------------------------------------------

/** Where the page sits inside the stage (letterboxed, like object-fit: contain). */
const fitBox = computed(() => {
  const { width: vw, height: vh } = view.viewport
  const scale = Math.min(stageSize.width / vw, stageSize.height / vh) || 1
  return { scale, left: (stageSize.width - vw * scale) / 2, top: (stageSize.height - vh * scale) / 2 }
})

const cursorStyle = computed(() => ({
  left: `${fitBox.value.left + cursor.x * fitBox.value.scale}px`,
  top: `${fitBox.value.top + cursor.y * fitBox.value.scale}px`,
}))

function sendResize(): void {
  if (!stageSize.width || !stageSize.height) return
  let width = 0
  let height = 0
  switch (mode.value) {
    case 'fit':
      width = stageSize.width
      height = stageSize.height
      send({ type: 'resize', width: Math.round(width), height: Math.round(height) })
      return
    case 'desktop':
      send({ type: 'resize', mode: 'desktop' })
      return
    case 'mobile':
      send({ type: 'resize', mode: 'mobile' })
      return
    case 'mobileLandscape':
      send({ type: 'resize', mode: 'mobileLandscape' })
      return
    case 'desktop1080':
      send({ type: 'resize', mode: 'desktop1080' })
      return
  }
}

function measure(): void {
  const el = screen.value
  if (!el) return
  stageSize.width = el.clientWidth
  stageSize.height = el.clientHeight
  window.clearTimeout(resizeTimer)
  resizeTimer = window.setTimeout(sendResize, 200)
}

watch(screen, (el) => {
  observer?.disconnect()
  observer = null
  if (!el) return
  observer = new ResizeObserver(measure)
  observer.observe(el)
  measure()
})

watch(mode, (value) => {
  localStorage.setItem(MODE_KEY, value)
  sendResize()
})

// --- positioning / resizing ----------------------------------------------------

function clamp(): void
{
  const el = panel.value
  const parent = el?.offsetParent as HTMLElement | null
  if ( !parent ) return
  size.value.w = Math.max( 360, Math.min( parent.clientWidth - 24, size.value.w ) )
  size.value.h = Math.max( 240, Math.min( parent.clientHeight - 24, size.value.h ) )
  pos.value.x = Math.max( 0, Math.min( parent.clientWidth - size.value.w, pos.value.x ) )
  pos.value.y = Math.max( 0, Math.min( parent.clientHeight - size.value.h, pos.value.y ) )
}

function loadPos(): void
{
  try
  {
    const raw = localStorage.getItem( POS_KEY )
    if ( !raw ) return
    const parsed = JSON.parse( raw ) as { x?: number; y?: number }
    if ( typeof parsed.x === 'number' ) pos.value.x = parsed.x
    if ( typeof parsed.y === 'number' ) pos.value.y = parsed.y
    hasStoredPos = true
  } catch { /* ignore */ }
}

function persistPos(): void
{
  try { localStorage.setItem( POS_KEY, JSON.stringify( pos.value ) ) } catch { /* ignore */ }
}

function loadSize(): void
{
  try
  {
    const raw = localStorage.getItem( SIZE_KEY )
    if ( !raw ) return
    const parsed = JSON.parse( raw ) as { w?: number; h?: number }
    if ( typeof parsed.w === 'number' ) size.value.w = Math.min( 900, Math.max( 360, parsed.w ) )
    if ( typeof parsed.h === 'number' ) size.value.h = Math.max( 240, parsed.h )
    hasStoredSize = true
  } catch { /* ignore */ }
}

function persistSize(): void
{
  try { localStorage.setItem( SIZE_KEY, JSON.stringify( size.value ) ) } catch { /* ignore */ }
  try { localStorage.setItem( WIDTH_KEY, String( size.value.w ) ) } catch { /* ignore */ }
}

let dragStart = { x: 0, y: 0, px: 0, py: 0 }
let dragging = false

function startDrag ( event: PointerEvent ): void
{
  const target = event.target as HTMLElement
  if ( target.closest( 'button, input, textarea, [data-no-drag]' ) ) return
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

let resizeStart = { x: 0, y: 0, w: 0, h: 0 }
let resizing = false

function startResize ( event: PointerEvent ): void
{
  event.preventDefault()
  event.stopPropagation()
  resizing = true
  resizeStart = { x: event.clientX, y: event.clientY, w: size.value.w, h: size.value.h }
  window.addEventListener( 'pointermove', onResizeMove )
  window.addEventListener( 'pointerup', onResizeEnd )
}

function onResizeMove ( event: PointerEvent ): void
{
  if ( !resizing ) return
  size.value.w = Math.max( 360, resizeStart.w + ( event.clientX - resizeStart.x ) )
  size.value.h = Math.max( 240, resizeStart.h + ( event.clientY - resizeStart.y ) )
  clamp()
}

function onResizeEnd (): void
{
  resizing = false
  window.removeEventListener( 'pointermove', onResizeMove )
  window.removeEventListener( 'pointerup', onResizeEnd )
  persistSize()
  // trigger measure
  window.setTimeout( () => measure(), 50 )
}

// --- user input ----------------------------------------------------------------

function toPage(event: MouseEvent): { x: number; y: number } | null {
  const el = screen.value
  if (!el) return null
  const rect = el.getBoundingClientRect()
  const { scale, left, top } = fitBox.value
  const x = (event.clientX - rect.left - left) / scale
  const y = (event.clientY - rect.top - top) / scale
  if (x < 0 || y < 0 || x > view.viewport.width || y > view.viewport.height) return null
  return { x, y }
}

const buttonName = (event: MouseEvent): string => (event.button === 2 ? 'right' : event.button === 1 ? 'middle' : 'left')

function onMouseDown(event: MouseEvent): void {
  screen.value?.focus()
  const p = toPage(event)
  if (p && !view.agentBusy) send({ type: 'mouse', action: 'down', button: buttonName(event), ...p })
}

function onMouseUp(event: MouseEvent): void {
  const p = toPage(event)
  if (p && !view.agentBusy) send({ type: 'mouse', action: 'up', button: buttonName(event), ...p })
}

function onMouseMove(event: MouseEvent): void {
  const now = performance.now()
  if (now - lastMove < 60 || view.agentBusy) return
  lastMove = now
  const p = toPage(event)
  if (p) send({ type: 'mouse', action: 'move', ...p })
}

function onWheel(event: WheelEvent): void {
  const p = toPage(event)
  if (p && !view.agentBusy) send({ type: 'mouse', action: 'wheel', deltaX: event.deltaX, deltaY: event.deltaY, ...p })
}

const MODIFIER_KEYS = new Set(['Control', 'Shift', 'Alt', 'Meta', 'AltGraph', 'CapsLock'])

function onKeyDown(event: KeyboardEvent): void {
  if (view.agentBusy || MODIFIER_KEYS.has(event.key) || event.isComposing) return
  // Let the browser raise a paste event; the text is forwarded from there.
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'v') return
  event.preventDefault()
  const key = event.key === ' ' ? 'Space' : event.key
  // AltGr reports Ctrl+Alt on Windows, but it only produces a character.
  const altGr = event.getModifierState('AltGraph')
  const mods = altGr
    ? []
    : [event.ctrlKey && 'Control', event.altKey && 'Alt', event.metaKey && 'Meta', event.shiftKey && key.length > 1 && 'Shift'].filter(Boolean)
  // A lone printable character is typed by the server; everything else is pressed as a combo.
  send({ type: 'key', key: mods.length ? [...mods, key].join('+') : key })
}

function onPaste(event: ClipboardEvent): void {
  const text = event.clipboardData?.getData('text')
  if (!text || view.agentBusy) return
  event.preventDefault()
  send({ type: 'text', text })
}

function navigate(): void {
  const value = address.value.trim()
  if (value) send({ type: 'navigate', url: value })
  ;(document.activeElement as HTMLElement | null)?.blur()
}

async function closeSession(): Promise<void> {
  const id = sessionId.value
  if (!id) return
  disconnect()
  try {
    await api('POST', `/api/sessions/${id}/browser/close`)
  } finally {
    view.url = 'about:blank'
    view.title = ''
    view.tabs = []
    emit('close')
  }
}

function tabLabel(tab: BrowserTab): string {
  try {
    return new URL(tab.url).host || tab.url
  } catch {
    return tab.url || 'about:blank'
  }
}

watch(
  () => [props.open, sessionId.value] as const,
  () => {
    view.url = 'about:blank'
    view.title = ''
    view.tabs = []
    view.agentBusy = false
    connect()
  },
  { immediate: true },
)

watch(controlsOpen, (v) => {
  try { localStorage.setItem(CONTROLS_KEY, v ? '1' : '0') } catch { /* ignore */ }
})

function applyDefaultPosition (el: HTMLElement | null = panel.value): void
{
  if (hasStoredPos) return
  const parent = (el?.offsetParent ?? el?.parentElement) as HTMLElement | null
  if (!parent) return
  pos.value.x = Math.max(16, parent.clientWidth - size.value.w - 24)
  pos.value.y = Math.max(16, parent.clientHeight - size.value.h - 24)
  if (!hasStoredSize)
  {
    size.value.w = Math.min(size.value.w, parent.clientWidth - 24)
    size.value.h = Math.min(size.value.h, parent.clientHeight - 24)
    hasStoredSize = true
  }
  hasStoredPos = true
  clamp()
}

onMounted(() => {
  loadPos()
  loadSize()
  applyDefaultPosition()
  window.addEventListener('resize', clamp)
})

watch(panel, (el) => {
  // The panel is mounted lazily (v-if on the root), so onMounted runs before
  // the element exists. Re-apply the default placement once it appears.
  if (!hasStoredPos) applyDefaultPosition(el)
}, { flush: 'post' })

onBeforeUnmount(() => {
  window.removeEventListener('resize', clamp)
  window.removeEventListener('pointermove', onDragMove)
  window.removeEventListener('pointerup', onDragEnd)
  window.removeEventListener('pointermove', onResizeMove)
  window.removeEventListener('pointerup', onResizeEnd)
  disconnect()
  observer?.disconnect()
  window.clearTimeout(resizeTimer)
  window.clearTimeout(cursorTimer)
})
</script>

<template>
  <div v-if="open" ref="panel" class="browser-float" :style="{ left: `${pos.x}px`, top: `${pos.y}px`, width: `${size.w}px`, height: `${size.h}px` }" aria-label="Live browser">
    <div class="bf-head" @pointerdown="startDrag">
      <SofiaIcon name="globe" :size="'1rem'" />
      <span class="bf-title sofia-truncate">{{ view.title || t('browser.title') }}</span>
      <div class="bf-head-side">
        <button class="bf-icon" type="button" title="Toggle controls" aria-label="Toggle controls" @click="controlsOpen = !controlsOpen">
          <SofiaIcon name="chevronDown" :size="'0.9rem'" :class="{ 'bf-icon--flipped': !controlsOpen }" />
        </button>
        <button class="bf-icon" type="button" :title="t('browser.closeSession')" :aria-label="t('browser.closeSession')" @click="closeSession">
          <SofiaIcon name="trash" :size="'0.9rem'" />
        </button>
        <button class="bf-icon" type="button" :title="t('panel.close')" :aria-label="t('panel.close')" @click="emit('close')">
          <SofiaIcon name="x" />
        </button>
      </div>
    </div>

    <div v-if="controlsOpen" class="bf-controls">
      <div class="bp-bar">
      <button class="bp-icon" type="button" :title="t('browser.back')" :aria-label="t('browser.back')" :disabled="view.agentBusy" @click="send({ type: 'back' })">
        <SofiaIcon name="chevronLeft" :size="'1rem'" />
      </button>
      <button class="bp-icon" type="button" :title="t('browser.forward')" :aria-label="t('browser.forward')" :disabled="view.agentBusy" @click="send({ type: 'forward' })">
        <SofiaIcon name="chevronRight" :size="'1rem'" />
      </button>
      <button class="bp-icon" type="button" :title="t('browser.reload')" :aria-label="t('browser.reload')" :disabled="view.agentBusy" @click="send({ type: 'reload' })">
        <SofiaIcon name="refresh" :size="'0.9rem'" :spin="view.loading" />
      </button>
      <input
        v-model="address"
        class="bp-address"
        type="text"
        spellcheck="false"
        autocomplete="off"
        :placeholder="t('browser.address')"
        :disabled="view.agentBusy || status !== 'live'"
        @focus="addressFocused = true; ($event.target as HTMLInputElement).select()"
        @blur="addressFocused = false; address = isBlank ? '' : view.url"
        @keydown.enter="navigate"
      />
      <button class="bp-icon" type="button" :title="t('browser.newTab')" :aria-label="t('browser.newTab')" :disabled="view.agentBusy || status !== 'live'" @click="send({ type: 'newTab' })">
        <SofiaIcon name="plus" :size="'1rem'" />
      </button>
      <button
        class="bp-fit"
        type="button"
        :class="{ 'bp-fit--on': mode === 'fit' }"
        :title="mode === 'fit' ? t('browser.fitOff') : t('browser.fitOn')"
        @click="mode = mode === 'fit' ? 'desktop' : 'fit'"
      >
        {{ mode === 'fit' ? t('browser.fit') : t('browser.desktop') }}
      </button>
      <button
        class="bp-fit"
        type="button"
        :class="{ 'bp-fit--on': mode === 'mobile' }"
        :title="t('browser.mobile')"
        @click="mode = 'mobile'"
      >
        {{ t('browser.mobile') }}
      </button>
      <button
        class="bp-fit"
        type="button"
        :class="{ 'bp-fit--on': mode === 'mobileLandscape' }"
        :title="t('browser.mobileLandscape')"
        @click="mode = 'mobileLandscape'"
      >
        {{ t('browser.mobileLandscape') }}
      </button>
      <button
        class="bp-fit"
        type="button"
        :class="{ 'bp-fit--on': mode === 'desktop1080' }"
        :title="t('browser.fullhd')"
        @click="mode = 'desktop1080'"
      >
        {{ t('browser.fullhd') }}
      </button>
    </div>

    <div v-if="view.tabs.length > 1" class="bp-tabs" role="tablist">
      <div v-for="tab in view.tabs" :key="tab.index" class="bp-tab" :class="{ 'bp-tab--active': tab.active }">
        <button class="bp-tab-label sofia-truncate" type="button" role="tab" :aria-selected="tab.active" :disabled="view.agentBusy" @click="send({ type: 'tab', index: tab.index })">
          {{ tabLabel(tab) }}
        </button>
        <button class="bp-tab-close" type="button" :aria-label="t('browser.closeTab')" :disabled="view.agentBusy" @click="send({ type: 'closeTab', index: tab.index })">
          <SofiaIcon name="x" :size="'0.7rem'" />
        </button>
      </div>
    </div>

    </div>

    <div class="bp-stage">
      <div
        ref="screen"
        class="bp-screen"
        :class="{ 'bp-screen--locked': view.agentBusy }"
        tabindex="0"
        @mousedown="onMouseDown"
        @mouseup="onMouseUp"
        @mousemove="onMouseMove"
        @wheel.prevent="onWheel"
        @contextmenu.prevent
        @keydown="onKeyDown"
        @paste="onPaste"
      >
        <canvas ref="canvas" class="bp-canvas" />
        <span v-if="cursor.visible" :key="cursor.key" class="bp-cursor" :class="`bp-cursor--${cursor.action}`" :style="cursorStyle" />
      </div>

      <div v-if="status === 'connecting'" class="bp-overlay">
        <SofiaIcon name="loader" spin :size="'1.2rem'" />
        <span>{{ t('browser.starting') }}</span>
      </div>
      <div v-else-if="status === 'error'" class="bp-overlay bp-overlay--error">
        <SofiaIcon name="alert" :size="'1.2rem'" />
        <span>{{ errorText || t('browser.failed') }}</span>
        <button class="bp-fit" type="button" @click="connect">{{ t('browser.retry') }}</button>
      </div>
      <div v-else-if="status === 'live' && isBlank && !view.agentBusy" class="bp-overlay bp-overlay--hint">
        <span>{{ t('browser.empty') }}</span>
      </div>
      <div v-if="view.agentBusy" class="bp-agent">
        <SofiaIcon name="sparkles" :size="'0.8rem'" />
        {{ t('browser.agent') }}
      </div>
      <p v-if="errorText && status === 'live'" class="bp-toast" role="status">{{ errorText }}</p>
    </div>
    <div class="bf-resize" @pointerdown="startResize" />
  </div>
</template>

<style scoped>
.browser-float {
  position: absolute;
  z-index: 40;
  display: flex;
  flex-direction: column;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.45);
  overflow: hidden;
  user-select: none;
  transition: box-shadow 0.2s ease;
}

.browser-float:hover {
  box-shadow: 0 20px 48px rgba(0, 0, 0, 0.55);
}

.bf-head {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.55rem 0.7rem;
  border-bottom: 1px solid var(--sofia-border);
  cursor: move;
  background: var(--sofia-surface-2);
  flex-shrink: 0;
}

.bf-title {
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--sofia-text);
  flex: 1;
  min-width: 0;
}

.bf-head-side {
  display: inline-flex;
  gap: 0.15rem;
  flex-shrink: 0;
}

.bf-icon {
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

.bf-icon:hover { background: var(--sofia-surface-3); color: var(--sofia-text); }

.bf-icon--flipped { transform: rotate(180deg); }

.bf-controls {
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
}

.bf-resize {
  position: absolute;
  right: 0;
  bottom: 0;
  width: 14px;
  height: 14px;
  cursor: nwse-resize;
  z-index: 5;
}

.bf-resize::after {
  content: '';
  position: absolute;
  right: 3px;
  bottom: 3px;
  width: 8px;
  height: 8px;
  border-right: 2px solid var(--sofia-text-faint);
  border-bottom: 2px solid var(--sofia-text-faint);
}

.bp-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.7rem 0.9rem 0.5rem;
  flex-shrink: 0;
}

.bp-title {
  display: inline-flex;
  align-items: center;
  gap: 0.45rem;
  min-width: 0;
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--sofia-text);
}

.bp-title svg {
  flex-shrink: 0;
  color: var(--sofia-primary);
}

.bp-head-side {
  display: inline-flex;
  gap: 0.15rem;
  flex-shrink: 0;
}

.bp-bar {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.45rem 0.7rem 0.55rem;
  border-bottom: 1px solid var(--sofia-border);
  flex-shrink: 0;
}

.bp-icon {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  flex-shrink: 0;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.bp-icon:hover:not(:disabled) {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.bp-icon:disabled,
.bp-address:disabled {
  opacity: 0.45;
  cursor: default;
}

.bp-address {
  flex: 1;
  min-width: 0;
  height: 28px;
  padding: 0 0.65rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 999px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-size: 0.75rem;
  outline: none;
}

.bp-address:focus {
  border-color: var(--sofia-primary);
}

.bp-fit {
  flex-shrink: 0;
  height: 26px;
  padding: 0 0.55rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text-muted);
  font-size: 0.6875rem;
  cursor: pointer;
}

.bp-fit:hover,
.bp-fit--on {
  color: var(--sofia-primary);
  border-color: var(--sofia-primary);
}

.bp-tabs {
  display: flex;
  gap: 0.25rem;
  padding: 0.4rem 0.7rem;
  overflow-x: auto;
  border-bottom: 1px solid var(--sofia-border);
  flex-shrink: 0;
}

.bp-tab {
  display: inline-flex;
  align-items: center;
  max-width: 11rem;
  border: 1px solid var(--sofia-border);
  border-radius: 8px;
  background: var(--sofia-surface-2);
}

.bp-tab--active {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
}

.bp-tab-label,
.bp-tab-close {
  border: none;
  background: transparent;
  color: var(--sofia-text-muted);
  font-size: 0.6875rem;
  cursor: pointer;
}

.bp-tab-label {
  padding: 0.25rem 0.2rem 0.25rem 0.5rem;
  min-width: 0;
}

.bp-tab-close {
  display: inline-flex;
  padding: 0.25rem 0.4rem 0.25rem 0.2rem;
}

.bp-stage {
  position: relative;
  flex: 1;
  min-height: 0;
  background: #111;
}

.bp-screen {
  position: absolute;
  inset: 0;
  outline: none;
  cursor: default;
}

.bp-screen:focus-visible {
  box-shadow: inset 0 0 0 2px var(--sofia-primary);
}

.bp-screen--locked {
  pointer-events: none;
}

.bp-canvas {
  width: 100%;
  height: 100%;
  object-fit: contain;
  display: block;
}

.bp-cursor {
  position: absolute;
  width: 26px;
  height: 26px;
  margin: -13px 0 0 -13px;
  border: 2px solid var(--sofia-primary);
  border-radius: 50%;
  background: color-mix(in srgb, var(--sofia-primary) 25%, transparent);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--sofia-primary) 25%, transparent);
  pointer-events: none;
  animation: bp-ping 0.9s ease-out;
}

@keyframes bp-ping {
  from {
    transform: scale(2.2);
    opacity: 0;
  }
  to {
    transform: scale(1);
    opacity: 1;
  }
}

.bp-overlay {
  position: absolute;
  inset: 0;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.6rem;
  padding: 1.5rem;
  background: rgba(17, 17, 17, 0.72);
  color: var(--sofia-text-muted);
  font-size: 0.8125rem;
  text-align: center;
}

.bp-overlay--hint {
  background: transparent;
  pointer-events: none;
  color: var(--sofia-text-faint);
}

.bp-overlay--error {
  color: var(--sofia-err);
}

.bp-agent {
  position: absolute;
  left: 50%;
  bottom: 0.8rem;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.3rem 0.7rem;
  border: 1px solid var(--sofia-primary);
  border-radius: 999px;
  background: color-mix(in srgb, var(--sofia-surface) 88%, transparent);
  color: var(--sofia-primary);
  font-size: 0.6875rem;
  font-weight: 600;
  pointer-events: none;
}

.bp-toast {
  position: absolute;
  left: 0.6rem;
  right: 0.6rem;
  top: 0.6rem;
  margin: 0;
  padding: 0.4rem 0.6rem;
  border-radius: 8px;
  background: color-mix(in srgb, var(--sofia-err) 22%, var(--sofia-surface));
  color: var(--sofia-text);
  font-size: 0.6875rem;
  pointer-events: none;
}
</style>
