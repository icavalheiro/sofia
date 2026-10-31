<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'

import logoUrl from '@/assets/logo_128.png'
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

/**
 * How many transcript messages stay rendered. Longer conversations keep the
 * newest window plus the pinned entries below and collapse every other block
 * into a centred "(...)" — the transcript forgets, the model never does (its
 * context is untouched; compaction owns that).
 */
const MESSAGE_WINDOW = 70

/** A rendered transcript row: a real message (with its index in `messages`) or an elided block. */
type TranscriptItem =
  | { kind: 'message'; message: Message; index: number }
  | { kind: 'gap'; key: string; count: number }

const transcript = computed<TranscriptItem[]>(() => {
  const all = messages.value
  if (all.length <= MESSAGE_WINDOW) return all.map((message, index) => ({ kind: 'message', message, index }))

  // Always readable: what the user asked, plus the first reply the model gave.
  const firstReply = all.findIndex((m) => m.role === 'assistant' && !m.compaction)
  const windowStart = all.length - MESSAGE_WINDOW

  const items: TranscriptItem[] = []
  let hidden = 0
  let hiddenFrom = 0
  for (let index = 0; index < all.length; index++) {
    const message = all[index]
    const visible = index >= windowStart || message.role === 'user' || index === firstReply
    if (visible) {
      if (hidden > 0) {
        items.push({ kind: 'gap', key: `gap-${hiddenFrom}`, count: hidden })
        hidden = 0
      }
      items.push({ kind: 'message', message, index })
      continue
    }
    if (hidden === 0) hiddenFrom = index
    hidden++
  }
  return items
})

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
let lastScrollHeight = 0

function scrollToBottom(): void {
  const el = scroller.value
  if (!el) return
  el.scrollTop = el.scrollHeight
  lastScrollTop = el.scrollTop
  lastScrollHeight = el.scrollHeight
}

function onScroll(): void {
  const el = scroller.value
  if (!el) return
  const distance = el.scrollHeight - el.scrollTop - el.clientHeight
  if (distance <= BOTTOM_EPSILON) following = true
  // Only treat an upward move as "user scrolled away" when the content did
  // not shrink: when truncation drops rendered rows the scroll height can fall
  // and the browser clamps scrollTop down, which must not disable auto-follow.
  else if (el.scrollTop < lastScrollTop && el.scrollHeight >= lastScrollHeight) following = false
  lastScrollTop = el.scrollTop
  lastScrollHeight = el.scrollHeight
}

function onEndKey(event: KeyboardEvent): void {
  if (event.key !== 'End') return
  const target = event.target as HTMLElement | null
  const editable = !!target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable)
  if (!scroller.value) return
  following = true
  if (editable) {
    // Let the textarea move its caret, then re-pin the transcript.
    window.setTimeout(() => scrollToBottom(), 0)
    return
  }
  event.preventDefault()
  requestAnimationFrame(() => scrollToBottom())
}

function followFromStart(): void {
  following = true
  void nextTick(scrollToBottom)
}

let resizeObserver: ResizeObserver | null = null

onMounted(() => {
  followFromStart()
  const el = scroller.value
  if (el) {
    lastScrollTop = el.scrollTop
    lastScrollHeight = el.scrollHeight
  }
  // Streamed text, reasoning folds and markdown layout all change the height.
  if (content.value && typeof ResizeObserver !== 'undefined') {
    resizeObserver = new ResizeObserver(() => {
      if (following) requestAnimationFrame(() => {
        if (following) scrollToBottom()
      })
    })
    resizeObserver.observe(content.value)
  }
  window.addEventListener('keydown', onEndKey)
})

onBeforeUnmount(() => {
  resizeObserver?.disconnect()
  window.removeEventListener('keydown', onEndKey)
})

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

// Keep pinned to the bottom when the transcript grows or its shape changes
// (e.g. crossing the 70-message truncation boundary) while auto-follow is on.
watch(
  () => messages.value.length,
  () => {
    if (following) void nextTick(scrollToBottom)
  },
)

watch(
  () => transcript.value.length,
  () => {
    if (following) void nextTick(scrollToBottom)
  },
)
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

      <template v-for="item in transcript" :key="item.kind === 'gap' ? item.key : item.message.id">
        <!-- Elided block: the transcript skips here, centred where the messages were. -->
        <div
          v-if="item.kind === 'gap'"
          class="msg-gap"
          role="note"
          :title="t('chat.omitted', { count: item.count })"
          :aria-label="t('chat.omitted', { count: item.count })"
        >
          <span class="msg-gap-dots" aria-hidden="true">(...)</span>
        </div>
        <ChatMessageItem
          v-else
          :message="item.message"
          :streaming="workingHere && item.index === messages.length - 1"
          :cache-break="cacheBreaks[item.index] ?? null"
        />
      </template>

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

/* --- elided block --------------------------------------------------------- */
/* Where the transcript forgets: a quiet "(...)" centred in the column, the
   width of the transcript, so it reads as a gap rather than as a card. */
.msg-gap {
  display: flex;
  justify-content: center;
  padding: 0.15rem 0;
  user-select: none;
}

.msg-gap-dots {
  color: var(--sofia-text-faint);
  font-size: 0.8125rem;
  letter-spacing: 0.18em;
  line-height: 1;
}
</style>
