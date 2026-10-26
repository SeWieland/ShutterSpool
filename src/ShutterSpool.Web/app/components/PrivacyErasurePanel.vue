<script setup lang="ts">
import { storageMessage, surfaceProgress, operationStatus } from '../lib/storage-presentation';
import { formatFileSize } from '../lib/workspace-interactions';
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { retentionTiming, type RetentionEvidence } from '../lib/retention';
import type { PrivacyErasureResponse, PrivacyErasurePreviewResponse, PrivacyErasureOperationListResponse, PrivacyErasurePreviewRequest } from '../api/models';
const props = defineProps<{ captureId?: string; displayName?: string; evidence?: RetentionEvidence | null; online: boolean; generation: number }>();
const emit = defineEmits<{ requested: [captureId: string] }>();
const preview = ref<PrivacyErasurePreviewResponse>();
const selection = ref<PrivacyErasurePreviewRequest>();
const operation = ref<PrivacyErasureResponse>();
const operations = ref<PrivacyErasureOperationListResponse>();
const operationId = ref('');
const busy = ref(false);
const feedback = ref('');
const stale = ref(false);
const confirmation = ref<HTMLDialogElement>();
const confirmationHeading = ref<HTMLElement>();
const confirmationTitle = useId();
const consent = ref(false);
const confirmationMode = ref<'erase' | 'continue'>('erase');
const reviewedOriginals = ref<NonNullable<RetentionEvidence['availableOriginals']>>([]);
const reviewedName = ref('Photograph');
const reviewedRemovedScope = ref(false);
let confirmationFocus: HTMLElement | undefined;
function clearConfirmation() {
    consent.value = false; preview.value = undefined; selection.value = undefined; reviewedOriginals.value = []; reviewedName.value = 'Photograph'; reviewedRemovedScope.value = false;
    confirmation.value?.close();
}
async function cancelConfirmation(event?: Event) {
    if (busy.value) { event?.preventDefault(); return; }
    sequence++; clearConfirmation(); await nextTick();
    if (confirmationFocus?.isConnected) confirmationFocus.focus({ preventScroll: true });
}
function openConfirmation(mode: 'erase' | 'continue') {
    confirmationMode.value = mode; consent.value = false;
    if (mode === 'continue') confirmationFocus = typeof document !== 'undefined' ? document.activeElement as HTMLElement : undefined;
    confirmation.value?.showModal();
    confirmationHeading.value?.focus();
}
function requestContinuation() {
    if (!operation.value || !['Pending', 'Blocked'].includes(operation.value.status ?? '') || busy.value || stale.value || !props.online) return;
    openConfirmation('continue');
}

