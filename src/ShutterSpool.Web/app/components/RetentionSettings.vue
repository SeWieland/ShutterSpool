<script setup lang="ts">
import { settingsChanges, storageMessage, historyStatus, reviewEstimates, type SettingFormat } from '../lib/storage-presentation';
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { durationHours, durationFromHours } from '../lib/retention';
import { formatFileSize } from '../lib/workspace-interactions';
const emit = defineEmits<{ dirty: [value: boolean] }>();
const props = defineProps<{ online: boolean; generation: number }>();
type Settings = NonNullable<Awaited<ReturnType<typeof api.management.retention.settings.get>>>;
type Capacity = NonNullable<Awaited<ReturnType<typeof api.management.retention.capacity.get>>>;
type History = NonNullable<Awaited<ReturnType<typeof api.management.retention.history.get>>>;
type Impact = NonNullable<Awaited<ReturnType<typeof api.management.retention.settings.preview.post>>>;
const settings = ref<Settings>();
const capacity = ref<Capacity>();
const capacityQuotaActive = computed(() => Array.isArray(capacity.value?.blockers) ? !capacity.value.blockers.includes('quota-not-active') : undefined);
const history = ref<History>();
const impact = ref<Impact>();
const estimates = computed(() => reviewEstimates(impact.value));
const baseline = ref<unknown>();
const reviewDraft = ref<ReturnType<typeof body>>();
const stage = ref<'editing' | 'review'>('editing');
const reviewing = ref(false);
const reviewHeading = ref<HTMLElement>();
const reviewTrigger = ref<HTMLButtonElement>();
let reviewSequence = 0;
let editorScroll = 0;
let editorFocus: HTMLElement | undefined;
let historySequence = 0;

const quotaGiB = ref(0);
const highGiB = ref(0);
const recoveryGiB = ref(0);
const automatic = ref(false);
const quotaActive = ref(false);
const cacheMode = ref<'Disk' | 'Memory'>('Disk');
const memoryMiB = ref(64);
const trashHours = ref(0);
const restoreHours = ref(0);
const galleryDays = ref(0);
const lifetimeKeys = ['confirmed', 'noDelivery', 'waiting', 'heldOrUnfinished'] as const;
const lifetimes = reactive({ confirmed: { pressureHours: 0, ordinaryDays: 0 }, noDelivery: { pressureHours: 0, ordinaryDays: 0 }, waiting: { pressureHours: 0, ordinaryDays: 0 }, heldOrUnfinished: { pressureHours: 0, ordinaryDays: 0 } });
const lifetimeNames = { confirmed: 'All intended deliveries confirmed', noDelivery: 'Explicit no delivery intended', waiting: 'Waiting or unmatched', heldOrUnfinished: 'Held or unfinished delivery' };

