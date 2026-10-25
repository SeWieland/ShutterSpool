<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration } from '../lib/session';
import type { RetentionEvidence } from '../lib/retention';
const props = defineProps<{ captureId: string; evidence?: RetentionEvidence | null; disabled?: boolean }>();
const emit = defineEmits<{ refresh: [id: string] }>();
const busy = ref(false);
const feedback = ref('');
watch(() => props.captureId, () => feedback.value = '');
async function resume() {
    if (busy.value || props.disabled || props.evidence?.captureRevision == null || !props.evidence.isPaused || props.evidence.disposition !== 'active') return;
    const id = props.captureId, access = accessGeneration(); busy.value = true;
    try {
        await api.management.captures.byId(id).resume.post({ expectedRevision: props.evidence.captureRevision });
        if (access === accessGeneration() && id === props.captureId) { feedback.value = 'Delivery resumed. Confirmed deliveries are kept.'; emit('refresh', id); }
    } catch (failure) {
        const status = handleFailure(failure);
        if (access === accessGeneration() && id === props.captureId) { feedback.value = status === 409 ? 'This photograph changed while resuming. Refresh and review it before trying again.' : 'We could not confirm the resume. Refresh to check what happened before trying again.'; emit('refresh', id); }
    } finally { busy.value = false; }
}
</script>

<template><div v-if="evidence?.isPaused && evidence.disposition === 'active'" class="recovery-notice"><p>Delivery is paused after recovery.</p><button class="primary" title="Continue existing work and keep confirmed deliveries. Retrying delivery is a separate action." :disabled="busy || disabled || evidence.captureRevision == null" @click="resume">Resume delivery</button><p v-if="feedback" role="status">{{ feedback }}</p></div></template>