const pendingOnly = ref(true);
const knownRemovedScope = computed(() => props.evidence?.disposition === 'purged' && !!props.evidence.availableOriginals?.length && props.evidence.availableOriginals.every(asset => asset.state === 'removed'));
let sequence = 0;
let disposed = false;
let readController: AbortController | undefined;
function cancelRead() { readController?.abort(); readController = undefined; }
function beginRead() { cancelRead(); const controller = new AbortController(); readController = controller; return controller; }
const canConfirm = computed(() => !!selection.value && ['Pending', 'Blocked'].includes(preview.value?.status ?? '') && (preview.value?.assetCount ?? 0) > 0);
watch(() => props.captureId, () => { sequence++; cancelRead(); clearConfirmation(); busy.value = false; preview.value = undefined; selection.value = undefined; operation.value = undefined; operationId.value = ''; feedback.value = ''; stale.value = false; });
watch(() => props.evidence?.captureRevision, () => { sequence++; cancelRead(); clearConfirmation(); busy.value = false; if (operationId.value) stale.value = true; });
async function previewSelection() {
    if (!props.captureId || busy.value || operationId.value || !props.online || props.evidence?.captureRevision == null) return;
    const originals = props.evidence.availableOriginals;
    if (!originals?.length || originals.some(asset => !asset.assetId) || retentionTiming(props.evidence, Date.now(), true).stale) { feedback.value = 'We could not check the full set of originals recently. Refresh this photograph before reviewing private data removal.'; return; }
    if (props.evidence.disposition === 'purged' && !knownRemovedScope.value) { feedback.value = 'The removal evidence is incomplete. Refresh this photograph; each original removal must be verified before its private data can be erased.'; return; }
    const request = ++sequence, access = accessGeneration(), controller = beginRead(); busy.value = true; feedback.value = '';
    confirmationFocus = typeof document !== 'undefined' ? document.activeElement as HTMLElement : undefined;
    const originalsSnapshot = originals.map(asset => ({ ...asset }));
    const nameSnapshot = props.displayName ?? 'Photograph';
    const removedSnapshot = knownRemovedScope.value;
    const scope = { selections: [{ captureId: props.captureId, expectedRevision: props.evidence.captureRevision, assetIds: originals.map(asset => String(asset.assetId)) }] };
    try {
        const result = await api.management.retention.erasures.preview.post(scope, { options: [cancellable(controller.signal)] });
        if (disposed || controller.signal.aborted || request !== sequence || access !== accessGeneration()) return;
        if (!result) throw new Error('Erasure preview unavailable');
        selection.value = scope; preview.value = result; reviewedOriginals.value = originalsSnapshot; reviewedName.value = nameSnapshot; reviewedRemovedScope.value = removedSnapshot;
        openConfirmation('erase');
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && request === sequence && access === accessGeneration()) { handleFailure(failure); selection.value = undefined; preview.value = undefined; feedback.value = 'We could not check what would be erased. Refresh this photograph and review the request again.'; }
    } finally { if (readController === controller) readController = undefined; if (request === sequence) busy.value = false; }
}
async function begin() {
    if (busy.value || operationId.value || !props.online || !canConfirm.value || !selection.value || !props.captureId || !consent.value || confirmationMode.value !== 'erase') return;
    const access = accessGeneration(), request = ++sequence, captureId = props.captureId;
    const reviewed = selection.value;
    operationId.value = crypto.randomUUID(); busy.value = true; feedback.value = '';
    try {
        const result = await api.management.retention.erasures.post({ operationId: operationId.value, confirmed: true, selections: reviewed.selections });
        if (disposed || access !== accessGeneration()) return;
        if (!result) throw new Error('Erasure outcome unconfirmed');
        if (request !== sequence) { emit('requested', captureId); return; }
        operation.value = result; clearConfirmation();
        feedback.value = 'Request saved. Check the progress below for each part; pending does not mean erased.';
        emit('requested', captureId);
    } catch (failure) {
        if (!disposed && request === sequence && access === accessGeneration()) { handleFailure(failure); clearConfirmation(); stale.value = true; feedback.value = 'We could not check whether the request saved. Check its status here or in Settings before making another request.'; }
    } finally { if (request === sequence) busy.value = false; }
}
async function readStatus(id = operationId.value) {
    if (!id || busy.value || !props.online) return;
    const request = ++sequence, access = accessGeneration(), controller = beginRead(); busy.value = true;
    try {
        const result = await api.management.retention.erasures.byOperationId(id).get({ options: [cancellable(controller.signal)] });
        if (disposed || controller.signal.aborted || request !== sequence || access !== accessGeneration()) return;
        if (!result) throw new Error('Status unavailable');
        operationId.value = id; operation.value = result; stale.value = false;
        if (props.captureId && ['Pending', 'Completed'].includes(result.status ?? '')) emit('requested', props.captureId);
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && request === sequence && access === accessGeneration()) { handleFailure(failure); stale.value = true; feedback.value = 'Progress could not be checked. The last result is still shown. Check again; the request was not repeated.'; }
    } finally { if (readController === controller) readController = undefined; if (request === sequence) busy.value = false; }
}
async function loadOperations(next = false) {
    if (busy.value || !props.online) return;
    if (next && !operations.value?.nextCursor) return;
    const request = ++sequence, access = accessGeneration(), controller = beginRead(); busy.value = true;
    try {
        const result = await api.management.retention.erasures.get({ queryParameters: { pageSize: 50, pendingOnly: pendingOnly.value, cursor: next ? operations.value?.nextCursor ?? undefined : undefined }, options: [cancellable(controller.signal)] });
        if (disposed || controller.signal.aborted || request !== sequence || access !== accessGeneration()) return;
        if (!result) throw new Error('Requests unavailable');
        operations.value = next ? { ...result, operations: [...new Map([...(operations.value?.operations ?? []), ...(result.operations ?? [])].map(item => [String(item.operationId), item])).values()] } : result; stale.value = false;
    } catch (failure) {
        if (!controller.signal.aborted && !disposed && request === sequence && access === accessGeneration()) { handleFailure(failure); stale.value = true; feedback.value = 'Privacy requests could not be loaded. Refresh to try again; missing progress does not mean removal finished.'; }
    } finally { if (readController === controller) readController = undefined; if (request === sequence) busy.value = false; }
}
async function continueOperation() {
    if (busy.value || stale.value || !props.online || !operationId.value || !consent.value || confirmationMode.value !== 'continue') return;
    const request = ++sequence, access = accessGeneration(); busy.value = true;
    try {
        const result = await api.management.retention.erasures.byOperationId(operationId.value).continueEscaped.post({ confirmed: true });
        if (!disposed && request === sequence && access === accessGeneration()) { if (!result) throw new Error('Continuation unconfirmed'); operation.value = result; clearConfirmation(); }
    } catch (failure) {
        if (!disposed && request === sequence && access === accessGeneration()) { handleFailure(failure); stale.value = true; feedback.value = 'We could not check whether removal continued. Check its status before trying again.'; }
    } finally { if (request === sequence) busy.value = false; }
}
onMounted(() => { if (!props.captureId) void loadOperations(); });
watch(pendingOnly, () => {
    if (!props.captureId) {
        if (readController) { sequence++; cancelRead(); busy.value = false; }
        void loadOperations();
    }
});
watch(() => props.generation, () => {
    if (readController) { sequence++; cancelRead(); busy.value = false; }
    if (operationId.value) void readStatus(); else if (!props.captureId) void loadOperations();
});
const stopOnAccessLost = onAccessLost(() => { sequence++; cancelRead(); clearConfirmation(); busy.value = false; operation.value = undefined; operations.value = undefined; operationId.value = ''; preview.value = undefined; selection.value = undefined; feedback.value = ''; });
onUnmounted(() => { disposed = true; sequence++; cancelRead(); clearConfirmation(); stopOnAccessLost(); });
</script>

