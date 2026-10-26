<script setup lang="ts">
// Skills tab: the globally configured skills Sofia.Server exposes (not the
// workspace's own) and their activation in the active session. The
// active list is the session's own `activeSkills`, kept live by
// `session.updated` events.
import { computed, ref, watch } from 'vue'

import { useHarness } from '@/composables/useHarness'
import { useSound } from '@/composables/useSound'
import { api } from '@/services/sofia'
import { useI18n } from '@/i18n'
import type { SessionDto, SkillDto } from '@/types'
import SofiaIcon from '@/components/SofiaIcon.vue'

const { state, activeConversation, configActivity } = useHarness()
const { playSound } = useSound()
const { t } = useI18n()

const skills = ref<SkillDto[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
/** Skill id being activated / deactivated right now. */
const activating = ref<string | null>(null)
/** Skill whose content is expanded. */
const expanded = ref<string | null>(null)

const activeIds = computed(() => activeConversation.value?.activeSkills ?? [])

async function refresh(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    // No `directory`: only the globally configured skills, never the workspace's.
    skills.value = (await api<SkillDto[]>('GET', '/api/skills')) ?? []
  } catch {
    skills.value = []
    error.value = t('skills.error')
  } finally {
    loading.value = false
  }
}

/** First line of the description, else of the skill body. */
function preview(skill: SkillDto): string {
  const description = (skill.description ?? '').split('\n')[0]
  if (description) return description
  const line = (skill.content ?? '').split('\n').map((l) => l.trim()).find((l) => l && !l.startsWith('#'))
  return line ?? ''
}

async function setActive(skill: SkillDto, active: boolean): Promise<void> {
  const conv = activeConversation.value
  if (!conv || activating.value) return
  activating.value = skill.id
  error.value = null
  try {
    const s = active
      ? await api<SessionDto>('POST', `/api/sessions/${conv.id}/skills`, { skillId: skill.id })
      : await api<SessionDto>('DELETE', `/api/sessions/${conv.id}/skills/${encodeURIComponent(skill.id)}`)
    if (s?.activeSkills) conv.activeSkills = [...s.activeSkills]
    playSound(active ? 'select' : 'click')
  } catch {
    error.value = t('skills.error')
  } finally {
    activating.value = null
  }
}

function activate(skill: SkillDto): void {
  void setActive(skill, true)
}

function deactivate(skill: SkillDto): void {
  void setActive(skill, false)
}

function isActive(skill: SkillDto): boolean {
  return activeIds.value.includes(skill.id)
}

function toggleContent(skill: SkillDto): void {
  expanded.value = expanded.value === skill.id ? null : skill.id
}

void refresh()
watch(
  () => state.activeConversationId,
  () => {
    expanded.value = null
  },
)
// This tab lists the skills of the harness home; re-read them when that folder changes on disk.
watch(
  () => configActivity.tick,
  () => {
    if (configActivity.scope === 'global') void refresh()
  },
)
</script>
<template>
  <div class="skills">
    <p class="sk-intro">{{ t('skills.intro') }}</p>

    <p v-if="loading && !skills.length" class="sk-intro">{{ t('skills.loading') }}</p>
    <p v-else-if="!skills.length" class="sk-intro">
      {{ t('skills.empty') }}
    </p>

    <p v-if="error" class="sk-error">{{ error }}</p>

    <div v-if="skills.length" class="skill-list">
      <div v-for="skill in skills" :key="skill.id" class="skill" :class="{ 'skill--active': isActive(skill) }">
        <div class="skill-head">
          <SofiaIcon class="skill-icon" name="sparkles" :size="'0.95rem'" />
          <div class="skill-text">
            <div class="skill-title">
              <span class="skill-name sofia-truncate">{{ skill.name }}</span>
              <span v-if="skill.source" class="skill-tag">{{ skill.source }}</span>
              <span v-if="isActive(skill)" class="skill-active">
                <SofiaIcon name="check" :size="'0.75rem'" />
                {{ t('skills.active') }}
              </span>
            </div>
            <div v-if="preview(skill)" class="skill-desc">{{ preview(skill) }}</div>
            <div v-if="skill.path" class="skill-path sofia-mono sofia-truncate">{{ skill.path }}</div>
          </div>
          <div class="skill-actions">
            <button class="sk-btn" type="button" @click="toggleContent(skill)">
              {{ expanded === skill.id ? t('skills.hide') : t('skills.view') }}
            </button>
            <button
              v-if="isActive(skill)"
              class="sk-btn"
              type="button"
              :disabled="activating === skill.id"
              @click="deactivate(skill)"
            >
              {{ activating === skill.id ? '…' : t('skills.deactivate') }}
            </button>
            <button
              v-else
              class="sk-btn sk-btn--primary"
              type="button"
              :disabled="activating === skill.id"
              @click="activate(skill)"
            >
              {{ activating === skill.id ? '…' : t('skills.activate') }}
            </button>
          </div>
        </div>
        <pre v-if="expanded === skill.id" class="skill-content">{{ skill.content }}</pre>
      </div>
    </div>

  </div>
