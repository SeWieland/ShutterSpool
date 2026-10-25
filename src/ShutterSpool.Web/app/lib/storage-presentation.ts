import { durationHours } from './retention';
type Capacity = { [key in 'usedBytes' | 'activeBytes' | 'trashBytes' | 'diskPreviewBytes' | 'transientBytes' | 'quarantineBytes' | 'reservedBytes' | 'quotaBytes']?: number | null } & { unknownAccounting?: boolean | null };
export function capacityComposition(snapshot: Capacity | undefined, quotaActive: boolean) {
    const bytes = (value: unknown): value is number => typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
    const fields = ['usedBytes', 'activeBytes', 'trashBytes', 'diskPreviewBytes', 'transientBytes', 'quarantineBytes', 'reservedBytes'] as const;
    const known = !!snapshot && snapshot.unknownAccounting === false && fields.every(key => bytes(snapshot[key]))
        && snapshot.usedBytes === snapshot.activeBytes! + snapshot.trashBytes! + snapshot.diskPreviewBytes! + snapshot.transientBytes! + snapshot.quarantineBytes!
        && Number.isSafeInteger(snapshot.usedBytes! + snapshot.reservedBytes!)
        && (!quotaActive || bytes(snapshot.quotaBytes));
    if (!known || !snapshot) return { known: false, total: undefined, remaining: undefined, excess: undefined, segments: [] as { key: string; label: string; bytes: number; explanation: string }[] };
    const occupied = snapshot.usedBytes! + snapshot.reservedBytes!;
    const remaining = quotaActive ? Math.max(0, snapshot.quotaBytes! - occupied) : undefined;
    const segments = [
        { key: 'photographs', label: 'Photographs', bytes: snapshot.activeBytes!, explanation: 'Accepted originals outside Trash, including Favorites and protected photographs.' },
        { key: 'trash', label: 'Trash', bytes: snapshot.trashBytes!, explanation: 'Recoverable originals still occupy storage until physical removal finishes.' },
        { key: 'previews', label: 'Disk previews', bytes: snapshot.diskPreviewBytes!, explanation: 'Regenerable previews stored on disk. Originals are separate.' },
        { key: 'temporary', label: 'Temporary / quarantined', bytes: snapshot.transientBytes! + snapshot.quarantineBytes!, explanation: 'Temporary uploads and quarantined files awaiting safe reconciliation.' },
        { key: 'reserved', label: 'Reserved', bytes: snapshot.reservedBytes!, explanation: 'Ongoing commitments for bytes not yet written; this is not additional photograph usage.' },
        ...(remaining === undefined ? [] : [{ key: 'remaining', label: 'Remaining allowance', bytes: remaining, explanation: 'Quota allowance after usage and reservations. This is not guaranteed free disk space.' }]),
    ];
    return { known: true, total: quotaActive ? Math.max(snapshot.quotaBytes!, occupied) : occupied, remaining, excess: quotaActive ? Math.max(0, occupied - snapshot.quotaBytes!) : undefined, segments };
}

