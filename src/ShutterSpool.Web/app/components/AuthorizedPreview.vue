<script setup lang="ts">
import { derivative } from "../lib/api";
import { onAccessLost } from "../lib/session";
import type { DisplayState } from '../lib/gallery-slideshow';
const props = defineProps<{
    captureId?: string | null;
    profile: "thumbnail" | "detail";
    name: string;
    generation?: number;
    forceGeneration?: number;
    sourceId?: string;
    maximumDimension?: number;
}>();
const emit = defineEmits<{ display: [state: DisplayState] }>();
const element = ref<HTMLElement>();
const url = ref("");
const state = ref("pending");
let visible = false;
let etag: string | undefined;
let controller: AbortController | undefined;
let observer: IntersectionObserver | undefined;
let version = 0;
let inFlight = false;
let imageLoaded = false;
function cancel() {
    version++;
    controller?.abort();
    inFlight = false;
}
function clear() {
    cancel();
    if (url.value) URL.revokeObjectURL(url.value);
    url.value = "";
    etag = undefined;
    imageLoaded = false;
    emit('display', 'pending');
}
async function load(force = false) {
    if (!visible || document.hidden) return;
    if (!props.captureId) {
        state.value = "pending";
        return;
    }
    if (inFlight && !force) return;
    controller?.abort();
    controller = new AbortController();
    const current = ++version;
    inFlight = true;
    try {
        const { response, blob } = await derivative(
            props.captureId,
            props.profile,
            controller.signal,
            etag,
            props.maximumDimension,
        );
        if (current !== version) return;
        if (response.status === 304) {
            if (url.value) state.value = "ready";
            if (url.value && imageLoaded) emit('display', 'ready');
            return;
        }
        state.value =
            response.headers.get("X-Preview-State")?.toLowerCase() ??
            (response.ok ? "ready" : "unavailable");
        if (!blob) {
            if ([401, 403, 404, 410].includes(response.status)) clear();
            emit('display', state.value === 'pending' ? 'pending' : 'unavailable');
            return;
        }
        if (url.value) URL.revokeObjectURL(url.value);
        imageLoaded = false;
        url.value = URL.createObjectURL(blob);
        emit('display', 'pending');
        etag = response.headers.get("ETag") ?? undefined;
    } catch {
        if (current === version) { state.value = "unavailable"; emit('display', 'unavailable'); }
    } finally {
        if (current === version) inFlight = false;
    }
}
function displayed() { imageLoaded = true; emit('display', state.value === 'ready' ? 'ready' : state.value === 'pending' ? 'pending' : 'unavailable'); }
function decodeFailed() { clear(); state.value = 'unavailable'; emit('display', 'unavailable'); }
function visibilityChanged() {
    if (document.hidden) cancel();
    else if (visible) void load();
}
const unsubscribe = onAccessLost(clear);
onMounted(() => {
    observer = new IntersectionObserver((entries) => {
        visible = !!entries[0]?.isIntersecting;
        if (visible) void load();
        else cancel();
    });
    if (element.value) observer.observe(element.value);
    document.addEventListener("visibilitychange", visibilityChanged);
});
watch(
    () => [props.captureId, props.sourceId],
    () => {
        clear();
        void load();
    },
);
watch(() => props.maximumDimension, () => {
    cancel();
    etag = undefined;
    void load();
});
watch(
    () => props.forceGeneration,
    () => { clear(); void load(true); },
);
watch(
    () => props.generation,
    () => {
        void load();
    },
);
onUnmounted(() => {
    clear();
    observer?.disconnect();
    document.removeEventListener("visibilitychange", visibilityChanged);
    unsubscribe();
});
</script>

<template>
    <div ref="element" class="preview" :class="profile">
        <img v-if="url" :src="url" :alt="state === 'ready' ? name : ''" @load="displayed" @error="decodeFailed" />
        <div
            v-if="!url || state !== 'ready'"
            class="preview-message"
            :class="{ 'preview-caption': url }"
        >
            <span>{{
                state === "pending" ? "Preview pending" : "Preview unavailable"
            }}</span>
        </div>
    </div>
</template>
