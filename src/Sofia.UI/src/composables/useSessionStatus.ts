import { reactive, watch } from 'vue'

import { api, onServerEvent, useBackend } from '@/services/sofia'
import type { SessionStatus } from '@/types'

// ---------------------------------------------------------------------------
// Global live-status store for sidebar / header badges.
//
// `GET /api/sessions/status` snapshots every non-idle session (also runs
// started in another window or before a reload); `session.status` events keep
// it current, and a `resync` (event stream reconnected) re-reads the snapshot.
// ---------------------------------------------------------------------------

/** Non-idle sessions, by id. */
const statuses = reactive(new Map<string, SessionStatus>())

let started = false

async function reconcile(): Promise<void> {
  try {
    const snapshot = await api<Record<string, SessionStatus>>('GET', '/api/sessions/status')
    const next = new Map(Object.entries(snapshot ?? {}).filter(([, s]) => s && s !== 'idle'))
    for (const id of [...statuses.keys()]) if (!next.has(id)) statuses.delete(id)
    for (const [id, status] of next) statuses.set(id, status)
  } catch {
    /* offline — keep whatever we already know */
  }
}

/** Idempotent startup — called once the backend gate reports ready. */
export function startSessionStatusWatcher(): void {
  if (started) return
  started = true
  onServerEvent((event) => {
    if (event.type === 'resync') {
      void reconcile()
      return
    }
    if (event.type === 'session.deleted') {
      const id = (event.data as { id?: string } | null)?.id ?? event.sessionId
      if (id) statuses.delete(id)
      return
    }
    if (event.type !== 'session.status' || !event.sessionId) return
    const status = (event.data as { status?: SessionStatus } | null)?.status
    if (!status || status === 'idle') statuses.delete(event.sessionId)
    else statuses.set(event.sessionId, status)
  })
  const { state } = useBackend()
  watch(
    () => state.phase,
    (phase) => {
      if (phase === 'ready') void reconcile()
      else statuses.clear()
    },
    { immediate: true },
  )
}

/** Live badge key for a session ('working' / 'permission' / 'question' / 'error'), or null when idle. */
export function sessionBadge(sessionId: string | undefined): string | null {
  if (!sessionId) return null
  return statuses.get(sessionId) ?? null
}
