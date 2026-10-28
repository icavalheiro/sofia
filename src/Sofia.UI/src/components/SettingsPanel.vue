<script setup lang="ts">
// Settings modal: local general toggles + Sofia.Server config, model
// providers (with an add-provider flow), skills, MCP servers and appearance.
// Local preferences live in useSettings() (sessionStorage); providers, models,
// MCP servers and the server config are Sofia.Server state.
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useSettings } from '@/composables/useSettings'
import { SOUND_KEYS, useSound } from '@/composables/useSound'
import {
  SHORTCUT_GROUPS,
  comboFromEvent,
  isBrowserReserved,
  isValidCombo,
  useShortcuts,
} from '@/composables/useShortcuts'
import { api, onServerEvent } from '@/services/sofia'
import { useI18n } from '@/i18n'
import type {
  ConfigDto,
  CopilotDeviceFlow,
  CopilotLoginPoll,
  McpServer,
  ModelOption,
  Provider,
  ProviderPresetDto,
} from '@/types'
import PairingDialog from '@/components/PairingDialog.vue'
import SkillsSection from '@/components/SkillsSection.vue'
import SofiaIcon from '@/components/SofiaIcon.vue'

export type SettingsTabId = 'general' | 'models' | 'skills' | 'mcp' | 'shortcuts' | 'appearance'

const props = defineProps<{ open: boolean; initialTab?: SettingsTabId }>()
const emit = defineEmits<{ (e: 'close'): void }>()

const { settings, resetSettings } = useSettings()
const { models, refreshModels, rediscoverModels } = useHarness()
const { shortcuts, control, bindingOf, conflictFor, setBinding, resetBinding, resetAllBindings, isCustomized } =
  useShortcuts()

const { t } = useI18n()
const { playSound } = useSound()

// --- sound preview ------------------------------------------------------------------
const previewing = ref(false)
let previewTimer: ReturnType<typeof setTimeout> | null = null

function previewSounds(): void {
  if (previewing.value || !settings.general.soundEffects) return
  previewing.value = true
  let i = 0
  const step = (): void => {
    const key = SOUND_KEYS[i]
    if (!key || !settings.general.soundEffects) {
      previewing.value = false
      previewTimer = null
      return
    }
    playSound(key)
    i += 1
    previewTimer = setTimeout(step, 420)
  }
  step()
}

onBeforeUnmount(() => {
  if (previewTimer) clearTimeout(previewTimer)
})

type TabId = SettingsTabId
const tab = ref<TabId>('general')

watch(
  () => props.open,
  (open) => {
    if (open && props.initialTab) tab.value = props.initialTab
  },
)

const tabs: { id: TabId; key: string; icon: string }[] = [
  { id: 'general', key: 'settings.tab.general', icon: 'sliders' },
  { id: 'models', key: 'settings.tab.models', icon: 'cpu' },
  { id: 'skills', key: 'settings.tab.skills', icon: 'sparkles' },
  { id: 'mcp', key: 'settings.tab.mcp', icon: 'plug' },
  { id: 'shortcuts', key: 'settings.tab.shortcuts', icon: 'keyboard' },
  { id: 'appearance', key: 'settings.tab.appearance', icon: 'palette' },
]

// --- shortcuts tab ------------------------------------------------------------------
const recordingId = ref<string | null>(null)
const recordError = ref('')

function onRecordKey(event: KeyboardEvent): void {
  const id = recordingId.value
  if (!id) return
  event.preventDefault()
  event.stopPropagation()
  const bare = !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey
  if (bare && event.key === 'Escape') return stopRecording()
  if (bare && (event.key === 'Backspace' || event.key === 'Delete')) {
    setBinding(id, '')
    return stopRecording()
  }
  const combo = comboFromEvent(event)
  if (!combo) return
  if (!isValidCombo(combo)) {
    recordError.value = t('shortcuts.needModifier')
    return
  }
  const other = conflictFor(id, combo)
  if (other) {
    recordError.value = t('shortcuts.conflict').replace('{action}', t(`shortcut.${other.id}`))
    return
  }
  setBinding(id, combo)
  stopRecording()
}

function startRecording(id: string): void {
  stopRecording()
  recordingId.value = id
  recordError.value = ''
  control.recording = true
  window.addEventListener('keydown', onRecordKey, true)
}

function stopRecording(): void {
  recordingId.value = null
  recordError.value = ''
  control.recording = false
  window.removeEventListener('keydown', onRecordKey, true)
}

watch(tab, stopRecording)

// While the modal is open it owns the keyboard: global shortcuts stay quiet.
watch(
  () => props.open,
  (open) => {
    control.suspended = open
    if (!open) stopRecording()
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  control.suspended = false
  stopRecording()
})

/** "Ctrl+Shift+N" → ["Ctrl", "Shift", "N"] for key-cap rendering. */
function comboKeys(combo: string): string[] {
  return combo.endsWith('++') ? [...combo.slice(0, -2).split('+'), '+'] : combo.split('+')
}

// Pairing dialog visibility (General tab).
const showPairing = ref(false)

function failure(e: unknown): string {
  return e instanceof Error ? e.message : String(e)
}

function statusLabel(status: string): string {
  if (status === 'connected') return 'Connected'
  if (status === 'error') return 'Error'
  if (status === 'stopped') return 'Stopped'
  if (status === 'pending') return 'Pending'
  return 'Disconnected'
}

// --- providers & models -------------------------------------------------------
const providers = ref<Provider[]>([])
const catalogLoading = ref(false)
const catalogError = ref<string | null>(null)
const rediscovering = ref(false)
/** Provider id with a request in flight (enable toggle / delete). */
const providerBusy = ref<string | null>(null)

const connectedProviders = computed(() => providers.value.filter((p) => p.status === 'connected').length)

async function refreshCatalog(): Promise<void> {
  catalogLoading.value = true
  catalogError.value = null
  try {
    const [list] = await Promise.all([api<Provider[]>('GET', '/api/providers'), refreshModels()])
    providers.value = list ?? []
  } catch (e) {
    catalogError.value = failure(e)
  } finally {
    catalogLoading.value = false
  }
}

/** Re-runs local runtime discovery and model listing on the server. */
async function rediscover(): Promise<void> {
  if (rediscovering.value) return
  rediscovering.value = true
  catalogError.value = null
  try {
    providers.value = (await api<Provider[]>('POST', '/api/providers/refresh')) ?? []
    await refreshModels()
  } catch (e) {
    catalogError.value = failure(e)
  } finally {
    rediscovering.value = false
  }
}

function replaceProvider(updated: Provider): void {
  const index = providers.value.findIndex((p) => p.id === updated.id)
  if (index === -1) providers.value.push(updated)
  else providers.value.splice(index, 1, updated)
}

async function toggleProvider(p: Provider): Promise<void> {
  if (providerBusy.value) return
  providerBusy.value = p.id
  try {
    replaceProvider(await api<Provider>('PATCH', `/api/providers/${p.id}`, { enabled: !p.enabled }))
    await refreshModels()
  } catch (e) {
    catalogError.value = failure(e)
  } finally {
    providerBusy.value = null
  }
}

async function togglePreserveThinking(p: Provider): Promise<void> {
  if (providerBusy.value) return
  providerBusy.value = p.id
  try {
    replaceProvider(await api<Provider>('PATCH', `/api/providers/${p.id}`, { preserveThinking: !p.preserveThinking }))
  } catch (e) {
    catalogError.value = failure(e)
  } finally {
    providerBusy.value = null
  }
}

async function removeProvider(p: Provider): Promise<void> {
  if (providerBusy.value) return
  providerBusy.value = p.id
  try {
    await api('DELETE', `/api/providers/${p.id}`)
    await refreshCatalog()
  } catch (e) {
    catalogError.value = failure(e)
  } finally {
    providerBusy.value = null
  }
}

// --- add provider ---------------------------------------------------------------
// Pick a preset (base URL + wire protocol prefilled) or a custom
// OpenAI-compatible / Anthropic endpoint; "Load models" probes it without
// saving, "Add provider" saves it on the server.
const showAddProvider = ref(false)
const presets = ref<ProviderPresetDto[]>([])
/** Selected preset id; '' = custom endpoint; null = nothing picked yet. */
const presetId = ref<string | null>(null)
const newProvider = reactive({
  name: '',
  type: 'openai' as 'openai' | 'openai-responses' | 'anthropic',
  baseUrl: '',
  apiKey: '',
})
const loadedModels = ref<ModelOption[]>([])
const loadingModels = ref(false)
const modelsNote = ref<string | null>(null)
const connecting = ref(false)
const connectError = ref<string | null>(null)
const connectDone = ref(false)

