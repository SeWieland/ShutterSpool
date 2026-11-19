import { useNuxtApp } from '#app';
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from '@microsoft/signalr';
import { mountSuspended } from '@nuxt/test-utils/runtime';
import { flushPromises } from '@vue/test-utils';
import assert from 'node:assert/strict';
import { onTestFinished, test, vi } from 'vitest';
import { defineComponent } from 'vue';
import { useReconciliation, type PreviewInvalidation, type RefreshScope } from '../../app/lib/reconcile';
import { clearPrivateAccess } from '../../app/lib/session';
import * as workspaceContext from '../../app/lib/workspace-context';

type Refresh = Parameters<typeof useReconciliation>[0];

function dispatchLifecycleEvents() {
    for (const event of ['online', 'offline', 'focus']) window.dispatchEvent(new Event(event));
    document.dispatchEvent(new Event('visibilitychange'));
}

async function reconciliation(refresh: Refresh, selectedId: string | null = null) {
    let host: { unmount(): void } | undefined;
    function unmount() {
        host?.unmount();
        host = undefined;
    }
    onTestFinished(() => {
        try { unmount(); }
        finally {
            vi.restoreAllMocks();
            vi.useRealTimers();
        }
    });

    const handlers = new Map<string, (hint?: unknown) => void>();
    let onReconnect: (() => void) | undefined;
    const hub = {
        state: HubConnectionState.Disconnected,
        on: (event: string, handler: (hint?: unknown) => void) => handlers.set(event, handler),
        onreconnecting() {},
        onreconnected(handler: () => void) { onReconnect = handler; },
        onclose() {},
        async start() { hub.state = HubConnectionState.Connected; },
        stop: vi.fn(async () => { hub.state = HubConnectionState.Disconnected; }),
    };
    const visibility = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    vi.spyOn(navigator, 'onLine', 'get').mockReturnValue(true);
    vi.spyOn(workspaceContext, 'getPageWorkspaceId').mockReturnValue(selectedId);
    const withUrl = vi.spyOn(HubConnectionBuilder.prototype, 'withUrl');
    vi.spyOn(HubConnectionBuilder.prototype, 'build').mockReturnValue(hub as unknown as HubConnection);

    let live!: ReturnType<typeof useReconciliation>;
    let lose!: () => void;
    host = await mountSuspended(defineComponent({
        setup() {
            const nuxtApp = useNuxtApp();
            vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
            live = useReconciliation(refresh);
            lose = () => nuxtApp.runWithContext(clearPrivateAccess);
            return () => null;
        },
    }), { route: '' });

    return {
        live,
        lose,
        unmount,
        hubUrl: withUrl.mock.calls[0]?.[0],
        hubStop: hub.stop,
        emit(event: string, hint?: unknown) {
            const handler = handlers.get(event);
            assert.ok(handler, `SignalR handler for ${event}`);
            handler(hint);
        },
        reconnect() {
            assert.ok(onReconnect);
            onReconnect();
        },
        setHidden: (value: boolean) => visibility.mockReturnValue(value),
        foreground: () => window.dispatchEvent(new Event('focus')),
        flush: () => vi.advanceTimersByTimeAsync(200),
        safety: () => vi.advanceTimersByTimeAsync(40_200),
    };
}

test('hub setup uses the workspace selected after the reconciliation module loads', async () => {
    const workspaceId = '2f2ed010-2a51-468a-a38a-a2372fba8211';
    const client = await reconciliation(async () => {}, workspaceId);
    assert.equal(client.hubUrl, `/live/changes?workspace=${workspaceId}`);
});

test('subscription precedes snapshot and a racing hint triggers one follow-up read', async () => {
    let reads = 0;
    const started = Promise.withResolvers<void>();
    const release = Promise.withResolvers<void>();
    const client = await reconciliation(async () => {
        if (++reads === 1) {
            started.resolve();
            await release.promise;
        }
    });
    const initial = client.live.initialize();
    await started.promise;
    client.emit('captureChanged', { captureId: 'synthetic' });
    await client.flush();
    assert.equal(reads, 1);
    release.resolve();
    await initial;
    await client.flush();
    assert.equal(reads, 2);
});

