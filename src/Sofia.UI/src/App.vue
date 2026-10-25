<script setup lang="ts">
import { ref, watch } from 'vue'
import { MantineProvider } from '@mantine-vue/core'

import AppSidebar from '@/components/AppSidebar.vue'
import BackendGate from '@/components/BackendGate.vue'
import BrowserPanel from '@/components/BrowserPanel.vue'
import ChatComposer from '@/components/ChatComposer.vue'
import ChatHeader from '@/components/ChatHeader.vue'
import ChatMessages from '@/components/ChatMessages.vue'
import RightPanel from '@/components/RightPanel.vue'
import { useAppearance } from '@/composables/useAppearance'
import { useHarness } from '@/composables/useHarness'
import { useSettings } from '@/composables/useSettings'
import { useShortcut, useShortcutListener } from '@/composables/useShortcuts'
import { startSessionStatusWatcher } from '@/composables/useSessionStatus'
import { onServerEvent, useBackend } from '@/services/sofia'
import { setLocale } from '@/i18n'
import { sofiaTheme } from '@/styles/theme'

// Apply accent / font-size / compact-sidebar settings to the document.
useAppearance()

// Keep the UI language in sync with the persisted selection.
const { settings } = useSettings()
watch(() => settings.language, (code) => setLocale(code), { immediate: true })

// Backend gate: the shell renders only once Sofia.Server answers.
const { state: backend } = useBackend()

// Once the backend is ready, load the workspace tree and the model catalog.
const { refreshLiveSessions, refreshModels } = useHarness()
watch(
  () => backend.phase,
  (phase) => {
    if (phase !== 'ready') return
    void refreshModels().then(() => refreshLiveSessions())
    // Live badge watcher (session status snapshot + events).
    startSessionStatusWatcher()
  },
  { immediate: true },
)

const showRightPanel = ref(false)
const showBrowser = ref(false)

// The agent opened or used the browser in the conversation on screen: show it live.
const { activeConversation } = useHarness()
onServerEvent((event) => {
  const data = event.data as { open?: boolean; agent?: boolean } | null
  if (event.type === 'browser.updated' && data?.open && data.agent && event.sessionId === activeConversation.value?.id)
    showBrowser.value = true
})

// Global keyboard shortcuts (rebindable in Settings → Shortcuts).
useShortcutListener()
useShortcut('panel.toggle', () => {
  showRightPanel.value = !showRightPanel.value
})
</script>

<template>
  <MantineProvider :theme="sofiaTheme" force-color-scheme="dark">
    <BackendGate v-if="backend.phase !== 'ready'" />
    <div v-else class="app">
      <AppSidebar />
      <main class="main">
        <ChatHeader
          :panel-open="showRightPanel"
          :browser-open="showBrowser"
          @toggle-panel="showRightPanel = !showRightPanel"
          @toggle-browser="showBrowser = !showBrowser"
        />
        <ChatMessages />
        <ChatComposer />
      </main>
      <BrowserPanel :open="showBrowser" @close="showBrowser = false" />
      <RightPanel :open="showRightPanel" @close="showRightPanel = false" />
    </div>
  </MantineProvider>
</template>

<style scoped>
.app {
  display: flex;
  height: 100vh;
  overflow: hidden;
}

.main {
  position: relative;
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}
</style>
