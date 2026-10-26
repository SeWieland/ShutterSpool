<script setup lang="ts">
import { formatFileSize } from '../lib/workspace-interactions';
import { retentionTiming, retentionLabel, type RetentionEvidence } from '../lib/retention';
import { injectWorkspaceClock } from '../composables/useWorkspaceClock';
const props = defineProps<{ evidence?: RetentionEvidence | null; stale?: boolean; expanded?: boolean }>();
const now = injectWorkspaceClock();
const timing = computed(() => retentionTiming(props.evidence, now.value, true, props.stale));
</script>

<template>
    <details class="retention-details section-disclosure" :open="expanded">
        <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Originals and recovery</summary>
        <p v-if="!evidence" class="muted">Original availability could not be checked. Refresh to try again.</p>
        <template v-else>
            <p>{{ retentionLabel(evidence.disposition) }} · {{ retentionLabel(evidence.tier) }}</p>
            <p>Removing a Favorite brings regular cleanup rules back into effect immediately. Automatic cleanup keeps the Trash recovery window.</p>
            <p v-if="evidence.timeline?.endsAt">{{ timing.title }}</p>
            <p v-if="evidence.timeline?.startedAt">Interval starts {{ evidence.timeline.startedAt.toLocaleString() }}.</p>
            <p v-if="evidence.protectedUntil">Protected until {{ evidence.protectedUntil.toLocaleString() }}.</p>
            <p v-for="reason in evidence.protectionReasons" :key="reason">Protection: {{ reason }}</p>
            <p v-for="reason in evidence.eligibilityReasons" :key="reason">{{ reason }}</p>
            <p v-if="evidence.isPaused">Resume continues existing delivery work and keeps confirmed deliveries. Retry is a separate action for the saved destinations.</p>
            <p v-for="reason in evidence.pauseReasons" :key="reason" class="muted">Pause detail: {{ reason }}</p>
            <ul><li v-for="asset in evidence.availableOriginals" :key="String(asset.assetId)">{{ asset.variant }} · {{ formatFileSize(asset.byteLength) }} · {{ retentionLabel(asset.state) }}<span v-if="asset.protectedUntil"> · protected until {{ asset.protectedUntil.toLocaleString() }}</span></li></ul>
            <p class="muted">Favorites are not backups. Without confirmed delivery, we do not know whether another copy exists. A preview does not prove the original is safely stored.</p>
        </template>
    </details>
</template>
