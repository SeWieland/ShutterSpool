export interface SourceActivity {
    transferId: string;
    sourceId: string;
    assetId?: string;
    captureId?: string;
    phase: 'Receiving' | 'AcceptedAwaitingGrouping' | 'PreparingPreview' | 'Ready' | 'FailedUnavailable' | 'Stale';
    observedAt: string;
    version: number;
    accepted: boolean;
    previewReady: boolean;
    epoch?: string;
}

export class SourceActivityState {
    epoch = '';
    items = new Map<string, SourceActivity>();
    available = false;
    coverageComplete = false;
    revision = 0;
    private hintOrder = new Map<string, { version: number; observedAt: string; revision: number }>();

    clear() {
        this.items.clear();
        this.hintOrder.clear();
        this.epoch = '';
        this.available = false;
        this.coverageComplete = false;
    }

    observe(hint: SourceActivity) {
        if (!hint.transferId || !hint.sourceId || !Number.isSafeInteger(hint.version) || !Number.isFinite(Date.parse(hint.observedAt))) return;
        if (hint.epoch && this.epoch && hint.epoch !== this.epoch) return;
        if (hint.epoch && !this.epoch) this.epoch = hint.epoch;
        const previous = this.items.get(hint.transferId);
        const ordering = this.hintOrder.get(hint.transferId);
        if (ordering && (hint.version < ordering.version || hint.version === ordering.version && Date.parse(hint.observedAt) <= Date.parse(ordering.observedAt))) return;
        if (!previous && this.items.size >= 100) return;
        this.hintOrder.set(hint.transferId, { version: hint.version, observedAt: hint.observedAt, revision: ++this.revision });
        if (previous?.accepted && hint.phase === 'Receiving') return;
        // Only authorized snapshots establish accepted identities and current derivative readiness.
        this.items.set(hint.transferId, { ...hint, assetId: previous?.assetId, captureId: previous?.captureId,
            accepted: previous?.accepted ?? false, previewReady: previous?.previewReady ?? false });
    }

    reconcile(snapshot: { epoch: string; coverageComplete: boolean; items: SourceActivity[] }, sourceIds: string[], readRevision = this.revision) {
        if (this.epoch && this.epoch !== snapshot.epoch) { this.items.clear(); this.hintOrder.clear(); }
        this.epoch = snapshot.epoch;
        const next = new Map<string, SourceActivity>();
        for (const saved of snapshot.items.slice(0, 100)) {
            if (!sourceIds.includes(saved.sourceId)) continue;
            const hint = this.items.get(saved.transferId);
            const raced = (this.hintOrder.get(saved.transferId)?.revision ?? 0) > readRevision;
            const current = hint && raced && !(saved.accepted && hint.phase === 'Receiving') ? hint : saved;
            next.set(saved.transferId, { ...current, assetId: saved.assetId ?? hint?.assetId,
                captureId: saved.captureId ?? hint?.captureId, accepted: saved.accepted || !!hint?.accepted,
                previewReady: saved.previewReady });
        }
        // A newer observation can race the snapshot; the follow-up read reconciles its mapping.
        for (const [id, hint] of this.items) if (!next.has(id) && sourceIds.includes(hint.sourceId) && next.size < 100 && !hint.accepted && (this.hintOrder.get(id)?.revision ?? 0) > readRevision)
            next.set(id, hint);
        this.items = next;
        for (const id of this.hintOrder.keys()) if (!next.has(id)) this.hintOrder.delete(id);
        this.available = true;
        this.coverageComplete = snapshot.coverageComplete;
    }
}

export type SourceActivityTone = 'receiving' | 'preparing' | 'received' | 'ready' | 'failed' | 'stale' | 'neutral';

export function sourceStatusPosition(anchor: { x: number; y: number }, size: { width: number; height: number }, viewport: { width: number; height: number }) {
    const left = anchor.x + size.width > viewport.width - 16 ? anchor.x - size.width : anchor.x;
    const top = anchor.y + size.height > viewport.height - 16 ? anchor.y - size.height : anchor.y;
    return { left: Math.max(16, Math.min(left, viewport.width - size.width - 16)), top: Math.max(16, Math.min(top, viewport.height - size.height - 16)) };
}

export function sourceActivityCounters(observations: Iterable<SourceActivity & { tone: SourceActivityTone }>, available: boolean) {
    if (!available) return { receiving: null, previewPending: null, received: null };
    let receiving = 0;
    const pending = new Set<string>();
    const received = new Set<string>();
    for (const item of observations) {
        if (item.tone === 'receiving') receiving++;
        if (item.tone === 'preparing' || item.accepted && !item.captureId && !['Stale', 'FailedUnavailable'].includes(item.phase) && item.tone !== 'stale')
            pending.add(item.captureId ?? item.transferId);
        if (item.accepted) received.add(item.assetId ?? item.transferId);
    }
    return { receiving, previewPending: pending.size, received: received.size };
}

export function activityPresentation(item: SourceActivity, now: number, connected: boolean, available: boolean): { label: string; active: boolean; tone: SourceActivityTone } {
    if (!connected || !available || ['Receiving', 'PreparingPreview'].includes(item.phase) && now - Date.parse(item.observedAt) >= 60_000)
        return { label: 'Activity stale · state unknown', active: false, tone: 'stale' };
    if (item.accepted && !item.captureId) return { label: 'Received · awaiting grouping', active: false, tone: 'received' };
    if (item.previewReady) return { label: 'Preview ready', active: false, tone: 'ready' };
    if (item.phase === 'Receiving' && !item.accepted) return { label: 'Receiving · observation', active: true, tone: 'receiving' };
    if (item.phase === 'PreparingPreview' && item.captureId) return { label: 'Preparing preview · observation', active: true, tone: 'preparing' };
    if (item.phase === 'FailedUnavailable') return { label: item.accepted ? 'Preview unavailable · original received' : 'Transfer unavailable · acceptance unknown', active: false, tone: 'failed' };
    if (item.phase === 'Stale') return { label: 'Activity stale · state unknown', active: false, tone: 'stale' };
    return { label: item.accepted ? 'Received · preview not available' : 'Verifying received state', active: false, tone: item.accepted ? 'received' : 'neutral' };
}
