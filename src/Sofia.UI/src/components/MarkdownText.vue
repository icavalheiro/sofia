<script setup lang="ts">
import { computed } from 'vue'
import { Marked, type Tokens } from 'marked'
import markedKatex from 'marked-katex-extension'
import 'katex/dist/katex.min.css'

const props = defineProps<{ source: string }>()

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

/** Only web/mail links and relative references are allowed (no javascript: etc.). */
function isSafeUrl(href: string): boolean {
  const value = href.trim()
  if (!value) return false
  const scheme = /^([a-z][a-z0-9+.-]*):/i.exec(value)
  return !scheme || ['http', 'https', 'mailto'].includes(scheme[1].toLowerCase())
}

// Model output is untrusted: raw HTML is shown as text and URLs are filtered.
const md = new Marked({
  gfm: true,
  breaks: true,
  renderer: {
    html({ text }: Tokens.HTML | Tokens.Tag): string {
      return escapeHtml(text)
    },
    link(this: { parser: { parseInline(tokens: Tokens.Generic[]): string } }, { href, title, tokens }: Tokens.Link): string {
      const inner = this.parser.parseInline(tokens)
      if (!isSafeUrl(href)) return inner
      const titleAttr = title ? ` title="${escapeHtml(title)}"` : ''
      return `<a href="${escapeHtml(href)}"${titleAttr} target="_blank" rel="noopener noreferrer">${inner}</a>`
    },
    image({ text }: Tokens.Image): string {
      return escapeHtml(text)
    },
  },
})

// Models often emit LaTeX ($\rightarrow$); invalid math falls back to plain text.
md.use(markedKatex({ throwOnError: false }))

const html = computed(() => md.parse(props.source ?? '', { async: false }) as string)
</script>

<template>
  <div class="md" v-html="html" />
</template>

<style scoped>
.md {
  overflow-wrap: anywhere;
}

.md > :deep(:first-child) {
  margin-top: 0;
}

.md > :deep(:last-child) {
  margin-bottom: 0;
}

.md :deep(p),
.md :deep(ul),
.md :deep(ol),
.md :deep(pre),
.md :deep(blockquote),
.md :deep(table) {
  margin: 0 0 0.7em;
}

.md :deep(h1),
.md :deep(h2),
.md :deep(h3),
.md :deep(h4),
.md :deep(h5),
.md :deep(h6) {
  margin: 1em 0 0.4em;
  line-height: 1.3;
  font-weight: 650;
}

.md :deep(h1) {
  font-size: 1.25em;
}

.md :deep(h2) {
  font-size: 1.15em;
}

.md :deep(h3),
.md :deep(h4),
.md :deep(h5),
.md :deep(h6) {
  font-size: 1.05em;
}

.md :deep(ul),
.md :deep(ol) {
  padding-left: 1.5em;
}

.md :deep(li) {
  margin: 0.15em 0;
}

.md :deep(li > p) {
  margin-bottom: 0.3em;
}

.md :deep(a) {
  color: var(--sofia-accent);
  text-decoration: underline;
  text-underline-offset: 2px;
}

.md :deep(code) {
  font-family: 'SFMono-Regular', Menlo, Consolas, 'Liberation Mono', monospace;
  font-size: 0.85em;
  padding: 0.1em 0.35em;
  border: 1px solid var(--sofia-border);
  border-radius: 5px;
  background: var(--sofia-surface-2);
}

.md :deep(pre) {
  padding: 0.65rem 0.8rem;
  border: 1px solid var(--sofia-border);
  border-radius: 8px;
  background: var(--sofia-bg);
  overflow-x: auto;
}

.md :deep(pre code) {
  padding: 0;
  border: 0;
  background: none;
  font-size: 0.8125rem;
  line-height: 1.55;
  overflow-wrap: normal;
  white-space: pre;
}

.md :deep(blockquote) {
  padding: 0.1em 0.9em;
  border-left: 3px solid var(--sofia-border-strong);
  color: var(--sofia-text-muted);
}

.md :deep(hr) {
  border: 0;
  border-top: 1px solid var(--sofia-border);
  margin: 1em 0;
}

.md :deep(table) {
  display: block;
  max-width: 100%;
  overflow-x: auto;
  border-collapse: collapse;
}

.md :deep(th),
.md :deep(td) {
  padding: 0.3rem 0.6rem;
  border: 1px solid var(--sofia-border);
}

.md :deep(th) {
  background: var(--sofia-surface-2);
  text-align: left;
}
</style>
