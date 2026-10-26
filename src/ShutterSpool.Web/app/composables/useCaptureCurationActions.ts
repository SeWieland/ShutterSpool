import { computed, onScopeDispose, ref, watch, type Ref } from 'vue';
import { accessGeneration } from '../lib/session';
import { api, cancellable, handleFailure } from '../lib/api';
import { mapBounded } from './useCurationVisit';
import { originalRemovalScope, retentionTiming } from '../lib/retention';
import { values, type CurationSession } from '../lib/curation-session';
import type { CaptureInboxItemResponse as Capture, CaptureCurationResponse, CaptureDetailResponse } from '../api/models';

type BulkAction = 'rating' | 'label' | 'tag' | 'removeTag' | 'discard' | 'hold' | 'restore' | 'release' | 'reset';
type ActionSession = Pick<CurationSession, 'intent' | 'opening' | 'settle' | 'unresolved' | 'external' | 'acceptBatch'>;

interface UseCaptureCurationActionsOptions {
    selection: Ref<string[]>;
    rows: Ref<Capture[]>;
    sessions: { get(id: string): ActionSession | undefined };
    online: Ref<boolean>;
    view: Ref<string>;
    visit: () => number;
    observe: (id: string, current: CaptureCurationResponse) => void;
    forget: (id: string) => void;
    refresh: (ids: Set<string>) => Promise<void> | void;
    confirm: (message: string) => boolean;
}

