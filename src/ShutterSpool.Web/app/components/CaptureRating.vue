<script setup lang="ts">
defineProps<{ rating?: number | null; label?: string | null }>();
</script>
<template>
    <span class="capture-rating" :class="`rating-${(label ?? 'None').toLowerCase()}`" :aria-label="rating === -1 ? 'Rejected' : !rating ? 'Not Rated' : `${rating} stars`">
        <UIcon v-if="rating === -1" name="i-lucide-star-off" />
        <RatingStar v-else-if="!rating" />
        <RatingStar v-for="n in Math.max(0, rating ?? 0)" v-else :key="n" class="filled" />
    </span>
</template>