const object = (value: unknown): Record<string, unknown> => value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : {};
const at = (value: unknown, path: string) => path.split('.').reduce<unknown>((current, key) => object(current)[key], value);
const quotaEffect = 'The saved limit controls admission immediately. New uploads may pause when usage plus reservations reaches it; recovery commitments are unchanged.';
const ageEffect = 'Applies to future arrivals. Existing photographs retain their saved cleanup schedules.';
const recoveryEffect = 'Applies to subsequent actions. Existing recovery and protection commitments stay unchanged.';
const previewEffect = 'A server restart is required. Memory mode does not remove existing disk previews.';
export type SettingFormat = 'boolean' | 'bytes' | 'duration' | 'cache';
type SettingField = readonly [path: string, label: string, format: SettingFormat, effect: string];
const fields: readonly SettingField[] = [
    ['quota.isActive', 'Storage limit', 'boolean', quotaEffect], ['quota.quotaBytes', 'Storage limit size', 'bytes', quotaEffect],
    ['quota.highWatermarkBytes', 'Start cleanup above', 'bytes', 'Sets when space-saving cleanup is considered. Protected photographs remain protected.'],
    ['quota.recoveryTargetBytes', 'Cleanup target', 'bytes', 'Cleanup aims for this level; eligible files and actual removal determine the space recovered.'],
    ['settings.automaticRemovalEnabled', 'Automatic cleanup', 'boolean', 'Allows eligible photographs to move to Trash. Moving to Trash does not immediately release space.'],
    ['settings.trashRecoveryWindow', 'Trash recovery', 'duration', recoveryEffect], ['settings.restoreProtection', 'Protection after Restore', 'duration', recoveryEffect],
    ['settings.galleryMaximum', 'Maximum gallery protection', 'duration', recoveryEffect],
    ['previewCache.cacheMode', 'Preview storage', 'cache', previewEffect], ['previewCache.memoryCacheBytes', 'Preview memory budget', 'bytes', previewEffect],
    ...([['confirmed', 'Confirmed deliveries'], ['noDelivery', 'No delivery intended'], ['waiting', 'Waiting or unmatched'], ['heldOrUnfinished', 'Held or unfinished']] as const).flatMap<SettingField>(([key, label]) => [
        [`settings.${key}.pressureAge`, `${label} · space-saving age`, 'duration', ageEffect],
        [`settings.${key}.ordinaryAge`, `${label} · regular age`, 'duration', ageEffect],
    ]),
];
export function settingsChanges(before: unknown, after: unknown) {
    return fields.flatMap(([path, label, format, explanation]) => {
        const previous = at(before, path), proposed = at(after, path);
        const normalize = (value: unknown) => format === 'duration' ? durationHours(typeof value === 'string' ? value : undefined) ?? value : value;
        if (normalize(previous) === normalize(proposed)) return [];
        let effect = explanation;
        if (path === 'quota.isActive') effect = proposed ? 'Enables a quota limit for new uploads, including existing occupancy and reservations. Uploads can pause if the limit is already reached.' : 'Disables this quota limit for new uploads. Storage health and other admission checks still apply.';
        if (path === 'settings.automaticRemovalEnabled') effect = proposed ? 'Enables automatic cleanup of eligible photographs into recoverable Trash. Moving to Trash does not immediately release space.' : 'Disables automatic cleanup under this policy. Existing recovery commitments remain unchanged.';
        if (path === 'quota.quotaBytes' && typeof previous === 'number' && typeof proposed === 'number') effect = `${proposed < previous ? 'Lowers' : 'Raises'} the quota limit for new uploads. Current usage plus reservations counts immediately; recovery commitments are unchanged.`;
        if (format === 'duration' && typeof normalize(previous) === 'number' && typeof normalize(proposed) === 'number') effect = `${Number(normalize(proposed)) < Number(normalize(previous)) ? 'Shortens' : 'Lengthens'} this interval. ${explanation}`;
        return [{ path, label, format, effect, before: previous, after: proposed }];
    });
}
export function reviewEstimates(impact: { complete?: boolean | null; eligibleCaptureCount?: number | null; plannedTrashCaptureCount?: number | null; plannedBytes?: number | null } | undefined) {
    return {
        eligibleCaptureCount: impact?.eligibleCaptureCount ?? null,
        plannedTrashCaptureCount: impact?.complete === true ? impact.plannedTrashCaptureCount ?? null : null,
        plannedBytes: impact?.complete === true ? impact.plannedBytes ?? null : null,
    };
}

