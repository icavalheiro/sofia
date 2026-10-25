// ---------------------------------------------------------------------------
// Sofia.Server connection: REST helper, attachment upload and the SignalR
// event hub. Module-level singleton, like the other composables.
//
// Startup tries, in order: the connection saved from the connect panel
// (localStorage `sofia.backend.v2`), `VITE_SOFIA_SERVER`, then the same origin
// (the server serves the built UI; in dev Vite proxies /api and /hubs). When
// nothing answers the state parks at `needs-connection` and the connect panel
// asks for a URL + token or a pairing code.
// ---------------------------------------------------------------------------

import { reactive } from 'vue'
import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr'

import type { AttachmentDto, SofiaEvent } from '@/types'

export type BackendPhase = 'checking' | 'needs-connection' | 'ready' | 'error'

export interface BackendState {
  phase: BackendPhase
  /** Server base URL; null/'' = same origin. */
  url: string | null
  /** Bearer token for remote (non-loopback) access. */
  token: string | null
  version: string | null
  error: string | null
  log: string[]
}

/** A connection saved from the connect panel. */
export interface StoredConnection {
  url: string
  token: string
}

/** Placeholder offered in the connect panel. */
export const DEFAULT_SERVER_URL = 'http://127.0.0.1:5199'

const ENV_SERVER_URL = (import.meta.env.VITE_SOFIA_SERVER ?? '').trim()
const CONNECTION_KEY = 'sofia.backend.v2'
const PROBE_TIMEOUT_MS = 8000
const HEALTH_CHECK_MS = 20000
const START_HINT = 'Start it with `dotnet run --project src/Sofia.Server`.'

const state = reactive<BackendState>({
  phase: 'checking',
  url: null,
  token: null,
  version: null,
  error: null,
  log: [],
})

function logLine(line: string): void {
  state.log.push(line)
  if (state.log.length > 40) state.log.splice(0, state.log.length - 40)
}

function describe(url: string): string {
  return url || (typeof location !== 'undefined' ? location.origin : 'this origin')
}

