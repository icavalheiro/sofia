// Unified-diff parsing for the right-side workspace panel: Sofia.Server
// (`/api/sessions/{id}/diff`, `/api/vcs/diff`) delivers per-file patches as
// plain unified-diff strings, while Sofia's DiffViewer renders aligned
// left/right rows (DiffLine). This module converts one into the other.

import type { DiffLine, FileChange, FileDiffDto } from '@/types'

/** FileDiffDto as returned by the diff endpoints. */
export type FileDiffInfo = FileDiffDto

/** Map the API status word to the one-letter badge the panel styles. */
function badge(status: FileDiffInfo['status']): FileChange['status'] {
  if (status === 'added') return 'A'
  if (status === 'deleted') return 'D'
  return 'M'
}

/**
 * Parse a unified diff body into per-line records for the two-column view.
 * Understands `@@ -a,b +c,d @@` hunk headers (git headers and "\ No newline"
 * markers are skipped); line numbers are tracked the way `diff` numbers them.
 */
export function parseUnifiedPatch(patch: string): DiffLine[] {
  const out: DiffLine[] = []
  let oldNo: number | undefined
  let newNo: number | undefined

  for (const raw of patch.split('\n')) {
    const line = raw.replace(/\r$/, '')
    const hunk = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/.exec(line)
    if (hunk) {
      oldNo = Number(hunk[1])
      newNo = Number(hunk[2])
      continue
    }
    if (!line.startsWith('---') && !line.startsWith('+++') && !line.startsWith('diff ') && !line.startsWith('index ') && !line.startsWith('\\ ')) {
      if (line.startsWith('-')) {
        out.push({ oldNo, newNo: undefined, type: 'del', text: line.slice(1) })
        if (oldNo !== undefined) oldNo++
      } else if (line.startsWith('+')) {
        out.push({ oldNo: undefined, newNo, type: 'add', text: line.slice(1) })
        if (newNo !== undefined) newNo++
      } else {
        out.push({ oldNo, newNo, type: 'ctx', text: line.slice(1) })
        if (oldNo !== undefined) oldNo++
        if (newNo !== undefined) newNo++
      }
    }
  }

  return out
}

/** One API file entry -> the FileChange the panel and DiffViewer already render. */
export function fileDiffToChange(info: FileDiffInfo): FileChange {
  return {
    path: info.file,
    status: badge(info.status),
    additions: info.additions,
    deletions: info.deletions,
    diff: parseUnifiedPatch(info.patch),
  }
}
