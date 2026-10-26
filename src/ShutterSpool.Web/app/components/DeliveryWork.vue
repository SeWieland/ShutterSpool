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
async function command(action: 'retry' | 'resend', confirm = false) {
    if (commandState.value !== 'idle' || reevaluating.value || props.online !== true || props.readOnly === true || action === 'retry' && !canRetry.value || action === 'resend' && props.deliveryOnly)
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
        const delivery = api.management.deliveries.byId(String(workId));
        let result: RoutingActionResponse | undefined;
        let pendingPreview: RoutingActionResponse | undefined;
        if (action === 'retry')
            result = await delivery.retry.post();
        else if (confirm) {
            const body = { confirmationToken: preview.value?.confirmationToken };
            result = await delivery.resend.post(body);
        }
        else {
            pendingPreview = await delivery.resend.preview.post();
        }
        if (!isCurrent())
            return;
        commandState.value = 'idle';
        if (confirm)
            preview.value = undefined;
        else if (pendingPreview)
            preview.value = pendingPreview;
        if (result) {
            feedback.value = `${result.status}: ${result.explanation ?? 'Work updated; queued does not mean confirmed.'}`;
            emit('refresh');
        }
    }
    catch (failure) {
        if (!isCurrent()) return;
        const status = handleFailure(failure);
        commandState.value = status === 400 ? 'idle' : 'uncertain';
        preview.value = undefined;
        feedback.value = status === 409 ? 'State changed. Refresh and preview the action again.' : status === 400 ? 'Action rejected. Check the saved work details.' : 'Outcome uncertain. Refresh work before issuing another action.';
        if (status !== 400) emit('refresh');
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
            <button v-if="!deliveryOnly" :disabled="busy || reevaluating || uncertain || online !== true" @click="command('resend')">
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
            <button :disabled="!preview.confirmationToken || busy || reevaluating || uncertain" @click="command('resend', true)">
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
