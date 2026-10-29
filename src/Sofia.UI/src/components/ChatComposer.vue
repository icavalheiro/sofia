<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, type Ref, watch } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useShortcut } from '@/composables/useShortcuts'
import { api, useBackend } from '@/services/sofia'
import { hitPercent } from '@/services/cacheHealth'
import { useI18n } from '@/i18n'
import {
  PERMISSION_MODES,
  REASONING_MODES,
  type AgentDto,
  type CommandDto,
  type PermissionModeId,
  type ReasoningModeId,
  type UsageDto,
} from '@/types'
import SofiaIcon from '@/components/SofiaIcon.vue'
import SofiaPopover, { type PopoverItem } from '@/components/SofiaPopover.vue'
import AgentDropdown from '@/components/AgentDropdown.vue'

const {
  state,
  metrics,
  configActivity,
  models,
  activeModelId,
  activeConversation,
  activeWorkspace,
  patchActiveSession,
  addFiles,
  removeAttachment,
  sendMessage,
  sendSteered,
  runCommand,
  cancelReply,
} = useHarness()
const { state: backend } = useBackend()
const { t } = useI18n()
const fileInput = ref<HTMLInputElement | null>(null)
const root = ref<HTMLElement | null>(null)
const textarea = ref<HTMLTextAreaElement | null>(null)
// Popup list bodies ("/" palette and "@" agents); used to keep the active row visible.
const mentionList = ref<HTMLElement | null>(null)
const paletteList = ref<HTMLElement | null>(null)
const { rediscoverModels } = useHarness()
const modelPopover = ref<InstanceType<typeof SofiaPopover> | null>(null)
const permissionPopover = ref<InstanceType<typeof SofiaPopover> | null>(null)
const reasoningPopover = ref<InstanceType<typeof SofiaPopover> | null>(null)
const agentDropdown = ref<InstanceType<typeof AgentDropdown> | null>(null)

function focusComposer(): void {
  textarea.value?.focus()
}

useShortcut('agent.pick', () => agentDropdown.value?.open())
useShortcut('model.pick', () => modelPopover.value?.open())
useShortcut('permission.pick', () => permissionPopover.value?.open())
useShortcut('reasoning.pick', () => reasoningPopover.value?.open())
useShortcut('composer.focus', focusComposer)
useShortcut('session.stop', () => {
  if (state.isReplying) void cancelReply()
})

/** Workspace folder used to scope commands and VCS ('' for chat groups). */
const directory = computed(() => (activeWorkspace.value?.kind === 'folder' ? activeWorkspace.value.path : ''))

function query(dir: string): string {
  return dir ? `?directory=${encodeURIComponent(dir)}` : ''
}

// --- agent dropdown -----------------------------------------------------------
// Its own control (not part of the model popover): picking an agent PATCHes the
// session `agent`. The catalog is shared with the "@name" mention popup below.
const agentCatalog = ref<AgentDto[]>([])
// Agents the user can call with "@name" (subagent or both).
const subagents = computed(() => agentCatalog.value.filter((a) => a.mode !== 'primary'))

async function refreshAgents(): Promise<void> {
  try {
    agentCatalog.value = (await api<AgentDto[]>('GET', `/api/agents${query(directory.value)}`)) ?? []
  } catch {
    agentCatalog.value = []
  }
}

const activeAgentId = computed(() => activeConversation.value?.agent ?? '')

function selectAgent(id: string): void {
  void patchActiveSession({ agent: id })
}

// --- model chip ---------------------------------------------------------------
const modelItems = computed<PopoverItem[]>(() => {
  const items: PopoverItem[] = models.value
    .filter((m) => m.enabled !== false)
    .map((m) => ({
      value: m.id,
      label: m.label,
      description: [m.provider, m.context, m.connected ? '' : 'offline'].filter(Boolean).join(' · '),
      icon: m.local ? 'cpu' : 'globe',
      badge: m.local ? 'local' : undefined,
    }))
  const current = activeModelId.value
  if (current && !items.some((i) => i.value === current)) {
    items.unshift({ value: current, label: current, description: 'Not in the catalog', icon: 'globe' })
  }
  return items
})

const permissionItems: PopoverItem[] = PERMISSION_MODES.map((p) => ({
  value: p.id,
  label: p.label,
  description: p.description,
  icon: 'shield',
}))

const reasoningItems: PopoverItem[] = REASONING_MODES.map((r) => ({
  value: r.id,
  label: r.label,
  description: r.description,
  icon: 'zap',
}))

function onPermissionSelect(value: string): void {
  const mode = value as PermissionModeId
  if (!PERMISSION_MODES.some((p) => p.id === mode)) return
  state.permissionMode = mode
  void patchActiveSession({ permissionMode: mode })
}