/** Owns selected-capture mutations and their uncertain outcomes for one workspace visit. */
export function useCaptureCurationActions(options: UseCaptureCurationActionsOptions) {
    const busy = ref(false);
    const result = ref('');
    const rating = ref(0);
    const label = ref<string | null>(null);
    const tag = ref('');
    const removedTag = ref('');
    const uncertainTrashIds = ref<string[]>([]);
    const pendingTrashIds = ref<string[]>([]);
    const trashVerificationBusy = ref(false);
    const trashVerificationMessage = ref('');
    let readController: AbortController | undefined;

    const selectedValues = computed(() => options.selection.value.map(id =>
        options.sessions.get(id)?.intent ?? values(options.rows.value.find(row => String(row.id) === id)?.curation ?? {})));
    const valuesSummary = computed(() => {
        const items = selectedValues.value;
        return {
            rating: items.length && items.every(item => item.rating === items[0]!.rating) ? items[0]!.rating : undefined,
            label: items.length && items.every(item => item.label === items[0]!.label) ? items[0]!.label : undefined,
            tags: items[0]?.tags.filter(value => items.every(item => item.tags.includes(value))) ?? [],
            mixed: items.length > 1 && items.some(item => JSON.stringify(item) !== JSON.stringify(items[0])),
        };
    });

    function isCurrent(access: number, visit: number, view: string) {
        return access === accessGeneration() && visit === options.visit() && view === options.view.value;
    }

    function selectionCurrent(ids: string[]) {
        return ids.length === options.selection.value.length && ids.every((id, index) => id === options.selection.value[index]);
    }

    function cancelReads() {
        readController?.abort();
        readController = undefined;
    }

    function readSignal() {
        cancelReads();
        return readController = new AbortController();
    }

    function markTrashUncertain(id: string) {
        uncertainTrashIds.value = [...new Set([...uncertainTrashIds.value, id])];
        trashVerificationMessage.value = pendingTrashIds.value.includes(id)
            ? 'A Trash request is still pending. Other controls stay locked until it completes.'
            : 'A Trash outcome needs checking. Verify current details before choosing Trash again.';
    }

    function settleTrash(id: string) {
        pendingTrashIds.value = pendingTrashIds.value.filter(value => value !== id);
        uncertainTrashIds.value = uncertainTrashIds.value.filter(value => value !== id);
    }

    async function submitTrash(id: string, expectedRevision: number) {
        if (busy.value || pendingTrashIds.value.includes(id) || uncertainTrashIds.value.includes(id) || !options.online.value) return;
        const access = accessGeneration();
        const ownerView = options.view.value;
        pendingTrashIds.value = [...pendingTrashIds.value, id];
        markTrashUncertain(id);
        busy.value = true;
        result.value = '';
        try {
            await api.management.captures.byId(id).trash.post({ expectedRevision });
            if (access !== accessGeneration()) return;
            pendingTrashIds.value = pendingTrashIds.value.filter(value => value !== id);
            if (options.view.value === ownerView) {
                settleTrash(id);
                if (!uncertainTrashIds.value.length) trashVerificationMessage.value = '';
            }
            else trashVerificationMessage.value = 'The Trash request completed after leaving this view. Verify current details before using another control.';
            result.value = 'The selected photograph was moved to Trash.';
            await options.refresh(new Set([id]));
            return true;
        } catch (failure) {
            if (access !== accessGeneration()) return;
            pendingTrashIds.value = pendingTrashIds.value.filter(value => value !== id);
            const status = handleFailure(failure);
            if (access !== accessGeneration()) return;
            markTrashUncertain(id);
            trashVerificationMessage.value = status === 409
                ? 'The photograph changed or has protection in place. Verify current details before choosing Trash again.'
                : 'The Trash outcome could not be confirmed. Verify current details before choosing Trash again.';
            result.value = trashVerificationMessage.value;
            return false;
        } finally {
            if (access === accessGeneration()) {
                pendingTrashIds.value = pendingTrashIds.value.filter(value => value !== id);
                busy.value = false;
            }
        }
        return false;
    }

    watch([options.view, options.selection], cancelReads, { deep: true, flush: 'sync' });
    onScopeDispose(cancelReads);

    function bulkItem(action: BulkAction, id: string, detail: CaptureDetailResponse) {
        const item = { id, expectedRevision: detail.curation?.revision, ...values(detail.curation ?? {}) };
        switch (action) {
            case 'reset': return { ...item, rating: 0, tags: [], label: null };
            case 'discard': return { ...item, ...options.sessions.get(id)?.opening };
            case 'rating': return { ...item, rating: rating.value };
            case 'label': return { ...item, label: label.value };
            case 'tag': return { ...item, tags: tag.value.trim() ? [...item.tags, tag.value.trim()] : item.tags };
            case 'removeTag': return { ...item, tags: item.tags.filter(value => value !== removedTag.value) };
            default: return item;
        }
    }

    async function run(action: BulkAction): Promise<boolean> {
        if (options.view.value !== 'Curation' || !options.online.value || !options.selection.value.length || busy.value)
            return false;
        if (action === 'release' && !options.confirm(`Release ${options.selection.value.length} explicitly selected Captures?`))
            return false;
        const ids = [...options.selection.value];
        const access = accessGeneration();
        const visit = options.visit();
        const view = options.view.value;
        const ownerCurrent = () => isCurrent(access, visit, view);
        const current = () => ownerCurrent() && selectionCurrent(ids);
        busy.value = true;
        result.value = '';
        try {
            for (const id of ids) {
                await options.sessions.get(id)?.settle();
                if (!current()) return false;
            }
            if (ids.some(id => options.sessions.get(id)?.unresolved))
                throw new Error('Resolve selected Capture edits before batching.');
            const controller = readSignal();
            const details = await mapBounded(ids, 4, id => api.management.captures.byId(id).get({ options: [cancellable(controller.signal)] }));
            if (readController === controller) readController = undefined;
            if (controller.signal.aborted || !current()) return false;
            const released = details.filter(detail => detail?.curation?.releasedAt);
            if (released.length) {
                result.value = `No batch submitted. Released Captures are read-only here: ${released.map(detail => detail?.displayName ?? 'Photograph').join(', ')}. Remove them from the selection and try again.`;
                return false;
            }
            for (const detail of details) {
                if (!detail?.id || !detail.curation) throw new Error('Capture unavailable');
                options.observe(String(detail.id), detail.curation);
            }
            if (action === 'discard' && ids.some(id => options.sessions.get(id)?.external))
                throw new Error('Values changed elsewhere; review before restoring.');
            const items = details.map((detail, index) => bulkItem(action, ids[index]!, detail!));
            const batch = api.management.captures.batch;
            const response = ['reset', 'discard', 'rating', 'label', 'tag', 'removeTag'].includes(action)
                ? await batch.curation.post({ items })
                : await batch[action as 'hold' | 'restore' | 'release'].post({ items });
            if (!ownerCurrent()) return false;
            const responseItems = response?.items ?? [];
            const failures = responseItems.filter(item => item.status !== 'committed');
            for (const item of responseItems)
                if (item.status === 'committed' && item.current && ids.includes(String(item.id)))
                    options.sessions.get(String(item.id))?.acceptBatch(item.current);
            result.value = failures.map(item => `${details.find(detail => String(detail?.id) === String(item.id))?.displayName ?? 'Photograph'}: ${item.errorCode ?? item.status}`).join(' · ');
            const completeSet = responseItems.length === ids.length
                && new Set(responseItems.map(item => String(item.id))).size === ids.length
                && responseItems.every(item => ids.includes(String(item.id)));
            const fullyCommitted = completeSet && responseItems.every(item => item.status === 'committed');
            if (!completeSet)
                result.value = 'Some outcomes are unknown. Review the selected photographs before retrying.';
            await options.refresh(new Set(ids));
            return ownerCurrent() && fullyCommitted;
        } catch (failure) {
            if (!ownerCurrent()) return false;
            handleFailure(failure);
            result.value = 'Batch did not complete or its outcome is uncertain. Selected photographs were retained; refresh and review before trying again.';
            await options.refresh(new Set(ids));
            return false;
        } finally {
            cancelReads();
            busy.value = false;
        }
    }

    async function favorite(isFavorite: boolean) {
        if (busy.value || !options.selection.value.length || !options.online.value) return;
        const ids = [...options.selection.value], access = accessGeneration(), visit = options.visit(), view = options.view.value;
        const ownerCurrent = () => isCurrent(access, visit, view);
        const current = () => ownerCurrent() && selectionCurrent(ids);
        busy.value = true;
        result.value = '';
        const outcomes: string[] = [];
        try {
            for (const id of ids) {
                if (!current()) return;
                try {
                    const controller = readSignal();
                    const detail = await api.management.captures.byId(id).get({ options: [cancellable(controller.signal)] });
                    if (readController === controller) readController = undefined;
                    if (!current()) return;
                    if (detail?.retention?.captureRevision == null) throw new Error('Retention unavailable');
                    await api.management.captures.byId(id).favorite.put({ expectedRevision: detail.retention.captureRevision, isFavorite });
                } catch (failure) {
                    if (!ownerCurrent()) return;
                    const status = handleFailure(failure);
                    outcomes.push(`${options.rows.value.find(row => String(row.id) === id)?.displayName ?? 'Photograph'}: ${status === 409 ? 'conflict; review current heart' : 'outcome unconfirmed; review before retrying'}`);
                }
            }
            if (ownerCurrent()) {
                result.value = outcomes.join(' · ') || `${ids.length} Favorites saved.`;
                await options.refresh(new Set(ids));
            }
        } finally {
            cancelReads();
            busy.value = false;
        }
    }

    async function trash() {
        if (busy.value || !options.selection.value.length || !options.online.value) return;
        const locked = new Set(uncertainTrashIds.value);
        if (options.selection.value.some(id => locked.has(id))) {
            result.value = 'Refresh and verify the previous Trash outcome before submitting these photographs again.';
            return;
        }
        const scope = options.selection.value.map(id => options.rows.value.find(row => String(row.id) === id)).filter((row): row is Capture => !!row);
        if (scope.length !== options.selection.value.length || scope.some(row => row.retention?.captureRevision == null || !originalRemovalScope(row.retention) || retentionTiming(row.retention, Date.now(), true).stale)) {
            result.value = 'We could not check the selected originals. Refresh before moving them to Trash.';
            return;
        }
        const variants = scope.map(row => `${row.displayName ?? 'Photograph'}: ${originalRemovalScope(row.retention)}`).join('\n');
        if (!options.confirm(`Move ${scope.length} selected photographs to Trash, including ${scope.filter(row => row.retention?.isFavorite).length} Favorites?\n\nOriginal variants:\n${variants}\nTheir recovery window starts once the move is safely recorded. Trash still uses storage. These may be your only copies; gallery promises and preservation holds still apply. Backups and delivered or downloaded copies remain separate.`)) return;
        const access = accessGeneration(), visit = options.visit(), view = options.view.value;
        const outcomes: string[] = [];
        busy.value = true;
        result.value = '';
        try {
            for (const row of scope) {
                if (!isCurrent(access, visit, view)) return;
                const id = String(row.id);
                pendingTrashIds.value = [...pendingTrashIds.value, id];
                markTrashUncertain(id);
                try {
                    await api.management.captures.byId(id).trash.post({ expectedRevision: row.retention!.captureRevision! });
                } catch (failure) {
                    if (access !== accessGeneration()) return;
                    pendingTrashIds.value = pendingTrashIds.value.filter(value => value !== id);
                    const status = handleFailure(failure);
                    if (access !== accessGeneration()) return;
                    outcomes.push(`${row.displayName ?? 'Photograph'}: ${status === 409 ? 'conflict/protected' : 'outcome unconfirmed'}`);
                    markTrashUncertain(id);
                    if (!isCurrent(access, visit, view)) return;
                    continue;
                }
                pendingTrashIds.value = pendingTrashIds.value.filter(value => value !== id);
                if (isCurrent(access, visit, view)) settleTrash(id);
                else trashVerificationMessage.value = 'A Trash request completed after leaving this view. Verify current details before using another control.';
            }
            if (isCurrent(access, visit, view)) {
                result.value = outcomes.join(' · ') || `${scope.length} selected photographs moved to Trash.`;
                if (!uncertainTrashIds.value.length) await options.refresh(new Set(scope.map(row => String(row.id))));
            }
        } finally {
            busy.value = false;
        }
    }

    async function verifyTrashOutcomes(requestedIds?: Set<string>) {
        if (trashVerificationBusy.value || !uncertainTrashIds.value.length || !options.online.value) return;
        const ids = uncertainTrashIds.value.filter(id => !pendingTrashIds.value.includes(id) && (!requestedIds || requestedIds.has(id)));
        if (!ids.length) return;
        const access = accessGeneration(), visit = options.visit(), view = options.view.value;
        trashVerificationBusy.value = true;
        trashVerificationMessage.value = 'Checking current Trash outcomes…';
        try {
            const controller = readSignal();
            const results = await mapBounded(ids, 4, async id => {
                try {
                    const detail = await api.management.captures.byId(id).get({ options: [cancellable(controller.signal)] });
                    return detail?.id && String(detail.id) === id && detail.retention?.captureRevision != null ? { id, detail } : { id, error: true };
                } catch (error) {
                    if (isCurrent(access, visit, view)) handleFailure(error);
                    return { id, error: true };
                }
            });
            if (readController === controller) readController = undefined;
            if (controller.signal.aborted || !isCurrent(access, visit, view)) return;
            const failed = results.some(item => 'error' in item);
            if (failed) {
                trashVerificationMessage.value = 'The authoritative reread was incomplete. This Trash outcome stays locked; refresh to retry the check.';
                result.value = trashVerificationMessage.value;
                return;
            }
            for (const item of results) {
                if (!('detail' in item)) continue;
                const id = item.id;
                const detail = item.detail!;
                if (['trash', 'purging', 'purged'].includes(detail.retention?.disposition ?? ''))
                    options.forget(id);
                else {
                    const index = options.rows.value.findIndex(row => String(row.id) === id);
                    if (index >= 0) options.rows.value[index] = { ...options.rows.value[index], ...detail };
                }
            }
            uncertainTrashIds.value = uncertainTrashIds.value.filter(id => !ids.includes(id));
            trashVerificationMessage.value = uncertainTrashIds.value.length
                ? 'The selected outcomes are verified. Other uncertain Trash outcomes remain locked.'
                : 'The previous Trash outcomes are verified. Review current details before choosing Trash again.';
            result.value = trashVerificationMessage.value;
            await options.refresh(new Set(ids));
        } finally {
            cancelReads();
            trashVerificationBusy.value = false;
        }
    }

    function clearPrivateState() {
        cancelReads();
        uncertainTrashIds.value = [];
        pendingTrashIds.value = [];
        result.value = '';
        trashVerificationMessage.value = '';
        busy.value = false;
        trashVerificationBusy.value = false;
    }

    return { busy, result, rating, label, tag, removedTag, selectedValues, valuesSummary,
        uncertainTrashIds, trashVerificationBusy, run, favorite, trash, verifyTrashOutcomes,
        pendingTrashIds, trashVerificationMessage, markTrashUncertain, settleTrash, submitTrash,
        cancelReads, clearPrivateState };
}