const acknowledge = ref(false);
const busy = ref(false);
const refreshing = ref(false);
const stale = ref(false);
const policyChanged = ref(false);
const feedback = ref('');
const historyError = ref('');
let sequence = 0;
let disposed = false;
let loadController: AbortController | undefined;
let historyController: AbortController | undefined;
let reviewController: AbortController | undefined;
function cancelLoad() { loadController?.abort(); loadController = undefined; }
function cancelHistory() { historyController?.abort(); historyController = undefined; }
function cancelReview() { reviewController?.abort(); reviewController = undefined; }
function body() {
    const policy = { ...settings.value?.settings, automaticRemovalEnabled: automatic.value,
        trashRecoveryWindow: durationFromHours(trashHours.value), restoreProtection: durationFromHours(restoreHours.value), galleryMaximum: durationFromHours(galleryDays.value * 24),
    };
    for (const key of lifetimeKeys) policy[key] = { pressureAge: durationFromHours(lifetimes[key].pressureHours), ordinaryAge: durationFromHours(lifetimes[key].ordinaryDays * 24) };
    return { expectedSettingsRevision: settings.value?.settings?.revision, expectedQuotaRevision: settings.value?.quota?.revision,
        previewCache: { cacheMode: cacheMode.value, memoryCacheBytes: Math.round(memoryMiB.value * 1024 ** 2) }, settings: policy, quota: { ...settings.value?.quota, quotaBytes: Math.round(quotaGiB.value * 1024 ** 3), highWatermarkBytes: Math.round(highGiB.value * 1024 ** 3), recoveryTargetBytes: Math.round(recoveryGiB.value * 1024 ** 3), isActive: quotaActive.value } };
}
const validationErrors = computed(() => {
    const positive = (value: number) => Number.isFinite(value) && value > 0;
    return {
        quota: [quotaGiB.value, highGiB.value, recoveryGiB.value].every(positive) && recoveryGiB.value < highGiB.value && highGiB.value <= quotaGiB.value ? '' : 'Use a positive cleanup target below the cleanup starting point, and keep both within the storage limit.',
        automatic: automatic.value && !quotaActive.value ? 'Enable a storage limit before allowing automatic cleanup.' : '',
        trash: positive(trashHours.value) ? '' : 'Trash recovery must be greater than zero hours.',
        restore: positive(restoreHours.value) ? '' : 'Restore protection must be greater than zero hours.',
        gallery: positive(galleryDays.value) ? '' : 'Maximum gallery protection must be greater than zero days.',
        memory: Number.isFinite(memoryMiB.value) && memoryMiB.value >= 1 && memoryMiB.value <= 1024 ? '' : 'Memory budget must be between 1 and 1024 MiB.',
        lifetimes: Object.fromEntries(lifetimeKeys.map(key => [key, positive(lifetimes[key].pressureHours) && positive(lifetimes[key].ordinaryDays) && lifetimes[key].pressureHours <= lifetimes[key].ordinaryDays * 24 ? '' : 'Both cleanup ages must be greater than zero. Space-saving cleanup must be no later than regular cleanup.'])) as Record<typeof lifetimeKeys[number], string>,
    };
});
const valid = computed(() => !validationErrors.value.quota && !validationErrors.value.automatic && !validationErrors.value.trash && !validationErrors.value.restore && !validationErrors.value.gallery && !validationErrors.value.memory && lifetimeKeys.every(key => !validationErrors.value.lifetimes[key]));
const impactBlocked = computed(() => !impact.value || automatic.value && impact.value.complete !== true
    || (impact.value.impactAcknowledgementRequired === true || impact.value.quotaActivationAcknowledgementRequired === true || automatic.value && settings.value?.settings?.automaticRemovalEnabled !== true)
    && (!acknowledge.value || impact.value.complete !== true && !(impact.value.quotaActivationAcknowledgementRequired === true && !automatic.value)));
