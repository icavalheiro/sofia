<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useSettings } from '@/composables/useSettings'
import { useSound } from '@/composables/useSound'
import { sessionBadge as liveBadge } from '@/composables/useSessionStatus'
import { useI18n } from '@/i18n'
import { LANGUAGES } from '@/types'
import SofiaIcon from '@/components/SofiaIcon.vue'
import SessionPopover from '@/components/SessionPopover.vue'

const props = defineProps<{ panelOpen: boolean; browserOpen: boolean; tasksOpen?: boolean }>()
const emit = defineEmits<{ (e: 'togglePanel'): void; (e: 'toggleBrowser'): void; (e: 'toggleTasks'): void }>()

const { activeWorkspace, activeConversation } = useHarness()
const { settings } = useSettings()
const { playSound } = useSound()
const { t } = useI18n()

// LLM status surfaced in the chat for the active workspace.
const STATUS_ICON: Record<string, string> = {
  done: 'check',
  permission: 'alert',
  question: 'help',
  error: 'x',
}
const STATUS_TITLE: Record<string, string> = {
  waiting: 'Waiting for the model to be free…',
  working: 'Sofia is working on a taskâ€¦',
  done: 'Last task finished',
  permission: 'Waiting for your permission',
  question: 'Asking you a question',
  error: 'The last run failed',
}
// LLM status of the conversation currently open â€” scoped to this chat so other
// conversations working in the background don't light up every header.
const chatLlmStatus = computed<string>(() => {
  const c = activeConversation.value
  if (!c) return 'idle'
  if (c.working) return 'working'
  if (c.status !== 'idle') return c.status
  return liveBadge(c.id) ?? 'idle'
})

/** Chat groups aren't backed by a folder, so they show a "Chat" tag instead. */
const isChatGroup = computed(() => activeWorkspace.value?.kind === 'chat')

// --- language picker --------------------------------------------------------
const langOpen = ref(false)
const langRoot = ref<HTMLElement | null>(null)

function pickLanguage(code: string): void {
  settings.language = code
  langOpen.value = false
  playSound('click')
}

function onDocPointer(event: MouseEvent): void {
  if (langRoot.value && !langRoot.value.contains(event.target as Node)) langOpen.value = false
}
function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') langOpen.value = false
}
onMounted(() => {
  document.addEventListener('mousedown', onDocPointer)
  document.addEventListener('keydown', onKeydown)
})
onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocPointer)
  document.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <header class="chat-header">
    <h1 class="conversation-title sofia-truncate">
      {{ activeConversation?.title || 'New conversation' }}
    </h1>
    <div class="crumbs sofia-truncate">
      <SofiaIcon :name="isChatGroup ? (activeWorkspace?.icon ?? 'chat') : 'folder'" :size="'0.8125rem'" />
      <span class="crumb-name">{{ activeWorkspace?.name }}</span>
      <span v-if="isChatGroup" class="crumb-tag">{{ t('nav.chatTag') }}</span>
      <span v-else class="crumb-path">{{ activeWorkspace?.path }}</span>
    </div>

    <!-- LLM status for the open conversation (working / done / permission / question). -->
    <span
      v-if="chatLlmStatus !== 'idle'"
      class="llm-status"
      :class="`llm-status--${chatLlmStatus}`"
      :title="STATUS_TITLE[chatLlmStatus]"
    >
      <SofiaIcon v-if="chatLlmStatus === 'working' || chatLlmStatus === 'waiting'" name="loader" spin :paused="chatLlmStatus === 'waiting'" :size="'0.85rem'" />
      <SofiaIcon v-else :name="STATUS_ICON[chatLlmStatus]" :size="'0.85rem'" />
    </span>

    <div class="header-actions">
      <!-- Language picker (flags). -->
      <div ref="langRoot" class="lang-wrap">
        <button
          class="panel-toggle"
          :class="{ 'panel-toggle--active': langOpen }"
          type="button"
          :title="t('header.language')"
          :aria-label="t('header.language')"
          @click="playSound('click'); langOpen = !langOpen"
        >
          <SofiaIcon name="languages" :size="'1rem'" />
        </button>
        <div v-if="langOpen" class="lang-menu" role="menu">
          <button
            v-for="l in LANGUAGES"
            :key="l.code"
            class="lang-item"
            :class="{ 'lang-item--active': l.code === settings.language }"
            type="button"
            role="menuitem"
            @click="pickLanguage(l.code)"
          >
            <span class="lang-item-code sofia-mono">{{ l.code }}</span>
            <span class="lang-item-label">{{ l.label }}</span>
            <SofiaIcon v-if="l.code === settings.language" name="check" :size="'0.85rem'" />
          </button>
        </div>
      </div>

      <!-- Session actions: compact / revert-clear / stats / export / import. -->
      <SessionPopover />

      <button
        class="panel-toggle"
        :class="{ 'panel-toggle--active': browserOpen }"
        type="button"
        :title="t('header.browser')"
        :aria-label="t('header.browser')"
        @click="playSound('click'); emit('toggleBrowser')"
      >
        <SofiaIcon name="globe" :size="'1rem'" />
      </button>

      <button
        class="panel-toggle"
        :class="{ 'panel-toggle--active': props.tasksOpen }"
        type="button"
        :title="'Tasks'"
        :aria-label="'Tasks'"
        @click="playSound('click'); emit('toggleTasks')"
      >
        <SofiaIcon name="list" :size="'1rem'" />
      </button>

      <button
        class="panel-toggle"
        :class="{ 'panel-toggle--active': panelOpen }"
        type="button"
        :title="t('header.panel')"
        :aria-label="t('header.panel')"
        @click="playSound('click'); emit('togglePanel')"
      >
        <SofiaIcon name="panelRight" :size="'1rem'" />
      </button>
    </div>
  </header>
