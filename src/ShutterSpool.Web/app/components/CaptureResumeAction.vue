<script setup lang="ts">
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import type { RetentionEvidence } from '../lib/retention';
const props = defineProps<{ captureId: string; evidence?: RetentionEvidence | null; disabled?: boolean }>();
const emit = defineEmits<{ refresh: [id: string] }>();
const busy = ref(false);
const feedback = ref('');
let sequence = 0, disposed = false;
let checkController: AbortController | undefined;
function cancelCheck() { checkController?.abort(); checkController = undefined; }
const needsReconcile = ref(false);
let awaitingEvidence = props.evidence, verifiedRevision: number | undefined;
watch(() => props.captureId, () => { sequence++; cancelCheck(); needsReconcile.value = false; verifiedRevision = undefined; awaitingEvidence = props.evidence; busy.value = false; feedback.value = ''; });
watch(() => props.evidence, evidence => { if (needsReconcile.value && evidence !== awaitingEvidence && evidence?.captureRevision === verifiedRevision) { needsReconcile.value = false; feedback.value = 'Current details refreshed. Review them before choosing Resume delivery again.'; } });
async function checkCurrent() {
    if (!needsReconcile.value || busy.value || props.disabled) return;
    cancelCheck();
    const id = props.captureId, access = accessGeneration(), request = ++sequence, controller = new AbortController();
    checkController = controller; busy.value = true;
    try {
        const current = await api.management.captures.byId(id).get({ options: [cancellable(controller.signal)] });
        if (!disposed && !controller.signal.aborted && request === sequence && access === accessGeneration()) {
            if (current?.retention?.captureRevision == null) throw new Error('Current retention details unavailable');
            verifiedRevision = current.retention.captureRevision; awaitingEvidence = props.evidence; feedback.value = 'Current details checked. Waiting for refreshed details before another action.'; emit('refresh', id);
        }
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && request === sequence && access === accessGeneration()) { handleFailure(failure); feedback.value = 'We could not check the current photograph. Refresh before choosing another action.'; }
    } finally { if (checkController === controller) checkController = undefined; if (!disposed && request === sequence && access === accessGeneration()) busy.value = false; }
}
async function resume() {
    if (busy.value || needsReconcile.value || props.disabled || props.evidence?.captureRevision == null || !props.evidence.isPaused || props.evidence.disposition !== 'active') return;
    const id = props.captureId, access = accessGeneration(), request = ++sequence; busy.value = true;
    try {
        await api.management.captures.byId(id).resume.post({ expectedRevision: props.evidence.captureRevision });
        if (!disposed && request === sequence && access === accessGeneration()) { feedback.value = 'Delivery resumed. Confirmed deliveries are kept.'; emit('refresh', id); }
    } catch (failure) {
        if (!disposed && request === sequence && access === accessGeneration()) { const status = handleFailure(failure); needsReconcile.value = true; awaitingEvidence = props.evidence; verifiedRevision = undefined; feedback.value = status === 409 ? 'This photograph changed while resuming. Check current details before trying again.' : 'We could not confirm the resume. Check current details before trying again.'; emit('refresh', id); }
    } finally { if (!disposed && request === sequence && access === accessGeneration()) busy.value = false; }
}
const stopOnAccessLost = onAccessLost(() => { sequence++; cancelCheck(); needsReconcile.value = false; verifiedRevision = undefined; busy.value = false; feedback.value = ''; });
onUnmounted(() => { disposed = true; sequence++; cancelCheck(); stopOnAccessLost(); });
</script>

<template><div v-if="evidence?.isPaused && evidence.disposition === 'active'" class="recovery-notice"><p>Delivery is paused after recovery.</p><button class="primary" title="Continue existing work and keep confirmed deliveries. Retrying delivery is a separate action." :disabled="busy || needsReconcile || disabled || evidence.captureRevision == null" @click="resume">Resume delivery</button><button v-if="needsReconcile" :disabled="busy || disabled" @click="checkCurrent">{{ busy ? 'Checking current details…' : 'Check current details' }}</button><p v-if="feedback" role="status">{{ feedback }}</p></div></template>
