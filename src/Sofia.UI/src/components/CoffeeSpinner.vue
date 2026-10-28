<script setup lang="ts">
import { computed } from 'vue'

/**
 * Loading indicator: a coffee mug with animated vapor. Inherits currentColor and sizes by `size`.
 * When too small for the vapor to read (< 1rem), it becomes a coffee bean that spins, pauses, repeats.
 */
const props = defineProps<{ size?: string; paused?: boolean; robot?: boolean }>()

const small = computed(() => {
  const m = /^([\d.]+)(rem|em|px)?$/.exec(props.size ?? '')
  if (!m) return false
  const px = parseFloat(m[1]) * (m[2] === 'px' ? 1 : 16)
  return px < 16
})
</script>

<template>
  <!-- Subagents: a little robot shaking in every direction, hard at work. -->
  <svg
    v-if="robot"
    class="robot"
    :class="{ paused }"
    :style="{ width: size ?? '1.125rem', height: size ?? '1.125rem' }"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <path d="M12 8V4H8" />
    <rect x="4" y="8" width="16" height="12" rx="2" />
    <path d="M2 14h2M20 14h2M15 13v2M9 13v2" />
  </svg>
  <svg
    v-else-if="small"
    class="bean"
    :class="{ paused }"
    :style="{ width: size, height: size }"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="2.2"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <ellipse cx="12" cy="12" rx="6.5" ry="9.5" transform="rotate(35 12 12)" />
    <path d="M9.5 5.5 C14 9 10 15 14.5 18.5" transform="rotate(35 12 12)" />
  </svg>
  <svg
    v-else
    class="coffee"
    :class="{ paused }"
    :style="{ width: size ?? '1.125rem', height: size ?? '1.125rem' }"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.75"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <path class="steam s1" d="M8 8 C6.5 6.5 9.5 5.5 8 4 C7.4 3.4 7.4 2.6 8 2" />
    <path class="steam s2" d="M12 8 C10.5 6.5 13.5 5.5 12 4 C11.4 3.4 11.4 2.6 12 2" />
    <path class="steam s3" d="M16 8 C14.5 6.5 17.5 5.5 16 4 C15.4 3.4 15.4 2.6 16 2" />
    <path d="M4 11 H18 V16 A5 5 0 0 1 13 21 H9 A5 5 0 0 1 4 16 Z" />
    <path d="M18 12.5 H19.5 A2.5 2.5 0 0 1 19.5 17.5 H17.6" />
  </svg>
</template>

<style scoped>
.coffee {
  flex: none;
  overflow: visible;
}

.robot {
  flex: none;
  animation: robot-shake 0.5s linear infinite;
}

.robot.paused {
  animation: none;
}

@keyframes robot-shake {
  0% { transform: translate(0, 0) rotate(0deg); }
  12% { transform: translate(-8%, 6%) rotate(-6deg); }
  25% { transform: translate(7%, -8%) rotate(5deg); }
  37% { transform: translate(-6%, -6%) rotate(-4deg); }
  50% { transform: translate(8%, 5%) rotate(7deg); }
  62% { transform: translate(-7%, 8%) rotate(-7deg); }
  75% { transform: translate(6%, -5%) rotate(4deg); }
  87% { transform: translate(-5%, 4%) rotate(-5deg); }
  100% { transform: translate(0, 0) rotate(0deg); }
}

.bean {
  flex: none;
  animation: bean-spin 1.8s ease-in-out infinite;
}

@keyframes bean-spin {
  0% {
    transform: rotate(0deg);
  }
  45%,
  100% {
    transform: rotate(360deg);
  }
}

/* Waiting for a busy model: everything stands still, vapor drawn at rest. */
.bean.paused {
  animation: none;
}

.paused .steam {
  animation: none;
  stroke-dasharray: none;
  opacity: 0.55;
}

.steam {
  stroke-dasharray: 6 10;
  stroke-width: 1.4;
  opacity: 0;
  animation: coffee-steam 2.2s ease-in-out infinite;
}

.s2 {
  animation-delay: 0.45s;
}

.s3 {
  animation-delay: 0.9s;
}

@keyframes coffee-steam {
  0% {
    stroke-dashoffset: 8;
    opacity: 0;
    transform: translate(0, 2px);
  }
  25% {
    opacity: 0.95;
  }
  70% {
    opacity: 0.6;
    transform: translate(1px, -1px);
  }
  100% {
    stroke-dashoffset: -8;
    opacity: 0;
    transform: translate(-1px, -3px);
  }
}

@media (prefers-reduced-motion: reduce) {
  .steam {
    animation-duration: 6s;
  }
}
</style>
