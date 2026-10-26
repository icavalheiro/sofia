<script setup lang="ts">
import { computed, nextTick, reactive, ref, watch } from 'vue'

import logoUrl from '@/assets/logo.png'
import MarkdownText from '@/components/MarkdownText.vue'
import SofiaIcon from '@/components/SofiaIcon.vue'
import { useHarness } from '@/composables/useHarness'
import { useQuestionDrafts } from '@/composables/useQuestionDrafts'
import { useI18n } from '@/i18n'
import { api, serverUrl } from '@/services/sofia'
import { cacheDiagnosisTemplate } from '@/services/cacheHealth'
import type {
  Attachment,
  AttachmentKind,
  CacheBreakInfo,
  KnowledgeEntryDto,
  Message,
  ToolCall,
} from '@/types'

const props = defineProps<{
  message: Message
  streaming?: boolean
  pending?: 'steer' | 'queue'
  /** Set when this step's prompt was almost fully re-billed (see `services/cacheHealth`). */
  cacheBreak?: CacheBreakInfo | null
}>()
const { t } = useI18n()
const { resolvePermissionRequest, answerFormQuestion, confirmFormQuestions, revertMessage, branchMessage, retryMessage, openConversation } =
  useHarness()

// Reasoning is "live" while this message is still streaming and the model has
// not yet moved on to its answer or a tool call.
const reasoningActive = computed(
  () => !!props.streaming && !props.message.text && !props.message.toolCalls?.length,
)

// Finished reasoning starts collapsed; live reasoning starts open and folds
// itself once the agent stops reasoning. The user can toggle at any time.
const reasoningCollapsed = ref(!reasoningActive.value)

watch(reasoningActive, (active, wasActive) => {
  if (wasActive && !active) reasoningCollapsed.value = true
})

function toggleReasoning(): void {
  reasoningCollapsed.value = !reasoningCollapsed.value
}

// Tool cards are expanded by default; only the ids the user folded are tracked.
const collapsedTools = reactive(new Set<string>())

// Tools that render their own card in the message (the question prompt) fold
// their call away by default, so the card — not the raw arguments — reads first.
const SELF_CARD_TOOLS = new Set(['question'])

function toggleTool(id: string): void {
  if (!collapsedTools.delete(id)) collapsedTools.add(id)
}

// A tool call's raw input and output stay folded until the user opens them.
const openInputs = reactive(new Set<string>())
const openOutputs = reactive(new Set<string>())

function toggleInput(id: string): void {
  if (!openInputs.delete(id)) openInputs.add(id)
}

function toggleOutput(id: string): void {
  if (!openOutputs.delete(id)) openOutputs.add(id)
}

/** Effective open state: `collapsedTools` holds user toggles, which flip the default. */
function toolOpen(tc: ToolCall): boolean {
  const flipped = collapsedTools.has(tc.id)
  return SELF_CARD_TOOLS.has(tc.name) ? flipped : !flipped
}

/** Preview source: the local object URL, else the server attachment URL. */
function previewSrc(a: Attachment): string {
  return a.previewUrl ?? (a.url ? serverUrl(a.url) : '')
}

// Image attachments with a preview render as a thumbnail gallery; everything
// else stays a compact file chip.
const imageAtts = computed(() =>
  (props.message.attachments ?? []).filter((a) => a.kind === 'image' && previewSrc(a)),
)
const otherAtts = computed(() =>
  (props.message.attachments ?? []).filter((a) => !(a.kind === 'image' && previewSrc(a))),
)

const ICON_FOR_KIND: Record<AttachmentKind, string> = {
  image: 'image',
  video: 'video',
  audio: 'music',
  file: 'file',
}

function toolIcon(name: string): string {
  if (name.startsWith('read') || name.startsWith('write') || name.includes('file')) return 'file'
  if (name.includes('command') || name.includes('run') || name.includes('shell')) return 'terminal'
  if (name.includes('search') || name.includes('fetch') || name === 'browser') return 'globe'
  if (name.includes('edit')) return 'code'
  return 'box'
}

// --- permission card -------------------------------------------------------
function resolvePermission(choice: string): void {
  void resolvePermissionRequest(props.message, choice)
}

function permAllowed(choice: string | undefined): boolean {
  if (!choice) return false
  return !choice.toLowerCase().startsWith('deny')
}

// --- single question card ---------------------------------------------------
function answerQuestion(option: string): void {
  void answerFormQuestion(props.message, option)
}

// Free-text answer: an extra option at the end of every question.
const singleFree = ref('')
function answerFree(): void {
  const text = singleFree.value.trim()
  if (!text) return
  answerQuestion(text)
  singleFree.value = ''
}
const freeText = reactive<Record<number, string>>({})
function submitFree(index: number): void {
  const text = (freeText[index] ?? '').trim()
  const q = questionSet.value?.questions[index]
  if (!text || !q) return
  freeText[index] = ''
  if (q.multi) {
    const current = picks.value[index] ?? []
    if (!current.includes(text)) picks.value[index] = [...current, text]
    return
  }
  picks.value[index] = [text]
  goTo(index + 1)
}

// --- multi-question form ----------------------------------------------------
// Pages walk each question, then a review page; nothing is sent until the user
// confirms there. Picks live in a draft store (not on the message) because
// server updates replace the message object.
const { draftFor, clearDraft } = useQuestionDrafts()
const slide = ref(0)
const slideDir = ref<'fwd' | 'back'>('fwd')

const questionSet = computed(() => props.message.questionSet)
const questionCount = computed(() => questionSet.value?.questions.length ?? 0)
const totalSlides = computed(() => questionCount.value + 1) // +1 review page
const onReview = computed(() => slide.value >= questionCount.value)
const picks = computed(() => (questionSet.value ? draftFor(questionSet.value.formId) : []))
const allAnswered = computed(
  () => questionCount.value > 0 && Array.from({ length: questionCount.value }, (_, i) => picks.value[i]?.length).every(Boolean),
)

function goTo(index: number): void {
  const next = Math.max(0, Math.min(index, totalSlides.value - 1))
  if (next === slide.value) return
  slideDir.value = next > slide.value ? 'fwd' : 'back'
  slide.value = next
}

function isPicked(index: number, option: string): boolean {
  return !!picks.value[index]?.includes(option)
}

function pickOption(index: number, option: string): void {
  const q = questionSet.value?.questions[index]
  if (!q) return
  const current = picks.value[index] ?? []
  if (q.multi) {
    picks.value[index] = current.includes(option) ? current.filter((o) => o !== option) : [...current, option]
    return
  }
  picks.value[index] = [option]
  goTo(index + 1)
}

function confirmQuestions(): void {
  const qset = questionSet.value
  if (!qset || !allAnswered.value) return
  const formId = qset.formId
  void confirmFormQuestions(props.message, picks.value.map((p) => [...(p ?? [])])).then(() => {
    if (props.message.questionSet?.confirmed) clearDraft(formId)
  })
}

// --- per-message revert / retry ----------------------------------------------
// Revert: the server drops this message and everything after it, and restores
// the workspace files. Two-step confirm so a stray click can't rewrite the tree.
// Failed messages offer Retry instead; it rewinds the session too, so it asks
// for confirmation in a modal first.
const failed = computed(() => !!props.message.error)
// A transient provider failure Sofia is repeating: the card says which attempt is running and why, until one succeeds.
const providerRetryText = computed(() => {
  const notice = props.message.retry
  if ( !notice ) return ''
  const base = t( 'chat.providerRetrying', { attempt: notice.attempt, attempts: notice.attempts } )
  return notice.error ? `${base} — ${notice.error}` : base
})
const revertArmed = ref(false)
const reverting = ref(false)
const retrying = ref(false)
const retryConfirmOpen = ref(false)
const retryConfirmBtn = ref<HTMLButtonElement | null>(null)
const revertError = ref<string | null>(null)
const branching = ref(false)

