<!-- -------------------------------------------------------------------------
  Startup gate / reconnect panel for Sofia.Server. Probes the saved
  connection, VITE_SOFIA_SERVER and the same origin; when nothing answers it
  asks for a server URL (+ token for remote access) or a one-time pairing
  code. The same panel comes back whenever a connected server drops.

  English-only on purpose: it renders before the shell (and before the
  language preference is applied).
--------------------------------------------------------------------------- -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'

import {
  DEFAULT_SERVER_URL,
  connect,
  ensureBackend,
  forgetConnection,
  pairWithCode,
  storedConnection,
  useBackend,
} from '@/services/sofia'

const { state } = useBackend()

const url = ref('')
const token = ref('')
const code = ref('')
const mode = ref<'token' | 'pair'>('token')
const busy = ref(false)
/** True when localStorage holds a connection the user can forget. */
const hasSaved = ref(false)

const showForm = computed(() => state.phase === 'needs-connection' || state.phase === 'error')

/** Prefills the form from the saved connection. */
function syncForm(): void {
  const saved = storedConnection()
  hasSaved.value = saved !== null
  url.value = saved?.url ?? state.url ?? ''
  token.value = saved?.token ?? ''
}

/** Re-runs discovery: saved connection, VITE_SOFIA_SERVER, same origin. */
async function retry(): Promise<void> {
  try {
    await ensureBackend()
  } finally {
    if (showForm.value) syncForm()
  }
}

async function submit(): Promise<void> {
  busy.value = true
  try {
    if (mode.value === 'pair') {
      await pairWithCode(url.value, code.value)
      code.value = ''
    } else {
      await connect(url.value, token.value)
    }
  } catch {
    /* the message is parked in state.error */
  } finally {
    busy.value = false
  }
}

function forget(): void {
  forgetConnection()
  syncForm()
}

onMounted(() => {
  void retry()
})
</script>

<template>
  <div class="gate">
    <div class="card">
      <h1>Sofia</h1>
      <p class="sub">Local AI harness · Sofia.Server</p>

      <p v-if="state.phase === 'checking'" class="status">Looking for Sofia.Server…</p>

      <template v-else-if="showForm">
        <p class="error">
          {{ state.phase === 'needs-connection' ? 'Connect to a Sofia server.' : 'Could not reach the backend.' }}
        </p>
        <p v-if="state.error" class="detail">{{ state.error }}</p>

        <form class="form" @submit.prevent="submit">
          <label class="field">
            <span class="field-label">Server URL</span>
            <input
              v-model="url"
              class="field-input"
              type="text"
              inputmode="url"
              autocomplete="off"
              spellcheck="false"
              :placeholder="DEFAULT_SERVER_URL"
              :disabled="busy"
            />
          </label>

          <div class="mode-switch" role="group">
            <button
              class="mode-btn"
              :class="{ 'mode-btn--active': mode === 'token' }"
              type="button"
              :disabled="busy"
              @click="mode = 'token'"
            >Token</button>
            <button
              class="mode-btn"
              :class="{ 'mode-btn--active': mode === 'pair' }"
              type="button"
              :disabled="busy"
              @click="mode = 'pair'"
            >Pairing code</button>
          </div>

          <label v-if="mode === 'token'" class="field">
            <span class="field-label">Token (optional, for remote access)</span>
            <input
              v-model="token"
              class="field-input"
              type="password"
              autocomplete="off"
              placeholder="leave empty on this machine"
              :disabled="busy"
            />
          </label>

          <label v-else class="field">
            <span class="field-label">Pairing code</span>
            <input
              v-model="code"
              class="field-input"
              type="text"
              autocomplete="off"
              spellcheck="false"
              placeholder="code from Settings → Remote access"
              :disabled="busy"
            />
          </label>

          <p class="hint">
            Start the server with <code>dotnet run --project src/Sofia.Server</code>
            (it listens on <code>{{ DEFAULT_SERVER_URL }}</code>). Leave the URL empty to use
            this origin. Another device connects with a pairing code generated on the
            server machine; Sofia remembers the connection for the next start.
          </p>

          <div class="actions">
            <button class="primary" type="submit" :disabled="busy || (mode === 'pair' && !code.trim())">
              {{ busy ? 'Connecting…' : mode === 'pair' ? 'Pair' : 'Connect' }}
            </button>
            <button class="ghost" type="button" :disabled="busy" @click="retry">Search again</button>
            <button v-if="hasSaved" class="ghost" type="button" :disabled="busy" @click="forget">
              Forget connection
            </button>
          </div>
        </form>
      </template>

      <details v-if="state.log.length" class="log">
        <summary>Details</summary>
        <pre>{{ state.log.join('\n') }}</pre>
      </details>
    </div>
  </div>