/** Accepts `host:port`, `http://…/`; empty stays empty (same origin). */
function normalizeUrl(value: string | null | undefined): string {
  const trimmed = (value ?? '').trim().replace(/\/+$/, '')
  if (!trimmed) return ''
  return /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`
}

// --- stored connection ---------------------------------------------------------

export function storedConnection(): StoredConnection | null {
  try {
    const raw = localStorage.getItem(CONNECTION_KEY)
    if (!raw) return null
    const parsed = JSON.parse(raw) as Partial<StoredConnection>
    return {
      url: typeof parsed.url === 'string' ? parsed.url : '',
      token: typeof parsed.token === 'string' ? parsed.token : '',
    }
  } catch {
    return null
  }
}

function saveConnection(connection: StoredConnection): void {
  try {
    localStorage.setItem(CONNECTION_KEY, JSON.stringify(connection))
  } catch {
    logLine('Could not persist the connection (storage unavailable)')
  }
}

/** Drops the saved connection and goes back to the connect panel. */
export function forgetConnection(): void {
  try {
    localStorage.removeItem(CONNECTION_KEY)
  } catch {
    /* storage unavailable — nothing to clear */
  }
  stopHealthCheck()
  void stopHub()
  state.url = null
  state.token = null
  state.version = null
  state.error = null
  state.phase = 'needs-connection'
  logLine('Connection forgotten')
}

// --- probing -------------------------------------------------------------------

type ProbeResult =
  | { ok: true; version: string | null }
  | { ok: false; reason: 'unauthorized' | 'unreachable' | 'not-sofia' }

function authHeaders(token: string | null | undefined): Record<string, string> {
  return token ? { Authorization: `Bearer ${token}` } : {}
}

async function timedFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const controller = new AbortController()
  const timer = window.setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS)
  try {
    return await fetch(input, { ...init, signal: controller.signal })
  } finally {
    window.clearTimeout(timer)
  }
}

/** Liveness via `/api/health`, then authorization via `/api/server`. */
async function probe(url: string, token: string | null): Promise<ProbeResult> {
  try {
    const health = await timedFetch(`${url}/api/health`, { headers: authHeaders(token) })
    if (health.status === 401 || health.status === 403) return { ok: false, reason: 'unauthorized' }
    if (!health.ok) return { ok: false, reason: 'unreachable' }
    const body = (await health.json().catch(() => null)) as { name?: string; version?: string } | null
    if (!body || body.name !== 'sofia') return { ok: false, reason: 'not-sofia' }
    const info = await timedFetch(`${url}/api/server`, { headers: authHeaders(token) })
    if (info.status === 401 || info.status === 403) return { ok: false, reason: 'unauthorized' }
    return { ok: true, version: body.version ?? null }
  } catch {
    return { ok: false, reason: 'unreachable' }
  }
}

function failureText(url: string, reason: 'unauthorized' | 'unreachable' | 'not-sofia'): string {
  const where = describe(url)
  if (reason === 'unauthorized') {
    return `${where} rejected the request. Remote clients need a token — pair this device with a code from Settings → Remote access on the server machine.`
  }
  if (reason === 'not-sofia') return `${where} answered, but it is not a Sofia server.`
  return `No Sofia server answered at ${where}. ${START_HINT}`
}

// --- health check --------------------------------------------------------------

let healthTimer: number | undefined

function stopHealthCheck(): void {
  if (healthTimer !== undefined) {
    window.clearInterval(healthTimer)
    healthTimer = undefined
  }
}

function startHealthCheck(): void {
  stopHealthCheck()
  healthTimer = window.setInterval(() => {
    void (async () => {
      if (state.phase !== 'ready') return
      if (typeof document !== 'undefined' && document.visibilityState === 'hidden') return
      const url = state.url ?? ''
      const result = await probe(url, state.token)
      if (result.ok || state.phase !== 'ready') return
      stopHealthCheck()
      void stopHub()
      state.phase = 'needs-connection'
      state.error = `Lost connection to ${describe(url)}. ${result.reason === 'unauthorized' ? 'The token was rejected.' : START_HINT}`
      logLine(state.error)
    })()
  }, HEALTH_CHECK_MS)
}

function markReady(url: string, token: string | null, version: string | null): void {
  state.url = url
  state.token = token || null
  state.version = version
  state.error = null
  state.phase = 'ready'
  logLine(`Connected to ${describe(url)}${version ? ` (Sofia.Server ${version})` : ''}`)
  startHealthCheck()
  void startHub()
}

/**
 * Startup / retry sequence. Never simulates a connection: when nothing
 * answers it parks at `needs-connection`, which shows the connect panel.
 */
export async function ensureBackend(): Promise<void> {
  state.phase = 'checking'
  state.error = null
  stopHealthCheck()
  logLine('Looking for Sofia.Server…')

  const stored = storedConnection()
  const candidates: StoredConnection[] = []
  const add = (url: string, token: string): void => {
    const normalized = normalizeUrl(url)
    if (!candidates.some((c) => c.url === normalized && c.token === token)) candidates.push({ url: normalized, token })
  }
  if (stored) add(stored.url, stored.token)
  if (ENV_SERVER_URL) add(ENV_SERVER_URL, '')
  add('', '')

  let lastFailure = ''
  for (const candidate of candidates) {
    logLine(`Probing ${describe(candidate.url)}…`)
    const result = await probe(candidate.url, candidate.token || null)
    if (result.ok) {
      markReady(candidate.url, candidate.token || null, result.version)
      return
    }
    lastFailure = failureText(candidate.url, result.reason)
    logLine(lastFailure)
  }

  state.url = stored?.url ?? null
  state.token = null
  state.phase = 'needs-connection'
  state.error = lastFailure || `No Sofia server answered. ${START_HINT}`
}

/**
 * Connects with details typed into the connect panel and remembers them.
 * Throws a user-facing message on failure (also kept in `state.error`).
 */
export async function connect(url: string, token: string): Promise<void> {
  const target = normalizeUrl(url)
  state.error = null
  logLine(`Connecting to ${describe(target)}…`)
  const result = await probe(target, token.trim() || null)
  if (!result.ok) {
    state.phase = 'needs-connection'
    state.error = failureText(target, result.reason)
    logLine(state.error)
    throw new Error(state.error)
  }
  saveConnection({ url: target, token: token.trim() })
  markReady(target, token.trim() || null, result.version)
}

/** Exchanges a one-time pairing code for a token, then connects with it. */
export async function pairWithCode(url: string, code: string): Promise<void> {
  const target = normalizeUrl(url)
  state.error = null
  logLine(`Pairing with ${describe(target)}…`)
  let token = ''
  try {
    const res = await timedFetch(`${target}/api/pair/connect`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({ code: code.trim() }),
    })
    const body = (await res.json().catch(() => null)) as { token?: string; error?: string } | null
    if (!res.ok || !body?.token) throw new Error(body?.error ?? (res.statusText || 'Pairing failed'))
    token = body.token
  } catch (e) {
    state.phase = 'needs-connection'
    state.error = e instanceof Error && e.name !== 'AbortError' ? e.message : failureText(target, 'unreachable')
    logLine(state.error)
    throw new Error(state.error)
  }
  await connect(target, token)
}

export function useBackend(): { state: BackendState } {
  return { state }
}

// --- REST ----------------------------------------------------------------------

function base(): string {
  return state.url ?? ''
}

async function errorFrom(res: Response): Promise<Error> {
  const body = (await res.json().catch(() => null)) as { error?: string } | null
  return new Error(body?.error ?? (res.statusText || `HTTP ${res.status}`))
}

/** JSON request against Sofia.Server. Throws `Error(body.error)` on non-2xx. */
export async function api<T = unknown>(method: string, path: string, body?: unknown): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json', ...authHeaders(state.token) }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  const res = await fetch(`${base()}${path}`, {
    method,
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  if (!res.ok) throw await errorFrom(res)
  if (res.status === 204) return undefined as T
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

/** Uploads one file (multipart field `file`) for a later prompt. */
export async function uploadAttachment(file: File): Promise<AttachmentDto> {
  const form = new FormData()
  form.append('file', file, file.name)
  const res = await fetch(`${base()}/api/attachments`, {
    method: 'POST',
    headers: { Accept: 'application/json', ...authHeaders(state.token) },
    body: form,
  })
  if (!res.ok) throw await errorFrom(res)
  return (await res.json()) as AttachmentDto
}

/**
 * Absolute URL for a server-relative path (attachment previews). Remote
 * connections carry the token as `access_token`, since <img> cannot send
 * headers.
 */
export function serverUrl(relative: string): string {
  if (/^(https?:|blob:|data:)/i.test(relative)) return relative
  const url = `${base()}${relative}`
  if (!state.token) return url
  return `${url}${url.includes('?') ? '&' : '?'}access_token=${encodeURIComponent(state.token)}`
}

/**
 * WebSocket URL for a server-relative path. Remote connections carry the token
 * as `access_token`, since browsers cannot set headers on a WebSocket.
 */
export function serverSocketUrl(relative: string): string {
  const url = new URL(relative, base() || location.origin)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  if (state.token) url.searchParams.set('access_token', state.token)
  return url.toString()
}

// --- live events (SignalR) -----------------------------------------------------

type EventHandler = (event: SofiaEvent) => void

const listeners = new Set<EventHandler>()
let hub: HubConnection | null = null
let hubRetry: number | undefined

function emit(event: SofiaEvent): void {
  for (const handler of [...listeners]) {
    try {
      handler(event)
    } catch (e) {
      console.error('Sofia event handler failed', event.type, e)
    }
  }
}

/** Subscribes to every server event; returns the unsubscribe function. */
export function onServerEvent(handler: EventHandler): () => void {
  listeners.add(handler)
  return () => listeners.delete(handler)
}

function clearHubRetry(): void {
  if (hubRetry !== undefined) {
    window.clearTimeout(hubRetry)
    hubRetry = undefined
  }
}

async function startHub(): Promise<void> {
  await stopHub()
  const connection = new HubConnectionBuilder()
    .withUrl(`${base()}/hubs/events`, { accessTokenFactory: () => state.token ?? '' })
    .withAutomaticReconnect({
      nextRetryDelayInMilliseconds: (ctx) => Math.min(15000, 1000 * 2 ** ctx.previousRetryCount),
    })
    .configureLogging(LogLevel.Warning)
    .build()
  connection.on('event', (event: SofiaEvent) => emit(event))
  connection.onreconnected(() => {
    logLine('Event stream reconnected')
    emit({ type: 'resync', data: null })
  })
  connection.onclose(() => {
    // Automatic reconnect gave up (or the server closed): start over while ready.
    if (hub !== connection || state.phase !== 'ready') return
    scheduleHubRestart(connection)
  })
  hub = connection
  try {
    await connection.start()
  } catch (e) {
    logLine(`Event stream failed to start: ${e instanceof Error ? e.message : String(e)}`)
    if (hub === connection && state.phase === 'ready') scheduleHubRestart(connection)
  }
}

function scheduleHubRestart(connection: HubConnection): void {
  clearHubRetry()
  hubRetry = window.setTimeout(() => {
    hubRetry = undefined
    if (hub !== connection || state.phase !== 'ready') return
    connection
      .start()
      .then(() => emit({ type: 'resync', data: null }))
      .catch(() => scheduleHubRestart(connection))
  }, 5000)
}

async function stopHub(): Promise<void> {
  clearHubRetry()
  const connection = hub
  hub = null
  if (!connection) return
  try {
    await connection.stop()
  } catch {
    /* already stopped */
  }
}
