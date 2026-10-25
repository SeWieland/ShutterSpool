<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import type { CollectionResponse } from '../api/models';
const props = defineProps<{ modelValue: string[]; multiple?: boolean; disabled?: boolean; currentName?: string; label?: string; compact?: boolean }>();
const emit = defineEmits<{ 'update:modelValue': [ids: string[]] }>();
const items = ref<CollectionResponse[]>([]);
const selectedNames = ref<Record<string, string>>({});
const search = ref('');
const cursor = ref<string | null>();
let loadedSearch = '';
const loading = ref(false);
const error = ref('');
const manager = ref(false);
let sequence = 0;
let nameSequence = 0;
let active = true;
let searchTimer: ReturnType<typeof setTimeout> | undefined;
async function readSelected() {
    const request = ++nameSequence, access = accessGeneration();
    const names: Record<string, string> = {};
    for (const id of props.modelValue.slice(0, 30)) {
        try { const item = await api.management.collections.byId(id).get(); names[id] = item?.path ?? item?.name ?? 'Collection unavailable'; }
        catch (failure) { handleFailure(failure); names[id] = 'Collection unavailable'; }
        if (request !== nameSequence || access !== accessGeneration()) return;
    }
    selectedNames.value = names;
}
async function read(next = false) {
    clearTimeout(searchTimer);
    if (!search.value.trim()) return;
    next = next && loadedSearch === search.value;
    const request = ++sequence, access = accessGeneration();
    const query = search.value;
    loading.value = true; error.value = '';
    try {
        const page = await api.management.collections.get({ queryParameters: { q: query || undefined, limit: 8, cursor: next ? cursor.value ?? undefined : undefined } });
        if (request !== sequence || access !== accessGeneration()) return;
        if (!page?.items) throw new Error('Unavailable');
        items.value = page.items;
        cursor.value = page.nextCursor;
        loadedSearch = query;
    } catch (failure) { handleFailure(failure); if (request === sequence && access === accessGeneration()) error.value = 'Collections unavailable. Retry the search.'; }
    finally { if (request === sequence) loading.value = false; }
}
function choose(id: string) {
    const selected = props.modelValue.includes(id);
    if (props.multiple) emit('update:modelValue', selected ? props.modelValue.filter(value => value !== id) : [...props.modelValue, id].slice(0, 30));
    else emit('update:modelValue', id ? [id] : []);
}
watch(search, () => {
    clearTimeout(searchTimer); sequence++;
    items.value = []; cursor.value = undefined; error.value = ''; loading.value = active && !!search.value.trim();
    if (active && search.value.trim()) searchTimer = setTimeout(() => void read(), 200);
}, { flush: 'sync' });
watch(() => props.modelValue.join(','), () => void readSelected(), { immediate: true });
const stop = onAccessLost(() => { active = false; clearTimeout(searchTimer); sequence++; nameSequence++; selectedNames.value = {}; items.value = []; search.value = ''; });
onUnmounted(() => { active = false; clearTimeout(searchTimer); sequence++; nameSequence++; stop(); });
</script>

<template>
    <div class="collection-picker" :class="{ 'collection-picker-compact': compact }" :aria-busy="loading">
        <div v-if="!compact" class="collection-heading"><strong>{{ label ?? 'Collection' }}</strong><button type="button" :disabled="disabled" @click="manager = true">Manage</button></div>
        <p v-if="currentName" class="muted">{{ currentName }}</p>
        <div v-if="modelValue.length" class="collection-choices" aria-label="Selected Collections"><button v-for="id in modelValue" :key="id" type="button" :disabled="disabled" :aria-label="multiple ? `Remove ${selectedNames[id] ?? 'Collection'} filter` : `Selected Collection: ${selectedNames[id] ?? 'Loading'}`" @click="multiple && choose(id)">{{ selectedNames[id] ?? 'Loading Collection…' }}<span v-if="multiple"> · Remove</span></button></div>
        <div class="actions"><input v-model="search" type="search" aria-label="Search collections" placeholder="Search collections" maxlength="128" :disabled="disabled" @keydown.enter.prevent="read()" /><button v-if="compact" type="button" :disabled="disabled" @click="manager = true">Manage</button></div>
        <div v-if="search.trim()" class="collection-choices collection-suggestions" role="group" :aria-label="label ?? 'Collection'">
            <button v-if="multiple" type="button" :aria-pressed="!modelValue.length" :disabled="disabled" @click="emit('update:modelValue', [])">All Collections</button>
            <button v-for="item in items" :key="String(item.id)" type="button" :aria-pressed="modelValue.includes(String(item.id))" :disabled="disabled || multiple && modelValue.length >= 30 && !modelValue.includes(String(item.id))" @click="choose(String(item.id))">{{ item.path ?? item.name }}</button>
        </div>
        <p v-if="loading" role="status">Loading Collections…</p>
        <p v-else-if="search.trim() && !items.length && !error" class="muted">No matching Collections. Manage Collections to create one before shooting.</p>
        <p v-if="error" class="notice error" role="alert">{{ error }} <button type="button" @click="read()">Retry</button></p>
        <button v-if="cursor && search.trim()" type="button" :disabled="loading || disabled" @click="read(true)">More Collections</button>
        <CollectionManager v-if="manager" @close="manager = false; read()" />
    </div>
</template>
