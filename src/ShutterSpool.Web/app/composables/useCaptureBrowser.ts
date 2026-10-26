import { reactive, ref } from 'vue';
import type { CaptureInboxItemResponse as Capture } from '../api/models';

type WorkspacePhotoView = 'Curation' | 'Gallery';
type PageSnapshot = { rows: Capture[]; cursor: string | null | undefined; afterId: string; selection: string[] };

/** Owns the loaded photo window, paging cursor, per-view snapshots and stale-read fence. */
export function useCaptureBrowser() {
    const rows = ref<Capture[]>([]);
    const cursor = ref<string | null>();
    const pageAfterId = ref('');
    const selection = ref<string[]>([]);
    const snapshots = reactive<Record<WorkspacePhotoView, PageSnapshot>>({
        Curation: { rows: [], cursor: null, afterId: '', selection: [] },
        Gallery: { rows: [], cursor: null, afterId: '', selection: [] },
    });
    const requestSequence = ref(0);
    let requestController: AbortController | undefined;
    let parentSignal: AbortSignal | undefined;
    let parentAbort: (() => void) | undefined;

    function releaseParent() {
        if (parentSignal && parentAbort) parentSignal.removeEventListener('abort', parentAbort);
        parentSignal = undefined;
        parentAbort = undefined;
    }

    function beginRequest() {
        releaseParent();
        requestController?.abort();
        requestController = new AbortController();
        return ++requestSequence.value;
    }
    function requestSignal(request: number) { return isCurrent(request) ? requestController?.signal : undefined; }
    function finishRequest(request: number) {
        if (!isCurrent(request)) return;
        releaseParent();
        requestController = undefined;
    }
    function cancelRequests() {
        requestSequence.value++;
        releaseParent();
        requestController?.abort();
        requestController = undefined;
    }
    function isCurrent(request: number) { return request === requestSequence.value; }
    function save(view: WorkspacePhotoView) {
        snapshots[view] = { rows: rows.value, cursor: cursor.value, afterId: pageAfterId.value, selection: [...selection.value] };
    }
    function restore(view: WorkspacePhotoView) {
        rows.value = snapshots[view].rows;
        cursor.value = snapshots[view].cursor;
        pageAfterId.value = snapshots[view].afterId;
        selection.value = [...snapshots[view].selection];
    }
    function forget(id: string) {
        rows.value = rows.value.filter(row => String(row.id) !== id);
        for (const view of ['Curation', 'Gallery'] as const)
            snapshots[view].rows = snapshots[view].rows.filter(row => String(row.id) !== id);
    }
    function clear() {
        rows.value = [];
        cursor.value = null;
        pageAfterId.value = '';
        selection.value = [];
        snapshots.Curation = { rows: [], cursor: null, afterId: '', selection: [] };
        snapshots.Gallery = { rows: [], cursor: null, afterId: '', selection: [] };
        cancelRequests();
    }

    /** A stable query snapshot couples filters and sort order to the page request that owns it. */
    function query(criteria: {
        favoritesOnly: boolean;
        sourceIds: string[];
        collectionIds: string[];
        rating: number | null;
        ratingMaximum: number | null;
        labels: string[];
        tag: string;
        workflows: string[];
        oldestFirst: boolean;
    }, next = false) {
        return {
            favoritesOnly: criteria.favoritesOnly || undefined,
            limit: 30,
            cursor: next ? cursor.value ?? undefined : undefined,
            sourceIds: criteria.sourceIds.length ? [...criteria.sourceIds] : undefined,
            collectionIds: criteria.collectionIds.length ? [...criteria.collectionIds] : undefined,
            rating: criteria.rating ?? undefined,
            ratingMaximum: criteria.ratingMaximum ?? undefined,
            labels: criteria.labels.length ? [...criteria.labels] : undefined,
            tag: criteria.tag || undefined,
            workflows: criteria.workflows.length ? [...criteria.workflows] : undefined,
            oldestFirst: criteria.oldestFirst,
        };
    }

    /** Owns transport cancellation and query snapshot for an inbox page; callers reconcile successful rows. */
    function requestPage<T>(parameters: {
        query: Record<string, unknown>;
        read: (query: Record<string, unknown>, signal: AbortSignal) => Promise<T>;
        parentSignal?: AbortSignal;
        onCancel?: () => void;
    }) {
        const request = beginRequest();
        const signal = requestSignal(request)!;
        const cancelFromParent = () => {
            if (isCurrent(request)) cancelRequests();
            parameters.onCancel?.();
        };
        parentSignal = parameters.parentSignal;
        parentAbort = cancelFromParent;
        if (parameters.parentSignal?.aborted) cancelFromParent();
        else parameters.parentSignal?.addEventListener('abort', cancelFromParent, { once: true });
        const response = (async () => {
            try {
                if (!isCurrent(request)) return { current: false as const };
                const value = await parameters.read(parameters.query, signal);
                return { current: isCurrent(request), value };
            } catch (error) {
                return { current: isCurrent(request), error };
            }
        })();
        return { request, response, finish: () => finishRequest(request) };
    }

    function acceptPage(page: { nextCursor?: string | null; items?: Capture[] | null } | undefined) {
        const items = page?.items ?? [];
        cursor.value = page?.nextCursor ?? null;
        pageAfterId.value = String(items.at(-1)?.id ?? '');
        return items;
    }

    return { rows, cursor, pageAfterId, selection, snapshots, requestSequence, beginRequest, requestSignal, finishRequest, cancelRequests, isCurrent, save, restore, forget, clear, query, requestPage, acceptPage };
}
