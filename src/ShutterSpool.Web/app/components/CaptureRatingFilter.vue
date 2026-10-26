<script setup lang="ts">
import { ratingInterval } from '../lib/workspace-interactions';

const props = defineProps<{ rating: number | null; maximum: number | null }>();
const emit = defineEmits<{ change: [value: { rating: number | null; maximum: number | null }] }>();
let anchor: number | undefined;
let intervalAnchor: number | undefined;
let dragged = false;
let suppressClick = false;

function select(rating: number, shift = false) {
    if (!shift) intervalAnchor = rating;
    else intervalAnchor ??= props.rating ?? rating;
    const range = ratingInterval(shift ? intervalAnchor! : rating, rating);
    emit('change', { rating: range.rating, maximum: range.ratingMaximum });
}
function selected(rating: number) {
    return props.rating != null && rating >= props.rating && rating <= (props.maximum ?? props.rating);
}
function ratingAt(target: EventTarget | null) {
    const value = (target as HTMLElement | null)?.closest<HTMLElement>('[data-rating]')?.dataset.rating;
    return value == null ? undefined : Number(value);
}
function begin(event: PointerEvent) {
    if (event.button !== 0) return;
    const rating = ratingAt(event.target);
    if (rating == null) return;
    anchor = rating;
    dragged = false;
}
function move(event: PointerEvent) {
    if (event.buttons === 0) { cancel(); return; }
    if (anchor == null) return;
    const rating = ratingAt(document.elementFromPoint(event.clientX, event.clientY));
    if (rating == null || rating === anchor && !dragged) return;
    if (!dragged) (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    dragged = true;
    const range = ratingInterval(anchor, rating);
    emit('change', { rating: range.rating, maximum: range.ratingMaximum });
}
function end() {
    suppressClick = dragged;
    anchor = undefined;
    dragged = false;
}
function cancel() {
    anchor = undefined;
    dragged = false;
}
function click(event: MouseEvent) {
    if (!suppressClick) return;
    suppressClick = false;
    event.preventDefault();
    event.stopPropagation();
}
</script>

<template>
    <div
        class="filter-group rating-interval"
        role="group"
        aria-labelledby="rating-filter-title"
        @pointerdown="begin"
        @pointermove="move"
        @pointerup="end"
        @pointercancel="cancel"
        @click.capture="click"
    >
        <button :aria-pressed="rating == null" @click="emit('change', { rating: null, maximum: null })">All ratings</button>
        <button data-rating="-1" :aria-pressed="selected(-1)" @click="select(-1, $event.shiftKey)"><UIcon name="i-lucide-star-off" /><span class="sr-only">Rejected</span></button>
        <button data-rating="0" :aria-pressed="selected(0)" @click="select(0, $event.shiftKey)"><UIcon name="i-lucide-circle-minus" /><span class="sr-only">Not Rated</span></button>
        <button v-for="value in 5" :key="value" :data-rating="value" :aria-label="`${value} stars; Shift selects interval`" :aria-pressed="selected(value)" @click="select(value, $event.shiftKey)">
            {{ value }}<RatingStar />
        </button>
    </div>
</template>
