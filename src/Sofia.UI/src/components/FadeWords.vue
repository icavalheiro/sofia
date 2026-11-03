<script setup lang="ts">
import { computed, ref, watch } from 'vue'

const props = defineProps<{
  text?: string
  /** When false, words are shown instantly (no fade). */
  streaming?: boolean
}>()

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

function countWords(value: string): number {
  return (value.match(/\S+/g) || []).length
}

// Words already present in the previous render: only words after this count fade in.
const previousWordCount = ref(0)

watch(
  () => props.text ?? '',
  (next, prev) => {
    if (prev && next.startsWith(prev)) {
      previousWordCount.value = countWords(prev)
    } else {
      previousWordCount.value = 0
    }
  },
  { flush: 'sync' },
)

const html = computed(() => {
  const text = props.text ?? ''
  if (!props.streaming) return escapeHtml(text)
  const threshold = previousWordCount.value
  const parts = text.split(/(\s+)/)
  let wordIndex = 0
  let out = ''
  for (const part of parts) {
    if (part === '') continue
    if (/^\s+$/.test(part)) {
      out += escapeHtml(part)
      continue
    }
    if (wordIndex >= threshold) {
      out += `<span class="llm-word-fade">${escapeHtml(part)}</span>`
    } else {
      out += escapeHtml(part)
    }
    wordIndex++
  }
  return out
})
</script>

<template>
  <span class="fade-words" v-html="html" />
</template>

<style scoped>
.fade-words {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.llm-word-fade {
  display: inline-block;
  animation: llm-word-fade 0.45s ease-out both;
}

@keyframes llm-word-fade {
  from {
    opacity: 0;
    transform: translateY(0.3em);
    filter: blur(2px);
  }
  to {
    opacity: 1;
    transform: none;
    filter: none;
  }
}
</style>
