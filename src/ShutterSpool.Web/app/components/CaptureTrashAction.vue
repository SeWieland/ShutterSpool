<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration } from '../lib/session';
import type { RetentionEvidence } from '../lib/retention';
import { originalRemovalScope, retentionTiming } from '../lib/retention';
const props = defineProps<{ captureId: string; name: string; evidence?: RetentionEvidence | null; disabled?: boolean }>();
const emit = defineEmits<{ refresh: [id: string, moved: boolean] }>();
const busy = ref(false);
const feedback = ref('');
watch(() => props.captureId, () => feedback.value = '');
async function trash() {
    if (busy.value || props.disabled || props.evidence?.captureRevision == null) return;
    const scope = originalRemovalScope(props.evidence);
    if (!scope || retentionTiming(props.evidence, Date.now(), true).stale) { feedback.value = 'We could not check the originals recently. Refresh this photograph before moving it to Trash.'; emit('refresh', props.captureId, false); return; }
    if (!window.confirm(`Move this photograph (${props.name}) to Trash?\n\nOriginal variants: ${scope}.\n\nFavorite: ${props.evidence.isFavorite ? 'yes' : 'no'}.\n\nIts guaranteed recovery window starts once it is safely recorded in Trash. Trash still uses storage. These may be the only copies. Gallery access protections and holds that protect files from deletion still apply.`)) return;
    const access = accessGeneration(), id = props.captureId;
    busy.value = true;
    try {
        await api.management.captures.byId(id).trash.post({ expectedRevision: props.evidence.captureRevision });
        if (access === accessGeneration()) { if (id === props.captureId) feedback.value = 'Moved to Trash.'; emit('refresh', id, true); }
    } catch (failure) {
        const status = handleFailure(failure);
        if (access === accessGeneration()) { if (id === props.captureId) feedback.value = status === 409 ? 'This photograph changed or has protection in place. Refresh and review it before trying again.' : 'We could not confirm the move to Trash. Refresh to check what happened before trying again.'; emit('refresh', id, false); }
    } finally { busy.value = false; }
}
</script>

<template><div class="retention-action"><button :disabled="disabled || busy || evidence?.captureRevision == null" @click="trash"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 6h18M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M5 6l1 14a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1l1-14M10 10v7M14 10v7" /></svg>Move to Trash</button><p v-if="feedback" role="status">{{ feedback }}</p></div></template>
