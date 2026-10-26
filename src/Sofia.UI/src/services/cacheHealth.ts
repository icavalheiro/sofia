// ---------------------------------------------------------------------------
// Prompt-cache health for the transcript. Providers only report how many tokens
// of a request came from the cache (`usage.cacheRead` over `usage.input`), so a
// low ratio is turned into an on-message warning plus localised suspects read
// off the transcript itself — never a claim from the provider.
// ---------------------------------------------------------------------------

import type { CacheBreakInfo, Message, UsageDto } from '@/types'

/** Steps below this cache-hit percentage get a warning card (a session's first step is exempt). */
export const LOW_CACHE_THRESHOLD = 10

/** Prompts smaller than this carry no useful ratio (and are never real turns). */
const MIN_PROMPT_TOKENS = 200

/** A tool result bigger than this share of the prompt is worth calling out. */
const BIG_TOOL_OUTPUT_SHARE = 0.25

/** Cache hit percentage of one request, or null when nothing was reported. */
export function cacheHitPct(usage?: UsageDto): number | null {
  if (!usage || usage.input <= 0) return null
  return hitPercent(usage.cacheRead, usage.input)
}

/** A 100% hit is impossible: whole numbers below 99, then up to 3 decimals capped at 99.999. */
export function hitPercent(cached: number, input: number): number {
  const raw = (cached / input) * 100
  if (raw < 99) return Math.round(raw)
  return Math.min(99.999, Math.floor(raw * 1000) / 1000)
}

/**
 * One warning per assistant step, indexed like `messages`, or null where there is
 * nothing to warn about: no usage, a tiny prompt, the session's first step, a
 * provider that never reports cache reuse, or a healthy ratio. Single pass: the
 * transcript re-renders on every streamed token.
 */
export function annotateCacheBreaks(messages: Message[]): (CacheBreakInfo | null)[] {
  const out: (CacheBreakInfo | null)[] = Array.from({ length: messages.length }, () => null)

  // A provider that never reuses cache reports 0 everywhere; warning on every
  // step would be noise, so a session needs at least one hit to be worth judging.
  const reportsCache = messages.some((m) => (m.usage?.cacheRead ?? 0) > 0)
  if (!reportsCache) return out

  let previous: Message | undefined
  let compactedBefore = false

  for (let i = 0; i < messages.length; i++) {
    const message = messages[i]

    const pct = message.role === 'assistant' && !message.summary ? cacheHitPct(message.usage) : null
    const usage = message.usage
    if (pct !== null && usage && usage.input >= MIN_PROMPT_TOKENS && pct < LOW_CACHE_THRESHOLD && previous) {
      out[i] = { pct, cached: usage.cacheRead, input: usage.input, reasons: breakReasons(message, previous, compactedBefore) }
    }

    if (message.summary) compactedBefore = true
    if (message.role === 'assistant') previous = message
  }
  return out
}

/** Localised reason keys, most likely first. Never empty. */
function breakReasons(message: Message, previous: Message, compactedBefore: boolean): string[] {
  const reasons: string[] = []
  if ((message.model ?? '') !== (previous.model ?? '')) reasons.push('cache.reasonModel')
  if ((message.reasoningMode ?? '') !== (previous.reasoningMode ?? '')) reasons.push('cache.reasonReasoning')
  if (compactedBefore) reasons.push('cache.reasonCompaction')

  // The tool results of the step before this one are new prompt bytes: a big one
  // appended after the cached prefix re-bills everything that follows it.
  let appended = 0
  for (const tc of previous.toolCalls ?? []) appended += Math.ceil((tc.output ?? tc.error ?? '').length / 4)
  if (appended > message.usage!.input * BIG_TOOL_OUTPUT_SHARE) reasons.push('cache.reasonToolOutput')

  if ((message.attachments?.length ?? 0) > 0 || (previous.attachments?.length ?? 0) > 0) {
    reasons.push('cache.reasonAttachments')
  }
  reasons.push('cache.reasonPrefixChanged')
  return reasons
}

/**
 * The prompt the user can hand to the model (or another tool) to understand the
 * break. `{pct}`, `{cached}`, `{input}` and `{reasons}` are filled by the caller.
 */
export function cacheDiagnosisTemplate(): string {
  return [
    "Sofia's prompt cache only covered {pct}% of the request that produced this reply ({cached} of {input} tokens).",
    '',
    'Suspects Sofia detected in the transcript:',
    '{reasons}',
    '',
    'Explain which of these actually broke the cached prefix for this request, how prompt caching works with this',
    'provider, and what I should change (message order, attachments, tool usage, model or reasoning switches) so the',
    'cache stays warm in this session.',
  ].join('\n')
}
