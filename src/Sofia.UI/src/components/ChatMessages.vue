<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

import logoUrl from '@/assets/logo.png'
import { useHarness } from '@/composables/useHarness'
import { useI18n } from '@/i18n'
import ChatMessageItem from '@/components/ChatMessageItem.vue'
import SofiaIcon from '@/components/SofiaIcon.vue'
import { annotateCacheBreaks } from '@/services/cacheHealth'
import type { Message } from '@/types'

const { messages, activeConversation, activeWorkspace } = useHarness()
const { t } = useI18n()

const scroller = ref<HTMLElement | null>(null)
const content = ref<HTMLElement | null>(null)

/** True while the open conversation's run is working (not waiting on the user). */
const workingHere = computed(() => !!activeConversation.value?.working)

/** Per-message prompt-cache warnings, indexed like `messages`. */
const cacheBreaks = computed(() => annotateCacheBreaks(messages.value))

/** True once the LLM started answering (text, reasoning, tool calls or a prompt for the user). */
const answering = computed(() => {
  const last = messages.value[messages.value.length - 1]
  if (last?.role !== 'assistant') return false
  return !!(
    last.text?.trim() ||
    last.reasoning?.trim() ||
    last.toolCalls?.length ||
    last.permission ||
    last.question ||
    last.questionSet
  )
})

/** The spinner only covers the wait for the first output of a run. */
const waitingHere = computed(() => activeConversation.value?.status === 'waiting')
const showSpinner = computed(() => (workingHere.value || waitingHere.value) && !answering.value)

/** Follow-ups the server holds back (steer / queue), shown greyed until they reach the model. */
const pendingMessages = computed(() => {
  const conv = activeConversation.value
  if (!conv?.queuedMessages?.length) return []
  const shown = new Set(conv.messages.map((m) => m.id))
  return conv.queuedMessages
    .filter((q) => !shown.has(q.id))
    .map((q) => ({
      delivery: q.delivery,
      message: {
        id: q.id,
        sessionId: conv.id,
        role: 'user',
        text: q.text,
        createdAt: '',
        time: '',
        attachments: q.attachments?.map((a) => ({ id: a.id, name: a.name, kind: a.kind, size: a.size, mime: a.mime, url: a.url })),
      } satisfies Message,
    }))
})

// Auto-follow only while the user is parked at the bottom. Scrolling up
// releases it (so streamed output never drags the view back down); returning
// to the bottom, sending a message or switching conversation re-engages it.
const BOTTOM_EPSILON = 4
let following = true
let lastScrollTop = 0

function scrollToBottom(): void {
  const el = scroller.value
  if (!el) return
  el.scrollTop = el.scrollHeight
  lastScrollTop = el.scrollTop
}

function onScroll(): void {
  const el = scroller.value
  if (!el) return
  const distance = el.scrollHeight - el.scrollTop - el.clientHeight
  if (distance <= BOTTOM_EPSILON) following = true
  else if (el.scrollTop < lastScrollTop) following = false
  lastScrollTop = el.scrollTop
}

function followFromStart(): void {
  following = true
  void nextTick(scrollToBottom)
}

let resizeObserver: ResizeObserver | null = null

onMounted(() => {
  followFromStart()
  // Streamed text, reasoning folds and markdown layout all change the height.
  if (content.value && typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(() => {
      if (following) scrollToBottom()
    })
    resizeObserver.observe(content.value)
  }
})

onBeforeUnmount(() => resizeObserver?.disconnect())

// A message the user just sent always jumps to the end.
watch(
  () => messages.value.length,
  () => {
    if (messages.value[messages.value.length - 1]?.role === 'user') followFromStart()
  },
)

watch(
  () => pendingMessages.value.length,
  (count, before) => {
    if (count > before) followFromStart()
  },
)

watch(() => activeConversation.value?.id, followFromStart)
</script>

<template>
  <div ref="scroller" class="messages" @scroll.passive="onScroll">
    <div ref="content" class="messages-inner">
      <div v-if="messages.length === 0 && !workingHere" class="empty">
        <img class="empty-logo" :src="logoUrl" alt="" />
        <h2 class="empty-title">{{ t('chat.emptyTitle') }}</h2>
        <p class="empty-sub">
          You’re in <strong>{{ activeWorkspace?.name ?? 'this workspace' }}</strong>. Pick a model,
          set permissions and reasoning below, then send your first message.
        </p>
      </div>

      <ChatMessageItem v-for="(m, i) in messages" :key="m.id" :message="m"
        :streaming="workingHere && i === messages.length - 1" :cache-break="cacheBreaks[i] ?? null" />

      <!-- Working indicator: a lone spinner centred below the transcript, only until the LLM starts answering. -->
      <div v-if="showSpinner" class="working" role="status" :aria-label="t('chat.working')">
        <SofiaIcon class="spinner" name="loader" spin :paused="waitingHere" :size="'1.6rem'" />
      </div>

      <ChatMessageItem v-for="p in pendingMessages" :key="p.message.id" :message="p.message" :pending="p.delivery" />
    </div>
  </div>
</template>

<style scoped>
.messages {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  /* Chat wallpaper: a seamless doodle tile that stays put while the transcript
     scrolls, plus a faint paper grain over it. Both are kept deliberately quiet
     (tile motifs ~3–5%, grain 0.03) so they never fight the white transcript
     text; raise them only in small steps. */
  background-color: var(--sofia-bg);
  background-image:
    url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='140' height='140'%3E%3Cfilter id='n'%3E%3CfeTurbulence type='fractalNoise' baseFrequency='0.85' numOctaves='2' stitchTiles='stitch'/%3E%3C/filter%3E%3Crect width='140' height='140' filter='url(%23n)' opacity='0.02'/%3E%3C/svg%3E"),
    url('@/assets/chat-wallpaper.svg');
  background-repeat: repeat;
}

.messages-inner {
  max-width: 880px;
  margin: 0 auto;
  /* Reserve room for the frosted composer that overlays the transcript. The
     height comes from the composer itself, so the gap never grows stale when
     the input box shrinks or an attachment strip appears. */
  padding: 1.25rem 1.5rem calc(var(--sofia-composer-height) + 1.25rem);
}

/* --- empty state -------------------------------------------------------- */
.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  text-align: center;
  min-height: 46vh;
  gap: 0.5rem;
}

.empty-logo {
  width: 88px;
  height: 88px;
  border-radius: 24px;
  object-fit: cover;
  border: 1px solid var(--sofia-border-strong);
  box-shadow: 0 0 0 6px var(--sofia-accent-soft), 0 18px 40px rgba(0, 0, 0, 0.4);
  margin-bottom: 0.75rem;
}

.empty-title {
  margin: 0;
  font-size: 1.375rem;
  font-weight: 650;
  color: var(--sofia-text);
}

.empty-sub {
  margin: 0;
  max-width: 460px;
  font-size: 0.9375rem;
  line-height: 1.6;
  color: var(--sofia-text-muted);
}

.empty-sub strong {
  color: var(--sofia-accent);
  font-weight: 600;
}

/* --- working spinner ------------------------------------------------------ */
.working {
  display: flex;
  justify-content: center;
  padding: 1rem 0;
}

.spinner {
  color: var(--sofia-primary);
}
</style>
