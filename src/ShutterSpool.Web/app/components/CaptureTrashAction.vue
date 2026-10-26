<script setup lang="ts">
import type { RetentionEvidence } from '../lib/retention';
import { originalRemovalScope, retentionTiming } from '../lib/retention';
const props = defineProps<{ captureId: string; name: string; evidence?: RetentionEvidence | null; disabled?: boolean; uncertain?: boolean; pending?: boolean; verifying?: boolean; verificationMessage?: string }>();
const emit = defineEmits<{ refresh: [id: string, moved: boolean]; submit: [id: string, expectedRevision: number]; verify: [id: string] }>();
const feedback = ref('');
watch(() => props.captureId, () => { feedback.value = ''; });
function verify() { if (props.uncertain && !props.pending && !props.verifying && !props.disabled) emit('verify', props.captureId); }
function trash() {
    if (props.pending || props.uncertain || props.disabled || props.evidence?.captureRevision == null) return;
    const scope = originalRemovalScope(props.evidence);
    if (!scope || retentionTiming(props.evidence, Date.now(), true).stale) { feedback.value = 'We could not check the originals recently. Refresh this photograph before moving it to Trash.'; emit('refresh', props.captureId, false); return; }
    if (!window.confirm(`Move this photograph (${props.name}) to Trash?\n\nOriginal variants: ${scope}.\n\nFavorite: ${props.evidence.isFavorite ? 'yes' : 'no'}.\n\nIts guaranteed recovery window starts once it is safely recorded in Trash. Trash still uses storage. These may be the only copies. Gallery access protections and holds that protect files from deletion still apply.`)) return;
    feedback.value = '';
    emit('submit', props.captureId, props.evidence.captureRevision);
}
</script>

<template><div class="retention-action"><button :disabled="disabled || pending || uncertain || evidence?.captureRevision == null" @click="trash"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 6h18M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2M5 6l1 14a1 1 0 0 0 1 1h10a1 1 0 0 0 1-1l1-14M10 10v7M14 10v7" /></svg>Move to Trash</button><button v-if="uncertain && !pending" :disabled="verifying || disabled" @click="verify">{{ verifying ? 'Verifying Trash outcome…' : 'Refresh and verify' }}</button><p v-if="pending || feedback || uncertain && verificationMessage" role="status">{{ pending ? 'Trash request pending. Other controls stay locked until it completes.' : feedback || verificationMessage }}</p></div></template>