</template>

<style scoped>
/* Slim, title-only. The controls moved into the composer as chips. */
.chat-header {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.7rem 1.5rem 0.55rem;
  border-bottom: 1px solid var(--sofia-border);
}

.conversation-title {
  margin: 0;
  font-size: 1rem;
  font-weight: 650;
  letter-spacing: -0.01em;
  color: var(--sofia-text);
  flex-shrink: 0;
  max-width: 60%;
}

.crumbs {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.75rem;
  color: var(--sofia-text-faint);
  min-width: 0;
}

.crumb-name {
  color: var(--sofia-text-muted);
}

.crumb-path {
  opacity: 0.8;
}

/* "Chat" tag shown instead of a path for folder-less chat groups. */
.crumb-tag {
  padding: 0.05rem 0.4rem;
  border: 1px solid var(--sofia-border);
  border-radius: 999px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text-faint);
  font-size: 0.625rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

/* LLM status chip for the active workspace. */
.llm-status {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  border-radius: 50%;
  border: 1px solid var(--sofia-border);
  background: var(--sofia-surface-2);
}

.llm-status--done {
  color: var(--sofia-ok);
  border-color: color-mix(in srgb, var(--sofia-ok) 40%, transparent);
}

.llm-status--permission {
  color: var(--sofia-warn);
  border-color: color-mix(in srgb, var(--sofia-warn) 45%, transparent);
}

.llm-status--question,
.llm-status--working,
.llm-status--waiting {
  color: var(--sofia-primary);
  border-color: color-mix(in srgb, var(--sofia-primary) 45%, transparent);
}

.llm-status--error {
  color: var(--sofia-err);
  border-color: color-mix(in srgb, var(--sofia-err) 45%, transparent);
}

.header-actions {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  flex-shrink: 0;
}

.panel-toggle {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 30px;
  height: 30px;
  padding: 0 0.3rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.panel-toggle:hover {
  color: var(--sofia-text);
  background: var(--sofia-surface-3);
}

.panel-toggle--active {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

/* --- language picker ----------------------------------------------------- */
.lang-wrap {
  position: relative;
  display: inline-flex;
}

.lang-menu {
  position: absolute;
  top: calc(100% + 6px);
  right: 0;
  z-index: 60;
  min-width: 11rem;
  padding: 0.25rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  box-shadow: 0 16px 40px rgba(0, 0, 0, 0.45);
}

.lang-item {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  width: 100%;
  padding: 0.45rem 0.55rem;
  border: none;
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text);
  font-size: 0.8125rem;
  text-align: left;
  cursor: pointer;
}

.lang-item:hover {
  background: var(--sofia-surface-3);
}

.lang-item--active {
  background: var(--sofia-primary-soft);
}

.lang-item-code {
  width: 1.4rem;
  font-size: 0.6875rem;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.lang-item-label {
  flex: 1;
  min-width: 0;
}

.lang-item svg {
  color: var(--sofia-primary);
}
</style>
