<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration } from '../lib/session';
import type { CaptureDetailResponse } from '../api/models';
const props = defineProps<{ capture: CaptureDetailResponse; disabled?: boolean }>();
const emit = defineEmits<{ refresh: [id: string] }>();
const busy = ref(false);
const uncertain = ref(false);
const feedback = ref('');
const unmatched = computed(() => (props.capture.deliverySummary?.noRoute ?? 0) > 0 && props.capture.retention?.disposition === 'active' && props.capture.retention?.tier !== 'explicitNoDelivery');
const eligible = computed(() => props.capture.retention?.canRecordNoDeliveryIntent === true);
const reasonLabels: Record<string, string> = {
    'capture-not-active': 'This photograph is no longer active.', 'no-members': 'The full set of originals could not be checked.',
    'members-not-routed': 'Some originals have not been checked against delivery rules yet.', 'route-outcome-not-no-match': 'Some originals do not have a confirmed result of no matching delivery rule.',
    'matched-route-exists': 'A saved delivery obligation is still in place.', 'delivery-history-exists': 'Recorded delivery history prevents this disposition.',
    'already-recorded': 'Delivery is already marked as not needed.', 'privacy-erasure-pending': 'Private data removal is pending for this photograph.',
};
watch(() => props.capture.id, () => { feedback.value = ''; uncertain.value = false; });
watch(() => props.capture.retention?.asOf, () => uncertain.value = false);
async function record() {
    const id = String(props.capture.id), revision = props.capture.retention?.captureRevision;
    if (!eligible.value || busy.value || uncertain.value || props.disabled || revision == null) return;
    if (!window.confirm('Mark delivery as not needed for this photograph and every original variant? Each original must have been checked against your delivery rules, with no saved delivery obligation or history. This does not cancel work, confirm a delivered copy or repeat delivery. Regular cleanup rules still apply.')) return;
    const access = accessGeneration(); busy.value = true;
    try {
        const result = await api.management.captures.byId(id).noDeliveryIntent.post({ expectedRevision: revision });
        if (result?.status?.toLowerCase() !== 'committed') throw new Error('Disposition not confirmed');
        if (access === accessGeneration() && String(props.capture.id) === id) { feedback.value = 'Marked as not needing delivery. This does not confirm another copy exists.'; emit('refresh', id); }
    } catch (failure) {
        const status = handleFailure(failure);
        if (access === accessGeneration() && String(props.capture.id) === id) { uncertain.value = true; feedback.value = status === 409 ? 'This photograph changed or already has delivery obligations. Refresh to review them; this action cannot cancel work.' : 'We could not confirm this choice. Refresh to check what happened before trying again.'; emit('refresh', id); }
    } finally { busy.value = false; }
}
</script>

<template>
    <section v-if="eligible || unmatched || feedback" class="retention-action" aria-label="Delivery choice">
        <p v-if="eligible || unmatched">No delivery rule matched. This does not confirm another copy exists or cancel delivery obligations.</p>
        <p v-for="reason in !eligible ? capture.retention?.noDeliveryIntentReasons : []" :key="reason" class="muted">{{ reasonLabels[reason] ?? 'We could not check whether this choice is available. Refresh the delivery details.' }}</p>
        <button v-if="eligible" :disabled="disabled || busy || uncertain || capture.retention?.captureRevision == null" @click="record">Mark delivery not needed</button>
        <button v-if="uncertain" :disabled="disabled || busy" @click="emit('refresh', String(capture.id))">Refresh delivery choice</button>
        <p v-if="feedback" role="status">{{ feedback }}</p>
    </section>
</template>
