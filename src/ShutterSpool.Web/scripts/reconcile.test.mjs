import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { ref } from 'vue';

function reconciliation(refresh) {
    const handlers = new Map(), timers = new Map();
    let next = 0, lost, reconnect;
    const hub = { state: 'Disconnected', on: (name, callback) => handlers.set(name, callback),
        onreconnecting() {}, onreconnected(callback) { reconnect = callback; }, onclose() {},
        async start() { this.state = 'Connected'; }, async stop() { this.state = 'Disconnected'; } };
    class Builder { withUrl() { return this; } configureLogging() { return this; } withAutomaticReconnect() { return this; } build() { return hub; } }
    const context = vm.createContext({ exports: {}, ref, navigator: { onLine: true },
        document: { hidden: false, addEventListener() {}, removeEventListener() {} },
        window: { addEventListener() {}, removeEventListener() {} }, onUnmounted() {},
        setTimeout: callback => { timers.set(++next, callback); return next; }, clearTimeout: id => timers.delete(id),
        setInterval: callback => { handlers.set('safety', callback); return 1; }, clearInterval() {},
        require: name => name === '@microsoft/signalr' ? { HubConnectionBuilder: Builder, LogLevel: { None: 0 } }
            : { token: async () => '', onAccessLost: callback => { lost = callback; return () => {}; } },
    });
    vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/reconcile.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const live = context.exports.useReconciliation(refresh);
    return { live, emit: (event, hint) => handlers.get(event)(hint), reconnect: () => reconnect(), lose: () => lost(),
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
