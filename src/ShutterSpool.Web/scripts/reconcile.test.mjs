import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { ref } from 'vue';

function reconciliation(refresh) {
    const handlers = new Map(), timers = new Map(), listeners = new Map();
    const navigator = { onLine: true }, document = { hidden: false };
    let next = 0, lost, reconnect;
    const hub = { state: 'Disconnected', on: (name, callback) => handlers.set(name, callback),
        onreconnecting() {}, onreconnected(callback) { reconnect = callback; }, onclose() {},
        async start() { this.state = 'Connected'; }, async stop() { this.state = 'Disconnected'; } };
    class Builder { withUrl() { return this; } configureLogging() { return this; } withAutomaticReconnect() { return this; } build() { return hub; } }
    Object.assign(document, { addEventListener: (name, callback) => listeners.set(`document:${name}`, callback), removeEventListener: name => listeners.delete(`document:${name}`) });
    const context = vm.createContext({ exports: {}, ref, navigator,
        document,
        window: { addEventListener: (name, callback) => listeners.set(`window:${name}`, callback), removeEventListener: name => listeners.delete(`window:${name}`) }, onUnmounted() {},
        setTimeout: callback => { timers.set(++next, callback); return next; }, clearTimeout: id => timers.delete(id),
        setInterval: callback => { handlers.set('safety', callback); return 1; }, clearInterval() {},
        require: name => name === '@microsoft/signalr' ? { HubConnectionBuilder: Builder, LogLevel: { None: 0 } }
            : name.endsWith('/workspace-context') ? { pageWorkspaceId: null, withWorkspace: path => path }
                : { token: async () => '', onAccessLost: callback => { lost = callback; return () => {}; } },
    });
    vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/reconcile.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const live = context.exports.useReconciliation(refresh);
    return { live, emit: (event, hint) => handlers.get(event)(hint), reconnect: () => reconnect(), lose: () => lost(), listeners,
        setOnline: value => { navigator.onLine = value; }, setHidden: value => { document.hidden = value; },
        foreground: () => listeners.get('window:focus')(),
        async flush() { const pending = [...timers.values()]; timers.clear(); pending.forEach(callback => callback()); await new Promise(resolve => setImmediate(resolve)); } };
}

test('subscription precedes snapshot and a racing hint triggers one follow-up read', async () => {
    let release, reads = 0;
    const client = reconciliation(async () => { reads++; if (reads === 1) await new Promise(resolve => { release = resolve; }); });
    const initial = client.live.initialize();
    await new Promise(resolve => setImmediate(resolve));
    client.emit('captureChanged', { captureId: 'synthetic' });
    await client.flush();
    assert.equal(reads, 1);
    release(); await initial; await client.flush();
    assert.equal(reads, 2);
});

test('duplicates and older authentication versions are rejected, bursts coalesce and access loss clears activity', async () => {
    let reads = 0;
    const client = reconciliation(async () => { reads++; });
    await client.live.initialize();
    for (const version of [3, 3, 2]) client.emit('sourceAuthenticationChanged', { sourceId: 'synthetic', outcome: 'Rejected', observedAt: '2026-10-13T12:00:00Z', version });
    assert.equal(client.live.authActivity.value.version, 3);
    client.emit('sourceAuthenticationChanged', { sourceId: 'synthetic', outcome: 'invalid', observedAt: 'invalid', version: 4 });
    assert.equal(client.live.authActivity.value.version, 3);
    await client.flush(); assert.equal(reads, 2);
    const recovery = client.live.recoverySequence.value;
    client.reconnect(); await client.flush(); assert.equal(reads, 3);
    assert.equal(client.live.recoverySequence.value, recovery + 1);
    assert.equal(client.live.authActivity.value.version, 3, 'reconnect does not replay a pulse');
    client.lose(); assert.equal(client.live.authActivity.value, undefined);
    client.emit('sourceAuthenticationChanged', { sourceId: 'synthetic', outcome: 'Succeeded', observedAt: '2026-10-13T12:00:00Z', version: 99 });
    client.emit('sourceActivityChanged', { transferId: 'late', sourceId: 'synthetic' });
    client.emit('previewReady', { captureId: 'late' });
    assert.equal(client.live.authActivity.value, undefined);
    assert.equal(client.live.phaseActivity.value, undefined);
    assert.equal(client.live.activity.value, undefined);
    client.emit('safety'); await client.flush(); assert.equal(reads, 3);
});

