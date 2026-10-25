<script setup lang="ts">
import type { curationTiming } from '../lib/workspace-interactions';
defineProps<{ state: ReturnType<typeof curationTiming> }>();
</script>

<template>
    <div v-if="state.fraction != null" class="release-lifetime" :class="{ urgent: state.urgent }" role="progressbar" aria-label="Time remaining until release" :aria-valuenow="Math.round(state.fraction * 100)" :aria-valuemin="0" :aria-valuemax="100" :aria-valuetext="state.title" :title="state.title">
        <span :style="{ transform: `scaleX(${state.fraction})` }" />
    </div>
</template>