async function branchOut(): Promise<void> {
  if (branching.value || props.message.local) return
  branching.value = true
  revertError.value = await branchMessage(props.message.id)
  branching.value = false
}

async function confirmRevert(): Promise<void> {
  if (reverting.value || props.message.local) return
  if (!revertArmed.value) {
    revertArmed.value = true
    window.setTimeout(() => {
      if (!reverting.value) revertArmed.value = false
    }, 2600)
    return
  }
  reverting.value = true
  revertError.value = await revertMessage(props.message.id)
  reverting.value = false
  revertArmed.value = false
}

function askRetry(): void {
  if (retrying.value) return
  retryConfirmOpen.value = true
  void nextTick(() => retryConfirmBtn.value?.focus())
}

async function retry(): Promise<void> {
  retryConfirmOpen.value = false
  if (retrying.value) return
  retrying.value = true
  revertError.value = await retryMessage(props.message.id)
  retrying.value = false
}

// --- knowledge card ---------------------------------------------------------
// Notes the harness learned from the conversation. The user can read them and
// delete any; the server then strikes the entry through a `message.updated`.
const knowledgeOpen = ref(false)
const knowledgeError = ref<string | null>(null)
const deletingNote = ref<string | null>(null)
const liveNotes = computed(() => (props.message.knowledge ?? []).filter((k) => !k.deleted).length)

const compactionOpen = ref(false)
const compactionBody = computed(() => {
  const c = props.message.compaction
  if (!c) return ''
  return c.status === 'failed' ? c.error ?? '' : c.result ?? ''
})
const canExpandCompaction = computed(() => compactionBody.value.length > 0)

const compactionText = computed(() => {
  const c = props.message.compaction
  if (!c) return ''
  const before = c.before.toLocaleString()
  switch (c.status) {
    case 'running': return t(c.auto ? 'chat.compact.runningAuto' : 'chat.compact.running', { before })
    case 'done': return t('chat.compact.done', { before, after: (c.after ?? 0).toLocaleString() })
    case 'failed': return t('chat.compact.failed', { before, error: c.error ?? '' })
    default: return t('chat.compact.skipped', { before })
  }
})

async function deleteNote(note: KnowledgeEntryDto): Promise<void> {
  if (deletingNote.value) return
  deletingNote.value = note.id
  knowledgeError.value = null
  try {
    await api('DELETE', `/api/knowledge/${encodeURIComponent(note.id)}`)
    note.deleted = true
  } catch {
    knowledgeError.value = t('knowledge.error')
  } finally {
    deletingNote.value = null
  }
}

// --- low prompt cache warning ----------------------------------------------
// The suspects come from `services/cacheHealth`; the copy button hands the user a
// ready-made prompt asking the model why this step re-billed its own prefix.
const diagnosisCopied = ref(false)
let diagnosisCopiedTimer: number | undefined

function diagnosisText(info: CacheBreakInfo): string {
  return cacheDiagnosisTemplate()
    .replace('{pct}', String(info.pct))
    .replace('{cached}', info.cached.toLocaleString())
    .replace('{input}', info.input.toLocaleString())
    .replace('{reasons}', info.reasons.map((key) => `- ${t(key)}`).join('\n'))
}

async function copyDiagnosis(info: CacheBreakInfo): Promise<void> {
  try {
    await navigator.clipboard.writeText(diagnosisText(info))
    diagnosisCopied.value = true
    if (diagnosisCopiedTimer !== undefined) window.clearTimeout(diagnosisCopiedTimer)
    diagnosisCopiedTimer = window.setTimeout(() => {
      diagnosisCopied.value = false
    }, 2000)
  } catch {
    diagnosisCopied.value = false
  }
}

// --- fullscreen image lightbox ----------------------------------------------
const lightbox = reactive<{ open: boolean; src: string; name: string }>({
  open: false,
  src: '',
  name: '',
})

function openLightbox(a: Attachment): void {
  const src = previewSrc(a)
  if (!src) return
  lightbox.src = src
  lightbox.name = a.name
  lightbox.open = true
}

function closeLightbox(): void {
  lightbox.open = false
}
</script>

