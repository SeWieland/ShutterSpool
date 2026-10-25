<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import type { CaptureDetailResponse } from '../api/models';
const props = defineProps<{ ids: string[]; online?: boolean; currentName?: string; released?: boolean; disabled?: boolean }>();
const emit = defineEmits<{ refresh: [ids: string[]]; busy: [busy: boolean]; completed: [ids: string[]] }>();
const target = ref<string[]>([]), feedback = ref('');
const busy = ref(false), checking = ref(false);
const reviewed = ref<CaptureDetailResponse[]>([]);
let sequence = 0;
const blocked = computed(() => props.disabled || !props.online || busy.value || !props.ids.length || props.ids.length > 100);
async function reconcile() {
    if (blocked.value) return;
    const recovering = checking.value;
    reviewed.value = [];
    const request = ++sequence, access = accessGeneration(); busy.value = true; emit('busy', true);
    try {
        const ids = [...props.ids], details: CaptureDetailResponse[] = [], missing: number[] = [];
        for (const [index, id] of ids.entries()) {
            try {
                const item = await api.management.captures.byId(id).get();
                if (String(item?.id) !== id || !Number.isSafeInteger(item?.curation?.revision)) throw new Error('Unavailable');
                details.push(item!);
            }
            catch (failure) { if (handleFailure(failure) !== 404) throw failure; missing.push(index + 1); }
            if (request !== sequence || access !== accessGeneration()) return;
        }
        if (missing.length) {
            checking.value = true;
            feedback.value = `Selected photograph ${missing.join(', ')} unavailable. No photographs moved. Remove unavailable photographs from the selection, then check again.`;
            emit('refresh', ids);
            return;
        }
        reviewed.value = details; checking.value = false;
        feedback.value = recovering ? `${details.length} selected photographs checked. Review current membership below before choosing Move.` : 'Selected photographs checked.';
        emit('refresh', [...props.ids]);
    } catch (failure) { handleFailure(failure); if (request === sequence && access === accessGeneration()) { checking.value = true; feedback.value = 'Unable to check every selected photograph. Retry the check before moving.'; } }
    finally { if (request === sequence) { busy.value = false; emit('busy', false); } }
}
async function move() {
    if (blocked.value || checking.value || !target.value[0]) return;
    const selection = props.ids.join(','), destination = target.value[0];
    if (!reviewed.value.length) await reconcile();
    if (checking.value || reviewed.value.length !== props.ids.length || selection !== props.ids.join(',') || !props.online || target.value[0] !== destination) return;
    const request = ++sequence, access = accessGeneration(), ids = [...props.ids]; busy.value = true; emit('busy', true);
    try {
        const result = await api.management.captures.collectionMoves.post({ targetCollectionId: target.value[0], items: reviewed.value.filter(item => ids.includes(String(item.id))).map(item => ({ captureId: String(item.id), expectedRevision: item.curation?.revision })) });
        if (request !== sequence || access !== accessGeneration()) return;
        const items = result?.items ?? [];
        const outcomes = items.filter(item => ids.includes(String(item.captureId)) && items.filter(other => other.captureId === item.captureId).length === 1 && ['applied', 'conflict', 'notFound', 'notMovable', 'targetMissing'].includes(item.status ?? ''));
        const committed = outcomes.filter(item => item.status === 'applied').map(item => String(item.captureId));
        const failed = outcomes.filter(item => item.status !== 'applied');
        const unknown = ids.length - outcomes.length;
        feedback.value = `${committed.length} moved · ${failed.length} failed · ${unknown} unknown.${failed.length ? ' ' + failed.map(item => `${reviewed.value.find(detail => detail.id === item.captureId)?.displayName ?? 'Photograph'}: ${item.status}`).join(' · ') : ''}`;
        checking.value = failed.length > 0 || unknown > 0; reviewed.value = [];
        emit('completed', committed); emit('refresh', ids);
    } catch (failure) {
        const status = handleFailure(failure); if (request !== sequence || access !== accessGeneration()) return;
        checking.value = true; reviewed.value = [];
        feedback.value = status === 409 ? 'Membership changed. Check selected photographs and review current values before retrying.' : 'Move outcome uncertain. Some photographs may have moved. Check selected photographs before retrying.';
    } finally { if (request === sequence) { busy.value = false; emit('busy', false); } }
}
watch(() => props.ids.join(','), () => { sequence++; reviewed.value = []; busy.value = false; emit('busy', false); }, { flush: 'sync' });
const stop = onAccessLost(() => { sequence++; reviewed.value = []; target.value = []; feedback.value = ''; });
onUnmounted(() => { sequence++; stop(); emit('busy', false); });
</script>

<template>
    <section class="collection-move" aria-label="Collection">
        <h3>Collection<span v-if="ids.length > 1"> - {{ ids.length }} selected</span></h3>
        <p v-if="currentName">Current Collection: <strong>{{ currentName }}</strong></p>
        <p v-if="ids.length > 100" class="notice">Select up to 100 photographs to move at once.</p>
        <p v-if="released" class="muted">This changes current membership. Collection at release and recorded deliveries stay unchanged.</p>
        <CollectionPicker v-model="target" :disabled="blocked || checking" label="Choose Collection" compact />
        <ul v-if="ids.length > 1 && reviewed.length" class="collection-list"><li v-for="item in reviewed" :key="String(item.id)">{{ item.displayName }} · {{ item.currentCollection?.path ?? item.currentCollection?.name ?? 'Unavailable' }}{{ item.curation?.releasedAt ? ' · released; deliveries stay in place' : '' }}</li></ul>
        <div class="actions"><button v-if="checking" type="button" :disabled="blocked" @click="reconcile()">Check outcomes before retry</button><button type="button" :disabled="blocked || checking || !target.length" @click="move()">{{ ids.length === 1 ? 'Move photograph' : `Move ${ids.length} photographs` }}</button></div>
        <p v-if="busy" role="status">Checking or moving selected photographs…</p>
        <p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
    </section>
</template>
