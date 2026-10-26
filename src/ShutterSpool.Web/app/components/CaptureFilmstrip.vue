<script setup lang="ts">
import type { CaptureInboxItemResponse as Capture } from '../api/models';

const props = defineProps<{
    items: Array<{ capture: Capture; retentionWins: boolean; curation?: Capture['curation'] }>;
    activeId: string;
    stale: boolean;
    maximumDimension: number;
    previewVersions: Record<string, number>;
}>();
const emit = defineEmits<{ open: [payload: { id: string; shift: boolean }] }>();
</script>

<template>
    <div class="filmstrip" aria-label="Capture filmstrip">
        <button
            v-for="{ capture, retentionWins, curation } in items"
            :key="String(capture.id)"
            :aria-label="capture.displayName ?? 'Capture'"
            :aria-pressed="String(capture.id) === activeId"
            @click="emit('open', { id: String(capture.id), shift: $event.shiftKey })"
        >
            <RetentionLifetime v-if="retentionWins" :evidence="capture.retention" details :stale="stale" />
            <ReleaseLifetime v-else :curation="curation" />
            <AuthorizedPreview
                :capture-id="String(capture.id)"
                profile="thumbnail"
                :name="capture.displayName ?? 'Capture'"
                :maximum-dimension="maximumDimension"
                :force-generation="previewVersions[String(capture.id)] ?? 0"
            />
        </button>
    </div>
</template>