function onReasoningSelect(value: string): void {
  const mode = value as ReasoningModeId
  if (!REASONING_MODES.some((r) => r.id === mode)) return
  state.reasoningMode = mode
  void patchActiveSession({ reasoningMode: mode })
}

const canSend = computed(() => state.composerText.trim().length > 0 || state.attachments.length > 0)

// --- git branch -----------------------------------------------------------------
const currentBranch = ref('')

async function refreshBranch(): Promise<void> {
  const dir = directory.value
  if (!dir) {
    currentBranch.value = ''
    return
  }
  try {
    const vcs = await api<{ isRepo: boolean; branch: string; branches: string[] }>('GET', `/api/vcs${query(dir)}`)
    if (dir !== directory.value) return
    currentBranch.value = vcs?.isRepo ? vcs.branch || vcs.branches?.[0] || '' : ''
  } catch {
    currentBranch.value = ''
  }
}

watch(
  () => backend.phase === 'ready' && directory.value,
  () => {
    if (backend.phase !== 'ready') return
    void refreshBranch()
    void refreshAgents()
  },
  { immediate: true },
)

// --- metrics row ----------------------------------------------------------------
function compactTokens(n: number): string {
  if (n < 1000) return String(n)
  if (n < 1_000_000) return `${(n / 1000).toFixed(n >= 100_000 ? 0 : 1)}K`
  return `${(n / 1_000_000).toFixed(1)}M`
}

/** Elapsed time in the largest unit that has started: 12.4s, 2.25m, 1.05h. */
function formatDuration(ms: number): string {
  const hours = ms / 3_600_000
  if (hours >= 1) return `${hours.toFixed(2)}h`
  const minutes = ms / 60_000
  if (minutes >= 1) return `${minutes.toFixed(2)}m`
  return `${(ms / 1000).toFixed(1)}s`
}

const costLabel = computed(() => `$${metrics.sessionCost.toFixed(4)}`)
const tpsLabel = computed(() => `${metrics.tokensPerSec} tok/s`)
// While the chat runs, the live clock; when it is idle, what its last request took (persisted on
// the session, so switching chats or reloading the client keeps the number).
const durationLabel = computed(() =>
  formatDuration(
    state.isReplying ? metrics.durationMs : activeConversation.value?.lastTurnMs ?? metrics.durationMs,
  ),
)

// Usage.input is the whole prompt (cached + uncached), so hit % = cacheRead / input.
// The metric never disappears: before the first request it reads 0% like the other
// zeroed readouts, and falls back to the session total when messages are not loaded.
const cacheStats = computed(() => {
  const conv = activeConversation.value
  const pct = (u?: { input: number; cacheRead: number }) =>
    u && u.input > 0 ? hitPercent(u.cacheRead, u.input) : null
  let last: UsageDto | undefined
  for (const m of conv?.messages ?? []) if (m.usage && m.usage.input > 0) last = m.usage
  const total = conv?.usage && conv.usage.input > 0 ? conv.usage : undefined
  const totalHit = pct(total)
  const hit = pct(last) ?? totalHit ?? 0
  const lines = [t('composer.hintCache')]
  if (last) {
    lines.push(
      `${t('composer.cacheLastStep')} ${hit}% ` +
        `(${last.cacheRead.toLocaleString()} / ${last.input.toLocaleString()} tok)`,
    )
  }
  if (total && totalHit !== null) {
    lines.push(
      `${t('composer.cacheSession')} ${totalHit}% ` +
        `(${total.cacheRead.toLocaleString()} / ${total.input.toLocaleString()} tok)`,
    )
  }
  return { hit, title: lines.join('\n') }
})
const contextLabel = computed(() =>
  metrics.contextLimit > 0 ? `${compactTokens(metrics.contextUsed)} / ${compactTokens(metrics.contextLimit)}` : '',
)

function iconForKind(kind: string): string {
  if (kind === 'image') return 'image'
  if (kind === 'video') return 'video'
  if (kind === 'audio') return 'music'
  return 'file'
}

function openFilePicker(): void {
  fileInput.value?.click()
}

function onFilesPicked(event: Event): void {
  const input = event.target as HTMLInputElement
  if (input.files?.length) addFiles(Array.from(input.files))
  input.value = '' // allow re-picking the same file
}

