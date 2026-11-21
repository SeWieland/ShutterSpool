<script setup lang="ts">
import { retentionTiming, type RetentionEvidence } from '../lib/retention';
import { injectWorkspaceClock } from '../composables/useWorkspaceClock';
const props = defineProps<{ evidence?: RetentionEvidence | null; details?: boolean; stale?: boolean }>();
const now = injectWorkspaceClock();
const timing = computed(() => retentionTiming(props.evidence, now.value, props.details ?? false, props.stale));
</script>

<template>
    <div v-if="timing.visible && timing.fraction != null" class="release-lifetime retention-lifetime" :class="[timing.tone, { stale: timing.stale }]" :title="timing.title">
        <progress
            class="sr-only"
            max="100"
            :value="Math.round(timing.fraction * 100)"
            :aria-label="evidence?.timeline?.meaning ?? 'Retention time remaining'"
            :title="timing.title"
        />
        <span aria-hidden="true" :style="{ transform: `scaleX(${timing.fraction})` }" />
    </div>
</template>
