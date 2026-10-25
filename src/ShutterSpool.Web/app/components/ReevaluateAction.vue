<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import type { RoutingActionResponse } from '../api/models';
const props = defineProps<{ assetId?: string | null; destinationId?: string | null; online?: boolean; disabled?: boolean }>();
const emit = defineEmits<{ refresh: []; busy: [value: boolean] }>();
const busy = ref(false);
const preview = ref<RoutingActionResponse>();
const feedback = ref('');
let sequence = 0;
function clear() {
    sequence++;
    preview.value = undefined;
    feedback.value = '';
    busy.value = false;
    emit('busy', false);
}
async function command(confirm = false) {
    if (busy.value || props.disabled || props.online !== true || !props.assetId || !props.destinationId || confirm && !preview.value?.confirmationToken) return;
    const assetId = props.assetId, destinationId = props.destinationId;
    const request = ++sequence, access = accessGeneration();
    const current = () => request === sequence && access === accessGeneration() && assetId === props.assetId && destinationId === props.destinationId;
    busy.value = true;
    emit('busy', true);
    feedback.value = '';
    try {
        const endpoint = api.management.assets.byAssetId(assetId).destinations.byDestinationId(destinationId);
        const result = confirm ? await endpoint.reevaluate.post({ confirmationToken: preview.value?.confirmationToken }) : await endpoint.reevaluatePreview.get();
        if (!current()) return;
        if (confirm) {
            preview.value = undefined;
            feedback.value = `${result?.status ?? 'Outcome unavailable'}: ${result?.explanation ?? 'Refresh work to confirm the result.'}`;
            emit('refresh');
        } else preview.value = result;
    } catch (failure) {
        const status = handleFailure(failure);
        if (current()) {
            preview.value = undefined;
            feedback.value = status === 409 ? 'State changed. Refresh and preview Reevaluate again.'
                : confirm ? 'Outcome uncertain. Refresh work before issuing another action.'
                    : status === 404 ? 'This destination or original is unavailable. Check destination settings.' : 'Unable to preview Reevaluate. Try again when connected.';
            if (confirm || status === 409) emit('refresh');
        }
    } finally {
        if (request === sequence) { busy.value = false; emit('busy', false); }
    }
}
const stop = onAccessLost(clear);
watch(() => [props.assetId, props.destinationId], clear);
watch(() => props.disabled, disabled => { if (disabled && !busy.value) preview.value = undefined; });
onUnmounted(() => { sequence++; stop(); });
</script>

<template>
    <div class="reevaluate-action">
        <button :disabled="busy || disabled || online !== true || !assetId || !destinationId" @click="command()"><UIcon name="i-lucide-refresh-cw" />Reevaluate</button>
        <div v-if="preview" class="notice">
            <p>{{ preview.explanation }}</p>
            <p>Previous target: {{ preview.previousTarget ?? 'None' }}</p>
            <p>Proposed target: {{ preview.proposedTarget ?? 'None' }}</p>
            <div class="actions"><button :disabled="busy || disabled || online !== true || !preview.confirmationToken" @click="command(true)">Confirm Reevaluate</button><button :disabled="busy" @click="preview = undefined">Cancel</button></div>
        </div>
        <p v-if="feedback" role="status" class="notice">{{ feedback }}</p>
    </div>
</template>