const changes = computed(() => valid.value && baseline.value ? settingsChanges(baseline.value, reviewDraft.value ?? body()) : []);
const effects = computed(() => [...new Set(changes.value.map(change => change.effect))]);
const hasChanges = computed(() => changes.value.length > 0);
function displaySetting(value: unknown, format: SettingFormat) {
    if (value === null || value === undefined) return 'Not set';
    if (format === 'boolean') return value ? 'On' : 'Off';
    if (format === 'bytes') return formatFileSize(Number(value));
    if (format === 'cache') return value === 'Memory' ? 'Memory only' : 'Disk';
    if (format === 'duration') {
        const hours = durationHours(String(value));
        return hours === undefined ? 'Not confirmed' : hours >= 24 && hours % 24 === 0 ? `${hours / 24} days` : `${hours} hours`;
    }
    return String(value);
}
function invalidateReview() {
    reviewSequence++; cancelReview(); reviewing.value = false; impact.value = undefined; reviewDraft.value = undefined; acknowledge.value = false; stage.value = 'editing';
}
async function backToEditing() {
    invalidateReview(); await nextTick();
    window.scrollTo?.(0, editorScroll);
    (editorFocus?.isConnected ? editorFocus : reviewTrigger.value)?.focus({ preventScroll: true });
}
function freezeDraft(value: ReturnType<typeof body>) {
    for (const item of Object.values(value)) if (item && typeof item === 'object') {
        for (const nested of Object.values(item)) if (nested && typeof nested === 'object') Object.freeze(nested);
        Object.freeze(item);
    }
    return Object.freeze(value);
}
watch([quotaGiB, highGiB, recoveryGiB, automatic, quotaActive, cacheMode, memoryMiB, trashHours, restoreHours, galleryDays, lifetimes], () => { invalidateReview(); emit('dirty', !valid.value || !baseline.value || hasChanges.value); }, { deep: true });
async function load(initial = false, clearFeedbackOnSuccess = true) {
    if (initial) invalidateReview();
    cancelLoad();
    const access = accessGeneration(), request = ++sequence, controller = new AbortController(); loadController = controller; busy.value = true; refreshing.value = true;
    try {
        const options = { options: [cancellable(controller.signal)] };
        const [settingsRead, capacityRead] = await Promise.allSettled([api.management.retention.settings.get(options), api.management.retention.capacity.get(options)]);
        if (disposed || controller.signal.aborted || access !== accessGeneration() || request !== sequence) return;
        if (settingsRead.status === 'rejected') handleFailure(settingsRead.reason);
        if (capacityRead.status === 'rejected') handleFailure(capacityRead.reason);
        const saved = settingsRead.status === 'fulfilled' ? settingsRead.value : undefined;
        const snapshot = capacityRead.status === 'fulfilled' ? capacityRead.value : undefined;
        if (snapshot) capacity.value = snapshot;
        if (saved) {
            policyChanged.value = !initial && !!settings.value && (saved.settings?.revision !== settings.value.settings?.revision || saved.quota?.revision !== settings.value.quota?.revision);
            if (policyChanged.value) { invalidateReview(); feedback.value = 'Settings changed elsewhere. Your edits are kept. Reload the saved settings before reviewing them.'; }
            if (initial || !settings.value) {
                settings.value = saved; cacheMode.value = saved.previewCache?.cacheMode === 'Memory' ? 'Memory' : 'Disk'; memoryMiB.value = (saved.previewCache?.memoryCacheBytes ?? 64 * 1024 ** 2) / 1024 ** 2; quotaGiB.value = (saved.quota?.quotaBytes ?? 0) / 1024 ** 3; highGiB.value = (saved.quota?.highWatermarkBytes ?? 0) / 1024 ** 3; recoveryGiB.value = (saved.quota?.recoveryTargetBytes ?? 0) / 1024 ** 3; quotaActive.value = saved.quota?.isActive === true; automatic.value = saved.settings?.automaticRemovalEnabled === true;
                trashHours.value = durationHours(saved.settings?.trashRecoveryWindow) ?? 0; restoreHours.value = durationHours(saved.settings?.restoreProtection) ?? 0; galleryDays.value = (durationHours(saved.settings?.galleryMaximum) ?? 0) / 24;
                for (const key of lifetimeKeys) { lifetimes[key].pressureHours = durationHours(saved.settings?.[key]?.pressureAge) ?? 0; lifetimes[key].ordinaryDays = (durationHours(saved.settings?.[key]?.ordinaryAge) ?? 0) / 24; }
                baseline.value = {
                    settings: { ...saved.settings, automaticRemovalEnabled: saved.settings?.automaticRemovalEnabled === true },
                    quota: { ...saved.quota, isActive: saved.quota?.isActive === true },
                    previewCache: { cacheMode: saved.previewCache?.cacheMode === 'Memory' ? 'Memory' : 'Disk', memoryCacheBytes: saved.previewCache?.memoryCacheBytes ?? 64 * 1024 ** 2 },
                };
                await nextTick(); if (!disposed && access === accessGeneration() && request === sequence) emit('dirty', false);
            }
        }
        stale.value = !saved || !snapshot;
        if (saved && snapshot && clearFeedbackOnSuccess && !policyChanged.value) feedback.value = '';
        if (!saved || !snapshot) feedback.value = saved && !snapshot
            ? 'We could not check storage. Your edits are kept. Refresh before saving.'
            : snapshot && !saved
                ? 'Capacity refreshed, but saved settings could not be checked. Refresh saved settings before saving.'
                : 'We could not check storage or saved settings. Your edits are kept. Refresh before saving.';
        await loadHistory();
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && access === accessGeneration() && request === sequence) { handleFailure(failure); stale.value = true; feedback.value = 'We could not check storage. Your edits are kept. Refresh before saving.'; }
    } finally { if (loadController === controller) loadController = undefined; if (request === sequence) { busy.value = false; refreshing.value = false; } }
}
async function loadHistory(next = false) {
    cancelHistory();
    const access = accessGeneration(), request = ++historySequence, controller = new AbortController(); historyController = controller; historyError.value = '';
    try {
        const result = await api.management.retention.history.get({ queryParameters: { pageSize: 30, cursor: next ? history.value?.nextCursor ?? undefined : undefined }, options: [cancellable(controller.signal)] });
        if (!controller.signal.aborted && access === accessGeneration() && !disposed && request === historySequence) { if (!result) throw new Error('History unavailable'); history.value = next ? { ...result, items: [...(history.value?.items ?? []), ...(result.items ?? [])] } : result; }
    } catch (failure) { if (!controller.signal.aborted && access === accessGeneration() && !disposed && request === historySequence) { handleFailure(failure); historyError.value = 'Cleanup history could not be loaded. Retry to check again.'; } }
    finally { if (historyController === controller) historyController = undefined; }
}
async function preview() {
    if (!valid.value || !hasChanges.value || busy.value || reviewing.value || stale.value || policyChanged.value || !props.online) return;
    cancelReview();
    const access = accessGeneration(), request = ++reviewSequence, controller = new AbortController(); reviewController = controller;
    const snapshot = freezeDraft(body());
    editorScroll = window.scrollY ?? 0;
    editorFocus = typeof document !== 'undefined' ? document.activeElement as HTMLElement : undefined;
    reviewDraft.value = snapshot; impact.value = undefined; acknowledge.value = false; reviewing.value = true; stage.value = 'review'; feedback.value = '';
    await nextTick(); reviewHeading.value?.focus();
    try {
        const result = await api.management.retention.settings.preview.post(snapshot, { options: [cancellable(controller.signal)] });
        if (disposed || controller.signal.aborted || access !== accessGeneration() || request !== reviewSequence) return;
        if (!result) throw new Error('Impact unavailable');
        impact.value = result;
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && access === accessGeneration() && request === reviewSequence) { handleFailure(failure); feedback.value = 'We could not review these changes. Return to editing and try again.'; }
    } finally { if (reviewController === controller) reviewController = undefined; if (request === reviewSequence) reviewing.value = false; }
}
async function save() {
    if (impactBlocked.value || !reviewDraft.value || !valid.value || busy.value || reviewing.value || stale.value || policyChanged.value || !props.online) return;
    const access = accessGeneration(), request = ++reviewSequence;
    const reviewed = reviewDraft.value;
    busy.value = true;
    try {
        const result = await api.management.retention.settings.put({ ...reviewed, acknowledgeImpact: acknowledge.value });
        if (disposed || access !== accessGeneration() || request !== reviewSequence) return;
        if (!result?.succeeded) throw new Error('Policy save not confirmed');
        feedback.value = 'Settings saved. Freed space appears after files have actually been removed.';
        await load(true, false);
    } catch (failure) {
        if (!disposed && access === accessGeneration() && request === reviewSequence) { const status = handleFailure(failure); invalidateReview(); stale.value = true; feedback.value = status === 409 ? 'Settings changed elsewhere. Reload saved settings, then review your changes again.' : 'We could not confirm the save. Refresh saved settings before trying again.'; }
    } finally { if (!disposed && access === accessGeneration()) busy.value = false; }
}
onMounted(() => void load(true));
watch(() => props.generation, () => { if (loadController || !busy.value && !reviewing.value) void load(); });
const stopOnAccessLost = onAccessLost(() => { sequence++; historySequence++; cancelLoad(); cancelHistory(); invalidateReview(); busy.value = false; refreshing.value = false; baseline.value = undefined; settings.value = undefined; capacity.value = undefined; history.value = undefined; impact.value = undefined; feedback.value = ''; });
onUnmounted(() => { disposed = true; sequence++; historySequence++; cancelLoad(); cancelHistory(); invalidateReview(); stopOnAccessLost(); });
</script>

