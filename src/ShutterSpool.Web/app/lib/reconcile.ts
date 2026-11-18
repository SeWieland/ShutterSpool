import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { token, onAccessLost } from "./session";
import { pageWorkspaceId, withWorkspace } from './workspace-context';
import type { SourceActivity } from './source-activity';

export interface PreviewInvalidation {
    ids: Set<string>;
    all: boolean;
}

export const allRefreshScopes = ['capture', 'previews', 'sources', 'delivery', 'storage', 'erasure'] as const;
export type RefreshScope = (typeof allRefreshScopes)[number];

function scopesForCaptureChange(kind?: string): RefreshScope[] {
    switch (kind) {
        case 'erasure': return ['capture', 'erasure', 'storage'];
        case 'retention': return ['capture', 'storage'];
        case 'delivery':
        case 'routing': return ['capture', 'delivery'];
        default: return ['capture'];
    }
}

// An undefined set covers the entire scope until the next flush.
function mergeInvalidatedIds(pending: Set<string> | undefined, incoming: Set<string> | undefined) {
    if (!pending || !incoming || pending.size + incoming.size > 60) return undefined;
    for (const id of incoming) pending.add(id);
    return pending;
}

export function useReconciliation(
    refresh: (ids?: Set<string>, previews?: PreviewInvalidation, scopes?: Set<RefreshScope>) => Promise<void>,
) {
    const online = ref(navigator.onLine);
    const connected = ref(false);
    const recoverySequence = ref(0);
    const connectionRecovery = ref(0);
    const activity = ref<{ captureId: string; kind: 'captureChanged' | 'previewReady'; changeKind?: string; sequence: number }>();
    const authActivity = ref<{ sourceId: string; outcome: 'Succeeded' | 'Rejected'; observedAt: string; version: number }>();
    const phaseActivity = ref<SourceActivity>();
    const unassignedRejection = ref(false);
    let rejectionTimer: ReturnType<typeof setTimeout> | undefined;
    const authVersions = new Map<string, number>();
    let initial: Promise<void> | undefined;
    let stopped = false;
    let running = false;
    const pendingScopes = new Set<RefreshScope>();
    let affected: Set<string> | undefined = new Set();
    let previewIds: Set<string> | undefined = new Set();
    let debounce: ReturnType<typeof setTimeout> | undefined;
    const hub = new HubConnectionBuilder()
        .withUrl(withWorkspace('/live/changes', pageWorkspaceId), { accessTokenFactory: token })
        .configureLogging(LogLevel.None)
        .withAutomaticReconnect()
        .build();

    async function flush() {
        if (stopped || running || !online.value || document.hidden) return;
        running = true;
        const ids = affected;
        const previews = { ids: previewIds ?? new Set<string>(), all: !previewIds };
        const scopes = new Set(pendingScopes);
        pendingScopes.clear();
        affected = new Set();
        previewIds = new Set();
        try {
            await refresh(ids?.size ? ids : undefined, previews, scopes);
        } catch {
            // The next foreground/safety read retries without an unbounded failure loop.
        } finally {
            running = false;
            if (pendingScopes.size) queueFlush();
        }
    }
    function invalidate(scopes: RefreshScope | Iterable<RefreshScope>, ids?: Set<string>, previews?: PreviewInvalidation) {
        if (stopped) return;
        const incoming = typeof scopes === 'string' ? [scopes] : [...scopes];
        for (const scope of incoming)
            pendingScopes.add(scope);
        if (pendingScopes.size > allRefreshScopes.length) {
            pendingScopes.clear();
            for (const scope of allRefreshScopes) pendingScopes.add(scope);
        }
        if (incoming.includes('capture')) affected = mergeInvalidatedIds(affected, ids);
        if (incoming.includes('previews')) previewIds = mergeInvalidatedIds(previewIds, previews?.all ? undefined : previews?.ids);
        queueFlush();
    }
    function refreshAll() {
        invalidate(allRefreshScopes);
    }
    function queueFlush() {
        if (!debounce)
            debounce = setTimeout(() => {
                debounce = undefined;
                void flush();
            }, 200);
    }
    hub.on("captureChanged", (hint: { captureId: string; kind?: string }) => {
        if (stopped) return;
        activity.value = { ...hint, changeKind: hint.kind, kind: 'captureChanged', sequence: (activity.value?.sequence ?? 0) + 1 };
        invalidate(scopesForCaptureChange(hint.kind), new Set([hint.captureId]));
    });
    hub.on("previewReady", (hint: { captureId: string }) => {
        if (stopped) return;
        activity.value = { ...hint, kind: 'previewReady', sequence: (activity.value?.sequence ?? 0) + 1 };
        if (hint.captureId) invalidate('previews', undefined, { ids: new Set([hint.captureId]), all: false });
        else refreshAll();
    });
    hub.on("sourceAuthenticationChanged", (hint: { sourceId: string; outcome: 'Succeeded' | 'Rejected'; observedAt: string; version: number }) => {
        if (stopped || !hint.sourceId || !['Succeeded', 'Rejected'].includes(hint.outcome) || !Number.isFinite(Date.parse(hint.observedAt)) || !Number.isSafeInteger(hint.version) || hint.version <= (authVersions.get(hint.sourceId) ?? 0)) return;
        if (!authVersions.has(hint.sourceId) && authVersions.size >= 100) authVersions.delete(authVersions.keys().next().value!);
        authVersions.set(hint.sourceId, hint.version);
        authActivity.value = hint;
        invalidate('sources');
    });
    hub.on("resyncRequired", () => { connectionRecovery.value++; recoverySequence.value++; refreshAll(); });
    hub.on('sourceActivityChanged', (hint: SourceActivity) => {
        if (stopped) return;
        phaseActivity.value = hint;
        invalidate(hint.captureId ? ['sources', 'capture'] : 'sources', hint.captureId ? new Set([hint.captureId]) : undefined);
    });
    hub.on('unassignedAuthenticationActivity', (hint: { observedAt: string }) => {
        if (!Number.isFinite(Date.parse(hint.observedAt)) || document.hidden || stopped) return;
        unassignedRejection.value = true;
        clearTimeout(rejectionTimer);
        rejectionTimer = setTimeout(() => unassignedRejection.value = false, 3000);
    });
    hub.onreconnecting(() => {
        connectionRecovery.value++;
        connected.value = false;
    });
    hub.onreconnected(() => {
        if (stopped) return;
        connected.value = true;
        recoverySequence.value++;
        refreshAll();
    });
    hub.onclose(() => {
        connected.value = false;
    });
    async function start() {
        if (
            stopped ||
            !online.value ||
            document.hidden ||
            hub.state !== "Disconnected"
        )
            return;
        try {
            await hub.start();
            connected.value = true;
            recoverySequence.value++;
        } catch {
            connected.value = false;
        }
        if (stopped) {
            await hub.stop();
            return;
        }
        refreshAll();
    }
    function foreground() {
        online.value = navigator.onLine;
        if (online.value && !document.hidden) {
            recoverySequence.value++;
            void start();
            refreshAll();
        }
    }
    const interval = setInterval(() => {
        if (online.value && !document.hidden) {
            recoverySequence.value++;
            void start();
            refreshAll();
        }
    }, 40_000);
    window.addEventListener("online", foreground);
    window.addEventListener("offline", foreground);
    window.addEventListener("focus", foreground);
    document.addEventListener("visibilitychange", foreground);
    function stop() {
        stopped = true;
        connected.value = false;
        affected?.clear();
        previewIds?.clear();
        activity.value = undefined;
        authActivity.value = undefined;
        phaseActivity.value = undefined;
        unassignedRejection.value = false;
        clearTimeout(rejectionTimer);
        authVersions.clear();
        clearInterval(interval);
        clearTimeout(debounce);
        window.removeEventListener("online", foreground);
        window.removeEventListener("offline", foreground);
        window.removeEventListener("focus", foreground);
        document.removeEventListener("visibilitychange", foreground);
        void hub.stop();
    }
    const unsubscribe = onAccessLost(stop);
    onUnmounted(() => {
        stop();
        unsubscribe();
    });
    function initialize() {
        return initial ??= (async () => {
            await start();
            clearTimeout(debounce);
            debounce = undefined;
            await flush();
        })();
    }
    return { online, connected, activity, authActivity, phaseActivity, unassignedRejection, recoverySequence, connectionRecovery, initialize, invalidate, refresh: refreshAll };
}
