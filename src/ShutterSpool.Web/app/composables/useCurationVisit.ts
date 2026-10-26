import { computed, reactive, type Ref } from 'vue';
import { CurationSession } from '../lib/curation-session';
import type { CaptureCurationResponse, CaptureDetailResponse } from '../api/models';

export function useCurationVisit(capture: Ref<CaptureDetailResponse | undefined>, activeId: Ref<string>) {
    const sessions = reactive(new Map<string, CurationSession>());
    const tagDrafts = reactive(new Map<string, string>());
    const activeSession = computed(() => String(capture.value?.id ?? '') === activeId.value ? sessions.get(activeId.value) : undefined);
    const activeTag = computed({
        get: () => tagDrafts.get(activeId.value) ?? '',
        set: (value: string) => tagDrafts.set(activeId.value, value),
    });
    const hasUnresolvedEdits = computed(() => [...sessions.values()].some(session => session.unresolved));
    const hasPendingTag = computed(() => [...tagDrafts.values()].some(value => value.trim().length > 0));

    function forget(id: string) {
        sessions.get(id)?.stop();
        sessions.delete(id);
        tagDrafts.delete(id);
    }
    function observe(
        id: string,
        current: CaptureCurationResponse,
        save: ConstructorParameters<typeof CurationSession>[1],
        read: ConstructorParameters<typeof CurationSession>[2],
        handleFailure: ConstructorParameters<typeof CurationSession>[3],
    ) {
        const session = sessions.get(id);
        if (session) {
            session.observe(current);
            return session;
        }
        const created = new CurationSession(current, save, read, handleFailure);
        sessions.set(id, created);
        return created;
    }
    function sync(id: string, current: CaptureCurationResponse) {
        sessions.get(id)?.observe(current);
    }
    function clear() {
        for (const session of sessions.values()) session.stop();
        sessions.clear();
        tagDrafts.clear();
    }

    return { sessions, tagDrafts, activeSession, activeTag, hasUnresolvedEdits, hasPendingTag, observe, sync, forget, clear };
}

export async function mapBounded<T, R>(items: readonly T[], limit: number, map: (item: T, index: number) => Promise<R>): Promise<R[]> {
    const results = new Array<R>(items.length);
    let next = 0;
    async function worker() {
        while (true) {
            const index = next++;
            if (index >= items.length) return;
            results[index] = await map(items[index]!, index);
        }
    }
    await Promise.all(Array.from({ length: Math.max(1, Math.min(items.length, Math.floor(limit) || 1)) }, worker));
    return results;
}
