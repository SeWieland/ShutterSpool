<script setup lang="ts">
import type { CaptureCurationResponse } from '../api/models';
import { curationTiming } from '../lib/workspace-interactions';
import { injectWorkspaceClock } from '../composables/useWorkspaceClock';
const props = defineProps<{ curation?: CaptureCurationResponse | null }>();
const now = injectWorkspaceClock();
const state = computed(() => curationTiming(props.curation, now.value));
</script>

<template>
    <div v-if="state.fraction != null" class="release-lifetime" :class="{ urgent: state.urgent }" role="progressbar" aria-label="Time remaining until release" :aria-valuenow="Math.round(state.fraction * 100)" :aria-valuemin="0" :aria-valuemax="100" :aria-valuetext="state.title" :title="state.title">
        <span :style="{ transform: `scaleX(${state.fraction})` }" />
    </div>
</template>