const selectedPreset = computed(() => presets.value.find((p) => p.id === presetId.value) ?? null)
const canProbe = computed(() => presetId.value !== null && newProvider.baseUrl.trim() !== '')
const canAddProvider = computed(
  () => presetId.value !== null && newProvider.name.trim() !== '' && newProvider.baseUrl.trim() !== '',
)

async function loadPresets(): Promise<void> {
  if (presets.value.length) return
  try {
    presets.value = (await api<ProviderPresetDto[]>('GET', '/api/providers/presets')) ?? []
  } catch {
    presets.value = []
  }
}

function pickPreset(id: string): void {
  stopCopilotLogin()
  presetId.value = id
  const preset = presets.value.find((p) => p.id === id)
  newProvider.name = preset?.name ?? ''
  newProvider.type = preset?.type ?? 'openai'
  newProvider.baseUrl = preset?.baseUrl ?? ''
  loadedModels.value = []
  modelsNote.value = null
  connectError.value = null
  connectDone.value = false
}

function clearPreset(): void {
  stopCopilotLogin()
  presetId.value = null
  loadedModels.value = []
  modelsNote.value = null
}

// GitHub Copilot signs in with the OAuth device flow: show the code, open GitHub,
// and poll the server until the user approves it there.
const copilotFlow = ref<CopilotDeviceFlow | null>(null)
const copilotBusy = ref(false)
let copilotTimer: ReturnType<typeof setTimeout> | null = null

function stopCopilotLogin(): void {
  if (copilotTimer) clearTimeout(copilotTimer)
  copilotTimer = null
  copilotFlow.value = null
  copilotBusy.value = false
}

async function startCopilotLogin(): Promise<void> {
  if (copilotBusy.value) return
  copilotBusy.value = true
  if (copilotTimer) clearTimeout(copilotTimer)
  connectError.value = null
  connectDone.value = false
  try {
    const flow = await api<CopilotDeviceFlow>('POST', '/api/providers/github-copilot/login')
    copilotFlow.value = flow
    window.open(flow.verificationUri, '_blank', 'noopener')
    copilotTimer = setTimeout(() => void pollCopilotLogin(flow), flow.interval * 1000)
  } catch (e) {
    connectError.value = failure(e)
  } finally {
    copilotBusy.value = false
  }
}

async function pollCopilotLogin(flow: CopilotDeviceFlow): Promise<void> {
  try {
    const res = await api<CopilotLoginPoll>('POST', '/api/providers/github-copilot/login/poll', {
      deviceCode: flow.deviceCode,
    })
    if (copilotFlow.value?.deviceCode !== flow.deviceCode) return
    if (res.status === 'success') {
      stopCopilotLogin()
      if (res.provider) replaceProvider(res.provider)
      connectDone.value = true
      await refreshCatalog()
      await refreshModels()
    } else if (res.status === 'pending') {
      copilotTimer = setTimeout(() => void pollCopilotLogin(flow), (res.interval ?? flow.interval) * 1000)
    } else {
      stopCopilotLogin()
      connectError.value = res.status === 'denied' ? 'GitHub sign-in was denied.' : 'The sign-in code expired. Try again.'
    }
  } catch (e) {
    stopCopilotLogin()
    connectError.value = failure(e)
  }
}

onBeforeUnmount(stopCopilotLogin)

async function loadModels(): Promise<void> {
  if (!canProbe.value || loadingModels.value) return
  loadingModels.value = true
  modelsNote.value = null
  try {
    const res = await api<{ models: ModelOption[] }>('POST', '/api/providers/probe', {
      type: newProvider.type,
      baseUrl: newProvider.baseUrl.trim(),
      apiKey: newProvider.apiKey.trim() || undefined,
    })
    loadedModels.value = res?.models ?? []
    if (!loadedModels.value.length) modelsNote.value = 'The endpoint listed no models.'
  } catch (e) {
    loadedModels.value = []
    modelsNote.value = failure(e)
  } finally {
    loadingModels.value = false
  }
}

async function submitProvider(): Promise<void> {
  if (!canAddProvider.value || connecting.value) return
  connecting.value = true
  connectError.value = null
  connectDone.value = false
  try {
    const saved = await api<Provider>('POST', '/api/providers', {
      name: newProvider.name.trim(),
      type: newProvider.type,
      baseUrl: newProvider.baseUrl.trim(),
      apiKey: newProvider.apiKey.trim() || undefined,
      models: loadedModels.value.length ? loadedModels.value.map((m) => m.modelId) : undefined,
      presetId: presetId.value || undefined,
    })
    // The key is spent; clear it from view state right away.
    newProvider.apiKey = ''
    replaceProvider(saved)
    connectDone.value = true
    await refreshCatalog()
  } catch (e) {
    connectError.value = failure(e)
  } finally {
    connecting.value = false
  }
}

function resetProviderForm(): void {
  stopCopilotLogin()
  showAddProvider.value = false
  presetId.value = null
  newProvider.name = ''
  newProvider.type = 'openai'
  newProvider.baseUrl = ''
  newProvider.apiKey = ''
  loadedModels.value = []
  modelsNote.value = null
  connectError.value = null
  connectDone.value = false
}

// Reload the Models tab whenever it is opened.
watch(
  () => props.open && tab.value === 'models',
  (active) => {
    if (!active) return
    void refreshCatalog()
    void loadPresets()
  },
  { immediate: true },
)

watch(
  () => props.open,
  (open) => {
    if (!open) {
      resetProviderForm()
      showPairing.value = false
    }
  },
)

// --- server config ----------------------------------------------------------------
const config = ref<ConfigDto | null>(null)
const configError = ref<string | null>(null)
const shellDraft = ref('')
const searxngDraft = ref('')

async function refreshConfig(): Promise<void> {
  configError.value = null
  try {
    config.value = await api<ConfigDto>('GET', '/api/config')
    shellDraft.value = config.value?.shell ?? ''
    searxngDraft.value = config.value?.searxngUrl ?? ''
  } catch (e) {
    config.value = null
    configError.value = failure(e)
  }
}

/** PATCHes the server config (optimistic; re-read on failure). */
async function saveConfig(patch: Partial<ConfigDto>): Promise<void> {
  if (!config.value) return
  Object.assign(config.value, patch)
  try {
    config.value = await api<ConfigDto>('PATCH', '/api/config', patch)
    configError.value = null
  } catch (e) {
    configError.value = failure(e)
    await refreshConfig()
  }
}

/** Switches a model on/off for the harness (optimistic; the catalog is re-read on failure). */
async function setModelEnabled(model: ModelOption, enabled: boolean): Promise<void> {
  await setModelsEnabled([model], enabled)
}

/** Marks a model as single client (one conversation at a time; optimistic). */
async function setModelSingleClient(model: ModelOption, singleClient: boolean): Promise<void> {
  model.singleClient = singleClient
  try {
    await api('POST', '/api/models/single-client', { ids: [model.id], enabled: singleClient })
  } catch (e) {
    configError.value = failure(e)
    await refreshModels()
  }
}

/** Bulk variant used by select all / deselect all in the provider's model manager. */
async function setModelsEnabled(targets: ModelOption[], enabled: boolean): Promise<void> {
  const changed = targets.filter((m) => m.enabled !== enabled)
  if (!changed.length) return
  for (const m of changed) m.enabled = enabled
  try {
    await api('POST', '/api/models/enabled', { ids: changed.map((m) => m.id), enabled })
  } catch (e) {
    configError.value = failure(e)
    await refreshModels()
  }
}

/** Provider whose model manager is expanded, and the manager's search text. */
const managingId = ref<string | null>(null)
const modelFilter = ref('')

function toggleManage(p: Provider): void {
  managingId.value = managingId.value === p.id ? null : p.id
  modelFilter.value = ''
}

function providerModels(p: Provider): ModelOption[] {
  return models.value.filter((m) => m.providerId === p.id)
}

function visibleProviderModels(p: Provider): ModelOption[] {
  const q = modelFilter.value.trim().toLowerCase()
  const all = providerModels(p)
  return q ? all.filter((m) => `${m.label} ${m.modelId}`.toLowerCase().includes(q)) : all
}

const enabledModels = computed(() => models.value.filter((m) => m.enabled !== false))

function saveThreshold(event: Event): void {
  const value = Number((event.target as HTMLInputElement).value)
  if (!Number.isFinite(value)) return
  void saveConfig({ compactThreshold: Math.min(0.99, Math.max(0.1, value / 100)) })
}