</template>
<style scoped>
.gate {
  display: flex;
  align-items: center;
  justify-content: center;
  height: 100vh;
  background: var(--sofia-bg);
  color: var(--sofia-text);
}
.card {
  max-width: 34rem;
  width: calc(100% - 3rem);
  padding: 2rem;
  border: 1px solid var(--sofia-border);
  border-radius: 0.75rem;
  background: var(--sofia-surface);
}
h1 {
  margin: 0;
  font-size: 1.5rem;
}
.sub {
  margin: 0.25rem 0 1.25rem;
  color: var(--sofia-text-muted);
}
.status {
  color: var(--sofia-text-muted);
}
.error {
  color: var(--sofia-err);
  font-weight: 600;
}
.detail {
  color: var(--sofia-text-muted);
  font-size: 0.875rem;
}
.guide {
  padding-left: 1.25rem;
  display: grid;
  gap: 0.5rem;
}
code {
  font-family: var(--font-family-monospace);
  background: var(--sofia-surface-2);
  padding: 0.1rem 0.35rem;
  border-radius: 0.25rem;
}
.hint {
  color: var(--sofia-text-muted);
  font-size: 0.875rem;
}
.form {
  display: grid;
  gap: 0.75rem;
  margin: 1rem 0 0;
}
.field {
  display: grid;
  gap: 0.3rem;
}
.field-label {
  font-size: 0.75rem;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--sofia-text-faint);
}
.field-input {
  padding: 0.5rem 0.65rem;
  border: 1px solid var(--sofia-border);
  border-radius: 0.5rem;
  background: var(--sofia-surface-2);
  color: var(--sofia-text);
  font: inherit;
}
.field-input:focus {
  outline: 2px solid var(--sofia-primary);
  outline-offset: 1px;
}
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.5rem;
  margin-top: 0.25rem;
}
.primary,
.ghost {
  padding: 0.5rem 1.25rem;
  border-radius: 0.5rem;
  font-weight: 600;
  cursor: pointer;
}
.primary {
  border: none;
  background: var(--sofia-primary);
  color: var(--sofia-primary-ink);
}
.primary:disabled,
.ghost:disabled {
  opacity: 0.6;
  cursor: default;
}
.ghost {
  border: 1px solid var(--sofia-border);
  background: transparent;
  color: var(--sofia-text-muted);
  font-weight: 500;
}
.ghost:hover:not(:disabled) {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}
.mode-switch {
  display: inline-flex;
  justify-self: start;
  border: 1px solid var(--sofia-border);
  border-radius: 999px;
  overflow: hidden;
}
.mode-btn {
  padding: 0.3rem 0.9rem;
  border: none;
  background: transparent;
  color: var(--sofia-text-muted);
  font: inherit;
  font-size: 0.8rem;
  font-weight: 600;
  cursor: pointer;
}
.mode-btn--active {
  background: var(--sofia-surface-2);
  color: var(--sofia-primary);
}
.log {
  margin-top: 1rem;
  font-size: 0.8rem;
  color: var(--sofia-text-muted);
}
.log pre {
  white-space: pre-wrap;
  font-family: var(--font-family-monospace);
}
</style>
