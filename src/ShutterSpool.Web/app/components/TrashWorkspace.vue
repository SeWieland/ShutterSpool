<script setup lang="ts">
import { durationHours, originalRemovalScope, retentionTiming } from '../lib/retention';
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
const props = defineProps<{ online: boolean; generation: number; gridMaximumDimension?: number }>();
const emit = defineEmits<{ refresh: []; erased: [id: string]; storageSettings: [] }>();
type TrashPage = NonNullable<Awaited<ReturnType<typeof api.management.trash.get>>>;
type TrashItem = NonNullable<TrashPage['items']>[number];
const items = ref<TrashItem[]>([]);
const cursor = ref<string | null>();
const purgeItems = computed(() => items.value.filter(item => item.retention?.disposition !== 'purged'));
const busy = ref(false);
const loaded = ref(false);
const stale = ref(false);
const feedback = ref('');
const now = ref(Date.now());
const restoreHours = ref<number>();
let clock: ReturnType<typeof setInterval> | undefined;
let sequence = 0;
let disposed = false;
let loadController: AbortController | undefined;
function cancelLoad() { loadController?.abort(); loadController = undefined; }
async function load(next = false) {
    cancelLoad();
    const request = ++sequence, access = accessGeneration(), controller = new AbortController();
    loadController = controller;
    busy.value = true;
    try {
        const options = { options: [cancellable(controller.signal)] };
        const [pageRead, policyRead] = await Promise.allSettled([api.management.trash.get({ queryParameters: { pageSize: 30, cursor: next ? cursor.value ?? undefined : undefined }, ...options }), api.management.retention.settings.get(options)]);
        if (pageRead.status === 'rejected') throw pageRead.reason;
        const page = pageRead.value;
        if (disposed || controller.signal.aborted || access !== accessGeneration() || request !== sequence) return;
        if (policyRead.status === 'fulfilled') restoreHours.value = durationHours(policyRead.value?.settings?.restoreProtection);
        else { handleFailure(policyRead.reason); restoreHours.value = undefined; }
        if (!page) throw new Error('Trash unavailable');
        items.value = next ? [...items.value, ...(page.items ?? [])] : page.items ?? [];
        cursor.value = page.nextCursor; loaded.value = true; stale.value = false;
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && access === accessGeneration() && request === sequence) { handleFailure(failure); stale.value = true; feedback.value = 'Trash could not be loaded. The last checked photographs are still shown. Refresh to try again.'; }
    } finally { if (loadController === controller) loadController = undefined; if (!disposed && access === accessGeneration() && request === sequence) busy.value = false; }
}
async function act(item: TrashItem, permanent: boolean) {
    if (busy.value || !props.online || stale.value || !item.captureId || item.captureRevision == null) return;
    if (permanent) {
        const scope = originalRemovalScope(item.retention);
        if (!scope || retentionTiming(item.retention, now.value, true).stale) { feedback.value = 'We could not check the originals recently. Refresh Trash before confirming permanent deletion.'; return; }
        if (!window.confirm(`Permanently delete this photograph (${item.displayName ?? 'Photograph'})?\n\nOriginal variants: ${scope}.\n\nFavorite: ${item.retention?.isFavorite ? 'yes' : 'no'}.\n\nYou cannot restore it afterward. If these are your only copies, they will be lost; other copies are unknown unless confirmed. Gallery access protections or holds that protect files from deletion can prevent removal. Backups and delivered or downloaded copies are separate and will not be removed by this action.`)) return;
    }
    const access = accessGeneration(); busy.value = true; feedback.value = '';
    try {
        const target = api.management.trash.byId(String(item.captureId));
        const result = permanent ? await target.purge.post({ expectedRevision: item.captureRevision, confirm: true }) : await target.restore.post({ expectedRevision: item.captureRevision });
        if (access === accessGeneration() && !disposed) { feedback.value = permanent ? 'Deletion requested. Check each original below to see whether removal has finished.' : `Photograph restored${result?.current?.protectedUntil ? ` · protected until ${result.current.protectedUntil.toLocaleString()}` : ''}. Delivery stays paused. Resume it separately when you are ready.`; await load(); emit('refresh'); }
    } catch (failure) {
        if (access === accessGeneration() && !disposed) { const status = handleFailure(failure); feedback.value = status === 409 ? 'This photograph changed or has protection in place. Review the refreshed details before trying again.' : 'We could not check whether the change saved. Review the refreshed details before trying again.'; await load(); }
    } finally { if (!disposed && access === accessGeneration()) busy.value = false; }
}
async function emptyLoaded() {
    if (busy.value || stale.value || !props.online) return;
    const scope = [...purgeItems.value];
    if (scope.some(item => !originalRemovalScope(item.retention) || retentionTiming(item.retention, now.value, true).stale)) { feedback.value = 'Some originals could not be checked recently. Refresh Trash before confirming deletion.'; return; }
    const variants = scope.map(item => `${item.displayName ?? 'Photograph'}: ${originalRemovalScope(item.retention)}`).join('\n');
    if (!scope.length || !window.confirm(`Permanently delete ${scope.length} loaded photographs from Trash, including ${scope.filter(item => item.retention?.isFavorite).length} Favorites?\n\nOriginal variants:\n${variants}\nYou cannot restore them afterward. These may be your only copies; other copies are unknown unless confirmed. Gallery access protections and holds that protect files from deletion can prevent removal. Backups and delivered or downloaded copies are separate. Older unloaded Trash items are excluded.`)) return;
    const access = accessGeneration(), outcomes: string[] = [];
    busy.value = true;
    try {
        for (const item of scope) {
            if (disposed || access !== accessGeneration()) return;
            try {
                if (!item.captureId || item.captureRevision == null) throw new Error('Revision unavailable');
                await api.management.trash.byId(String(item.captureId)).purge.post({ expectedRevision: item.captureRevision, confirm: true });
            } catch (failure) {
                if (disposed || access !== accessGeneration()) return;
                const status = handleFailure(failure);
                outcomes.push(`${item.displayName ?? 'Photograph'}: ${status === 409 ? 'changed or protected' : 'change not confirmed'}`);
                break;
            }
        }
        if (access === accessGeneration() && !disposed) {
            feedback.value = outcomes.join(' · ') || `Deletion requested for ${scope.length} loaded photographs. Check each original to see whether removal has finished.`;
            await load(); emit('refresh');
        }
    } finally { if (!disposed && access === accessGeneration()) busy.value = false; }
}
onMounted(() => { void load(); clock = setInterval(() => { if (!document.hidden) now.value = Date.now(); }, 1000); });
watch(() => props.generation, () => { if (loadController || !busy.value) void load(); });
const stopOnAccessLost = onAccessLost(() => { sequence++; cancelLoad(); busy.value = false; items.value = []; cursor.value = null; feedback.value = ''; });
onUnmounted(() => { disposed = true; sequence++; cancelLoad(); clearInterval(clock); stopOnAccessLost(); });
</script>