test('overflow stays bounded and only targeted preview hints invalidate derivatives', async () => {
    const reads = [];
    const client = reconciliation(async (ids, previews) => reads.push({ ids, previews }));
    await client.live.initialize();
    for (let index = 0; index < 1000; index++) client.emit('captureChanged', { captureId: String(index) });
    client.emit('previewReady', { captureId: 'ready' });
    await client.flush();
    assert.equal(reads.at(-1).ids, undefined);
    assert.equal(reads.at(-1).previews.all, false);
    assert.deepEqual([...reads.at(-1).previews.ids], ['ready']);
});

test('connection recovery interrupts playback while ordinary safety reconciliation does not', async () => {
    const client = reconciliation(async () => {});
    await client.live.initialize();
    const initial = client.live.connectionRecovery.value;
    client.emit('safety');
    await client.flush();
    assert.equal(client.live.connectionRecovery.value, initial);
    client.emit('resyncRequired');
    assert.equal(client.live.connectionRecovery.value, initial + 1);
    await client.flush();
});

test('hints invalidate only their scopes and unrelated bursts merge into one read', async () => {
    const reads = [];
    const client = reconciliation(async (ids, previews, scopes) => reads.push({ ids, previews, scopes: [...scopes].sort() }));
    await client.live.initialize();
    reads.length = 0;
    client.emit('captureChanged', { captureId: 'capture-a', kind: 'capture' });
    client.emit('previewReady', { captureId: 'capture-b' });
    client.emit('sourceAuthenticationChanged', { sourceId: 'source-a', outcome: 'Rejected', observedAt: '2026-10-13T12:00:00Z', version: 1 });
    await client.flush();
    assert.equal(reads.length, 1);
    assert.deepEqual(reads[0].scopes, ['capture', 'previews', 'sources']);
    assert.deepEqual([...reads[0].ids], ['capture-a']);
    assert.deepEqual([...reads[0].previews.ids], ['capture-b']);
    reads.length = 0;
    client.emit('captureChanged', { captureId: 'capture-c', kind: 'delivery' });
    client.emit('captureChanged', { captureId: 'capture-d', kind: 'erasure' });
    await client.flush();
    assert.deepEqual(reads[0].scopes, ['capture', 'delivery', 'erasure', 'storage']);
    assert.deepEqual([...reads[0].ids].sort(), ['capture-c', 'capture-d']);
    reads.length = 0;
    client.emit('captureChanged', { captureId: 'capture-e', kind: 'routing' });
    client.emit('captureChanged', { captureId: 'capture-f', kind: 'retention' });
    await client.flush();
    assert.deepEqual(reads[0].scopes, ['capture', 'delivery', 'storage']);
    reads.length = 0;
    client.live.refresh();
    await client.flush();
    assert.deepEqual(reads[0].scopes, ['capture', 'delivery', 'erasure', 'previews', 'sources', 'storage']);
    assert.equal(reads[0].previews.all, true, 'broad recovery invalidates visible preview URLs');
});

test('active refreshes stay serial and merge an invalidation received during a read', async () => {
    let release;
    let active = 0, peak = 0;
    const reads = [];
    let holdSources = false;
    const client = reconciliation(async (_ids, _previews, scopes) => {
        active++;
        peak = Math.max(peak, active);
        reads.push([...scopes].sort());
        if (holdSources && scopes.has('sources')) await new Promise(resolve => { release = resolve; });
        active--;
    });
    await client.live.initialize();
    reads.length = 0;
    holdSources = true;
    client.live.invalidate('sources');
    await client.flush();
    client.live.invalidate('delivery');
    release();
    await new Promise(resolve => setImmediate(resolve));
    await client.flush();
    assert.equal(peak, 1);
    assert.deepEqual(reads, [['sources'], ['delivery']]);
});

test('foreground recovery schedules stale scopes and disposal removes lifecycle listeners', async () => {
    let reads = 0;
    const client = reconciliation(async () => { reads++; });
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
    client.lose();
    assert.equal(client.listeners.size, 0);
    client.emit('captureChanged', { captureId: 'late' });
    await client.flush();
    assert.equal(reads, before + 1);
});
