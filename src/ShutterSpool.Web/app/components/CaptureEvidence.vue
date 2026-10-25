<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { deliveryPresentation, formatFileSize } from '../lib/workspace-interactions';
import type { CaptureDetailResponse, RoutingDecisionResponse, RoutingDeliveryResponse, RoutingDecisionPageResponse, RoutingDeliveryPageResponse, RoutingActionPageResponse } from '../api/models';
const props = defineProps<{
    capture: CaptureDetailResponse;
    generation: number;
    online?: boolean;
    readOnly?: boolean;
    focusState?: string;
    deliveryOnly?: boolean;
}>();
const emit = defineEmits<{ refresh: [captureId: string] }>();
const evidence = ref<HTMLElement>();
const decisions = ref<RoutingDecisionPageResponse>();
const deliveries = ref<RoutingDeliveryPageResponse>();
const actions = ref<RoutingActionPageResponse>();
const error = ref('');
let sequence = 0;
let owner = props.capture.id;
function clearEvidence() {
    sequence++;
    decisions.value = undefined;
    deliveries.value = undefined;
    actions.value = undefined;
    error.value = '';
}
async function read(kind?: 'decisions' | 'deliveries' | 'actions') {
    if (!props.capture.id)
        return;
    const captureId = props.capture.id;
    const request = ++sequence;
    const access = accessGeneration();
    const isCurrent = () => request === sequence && captureId === props.capture.id && access === accessGeneration();
    const endpoint = api.management.captures.byId(String(captureId));
    try {
        if (!kind) {
            const pages = await Promise.all([endpoint.routing.get({ queryParameters: { limit: 30 } }), endpoint.deliveries.get({ queryParameters: { limit: 30 } }), props.deliveryOnly ? undefined : endpoint.actions.get({ queryParameters: { limit: 30 } })]);
            if (isCurrent())
                [decisions.value, deliveries.value, actions.value] = pages;
        }
        else {
            const cursor = kind === 'decisions' ? decisions.value?.nextCursor : kind === 'deliveries' ? deliveries.value?.nextCursor : actions.value?.nextCursor;
            const config = { queryParameters: { limit: 30, cursor: cursor ?? undefined } };
            const page = kind === 'decisions' ? await endpoint.routing.get(config) : kind === 'deliveries' ? await endpoint.deliveries.get(config) : await endpoint.actions.get(config);
            if (!isCurrent())
                return;
            if (kind === 'decisions')
                decisions.value = page as RoutingDecisionPageResponse;
            else if (kind === 'deliveries')
                deliveries.value = page as RoutingDeliveryPageResponse;
            else
                actions.value = page as RoutingActionPageResponse;
        }
        if (isCurrent())
            error.value = '';
    }
    catch (failure) {
        handleFailure(failure);
        if (isCurrent())
            error.value = 'Unable to read delivery evidence. Refresh to retry.';
    }
}
const stopOnAccessLost = onAccessLost(clearEvidence);
function refreshWork() { emit('refresh', String(props.capture.id)); }
function canReevaluateDecision(decision: RoutingDecisionResponse) {
    return props.deliveryOnly && !props.readOnly && !!props.capture.curation?.releasedAt && decision.isCurrent
        && decision.destinationId && decision.destinationId !== '00000000-0000-0000-0000-000000000000'
        && !deliveries.value?.items?.some(work => work.assetId === decision.assetId && work.destinationId === decision.destinationId && work.state !== 'Superseded');
}
const destinationGroups = computed(() => {
    const groups = new Map<string, { id: string; name: string; decisions: RoutingDecisionResponse[]; work: RoutingDeliveryResponse[]; history: RoutingDeliveryResponse[] }>();
    function group(id?: string | null, name?: string | null) {
        const key = id ?? '';
        if (!groups.has(key)) groups.set(key, { id: key, name: name || 'Destination unavailable', decisions: [], work: [], history: [] });
        const value = groups.get(key)!;
        if (name) value.name = name;
        return value;
    }
    for (const decision of decisions.value?.items ?? []) {
        if (!decision.isCurrent) continue;
        const target = group(decision.destinationId, decision.destinationName);
        if (decision.kind !== 'Matched' || !deliveries.value?.items?.some(work => work.assetId === decision.assetId && work.destinationId === decision.destinationId && work.state !== 'Superseded'))
            target.decisions.push(decision);
    }
    for (const work of deliveries.value?.items ?? []) {
        const target = group(work.destinationId, work.destinationName);
        (work.state === 'Superseded' ? target.history : target.work).push(work);
    }
    for (const target of groups.values()) target.work.sort((a, b) => Number(deliveryPresentation(b.state).tone === 'attention') - Number(deliveryPresentation(a.state).tone === 'attention'));
    return [...groups.values()].sort((a, b) => Number(b.decisions.length > 0 || b.work.some(work => deliveryPresentation(work.state).tone === 'attention')) - Number(a.decisions.length > 0 || a.work.some(work => deliveryPresentation(work.state).tone === 'attention')));
});
function variant(assetId?: string | null) { return props.capture.assets?.find(asset => asset.id === assetId)?.variant ?? 'Photo'; }
watch(() => [deliveries.value, props.focusState], async () => {
    if (!props.focusState || !deliveries.value) return;
    await nextTick();
    const entries = [...(evidence.value?.querySelectorAll<HTMLElement>('[data-work-state]') ?? [])];
    const work = entries.find(entry => props.focusState === 'Attention'
        ? ['RetryWait', 'Failed', 'Ambiguous', 'Disabled'].includes(entry.dataset.workState ?? '')
        : entry.dataset.workState === props.focusState) ?? entries[0];
    const section = work?.closest<HTMLDetailsElement>('.routing-evidence');
    if (section) section.open = true;
    const asset = work?.closest<HTMLDetailsElement>('.delivery-asset');
    if (asset) asset.open = true;
    work?.focus();
    work?.scrollIntoView({ block: 'nearest' });
});
watch(() => props.capture.id, captureId => {
    if (captureId !== owner) {
        owner = captureId;
        clearEvidence();
    }
    void read();
}, { immediate: true });
watch(() => props.generation, () => void read());
onUnmounted(() => { sequence++; stopOnAccessLost(); });
</script>

