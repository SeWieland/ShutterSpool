<script setup lang="ts">
import { retentionTiming, type RetentionEvidence } from '../lib/retention';
import { injectWorkspaceClock } from '../composables/useWorkspaceClock';
const props = defineProps<{ evidence?: RetentionEvidence | null; details?: boolean; stale?: boolean }>();
const now = injectWorkspaceClock();
const timing = computed(() => retentionTiming(props.evidence, now.value, props.details ?? false, props.stale));
</script>

<template>
    <div v-if="timing.visible && timing.fraction != null" class="release-lifetime retention-lifetime" :class="[timing.tone, { stale: timing.stale }]" role="progressbar" :aria-label="evidence?.timeline?.meaning ?? 'Retention time remaining'" :aria-valuenow="Math.round(timing.fraction * 100)" :aria-valuemin="0" :aria-valuemax="100" :aria-valuetext="timing.title" :title="timing.title"><span :style="{ transform: `scaleX(${timing.fraction})` }" /></div>
</template>