<template>
  <div class="msg" :class="[`msg--${message.role}`, { 'msg--pending': pending }]">
    <!-- Only the assistant gets an avatar; user messages align without one. -->
    <div v-if="message.role === 'assistant' && !message.compaction" class="msg-avatar msg-avatar--ai">
      <img :src="logoUrl" alt="" />
    </div>

    <div class="msg-body">
      <!-- Compaction notice: a system line, not a chat bubble. -->
      <div v-if="message.compaction" class="compact-card" :class="`compact-notice--${message.compaction.status}`">
        <component
          :is="canExpandCompaction ? 'button' : 'div'"
          class="compact-notice"
          :class="`compact-notice--${message.compaction.status}`"
          role="status"
          :type="canExpandCompaction ? 'button' : undefined"
          :aria-expanded="canExpandCompaction ? compactionOpen : undefined"
          @click="canExpandCompaction && (compactionOpen = !compactionOpen)"
        >
          <SofiaIcon :name="message.compaction.status === 'failed' ? 'alert' : 'book'" :size="'0.85rem'" />
          <span class="compact-text">{{ compactionText }}</span>
          <SofiaIcon
            v-if="canExpandCompaction"
            :name="compactionOpen ? 'chevronUp' : 'chevronDown'"
            :size="'0.75rem'"
          />
        </component>
        <MarkdownText v-if="compactionOpen && canExpandCompaction" class="summary-text compact-result" :source="compactionBody" />
      </div>
      <template v-else>
      <div class="msg-meta">
        <span class="msg-author">{{ message.role === 'assistant' ? 'Sofia' : 'You' }}</span>
        <span v-if="message.model" class="msg-model">{{ message.model }}</span>
        <!-- Waiting for the model: steer lands after the running tool call, queued after the reply. -->
        <span v-if="pending" class="msg-time msg-pending-tag">
          <SofiaIcon name="clock" :size="'0.7rem'" />
          {{ pending === 'steer' ? t('chat.pendingSteer') : t('chat.pendingQueue') }}
        </span>
        <span v-else class="msg-time">{{ message.time }}</span>
      </div>

      <!-- Provider hiccup being retried: the step restarts, nothing has failed yet. -->
      <div v-if="message.retry" class="msg-retry" role="status" aria-live="polite">
        <SofiaIcon name="retry" :size="'0.95rem'" />
        <div class="msg-retry-text">{{ providerRetryText }}</div>
      </div>

      <!-- Connection / runtime error banner. -->
      <div v-if="message.error" class="msg-error">
        <SofiaIcon name="alert" :size="'0.95rem'" />
        <div class="msg-error-text">{{ message.error }}</div>
      </div>
      <div v-if="revertError" class="msg-error">
        <SofiaIcon name="alert" :size="'0.95rem'" />
        <div class="msg-error-text">{{ revertError }}</div>
      </div>

      <div v-if="message.reasoning" class="reasoning" :class="{ 'reasoning--collapsed': reasoningCollapsed }">
        <button
          class="reasoning-toggle"
          type="button"
          :aria-expanded="!reasoningCollapsed"
          @click="toggleReasoning"
        >
          <SofiaIcon name="zap" :size="'0.75rem'" />
          <span class="reasoning-label">{{ t('chat.reasoning') }} · {{ message.reasoningMode }}</span>
          <SofiaIcon class="reasoning-chevron" :name="reasoningCollapsed ? 'chevronDown' : 'chevronUp'" :size="'0.75rem'" />
        </button>
        <div class="collapsible" :class="{ 'collapsible--closed': reasoningCollapsed }">
          <div class="collapsible-inner">
            <p class="reasoning-text">{{ message.reasoning }}</p>
          </div>
        </div>
      </div>

      <!-- Message text comes before the tool cards: the model speaks, then acts. -->
      <template v-if="!message.knowledge?.length">
        <MarkdownText
          v-if="message.text && message.role === 'assistant'"
          class="msg-text msg-text--md"
          :source="message.text"
        />
        <p v-else-if="message.text" class="msg-text">{{ message.text }}</p>
      </template>

      <!-- Tool invocations (success + failure); each card folds to its header. -->
      <div v-if="message.toolCalls?.length" class="tool-list">
        <div v-for="tc in message.toolCalls" :key="tc.id" class="tool" :class="`tool--${tc.status}`">
          <div
            class="tool-head"
            role="button"
            tabindex="0"
            :aria-expanded="toolOpen(tc)"
            @click="toggleTool(tc.id)"
            @keydown.enter.prevent="toggleTool(tc.id)"
            @keydown.space.prevent="toggleTool(tc.id)"
          >
            <SofiaIcon class="tool-icon" :name="toolIcon(tc.name)" :size="'0.9rem'" />
            <span class="tool-name sofia-mono">{{ tc.name }}</span>
            <span class="tool-status" :class="`tool-status--${tc.status}`">
              <SofiaIcon
                :name="tc.status === 'success' ? 'check' : tc.status === 'running' ? 'clock' : 'x'"
                :size="'0.7rem'"
              />
              {{ tc.status === 'success' ? t('tool.success') : tc.status === 'running' ? t('tool.running') : t('tool.fail') }}
            </span>
            <span class="tool-dur">{{ tc.duration }}</span>
            <SofiaIcon class="tool-chevron" :name="toolOpen(tc) ? 'chevronUp' : 'chevronDown'" :size="'0.75rem'" />
          </div>

          <div class="collapsible" :class="{ 'collapsible--closed': !toolOpen(tc) }">
            <div class="collapsible-inner">
              <div class="tool-summary">{{ tc.summary }}</div>

              <!-- Subagent run spawned by this call: open its own conversation. -->
              <button
                v-if="tc.childSessionId"
                class="tool-child"
                type="button"
                @click="openConversation(tc.childSessionId)"
              >
                <SofiaIcon name="chat" :size="'0.75rem'" />
                {{ t('tool.openSubagent') }}
              </button>

              <div v-if="tc.input" class="tool-block tool-block--input" :class="{ 'tool-block--folded': !openInputs.has(tc.id) }">
                <button
                  class="tool-block-label tool-block-toggle"
                  type="button"
                  :aria-expanded="openInputs.has(tc.id)"
                  @click="toggleInput(tc.id)"
                >
                  <span>{{ t('tool.input') }}</span>
                  <SofiaIcon :name="openInputs.has(tc.id) ? 'chevronUp' : 'chevronDown'" :size="'0.7rem'" />
                </button>
                <pre v-if="openInputs.has(tc.id)" class="sofia-mono">{{ tc.input }}</pre>
              </div>
              <div v-if="tc.output" class="tool-block tool-block--ok" :class="{ 'tool-block--folded': !openOutputs.has(tc.id) }">
                <button
                  class="tool-block-label tool-block-toggle"
                  type="button"
                  :aria-expanded="openOutputs.has(tc.id)"
                  @click="toggleOutput(tc.id)"
                >
                  <span>{{ t('tool.output') }}</span>
                  <SofiaIcon :name="openOutputs.has(tc.id) ? 'chevronUp' : 'chevronDown'" :size="'0.7rem'" />
                </button>
                <pre v-if="openOutputs.has(tc.id)" class="sofia-mono">{{ tc.output }}</pre>
              </div>
              <div v-if="tc.images?.length" class="img-gallery">
                <figure v-for="a in tc.images" :key="a.id" class="img-item">
                  <video v-if="a.kind === 'video'" class="img-thumb img-thumb--media" :src="previewSrc(a)" controls preload="metadata" />
                  <audio v-else-if="a.kind === 'audio'" class="img-audio" :src="previewSrc(a)" controls preload="none" />
                  <img
                    v-else
                    class="img-thumb"
                    :src="previewSrc(a)"
                    :alt="a.name"
                    role="button"
                    tabindex="0"
                    @click="openLightbox(a)"
                    @keydown.enter="openLightbox(a)"
                  />
                  <figcaption class="img-cap sofia-truncate">{{ a.name }} · {{ a.size }}</figcaption>
                </figure>
              </div>
              <div v-if="tc.error" class="tool-block tool-block--err">
                <div class="tool-block-label">{{ t('tool.error') }}</div>
                <pre class="sofia-mono">{{ tc.error }}</pre>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- Image attachments render as thumbnails (click to open fullscreen). -->
      <div v-if="imageAtts.length" class="img-gallery">
        <figure v-for="a in imageAtts" :key="a.id" class="img-item">
          <img
            class="img-thumb"
            :src="previewSrc(a)"
            :alt="a.name"
            role="button"
            tabindex="0"
            @click="openLightbox(a)"
            @keydown.enter="openLightbox(a)"
          />
          <figcaption class="img-cap sofia-truncate">{{ a.name }} · {{ a.size }}</figcaption>
        </figure>
      </div>

      <!-- Non-image attachments stay as compact chips. -->
      <div v-if="otherAtts.length" class="attachments">
        <component
          :is="a.url ? 'a' : 'div'"
          v-for="a in otherAtts"
          :key="a.id"
          class="attach-chip"
          :href="a.url ? serverUrl(a.url) : undefined"
          :download="a.url ? a.name : undefined"
        >
          <SofiaIcon :name="ICON_FOR_KIND[a.kind]" :size="'0.9rem'" />
          <span class="attach-name sofia-truncate">{{ a.name }}</span>
          <span class="attach-size">{{ a.size }}</span>
        </component>
      </div>

      <!-- Compaction summary: the earlier conversation, condensed. -->
      <div v-if="message.summary" class="summary-card">
        <div class="summary-head">
          <SofiaIcon name="book" :size="'0.85rem'" />
          <span>{{ t('chat.summary') }}</span>
        </div>
        <MarkdownText class="summary-text" :source="message.text" />
      </div>

      <!-- Knowledge the harness distilled from the conversation (viewable, deletable). -->
      <div v-if="message.knowledge?.length" class="knowledge-card">
        <div class="knowledge-head">
          <SofiaIcon name="sparkles" :size="'0.85rem'" />
          <span>{{ t('knowledge.title') }}</span>
          <span class="knowledge-count">{{ liveNotes }}</span>
          <button class="knowledge-toggle" type="button" :aria-expanded="knowledgeOpen" @click="knowledgeOpen = !knowledgeOpen">
            {{ knowledgeOpen ? t('knowledge.hide') : t('knowledge.view') }}
          </button>
        </div>
        <ul v-if="knowledgeOpen" class="knowledge-list">
          <li
            v-for="note in message.knowledge"
            :key="note.id"
            class="knowledge-note"
            :class="{ 'knowledge-note--deleted': note.deleted }"
          >
            <span class="knowledge-scope">{{ t(`knowledge.scope.${note.scope}`) }}</span>
            <span class="knowledge-body">
              <span class="knowledge-text">{{ note.text }}</span>
              <span v-if="note.why && !note.deleted" class="knowledge-why">{{ note.why }}</span>
            </span>
            <span v-if="note.deleted" class="knowledge-gone">{{ t('knowledge.deleted') }}</span>
            <button
              v-else
              class="knowledge-delete"
              type="button"
              :title="t('knowledge.delete')"
              :aria-label="t('knowledge.delete')"
              :disabled="deletingNote === note.id"
              @click="deleteNote(note)"
            >
              <SofiaIcon name="trash" :size="'0.8rem'" />
            </button>
          </li>
        </ul>
        <p v-if="knowledgeError" class="knowledge-error">{{ knowledgeError }}</p>
      </div>

      <!-- Structured report produced by a deep-search run (with its file). -->
      <div v-if="message.report" class="report">
        <div class="report-head">
          <SofiaIcon class="report-icon" name="book" :size="'1rem'" />
          <span class="report-title">{{ message.report.title }}</span>
          <span class="report-badge">{{ t('report.badge') }}</span>
        </div>

        <p class="report-summary">{{ message.report.summary }}</p>

        <div class="report-sections">
          <section v-for="s in message.report.sections" :key="s.heading" class="report-section">
            <h4 class="report-heading">{{ s.heading }}</h4>
            <p class="report-body">{{ s.body }}</p>
          </section>
        </div>

        <div v-if="message.report.file" class="report-file">
          <SofiaIcon name="file" :size="'0.95rem'" />
          <span class="report-file-name sofia-truncate">{{ message.report.file.name }}</span>
          <span class="report-file-size">{{ message.report.file.size }}</span>
          <button class="report-open" type="button">
            <SofiaIcon name="upload" :size="'0.85rem'" />
            {{ t('report.open') }}
          </button>
        </div>
      </div>

      <!-- Permission request the model is waiting on. -->
      <div v-if="message.permission" class="perm-card" :class="message.permission.resolved ? 'perm-card--resolved' : ''">
        <div class="perm-head" :class="`perm-head--${message.permission.resolved ? (permAllowed(message.permission.resolved) ? 'allowed' : 'denied') : 'pending'}`">
          <SofiaIcon name="shield" :size="'0.95rem'" />
          <span class="perm-title">
            {{ message.permission.resolved ? (permAllowed(message.permission.resolved) ? t('perm.approved') : t('perm.denied')) : t('perm.title') }}
          </span>
          <span class="perm-tool sofia-mono">{{ message.permission.tool }}</span>
        </div>
        <p class="perm-desc">{{ message.permission.description }}</p>

        <div v-if="!message.permission.resolved" class="perm-choices">
          <button
            v-for="(c, i) in message.permission.choices"
            :key="c"
            class="perm-btn"
            :class="i === message.permission.choices.length - 1 ? 'perm-btn--deny' : 'perm-btn--allow'"
            type="button"
            @click="resolvePermission(c)"
          >{{ c }}</button>
        </div>
        <div v-else class="perm-resolved" :class="permAllowed(message.permission.resolved) ? 'perm-resolved--ok' : 'perm-resolved--err'">
          <SofiaIcon :name="permAllowed(message.permission.resolved) ? 'check' : 'x'" :size="'0.9rem'" />
          <span>{{ message.permission.resolved }}</span>
        </div>
      </div>

      <!-- Clarifying question with quick replies. -->
      <div v-if="message.question && !message.question.resolved" class="q-card">
        <div class="q-head">
          <SofiaIcon name="help" :size="'0.95rem'" />
          <span class="q-title">{{ message.question.prompt }}</span>
        </div>
        <div class="q-options">
          <button
            v-for="o in message.question.options"
            :key="o"
            class="q-opt"
            :class="{ 'q-opt--chosen': message.question?.resolved === o }"
            type="button"
            :disabled="!!message.question?.resolved"
            @click="answerQuestion(o)"
          >{{ o }}</button>
          <input v-model="singleFree" class="q-free" type="text" :placeholder="t('q.freePlaceholder')" :aria-label="t('q.freePlaceholder')" @keydown.enter.prevent="answerFree()">
        </div>
      </div>

      <!-- Answered single question: same compact card as the multi-question one. -->
      <div v-if="message.question?.resolved" class="q-confirmed">
        <ul class="q-summary q-summary--done">
          <li class="q-summary-row">
            <span class="q-summary-q">{{ message.question.prompt }}</span>
            <span class="q-summary-answers">
              <span class="q-chip">{{ message.question.resolved }}</span>
            </span>
          </li>
        </ul>
      </div>

      <!-- Multi-question carousel with a final confirmation slide. -->
      <div v-if="message.questionSet && !message.questionSet.confirmed" class="q-carousel">
        <div class="q-carousel-head">
          <SofiaIcon name="help" :size="'0.95rem'" />
          <span class="q-carousel-title">{{ t('q.title') }}</span>
          <span class="q-carousel-count">
            {{ onReview ? t('q.review') : `${slide + 1} / ${questionCount}` }}
          </span>
        </div>

        <div class="q-stage">
          <Transition :name="slideDir === 'fwd' ? 'q-fwd' : 'q-back'" mode="out-in">
            <!-- Question page. -->
            <div v-if="!onReview" :key="slide" class="q-slide">
              <div class="q-slide-prompt">{{ message.questionSet.questions[slide].prompt }}</div>
              <div class="q-options">
                <button
                  v-for="o in message.questionSet.questions[slide].options"
                  :key="o"
                  class="q-opt"
                  :class="{ 'q-opt--chosen': isPicked(slide, o) }"
                  type="button"
                  @click="pickOption(slide, o)"
                >{{ o }}</button>
                <input v-model="freeText[slide]" class="q-free" type="text" :placeholder="t('q.freePlaceholder')" :aria-label="t('q.freePlaceholder')" @keydown.enter.prevent="submitFree(slide)">
              </div>
              <div v-if="message.questionSet.questions[slide].multi" class="q-slide-foot">
                <span class="q-hint">{{ t('q.multiHint') }}</span>
                <button class="q-step-btn" type="button" :disabled="!picks[slide]?.length" @click="goTo(slide + 1)">
                  {{ t('q.next') }}
                  <SofiaIcon name="chevronRight" :size="'0.8rem'" />
                </button>
              </div>
            </div>

            <!-- Review page: nothing is sent until confirmed here. -->
            <div v-else key="review" class="q-slide q-slide--confirm">
              <div class="q-slide-prompt">{{ t('q.reviewPrompt') }}</div>
              <ul class="q-summary">
                <li v-for="(q, i) in message.questionSet.questions" :key="i" class="q-summary-row">
                  <span class="q-summary-q">{{ q.prompt }}</span>
                  <span class="q-summary-answers">
                    <span v-for="a in picks[i] ?? []" :key="a" class="q-chip">{{ a }}</span>
                    <span v-if="!picks[i]?.length" class="q-summary-empty">—</span>
                  </span>
                  <button class="q-edit" type="button" :title="t('q.edit')" :aria-label="t('q.edit')" @click="goTo(i)">
                    <SofiaIcon name="code" :size="'0.8rem'" />
                  </button>
                </li>
              </ul>
              <button class="q-confirm-btn" type="button" :disabled="!allAnswered" @click="confirmQuestions">
                <SofiaIcon name="send" :size="'0.85rem'" />
                {{ t('q.confirm') }}
              </button>
            </div>
          </Transition>
        </div>

        <!-- Carousel controls: dots + prev/next. -->
        <div class="q-carousel-nav">
          <button class="q-nav-arrow" type="button" :disabled="slide === 0" :aria-label="t('q.back')" @click="goTo(slide - 1)">
            <SofiaIcon name="chevronLeft" :size="'0.9rem'" />
          </button>
          <div class="q-dots">
            <span
              v-for="n in totalSlides"
              :key="n"
              class="q-dot"
              :class="{ 'q-dot--active': n - 1 === slide }"
            />
          </div>
          <button
            class="q-nav-arrow"
            type="button"
            :disabled="onReview || !picks[slide]?.length"
            :aria-label="t('q.next')"
            @click="goTo(slide + 1)"
          >
            <SofiaIcon name="chevronRight" :size="'0.9rem'" />
          </button>
        </div>
      </div>

      <!-- Multi-question confirmed state. -->
      <div v-if="message.questionSet?.confirmed" class="q-confirmed">
        <ul class="q-summary q-summary--done">
          <li v-for="(q, i) in message.questionSet.questions" :key="i" class="q-summary-row">
            <span class="q-summary-q">{{ q.prompt }}</span>
            <span class="q-summary-answers">
              <span class="q-chip">{{ message.questionSet.answers?.[i] || '—' }}</span>
            </span>
          </li>
        </ul>
      </div>

      <!-- Prompt cache re-billed almost the whole context: warn under the reply and
           offer a ready-made prompt that asks the model why. -->
      <div v-if="cacheBreak && !pending" class="cache-warn" role="note">
        <SofiaIcon name="alert" :size="'0.9rem'" />
        <div class="cache-warn-body">
          <p class="cache-warn-title">{{ t('cache.lowTitle', { pct: cacheBreak.pct }) }}</p>
          <ul class="cache-warn-reasons">
            <li v-for="key in cacheBreak.reasons" :key="key">{{ t(key) }}</li>
          </ul>
          <button class="cache-warn-copy" type="button" @click="copyDiagnosis(cacheBreak)">
            <SofiaIcon :name="diagnosisCopied ? 'check' : 'copy'" :size="'0.75rem'" />
            {{ diagnosisCopied ? t('pair.copied') : t('cache.copyPrompt') }}
          </button>
        </div>
      </div>

      <!-- Message actions, bottom-right: Branch out, plus Retry for failed messages or Revert otherwise. -->
      <div v-if="!pending && (failed || !message.local)" class="msg-actions">
        <button
          v-if="!message.local"
          class="msg-revert msg-revert--branch"
          type="button"
          title="Branch out: copies the conversation up to this message into a new one"
          :disabled="branching"
          @click="branchOut"
        >
          <SofiaIcon name="branch" :size="'0.8rem'" />
          <span>{{ branching ? 'Branching…' : 'Branch out' }}</span>
        </button>
        <button
          v-if="failed"
          class="msg-revert msg-revert--retry"
          type="button"
          title="Retry: rewinds to the prompt that triggered this failure and sends it again"
          :disabled="retrying"
          @click="askRetry"
        >
          <SofiaIcon name="retry" :size="'0.8rem'" />
          <span>{{ retrying ? 'Retrying…' : 'Retry' }}</span>
        </button>
        <button
          v-else
          class="msg-revert"
          type="button"
          title="Revert the session here: removes this message and everything after, restores files"
          :class="{ 'msg-revert--armed': revertArmed }"
          @click="confirmRevert"
        >
          <SofiaIcon name="undo" :size="'0.8rem'" />
          <span>{{ reverting ? 'Reverting…' : revertArmed ? 'Confirm' : 'Revert' }}</span>
        </button>
      </div>
      </template>
    </div>

    <Teleport to="body">
      <div
        v-if="retryConfirmOpen"
        class="retry-overlay"
        @click.self="retryConfirmOpen = false"
        @keydown.esc="retryConfirmOpen = false"
      >
        <div class="retry-modal" role="alertdialog" aria-modal="true" aria-labelledby="retry-title">
          <h2 id="retry-title" class="retry-title">Retry this message?</h2>
          <p class="retry-text">
            This rewinds the session to the prompt that triggered the failure, discarding this message and everything
            after it, then sends the prompt again.
          </p>
          <div class="retry-actions">
            <button class="retry-btn" type="button" @click="retryConfirmOpen = false">Cancel</button>
            <button ref="retryConfirmBtn" class="retry-btn retry-btn--primary" type="button" @click="retry">Retry</button>
          </div>
        </div>
      </div>
    </Teleport>

    <!-- Fullscreen image lightbox with close + download. -->
    <Teleport to="body">
      <div v-if="lightbox.open" class="lightbox" role="dialog" aria-modal="true" @click="closeLightbox">
        <button class="lightbox-close" type="button" :aria-label="t('img.close')" :title="t('img.close')" @click.stop="closeLightbox">
          <SofiaIcon name="x" :size="'1.1rem'" />
        </button>
        <img class="lightbox-img" :src="lightbox.src" :alt="lightbox.name" @click.stop />
        <a class="lightbox-download" :href="lightbox.src" :download="lightbox.name" :title="t('img.download')" @click.stop>
          <SofiaIcon name="upload" :size="'0.95rem'" />
          {{ t('img.download') }}
        </a>
      </div>
    </Teleport>
  </div>