const messages: Record<string, string> = {
    'accounting-unknown': 'Storage usage is not confirmed. Check the spool root and refresh before relying on these estimates.',
    'quota-not-active': 'A storage limit is not active. Usage is shown without a quota percentage.',
    'admission-blocked': 'New photographs cannot be accepted right now. Check the storage limit and accounting.',
    'already-over-quota': 'Current usage and reservations already exceed the saved storage limit.',
    'protected-occupancy': 'Some space is protected. Cleanup cannot remove those originals while their protection applies.',
    'eligibility-classification-incomplete': 'Some photographs could not be classified. Eligibility and space estimates are incomplete.',
    'planned-trash-scan-bounded': 'The cleanup plan covers a bounded set of photographs. More may become eligible later.',
    'existing-assets-retain-policy-revision': ageEffect,
    unknown_accounting: 'Storage usage is not confirmed. Check the spool root and refresh accounting before resuming uploads.',
    quota_capacity: 'Usage and reservations have reached the storage limit. Reclaim eligible space or review the limit before resuming uploads.',
    filesystem_reserve: 'The filesystem safety reserve cannot be met. Restore free disk space or resolve its unavailable accounting before resuming uploads.',
    PRESERVATION_HOLD_ACTIVE: 'A preservation hold must be explicitly resolved by its authority before removal can continue. A review date does not end the hold.',
    DELIVERY_RECONCILIATION_REQUIRED: 'Delivery is running or its result is uncertain. Reconcile delivery before continuing removal.',
    CAMERA_RECEIVE_RECONCILIATION_REQUIRED: 'A camera upload is still active. Finish or reconcile that receive before continuing.',
    INGEST_RECONCILIATION_REQUIRED: 'Incoming-file state needs reconciliation before removal can continue.',
    ORIGINAL_REMOVAL_NOT_AUTHORIZED: 'Original removal is not authorized by the current protection evidence. Refresh the photograph before proceeding.',
    REMOVAL_ALREADY_OWNED: 'Another removal operation owns this original. Check that operation before continuing.',
    ERASURE_ALREADY_OWNS_ASSET: 'Another privacy erasure request already owns this original. Check its saved progress.',
    ORIGINAL_UNAVAILABLE: 'An original could not be verified. Check the spool and refresh its removal evidence.',
    TRANSIENT_COPY_SET_UNBOUNDED: 'The complete set of temporary copies could not be established. Reconcile storage before continuing.',
    SPOOL_STORAGE_IDENTITY_UNKNOWN: 'The spool storage identity is not confirmed. Restore access to the configured spool before continuing.',
    REVISION_CONFLICT: 'The photograph changed after review. Refresh it and review the exact selection again.',
    SELECTION_CHANGED: 'The original-file selection changed. Refresh and review all variants again.',
    CAPTURE_UNAVAILABLE: 'The photograph is no longer available. Check the existing request for its removal progress.',
    OPERATION_SCOPE_MISMATCH: 'This request identity belongs to a different selection. Check the saved request instead of repeating it.',
    ERASURE_NOT_FOUND: 'This request could not be found. Check its identifier and refresh the request list.',
    RESTORE_ERASURE_BLOCKED: 'Restore is blocked by an existing privacy erasure request. Check its progress.',
    RESTORE_ERASURE_PENDING: 'Privacy erasure is pending; this photograph cannot be restored.',
    GALLERY_PROMISE_ACTIVE: 'An active gallery promise protects these originals until its commitment is resolved.',
};
export function storageMessage(code: string | null | undefined) {
    return code ? messages[code] ?? 'The server reported an additional condition. Check Details and resolve it before continuing.' : '';
}
export function surfaceProgress(status: string | null | undefined) {
    if (status === 'Completed') return { label: 'Completed', icon: 'i-lucide-check', tone: 'complete' };
    if (status === 'Blocked') return { label: 'Blocked', icon: 'i-lucide-shield-alert', tone: 'blocked' };
    if (status === 'Pending') return { label: 'Pending', icon: 'i-lucide-clock', tone: 'pending' };
    return { label: 'Not confirmed', icon: 'i-lucide-circle-help', tone: 'unknown' };
}
export function operationStatus(status: string | null | undefined) {
    const labels: Record<string, string> = { Pending: 'Removal in progress', Completed: 'Removal completed', Blocked: 'Removal blocked', Conflict: 'Review changed selection', NotFound: 'Request unavailable' };
    return status ? labels[status] ?? 'Status not recognized; check Details' : 'Request not yet confirmed';
}
export function historyStatus(value: string | null | undefined) {
    const labels: Record<string, string> = { pressure: 'Storage pressure', selection: 'Photographs selected for cleanup', removal: 'Original removal', age: 'Regular cleanup age reached', authorized: 'Removal authorized', completed: 'Removal completed', failed: 'Removal needs attention', 'capture-purged': 'All local originals removed', Normal: 'Within storage target', Pressure: 'Storage pressure', Recovering: 'Recovering space', Blocked: 'Cleanup blocked', Unknown: 'Storage state not confirmed', Started: 'Cleanup started', Completed: 'Cleanup completed', Planned: 'Cleanup planned', Active: 'Space-saving cleanup active', Idle: 'No active cleanup', Recovered: 'Storage target recovered', AdmissionBlocked: 'Uploads paused', Planning: 'Planning space-saving cleanup', ObservationUnknown: 'Current storage observation unavailable', AutomaticRemovalDisabled: 'Automatic cleanup is off', ReclamationFailed: 'Space reclamation needs attention', Reclaiming: 'Removing files to reclaim space', ReclamationStalled: 'No further space reclaimed yet', BelowHighWatermark: 'Below cleanup starting point; recovery target not yet reached' };
    return value ? labels[value] ?? 'Additional cleanup event; see Details' : 'Status not confirmed';
}