/** Handles Ctrl+V of images/videos from the clipboard: attach + insert a tag. */
function onPaste(event: ClipboardEvent): void {
  const items = event.clipboardData?.items
  if (!items) return

  const files: File[] = []
  const tags: string[] = []
  let n = 0

  for (const item of Array.from(items)) {
    if (item.kind !== 'file') continue
    const type = item.type
    const isImage = type.startsWith('image/')
    const isVideo = type.startsWith('video/')
    if (!isImage && !isVideo) continue
    const blob = item.getAsFile()
    if (!blob) continue

    n += 1
    const ext = (type.split('/')[1] || 'bin').split('+')[0]
    const word = isImage ? 'image' : 'video'
    const name = `pasted-${word}-${n}.${ext}`
    files.push(new File([blob], name, { type }))
    tags.push(`[${word}:${name}]`)
  }

  if (files.length === 0) return
  event.preventDefault()
  addFiles(files)
  insertTagsAtCursor(tags.join(' '))
}

function insertTagsAtCursor(text: string): void {
  const el = textarea.value
  const insertion = `${text} `
  const pos = el?.selectionStart ?? state.composerText.length
  state.composerText =
    state.composerText.slice(0, pos) + insertion + state.composerText.slice(pos)
  nextTick(() => {
    if (el) {
      const caret = pos + insertion.length
      el.selectionStart = el.selectionEnd = caret
    }
    autoResize()
  })
}