</template>

<style scoped>
.msg {
  display: flex;
  gap: 0.75rem;
  padding: 0.75rem 0;
}

.msg--user {
  flex-direction: row-reverse;
}

.msg-avatar {
  flex-shrink: 0;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  border-radius: 10px;
  overflow: hidden;
  border: 1px solid var(--sofia-border-strong);
}

.msg-avatar--ai {
  background: var(--sofia-primary-soft);
}

.msg-avatar--ai img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

/* Assistant body fills the chat column so tool cards span the full width;
   the prose itself is capped separately for readability. User stays a bubble. */
.msg-body {
  min-width: 0;
  flex: 1;
}

.msg--user .msg-body {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  flex: 0 1 auto;
  max-width: min(680px, 82%);
}

.msg-meta {
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
  margin-bottom: 0.3rem;
  font-size: 0.75rem;
}

.msg-author {
  font-weight: 600;
  color: var(--sofia-text);
}

.msg--user .msg-author {
  color: var(--sofia-primary);
}

.msg-model {
  color: var(--sofia-text-faint);
}

.msg-time {
  margin-left: auto;
  color: var(--sofia-text-faint);
}

/* Revert / retry sit bottom-right under the message, hidden until the row is
   hovered so the transcript stays quiet. Armed state (first click) is a confirm. */