</template>

<style scoped>
.skills {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}

.skill-list {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.skill {
  padding: 0.5rem 0.6rem;
  border: 1px solid var(--sofia-border);
  border-radius: 10px;
  background: var(--sofia-surface-2);
}

.skill--active {
  border-color: var(--sofia-primary);
  background: var(--sofia-primary-soft);
}

.skill-head {
  display: flex;
  align-items: flex-start;
  gap: 0.55rem;
}

.skill-icon {
  margin-top: 0.15rem;
  color: var(--sofia-primary);
  flex-shrink: 0;
}

.skill-text {
  flex: 1;
  min-width: 0;
}

.skill-title {
  display: flex;
  align-items: center;
  gap: 0.4rem;
  flex-wrap: wrap;
}

.skill-name {
  font-size: 0.8125rem;
  font-weight: 600;
  color: var(--sofia-text);
}

.skill-tag {
  padding: 0.05rem 0.35rem;
  border-radius: 999px;
  background: var(--sofia-surface-3);
  color: var(--sofia-text-faint);
  font-size: 0.625rem;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.skill-active {
  display: inline-flex;
  align-items: center;
  gap: 0.2rem;
  font-size: 0.6875rem;
  color: var(--sofia-primary);
}

.skill-desc {
  margin-top: 0.15rem;
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
  overflow-wrap: anywhere;
}

.skill-path {
  margin-top: 0.15rem;
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
}

.skill-actions {
  display: flex;
  align-items: center;
  gap: 0.25rem;
  flex-shrink: 0;
}

.skill-content {
  max-height: 180px;
  margin: 0.5rem 0 0;
  padding: 0.45rem 0.55rem;
  overflow: auto;
  border-radius: 8px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
  font-size: 0.6875rem;
  line-height: 1.5;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.skill-note {
  margin: 0;
  font-size: 0.6875rem;
  color: var(--sofia-text-faint);
}

.sk-intro {
  margin: 0;
  font-size: 0.75rem;
  color: var(--sofia-text-muted);
}

.sk-error {
  margin: 0;
  font-size: 0.75rem;
  color: var(--sofia-err);
}

.sk-btn {
  padding: 0.22rem 0.55rem;
  border: 1px solid var(--sofia-border-strong);
  border-radius: 7px;
  background: var(--sofia-surface);
  color: var(--sofia-text-muted);
  font-size: 0.6875rem;
  white-space: nowrap;
  cursor: pointer;
}

.sk-btn:not(:disabled):hover {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.sk-btn--primary {
  border-color: var(--sofia-primary);
  color: var(--sofia-primary);
}

.sk-btn--primary:not(:disabled):hover {
  background: var(--sofia-primary-soft);
}

.sk-btn:disabled {
  opacity: 0.5;
  cursor: default;
}
</style>
