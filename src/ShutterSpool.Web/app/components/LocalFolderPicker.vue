<script setup lang="ts">
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
const props = defineProps<{ initialPath: string; online: boolean }>();
const emit = defineEmits<{ select: [path: string]; close: [] }>();
type Listing = NonNullable<Awaited<ReturnType<typeof api.management.destinations.localFolders.get>>>;
const dialog = ref<HTMLDialogElement>();
const location = ref<HTMLInputElement>();
const path = ref(props.initialPath);
const listing = ref<Listing>();
const busy = ref(false);
const error = ref('');
let sequence = 0;
let disposed = false;
let browseController: AbortController | undefined;
function cancelBrowse() { browseController?.abort(); browseController = undefined; }
async function browse(value = '') {
    if (!props.online || disposed) return;
    cancelBrowse();
    const request = ++sequence, access = accessGeneration(), controller = new AbortController();
    browseController = controller;
    busy.value = true; error.value = ''; listing.value = undefined;
    try {
        const result = await api.management.destinations.localFolders.get({ queryParameters: { path: value || undefined }, options: [cancellable(controller.signal)] });
        if (disposed || controller.signal.aborted || request !== sequence || access !== accessGeneration()) return;
        if (!result) throw new Error('Folder listing unavailable');
        listing.value = result; path.value = result.path ?? '';
        await nextTick();
        if (!disposed && !controller.signal.aborted && request === sequence && access === accessGeneration()) location.value?.focus();
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && request === sequence && access === accessGeneration()) {
            handleFailure(failure);
            const detail = (failure as { detail?: string })?.detail;
            error.value = detail || 'We could not open this folder. Check the path and server permissions, or choose another folder.';
        }
    } finally { if (browseController === controller) browseController = undefined; if (!disposed && request === sequence) busy.value = false; }
}
function choose() {
    if (!disposed && !busy.value && props.online && listing.value?.path) {
        dialog.value?.close();
        emit('select', listing.value.path);
    }
}
onMounted(() => { dialog.value?.showModal(); void browse(props.initialPath); });
onUnmounted(() => { disposed = true; sequence++; cancelBrowse(); });
const stopOnAccessLost = onAccessLost(() => { sequence++; cancelBrowse(); busy.value = false; listing.value = undefined; path.value = ''; error.value = ''; emit('close'); });
onUnmounted(stopOnAccessLost);
watch(() => props.online, online => { if (!online) { sequence++; cancelBrowse(); busy.value = false; } });
</script>

<template>
    <dialog ref="dialog" class="discard-dialog folder-browser" aria-labelledby="folder-browser-title" aria-describedby="folder-browser-help" @close="emit('close')">
        <h2 id="folder-browser-title">Choose a destination folder</h2>
        <p id="folder-browser-help" class="muted">Folders on the machine running ShutterSpool. Choose an existing folder where the server can write your photographs.</p>
        <form class="folder-location" @submit.prevent="browse(path)">
            <label class="field">Folder path<input ref="location" v-model="path" autofocus autocomplete="off" maxlength="2048" :disabled="busy || !online" /></label>
            <button :disabled="busy || !online">Go</button>
        </form>
        <div class="actions">
            <button type="button" :disabled="busy || !online" @click="browse()">Drives / roots</button>
            <button type="button" :disabled="busy || !online || !listing?.path" @click="browse(listing?.parentPath ?? '')">Up one level</button>
        </div>
        <p v-if="error" class="notice error" role="alert">{{ error }}</p>
        <div class="folder-list" :aria-busy="busy" aria-label="Folders">
            <p v-if="busy" role="status">Loading folders…</p>
            <p v-else-if="listing && !listing.directories?.length" class="muted">No subfolders here.</p>
            <button v-for="folder in listing?.directories" :key="folder.path ?? ''" type="button" :disabled="!online" @click="browse(folder.path ?? '')">
                <UIcon name="i-lucide-folder" aria-hidden="true" /><span>{{ folder.name }}</span><UIcon name="i-lucide-chevron-right" aria-hidden="true" />
            </button>
        </div>
        <p v-if="listing?.truncated" class="muted">Only the first 200 folders are shown. Enter a full path to open another folder.</p>
        <p v-if="!online" class="notice" role="status">Reconnect to browse or select a folder.</p>
        <p v-if="listing?.path" class="folder-selected">Selected folder: {{ listing.path }}</p>
        <div class="actions">
            <button type="button" @click="dialog?.close()">Cancel</button>
            <button type="button" class="primary" :disabled="busy || !online || !listing?.path" @click="choose">Use this folder</button>
        </div>
    </dialog>
</template>

<style scoped>
.folder-browser { width: min(40rem, calc(100vw - 2rem)); max-height: calc(100dvh - 2rem); overflow: auto; }
.folder-location { display: flex; align-items: end; gap: .75rem; margin-block: 1rem; }
.folder-location .field { flex: 1; min-width: 0; }
.folder-location input { width: 100%; }
.folder-list { min-height: 6rem; max-height: 35dvh; overflow: auto; margin-block: 1rem; }
.folder-list button { display: flex; align-items: center; gap: .75rem; width: 100%; min-height: 44px; text-align: left; margin-block: .25rem; }
.folder-list button span:not(.iconify) { flex: 1; overflow-wrap: anywhere; }
.folder-selected { overflow-wrap: anywhere; }
</style>