.msg-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.35rem;
  margin-top: 0.3rem;
}

.msg-revert {
  display: inline-flex;
  align-items: center;
  gap: 0.2rem;
  padding: 0.05rem 0.35rem;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: var(--sofia-text-faint);
  font-size: 0.625rem;
  font-weight: 600;
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.15s ease, background 0.15s ease, color 0.15s ease;
}

.msg:hover .msg-revert,
.msg-revert:focus-visible,
.msg-revert--armed,
.msg-revert--retry {
  opacity: 1;
}

.msg-revert:hover,
.msg-revert--armed {
  border-color: var(--sofia-border-strong);
  background: var(--sofia-surface-2);
  color: var(--sofia-err);
}

.msg-revert--retry {
  color: var(--sofia-text-muted);
}

.msg-revert--branch:hover:not(:disabled) {
  color: var(--sofia-primary);
}

.msg-revert--retry:hover:not(:disabled) {
  border-color: var(--sofia-primary);
  background: var(--sofia-surface-2);
  color: var(--sofia-primary);
}

.retry-overlay {
  position: fixed;
  inset: 0;
  z-index: 2100;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  background: rgba(0, 0, 0, 0.45);
}

.retry-modal {
  width: min(400px, 100%);
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  padding: 1rem;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 14px;
  box-shadow: 0 24px 60px rgba(0, 0, 0, 0.5);
}

.retry-title {
  margin: 0;
  font-size: 0.9375rem;
  font-weight: 600;
  color: var(--sofia-primary);
}

.retry-text {
  margin: 0;
  font-size: 0.8125rem;
  line-height: 1.5;
  color: var(--sofia-text-muted);
}

.retry-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 0.3rem;
}

.retry-btn {
  padding: 0.3rem 0.8rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
  cursor: pointer;
}