<template>
    <section aria-label="Trash recovery">
        <div class="toolbar"><h2>Trash</h2><span class="spacer" /><button @click="emit('storageSettings')">Storage settings</button><button :disabled="busy || !online" @click="load()">Refresh Trash</button></div>
        <p class="muted">Changed your mind? Restore a photograph before its recovery deadline. It stays protected from automatic cleanup for {{ restoreHours != null ? `${restoreHours} hours` : 'the configured window, which we could not check yet' }}. Delivery stays paused until you resume it. Trash still uses storage.</p>
        <p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
        <p v-if="stale || !online" class="notice" role="status">Showing the last check. Refresh when you are back online to update recovery deadlines.</p>
        <div v-if="!loaded && busy" class="empty" aria-busy="true">Loading photographs you can recover…</div>
        <div v-else-if="loaded && !items.length" class="empty">Trash is empty. Photographs moved here can be restored until their recovery deadline.</div>
        <article v-for="item in items" :key="String(item.captureId)" class="trash-row">
            <div class="trash-photo"><RetentionLifetime :evidence="item.retention" :now="now" details :stale="stale || !online" /><AuthorizedPreview :maximum-dimension="gridMaximumDimension" :capture-id="String(item.captureId)" profile="thumbnail" :name="item.displayName ?? 'Capture'" /></div>
            <div><h3>{{ item.displayName ?? 'Photograph' }}</h3><p v-if="item.recoverableUntil">Restore before {{ item.recoverableUntil.toLocaleString() }}</p><p v-if="item.recoverableUntil && item.recoverableUntil.getTime() <= now">The recovery window has ended. Refresh to check whether removal has finished.</p>
                <div class="actions"><button class="primary" :disabled="busy || stale || !online || restoreHours == null || item.retention?.disposition !== 'trash' || !item.recoverableUntil || item.recoverableUntil.getTime() <= now" @click="act(item, false)">Restore photograph</button></div>
                <RetentionDetails :evidence="item.retention" :now="now" :stale="stale || !online" />
                <details class="section-disclosure trash-removal"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Permanent removal</summary>
                    <button class="danger" :disabled="busy || stale || !online || item.retention?.disposition === 'purged'" @click="act(item, true)">Delete permanently</button>
                    <PrivacyErasurePanel :capture-id="String(item.captureId)" :display-name="item.displayName ?? 'Photograph'" :evidence="item.retention" :online="online" :generation="generation" @requested="items = items.filter(row => String(row.captureId) !== $event); emit('erased', $event)" />
                </details>
            </div>
        </article>
        <button v-if="cursor" :disabled="busy || !online" @click="load(true)">Older Trash items</button>
        <details v-if="purgeItems.length" class="section-disclosure trash-removal"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Empty Trash</summary><p>Only the {{ purgeItems.length }} loaded photographs are included. Older, unloaded items stay in Trash.</p><button class="danger" :disabled="busy || stale || !online || !purgeItems.length" @click="emptyLoaded">Permanently delete loaded photographs</button></details>
    </section>
</template>