<template>
    <details class="section-disclosure retention-details privacy-erasure">
        <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />{{ captureId ? 'Erase photograph and private data' : 'Privacy erasure requests' }}</summary>
        <div class="privacy-entry-copy"><p>Trash keeps recoverable originals and still uses storage. Permanent deletion removes local originals and previews; operational records may remain.</p><p>Privacy erasure also removes known temporary copies, redacts private metadata and history, and prevents stale work from recreating erased identities. It can revoke gallery promises; preservation holds still apply.</p><p class="muted">Backups and delivered or downloaded copies stay separately managed. Local completion does not promise secure overwrite or removal of every copy.</p></div>
        <p v-if="knownRemovedScope" class="notice">Originals are already recorded removed. Their sizes are historical; erase remaining private data without claiming available original bytes.</p>
        <button v-if="captureId" class="danger" :disabled="busy || !online || !!operationId" @click="previewSelection">{{ busy ? 'Checking removal scope…' : knownRemovedScope ? 'Erase remaining private data' : 'Erase photograph and private data' }}</button>
        <div v-else class="privacy-list-controls"><label class="settings-checkbox"><span><input v-model="pendingOnly" type="checkbox" :disabled="busy" />Unfinished requests only</span></label><button :disabled="busy || !online" @click="loadOperations(false)">Refresh requests</button></div>
        <p v-if="feedback" role="status" class="notice">{{ feedback }}</p>
        <p v-if="operations && !operations.operations?.length" class="muted">No {{ pendingOnly ? 'unfinished' : 'retained' }} privacy erasure requests in this page.</p>
        <article v-for="item in operations?.operations" :key="String(item.operationId)" class="privacy-request-item"><div class="privacy-request-heading"><strong>{{ operationStatus(item.status) }}</strong><span>{{ item.requestedAt?.toLocaleString() }}</span></div><p>{{ item.completedCount ?? 'Not confirmed' }} completed · {{ item.pendingCount ?? 'Not confirmed' }} pending of {{ item.assetCount ?? 'unknown' }} original identities.</p><p v-if="item.errorCode">{{ storageMessage(item.errorCode) }}</p><details class="section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details</summary><p>Request {{ item.operationId }}</p><p>{{ item.status }}<span v-if="item.errorCode"> · {{ item.errorCode }}</span></p></details><button :disabled="busy || !online" @click="readStatus(String(item.operationId))">Check removal progress</button></article>
        <button v-if="operations?.nextCursor" :disabled="busy || !online" @click="loadOperations(true)">Older requests</button>
        <section v-if="operation || operationId" class="privacy-progress" aria-label="Erasure surface progress">
            <h4>{{ stale ? 'Last known removal progress' : operationStatus(operation?.status) }}</h4><p v-if="stale" class="notice">Progress is unavailable or the submission is uncertain. Check status before making another request; the command has not been repeated.</p><p v-if="operation?.errorCode">{{ storageMessage(operation.errorCode) }}</p>
            <article v-for="(asset, index) in operation?.assets" :key="String(asset.assetId)" class="privacy-asset-progress"><h5>Original {{ index + 1 }}</h5><dl><div v-for="surface in ([{key:'metadataRedaction',label:'Private metadata'},{key:'derivatives',label:'Previews'},{key:'originalsAndTransients',label:'Originals and temporary copies'}] as const)" :key="surface.key" :class="`privacy-surface-${surfaceProgress(asset[surface.key]).tone}`"><dt>{{ surface.label }}</dt><dd><UIcon :name="surfaceProgress(asset[surface.key]).icon" />{{ surfaceProgress(asset[surface.key]).label }}</dd></div></dl><p v-if="asset.errorCode">{{ storageMessage(asset.errorCode) }}</p></article>
            <details class="section-disclosure privacy-diagnostics"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details</summary><p>Request {{ operationId }} · {{ operation?.status ?? 'Not confirmed' }}</p><p v-if="operation?.errorCode">{{ operation.errorCode }}</p><ul><li v-for="asset in operation?.assets" :key="String(asset.assetId)">{{ asset.captureId }} / {{ asset.assetId }} · {{ asset.metadataRedaction }} / {{ asset.derivatives }} / {{ asset.originalsAndTransients }}<span v-if="asset.errorCode"> · {{ asset.errorCode }}</span></li></ul></details>
            <div class="actions"><button :disabled="busy || !online" @click="readStatus()">Check status</button><button v-if="operation && ['Pending', 'Blocked'].includes(operation.status ?? '')" :disabled="busy || stale || !online" @click="requestContinuation">Continue removal</button></div>
        </section>
        <dialog ref="confirmation" class="privacy-confirmation" :aria-labelledby="confirmationTitle" @cancel="cancelConfirmation" @close="consent = false">
            <form @submit.prevent="confirmationMode === 'erase' ? begin() : continueOperation()">
                <h2 :id="confirmationTitle" ref="confirmationHeading" tabindex="-1" autofocus>{{ confirmationMode === 'continue' ? 'Continue privacy erasure' : reviewedRemovedScope ? 'Erase remaining private data' : 'Erase photograph and private data' }}</h2>
                <template v-if="confirmationMode === 'erase'">
                    <p class="privacy-selected-name">{{ reviewedName }}</p>
                    <p v-if="reviewedRemovedScope">These originals are recorded removed. This erases remaining private metadata and keeps their identities durably suppressed. No available original bytes are selected.</p>
                    <ul class="privacy-variant-list"><li v-for="asset in reviewedOriginals" :key="String(asset.assetId)"><strong>{{ asset.variant ?? 'Original' }}</strong><span>{{ asset.byteLength == null ? 'Size not confirmed' : `${asset.byteLength.toLocaleString('en')} ${asset.state === 'removed' ? 'historical bytes' : 'bytes'}` }}</span><span>{{ asset.state === 'removed' ? 'Recorded removed' : asset.state === 'available' ? 'Available locally' : asset.state === 'failedRemoval' ? 'Prior removal failed' : 'Availability needs checking' }}</span></li></ul>
                    <p>The exact reviewed originals, local previews, known temporary copies and private metadata are in scope. Operational history is redacted and stale work cannot recreate the erased identities.</p>
                    <dl class="capacity-values"><dt>Gallery promises revoked</dt><dd>{{ preview?.galleryPromiseCount ?? 'Not confirmed' }}</dd><dt>Preservation holds</dt><dd>{{ preview?.preservationHoldCount ?? 'Not confirmed' }}</dd><dt>Active deliveries</dt><dd>{{ preview?.activeDeliveryCount ?? 'Not confirmed' }}</dd><dt>Uncertain delivery results</dt><dd>{{ preview?.ambiguousDeliveryCount ?? 'Not confirmed' }}</dd></dl>
                    <p v-if="preview?.preservationHoldCount && preview.errorCode !== 'PRESERVATION_HOLD_ACTIVE'">Preservation holds can keep removal blocked. Their authority must explicitly resolve them; a review date does not end a hold.</p><p v-if="(preview?.activeDeliveryCount || preview?.ambiguousDeliveryCount) && preview?.errorCode !== 'DELIVERY_RECONCILIATION_REQUIRED'">Delivery must be reconciled before removal can finish.</p><p v-if="preview?.errorCode" class="notice">{{ storageMessage(preview.errorCode) }}</p>
                </template>
                <template v-else><p>This continues the remaining irreversible work of the saved request. Completed parts will not be repeated. Resolve the reasons below before continuing.</p><p v-if="operation?.errorCode">{{ storageMessage(operation.errorCode) }}</p></template>
                <p>Trash would retain recoverable originals and consume quota. This irreversible erasure can revoke gallery promises, but never bypasses preservation holds.</p><p class="muted">Backups and delivered or downloaded copies remain separately managed. Secure overwrite and removal of every external copy are not promised.</p>
                <details class="section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details</summary><p v-if="confirmationMode === 'continue'">Request {{ operationId }}</p><ul v-else><li v-for="item in selection?.selections" :key="String(item.captureId)">{{ item.captureId }} · revision {{ item.expectedRevision }} · {{ item.assetIds?.join(', ') }}</li></ul><p v-if="preview?.errorCode">{{ preview.errorCode }}</p></details>
                <label class="settings-checkbox privacy-consent"><span><input v-model="consent" type="checkbox" :disabled="busy" />I understand this irreversible removal and its scope.</span></label>
                <div class="privacy-confirmation-actions"><button type="button" :disabled="busy" @click="cancelConfirmation()">Cancel</button><button class="danger" :disabled="busy || !online || !consent || confirmationMode === 'erase' && !canConfirm">{{ busy ? 'Submitting request…' : confirmationMode === 'continue' ? 'Continue erasure' : 'Erase permanently' }}</button></div>
            </form>
        </dialog>
    </details>
</template>