.retry-btn:hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.retry-btn--primary {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.msg-revert:disabled {
  cursor: wait;
  opacity: 0.6;
}

.msg--user .msg-meta .msg-time {
  margin-left: 0;
}

/* A follow-up the server accepted but has not sent to the model yet. */
.msg--pending {
  opacity: 0.55;
}

.msg--pending .msg-text {
  border-style: dashed;
}

.msg-pending-tag {
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
}

.msg-text {
  margin: 0;
  font-size: 0.9375rem;
  line-height: 1.6;
  color: var(--sofia-text);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

/* Keep prose at a readable measure even though the body spans full width. */
.msg--assistant .msg-text {
  max-width: 74ch;
}

.msg-text--md {
  white-space: normal;
}

.msg--user .msg-text {
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border);
  border-radius: 14px;
  padding: 0.6rem 0.85rem;
}

/* --- reasoning (the one beige "detail") ---------------------------------- */
.reasoning {
  margin-bottom: 0.5rem;
  padding: 0.55rem 0.75rem;
  border-left: 2px solid var(--sofia-detail);
  background: var(--sofia-detail-soft);
  border-radius: 0 8px 8px 0;
}

.reasoning-toggle {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  width: 100%;
  padding: 0;
  border: 0;
  background: none;
  font: inherit;
  color: var(--sofia-detail);
  text-align: left;
  cursor: pointer;
}

.reasoning-label {
  font-size: 0.6875rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.reasoning-chevron {
  margin-left: auto;
}

.reasoning-text {
  margin: 0;
  padding-top: 0.25rem;
  font-size: 0.8125rem;
  line-height: 1.55;
  color: var(--sofia-text-muted);
  font-style: italic;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

/* Height-animated fold: grid rows go 1fr -> 0fr without a fixed max-height. */
.collapsible {
  display: grid;
  grid-template-rows: 1fr;
  transition: grid-template-rows 0.2s ease-in-out;
}

.collapsible--closed {
  grid-template-rows: 0fr;
}

.collapsible-inner {
  min-height: 0;
  overflow: hidden;
}

/* --- tool calls --------------------------------------------------------- */
.msg-text + .tool-list {
  margin-top: 0.6rem;
}

.tool-list {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-bottom: 0.6rem;
}

.tool {
  border: 1px solid var(--sofia-border);
  border-left-width: 2px;
  border-radius: 10px;
  background: var(--sofia-surface-2);
  padding: 0.5rem 0.65rem;
  overflow: hidden;
}

.tool--success {
  border-left-color: var(--sofia-ok);
}

.tool--error {
  border-left-color: var(--sofia-err);
}

.tool-head {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.75rem;
  cursor: pointer;
  user-select: none;
}

.tool-chevron {
  color: var(--sofia-text-faint);
  flex-shrink: 0;
}

.tool-icon {
  color: var(--sofia-text-muted);
  flex-shrink: 0;
}

.tool-name {
  color: var(--sofia-text);
  font-weight: 600;
}

.tool-status {
  display: inline-flex;
  align-items: center;
  gap: 0.2rem;
  font-size: 0.6875rem;
  font-weight: 600;
  padding: 0.05rem 0.4rem;
  border-radius: 999px;
  margin-left: auto;
}

.tool-status--success {
  color: var(--sofia-ok);
  background: var(--sofia-ok-soft);
}

.tool-status--error {
  color: var(--sofia-err);
  background: var(--sofia-err-soft);
}

.tool-dur {
  color: var(--sofia-text-faint);
  flex-shrink: 0;
}

.tool-summary {
  margin-top: 0.25rem;
  font-size: 0.8125rem;
  color: var(--sofia-text-muted);
}

.tool-status--running {
  color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
}

.tool--running {
  border-left-color: var(--sofia-primary);
}

.tool-child {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  margin-top: 0.4rem;
  padding: 0.2rem 0.55rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 999px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.7rem;
  font-weight: 600;
  cursor: pointer;
}

.tool-child:hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

/* --- compaction summary ---------------------------------------------------- */
.compact-card {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  max-width: 74ch;
  width: 100%;
  margin-inline: auto;
}

.compact-notice {
  width: 100%;
  text-align: left;
  font: inherit;
  font-size: 0.78rem;
  display: flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.35rem 0.7rem;
  border: 1px dashed var(--sofia-border-strong);
  border-radius: 8px;
  color: var(--sofia-text-muted);
  background: var(--sofia-surface-2);
}

button.compact-notice {
  cursor: pointer;
}

.compact-text {
  flex: 1;
  min-width: 0;
}

.compact-result {
  padding: 0.55rem 0.75rem;
  border: 1px dashed var(--sofia-border-strong);
  border-radius: 10px;
  background: var(--sofia-surface-2);
}

.compact-notice--failed {
  color: var(--sofia-danger, #d9534f);
  border-color: currentColor;
}

.compact-notice--running .compact-text {
  animation: compact-pulse 1.4s ease-in-out infinite;
}

@keyframes compact-pulse {
  50% { opacity: 0.55; }
}
.summary-card {
  max-width: 74ch;
  padding: 0.55rem 0.75rem;
  border: 1px dashed var(--sofia-border-strong);
  border-radius: 10px;
  background: var(--sofia-surface-2);
}

.summary-head {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  margin-bottom: 0.3rem;
  font-size: 0.6875rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.summary-text {
  margin: 0;
  font-size: 0.8125rem;
  line-height: 1.55;
  color: var(--sofia-text-muted);
  white-space: normal;
}

/* --- knowledge notes ---------------------------------------------------------- */
.knowledge-card {
  max-width: 74ch;
  padding: 0.45rem 0.75rem;
  border: 1px dashed var(--sofia-primary);
  border-radius: 10px;
  background: var(--sofia-primary-soft);
}

.knowledge-head {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  font-size: 0.6875rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--sofia-primary);
}

.knowledge-count {
  padding: 0 0.4rem;
  border-radius: 999px;
  background: var(--sofia-surface-3);
  color: var(--sofia-text-muted);
  letter-spacing: 0;
}

.knowledge-toggle {
  margin-left: auto;
  padding: 0.15rem 0.5rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.6875rem;
  letter-spacing: 0;
  text-transform: none;
  cursor: pointer;
}

.knowledge-toggle:hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.knowledge-list {
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
  margin: 0.45rem 0 0;
  padding: 0;
  list-style: none;
}

.knowledge-note {
  display: flex;
  align-items: flex-start;
  gap: 0.45rem;
  font-size: 0.8125rem;
  line-height: 1.5;
  color: var(--sofia-text);
}

.knowledge-note--deleted .knowledge-text {
  color: var(--sofia-text-faint);
  text-decoration: line-through;
}

.knowledge-scope {
  flex-shrink: 0;
  margin-top: 0.1rem;
  padding: 0.05rem 0.4rem;
  border-radius: 999px;
  background: var(--sofia-surface-3);
  color: var(--sofia-text-faint);
  font-size: 0.625rem;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.knowledge-text {
  min-width: 0;
  overflow-wrap: anywhere;
}

.knowledge-body {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

/* The utility argument the reviewer gave for keeping the note. */
.knowledge-why {
  margin-top: 0.1rem;
  font-size: 0.6875rem;
  line-height: 1.4;
  color: var(--sofia-text-faint);
}

.knowledge-gone {
  flex-shrink: 0;
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
}

.knowledge-delete {
  flex-shrink: 0;
  display: inline-flex;
  padding: 0.2rem;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--sofia-text-faint);
  cursor: pointer;
}

.knowledge-delete:not(:disabled):hover {
  color: var(--sofia-err);
}

.knowledge-delete:disabled {
  opacity: 0.5;
  cursor: default;
}

.knowledge-error {
  margin: 0.35rem 0 0;
  font-size: 0.75rem;
  color: var(--sofia-err);
}

.tool-block {
  margin-top: 0.4rem;
  border: 1px solid var(--sofia-border);
  border-radius: 8px;
  background: var(--sofia-bg);
  overflow: hidden;
}

.tool-block-label {
  padding: 0.2rem 0.5rem;
  font-size: 0.625rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
  border-bottom: 1px solid var(--sofia-border);
  background: var(--sofia-surface);
}

.tool-block pre {
  margin: 0;
  padding: 0.45rem 0.5rem;
  color: var(--sofia-text-muted);
  max-height: 200px;
  overflow: auto;
}

.tool-block-toggle {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  border-width: 0 0 1px;
  border-style: solid;
  border-color: var(--sofia-border);
  font-family: inherit;
  text-align: left;
  cursor: pointer;
}

.tool-block-toggle:hover {
  color: var(--sofia-text-muted);
}

.tool-block--folded .tool-block-label {
  border-bottom-width: 0;
}

.tool-block--ok .tool-block-label {
  color: var(--sofia-ok);
}

.tool-block--err .tool-block-label {
  color: var(--sofia-err);
}

.tool-block--err pre {
  color: var(--sofia-err);
}

/* --- error banner -------------------------------------------------------- */
.msg-error {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
  padding: 0.5rem 0.65rem;
  border: 1px solid var(--sofia-err);
  border-left-width: 3px;
  border-radius: 8px;
  background: var(--sofia-err-soft);
  color: var(--sofia-err);
}

.msg-error svg {
  flex-shrink: 0;
  margin-top: 0.1rem;
}

.msg-error-text {
  font-size: 0.8125rem;
  line-height: 1.5;
}

/* --- provider retry notice --------------------------------------------------- */
.msg-retry {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  max-width: 74ch;
  margin-bottom: 0.5rem;
  padding: 0.45rem 0.65rem;
  border: 1px dashed var(--sofia-warn);
  border-radius: 8px;
  background: var(--sofia-surface-2);
  color: var(--sofia-warn);
}

.msg-retry svg {
  flex-shrink: 0;
  margin-top: 0.1rem;
  animation: msg-retry-spin 1.8s linear infinite;
}

@keyframes msg-retry-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .msg-retry svg {
    animation: none;
  }
}

.msg-retry-text {
  font-size: 0.8125rem;
  line-height: 1.5;
}

/* --- low prompt cache warning --------------------------------------------- */
.cache-warn {
  display: flex;
  align-items: flex-start;
  gap: 0.5rem;
  max-width: 74ch;
  margin-top: 0.6rem;
  padding: 0.5rem 0.65rem;
  border: 1px solid var(--sofia-warn);
  border-left-width: 3px;
  border-radius: 8px;
  background: var(--sofia-warn-soft);
  color: var(--sofia-warn);
}

.cache-warn svg:first-child {
  flex-shrink: 0;
  margin-top: 0.15rem;
}

.cache-warn-body {
  min-width: 0;
}

.cache-warn-title {
  margin: 0;
  font-size: 0.8125rem;
  font-weight: 650;
  line-height: 1.45;
}

.cache-warn-reasons {
  margin: 0.3rem 0 0;
  padding-left: 1.1rem;
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
  font-size: 0.75rem;
  line-height: 1.5;
  color: var(--sofia-text-muted);
}

.cache-warn-copy {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  margin-top: 0.45rem;
  padding: 0.2rem 0.55rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 999px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.6875rem;
  font-weight: 600;
  cursor: pointer;
}

.cache-warn-copy:hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

/* --- image thumbnails ---------------------------------------------------- */
.img-gallery {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}

.img-item {
  margin: 0;
  max-width: 240px;
}

.img-thumb {
  display: block;
  width: 100%;
  max-height: 180px;
  object-fit: cover;
  border: 1px solid var(--sofia-border);
  border-radius: 10px;
  background: var(--sofia-surface-2);
  cursor: zoom-in;
  transition: border-color 0.12s ease, transform 0.12s ease;
}

.img-thumb:hover {
  border-color: var(--sofia-primary);
}

.img-thumb--media {
  cursor: default;
}

.img-audio {
  display: block;
  width: 240px;
  max-width: 100%;
}

.img-cap {
  margin-top: 0.25rem;
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
}

/* --- fullscreen lightbox -------------------------------------------------- */
.lightbox {
  position: fixed;
  inset: 0;
  z-index: 200;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.88);
  backdrop-filter: blur(4px);
  padding: 3rem;
}

.lightbox-img {
  max-width: 100%;
  max-height: 100%;
  object-fit: contain;
  border-radius: 8px;
  box-shadow: 0 20px 60px rgba(0, 0, 0, 0.6);
}

.lightbox-close {
  position: fixed;
  top: 1.25rem;
  right: 1.25rem;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 42px;
  height: 42px;
  border-radius: 50%;
  border: 1px solid rgba(255, 255, 255, 0.25);
  background: rgba(255, 255, 255, 0.1);
  color: #fff;
  cursor: pointer;
}

.lightbox-close:hover {
  background: rgba(255, 255, 255, 0.2);
}

.lightbox-download {
  position: fixed;
  bottom: 1.5rem;
  left: 50%;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  padding: 0.55rem 1rem;
  border-radius: 999px;
  border: 1px solid rgba(255, 255, 255, 0.25);
  background: rgba(255, 255, 255, 0.12);
  color: #fff;
  font-size: 0.8125rem;
  font-weight: 600;
  text-decoration: none;
}

.lightbox-download:hover {
  background: rgba(255, 255, 255, 0.22);
}

/* --- permission request -------------------------------------------------- */
.perm-card {
  margin-top: 0.5rem;
  border: 1px solid var(--sofia-warn);
  border-radius: 10px;
  background: var(--sofia-warn-soft);
  padding: 0.6rem 0.7rem;
}

.perm-card--resolved {
  border-color: var(--sofia-border);
  background: var(--sofia-surface-2);
}

.perm-head {
  display: flex;
  align-items: center;
  gap: 0.4rem;
}

.perm-head--pending {
  color: var(--sofia-warn);
}

.perm-head--allowed {
  color: var(--sofia-ok);
}

.perm-head--denied {
  color: var(--sofia-err);
}

.perm-title {
  font-size: 0.75rem;
  font-weight: 650;
  letter-spacing: 0.02em;
}

.perm-tool {
  margin-left: auto;
  color: var(--sofia-text-faint);
}

.perm-desc {
  margin: 0.35rem 0 0.55rem;
  font-size: 0.8125rem;
  line-height: 1.5;
  color: var(--sofia-text);
}

.perm-choices {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
}

.perm-btn {
  padding: 0.35rem 0.7rem;
  border-radius: 8px;
  font-size: 0.75rem;
  font-weight: 600;
  cursor: pointer;
}

.perm-btn--allow {
  border: 1px solid var(--sofia-ok);
  background: var(--sofia-ok-soft);
  color: var(--sofia-ok);
}

.perm-btn--allow:hover {
  background: var(--sofia-ok);
  color: #fff;
}

.perm-btn--deny {
  border: 1px solid var(--sofia-border-strong);
  background: var(--sofia-surface-2);
  color: var(--sofia-text-muted);
}

.perm-btn--deny:hover {
  border-color: var(--sofia-err);
  color: var(--sofia-err);
}

.perm-resolved {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  font-size: 0.78rem;
  font-weight: 600;
  padding: 0.3rem 0.6rem;
  border-radius: 8px;
}

.perm-resolved--ok {
  color: var(--sofia-ok);
  background: var(--sofia-ok-soft);
}

.perm-resolved--err {
  color: var(--sofia-err);
  background: var(--sofia-err-soft);
}

/* --- clarifying question ------------------------------------------------- */
.q-card {
  margin-top: 0.5rem;
  border: 1px solid var(--sofia-primary);
  border-radius: 10px;
  background: var(--sofia-primary-soft);
  padding: 0.6rem 0.7rem;
}

.q-head {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  color: var(--sofia-primary);
  margin-bottom: 0.45rem;
}

.q-title {
  font-size: 0.8125rem;
  font-weight: 600;
}

.q-free {
  padding: 0.3rem 0.6rem;
  border: 1px dashed var(--sofia-border-strong);
  border-radius: 8px;
  background: transparent;
  color: var(--sofia-text);
  font-size: 0.75rem;
}

.q-free:focus {
  outline: none;
  border-style: solid;
  border-color: #a9744f;
}

.q-options {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 0.3rem;
}

.q-opt {
  padding: 0.3rem 0.6rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-size: 0.75rem;
  text-align: left;
  cursor: pointer;
  transition: background 0.12s ease, border-color 0.12s ease;
}

.q-opt:hover:not(:disabled) {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft-strong);
}

.q-opt--chosen {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary);
  color: #fff;
}

.q-opt:disabled {
  cursor: default;
  opacity: 0.85;
}

.q-answer {
  display: inline-flex;
  align-items: center;
  gap: 0.35rem;
  margin-top: 0.5rem;
  font-size: 0.78rem;
  font-weight: 600;
  color: var(--sofia-primary);
}

/* --- multi-question carousel --------------------------------------------- */
.q-carousel {
  margin-top: 0.5rem;
  border: 1px solid var(--sofia-primary);
  border-radius: 10px;
  background: var(--sofia-primary-soft);
  padding: 0.6rem 0.7rem;
}

.q-carousel-head {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  color: var(--sofia-primary);
  margin-bottom: 0.5rem;
}

.q-carousel-title {
  font-size: 0.8125rem;
  font-weight: 600;
}

.q-carousel-count {
  margin-left: auto;
  font-size: 0.7rem;
  color: var(--sofia-text-faint);
}

.q-slide-prompt {
  font-size: 0.85rem;
  font-weight: 600;
  color: var(--sofia-text);
  margin-bottom: 0.5rem;
}

.q-slide--confirm .q-slide-prompt {
  color: var(--sofia-primary);
}

.q-summary {
  list-style: none;
  margin: 0 0 0.6rem;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
}

.q-summary-row {
  display: grid;
  grid-template-columns: 1fr auto;
  column-gap: 0.5rem;
  row-gap: 0.15rem;
  align-items: center;
  font-size: 0.78rem;
}

.q-summary-q {
  grid-column: 1;
  color: var(--sofia-text-muted);
  min-width: 0;
}

.q-summary-a {
  color: var(--sofia-text-faint);
  font-weight: 600;
}

.q-summary-a--set {
  color: var(--sofia-primary);
}

.q-confirm-btn {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  margin: 1.25rem auto 1rem;
  padding: 0.4rem 0.8rem;
  border-radius: 8px;
  border: none;
  background: var(--sofia-primary);
  color: #fff;
  font-size: 0.78rem;
  font-weight: 600;
  cursor: pointer;
}

.q-confirm-btn:hover {
  background: var(--sofia-primary-strong);
}

.q-carousel-nav {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  margin-top: 0.6rem;
}

.q-nav-arrow {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 26px;
  height: 26px;
  border-radius: 7px;
  border: 1px solid var(--sofia-border-strong);
  background: var(--sofia-surface-2);
  color: var(--sofia-text-muted);
  cursor: pointer;
}

.q-nav-arrow:disabled {
  opacity: 0.35;
  cursor: default;
}

.q-nav-arrow:not(:disabled):hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.q-dots {
  display: flex;
  gap: 0.3rem;
}

.q-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--sofia-border-strong);
}

