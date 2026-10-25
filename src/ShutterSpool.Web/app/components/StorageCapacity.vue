<script setup lang="ts">
import type { RetentionCapacityResponse } from '../api/models';
import { capacityComposition, storageMessage, historyStatus } from '../lib/storage-presentation';
import { formatFileSize } from '../lib/workspace-interactions';
const props = defineProps<{ snapshot?: RetentionCapacityResponse; quotaActive?: boolean; stale: boolean }>();
const selected = ref('photographs');
const lastKnown = computed(() => {
    const observed = props.snapshot?.observedAt?.getTime();
    return props.stale || observed == null || !Number.isFinite(observed) || Date.now() - observed > 90000;
});
const composition = computed(() => capacityComposition(props.snapshot, props.quotaActive === true));
const segments = computed(() => {
    let offset = 0;
    return composition.value.segments.map(segment => {
        const length = composition.value.total ? segment.bytes / composition.value.total * 100 : 0;
        const result = { ...segment, length, offset };
        offset += length;
        return result;
    });
});
const explanation = computed(() => segments.value.find(segment => segment.key === selected.value)?.explanation ?? 'Choose a category to see what it includes.');
const label = useId();
</script>

<template>
    <section class="storage-capacity" aria-label="Storage capacity">
        <div class="capacity-visual" :class="{ 'capacity-unavailable': !composition.known }">
            <svg viewBox="0 0 200 200" role="img" :aria-labelledby="label">
                <title :id="label">{{ composition.known ? 'Storage composition. Exact amounts are listed beside the chart.' : 'Storage accounting is unavailable. No usage percentage can be confirmed.' }}</title>
                <circle class="capacity-track" cx="100" cy="100" r="78" fill="none" stroke-width="18" />
                <circle v-for="segment in segments.filter(segment => segment.bytes > 0)" :key="segment.key" :class="[`capacity-${segment.key}`, { 'capacity-highlight': selected === segment.key }]" cx="100" cy="100" r="78" pathLength="100" fill="none" stroke-width="18" :stroke-dasharray="`${segment.length} ${100 - segment.length}`" :stroke-dashoffset="-segment.offset" transform="rotate(-90 100 100)" />
            </svg>
            <div class="capacity-center"><strong>{{ composition.known ? formatFileSize(snapshot?.usedBytes) : 'Unavailable' }}</strong><span>{{ composition.known ? 'in use' : 'usage not confirmed' }}</span><span v-if="lastKnown" class="capacity-stale">Last known</span></div>
        </div>
        <div class="capacity-reading">
            <div class="capacity-heading"><h3>Storage capacity</h3><span v-if="snapshot?.admissionBlocked" class="capacity-state">Uploads paused</span><span v-else-if="quotaActive === false" class="capacity-state">No active limit</span><span v-else-if="quotaActive === undefined" class="capacity-state">Limit state not confirmed</span><slot name="action" /></div>
            <p v-if="!composition.known" class="notice">Storage usage cannot be confirmed. Refresh and check the configured spool before relying on available space.</p>
            <template v-else>
                <p v-if="composition.excess" class="capacity-warning">{{ formatFileSize(composition.excess) }} above the saved limit, including reservations.</p>
                <p v-else-if="quotaActive" class="muted">Saved limit {{ formatFileSize(snapshot?.quotaBytes) }} · {{ formatFileSize(composition.remaining) }} allowance remaining.</p>
                <p v-else class="muted">Usage and ongoing reservations. {{ quotaActive === false ? 'A storage limit is not active.' : 'Saved limit settings are unavailable.' }}</p>
                <ul class="capacity-legend" aria-label="Storage categories">
                    <li v-for="segment in segments" :key="segment.key"><button :aria-pressed="selected === segment.key" @pointerenter="selected = segment.key" @focus="selected = segment.key" @click="selected = segment.key"><span class="capacity-swatch" :class="`capacity-${segment.key}`" aria-hidden="true" /><span>{{ segment.label }}</span><strong>{{ formatFileSize(segment.bytes) }}</strong></button></li>
                </ul>
                <p class="capacity-explanation">{{ explanation }}</p>
            </template>
            <p v-if="snapshot?.observedAt" class="capacity-observation">{{ lastKnown ? 'Last known observation' : 'Checked' }} {{ snapshot.observedAt.toLocaleString() }}. Quota allowance is not guaranteed free disk space.</p>
        </div>
        <details class="section-disclosure settings-advanced capacity-details" v-if="snapshot"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Storage details</summary>
            <p>{{ historyStatus(snapshot.pressureState) }}</p>
            <p v-for="blocker in snapshot.blockers" :key="blocker">{{ storageMessage(blocker) }}</p>
            <dl class="capacity-values"><dt>Cleanup starts above</dt><dd>{{ formatFileSize(snapshot.highWatermarkBytes) }}</dd><dt>Cleanup target</dt><dd>{{ formatFileSize(snapshot.recoveryTargetBytes) }}</dd><dt>Protected / Favorites</dt><dd>{{ formatFileSize(snapshot.protectedBytes) }} / {{ formatFileSize(snapshot.favoriteBytes) }} · overlapping subsets of the usage above</dd><dt>Planned reclamation</dt><dd>{{ formatFileSize(snapshot.plannedReclamationBytes) }} · not yet freed</dd><dt>Historically purged</dt><dd>{{ formatFileSize(snapshot.purgedBytes) }} · physical removal already recorded</dd></dl>
            <p v-if="snapshot.nextEligiblePurgeAt">Next known purge eligibility: {{ snapshot.nextEligiblePurgeAt.toLocaleString() }}. Eligibility does not promise execution.</p>
            <details class="section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Technical details</summary><p>{{ snapshot.pressureState ?? 'No state supplied' }}</p><ul><li v-for="blocker in snapshot.blockers" :key="blocker">{{ blocker }}</li></ul></details>
        </details>
    </section>
</template>