function saveShell(): void {
  if (!config.value || shellDraft.value.trim() === config.value.shell) return
  void saveConfig({ shell: shellDraft.value.trim() })
}

function saveSearxng(): void {
  if (!config.value || searxngDraft.value.trim() === config.value.searxngUrl) return
  void saveConfig({ searxngUrl: searxngDraft.value.trim() })
}

watch(
  () => props.open && tab.value === 'general',
  (active) => {
    if (!active) return
    void refreshConfig()
    void rediscoverModels()
  },
  { immediate: true },
)

// --- MCP servers ------------------------------------------------------------------
const mcpServers = ref<McpServer[]>([])
const mcpLoading = ref(false)
const mcpError = ref<string | null>(null)
/** Server id with a request in flight. */
const mcpBusy = ref<string | null>(null)

const connectedMcp = computed(() => mcpServers.value.filter((s) => s.status === 'connected').length)

function replaceMcp(server: McpServer): void {
  const index = mcpServers.value.findIndex((s) => s.id === server.id)
  if (index === -1) mcpServers.value.push(server)
  else mcpServers.value.splice(index, 1, server)
}

async function refreshMcp(): Promise<void> {
  mcpLoading.value = true
  mcpError.value = null
  try {
    mcpServers.value = (await api<McpServer[]>('GET', '/api/mcp')) ?? []
  } catch (e) {
    mcpError.value = failure(e)
  } finally {
    mcpLoading.value = false
  }
}

async function mcpAction(server: McpServer, action: 'connect' | 'disconnect'): Promise<void> {
  if (mcpBusy.value) return
  mcpBusy.value = server.id
  mcpError.value = null
  try {
    replaceMcp(await api<McpServer>('POST', `/api/mcp/${server.id}/${action}`))
  } catch (e) {
    mcpError.value = failure(e)
  } finally {
    mcpBusy.value = null
  }
}

/** Toggle = connect / disconnect; a failed server restarts with a fresh connect. */
function toggleMcp(server: McpServer): void {
  const running = server.status === 'connected' || server.status === 'pending'
  void mcpAction(server, running ? 'disconnect' : 'connect')
}

async function removeMcp(server: McpServer): Promise<void> {
  if (mcpBusy.value) return
  mcpBusy.value = server.id
  try {
    await api('DELETE', `/api/mcp/${server.id}`)
    mcpServers.value = mcpServers.value.filter((s) => s.id !== server.id)
  } catch (e) {
    mcpError.value = failure(e)
  } finally {
    mcpBusy.value = null
  }
}

watch(
  () => props.open && tab.value === 'mcp',
  (active) => {
    if (!active) return
    void refreshMcp()
  },
  { immediate: true },
)

// --- add mcp ------------------------------------------------------------------------
const showAddMcp = ref(false)
const newMcp = reactive({
  name: '',
  transport: 'command' as 'command' | 'web',
  command: '',
  url: '',
  token: '',
})
const addingMcp = ref(false)
const addMcpError = ref<string | null>(null)

const canAddMcp = computed(
  () => newMcp.name.trim() !== '' && (newMcp.transport === 'command' ? newMcp.command.trim() !== '' : newMcp.url.trim() !== ''),
)

async function submitMcp(): Promise<void> {
  if (!canAddMcp.value || addingMcp.value) return
  addingMcp.value = true
  addMcpError.value = null
  try {
    const created = await api<McpServer>('POST', '/api/mcp', {
      name: newMcp.name.trim(),
      transport: newMcp.transport,
      command: newMcp.transport === 'command' ? newMcp.command.trim() : undefined,
      url: newMcp.transport === 'web' ? newMcp.url.trim() : undefined,
      token: newMcp.transport === 'web' && newMcp.token.trim() ? newMcp.token.trim() : undefined,
    })
    replaceMcp(created)
    showAddMcp.value = false
    newMcp.name = ''
    newMcp.transport = 'command'
    newMcp.command = ''
    newMcp.url = ''
    newMcp.token = ''
  } catch (e) {
    addMcpError.value = failure(e)
  } finally {
    addingMcp.value = false
  }
}

