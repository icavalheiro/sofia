<script setup lang="ts">
// One-time pairing code for another device.
//
// `POST /api/pair` (loopback only) mints a short-lived code ({ code,
// expiresIn }) that a remote client exchanges for a token through
// `POST /api/pair/connect`; the server accepts it once. The dialog shows the
// code, counts the expiry down, lists the addresses the server answers on
// (`GET /api/server`) and lets the user mint a fresh code when it lapses.
import { computed, onBeforeUnmount, ref, watch } from 'vue'

import { api } from '@/services/sofia'
import { useI18n } from '@/i18n'
import SofiaIcon from '@/components/SofiaIcon.vue'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'close'): void }>()

const { t } = useI18n()

const code = ref('')
const urls = ref<string[]>([])
// True when the server listens on loopback only, so the addresses are not reachable yet.
const localOnly = ref(false)
const expiresAt = ref(0)
const now = ref(Date.now())
const loading = ref(false)
const copied = ref(false)
const error = ref<string | null>(null)

let ticker: number | undefined
let copiedTimer: number | undefined

const remaining = computed(() => Math.max(0, Math.ceil((expiresAt.value - now.value) / 1000)))
const expired = computed(() => !!code.value && remaining.value <= 0)
const countdown = computed(() => {
  const seconds = remaining.value
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
})

function stopTicker(): void {
  if (ticker !== undefined) {
    window.clearInterval(ticker)
    ticker = undefined
  }
}

function startTicker(): void {
  stopTicker()
  ticker = window.setInterval(() => {
    now.value = Date.now()
    if (expired.value) stopTicker()
  }, 500)
}

async function generate(): Promise<void> {
  loading.value = true
  error.value = null
  copied.value = false
  stopTicker()
  try {
    const pair = await api<{ code: string; expiresIn: number }>('POST', '/api/pair')
    code.value = pair?.code ?? ''
    expiresAt.value = Date.now() + Number(pair?.expiresIn ?? 0) * 1000
    now.value = Date.now()
    if (!code.value) {
      error.value = t('pair.error')
      return
    }
    startTicker()
    // Addresses another device can reach this server on (best effort).
    try {
      const info = await api<{ urls: string[]; networkReachable?: boolean }>('GET', '/api/server')
      urls.value = info?.urls ?? []
      localOnly.value = info?.networkReachable === false
    } catch {
      urls.value = []
      localOnly.value = false
    }
  } catch (e) {
    code.value = ''
    error.value = e instanceof Error && e.message ? e.message : t('pair.error')
  } finally {
    loading.value = false
  }
}

async function copy(): Promise<void> {
  try {
    await navigator.clipboard.writeText(code.value)
    copied.value = true
    if (copiedTimer !== undefined) window.clearTimeout(copiedTimer)
    copiedTimer = window.setTimeout(() => {
      copied.value = false
    }, 2000)
  } catch {
    error.value = t('pair.copyFailed')
  }
}

watch(
  () => props.open,
  (open) => {
    if (open) {
      void generate()
      return
    }
    stopTicker()
    code.value = ''
    urls.value = []
    localOnly.value = false
    error.value = null
    copied.value = false
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  stopTicker()
  if (copiedTimer !== undefined) window.clearTimeout(copiedTimer)
})
</script>

<template>
  <Teleport to="body">
    <div v-if="open" class="pair-overlay" @click.self="emit('close')">
      <div class="pair-modal" role="dialog" aria-modal="true" :aria-label="t('pair.title')">
        <div class="pair-head">
          <span class="pair-title">
            <SofiaIcon name="link" :size="'1rem'" />
            {{ t('pair.title') }}
          </span>
          <button class="close-btn" type="button" :aria-label="t('pair.close')" @click="emit('close')">
            <SofiaIcon name="x" :size="'0.95rem'" />
          </button>
        </div>

        <div class="pair-body">
          <p class="pair-intro">{{ t('pair.intro') }}</p>

          <p v-if="error" class="pair-error">{{ error }}</p>
          <p v-else-if="loading" class="pair-note">{{ t('pair.loading') }}</p>

          <template v-else-if="code">
            <div class="pair-code-box">
              <span class="pair-code sofia-mono" :class="{ 'pair-code--expired': expired }">{{ code }}</span>
              <button class="pair-btn pair-btn--primary" type="button" @click="copy">
                {{ copied ? t('pair.copied') : t('pair.copy') }}
              </button>
            </div>

            <p class="pair-expiry" :class="{ 'pair-expiry--expired': expired }">
              <SofiaIcon :name="expired ? 'alert' : 'clock'" :size="'0.85rem'" />
              <span v-if="expired">{{ t('pair.expired') }}</span>
              <span v-else>{{ t('pair.expiresIn') }} {{ countdown }}</span>
            </p>

            <div v-if="urls.length" class="pair-urls">
              <span class="pair-label">{{ t('pair.address') }}</span>
              <code v-for="url in urls" :key="url" class="pair-url sofia-mono">{{ url }}</code>
            </div>
            <p v-if="localOnly" class="pair-error">{{ t('pair.localOnly') }}</p>

            <p class="pair-note">{{ t('pair.hint') }}</p>

            <button class="pair-btn pair-btn--wide" type="button" @click="generate">
              <SofiaIcon name="refresh" :size="'0.85rem'" />
              {{ t('pair.newCode') }}
            </button>
          </template>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.pair-overlay {
  position: fixed;
  inset: 0;
  /* Above the settings modal (z-index 2000), which is where it opens from. */
  z-index: 2100;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2rem;
  background: rgba(0, 0, 0, 0.45);
}

.pair-modal {
  width: min(420px, 100%);
  display: flex;
  flex-direction: column;
  background: var(--sofia-surface);
  border: 1px solid var(--sofia-border-strong);
  border-radius: 14px;
  box-shadow: 0 24px 60px rgba(0, 0, 0, 0.5);
  overflow: hidden;
}

.pair-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.75rem 0.9rem;
  border-bottom: 1px solid var(--sofia-border);
}

.pair-title {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.875rem;
  font-weight: 600;
  color: var(--sofia-primary);
}

.pair-body {
  display: flex;
  flex-direction: column;
  gap: 0.6rem;
  padding: 0.9rem;
}

.pair-intro {
  margin: 0;
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
}

.pair-error {
  margin: 0;
  font-size: 0.75rem;
  color: var(--sofia-err);
}

.pair-note {
  margin: 0;
  font-size: 0.7rem;
  color: var(--sofia-text-faint);
}

.pair-code-box {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 0.7rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 10px;
  background: var(--sofia-surface-2);
}

.pair-code {
  flex: 1;
  min-width: 0;
  font-size: 0.875rem;
  letter-spacing: 0.02em;
  color: var(--sofia-text);
  overflow-wrap: anywhere;
}

.pair-code--expired {
  color: var(--sofia-text-faint);
  text-decoration: line-through;
}

.pair-expiry {
  display: flex;
  align-items: center;
  gap: 0.35rem;
  margin: 0;
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
}

.pair-expiry--expired {
  color: var(--sofia-err);
}

.pair-urls {
  display: flex;
  flex-direction: column;
  gap: 0.15rem;
}

.pair-label {
  font-size: 0.625rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}

.pair-url {
  font-size: 0.7rem;
  color: var(--sofia-text-muted);
  overflow-wrap: anywhere;
}

.pair-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 0.35rem;
  padding: 0.3rem 0.65rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 8px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.75rem;
  white-space: nowrap;
  cursor: pointer;
}

.pair-btn:hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.pair-btn--primary {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.pair-btn--wide {
  width: 100%;
}
</style>
