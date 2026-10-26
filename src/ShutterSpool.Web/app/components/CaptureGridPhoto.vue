<script setup lang="ts">
import { computed } from 'vue';
import type { CaptureInboxItemResponse as Capture, CaptureCurationResponse } from '../api/models';
const props = defineProps<{
    capture: Capture;
    selected: boolean;
    gallery: boolean;
    retentionPreferred: boolean;
    workflow?: CaptureCurationResponse | null;
    details: boolean;
    stale: boolean;
    maximumDimension: number;
    previewGeneration: number;
    camera: string;
}>();
const emit = defineEmits<{
    'start-hold': [event: PointerEvent, id: string];
    'cancel-hold': [event?: PointerEvent];
    'end-hold': [];
    open: [id: string];
}>();
const id = computed(() => String(props.capture.id));
</script>

<template>
    <article class="gallery-tile" :data-id="id" :class="{ selected }">
        <button
            class="tile-open"
            :aria-label="gallery ? `Photo details for ${capture.displayName}` : `Open ${capture.displayName}`"
            @pointerdown="emit('start-hold', $event, id)"
            @pointermove="emit('cancel-hold', $event)"
            @pointerup="emit('end-hold')"
            @pointercancel="emit('cancel-hold')"
            @click="emit('open', id)"
        >
            <RetentionLifetime v-if="gallery || retentionPreferred" :evidence="capture.retention" :details="details" :stale="stale" />
            <ReleaseLifetime v-else :curation="workflow" />
            <AuthorizedPreview
                :capture-id="id"
                profile="thumbnail"
                :name="capture.displayName ?? 'Capture'"
                :maximum-dimension="maximumDimension"
                :force-generation="previewGeneration"
            />
            <div v-if="details" class="tile-information">
                <span>{{ capture.displayName }}</span>
                <CaptureRating :rating="capture.curation?.rating" :label="capture.curation?.label" />
                <div class="tile-detail">
                    <span>{{ capture.assets?.map(asset => asset.variant).join(' + ') }}</span>
                    <span>{{ camera }}</span>
                    <span>{{ capture.curation?.tags?.join(' · ') || 'No tags' }}</span>
                </div>
            </div>
        </button>
        <slot name="actions" :capture="capture" />
    </article>
</template>