<template>
    <section class="storage-settings storage-redesign" aria-label="Storage policy and capacity">
        <p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
        <template v-if="stage === 'editing'">
            <div v-if="!capacity && busy" class="capacity-skeleton" aria-busy="true" role="status">Checking storage capacity…</div>
            <StorageCapacity v-else :snapshot="capacity" :quota-active="capacityQuotaActive" :stale="stale || !online"><template #action><button :disabled="busy || !online" :aria-busy="refreshing" @click="load()"><UIcon name="i-lucide-refresh-cw" />{{ refreshing ? 'Refreshing…' : 'Refresh capacity' }}</button></template></StorageCapacity>
            <p v-if="!settings && !busy" class="notice">Saved settings are unavailable. Refresh capacity to check them; Workspace preferences remain available.</p>
            <form v-if="settings" class="storage-editor" @submit.prevent="preview">
                <section class="storage-policy-section" aria-labelledby="storage-limit-heading">
                    <div class="storage-section-top"><h3 id="storage-limit-heading">Storage limit and cleanup</h3><button type="button" :disabled="busy || !online" @click="load(true)">Reload saved settings</button></div>
                    <p class="muted">The storage limit controls new uploads. Automatic cleanup moves eligible photographs to recoverable Trash.</p>
                    <label class="settings-checkbox"><span><input v-model="quotaActive" type="checkbox" :disabled="busy" /> Use a storage limit for new photographs</span></label>
                    <div class="storage-field-grid">
                        <label>Storage limit (GiB)<input v-model.number="quotaGiB" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.quota" :aria-describedby="validationErrors.quota ? 'retention-quota-error' : undefined" /></label>
                        <label>Start cleanup above (GiB)<input v-model.number="highGiB" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.quota" :aria-describedby="validationErrors.quota ? 'retention-quota-error' : undefined" /></label>
                        <label>Cleanup target (GiB)<input v-model.number="recoveryGiB" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.quota" :aria-describedby="validationErrors.quota ? 'retention-quota-error' : undefined" /></label>
                    </div>
                    <p v-if="validationErrors.quota" id="retention-quota-error" class="field-error">{{ validationErrors.quota }}</p>
                    <label class="settings-checkbox"><span><input v-model="automatic" type="checkbox" :disabled="busy || !automatic && (!settings.quota?.isActive || capacity?.unknownAccounting !== false)" :aria-invalid="!!validationErrors.automatic" :aria-describedby="validationErrors.automatic ? 'retention-automatic-error' : 'retention-cleanup-hint'" /> Allow automatic cleanup</span></label>
                    <p id="retention-cleanup-hint" class="muted">{{ !settings.quota?.isActive ? 'Save a storage limit with cleanup off first. Enable cleanup after storage accounting is confirmed.' : capacity?.unknownAccounting !== false ? 'Cleanup can be enabled after storage usage is confirmed.' : 'Protected originals remain protected. Moving to Trash does not free space until physical removal finishes.' }}</p>
                    <p v-if="validationErrors.automatic" id="retention-automatic-error" class="field-error">{{ validationErrors.automatic }}</p>
                    <details class="section-disclosure settings-advanced"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Cleanup ages<span v-if="lifetimeKeys.some(key => !!validationErrors.lifetimes[key])" class="validation-cue"> · check values</span></summary>
                        <p class="muted">Space-saving cleanup applies under storage pressure; regular cleanup applies after the ordinary age. Existing photographs retain their schedules.</p>
                        <fieldset v-for="key in lifetimeKeys" :key="key" class="cleanup-age-group"><legend>{{ lifetimeNames[key] }}</legend><div class="storage-field-grid storage-two-fields"><label>Space-saving cleanup after (hours)<input v-model.number="lifetimes[key].pressureHours" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.lifetimes[key]" :aria-describedby="validationErrors.lifetimes[key] ? `retention-${key}-error` : undefined" /></label><label>Regular cleanup after (days)<input v-model.number="lifetimes[key].ordinaryDays" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.lifetimes[key]" :aria-describedby="validationErrors.lifetimes[key] ? `retention-${key}-error` : undefined" /></label></div><p v-if="validationErrors.lifetimes[key]" :id="`retention-${key}-error`" class="field-error">{{ validationErrors.lifetimes[key] }}</p></fieldset>
                    </details>
                </section>
                <section class="storage-policy-section" aria-labelledby="storage-recovery-heading">
                    <h3 id="storage-recovery-heading">Recovery and protection</h3><p class="muted">New durations apply to subsequent actions. Existing recovery and protection commitments stay unchanged.</p>
                    <div class="storage-field-grid">
                        <label>Trash recovery (hours)<input v-model.number="trashHours" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.trash" :aria-describedby="validationErrors.trash ? 'retention-trash-error' : undefined" /><span v-if="validationErrors.trash" id="retention-trash-error" class="field-error">{{ validationErrors.trash }}</span></label>
                        <label>Protection after Restore (hours)<input v-model.number="restoreHours" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.restore" :aria-describedby="validationErrors.restore ? 'retention-restore-error' : undefined" /><span v-if="validationErrors.restore" id="retention-restore-error" class="field-error">{{ validationErrors.restore }}</span></label>
                        <label>Maximum gallery protection (days)<input v-model.number="galleryDays" type="number" min="0" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.gallery" :aria-describedby="validationErrors.gallery ? 'retention-gallery-error' : undefined" /><span v-if="validationErrors.gallery" id="retention-gallery-error" class="field-error">{{ validationErrors.gallery }}</span></label>
                    </div>
                </section>
                <section class="storage-policy-section" aria-labelledby="storage-preview-heading">
                    <h3 id="storage-preview-heading">Preview storage</h3><p class="muted">Changes take effect after a server restart. Choosing Memory does not remove existing disk previews.</p>
                    <div class="storage-field-grid storage-two-fields"><label>Preview storage<select v-model="cacheMode" :disabled="busy"><option value="Disk">Disk</option><option value="Memory">Memory only</option></select></label><label>Memory budget (MiB, 1–1024)<input v-model.number="memoryMiB" type="number" min="1" max="1024" step="any" required :disabled="busy" :aria-invalid="!!validationErrors.memory" :aria-describedby="validationErrors.memory ? 'retention-memory-error' : undefined" /></label></div>
                    <p v-if="validationErrors.memory" id="retention-memory-error" class="field-error">{{ validationErrors.memory }}</p>
                    <p class="storage-running">Running: {{ settings.effectivePreviewCache?.cacheMode === 'Memory' ? 'Memory only' : settings.effectivePreviewCache?.cacheMode === 'Disk' ? 'Disk' : 'not confirmed' }} · {{ formatFileSize(settings.effectivePreviewCache?.memoryCacheBytes) }} memory budget.<span v-if="settings.restartRequired"> Saved changes need a restart.</span></p>
                    <p v-if="cacheMode === 'Memory'" class="muted">Memory previews can disappear on restart or when space is needed. System paging and crash dumps remain an operator consideration; previews never authorize deletion of originals.</p>
                </section>
                <div class="storage-editor-actions"><span class="muted">{{ !hasChanges && valid ? 'No changes to review.' : 'Shared settings · review before saving' }}</span><button ref="reviewTrigger" class="primary" :disabled="busy || stale || policyChanged || !online || !valid || !hasChanges">Review changes<UIcon name="i-lucide-arrow-right" /></button></div>
            </form>
            <div class="storage-secondary">
                <details class="section-disclosure settings-advanced cleanup-history"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Cleanup history</summary><p class="muted">Completed delivery attempts are retained for {{ settings?.settings?.completedAttemptHistoryDays ?? 'the configured number of' }} days; pressure history for {{ settings?.settings?.completedPressureHistoryDays ?? 'the configured number of' }} days.</p><p v-if="historyError" class="notice" role="alert">{{ historyError }} <button @click="loadHistory()">Retry history</button></p><p v-if="history?.hasGap" class="notice">Some history has expired or could not be loaded. Missing history does not mean nothing happened.</p><p v-if="history && !history.items?.length" class="muted">No saved cleanup events.</p><article v-for="event in history?.items" :key="event.eventId ?? undefined" class="cleanup-history-event"><h4>{{ historyStatus(event.kind) }}</h4><p>{{ event.committedAt?.toLocaleString() }} · {{ historyStatus(event.state) }}</p><dl class="capacity-values"><dt>Planned reclamation</dt><dd>{{ formatFileSize(event.plannedReclamationBytes) }}</dd><dt>Physically purged</dt><dd>{{ formatFileSize(event.purgedBytes) }}</dd></dl><p v-for="blocker in event.blockers" :key="blocker">{{ storageMessage(blocker) }}</p><details class="section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details</summary><p>{{ event.kind }} · {{ event.state }}</p><ul><li v-for="blocker in event.blockers" :key="blocker">{{ blocker }}</li></ul></details></article><button v-if="history?.nextCursor" :disabled="busy || !online" @click="loadHistory(true)">Older cleanup history</button></details>
                <PrivacyErasurePanel :online="online" :generation="generation" />
            </div>
        </template>
        <section v-else class="storage-review" aria-labelledby="storage-review-heading">
            <button class="storage-back" :disabled="busy" @click="backToEditing"><UIcon name="i-lucide-arrow-left" />Back to editing</button>
            <h3 id="storage-review-heading" ref="reviewHeading" tabindex="-1">Review storage changes</h3><p class="muted">Only the changes below will be saved. Current usage remains separate from your proposed limit.</p>
            <dl class="storage-change-list"><div v-for="change in changes" :key="change.path"><dt>{{ change.label }}</dt><dd><span class="change-before"><span class="sr-only">Previous: </span>{{ displaySetting(change.before, change.format) }}</span><UIcon name="i-lucide-arrow-right" /><strong><span class="sr-only">Proposed: </span>{{ displaySetting(change.after, change.format) }}</strong></dd></div></dl>
            <section class="storage-review-effects" aria-labelledby="storage-effects-heading"><h4 id="storage-effects-heading">What changes</h4><ul><li v-for="effect in effects" :key="effect">{{ effect }}</li></ul></section>
            <p v-if="reviewing" class="notice" role="status" aria-busy="true">Checking current usage, protection and cleanup eligibility…</p>
            <template v-if="impact">
                <section class="storage-review-estimates" aria-labelledby="storage-estimates-heading"><h4 id="storage-estimates-heading">Current impact estimate</h4><p v-if="impact.asOf" class="muted">Observed {{ impact.asOf.toLocaleString() }}</p>
                    <dl class="capacity-values"><dt>Eligible now</dt><dd>{{ estimates.eligibleCaptureCount == null ? 'Not yet known' : `${estimates.eligibleCaptureCount} photographs` }}</dd><dt>Planned movement to Trash</dt><dd>{{ estimates.plannedTrashCaptureCount == null ? 'Not yet known' : `${estimates.plannedTrashCaptureCount} photographs` }}</dd><dt>Planned reclamation</dt><dd>{{ estimates.plannedBytes == null ? 'Not yet known' : formatFileSize(estimates.plannedBytes) }} · physical removal still required</dd><dt>Current usage / reserved</dt><dd>{{ impact.accountingKnown ? `${formatFileSize(impact.usedBytes)} / ${formatFileSize(impact.reservedBytes)}` : 'Not confirmed' }}</dd><dt>Allowance after proposed limit</dt><dd>{{ impact.proposedQuotaHeadroomBytes == null ? reviewDraft?.quota.isActive ? 'Not confirmed' : 'No active limit proposed' : impact.proposedQuotaHeadroomBytes < 0 ? `${formatFileSize(Math.abs(impact.proposedQuotaHeadroomBytes))} above proposed limit` : formatFileSize(impact.proposedQuotaHeadroomBytes) }}</dd><dt>Protected usage</dt><dd>{{ formatFileSize(impact.protectedBytes) }} · overlapping usage, not additional space</dd></dl>
                    <p>Moving photographs to Trash does not immediately release space. Planned reclamation is an estimate, not space already freed.</p>
                    <p v-if="reviewDraft?.quota.isActive && impact.proposedQuotaHeadroomBytes != null && impact.proposedQuotaHeadroomBytes <= 0" class="notice">The proposed limit leaves no upload allowance after current usage and reservations. New uploads can pause after saving.</p>
                    <p v-if="impact.overQuota || impact.admissionBlocked" class="notice">The current saved configuration is already over quota or pausing uploads. The proposed-limit comparison above is separate.</p>
                    <p v-if="impact.complete !== true" class="notice">The estimate is incomplete. Unknown values are not zero; automatic cleanup cannot be enabled from an incomplete review.</p>
                    <ul class="storage-review-warnings"><li v-for="warning in (impact.warnings ?? []).filter(warning => warning !== 'existing-assets-retain-policy-revision')" :key="warning">{{ storageMessage(warning) }}</li></ul>
                    <details class="section-disclosure settings-advanced" v-if="impact.warnings?.length"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Review details</summary><ul><li v-for="warning in impact.warnings" :key="warning">{{ warning }}</li></ul></details>
                </section>
                <div class="storage-save-consequences">
                    <p v-if="impact.quotaActivationAcknowledgementRequired && impact.complete !== true && !automatic">Enabling a limit includes existing photographs. Some usage is unknown, so uploads may stay paused while accounting is checked. Automatic cleanup stays off.</p>
                    <label v-if="(impact.impactAcknowledgementRequired || impact.quotaActivationAcknowledgementRequired) && (impact.complete === true || impact.quotaActivationAcknowledgementRequired && !automatic)" class="settings-checkbox"><span><input v-model="acknowledge" type="checkbox" :disabled="busy" />I understand the effects on existing photographs and new uploads.</span></label>
                    <button class="primary" :disabled="busy || reviewing || stale || policyChanged || !valid || !online || impactBlocked" @click="save">{{ busy ? 'Saving settings…' : 'Save settings' }}</button>
                </div>
            </template>
        </section>
    </section>
</template>
