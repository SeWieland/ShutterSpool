<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration } from '../lib/session';
import type { RetentionEvidence } from '../lib/retention';
const props = defineProps<{ captureId: string; evidence?: RetentionEvidence | null; disabled?: boolean; compact?: boolean }>();
const emit = defineEmits<{ refresh: [id: string]; feedback: [visible: boolean] }>();
const busy = ref(false);
const feedback = ref('');
const uncertain = ref(false);
watch(uncertain, visible => { if (props.compact) emit('feedback', visible); }, { flush: 'sync' });
onUnmounted(() => emit('feedback', false));
watch(() => props.evidence?.asOf, () => uncertain.value = false);
watch(() => props.captureId, () => { feedback.value = ''; busy.value = false; uncertain.value = false; });
async function toggle() {
    if (busy.value || uncertain.value || props.disabled || props.evidence?.captureRevision == null) return;
    const id = props.captureId, access = accessGeneration();
    busy.value = true; feedback.value = 'Saving Favorite…';
    try {
        await api.management.captures.byId(id).favorite.put({ expectedRevision: props.evidence.captureRevision, isFavorite: !props.evidence.isFavorite });
        if (access === accessGeneration() && id === props.captureId) { feedback.value = 'Favorite saved.'; emit('refresh', id); }
    } catch (failure) {
        const status = handleFailure(failure);
        if (access === accessGeneration() && id === props.captureId) {
            uncertain.value = true;
            feedback.value = status === 409 ? 'Favorite changed elsewhere. Refresh the heart before trying again.' : 'We could not confirm your Favorite change. Refresh the heart before trying again.';
            emit('refresh', id);
        }
    } finally { if (id === props.captureId) busy.value = false; }
}
</script>

<template>
    <div class="favorite-control" :class="{ compact }">
        <button class="favorite-button" :class="{ 'favorite-icon-button': compact }" :aria-pressed="evidence?.isFavorite === true" :aria-label="evidence?.isFavorite ? 'Remove Favorite' : 'Mark Favorite'" :aria-description="evidence?.isFavorite ? 'Regular cleanup rules apply immediately; automatic cleanup keeps the Trash recovery window.' : undefined" :title="evidence?.isFavorite ? 'Remove Favorite · regular cleanup rules apply immediately; automatic cleanup keeps the Trash recovery window.' : 'Mark Favorite'" :disabled="disabled || busy || uncertain || evidence?.captureRevision == null" :aria-busy="busy" @click.stop="toggle">
            <svg class="favorite-heart" viewBox="0 0 24 24" :fill="evidence?.isFavorite ? 'currentColor' : 'none'" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M20.8 4.6a5.5 5.5 0 0 0-7.8 0L12 5.7l-1.1-1.1a5.5 5.5 0 0 0-7.8 7.8L12 21l8.8-8.6a5.5 5.5 0 0 0 0-7.8Z" /></svg>
            <span v-if="!compact">{{ evidence?.isFavorite ? 'Favorite' : 'Mark Favorite' }}</span>
        </button>
        <div v-if="feedback" :class="{ 'favorite-feedback': compact && uncertain, 'sr-only': compact && !uncertain }"><p class="muted" role="status">{{ feedback }}</p><button v-if="uncertain" :disabled="disabled || busy" @click.stop="emit('refresh', captureId)">Refresh heart</button></div>
    </div>
</template>