test('duplicates and older authentication versions are rejected, bursts coalesce and access loss clears activity', async () => {
    let reads = 0;
    const client = await reconciliation(async () => { reads++; });
    await client.live.initialize();
    for (const version of [3, 3, 2]) client.emit('sourceAuthenticationChanged', { sourceId: 'synthetic', outcome: 'Rejected', observedAt: '2026-10-13T12:00:00Z', version });
    assert.equal(client.live.authActivity.value?.version, 3);
    client.emit('sourceAuthenticationChanged', { sourceId: 'synthetic', outcome: 'invalid', observedAt: 'invalid', version: 4 });
    assert.equal(client.live.authActivity.value?.version, 3);
    await client.flush(); assert.equal(reads, 2);
    const recovery = client.live.recoverySequence.value;
    client.reconnect(); await client.flush(); assert.equal(reads, 3);
    assert.equal(client.live.recoverySequence.value, recovery + 1);
    assert.equal(client.live.authActivity.value?.version, 3, 'reconnect does not replay a pulse');
    client.lose();
    assert.equal(client.live.authActivity.value, undefined);
    const recoveryAfterAccessLoss = client.live.recoverySequence.value;
    dispatchLifecycleEvents();
    assert.equal(client.live.recoverySequence.value, recoveryAfterAccessLoss, 'access loss removes lifecycle listeners');
    client.emit('sourceAuthenticationChanged', { sourceId: 'synthetic', outcome: 'Succeeded', observedAt: '2026-10-13T12:00:00Z', version: 99 });
    client.emit('sourceActivityChanged', { transferId: 'late', sourceId: 'synthetic' });
    client.emit('previewReady', { captureId: 'late' });
    assert.equal(client.live.authActivity.value, undefined);
    assert.equal(client.live.phaseActivity.value, undefined);
    assert.equal(client.live.activity.value, undefined);
    await client.safety();
    assert.equal(reads, 3);
});

test('overflow stays bounded and only targeted preview hints invalidate derivatives', async () => {
    const reads: { ids?: Set<string>; previews?: PreviewInvalidation }[] = [];
    const client = await reconciliation(async (ids, previews) => {
        reads.push({ ids, previews });
    });
    await client.live.initialize();
    for (let index = 0; index < 1000; index++) client.emit('captureChanged', { captureId: String(index) });
    client.emit('previewReady', { captureId: 'ready' });
    await client.flush();
    assert.equal(reads.length, 2);
    assert.ok(reads[0].previews);
    const batch = reads.at(-1)!;
    assert.ok(batch.previews);
    assert.equal(batch.ids, undefined);
    assert.equal(batch.previews.all, false);
    assert.deepEqual([...batch.previews.ids], ['ready']);
});

test('connection recovery interrupts playback while ordinary safety reconciliation does not', async () => {
    let reads = 0;
    const client = await reconciliation(async () => { reads++; });
    await client.live.initialize();
    const initial = client.live.connectionRecovery.value;
    await client.safety();
    assert.equal(reads, 2);
    assert.equal(client.live.connectionRecovery.value, initial);
    client.emit('resyncRequired');
    assert.equal(client.live.connectionRecovery.value, initial + 1);
    await client.flush();
});

test('Capture and preview batches each keep 60 IDs and promote independently on a further hint', async () => {
    const reads: { ids?: Set<string>; previews?: PreviewInvalidation }[] = [];
    const client = await reconciliation(async (ids, previews) => {
        reads.push({ ids, previews });
    });
    await client.live.initialize();
    const ids = new Set(Array.from({ length: 60 }, (_, index) => String(index)));
    for (const [event, captureId] of [
        [], ['captureChanged', '0'], ['captureChanged', 'new'], ['previewReady', '0'], ['previewReady', 'new'],
    ]) {
        reads.length = 0;
        client.live.invalidate(['capture', 'previews'], ids, { ids, all: false });
        if (event) client.emit(event, { captureId });
        await client.flush();
        assert.equal(reads.length, 1);
        const [batch] = reads;
        assert.ok(batch.previews);
        assert.deepEqual(batch.ids && [...batch.ids], event === 'captureChanged' ? undefined : [...ids]);
        assert.equal(batch.previews.all, event === 'previewReady');
        assert.deepEqual([...batch.previews.ids], event === 'previewReady' ? [] : [...ids]);
    }
});

