<script setup lang="ts">
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { deliveryPresentation } from '../lib/workspace-interactions';
import type { RoutingDeliveryResponse, RoutingAttemptPageResponse, RoutingActionResponse } from '../api/models';
const props = defineProps<{
    work: RoutingDeliveryResponse;
    online?: boolean;
    readOnly?: boolean;
    deliveryOnly?: boolean;
    variant?: string;
    recoverySequence?: number;
}>();
const emit = defineEmits<{
    refresh: [
    ];
}>();
const canRetry = computed(() => (props.deliveryOnly ? ['RetryWait', 'Failed'] : ['RetryWait', 'Failed', 'Disabled']).includes(props.work.state ?? ''));
const outcome = computed(() => deliveryPresentation(props.work.state));
const directActions = computed(() => props.deliveryOnly && (outcome.value.tone === 'attention' || canRetry.value));
const attempts = ref<RoutingAttemptPageResponse>();
type CommandRecoveryState = 'idle' | 'pending' | 'uncertain';
const commandState = ref<CommandRecoveryState>('idle');
const busy = computed(() => commandState.value === 'pending');
const attemptsBusy = ref(false);
const reevaluating = ref(false);
const uncertain = computed(() => commandState.value === 'uncertain');
const feedback = ref('');
const preview = ref<RoutingActionResponse>();
let sequence = 0;
let attemptsSequence = 0;
let attemptsController: AbortController | undefined;
function clearPrivateState() {
    sequence++;
    attemptsSequence++;
    attemptsController?.abort();
    attemptsController = undefined;
    attempts.value = undefined;
    preview.value = undefined;
    feedback.value = '';
    commandState.value = 'idle';
    attemptsBusy.value = false;
    reevaluating.value = false;
}
async function readAttempts(next = false) {
    const access = accessGeneration();
    const request = ++attemptsSequence;
    const workId = props.work.id;
    attemptsController?.abort();
    const controller = attemptsController = new AbortController();
    const isCurrent = () => request === attemptsSequence && workId === props.work.id && access === accessGeneration() && !controller.signal.aborted;
    attemptsBusy.value = true;
    try {
        const page = await api.management.deliveries.byId(String(workId)).attempts.get({ queryParameters: { limit: 30, cursor: next ? attempts.value?.nextCursor ?? undefined : undefined }, options: [cancellable(controller.signal)] });
        if (isCurrent())
            attempts.value = page;
    }
    catch (failure) {
        if (!isCurrent()) return;
        handleFailure(failure);
        feedback.value = 'Unable to read attempts. Reopen attempts to retry.';
    }
    finally {
        if (request === attemptsSequence) attemptsBusy.value = false;
        if (attemptsController === controller) attemptsController = undefined;
    }
}
type WorkAction = 'retry' | 'preview-resend' | 'resend';
function requestAction(workId: string, action: WorkAction) {
    const delivery = api.management.deliveries.byId(workId);
    switch (action) {
        case 'retry': return delivery.retry.post();
        case 'preview-resend': return delivery.resend.preview.post();
        case 'resend': return delivery.resend.post({ confirmationToken: preview.value?.confirmationToken });
    }
}
function actionFailed(failure: unknown) {
    const status = handleFailure(failure);
    preview.value = undefined;
    if (status === 400) {
        commandState.value = 'idle';
        feedback.value = 'Action rejected. Check the saved work details.';
        return;
    }
    commandState.value = 'uncertain';
    feedback.value = status === 409 ? 'State changed. Refresh and preview the action again.' : 'Outcome uncertain. Refresh work before issuing another action.';
    emit('refresh');
}
async function command(action: WorkAction) {
    if (commandState.value !== 'idle' || reevaluating.value || props.online !== true || props.readOnly === true)
        return;
    if (action === 'retry' ? !canRetry.value : props.deliveryOnly)
        return;
    const access = accessGeneration();
    const request = ++sequence;
    const workId = props.work.id;
    const assetId = props.work.assetId;
    const destinationId = props.work.destinationId;
    const isCurrent = () => request === sequence && workId === props.work.id
        && assetId === props.work.assetId && destinationId === props.work.destinationId
        && access === accessGeneration();
    commandState.value = 'pending';
    feedback.value = '';
    try {
        const result = await requestAction(String(workId), action);
        if (!isCurrent())
            return;
        commandState.value = 'idle';
        if (action === 'preview-resend') {
            if (result) preview.value = result;
            return;
        }
        if (action === 'resend')
            preview.value = undefined;
        if (result) {
            feedback.value = `${result.status}: ${result.explanation ?? 'Work updated; queued does not mean confirmed.'}`;
            emit('refresh');
        }
    }
    catch (failure) {
        if (!isCurrent()) return;
        actionFailed(failure);
    }
    finally {
        if (request === sequence && commandState.value === 'pending')
            commandState.value = 'idle';
    }
}
const stopOnAccessLost = onAccessLost(clearPrivateState);
watch([() => props.work.id, () => props.work.assetId, () => props.work.destinationId], clearPrivateState);
// Parent increments this only after an authoritative work reread succeeds.
watch(() => props.recoverySequence, (value, previous) => { if (value !== previous && commandState.value === 'uncertain') commandState.value = 'idle'; });
watch(() => props.readOnly, readOnly => {
    if (readOnly) {
        preview.value = undefined;
        feedback.value = '';
        reevaluating.value = false;
    }
});
watch(reevaluating, value => { if (value) preview.value = undefined; });
onUnmounted(() => { sequence++; attemptsSequence++; attemptsController?.abort(); attemptsController = undefined; stopOnAccessLost(); });
</script>