<template>
    <section ref="evidence" class="capture-facts">
    <p v-if="error" role="alert" class="notice">
        {{ error }}
        <button @click="read()">
            Refresh evidence
        </button>
    </p>
        <template v-if="deliveryOnly">
            <p v-if="!capture.curation?.releasedAt" class="muted">Release this photograph to apply your delivery rules.</p>
            <p v-else-if="capture.deliverySummary?.waitingForRouting" class="muted">Waiting for delivery rules to be checked.</p>
            <p v-if="!deliveries && !error" role="status">Loading deliveries…</p>
            <section v-for="target in destinationGroups" :key="target.id" class="delivery-target">
                <h3><UIcon name="i-lucide-send" />{{ target.name }}</h3>
                <article v-for="decision in target.decisions" :key="String(decision.id)" class="delivery-route">
                    <div class="delivery-outcome" :class="deliveryPresentation(decision.kind).tone">
                        <span class="variant-chip">{{ variant(decision.assetId) }}</span>
                        <UIcon :name="deliveryPresentation(decision.kind).icon" /><strong>{{ deliveryPresentation(decision.kind).label }}</strong>
                        <small v-if="!decision.required" class="muted">Optional</small>
                    </div>
                    <ReevaluateAction v-if="canReevaluateDecision(decision)" :asset-id="decision.assetId" :destination-id="decision.destinationId" :online="online" @refresh="refreshWork" />
                    <details class="section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details</summary><p>{{ decision.explanation }}</p><p class="muted">Recorded Collection: {{ decision.collectionPath ?? 'Legacy decision - Collection not recorded' }}</p><p v-if="decision.ruleName" class="muted">Rule: {{ decision.ruleName }}</p><p v-if="decision.targetPath" class="delivery-path">{{ decision.targetPath }}</p></details>
                </article>
                <DeliveryWork v-for="work in target.work" :key="String(work.id)" :work="work" :variant="variant(work.assetId)" :online="online" :read-only="readOnly || !capture.curation?.releasedAt" delivery-only tabindex="-1" :data-work-state="work.state" @refresh="refreshWork" />
                <details v-if="target.history.length" class="section-disclosure delivery-history"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Earlier work · {{ target.history.length }}</summary><DeliveryWork v-for="work in target.history" :key="String(work.id)" :work="work" :variant="variant(work.assetId)" :online="online" read-only delivery-only /></details>
            </section>
            <p v-if="deliveries && !destinationGroups.length" class="muted">No delivery is recorded on this page.</p>
        </template>
        <template v-else>
            <dl class="routing-facts">
                <dt>Current Collection</dt><dd>{{ capture.currentCollection?.path ?? capture.currentCollection?.name ?? 'Unavailable' }}</dd>
                <dt>Source</dt><dd>{{ capture.source?.name ?? 'Unavailable' }}</dd>
                <dt>Rating / color</dt><dd class="metadata-rating"><CaptureRating :rating="capture.curation?.rating" :label="capture.curation?.label" /><span>{{ capture.curation?.label ?? 'None' }}</span></dd>
                <dt>Tags</dt><dd class="metadata-tags"><span v-for="tag in capture.curation?.tags" :key="tag">{{ tag }}</span><span v-if="!capture.curation?.tags?.length" class="muted">None</span></dd>
                <dt>Choice</dt><dd>{{ capture.curation?.selection ?? 'Neutral' }}</dd>
                <dt>Delivery stage</dt><dd><span class="metadata-workflow" :class="capture.curation?.releasedAt ? 'released' : capture.curation?.held ? 'held' : 'waiting'"><UIcon :name="capture.curation?.releasedAt ? 'i-lucide-check' : capture.curation?.held ? 'i-lucide-pause' : 'i-lucide-clock'" />{{ capture.curation?.releasedAt ? 'Released' : capture.curation?.held ? 'Held' : 'Waiting' }}</span></dd>
            </dl>
            <details v-if="capture.curation?.releasedAt" class="section-disclosure release-metadata">
                <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />At release</summary>
                <dl class="routing-facts">
                    <dt>Collection</dt><dd>{{ capture.releasedCollection?.path ?? capture.releasedCollection?.name ?? 'Legacy release · Collection not recorded' }}</dd>
                    <dt>Rating / color</dt><dd class="metadata-rating"><CaptureRating :rating="capture.curation.releasedRating" :label="capture.curation.releasedLabel" /><span>{{ capture.curation.releasedLabel ?? 'None' }}</span></dd>
                    <dt>Tags</dt><dd>{{ capture.curation.releasedTags?.join(' · ') || 'None' }}</dd>
                </dl>
            </details>
        <details v-for="asset in capture.assets" :key="String(asset.id)" class="section-disclosure delivery-asset" open>
            <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" /><span class="variant-chip">{{ asset.variant }}</span><span>{{ asset.displayName }}</span></summary>
            <dl class="routing-facts">
                <dt>
                    Camera
                </dt>
                <dd>
                    {{ [asset.cameraMake, asset.cameraModel].filter(Boolean).join(' ') || 'Unavailable' }}
                </dd>
                <dt>
                    Capture time
                </dt>
                <dd>
                    {{ asset.captureTime ? new Date(asset.captureTime).toLocaleString() : 'Unavailable' }}{{ asset.captureTimeUncertain ? ' · uncertain' : '' }}
                </dd>
                <dt>Camera serial</dt><dd>{{ asset.cameraSerial ?? 'Unavailable' }}</dd>
                <dt>Extension</dt><dd>{{ asset.displayName?.split('.').pop() ?? 'Unavailable' }}</dd>
                <dt>Size</dt><dd><details v-if="asset.size != null" class="section-disclosure metadata-size"><summary :title="`${asset.size.toLocaleString()} bytes`"><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />{{ formatFileSize(asset.size) }}</summary><span>{{ asset.size.toLocaleString() }} bytes</span></details><span v-else>Unavailable</span></dd>
                <dt>Media type</dt><dd>{{ asset.detectedMediaType ?? 'Unavailable' }}</dd>
            </dl>
        <details class="section-disclosure routing-evidence"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Delivery rules and progress</summary>
        <div
            v-for="decision in decisions?.items?.filter(item => item.assetId === asset.id)"
            :key="String(decision.id)"
        >
            <p>
                {{ decision.kind }}
                ·
                {{ decision.explanation }}
            </p>
            <p class="muted">Recorded Collection: {{ decision.collectionPath ?? 'Legacy decision - Collection not recorded' }}</p>
        </div>
        <DeliveryWork
            v-for="work in deliveries?.items?.filter(item => item.assetId === asset.id)"
            :key="String(work.id)"
            :work="work"
            :online="online"
            :read-only="readOnly || !capture.curation?.releasedAt"
            tabindex="-1"
            :data-work-state="work.state"
            @refresh="refreshWork"
        />
        </details>
    </details>
    <p v-if="deliveries && !deliveries.items?.length">
        No delivery is recorded on this page.
    </p>
        </template>
    <div class="actions">
        <button v-if="decisions?.nextCursor" @click="read('decisions')">
            Earlier delivery decisions
        </button>
        <button v-if="deliveries?.nextCursor" @click="read('deliveries')">
            Earlier delivery work
        </button>
    </div>
    <details class="section-disclosure" v-if="!deliveryOnly">
        <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />
            Earlier actions
        </summary>
        <p v-for="(action, i) in actions?.items" :key="i">
            {{ action.kind }}
            ·
            {{ action.createdAt }}
        </p>
        <button v-if="actions?.nextCursor" @click="read('actions')">
            Older actions
        </button>
    </details>
    </section>
</template>
