// ---------------------------------------------------------------------------
// Git worktree helpers for a folder workspace (`/api/worktrees`). Every call
// is scoped by the workspace directory; the server resolves the repository.
// ---------------------------------------------------------------------------

import { api } from '@/services/sofia'

/** Compares two absolute paths ignoring separators, trailing slashes and case. */
export function samePath(a: string, b: string): boolean {
  const norm = (p: string): string => p.replace(/\//g, '\\').replace(/\\+$/, '').toLowerCase()
  return norm(a) === norm(b)
}

export interface WorktreeEntry {
  directory: string
  branch: string
  /** The repository's main checkout — the one that must not be removed. */
  isMain: boolean
}

export interface WorktreeList {
  /** Repository root ('' when the folder is not a git repository). */
  root: string
  worktrees: WorktreeEntry[]
}

export async function listWorktrees(directory: string): Promise<WorktreeList> {
  const res = await api<WorktreeList>('GET', `/api/worktrees?directory=${encodeURIComponent(directory)}`)
  return { root: res?.root ?? '', worktrees: res?.worktrees ?? [] }
}

/** Creates a worktree next to the repository owning `directory`. */
export async function createWorktree(directory: string, name: string): Promise<WorktreeEntry> {
  return api<WorktreeEntry>('POST', '/api/worktrees', { directory, name })
}

/** Removes a worktree. `force` discards modified and untracked files. */
export async function removeWorktree(directory: string, worktree: string, force = false): Promise<void> {
  await api('DELETE', '/api/worktrees', { directory, worktree, force })
}

/** Last path segment — the worktree's display name. */
export function worktreeName(directory: string): string {
  return directory.split(/[\\/]/).filter(Boolean).pop() ?? directory
}