// --- live updates -------------------------------------------------------------------
const stopEvents = onServerEvent((event) => {
  if (event.type === 'mcp.updated' && event.data) replaceMcp(event.data as McpServer)
  else if (event.type === 'models.updated' && props.open && tab.value === 'models') void refreshCatalog()
})
onBeforeUnmount(stopEvents)
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="settings-overlay" @click.self="emit('close')">
      <div class="settings-modal" role="dialog" aria-modal="true" aria-label="Settings">
        <header class="settings-head">
          <div class="settings-title">
            <SofiaIcon name="gear" :size="'1.05rem'" />
            <h2>{{ t('settings.title') }}</h2>
          </div>
          <button class="close-btn" type="button" :aria-label="t('settings.close')" @click="emit('close')">
            <SofiaIcon name="x" />
          </button>
        </header>

        <nav class="settings-tabs">
          <button
            v-for="tabItem in tabs"
            :key="tabItem.id"
            class="tab"
            :class="{ 'tab--active': tab === tabItem.id }"
            type="button"
            @click="tab = tabItem.id"
          >
            <SofiaIcon :name="tabItem.icon" :size="'0.95rem'" />
            {{ t(tabItem.key) }}
          </button>
        </nav>

        <div class="settings-body">
          <!-- General -->
          <template v-if="tab === 'general'">
            <p class="tab-intro">
              The toggles below are stored locally in this browser. The Server block changes
              Sofia.Server itself.
            </p>

            <div class="rows">
              <label class="row">
                <span class="row-text">
                  <span class="row-title">Stream responses</span>
                  <span class="row-sub">Show tokens as they arrive.</span>
                </span>
                <button
                  class="toggle"
                  :class="{ 'toggle--on': settings.general.streamResponses }"
                  type="button"
                  role="switch"
                  :aria-checked="settings.general.streamResponses"
                  @click="settings.general.streamResponses = !settings.general.streamResponses"
                ><span class="knob" /></button>
              </label>

              <label class="row">
                <span class="row-text">
                  <span class="row-title">Confirm destructive actions</span>
                  <span class="row-sub">Ask before deleting files or workspaces.</span>
                </span>
                <button
                  class="toggle"
                  :class="{ 'toggle--on': settings.general.confirmDestructive }"
                  type="button"
                  role="switch"
                  :aria-checked="settings.general.confirmDestructive"
                  @click="settings.general.confirmDestructive = !settings.general.confirmDestructive"
                ><span class="knob" /></button>
              </label>

              <label class="row">
                <span class="row-text">
                  <span class="row-title">Auto-update</span>
                  <span class="row-sub">Download harness updates in the background.</span>
                </span>
                <button
                  class="toggle"
                  :class="{ 'toggle--on': settings.general.autoUpdate }"
                  type="button"
                  role="switch"
                  :aria-checked="settings.general.autoUpdate"
                  @click="settings.general.autoUpdate = !settings.general.autoUpdate"
                ><span class="knob" /></button>
              </label>

              <label class="row">
                <span class="row-text">
                  <span class="row-title">Anonymous telemetry</span>
                  <span class="row-sub">Share usage stats to improve Sofia.</span>
                </span>
                <button
                  class="toggle"
                  :class="{ 'toggle--on': settings.general.telemetry }"
                  type="button"
                  role="switch"
                  :aria-checked="settings.general.telemetry"
                  @click="settings.general.telemetry = !settings.general.telemetry"
                ><span class="knob" /></button>
              </label>
              <label class="row">
                <span class="row-text">
                  <span class="row-title">Sound effects</span>
                  <span class="row-sub">Soft audio feedback for UI events.</span>
                </span>
                <button
                  class="toggle"
                  :class="{ 'toggle--on': settings.general.soundEffects }"
                  type="button"
                  role="switch"
                  :aria-checked="settings.general.soundEffects"
                  @click="settings.general.soundEffects = !settings.general.soundEffects"
                ><span class="knob" /></button>
              </label>
              <label class="row vol-row" :class="{ 'vol-row--off': !settings.general.soundEffects }">
                <span class="row-text">
                  <span class="row-title">Volume</span>
                  <span class="row-sub">Loudness of sound effects.</span>
                </span>
                <span class="vol-control">
                  <input
                    v-model.number="settings.general.soundVolume"
                    class="vol-slider"
                    type="range"
                    min="0"
                    max="100"
                    step="1"
                    aria-label="Sound effects volume"
                    :disabled="!settings.general.soundEffects"
                  >
                  <span class="vol-value" aria-hidden="true">{{ settings.general.soundVolume }}%</span>
                </span>
              </label>
              <div class="row" :class="{ 'vol-row--off': !settings.general.soundEffects }">
                <span class="row-text">
                  <span class="row-title">Preview sounds</span>
                  <span class="row-sub">Plays every effect in order.</span>
                </span>
                <button
                  class="mini-btn"
                  type="button"
                  :disabled="previewing || !settings.general.soundEffects"
                  @click="previewSounds"
                >{{ previewing ? 'Playing…' : 'Preview' }}</button>
              </div>
            </div>

            <!-- Sofia.Server configuration (GET / PATCH /api/config). -->
            <div class="srv-cfg">
              <div class="srv-cfg-head">
                <span class="row-title">Server</span>
                <span class="srv-cfg-badge">Sofia.Server</span>
              </div>
              <p v-if="configError" class="form-note form-note--error">{{ configError }}</p>
              <template v-if="config">
                <div class="rows">
                  <div class="row">
                    <span class="row-text">
                      <span class="row-title">Default model</span>
                      <span class="row-sub">Used when a workspace has no model of its own.</span>
                    </span>
                    <select
                      class="field-input cfg-input"
                      :value="config.defaultModel"
                      @change="saveConfig({ defaultModel: ($event.target as HTMLSelectElement).value })"
                    >
                      <option value="">First connected model</option>
                      <option v-for="m in models.filter((x) => x.enabled !== false)" :key="m.id" :value="m.id">{{ m.label }} · {{ m.provider }}</option>
                    </select>
                  </div>

                  <label class="row">
                    <span class="row-text">
                      <span class="row-title">Auto-compact</span>
                      <span class="row-sub">Summarise long sessions before the context window fills up.</span>
                    </span>
                    <button
                      class="toggle"
                      :class="{ 'toggle--on': config.autoCompact }"
                      type="button"
                      role="switch"
                      :aria-checked="config.autoCompact"
                      @click="saveConfig({ autoCompact: !config.autoCompact })"
                    ><span class="knob" /></button>
                  </label>

                  <div class="row">
                    <span class="row-text">
                      <span class="row-title">Compact threshold</span>
                      <span class="row-sub">Percent of the context window that triggers compaction.</span>
                    </span>
                    <input
                      class="field-input cfg-input cfg-input--num"
                      type="number"
                      min="10"
                      max="99"
                      step="5"
                      :value="Math.round(config.compactThreshold * 100)"
                      :disabled="!config.autoCompact"
                      @change="saveThreshold"
                    />
                  </div>

                  <label class="row">
                    <span class="row-text">
                      <span class="row-title">Local model discovery</span>
                      <span class="row-sub">Probe Ollama, llama.cpp, LM Studio, vLLM and Unsloth Studio.</span>
                    </span>
                    <button
                      class="toggle"
                      :class="{ 'toggle--on': config.discovery }"
                      type="button"
                      role="switch"
                      :aria-checked="config.discovery"
                      @click="saveConfig({ discovery: !config.discovery })"
                    ><span class="knob" /></button>
                  </label>

                  <div class="row">
                    <span class="row-text">
                      <span class="row-title">Shell</span>
                      <span class="row-sub">Empty = pwsh / powershell on Windows, bash elsewhere.</span>
                    </span>
                    <input
                      v-model="shellDraft"
                      class="field-input cfg-input"
                      placeholder="default"
                      autocomplete="off"
                      spellcheck="false"
                      @change="saveShell"
                      @keyup.enter="saveShell"
                    />
                  </div>

                  <label class="row">
                    <span class="row-text">
                      <span class="row-title">Keep screen awake</span>
                      <span class="row-sub">Stop the display from sleeping while an agent is working (on the machine running Sofia.Server).</span>
                    </span>
                    <button
                      class="toggle"
                      :class="{ 'toggle--on': config.keepScreenAwake }"
                      type="button"
                      role="switch"
                      :aria-checked="config.keepScreenAwake"
                      @click="saveConfig({ keepScreenAwake: !config.keepScreenAwake })"
                    ><span class="knob" /></button>
                  </label>

                  <label class="row">
                    <span class="row-text">
                      <span class="row-title">Learn from conversations</span>
                      <span class="row-sub">After a reply, the model distils short notes about the workspace or agent into the system prompt. It must justify each note as useful, so unjustified ones are dropped. Costs one extra model call.</span>
                    </span>
                    <button
                      class="toggle"
                      :class="{ 'toggle--on': config.knowledge }"
                      type="button"
                      role="switch"
                      :aria-checked="config.knowledge"
                      @click="saveConfig({ knowledge: !config.knowledge })"
                    ><span class="knob" /></button>
                  </label>

                  <div class="row">
                    <span class="row-text">
                      <span class="row-title">Web search</span>
                      <span class="row-sub">Backend of the web_search tool. DuckDuckGo needs no key; SearXNG uses your own instance.</span>
                    </span>
                    <select
                      class="field-input cfg-input"
                      :value="config.searchProvider"
                      @change="saveConfig({ searchProvider: ($event.target as HTMLSelectElement).value as ConfigDto['searchProvider'] })"
                    >
                      <option value="duckduckgo">DuckDuckGo</option>
                      <option value="searxng">SearXNG</option>
                    </select>
                  </div>

                  <div v-if="config.searchProvider === 'searxng'" class="row">
                    <span class="row-text">
                      <span class="row-title">SearXNG URL</span>
                      <span class="row-sub">Instance base URL. Sofia uses the instance JSON API when it is enabled and falls back to the result page otherwise.</span>
                    </span>
                    <input
                      v-model="searxngDraft"
                      class="field-input cfg-input"
                      placeholder="http://localhost:8080"
                      autocomplete="off"
                      spellcheck="false"
                      @change="saveSearxng"
                      @keyup.enter="saveSearxng"
                    />
                  </div>
                </div>

                <dl class="srv-cfg-list">
                  <div class="srv-cfg-item">
                    <dt>Data directory</dt>
                    <dd>{{ config.dataDir || '—' }}</dd>
                  </div>
                  <div class="srv-cfg-item">
                    <dt>Config file</dt>
                    <dd>{{ config.configPath || '—' }}</dd>
                  </div>
                </dl>
              </template>
            </div>

            <!-- Hand another device a one-time pairing code. -->
            <div class="pair-row">
              <span class="row-text">
                <span class="row-title">{{ t('pair.section') }}</span>
                <span class="row-sub">{{ t('pair.sectionSub') }}</span>
              </span>
              <button class="mini-btn" type="button" @click="showPairing = true">
                <SofiaIcon name="link" :size="'0.9rem'" />
                {{ t('pair.open') }}
              </button>
            </div>
          </template>

          <!-- Models -->
          <template v-else-if="tab === 'models'">
            <p class="tab-intro">
              {{ connectedProviders }} of {{ providers.length }} providers connected.
            </p>

            <p v-if="catalogError" class="form-note form-note--error">{{ catalogError }}</p>

            <div class="add-actions add-actions--start">
              <button class="add-btn-lg" type="button" @click="showAddProvider = !showAddProvider">
                <SofiaIcon name="plus" :size="'0.95rem'" />
                {{ t('settings.models.addProvider') }}
              </button>
              <button class="mini-btn" type="button" :disabled="rediscovering" @click="rediscover">
                <SofiaIcon name="refresh" :size="'0.85rem'" :class="{ spinning: rediscovering }" />
                {{ rediscovering ? 'Refreshing…' : 'Refresh / rediscover' }}
              </button>
            </div>

            <div v-if="showAddProvider" class="add-form">
              <div class="field">
                <label class="field-label">Provider</label>
                <div v-if="presetId === null" class="integ-list">
                  <button
                    v-for="p in presets"
                    :key="p.id"
                    class="integ-item"
                    type="button"
                    @click="pickPreset(p.id)"
                  >
                    <SofiaIcon name="plug" :size="'0.85rem'" />
                    <span class="sofia-truncate">{{ p.name }}</span>
                  </button>
                  <button class="integ-item" type="button" @click="pickPreset('')">
                    <SofiaIcon name="plus" :size="'0.85rem'" />
                    <span class="sofia-truncate">Custom (OpenAI-compatible / Responses / Anthropic)</span>
                  </button>
                </div>
                <div v-else class="integ-picked">
                  <SofiaIcon name="plug" :size="'0.85rem'" />
                  <span class="sofia-truncate">{{ selectedPreset?.name ?? 'Custom endpoint' }}</span>
                  <button class="icon-mini" type="button" aria-label="Pick another provider" @click="clearPreset">
                    <SofiaIcon name="x" :size="'0.75rem'" />
                  </button>
                </div>
              </div>

              <template v-if="presetId !== null">
                <p v-if="selectedPreset?.note" class="form-note">{{ selectedPreset.note }}</p>
                <div v-if="selectedPreset?.auth === 'github-device'" class="field">
                  <label class="field-label">GitHub sign-in</label>
                  <div v-if="copilotFlow" class="device-flow">
                    <span>Enter this code at {{ copilotFlow.verificationUri }}</span>
                    <code class="device-code">{{ copilotFlow.userCode }}</code>
                    <span class="form-note">Waiting for approval…</span>
                  </div>
                  <div class="add-actions add-actions--start">
                    <button class="mini-btn" type="button" :disabled="copilotBusy" @click="startCopilotLogin">
                      <SofiaIcon name="link" :size="'0.85rem'" />
                      {{ copilotFlow ? 'Restart sign-in' : 'Sign in with GitHub' }}
                    </button>
                  </div>
                </div>                <div class="field">
                  <label class="field-label">Name</label>
                  <input v-model="newProvider.name" class="field-input" placeholder="My provider" autocomplete="off" />
                </div>
                <div class="field">
                  <label class="field-label">{{ t('settings.models.protocol') }}</label>
                  <div class="seg" role="group">
                    <button
                      class="seg-btn"
                      :class="{ 'seg-btn--active': newProvider.type === 'openai' }"
                      type="button"
                      @click="newProvider.type = 'openai'"
                    >{{ t('settings.models.openai') }}</button>
                    <button
                      class="seg-btn"
                      :class="{ 'seg-btn--active': newProvider.type === 'openai-responses' }"
                      type="button"
                      @click="newProvider.type = 'openai-responses'"
                    >{{ t('settings.models.openaiResponses') }}</button>
                    <button
                      class="seg-btn"
                      :class="{ 'seg-btn--active': newProvider.type === 'anthropic' }"
                      type="button"
                      @click="newProvider.type = 'anthropic'"
                    >{{ t('settings.models.anthropic') }}</button>
                  </div>
                </div>
                <div class="field">
                  <label class="field-label">{{ t('settings.models.baseUrl') }}</label>
                  <input
                    v-model="newProvider.baseUrl"
                    class="field-input"
                    placeholder="https://api.example.com/v1"
                    autocomplete="off"
                    spellcheck="false"
                  />
                </div>
                <div class="field">
                  <label class="field-label">{{ t('settings.models.token') }}</label>
                  <input
                    v-model="newProvider.apiKey"
                    type="password"
                    class="field-input"
                    :placeholder="selectedPreset?.envKey ? `sk-…  (or set ${selectedPreset.envKey} on the server)` : 'sk-…'"
                    autocomplete="off"
                  />
                </div>
              </template>

              <div class="add-actions">
                <button class="mini-btn" type="button" :disabled="!canProbe || loadingModels" @click="loadModels">
                  <SofiaIcon name="refresh" :size="'0.85rem'" />
                  {{ loadingModels ? 'Loading…' : t('settings.models.loadModels') }}
                </button>
                <button class="done-btn" type="button" :disabled="!canAddProvider || connecting" @click="submitProvider">
                  {{ connecting ? 'Saving…' : t('settings.models.addProvider') }}
                </button>
              </div>
              <p v-if="modelsNote" class="form-note">{{ modelsNote }}</p>
              <p v-if="connectError" class="form-note form-note--error">{{ connectError }}</p>
              <p v-if="connectDone" class="form-note">Provider saved — its models are listed below.</p>
              <div v-if="loadedModels.length" class="loaded-models">
                <div class="field-label">{{ loadedModels.length }} models found</div>
                <div class="model-grid">
                  <div v-for="m in loadedModels" :key="m.id" class="model-chip">
                    <span class="status-dot status-dot--on" />
                    <SofiaIcon :name="m.local ? 'cpu' : 'globe'" :size="'0.85rem'" />
                    <span class="model-chip-label sofia-truncate">{{ m.label }}</span>
                    <span class="model-chip-ctx">{{ m.context }}</span>
                  </div>
                </div>
              </div>
              <div class="add-actions">
                <button class="mini-btn" type="button" @click="resetProviderForm">Cancel</button>
              </div>
            </div>

            <div class="section-label">{{ t('settings.models.providers') }}</div>
            <div v-if="catalogLoading && !providers.length" class="tab-intro">Loading providers…</div>
            <p v-else-if="!providers.length" class="tab-intro">
              No providers yet. Start a local runtime (Ollama, llama.cpp, LM Studio…) and refresh, or add one above.
            </p>
            <div class="cards">
              <div v-for="p in providers" :key="p.id" class="card">
                <div class="card-main">
                  <SofiaIcon :name="p.kind === 'local' ? 'cpu' : 'globe'" :size="'1rem'" />
                  <div class="card-text">
                    <div class="card-title">
                      {{ p.name }}
                      <span class="kind-tag">{{ p.kind }}</span>
                    </div>
                    <div class="card-sub sofia-truncate" :title="p.baseUrl">{{ p.detail || p.baseUrl }}</div>
                  </div>
                </div>
                <div class="card-side">
                  <span class="status" :class="`status--${p.enabled ? p.status : 'stopped'}`">
                    <span class="dot" />{{ p.enabled ? statusLabel(p.status) : 'Disabled' }}
                  </span>
                  <div class="card-actions">
                    <button
                      class="mini-btn"
                      type="button"
                      :aria-expanded="managingId === p.id"
                      @click="toggleManage(p)"
                    >
                      <SofiaIcon name="cpu" :size="'0.8rem'" />
                      {{ t('settings.models.manage') }}
                    </button>
                    <button
                      class="mini-btn"
                      type="button"
                      :disabled="providerBusy === p.id"
                      @click="toggleProvider(p)"
                    >
                      {{ p.enabled ? 'Disable' : 'Enable' }}
                    </button>
                    <button
                      v-if="p.type === 'openai'"
                      class="mini-btn"
                      type="button"
                      :aria-pressed="p.preserveThinking"
                      title="Resend earlier reasoning (reasoning_content) with the history"
                      :disabled="providerBusy === p.id"
                      @click="togglePreserveThinking(p)"
                    >
                      Preserve thinking: {{ p.preserveThinking ? 'on' : 'off' }}
                    </button>
                    <button
                      v-if="p.custom"
                      class="icon-mini"
                      type="button"
                      aria-label="Remove provider"
                      :disabled="providerBusy === p.id"
                      @click="removeProvider(p)"
                    >
                      <SofiaIcon name="trash" :size="'0.8rem'" />
                    </button>
                  </div>
                </div>

                <!-- Per-provider model manager: unchecked models are hidden everywhere. -->
                <div v-if="managingId === p.id" class="card-manage">
                  <div class="manage-bar">
                    <input
                      v-model="modelFilter"
                      class="field-input manage-filter"
                      :placeholder="t('settings.models.filter')"
                      autocomplete="off"
                      spellcheck="false"
                    />
                    <button
                      class="mini-btn"
                      type="button"
                      :disabled="!visibleProviderModels(p).length"
                      @click="setModelsEnabled(visibleProviderModels(p), true)"
                    >{{ t('settings.models.selectAll') }}</button>
                    <button
                      class="mini-btn"
                      type="button"
                      :disabled="!visibleProviderModels(p).length"
                      @click="setModelsEnabled(visibleProviderModels(p), false)"
                    >{{ t('settings.models.deselectAll') }}</button>
                  </div>
                  <p v-if="!providerModels(p).length" class="form-note">{{ t('settings.models.noModels') }}</p>
                  <p v-else class="form-note">
                    {{ providerModels(p).filter((m) => m.enabled !== false).length }} / {{ providerModels(p).length }}
                    {{ t('settings.models.enabledCount') }}
                  </p>
                  <div class="manage-list">
                    <label v-for="m in visibleProviderModels(p)" :key="m.id" class="manage-item" :title="m.id">
                      <input
                        type="checkbox"
                        :checked="m.enabled !== false"
                        @change="setModelEnabled(m, ($event.target as HTMLInputElement).checked)"
                      />
                      <span class="manage-label sofia-truncate">{{ m.label }}</span>
                      <button
                        type="button"
                        class="kind-tag"
                        :class="{ 'kind-tag--on': m.singleClient }"
                        :aria-pressed="!!m.singleClient"
                        title="Single client: serves one conversation at a time (subagents first, then sessions)"
                        @click.prevent="setModelSingleClient(m, !m.singleClient)"
                      >
                        {{ m.singleClient ? 'single client ✓' : 'single client' }}
                      </button>
                      <span class="model-chip-ctx">{{ m.context }}</span>
                    </label>
                  </div>
                </div>
              </div>
            </div>

            <div class="section-label">{{ t('settings.models.available') }}</div>
            <div v-if="catalogLoading && !models.length" class="tab-intro">Loading models…</div>
            <div class="model-grid">
              <div
                v-for="m in enabledModels"
                :key="m.id"
                class="model-chip"
                :title="`${m.provider} · ${m.id}`"
              >
                <span class="status-dot" :class="m.connected ? 'status-dot--on' : 'status-dot--off'" />
                <SofiaIcon :name="m.local ? 'cpu' : 'globe'" :size="'0.85rem'" />
                <span class="model-chip-label sofia-truncate">{{ m.label }}</span>
                <span v-if="m.local" class="kind-tag">local</span>
                <span class="model-chip-ctx">{{ m.context }}</span>
              </div>
            </div>
          </template>

          <template v-else-if="tab === 'skills'">
            <!-- Global skills, activated per session. -->
            <SkillsSection />
          </template>

          <!-- MCP -->
          <template v-else-if="tab === 'mcp'">
            <p class="tab-intro">
              {{ connectedMcp }} of {{ mcpServers.length }} servers connected.
            </p>

            <p v-if="mcpError" class="form-note form-note--error">{{ mcpError }}</p>

            <button class="add-btn-lg" type="button" @click="showAddMcp = !showAddMcp">
              <SofiaIcon name="plus" :size="'0.95rem'" />
              {{ t('settings.mcp.addServer') }}
            </button>

            <div v-if="showAddMcp" class="add-form">
              <div class="field">
                <label class="field-label">Name</label>
                <input v-model="newMcp.name" class="field-input" placeholder="brave-search" />
              </div>

              <!-- Transport: local command vs remote web endpoint. -->
              <div class="field">
                <label class="field-label">{{ t('settings.mcp.command') }} / {{ t('settings.mcp.web') }}</label>
                <div class="seg" role="group">
                  <button
                    class="seg-btn"
                    :class="{ 'seg-btn--active': newMcp.transport === 'command' }"
                    type="button"
                    @click="newMcp.transport = 'command'"
                  >{{ t('settings.mcp.command') }}</button>
                  <button
                    class="seg-btn"
                    :class="{ 'seg-btn--active': newMcp.transport === 'web' }"
                    type="button"
                    @click="newMcp.transport = 'web'"
                  >{{ t('settings.mcp.web') }}</button>
                </div>
              </div>

              <div v-if="newMcp.transport === 'command'" class="field">
                <label class="field-label">{{ t('settings.mcp.command') }}</label>
                <input
                  v-model="newMcp.command"
                  class="field-input"
                  placeholder="npx @modelcontextprotocol/server-brave-search"
                />
              </div>
              <div v-else class="field">
                <label class="field-label">{{ t('settings.mcp.url') }}</label>
                <input v-model="newMcp.url" class="field-input" placeholder="https://mcp.example.com/mcp" />
              </div>

              <!-- Web transport auth: optional bearer token. -->
              <div v-if="newMcp.transport === 'web'" class="field">
                <label class="field-label">{{ t('settings.mcp.token') }}</label>
                <input v-model="newMcp.token" class="field-input" type="password" placeholder="sk-..." />
              </div>

              <div class="add-actions">
                <button class="mini-btn" type="button" @click="showAddMcp = false">Cancel</button>
                <button class="done-btn" type="button" :disabled="!canAddMcp || addingMcp" @click="submitMcp">
                  {{ addingMcp ? 'Adding…' : t('settings.mcp.addServer') }}
                </button>
              </div>
              <p v-if="addMcpError" class="form-note form-note--error">{{ addMcpError }}</p>
            </div>

            <div class="section-label">Servers</div>
            <div v-if="mcpLoading && !mcpServers.length" class="tab-intro">Loading servers…</div>
            <p v-else-if="!mcpServers.length" class="tab-intro">
              No MCP servers registered yet. Add one above — it is stored on Sofia.Server, not in this browser.
            </p>
            <div v-if="mcpServers.length" class="cards cards--after-add">
              <div v-for="s in mcpServers" :key="s.id" class="card">
                <div class="card-main">
                  <SofiaIcon name="plug" :size="'1rem'" />
                  <div class="card-text">
                    <div class="card-title">
                      <span class="kind-tag sofia-mono">{{ s.transport === 'web' ? 'web' : 'cmd' }}</span>
                      {{ s.name }}
                    </div>
                    <div class="card-sub sofia-truncate" :title="s.transport === 'web' ? s.url : s.command">
                      {{ s.transport === 'web' ? s.url : s.command }}
                    </div>
                    <div v-if="s.error" class="card-sub sofia-truncate" :title="s.error">{{ s.error }}</div>
                    <div v-else-if="s.tools" class="card-desc sofia-truncate" :title="s.toolNames.join(', ')">
                      {{ s.tools }} tools
                    </div>
                  </div>
                </div>
                <div class="card-side">
                  <span class="status" :class="`status--${s.status}`">
                    <span class="dot" />{{ statusLabel(s.status) }}
                  </span>
                  <div class="card-actions">
                    <button
                      class="mini-btn"
                      type="button"
                      :disabled="mcpBusy === s.id"
                      @click="s.status === 'error' ? mcpAction(s, 'connect') : toggleMcp(s)"
                    >
                      {{ s.status === 'error' ? 'Restart' : s.status === 'connected' || s.status === 'pending' ? 'Stop' : 'Start' }}
                    </button>
                    <button
                      class="icon-mini"
                      type="button"
                      aria-label="Remove server"
                      :disabled="mcpBusy === s.id"
                      @click="removeMcp(s)"
                    >
                      <SofiaIcon name="trash" :size="'0.8rem'" />
                    </button>
                  </div>
                </div>
              </div>
            </div>
          </template>

          <!-- Shortcuts -->
          <template v-else-if="tab === 'shortcuts'">
            <p class="tab-intro">{{ t('shortcuts.intro') }}</p>

            <template v-for="group in SHORTCUT_GROUPS" :key="group">
              <div class="section-label">{{ t(`shortcuts.group.${group}`) }}</div>
              <div class="sc-list">
                <div
                  v-for="s in shortcuts.filter((x) => x.group === group)"
                  :key="s.id"
                  class="sc-row"
                  :class="{ 'sc-row--recording': recordingId === s.id }"
                >
                  <SofiaIcon class="sc-icon" :name="s.icon" :size="'0.95rem'" />
                  <span class="sc-label">{{ t(`shortcut.${s.id}`) }}</span>

                  <button
                    class="sc-keys"
                    type="button"
                    :title="t('shortcuts.edit')"
                    :aria-label="`${t('shortcuts.edit')}: ${t(`shortcut.${s.id}`)}`"
                    @click="recordingId === s.id ? stopRecording() : startRecording(s.id)"
                  >
                    <span v-if="recordingId === s.id" class="sc-hint">{{ t('shortcuts.press') }}</span>
                    <span v-else-if="!bindingOf(s.id)" class="sc-hint">{{ t('shortcuts.unbound') }}</span>
                    <template v-else>
                      <kbd v-for="(k, i) in comboKeys(bindingOf(s.id))" :key="i" class="sc-key">{{ k }}</kbd>
                    </template>
                  </button>

                  <span
                    v-if="bindingOf(s.id) && isBrowserReserved(bindingOf(s.id))"
                    class="sc-warn"
                    :title="t('shortcuts.reserved')"
                  >
                    <SofiaIcon name="alert" :size="'0.85rem'" />
                  </span>
                  <button
                    class="sc-reset"
                    type="button"
                    :title="t('shortcuts.reset')"
                    :aria-label="t('shortcuts.reset')"
                    :disabled="!isCustomized(s.id)"
                    @click="resetBinding(s.id)"
                  >
                    <SofiaIcon name="undo" :size="'0.85rem'" />
                  </button>
                  <p v-if="recordingId === s.id && recordError" class="sc-error">{{ recordError }}</p>
                </div>
              </div>
            </template>

            <button class="sc-reset-all" type="button" @click="resetAllBindings">
              {{ t('shortcuts.resetAll') }}
            </button>
          </template>

          <!-- Appearance -->
          <template v-else>
            <p class="tab-intro">Cosmetic preferences. Saved to session storage.</p>

            <div class="section-label">Accent</div>
            <div class="accent-row">
              <button
                v-for="a in ['brown', 'beige', 'slate']"
                :key="a"
                class="accent-swatch"
                :class="[`accent-swatch--${a}`, { 'accent-swatch--active': settings.appearance.accent === a }]"
                type="button"
                @click="settings.appearance.accent = a as 'brown' | 'beige' | 'slate'"
              >
                <span class="accent-name">{{ a }}</span>
              </button>
              <button
                class="accent-swatch accent-swatch--custom"
                :class="{ 'accent-swatch--active': settings.appearance.accent === 'custom' }"
                :style="{ '--sw': settings.appearance.customColor }"
                type="button"
                @click="settings.appearance.accent = 'custom'"
              >
                <span class="accent-name">custom</span>
              </button>
            </div>
            <div class="color-row">
              <input
                class="color-input"
                type="color"
                :value="settings.appearance.customColor"
                title="Pick your colour"
                @input="settings.appearance.accent = 'custom'; settings.appearance.customColor = ($event.target as HTMLInputElement).value"
              />
              <span class="color-hex sofia-mono">{{ settings.appearance.customColor }}</span>
            </div>

            <div class="section-label">Text size</div>
            <div class="seg">
              <button
                v-for="s in ['sm', 'md', 'lg']"
                :key="s"
                class="seg-btn"
                :class="{ 'seg-btn--active': settings.appearance.fontSize === s }"
                type="button"
                @click="settings.appearance.fontSize = s as 'sm' | 'md' | 'lg'"
              >{{ s.toUpperCase() }}</button>
            </div>

            <label class="row">
              <span class="row-text">
                <span class="row-title">Compact sidebar</span>
                <span class="row-sub">Tighten workspace rows.</span>
              </span>
              <button
                class="toggle"
                :class="{ 'toggle--on': settings.appearance.compactSidebar }"
                type="button"
                role="switch"
                :aria-checked="settings.appearance.compactSidebar"
                @click="settings.appearance.compactSidebar = !settings.appearance.compactSidebar"
              ><span class="knob" /></button>
            </label>
          </template>
        </div>

        <footer class="settings-foot">
          <button class="reset-btn" type="button" @click="resetSettings">{{ t('settings.reset') }}</button>
          <button class="done-btn" type="button" @click="emit('close')">{{ t('settings.done') }}</button>
        </footer>
      </div>

      <!-- SOF-19: pairing dialog stacks above this modal. -->
      <PairingDialog :open="showPairing" @close="showPairing = false" />
    </div>
  </Teleport>
