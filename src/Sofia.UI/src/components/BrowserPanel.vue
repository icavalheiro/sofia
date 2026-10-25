<script setup lang="ts">
// Live browser panel: the headless Chromium that Sofia's `browser` tool drives,
// streamed as JPEG frames over `/api/sessions/{id}/browser/stream`. The user
// watches the agent work (its clicks show as a ring) and can drive the same
// page between tool calls; input is locked while the agent is acting.
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'

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

const FIT_KEY = 'sofia.browser.fit'
const WIDTH_KEY = 'sofia.browser.width'
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
const fit = ref(localStorage.getItem(FIT_KEY) === '1')
const panelWidth = ref(Math.min(900, Math.max(360, Number(localStorage.getItem(WIDTH_KEY)) || 560)))

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
  const width = fit.value ? stageSize.width : DESKTOP_WIDTH
  const height = fit.value ? stageSize.height : Math.round((DESKTOP_WIDTH * stageSize.height) / stageSize.width)
  send({ type: 'resize', width: Math.round(width), height })
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

watch(fit, (value) => {
  localStorage.setItem(FIT_KEY, value ? '1' : '0')
  sendResize()
})

function startDrag(event: PointerEvent): void {
  const startX = event.clientX
  const startWidth = panelWidth.value
  const move = (e: PointerEvent): void => {
    panelWidth.value = Math.min(900, Math.max(360, startWidth + (startX - e.clientX)))
  }
  const up = (): void => {
    window.removeEventListener('pointermove', move)
    window.removeEventListener('pointerup', up)
    localStorage.setItem(WIDTH_KEY, String(panelWidth.value))
  }
  window.addEventListener('pointermove', move)
  window.addEventListener('pointerup', up)
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

onBeforeUnmount(() => {
  disconnect()
  observer?.disconnect()
  window.clearTimeout(resizeTimer)
  window.clearTimeout(cursorTimer)
})
</script>

<template>
  <aside v-if="open" class="browser-panel" :style="{ width: `${panelWidth}px` }" aria-label="Live browser">
    <div class="bp-grip" role="separator" aria-orientation="vertical" @pointerdown.prevent="startDrag" />

    <header class="bp-head">
      <div class="bp-title">
        <SofiaIcon name="globe" :size="'1rem'" />
        <span class="sofia-truncate">{{ view.title || t('browser.title') }}</span>
      </div>
      <div class="bp-head-side">
        <button class="bp-icon" type="button" :title="t('browser.closeSession')" :aria-label="t('browser.closeSession')" @click="closeSession">
          <SofiaIcon name="trash" :size="'0.9rem'" />
        </button>
        <button class="bp-icon" type="button" :title="t('panel.close')" :aria-label="t('panel.close')" @click="emit('close')">
          <SofiaIcon name="x" />
        </button>
      </div>
    </header>

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
      <button
        class="bp-fit"
        type="button"
        :class="{ 'bp-fit--on': fit }"
        :title="fit ? t('browser.fitOn') : t('browser.fitOff')"
        @click="fit = !fit"
      >
        {{ fit ? t('browser.fit') : t('browser.desktop') }}
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
  </aside>
</template>

<style scoped>
.browser-panel {
  position: relative;
  display: flex;
  flex-direction: column;
  flex-shrink: 0;
  min-height: 0;
  border-left: 1px solid var(--sofia-border);
  background: var(--sofia-surface);
  overflow: hidden;
}

.bp-grip {
  position: absolute;
  inset: 0 auto 0 0;
  z-index: 5;
  width: 5px;
  cursor: col-resize;
}

.bp-grip:hover {
  background: var(--sofia-primary-soft);
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
  padding: 0 0.7rem 0.55rem;
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
