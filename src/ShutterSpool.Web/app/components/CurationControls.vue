<script setup lang="ts">
defineProps<{
    rating?: number;
    label?: string | null;
    tags: string[];
    tag: string;
    disabled: boolean;
    busy?: boolean;
    mixed?: boolean;
    suggestions?: string[];
}>();
const emit = defineEmits<{
    rating: [value: number];
    label: [value: string | null];
    'update:tag': [value: string];
    addTag: [value?: string];
    removeTag: [value: string];
    reset: [all: boolean];
}>();
const labels = ['Red', 'Yellow', 'Green', 'Blue', 'Purple'];
</script>

<template>
    <div class="curation-controls" :aria-busy="busy">
        <fieldset class="curation-rating-controls" :disabled="disabled || busy">
        <legend class="sr-only">Rating</legend>
        <div class="signed-ratings" role="group" aria-label="Rating">
            <button aria-label="Rejected" title="Rejected" :aria-pressed="rating === -1" @click="emit('rating', -1)"><UIcon name="i-lucide-star-off" /></button>
            <button class="not-rated" aria-label="Not Rated" title="Not Rated" :aria-pressed="rating === 0" @click="emit('rating', 0)"><RatingStar /></button>
            <button v-for="n in 5" :key="n" :aria-label="`${n} stars`" :title="`${n} stars`" :aria-pressed="rating != null && rating >= n" @click="emit('rating', n)"><RatingStar /></button>
        </div>
        </fieldset>
        <div class="curation-markers">
        <fieldset class="curation-color-controls" :disabled="disabled || busy"><legend class="sr-only">Color label</legend>
        <div class="color-controls" role="group" aria-label="Color label">
            <button aria-label="None" title="None" :aria-pressed="label === null" @click="emit('label', null)"><UIcon name="i-lucide-circle-slash" /></button>
            <button v-for="color in labels" :key="color" :aria-label="color" :title="color" :aria-pressed="label === color" @click="emit('label', color)"><span class="label-dot" :class="color.toLowerCase()" /></button>
        </div>
        </fieldset>
        <slot name="favorite" />
        </div>
        <fieldset class="curation-tag-controls" :disabled="disabled || busy"><legend class="sr-only">Tags and changes</legend>
        <p v-if="mixed" class="muted">Mixed values · changes apply to all selected photographs.</p>
        <UIcon v-if="busy" name="i-lucide-loader-circle" class="saving-spinner" aria-label="Saving" role="status" />
        <div class="tag-chips">
            <button v-for="value in tags" :key="value" :aria-label="`Remove tag ${value}`" @click="emit('removeTag', value)">{{ value }}<UIcon name="i-lucide-x" /></button>
        </div>
        <form class="tag-entry" @submit.prevent="emit('addTag')">
            <input :value="tag" aria-label="Add tag" maxlength="64" placeholder="Add a tag…" autocomplete="off" @input="emit('update:tag', ($event.target as HTMLInputElement).value)" />
            <button :disabled="!tag.trim()" aria-label="Add tag" title="Add tag"><UIcon name="i-lucide-plus" /></button>
        </form>
        <div v-if="suggestions?.length" class="tag-suggestions"><button v-for="value in suggestions" :key="value" @click="emit('addTag', value)">{{ value }}</button></div>
        </fieldset>
        <slot name="collection" />
        <fieldset class="curation-tag-controls" :disabled="disabled || busy"><legend class="sr-only">Changes</legend>
        <div class="actions reset-actions">
            <button title="Return rating, color and tags to how they were when you opened this workspace" @click="emit('reset', false)">Revert edits</button>
            <button title="Clear rating, color and tags" @click="emit('reset', true)">Clear choices</button>
        </div>
        </fieldset>
    </div>
</template>
