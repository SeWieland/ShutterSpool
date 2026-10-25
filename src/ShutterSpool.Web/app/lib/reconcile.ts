import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { token, onAccessLost } from "./session";
import type { SourceActivity } from './source-activity';

export interface PreviewInvalidation {
    ids: Set<string>;
    all: boolean;
}

export function useReconciliation(
    refresh: (ids?: Set<string>, previews?: PreviewInvalidation) => Promise<void>,
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
    let broad = false;
    const affected = new Set<string>();
    const previewIds = new Set<string>();
    let allPreviews = false;
    let debounce: ReturnType<typeof setTimeout> | undefined;
    const hub = new HubConnectionBuilder()
        .withUrl("/live/changes", { accessTokenFactory: token })
        .configureLogging(LogLevel.None)
        .withAutomaticReconnect()
        .build();

    async function flush() {
        if (stopped || running || !online.value || document.hidden) return;
        running = true;
        const ids = broad ? undefined : new Set(affected);
        const previews = { ids: new Set(previewIds), all: allPreviews };
        broad = false;
        affected.clear();
        previewIds.clear();
        allPreviews = false;
        try {
            await refresh(ids?.size ? ids : undefined, previews);
        } catch {
            // The next foreground/safety read retries without an unbounded failure loop.
        } finally {
            running = false;
            if (broad || affected.size || previewIds.size || allPreviews)
                queueFlush();
        }
    }
    function schedule(id?: string, previewReady = false) {
        if (stopped) return;
        if (!id || affected.size >= 60) {
            broad = true;
            affected.clear();
        } else affected.add(id);
        if (previewReady && id) {
            if (previewIds.size >= 60) {
                allPreviews = true;
                previewIds.clear();
            }
            else if (!allPreviews) previewIds.add(id);
        }
        queueFlush();
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
        schedule(hint.captureId);
    });
    hub.on("previewReady", (hint: { captureId: string }) => {
        if (stopped) return;
        activity.value = { ...hint, kind: 'previewReady', sequence: (activity.value?.sequence ?? 0) + 1 };
        schedule(hint.captureId, true);
    });
    hub.on("sourceAuthenticationChanged", (hint: { sourceId: string; outcome: 'Succeeded' | 'Rejected'; observedAt: string; version: number }) => {
        if (stopped || !hint.sourceId || !['Succeeded', 'Rejected'].includes(hint.outcome) || !Number.isFinite(Date.parse(hint.observedAt)) || !Number.isSafeInteger(hint.version) || hint.version <= (authVersions.get(hint.sourceId) ?? 0)) return;
        if (!authVersions.has(hint.sourceId) && authVersions.size >= 100) authVersions.delete(authVersions.keys().next().value!);
        authVersions.set(hint.sourceId, hint.version);
        authActivity.value = hint;
        schedule();
    });
    hub.on("resyncRequired", () => { connectionRecovery.value++; recoverySequence.value++; schedule(); });
    hub.on('sourceActivityChanged', (hint: SourceActivity) => {
        if (stopped) return;
        phaseActivity.value = hint;
        schedule(hint.captureId);
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
        schedule();
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
        schedule();
    }
    function foreground() {
        online.value = navigator.onLine;
        if (online.value && !document.hidden) {
            recoverySequence.value++;
            void start();
            schedule();
        }
    }
    const interval = setInterval(() => {
        if (online.value && !document.hidden) {
            recoverySequence.value++;
            void start();
            schedule();
        }
    }, 60_000);
    window.addEventListener("online", foreground);
    window.addEventListener("offline", foreground);
    window.addEventListener("focus", foreground);
    document.addEventListener("visibilitychange", foreground);
    function stop() {
        stopped = true;
        connected.value = false;
        affected.clear();
        previewIds.clear();
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
    return { online, connected, activity, authActivity, phaseActivity, unassignedRejection, recoverySequence, connectionRecovery, initialize, refresh: () => schedule() };
}