<template>
    <article class="delivery-work">
        <template v-if="deliveryOnly">
            <div class="delivery-outcome" :class="outcome.tone">
                <span class="variant-chip">{{ variant ?? 'Photo' }}</span>
                <UIcon :name="outcome.icon" /><strong>{{ outcome.label }}</strong>
                <small v-if="!work.required" class="muted">Optional</small>
            </div>
            <p v-if="work.state === 'RetryWait' && work.nextAttemptAt" class="muted">Next try: {{ new Date(work.nextAttemptAt).toLocaleString() }}</p>
            <div v-if="directActions && !readOnly" class="actions">
                <button v-if="canRetry" :disabled="busy || reevaluating || uncertain || online !== true" @click="command('retry')"><UIcon name="i-lucide-rotate-cw" />Retry now</button>
                <ReevaluateAction :asset-id="work.assetId" :destination-id="work.destinationId" :online="online" :disabled="busy || uncertain" @busy="reevaluating = $event" @refresh="emit('refresh')" />
            </div>
        </template>
        <component :is="deliveryOnly ? 'details' : 'div'" class="section-disclosure work-diagnostics">
            <summary v-if="deliveryOnly"><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details</summary>
        <div class="toolbar">
            <strong v-if="!deliveryOnly">
                {{ work.state }}
            </strong>
            <span class="delivery-path">
                {{ work.targetPath ?? 'No target' }}
            </span>
        </div>
        <p class="muted">
            {{ work.required ? 'Required' : 'Optional' }}
            ·
            {{ work.ruleName }}
        </p>
        <p>
            {{ work.explanation }}
        </p>
        <p class="muted">Recorded Collection: {{ work.collectionPath ?? 'Legacy work - Collection not recorded' }}. Retry and Resend keep the recorded target.</p>
        <p v-if="work.retryReason">
            Retry:
            {{ work.retryReason }}
            <span v-if="work.nextAttemptAt">
                ·
                {{ new Date(work.nextAttemptAt).toLocaleString() }}
            </span>
        </p>
        <p v-if="work.confirmationEvidence">
            Confirmed:
            {{ work.confirmationEvidence }}
        </p>
        <div v-if="!readOnly && !directActions" class="actions">
            <button v-if="canRetry" :disabled="busy || reevaluating || uncertain || online !== true" @click="command('retry')">
                Retry
            </button>
            <button v-if="!deliveryOnly" :disabled="busy || reevaluating || uncertain || online !== true" @click="command('preview-resend')">
                Preview Resend
            </button>
            <ReevaluateAction :asset-id="work.assetId" :destination-id="work.destinationId" :online="online" :disabled="busy || uncertain" @busy="reevaluating = $event" @refresh="emit('refresh')" />
        </div>
        <div v-if="preview && !readOnly" class="notice">
            <p>
                {{ preview.status }}
                ·
                {{ preview.explanation }}
            </p>
            <p>
                Previous target:
                {{ preview.previousTarget ?? 'None' }}
            </p>
            <p>
                Proposed target:
                {{ preview.proposedTarget ?? 'None' }}
            </p>
            <button :disabled="!preview.confirmationToken || busy || reevaluating || uncertain" @click="command('resend')">
                Confirm
                Resend
            </button>
            <button @click="preview = undefined">
                Cancel
            </button>
        </div>
        <div v-if="uncertain" class="notice" role="status">
            <p>Actions are paused until this work has been reread.</p>
            <button type="button" :disabled="busy || online !== true" @click="emit('refresh')">Refresh work</button>
        </div>
        <details class="section-disclosure" @toggle="($event.target as HTMLDetailsElement).open && readAttempts()">
            <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />
                Attempts
            </summary>
            <p v-if="!attempts && attemptsBusy">
                Loading attempts…
            </p>
            <div v-for="(attempt, i) in attempts?.items" :key="i" class="attempt">
                <p>
                    {{ attempt.result }}
                    ·
                    {{ attempt.errorMessage }}
                </p>
                <p>
                    {{ attempt.errorCode }}
                    ·
                    {{ attempt.confirmationEvidence }}
                </p>
            </div>
            <p v-if="attempts && !attempts.items?.length">
                No recorded attempts.
            </p>
            <button v-if="attempts?.nextCursor" :disabled="attemptsBusy" @click="readAttempts(true)">
                Older attempts
            </button>
        </details>
        </component>
        <p v-if="feedback" role="status" class="notice">{{ feedback }}</p>
    </article>
</template>