</template>

<style scoped>
.settings-overlay {
  position: fixed;
  inset: 0;
  z-index: 2000;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  background: rgba(0, 0, 0, 0.55);
  backdrop-filter: blur(2px);
}

/* Fixed height so switching tabs never resizes the modal. */
.settings-modal {
  width: min(720px, 100%);
  height: min(640px, 92vh);
  display: flex;
  flex-direction: column;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 16px;
  box-shadow: 0 30px 80px rgba(0, 0, 0, 0.55);
  overflow: hidden;
}

.settings-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.9rem 1.1rem;
  border-bottom: 1px solid var(--sofia-border);
  flex-shrink: 0;
}

.settings-title {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  color: var(--sofia-primary);
}

.settings-title h2 {
  margin: 0;
  font-size: 1.0625rem;
  color: var(--sofia-text);
}

.close-btn {
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

.close-btn:hover {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

/* --- tabs --------------------------------------------------------------- */
.settings-tabs {
  display: flex;
  gap: 0.25rem;
  padding: 0.5rem 0.75rem;
  border-bottom: 1px solid var(--sofia-border);
  flex-shrink: 0;
}

.tab {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.4rem 0.7rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  font-size: 0.8125rem;
  cursor: pointer;
}

.tab:hover {
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
}

.tab--active {
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

/* --- body --------------------------------------------------------------- */
.settings-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 1rem 1.1rem;
}

.tab-intro {
  margin: 0 0 1rem;
  font-size: 0.8125rem;
  color: var(--sofia-text-muted);
}

.section-label {
  font-size: 0.625rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
  margin: 1rem 0 0.5rem;
}

/* --- shortcuts tab ------------------------------------------------------- */
.sc-list {
  display: flex;
  flex-direction: column;
}

.sc-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.6rem;
  padding: 0.45rem 0.4rem;
  border-radius: 8px;
}

.sc-row:hover,
.sc-row--recording {
  background: var(--sofia-surface-2);
}

.sc-icon {
  color: var(--sofia-text-muted);
  flex-shrink: 0;
}

.sc-label {
  flex: 1;
  min-width: 0;
  font-size: 0.875rem;
  color: var(--sofia-text);
}

.sc-keys {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  min-width: 7rem;
  justify-content: flex-end;
  padding: 0.25rem 0.45rem;
  border: 1px solid transparent;
  border-radius: 8px;
  background: transparent;
  cursor: pointer;
}

.sc-keys:hover {
  border-color: var(--sofia-border-strong);
}

.sc-row--recording .sc-keys {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
}

.sc-key {
  padding: 0.1rem 0.4rem;
  border: 1px solid var(--sofia-border-strong);
  border-bottom-width: 2px;
  border-radius: 6px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-family: var(--font-family-monospace);
  font-size: 0.75rem;
}

.sc-hint {
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
}

.sc-warn {
  display: inline-flex;
  color: var(--sofia-warn);
  cursor: help;
}

.sc-reset {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.sc-reset:hover:not(:disabled) {
  background: var(--sofia-surface-3);
  color: var(--sofia-text);
}

.sc-reset:disabled {
  opacity: 0.3;
  cursor: default;
}

.sc-error {
  flex-basis: 100%;
  margin: 0;
  text-align: right;
  font-size: 0.75rem;
  color: var(--sofia-err);
}

.sc-reset-all {
  margin-top: 1rem;
  padding: 0.4rem 0.75rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  font-size: 0.8125rem;
  cursor: pointer;
}

.sc-reset-all:hover {
  color: var(--sofia-text);
  border-color: var(--sofia-primary);
}

/* --- rows / toggles ----------------------------------------------------- */
.rows {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.6rem 0.15rem;
  cursor: pointer;
}

.row-text {
  display: flex;
  flex-direction: column;
  gap: 0.1rem;
  min-width: 0;
}

.row-title {
  font-size: 0.875rem;
  color: var(--sofia-text);
}

.row-sub {
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
}

/* Sofia.Server config block under the local toggles. */
.srv-cfg {
  margin-top: 1.25rem;
  padding: 0.7rem 0.8rem;
  border: 1px solid var(--sofia-border);
  border-radius: 10px;
  background: var(--sofia-surface);
}

/* Remote access row (opens the pairing dialog). */
.pair-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 1rem;
  margin-top: 1.25rem;
  padding: 0.7rem 0.8rem;
  border: 1px solid var(--sofia-border);
  border-radius: 10px;
  background: var(--sofia-surface);
}

.srv-cfg-head {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.45rem;
}

.srv-cfg-badge {
  padding: 0.1rem 0.45rem;
  border-radius: 999px;
  background: var(--sofia-surface-2);
  font-size: 0.625rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.srv-cfg-list {
  margin: 0;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.srv-cfg-item {
  display: flex;
  gap: 0.75rem;
  align-items: baseline;
}

.srv-cfg-item dt {
  flex-shrink: 0;
  width: 7.5rem;
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
}

.srv-cfg-item dd {
  margin: 0;
  min-width: 0;
  overflow-wrap: anywhere;
  font-size: 0.75rem;
  color: var(--sofia-text);
}

.toggle {
  position: relative;
  width: 38px;
  height: 22px;
  flex-shrink: 0;
  border: none;
  border-radius: 999px;
  background: var(--sofia-surface-3);
  cursor: pointer;
  transition: background 0.15s ease;
}

.toggle--on {
  background: var(--sofia-primary);
}

.knob {
  position: absolute;
  top: 3px;
  left: 3px;
  width: 16px;
  height: 16px;
  border-radius: 999px;
  background: #fff;
  transition: transform 0.15s ease;
}

.toggle--on .knob {
  transform: translateX(16px);
}

/* --- forms -------------------------------------------------------------- */
.add-btn-lg {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.45rem 0.8rem;
  border: 1px dashed var(--sofia-border-strong);
  border-radius: 9px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-size: 0.8125rem;
  font-weight: 550;
  cursor: pointer;
}

.add-btn-lg:hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.add-form {
  margin-top: 0.6rem;
  padding: 0.8rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border);
  border-radius: 10px;
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
}

.field {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
}

.field-label {
  font-size: 0.6875rem;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.field-input {
  padding: 0.45rem 0.6rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface);
  color: var(--sofia-text);
  font-size: 0.8125rem;
  outline: none;
}

.field-input:focus {
  border-color: var(--sofia-primary);
}

.field-input::placeholder {
  color: var(--sofia-text-faint);
}

.add-actions {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
}

.loaded-models {
  border-top: 1px solid var(--sofia-border);
  padding-top: 0.6rem;
}

/* --- preset picker in the add-provider form ------------------------------ */
.integ-list {
  margin-top: 0.3rem;
  max-height: 9.5rem;
  overflow-y: auto;
  border: 1px solid var(--sofia-border);
  border-radius: 8px;
  background: var(--sofia-surface);
}

.integ-item {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  width: 100%;
  padding: 0.4rem 0.6rem;
  border: none;
  border-bottom: 1px solid var(--sofia-border);
  background: transparent;
  color: var(--sofia-text);
  font-size: 0.8125rem;
  text-align: left;
  cursor: pointer;
}

.integ-item:last-child {
  border-bottom: none;
}

.integ-item:hover {
  background: var(--sofia-surface-2);
}

.integ-picked {
  margin-top: 0.3rem;
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.4rem 0.6rem;
  border: 1px solid var(--sofia-primary);
  border-radius: 8px;
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
  font-size: 0.8125rem;
}

.integ-picked .icon-mini {
  margin-left: auto;
}

.add-actions--start {
  justify-content: flex-start;
  align-items: center;
  margin-bottom: 0.25rem;
}

.cfg-input {
  width: 14rem;
  flex-shrink: 0;
}

.cfg-input--num {
  width: 5.5rem;
}

.srv-cfg .rows {
  margin-bottom: 0.6rem;
}

.form-note {
  margin: 0;
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
}

.form-note--error {
  color: var(--sofia-danger, #c0605a);
}

.device-flow {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 0.35rem;
  margin-bottom: 0.5rem;
  font-size: 0.8rem;
}

.device-code {
  font-size: 1.1rem;
  letter-spacing: 0.15em;
  padding: 0.15rem 0.5rem;
  border-radius: 0.3rem;
  user-select: all;
}

.spinning {
  animation: spin-720 0.7s linear infinite;
}

@keyframes spin-720 {
  to {
    transform: rotate(360deg);
  }
}

.cards--after-add {
  margin-top: 0.9rem;
}

/* --- cards (providers / mcp) -------------------------------------------- */
.cards {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.card {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.65rem 0.75rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border);
  border-radius: 10px;
}

.card-main {
  display: flex;
  align-items: flex-start;
  gap: 0.55rem;
  color: var(--sofia-text-muted);
  min-width: 0;
}

.card-text {
  min-width: 0;
}

.card-title {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.875rem;
  font-weight: 600;
  color: var(--sofia-text);
}

.kind-tag {
  font-size: 0.625rem;
  font-weight: 600;
  padding: 0.05rem 0.35rem;
  border-radius: 999px;
  background: var(--sofia-surface);
  color: var(--sofia-text-faint);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.card-sub {
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
  margin-top: 0.15rem;
}

.card-desc {
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
  margin-top: 0.15rem;
}

.card-side {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 0.35rem;
  flex-shrink: 0;
}

.card-actions {
  display: flex;
  align-items: center;
  gap: 0.3rem;
}

.status {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  font-size: 0.6875rem;
  color: var(--sofia-text-muted);
}

.status .dot {
  width: 7px;
  height: 7px;
  border-radius: 999px;
}

.status--connected .dot { background: var(--sofia-ok); }
.status--error .dot,
.status--failed .dot { background: var(--sofia-err); }
.status--stopped .dot,
.status--disconnected .dot,
.status--disabled .dot,
.status--pending .dot,
.status--needs_auth .dot { background: var(--sofia-text-faint); }

.mini-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  padding: 0.25rem 0.55rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text);
  font-size: 0.75rem;
  cursor: pointer;
}

.mini-btn:hover:not(:disabled) {
  background: var(--sofia-surface-3);
}

.mini-btn:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.vol-control {
  display: inline-flex;
  align-items: center;
  gap: 0.6rem;
}

.vol-slider {
  width: 180px;
  accent-color: var(--sofia-primary);
  cursor: pointer;
}

.vol-slider:disabled {
  cursor: not-allowed;
}

.vol-value {
  min-width: 2.6rem;
  text-align: right;
  font-size: 0.75rem;
  color: var(--sofia-text-dim, var(--sofia-text-faint));
  font-variant-numeric: tabular-nums;
}

.vol-row--off {
  opacity: 0.55;
}

.icon-mini {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.icon-mini:hover {
  color: var(--sofia-err);
  border-color: var(--sofia-err);
}

/* --- model grid --------------------------------------------------------- */
.model-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 0.4rem;
}

.model-chip {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.45rem 0.6rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border);
  border-radius: 9px;
  color: var(--sofia-text-muted);
  font-size: 0.8125rem;
}

.status-dot {
  width: 7px;
  height: 7px;
  border-radius: 999px;
  flex-shrink: 0;
}

.status-dot--on { background: var(--sofia-ok); }
.status-dot--off { background: var(--sofia-text-faint); }

.model-chip-label {
  flex: 1;
  min-width: 0;
  color: var(--sofia-text);
}

.model-chip-ctx {
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
}

/* --- per-provider model manager ------------------------------------------ */
.card {
  flex-wrap: wrap;
}

.card-manage {
  flex-basis: 100%;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  padding-top: 0.55rem;
  border-top: 1px solid var(--sofia-border);
}

.manage-bar {
  display: flex;
  align-items: center;
  gap: 0.4rem;
}

.manage-filter {
  flex: 1;
  min-width: 0;
}

.manage-list {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 0.2rem 0.6rem;
  max-height: 16rem;
  overflow-y: auto;
}

.manage-item {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.25rem 0.35rem;
  border-radius: 7px;
  font-size: 0.8125rem;
  color: var(--sofia-text);
  cursor: pointer;
}

.manage-item:hover {
  background: var(--sofia-surface-3);
}

.manage-item input {
  accent-color: var(--sofia-primary);
  flex-shrink: 0;
}

.manage-label {
  flex: 1;
  min-width: 0;
}

/* --- appearance --------------------------------------------------------- */
.accent-row {
  display: flex;
  gap: 0.5rem;
}

.accent-swatch {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  gap: 0.3rem;
  padding: 0.5rem 0.75rem;
  border: 2px solid var(--sofia-border);
  border-radius: 10px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.accent-swatch--active {
  border-color: var(--sofia-primary);
  color: var(--sofia-text);
}

.accent-swatch--brown { --sw: #a9744f; }
.accent-swatch--beige { --sw: #e4d3ac; }
.accent-swatch--slate { --sw: #7b7b84; }

.accent-swatch::before {
  content: '';
  width: 28px;
  height: 28px;
  border-radius: 999px;
  background: var(--sw);
}

.accent-name {
  font-size: 0.75rem;
  text-transform: capitalize;
}

.accent-swatch--custom::before {
  background: var(--sw, var(--sofia-primary));
}

.color-row {
  display: flex;
  align-items: center;
  gap: 0.55rem;
  margin-top: 0.6rem;
}

.color-input {
  width: 34px;
  height: 28px;
  padding: 0;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: transparent;
  cursor: pointer;
}

.color-hex {
  color: var(--sofia-text-muted);
}

.seg {
  display: inline-flex;
  padding: 0.2rem;
  gap: 0.15rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border);
  border-radius: 9px;
}

.seg-btn {
  padding: 0.3rem 0.7rem;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
  font-weight: 600;
  cursor: pointer;
}

.seg-btn--active {
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

/* --- web MCP auth (token / SSO) ----------------------------------------- */
.sso-row {
  display: inline-flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.8125rem;
  color: var(--sofia-text);
  cursor: pointer;
  user-select: none;
}

.sso-check {
  width: 16px;
  height: 16px;
  accent-color: var(--sofia-primary);
  cursor: pointer;
}

/* --- footer ------------------------------------------------------------- */
.settings-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.75rem 1.1rem;
  border-top: 1px solid var(--sofia-border);
  flex-shrink: 0;
}

.reset-btn {
  padding: 0.4rem 0.7rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text-muted);
  font-size: 0.8125rem;
  cursor: pointer;
}

.reset-btn:hover {
  background: var(--sofia-surface-2);
  color: var(--sofia-err);
}

.done-btn {
  padding: 0.45rem 1rem;
  border: none;
  border-radius: 8px;
  background: var(--sofia-primary);
  color: var(--sofia-primary-ink);
  font-size: 0.8125rem;
  font-weight: 600;
  cursor: pointer;
}

.done-btn:hover:not(:disabled) {
  background: var(--sofia-primary-strong);
}

.done-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}
</style>
