<script setup lang="ts">
import type { CaptureCurationResponse } from '../api/models';
import { curationTiming } from '../lib/workspace-interactions';
import { injectWorkspaceClock } from '../composables/useWorkspaceClock';
const props = defineProps<{ curation?: CaptureCurationResponse | null }>();
const now = injectWorkspaceClock();
const state = computed(() => curationTiming(props.curation, now.value));
</script>

<template>
    <div v-if="state.fraction != null" class="release-lifetime" :class="{ urgent: state.urgent }" :title="state.title">
        <progress
            class="sr-only"
            max="100"
            :value="Math.round(state.fraction * 100)"
            aria-label="Time remaining until release"
            :title="state.title"
        />
        <span aria-hidden="true" :style="{ transform: `scaleX(${state.fraction})` }" />
    </div>
</template>
