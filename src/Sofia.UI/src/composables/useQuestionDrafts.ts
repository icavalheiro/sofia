import { reactive } from 'vue'

/** Picked options per question, keyed by form id. Lives outside the message
 *  (server updates replace message objects) and outside the component (so a
 *  half-filled form survives switching conversations). */
const drafts = reactive<Record<string, string[][]>>({})

export function useQuestionDrafts() {
  function draftFor(formId: string): string[][] {
    return (drafts[formId] ??= [])
  }

  function clearDraft(formId: string): void {
    delete drafts[formId]
  }

  return { draftFor, clearDraft }
}