.q-dot--active {
  background: var(--sofia-primary);
}

.q-confirmed {
  margin-top: 0.5rem;
  padding: 0.4rem 0.6rem;
  border-radius: 8px;
  background: var(--sofia-ok-soft);
  color: var(--sofia-ok);
  font-size: 0.78rem;
}

.q-summary--done {
  margin: 0;
}

.q-summary-answers {
  grid-column: 1;
  display: flex;
  flex-wrap: wrap;
  gap: 0.25rem;
}

.q-chip {
  padding: 0;
  background: transparent;
  color: #fff;
  font-weight: 600;
}

.q-chip::before {
  content: '>';
  margin-right: 0.35rem;
}

.q-summary--done .q-chip {
  color: var(--sofia-ok);
}

.q-summary-empty {
  color: var(--sofia-text-faint);
}

.q-edit {
  grid-column: 2;
  grid-row: 1 / span 2;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--sofia-text-faint);
  cursor: pointer;
}

.q-edit:hover {
  background: var(--sofia-primary-soft-strong);
  color: var(--sofia-primary);
}

.q-slide-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  margin-top: 0.6rem;
}

.q-hint {
  font-size: 0.7rem;
  color: var(--sofia-text-faint);
}

.q-step-btn {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  padding: 0.3rem 0.7rem;
  border: none;
  border-radius: 8px;
  background: var(--sofia-primary);
  color: #fff;
  font-size: 0.75rem;
  font-weight: 600;
  cursor: pointer;
}

