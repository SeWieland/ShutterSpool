<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import type { CollectionResponse } from '../api/models';
const emit = defineEmits<{ close: [] }>();
const dialog = ref<HTMLDialogElement>();
const items = ref<CollectionResponse[]>([]);
const search = ref(''), name = ref(''), feedback = ref('');
const selected = ref<CollectionResponse>();
const cursor = ref<string | null>();
let loadedSearch = '';
const busy = ref(false), checking = ref(false), mutating = ref(false);
const unused = ref<CollectionResponse[]>([]), unusedReady = ref(false), unusedLimited = ref(false);
let sequence = 0;
let searchTimer: ReturnType<typeof setTimeout> | undefined;
let previousFocus: HTMLElement | null = null;
async function read(next = false, preserveDraft = false) {
    clearTimeout(searchTimer);
    next = next && loadedSearch === search.value;
    const request = ++sequence, access = accessGeneration(); busy.value = true;
    const review = checking.value, draft = name.value, owner = selected.value;
    const query = review && !owner ? [...draft.replace(/^\//, '')].slice(0, 64).join('') : search.value;
    try {
        const page = await api.management.collections.get({ queryParameters: { q: query || undefined, limit: 30, cursor: next ? cursor.value ?? undefined : undefined } });
        let current = owner;
        if (review && owner?.id) {
            try { current = await api.management.collections.byId(String(owner.id)).get() ?? undefined; }
            catch (failure) { if (handleFailure(failure) !== 404) throw failure; current = undefined; }
        }
        if (request !== sequence || access !== accessGeneration()) return;
        if (!page?.items) throw new Error('Unavailable');
        items.value = next ? [...items.value, ...page.items] : page.items; cursor.value = page.nextCursor;
        loadedSearch = query;
        if (review && !owner) search.value = query;
        checking.value = false;
        if (!preserveDraft || review) { selected.value = review ? current : undefined; name.value = review ? draft : ''; }
        feedback.value = review ? `Saved Collections checked. ${current ? `Current name: ${current.path ?? current.name}. Your entered name is retained.` : 'Review the results before deliberately creating another Collection.'}` : 'Saved Collections checked. Choose a Collection to review before another change.';
    } catch (failure) { handleFailure(failure); if (request === sequence && access === accessGeneration()) { checking.value = true; feedback.value = 'Unable to check saved Collections. Retry before changing them.'; } }
    finally { if (request === sequence) busy.value = false; }
}
function choose(item?: CollectionResponse) { selected.value = item; name.value = item?.path ?? item?.name ?? ''; feedback.value = ''; }
async function save(remove = false) {
    if (busy.value || checking.value) return;
    clearTimeout(searchTimer);
    const access = accessGeneration(), item = selected.value; busy.value = true; mutating.value = true; unusedReady.value = false;
    try {
        if (remove && item?.id) await api.management.collections.byId(String(item.id)).delete({ queryParameters: { expectedRevision: item.revision ?? undefined } });
        else if (item?.id) await api.management.collections.byId(String(item.id)).put({ expectedRevision: item.revision, name: name.value });
        else await api.management.collections.post({ name: name.value });
        if (access !== accessGeneration()) return;
        await read(); feedback.value = remove ? 'Collection deleted.' : 'Collection saved.';
    } catch (failure) {
        const status = handleFailure(failure); if (access !== accessGeneration()) return;
        const duplicate = status === 409 && !remove && (!item || (failure as { errorCode?: string }).errorCode === 'COLLECTION_NAME_CONFLICT');
        checking.value = status !== 400 && !duplicate;
        feedback.value = duplicate ? 'A Collection already uses this name. Enter a different name.' : status === 409 ? 'Collection changed or remains referenced by Sources or retained photographs. Check saved Collections and review its reference counts.' : status === 400 ? 'Name was not accepted. Check the name and its folder segments.' : 'Outcome uncertain. Check saved Collections before deliberately trying again.';
    } finally { busy.value = false; mutating.value = false; }
}
async function prepareClearUnused() {
    if (busy.value || checking.value) return;
    clearTimeout(searchTimer);
    const request = ++sequence, access = accessGeneration(); busy.value = true; mutating.value = true;
    unused.value = []; unusedReady.value = false; unusedLimited.value = false;
    try {
        let next: string | undefined;
        const cursors = new Set<string>();
        const candidates: CollectionResponse[] = [];
        do {
            const page = await api.management.collections.get({ queryParameters: { limit: Math.min(30, 100 - candidates.length), cursor: next } });
            if (request !== sequence || access !== accessGeneration()) return;
            if (!page?.items) throw new Error('Unavailable');
            candidates.push(...page.items.filter(item => !item.isBuiltIn && item.id && item.revision != null && item.sourceReferences === 0 && item.captureReferences === 0));
            next = page.nextCursor ?? undefined;
            if (next && (!page.items.length || cursors.has(next))) throw new Error('Search did not progress');
            if (next) cursors.add(next);
        } while (next && candidates.length < 100);
        unused.value = candidates; unusedReady.value = true; unusedLimited.value = !!next;
        feedback.value = candidates.length ? '' : 'No unused Collections found in the checked batch.';
    } catch (failure) {
        handleFailure(failure);
        if (request === sequence && access === accessGeneration()) feedback.value = 'Unable to check unused Collections. Try again.';
    } finally { if (request === sequence) { busy.value = false; mutating.value = false; } }
}
async function clearUnused() {
    if (busy.value || checking.value || !unusedReady.value || !unused.value.length) return;
    clearTimeout(searchTimer);
    const request = ++sequence, access = accessGeneration(); busy.value = true; mutating.value = true;
    const candidates = unused.value; unusedReady.value = false; unused.value = [];
    let deleted = 0, skipped = 0;
    try {
        for (const item of candidates) {
            try {
                await api.management.collections.byId(String(item.id)).delete({ queryParameters: { expectedRevision: item.revision ?? undefined } });
                deleted++;
            } catch (failure) {
                const status = handleFailure(failure);
                if (request !== sequence || access !== accessGeneration()) return;
                if (status === 409 || status === 404) skipped++;
                else {
                    checking.value = true;
                    feedback.value = `${deleted} deleted · ${skipped} skipped. Outcome uncertain; remaining Collections were not attempted. Check saved Collections before trying again.`;
                    return;
                }
            }
            if (request !== sequence || access !== accessGeneration()) return;
        }
        await read();
        feedback.value = `${deleted} deleted · ${skipped} skipped because they changed or are referenced. Originals and photographs stay in place.`;
    } finally { busy.value = false; mutating.value = false; }
}
watch(search, () => {
    clearTimeout(searchTimer);
    if (checking.value || mutating.value) return;
    sequence++; items.value = []; cursor.value = undefined; busy.value = false;
    searchTimer = setTimeout(() => void read(false, true), 200);
}, { flush: 'sync' });
const stop = onAccessLost(() => { clearTimeout(searchTimer); sequence++; emit('close'); });
onMounted(() => { previousFocus = document.activeElement as HTMLElement; dialog.value?.showModal(); void read(); });
onUnmounted(() => { clearTimeout(searchTimer); sequence++; stop(); previousFocus?.focus(); });
</script>

<template>
    <dialog ref="dialog" class="collection-manager" aria-labelledby="collections-title" @cancel.prevent="!busy && emit('close')">
        <div class="collection-heading"><h2 id="collections-title">Collections</h2><button type="button" :disabled="busy" @click="emit('close')">Close</button></div>
        <p>Organize photographs before or after release. Originals and recorded deliveries stay in place.</p>
        <div class="actions"><input v-model="search" type="search" aria-label="Search collections" placeholder="Search collections" maxlength="128" :disabled="mutating || checking" @keydown.enter.prevent="read(false, true)" /><button type="button" :disabled="busy || checking" @click="choose()">New Collection</button><button type="button" :disabled="busy || checking" @click="prepareClearUnused()">Clear unused</button></div>
        <div v-if="unusedReady && unused.length" class="notice">
            <p>Delete {{ unused.length }} unused Collections? Built-in Collections and those used by Sources or retained photographs are kept.{{ unusedLimited ? ' This batch contains the first 100 unused Collections; more may remain.' : '' }}</p>
            <ul class="collection-list"><li v-for="item in unused" :key="String(item.id)">{{ item.path ?? item.name }}</li></ul>
            <div class="actions"><button type="button" :disabled="busy" @click="clearUnused()">Delete {{ unused.length }} unused Collections</button><button type="button" :disabled="busy" @click="unusedReady = false">Cancel</button></div>
        </div>
        <p v-if="busy" role="status">Checking Collections…</p>
        <ul class="collection-list"><li v-for="item in items" :key="String(item.id)"><button type="button" :aria-pressed="selected?.id === item.id" :disabled="busy || checking" @click="choose(item)">{{ item.path ?? item.name }}</button><small>{{ item.isBuiltIn ? 'Built-in · fixed' : `${(item.sourceReferences ?? 0) > 1000 ? '1000+' : item.sourceReferences ?? 0} Sources · ${(item.captureReferences ?? 0) > 1000 ? '1000+' : item.captureReferences ?? 0} retained photographs` }}</small></li></ul>
        <button v-if="cursor" type="button" :disabled="busy" @click="read(true)">More Collections</button>
        <form @submit.prevent="save()">
            <label class="field">Name<input v-model="name" required maxlength="1025" placeholder="Berlin" :disabled="busy || checking || !!selected?.isBuiltIn" /></label>
            <p class="muted">Use Berlin, or Events/Berlin for nested delivery folders. Parent folders are not separate Collections.</p>
            <p v-if="selected && !selected.isBuiltIn" class="notice">Renaming changes future name/path rule matches. Existing literal rules are unchanged; reusing a deleted name may match them.</p>
            <div class="actions"><button type="submit" :disabled="busy || checking || !name || !!selected?.isBuiltIn">{{ selected ? 'Rename' : 'Create Collection' }}</button><button v-if="selected && !selected.isBuiltIn" type="button" :disabled="busy || checking" @click="save(true)">Delete Collection</button></div>
        </form>
        <p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
        <button v-if="checking" type="button" :disabled="busy" @click="read()">Check saved Collections</button>
        <p class="muted">Sources, including archived Sources, and retained photographs block deletion. Counts are advisory; deletion checks current references.</p>
    </dialog>
</template>