test('hints invalidate only their scopes and unrelated bursts merge into one read', async () => {
    const reads: { ids?: Set<string>; previews?: PreviewInvalidation; scopes?: RefreshScope[] }[] = [];
    const client = await reconciliation(async (ids, previews, scopes) => {
        reads.push({ ids, previews, scopes: scopes && [...scopes].sort() });
    });
    await client.live.initialize();
    reads.length = 0;
    client.emit('captureChanged', { captureId: 'capture-a', kind: 'capture' });
    client.emit('previewReady', { captureId: 'capture-b' });
    client.emit('sourceAuthenticationChanged', { sourceId: 'source-a', outcome: 'Rejected', observedAt: '2026-10-13T12:00:00Z', version: 1 });
    await client.flush();
    assert.equal(reads.length, 1);
    assert.deepEqual(reads[0].scopes, ['capture', 'previews', 'sources']);
    assert.deepEqual([...reads[0].ids!], ['capture-a']);
    assert.ok(reads[0].previews);
    assert.deepEqual([...reads[0].previews.ids], ['capture-b']);
    reads.length = 0;
    client.emit('captureChanged', { captureId: 'capture-c', kind: 'delivery' });
    client.emit('captureChanged', { captureId: 'capture-d', kind: 'erasure' });
    await client.flush();
    assert.ok(reads[0].previews);
    assert.deepEqual(reads[0].scopes, ['capture', 'delivery', 'erasure', 'storage']);
    assert.deepEqual([...reads[0].ids!].sort(), ['capture-c', 'capture-d']);
    reads.length = 0;
    client.emit('captureChanged', { captureId: 'capture-e', kind: 'routing' });
    client.emit('captureChanged', { captureId: 'capture-f', kind: 'retention' });
    await client.flush();
    assert.ok(reads[0].previews);
    assert.deepEqual(reads[0].scopes, ['capture', 'delivery', 'storage']);
    reads.length = 0;
    client.live.refresh();
    await client.flush();
    assert.deepEqual(reads[0].scopes, ['capture', 'delivery', 'erasure', 'previews', 'sources', 'storage']);
    assert.equal(reads[0].previews?.all, true, 'broad recovery invalidates visible preview URLs');
    reads.length = 0;
    client.emit('previewReady', { captureId: '' });
    await client.flush();
    assert.deepEqual(reads[0].scopes, ['capture', 'delivery', 'erasure', 'previews', 'sources', 'storage']);
    assert.equal(reads[0].previews?.all, true);
});

test('active refreshes stay serial and merge an invalidation received during a read', async () => {
    const release = Promise.withResolvers<void>();
    let active = 0, peak = 0;
    const reads: (RefreshScope[] | undefined)[] = [];
    let holdSources = false;
    const client = await reconciliation(async (_ids, _previews, scopes) => {
        active++;
        peak = Math.max(peak, active);
        reads.push(scopes && [...scopes].sort());
        if (holdSources && scopes?.has('sources')) await release.promise;
        active--;
    });
    await client.live.initialize();
    reads.length = 0;
    holdSources = true;
    client.live.invalidate('sources');
    await client.flush();
    assert.equal(active, 1);
    client.live.invalidate('delivery');
    release.resolve();
    await flushPromises();
    await client.flush();
    assert.equal(peak, 1);
    assert.deepEqual(reads, [['sources'], ['delivery']]);
});

test('foreground recovery schedules stale scopes and disposal removes lifecycle listeners', async () => {
    let reads = 0;
    const client = await reconciliation(async () => { reads++; });
    await client.live.initialize();
    const before = reads;
    client.setHidden(true);
    client.live.invalidate('erasure');
    await client.flush();
    assert.equal(reads, before);
    client.setHidden(false);
    client.foreground();
    await client.flush();
    assert.equal(reads, before + 1);
    const recovery = client.live.recoverySequence.value;
    client.unmount();
    assert.equal(client.hubStop.mock.calls.length, 1);
    client.lose();
    assert.equal(client.hubStop.mock.calls.length, 1, 'unmount unsubscribes from access loss');
    dispatchLifecycleEvents();
    assert.equal(client.live.recoverySequence.value, recovery, 'unmount removes lifecycle listeners');
    client.emit('captureChanged', { captureId: 'late' });
    await client.flush();
    assert.equal(reads, before + 1);
});