.q-step-btn:disabled,
.q-confirm-btn:disabled {
  opacity: 0.4;
  cursor: default;
}

/* Pages slide in from the right and out to the left (reversed when going back). */
.q-stage {
  overflow: hidden;
}

.q-fwd-enter-active,
.q-fwd-leave-active,
.q-back-enter-active,
.q-back-leave-active {
  transition: transform 0.22s ease, opacity 0.22s ease;
}

.q-fwd-enter-from,
.q-back-leave-to {
  transform: translateX(32px);
  opacity: 0;
}

.q-fwd-leave-to,
.q-back-enter-from {
  transform: translateX(-32px);
  opacity: 0;
}

@media (prefers-reduced-motion: reduce) {
  .q-fwd-enter-active,
  .q-fwd-leave-active,
  .q-back-enter-active,
  .q-back-leave-active {
    transition: none;
  }
}

/* --- deep-search report card ---------------------------------------------- */
.report {
  margin-top: 0.6rem;
  max-width: 74ch;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 12px;
  background: var(--sofia-surface-2);
  overflow: hidden;
}

.report-head {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.55rem 0.75rem;
  border-bottom: 1px solid var(--sofia-border);
  background: var(--sofia-surface);
}

.report-icon {
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.report-title {
  font-size: 0.875rem;
  font-weight: 650;
  color: var(--sofia-text);
}

.report-badge {
  margin-left: auto;
  padding: 0.1rem 0.5rem;
  border-radius: 999px;
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
  font-size: 0.625rem;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.report-summary {
  margin: 0;
  padding: 0.6rem 0.75rem 0;
  font-size: 0.8125rem;
  line-height: 1.55;
  color: var(--sofia-text-muted);
}

.report-sections {
  display: flex;
  flex-direction: column;
  gap: 0.55rem;
  padding: 0.6rem 0.75rem;
}

.report-heading {
  margin: 0 0 0.2rem;
  font-size: 0.78rem;
  font-weight: 650;
  color: var(--sofia-text);
}

.report-body {
  margin: 0;
  font-size: 0.8125rem;
  line-height: 1.6;
  color: var(--sofia-text-muted);
}

.report-file {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.5rem 0.75rem;
  border-top: 1px solid var(--sofia-border);
  background: var(--sofia-surface);
  font-size: 0.75rem;
}

.report-file svg {
  color: var(--sofia-text-muted);
  flex-shrink: 0;
}

.report-file-name {
  flex: 1;
  min-width: 0;
  color: var(--sofia-text);
}

.report-file-size {
  flex-shrink: 0;
  color: var(--sofia-text-faint);
}

.report-open {
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  flex-shrink: 0;
  padding: 0.28rem 0.6rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font-size: 0.72rem;
  font-weight: 600;
  cursor: pointer;
  transition: background 0.12s ease, border-color 0.12s ease, color 0.12s ease;
}

.report-open:hover {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
  color: var(--sofia-primary);
}

/* --- attachments -------------------------------------------------------- */
.attachments {
  display: flex;
  flex-wrap: wrap;
  gap: 0.4rem;
  margin-bottom: 0.5rem;
}

.attach-chip {
  display: inline-flex;
  align-items: center;
  gap: 0.4rem;
  max-width: 14rem;
  padding: 0.3rem 0.55rem;
  background: var(--sofia-surface-2);
  border: 1px solid var(--sofia-border);
  border-radius: 8px;
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
}

.attach-thumb {
  width: 26px;
  height: 26px;
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

a.attach-chip {
  text-decoration: none;
}

a.attach-chip:hover {
  border-color: var(--sofia-primary);
}
</style>