function autoResize(): void {
  const el = textarea.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${el.scrollHeight}px`
}

// Follow-ups while a reply is running: steer (mid-run) or queue (afterwards).
type Delivery = 'steer' | 'queue'
const DELIVERY_MODES: { id: Delivery; label: string; description: string; icon: string }[] = [
  { id: 'steer', label: 'Steer', description: 'Deliver mid-run: the working reply picks it up', icon: 'send' },
  { id: 'queue', label: 'Queue', description: 'Run afterwards, once the current reply ends', icon: 'clock' },
]
const steerDelivery = ref<Delivery>('steer')
const steering = ref(false)
const modeOpen = ref(false)
const modeRoot = ref<HTMLElement | null>(null)

const sendTitle = computed(() => {
  if (!state.isReplying) return t('composer.send')
  if (steering.value) return 'Sending…'
  return steerDelivery.value === 'steer' ? 'Send steer' : 'Send queued'
})

function pickDelivery(mode: Delivery): void {
  steerDelivery.value = mode
  modeOpen.value = false
}

function onDocumentPointer(event: MouseEvent): void {
  if (modeRoot.value && !modeRoot.value.contains(event.target as Node)) modeOpen.value = false
}

// The mode only exists while a reply runs; drop the popup when it ends.
watch(
  () => state.isReplying,
  (busy) => {
    if (!busy) modeOpen.value = false
  },
)

// --- "/" command palette ----------------------------------------------------------
const paletteOpen = ref(false)
const paletteIndex = ref(0)
const commands = ref<CommandDto[]>([])
const commandsLoading = ref(false)
const commandsLoaded = ref(false)

const paletteQuery = computed(() => {
  const text = state.composerText
  if (!text.startsWith('/')) return ''
  return text.slice(1).split(/\s/)[0]
})
const filteredCommands = computed(() => {
  const q = paletteQuery.value.toLowerCase()
  return commands.value.filter((c) => c.name.toLowerCase().includes(q))
})
watch(paletteQuery, () => {
  paletteIndex.value = 0
})
// Project commands depend on the folder: reload them for another workspace.
watch(directory, () => {
  commandsLoaded.value = false
  commands.value = []
})

// The server watches the config folders (its own home and every workspace's `.sofia` / `.agents`): drop the cached
// lists when they change, so a command or agent written a moment ago is usable without switching workspace or reloading.
watch(
  () => configActivity.tick,
  () => {
    commandsLoaded.value = false
    void refreshAgents()
    if (paletteOpen.value) void loadCommands(true)
  },
)

watch(
  () => state.composerText,
  (text) => {
    nextTick(autoResize)
    if (mentionOpen.value) nextTick(updateMention)
    if (/^\/\S*$/.test(text) && !state.isReplying) {
      const wasOpen = paletteOpen.value
      paletteOpen.value = true
      // Command files are added at any time: re-read them when the palette opens (and on `config.changed`).
      void loadCommands(!wasOpen)
    } else if (paletteOpen.value) {
      closePalette()
    }
  },
)

function closePalette(): void {
  paletteOpen.value = false
  paletteIndex.value = 0
}

// --- "@" agent mentions -----------------------------------------------------------
// Typing "@" at a word start lists the subagents; picking one inserts "@name ".
// The server hands the request to that agent with the `task` tool.
const mentionOpen = ref(false)
const mentionIndex = ref(0)
const mentionQuery = ref('')
let mentionRange: { start: number; end: number } | null = null

const filteredMentions = computed(() => {
  const q = mentionQuery.value.toLowerCase()
  return subagents.value.filter((a) => a.id.toLowerCase().includes(q) || a.name.toLowerCase().includes(q))
})
const mentionVisible = computed(() => mentionOpen.value && filteredMentions.value.length > 0)
watch(mentionQuery, () => {
  mentionIndex.value = 0
  scrollActiveItemIntoView(mentionList, 0)
})

/** Keeps the highlighted row of a popup list inside its scroll viewport. */
function scrollActiveItemIntoView(list: Ref<HTMLElement | null>, index: number): void {
  void nextTick(() => {
    list.value?.querySelectorAll('.cmd-item')[index]?.scrollIntoView({ block: 'nearest' })
  })
}

watch(mentionIndex, (next) => scrollActiveItemIntoView(mentionList, next))
watch(paletteIndex, (next) => scrollActiveItemIntoView(paletteList, next))

function closeMention(): void {
  mentionOpen.value = false
  mentionRange = null
}

/** Opens the popup when the caret sits right after a "@word" that starts a word. */
function updateMention(): void {
  const el = textarea.value
  if (!el) return
  const caret = el.selectionStart ?? 0
  const match = /(^|\s)@([\w.-]*)$/.exec(el.value.slice(0, caret))
  if (!match) {
    closeMention()
    return
  }
  const wasOpen = mentionOpen.value
  mentionRange = { start: caret - match[2].length - 1, end: caret }
  mentionQuery.value = match[2]
  mentionOpen.value = true
  // Re-opening the popup must start from the first row (the watcher above only
  // fires when the query text itself changes).
  if (!wasOpen) mentionIndex.value = 0
  // Agents are files the user (or an LLM) may have just added: re-read them when the popup opens.
  if (!wasOpen) void refreshAgents()
}

function onInput(): void {
  autoResize()
  updateMention()
}

function onKeyup(event: KeyboardEvent): void {
  if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) updateMention()
}

function pickMention(agent: AgentDto): void {
  const range = mentionRange
  if (!range) return
  const insertion = `@${agent.id} `
  state.composerText = state.composerText.slice(0, range.start) + insertion + state.composerText.slice(range.end)
  closeMention()
  nextTick(() => {
    const el = textarea.value
    if (el) {
      el.focus()
      el.selectionStart = el.selectionEnd = range.start + insertion.length
    }
    autoResize()
  })
}

/** Reads the command list; `force` re-reads even when it was fetched before (a file may have changed since). */
async function loadCommands(force = false): Promise<void> {
  if (commandsLoaded.value && !force) return
  commandsLoading.value = true
  try {
    const list = await api<CommandDto[]>('GET', `/api/commands${query(directory.value)}`)
    commands.value = (list ?? []).filter((c) => !!c?.name)
    commandsLoaded.value = true
  } catch {
    // Palette still opens; it shows the empty state.
  } finally {
    commandsLoading.value = false
  }
}

/** Picking a command only inserts "/name " so the user can add arguments before sending. */
function executeCommand(command: CommandDto): void {
  const rest = state.composerText.replace(/^\/\S*\s*/, '')
  state.composerText = `/${command.name} ${rest}`
  closePalette()
  nextTick(() => {
    const el = textarea.value
    if (el) {
      el.focus()
      el.selectionStart = el.selectionEnd = el.value.length
    }
    autoResize()
  })
}

/** Sends "/name args" as the command it stands for; null when the text is not a known command. */
async function trySendCommand(): Promise<boolean> {
  const match = /^\/(\S+)(?:\s+([\s\S]*))?$/.exec(state.composerText.trim())
  if (!match) return false
  await loadCommands()
  const name = match[1].toLowerCase()
  let command = commands.value.find((c) => c.name.toLowerCase() === name)
  if (!command) {
    // The cached list can predate a command file written since: re-read once before treating the text as plain input.
    await loadCommands(true)
    command = commands.value.find((c) => c.name.toLowerCase() === name)
  }
  if (!command) return false
  await runCommand(command.name, (match[2] ?? '').trim())
  nextTick(autoResize)
  return true
}

async function submit(): Promise<void> {
  if (!canSend.value) return
  if (state.isReplying) {
    const text = state.composerText.trim()
    if ((!text && !state.attachments.length) || steering.value) return
    steering.value = true
    const before = state.composerText
    const err = await sendSteered(text, steerDelivery.value)
    steering.value = false
    if (!err && state.composerText === before) {
      // Keep whatever the user typed while the steer was in flight.
      state.composerText = ''
      nextTick(autoResize)
    }
    return
  }
  if (await trySendCommand()) return
  await sendMessage()
  nextTick(autoResize)
}

function onKeydown(event: KeyboardEvent): void {
  if (modeOpen.value && event.key === 'Escape') {
    modeOpen.value = false
    return
  }
  if (mentionVisible.value) {
    const count = filteredMentions.value.length
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      mentionIndex.value = Math.min(mentionIndex.value + 1, count - 1)
      return
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault()
      mentionIndex.value = Math.max(mentionIndex.value - 1, 0)
      return
    }
    if ((event.key === 'Enter' && !event.shiftKey) || event.key === 'Tab') {
      event.preventDefault()
      const chosen = filteredMentions.value[mentionIndex.value]
      if (chosen) pickMention(chosen)
      return
    }
  }
  if (mentionOpen.value && event.key === 'Escape') {
    event.preventDefault()
    closeMention()
    return
  }
  // Escape always dismisses the palette; arrows/Enter navigate it when there
  // are matches.
  if (paletteOpen.value && event.key === 'Escape') {
    event.preventDefault()
    closePalette()
    return
  }
  if (paletteOpen.value && !mentionOpen.value && filteredCommands.value.length) {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      paletteIndex.value = Math.min(paletteIndex.value + 1, filteredCommands.value.length - 1)
      return
    }
    if (event.key === 'ArrowUp') {
      event.preventDefault()
      paletteIndex.value = Math.max(paletteIndex.value - 1, 0)
      return
    }
    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault()
      const chosen = filteredCommands.value[paletteIndex.value]
      if (chosen) {
        executeCommand(chosen)
        return
      }
    }
  }
  if (event.key === 'Enter' && !event.shiftKey) {
    event.preventDefault()
    submit()
  }
}

// The composer overlays the transcript, so ChatMessages reserves room for it.
// Publish its real height — the attachment strip, the growing textarea and the
// reply-mode buttons all change it — so the reserved space tracks the box
// instead of a fixed guess. The observer watches the footer itself, so every one
// of those changes re-publishes without extra watchers.
function publishHeight(): void {
  const el = root.value
  if (!el) return
  document.documentElement.style.setProperty('--sofia-composer-height', `${Math.ceil(el.getBoundingClientRect().height)}px`)
}

let heightObserver: ResizeObserver | null = null

onMounted(() => {
  autoResize()
  publishHeight()
  if (root.value && typeof ResizeObserver !== 'undefined') {
    heightObserver = new ResizeObserver(publishHeight)
    heightObserver.observe(root.value)
  }
  document.addEventListener('mousedown', onDocumentPointer)
})
onBeforeUnmount(() => {
  heightObserver?.disconnect()
  document.removeEventListener('mousedown', onDocumentPointer)
})

// Each chat keeps its own draft: re-fit the textarea when another one is loaded.
watch(() => state.activeConversationId, () => nextTick(autoResize))
</script>
<template>
  <footer ref="root" class="composer">
    <div class="composer-inner">
      <div v-if="state.attachments.length" class="attach-strip">
        <div v-for="a in state.attachments" :key="a.id" class="attach-chip">
          <img
            v-if="a.kind === 'image' && a.previewUrl"
            class="attach-thumb"
            :src="a.previewUrl"
            alt=""
          />
          <SofiaIcon v-else :name="iconForKind(a.kind)" :size="'0.9rem'" />
          <span class="attach-name sofia-truncate">{{ a.name }}</span>
          <span class="attach-size">{{ a.size }}</span>
          <button
            class="attach-remove"
            type="button"
            aria-label="Remove attachment"
            @click="removeAttachment(a.id)"
          >
            <SofiaIcon name="x" :size="'0.7rem'" />
          </button>
        </div>
      </div>

      <div class="input-box">
        <!-- "@" agent mentions: lists the subagents the request can be handed to. -->
        <div v-if="mentionVisible" ref="mentionList" class="cmd-palette" role="listbox" aria-label="Agents">
          <button
            v-for="(a, i) in filteredMentions"
            :key="a.id"
            class="cmd-item"
            :class="{ 'cmd-item--active': i === mentionIndex }"
            type="button"
            role="option"
            :aria-selected="i === mentionIndex"
            @mousedown.prevent
            @mouseenter="mentionIndex = i"
            @click="pickMention(a)"
          >
            <span class="cmd-name sofia-mono">@{{ a.id }}</span>
            <span v-if="a.description" class="cmd-desc sofia-truncate">{{ a.description }}</span>
            <span v-if="a.source && a.source !== 'builtin'" class="cmd-tag">{{ a.source }}</span>
          </button>
        </div>

        <!-- SOF-14: "/" command palette popover, anchored above the input. -->
        <div v-if="paletteOpen && !mentionVisible" ref="paletteList" class="cmd-palette" role="listbox" aria-label="Server commands">
          <p v-if="commandsLoading && !commands.length" class="cmd-note">Loading commands…</p>
          <p v-else-if="!filteredCommands.length" class="cmd-note">
            No server commands{{ paletteQuery ? ` matching “${paletteQuery}”` : ' on this server' }}.
          </p>
          <button
            v-for="(c, i) in filteredCommands"
            :key="c.name"
            class="cmd-item"
            :class="{ 'cmd-item--active': i === paletteIndex }"
            type="button"
            role="option"
            :aria-selected="i === paletteIndex"
            @mouseenter="paletteIndex = i"
            @click="executeCommand(c)"
          >
            <span class="cmd-name sofia-mono">/{{ c.name }}</span>
            <span v-if="c.description" class="cmd-desc sofia-truncate">{{ c.description }}</span>
          </button>
        </div>

        <button class="icon-btn" type="button" :aria-label="t('composer.attach')" @click="openFilePicker">
          <SofiaIcon name="paperclip" />
        </button>

        <textarea
          ref="textarea"
          v-model="state.composerText"
          class="input-area"
          rows="1"
          :placeholder="t('composer.placeholder')"
          @input="onInput"
          @keydown="onKeydown"
          @keyup="onKeyup"
          @click="updateMention"
          @blur="closeMention"
          @paste="onPaste"
        />

        <button
          v-if="state.isReplying"
          class="icon-btn icon-btn--stop"
          type="button"
          :title="t('composer.stop')"
          :aria-label="t('composer.stop')"
          @click="cancelReply"
        >
          <SofiaIcon name="stop" :size="'1.1rem'" />
        </button>

        <!-- Send is always visible. While a reply runs, the caret next to it picks
             how the follow-up is delivered (SOF-13): steer mid-run or queue. -->
        <div ref="modeRoot" class="send-split" :class="{ 'send-split--idle': !canSend }">
          <button
            class="send-btn"
            :class="{ 'send-btn--split': state.isReplying }"
            type="button"
            :title="sendTitle"
            :aria-label="sendTitle"
            :disabled="!canSend || steering"
            @click="submit"
          >
            <SofiaIcon name="send" :size="'1rem'" />
          </button>
          <button
            v-if="state.isReplying"
            class="mode-btn"
            type="button"
            title="Follow-up mode"
            aria-label="Follow-up mode"
            aria-haspopup="menu"
            :aria-expanded="modeOpen"
            @click="modeOpen = !modeOpen"
          >
            <SofiaIcon name="chevronUp" :size="'0.75rem'" />
          </button>
          <div v-if="state.isReplying && modeOpen" class="mode-menu" role="menu">
            <button
              v-for="m in DELIVERY_MODES"
              :key="m.id"
              class="mode-item"
              :class="{ 'mode-item--active': steerDelivery === m.id }"
              type="button"
              role="menuitemradio"
              :aria-checked="steerDelivery === m.id"
              @click="pickDelivery(m.id)"
            >
              <SofiaIcon :name="m.icon" :size="'0.9rem'" />
              <span class="mode-item-body">
                <span class="mode-item-label">{{ m.label }}</span>
                <span class="mode-item-desc">{{ m.description }}</span>
              </span>
              <SofiaIcon v-if="steerDelivery === m.id" class="mode-check" name="check" :size="'0.85rem'" />
            </button>
          </div>
        </div>
      </div>

      <div class="hint-row">
        <div class="metrics">
          <span class="metric" :title="t('composer.hintDuration')">
            <SofiaIcon name="clock" :size="'0.75rem'" />
            {{ durationLabel }}
          </span>
          <span v-if="metrics.costAvailable" class="metric" :title="t('composer.hintCost')">
            <SofiaIcon name="dollar" :size="'0.75rem'" />
            {{ costLabel }}
          </span>
          <span class="metric" :title="t('composer.hintSpeed')">
            <SofiaIcon name="zap" :size="'0.75rem'" />
            {{ tpsLabel }}
          </span>
          <span class="metric" :title="cacheStats.title">
            <SofiaIcon name="database" :size="'0.75rem'" />
            {{ cacheStats.hit }}% {{ t('composer.cacheHit') }}
          </span>
          <span v-if="contextLabel" class="metric" title="Context used / model window">
            <SofiaIcon name="layers" :size="'0.75rem'" />
            {{ contextLabel }}
          </span>
        </div>

        <div class="chip-row">
          <div v-if="currentBranch" class="branch-chip" title="Git branch">
            <SofiaIcon name="branch" :size="'0.8rem'" />
            <span class="branch-name sofia-truncate">{{ currentBranch }}</span>
          </div>

          <SofiaPopover
            ref="permissionPopover"
            compact
            align="right"
            :model-value="state.permissionMode"
            icon="shield"
            :label="t('composer.perms')"
            :items="permissionItems"
            :width="260"
            @update:model-value="onPermissionSelect"
            @dismiss="focusComposer"
          />
          <AgentDropdown
            ref="agentDropdown"
            align="right"
            :agents="agentCatalog"
            :model-value="activeAgentId"
            :width="280"
            @update:model-value="selectAgent"
            @refresh="refreshAgents"
            @dismiss="focusComposer"
          />
          <SofiaPopover
            ref="modelPopover"
            compact
            align="right"
            v-model="activeModelId"
            icon="sparkles"
            :label="t('composer.model')"
            :items="modelItems"
            :width="280"
            @dismiss="focusComposer"
            @opened="rediscoverModels"
          />
          <SofiaPopover
            ref="reasoningPopover"
            compact
            align="right"
            :model-value="state.reasoningMode"
            icon="zap"
            :label="t('composer.reasoning')"
            :items="reasoningItems"
            :width="240"
            @update:model-value="onReasoningSelect"
            @dismiss="focusComposer"
          />
        </div>
      </div>
    </div>

    <input ref="fileInput" class="file-input" type="file" multiple @change="onFilesPicked" />
  </footer>
</template>

<style scoped>
/* Frosted-glass composer that overlays the transcript so long conversations
   scroll behind it with a soft translucency. */
.composer {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  z-index: 10;
  padding: 0.45rem 1.5rem 0.55rem;
  background: color-mix(in srgb, var(--sofia-bg) 66%, transparent);
  backdrop-filter: blur(14px) saturate(125%);
  -webkit-backdrop-filter: blur(14px) saturate(125%);
  border-top: 1px solid color-mix(in srgb, var(--sofia-border) 55%, transparent);
}

.composer-inner {
  position: relative;
  max-width: 880px;
  margin: 0 auto;
}

/* --- attachment strip --------------------------------------------------- */
.attach-strip {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin-bottom: 0.5rem;
}

.attach-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  max-width: 16rem;
  padding: 0.3rem 0.4rem 0.3rem 0.55rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border);
  border-radius: 8px;
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
}

.attach-thumb {
  width: 24px;
  height: 24px;
  border-radius: 5px;
  object-fit: cover;
}

.attach-name {
  color: var(--sofia-text);
}

.attach-size {
  color: var(--sofia-text-faint);
  flex-shrink: 0;
}

.attach-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  border: none;
  border-radius: 5px;
  background: transparent;
  color: var(--sofia-text-faint);
  cursor: pointer;
  flex-shrink: 0;
}

.attach-remove:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

/* --- chip row (right side of the hint row) -------------------------------- */
.chip-row {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  align-items: center;
  gap: 0.3rem;
  min-width: 0;
  margin-left: auto;
}

/* --- branch chip --------------------------------------------------------- */
.branch-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  max-width: 10rem;
  padding: 0.15rem 0.5rem;
  background: color-mix(in srgb, var(--sofia-surface-2) 74%, transparent);
  border: 1px solid var(--sofia-border);
  border-radius: 999px;
  color: var(--sofia-text-muted);
  font-size: 0.6875rem;
  font-weight: 500;
  line-height: 1.4;
  user-select: none;
}

.branch-chip svg {
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.branch-name {
  color: var(--sofia-text);
  font-family: var(--font-family-monospace, 'SFMono-Regular', Menlo, Consolas, monospace);
  font-size: 0.7rem;
}

/* --- input box ---------------------------------------------------------- */
.input-box {
  display: flex;
  align-items: flex-end;
  gap: 0.4rem;
  padding: 0.4rem 0.4rem 0.4rem 0.6rem;
  background: color-mix(in srgb, var(--sofia-surface-2) 72%, transparent);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 14px;
  transition: border-color 0.15s ease, box-shadow 0.15s ease;
}

.input-box:focus-within {
  border-color: var(--sofia-primary);
  box-shadow: 0 0 0 3px var(--sofia-primary-soft);
}

/* SOF-14: "/" command palette, anchored above the input box. */
.cmd-palette {
  position: absolute;
  left: 50%;
  bottom: 100%;
  transform: translateX(-50%);
  z-index: 50;
  width: min(340px, calc(100% - 1rem));
  max-height: 240px;
  overflow-y: auto;
  margin-bottom: 0.5rem;
  padding: 0.4rem;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 42px rgba(0, 0, 0, 0.45);
}

.cmd-note {
  margin: 0.2rem 0.35rem;
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}

.cmd-item {
  position: relative;
  display: flex;
  align-items: baseline;
  gap: 0.55rem;
  width: 100%;
  padding: 0.4rem 0.5rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text);
  font-size: 0.8125rem;
  text-align: left;
  cursor: pointer;
}

.cmd-item--active,
.cmd-item:hover {
  background: var(--sofia-surface-2);
  color: var(--sofia-primary);
}

.cmd-name {
  flex-shrink: 0;
  font-size: 0.75rem;
  font-weight: 600;
}

.cmd-desc {
  flex: 1;
  min-width: 0;
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
}

.cmd-tag {
  flex-shrink: 0;
  font-size: 0.625rem;
  color: var(--sofia-text-faint);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 6px;
  padding: 0 0.3rem;
}

.cmd-item--active .cmd-desc,
.cmd-item:hover .cmd-desc {
  color: var(--sofia-text-muted);
}

.input-area {
  flex: 1;
  min-width: 0;
  max-height: 220px;
  padding: 0.45rem 0;
  border: none;
  background: transparent;
  color: var(--sofia-text);
  font-family: inherit;
  font-size: 0.9375rem;
  line-height: 1.5;
  resize: none;
  outline: none;
  overflow-y: auto;
}

.input-area::placeholder {
  color: var(--sofia-text-faint);
}

.icon-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 34px;
  border: none;
  border-radius: 10px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
  flex-shrink: 0;
  transition: background 0.12s ease, color 0.12s ease;
}

.icon-btn:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.icon-btn--stop {
  background: var(--sofia-primary-soft-strong);
  color: var(--sofia-primary);
  border-radius: 10px;
}

.icon-btn--stop:hover {
  background: var(--sofia-primary-soft-strong);
  color: var(--sofia-primary-strong);
}

/* Send and the follow-up mode caret form one solid pill: the pill owns the
   background (dimmed while there is nothing to send) so both halves always match. */
.send-split {
  position: relative;
  display: inline-flex;
  align-items: stretch;
  flex-shrink: 0;
  height: 34px;
  border-radius: 10px;
  background: var(--sofia-primary);
  transition: background 0.12s ease;
}

.send-split--idle {
  background: color-mix(in srgb, var(--sofia-primary) 38%, transparent);
}

.mode-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  border: none;
  border-left: 1px solid color-mix(in srgb, var(--sofia-primary-ink) 28%, transparent);
  border-radius: 0 10px 10px 0;
  background: transparent;
  color: var(--sofia-primary-ink);
  cursor: pointer;
  transition: background 0.12s ease;
}

.mode-btn:hover,
.mode-btn[aria-expanded='true'] {
  background: var(--sofia-primary-strong);
}

.mode-menu {
  position: absolute;
  right: 0;
  bottom: calc(100% + 8px);
  z-index: 40;
  min-width: 15rem;
  padding: 0.25rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.45);
}

.mode-item {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  width: 100%;
  padding: 0.45rem 0.5rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  text-align: left;
  cursor: pointer;
}

.mode-item:hover {
  background: var(--sofia-surface-3);
}

.mode-item--active {
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

.mode-item-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 0.1rem;
  min-width: 0;
}

.mode-item-label {
  color: var(--sofia-text);
  font-size: 0.8125rem;
  font-weight: 550;
}

.mode-item-desc {
  font-size: 0.75rem;
  line-height: 1.4;
  color: var(--sofia-text-muted);
}

.mode-check {
  flex-shrink: 0;
  margin-top: 0.1rem;
  color: var(--sofia-primary);
}

.send-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 34px;
  height: 34px;
  border: none;
  border-radius: 10px;
  background: transparent;
  color: var(--sofia-primary-ink);
  cursor: pointer;
  flex-shrink: 0;
  transition: background 0.12s ease, transform 0.08s ease, opacity 0.12s ease;
}

.send-btn--split {
  border-radius: 10px 0 0 10px;
}

.send-btn:hover:not(:disabled) {
  background: var(--sofia-primary-strong);
}

.send-btn:active:not(:disabled) {
  transform: translateY(1px);
}

.send-btn:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

/* --- hint row ----------------------------------------------------------- */
.hint-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.35rem 0.75rem;
  margin-top: 0.3rem;
  font-size: 0.6875rem;
}

.metrics {
  display: inline-flex;
  align-items: center;
  gap: 0.9rem;
  min-width: 0;
}

.metric {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  color: var(--sofia-text-muted);
  font-variant-numeric: tabular-nums;
}

.metric svg {
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.file-input {
  display: none;
}
</style>
