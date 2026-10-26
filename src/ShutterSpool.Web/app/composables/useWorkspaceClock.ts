import { inject, onMounted, onUnmounted, provide, ref, watch, type Ref } from 'vue';

export const workspaceClockKey = Symbol('workspace-clock');

export function useWorkspaceClock(active: Ref<boolean>) {
    const now = ref(Date.now());
    let timer: ReturnType<typeof setInterval> | undefined;
    const update = () => { if (active.value) now.value = Date.now(); };
    onMounted(() => { timer = setInterval(update, 1000); });
    watch(active, update);
    onUnmounted(() => clearInterval(timer));
    return now;
}

export function provideWorkspaceClock(active: Ref<boolean>) {
    const now = useWorkspaceClock(active);
    provide(workspaceClockKey, now);
    return now;
}

export function injectWorkspaceClock() {
    return inject<Ref<number>>(workspaceClockKey, ref(Date.now()));
}
