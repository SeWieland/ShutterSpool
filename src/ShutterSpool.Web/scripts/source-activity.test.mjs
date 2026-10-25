import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
const context = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/source-activity.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
const { SourceActivityState, activityPresentation, sourceActivityCounters, sourceStatusPosition } = context.exports;
const item = (transferId, patch = {}) => ({ transferId, sourceId: 'synthetic', phase: 'Receiving', observedAt: '2026-10-13T12:00:00Z', version: 1, accepted: false, previewReady: false, epoch: 'epoch-one', ...patch });

test('status popup starts at its anchor and flips/clamps at desktop and phone edges', () => {
    const normal = sourceStatusPosition({ x: 80, y: 100 }, { width: 420, height: 300 }, { width: 1440, height: 900 });
    assert.equal(normal.left, 80); assert.equal(normal.top, 100);
    const edge = sourceStatusPosition({ x: 1400, y: 870 }, { width: 420, height: 300 }, { width: 1440, height: 900 });
    assert.equal(edge.left, 980); assert.equal(edge.top, 570);
    const phone = sourceStatusPosition({ x: 57, y: 280 }, { width: 288, height: 480 }, { width: 320, height: 700 });
    assert.equal(phone.left, 16); assert.equal(phone.top, 16);
    const rotated = sourceStatusPosition({ x: 700, y: 600 }, { width: 288, height: 480 }, { width: 320, height: 600 });
    assert.equal(rotated.left, 16); assert.equal(rotated.top, 104);
});

test('authoritative snapshots recover Stale to Receiving and demote Ready after derivative cache loss', () => {
    const state = new SourceActivityState();
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { phase: 'Stale', version: 2 })] }, ['synthetic']);
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { phase: 'Receiving', version: 0, observedAt: '2026-10-13T12:01:00Z' })] }, ['synthetic']);
    assert.equal(state.items.get('one').phase, 'Receiving');
    assert.equal(activityPresentation(state.items.get('one'), Date.parse('2026-10-13T12:01:01Z'), true, true).active, true);
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { phase: 'Ready', version: 4, accepted: true, previewReady: true, assetId: 'asset', captureId: 'known' })] }, ['synthetic']);
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { phase: 'PreparingPreview', version: 0, accepted: true, previewReady: false, assetId: 'asset', captureId: 'known' })] }, ['synthetic']);
    assert.equal(state.items.get('one').phase, 'PreparingPreview');
    assert.equal(state.items.get('one').previewReady, false);
    assert.equal(activityPresentation(state.items.get('one'), Date.parse('2026-10-13T12:00:01Z'), true, true).active, true);
});

test('two transfers keep independent identities while a RAW/JPEG snapshot links one Capture', () => {
    const state = new SourceActivityState();
    state.observe(item('raw', { accepted: true, captureId: 'spoofed' }));
    state.observe(item('jpeg'));
    assert.equal(state.items.size, 2);
    assert.equal(state.items.get('raw').accepted, false);
    assert.equal(state.items.get('raw').captureId, undefined);
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: ['raw', 'jpeg'].map(id => item(id, { accepted: true, assetId: id, captureId: 'pair', phase: 'PreparingPreview', version: 2 })) }, ['synthetic']);
    assert.equal(new Set([...state.items.values()].map(row => row.captureId)).size, 1);
    state.observe(item('raw', { version: 3, phase: 'Receiving' }));
    assert.equal(state.items.get('raw').accepted, true);
    assert.equal(state.items.get('raw').phase, 'PreparingPreview');
    assert.match(activityPresentation(state.items.get('raw'), Date.parse('2026-10-13T12:00:01Z'), true, true).label, /^Preparing preview/);
});

test('snapshot races preserve newer observations and accepted mapping; derivative readiness remains snapshot-owned', () => {
    const state = new SourceActivityState();
    state.observe(item('one', { version: 3, phase: 'Ready', previewReady: true }));
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { version: 2, accepted: true, captureId: 'known' })] }, ['synthetic'], 0);
    assert.equal(state.items.get('one').version, 3);
    assert.equal(state.items.get('one').accepted, true);
    assert.equal(state.items.get('one').previewReady, false);
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one')] }, ['synthetic']);
    assert.equal(state.items.get('one').accepted, true);
    assert.equal(state.items.get('one').captureId, 'known');
    state.reconcile({ epoch: 'epoch-two', coverageComplete: true, items: [item('one', { epoch: 'epoch-two' })] }, ['synthetic']);
    assert.equal(state.items.get('one').version, 1);
    state.observe(item('one', { version: 90 }));
    assert.equal(state.items.get('one').version, 1, 'old epoch cannot undo restart state');
});

test('expiry/disconnect/unavailable stays unknown, RAW-only never spins and memory stays bounded', () => {
    const state = new SourceActivityState();
    for (let index = 0; index < 1000; index++) state.observe(item(String(index)));
    assert.equal(state.items.size, 100);
    for (const [connected, available, seconds] of [[true, true, 60], [false, true, 1], [true, false, 1]]) {
        const result = activityPresentation(item('one'), Date.parse('2026-10-13T12:00:00Z') + seconds * 1000, connected, available);
        assert.equal(result.active, false); assert.match(result.label, /unknown/);
    }
    const raw = activityPresentation(item('raw', { accepted: true, captureId: 'raw-only', phase: 'AcceptedAwaitingGrouping' }), Date.parse('2026-10-13T12:00:01Z'), true, true);
    assert.equal(raw.active, false); assert.match(raw.label, /preview not available/);
});

test('same-version renewal advances observed time without regressing phase or replaying old renewals', () => {
    const state = new SourceActivityState();
    state.observe(item('one'));
    state.observe(item('one', { observedAt: '2026-10-13T12:00:30Z' }));
    assert.equal(state.items.get('one').observedAt, '2026-10-13T12:00:30Z');
    state.observe(item('one', { observedAt: '2026-10-13T12:00:10Z' }));
    assert.equal(state.items.get('one').observedAt, '2026-10-13T12:00:30Z');
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { version: 2, accepted: true })] }, ['synthetic']);
    state.observe(item('one', { observedAt: '2026-10-13T12:01:30Z' }));
    assert.equal(state.items.get('one').version, 2);
    assert.equal(state.items.get('one').accepted, true);
});

test('hint sequence remains ordered independently of authoritative snapshots with unknown versions', () => {
    const state = new SourceActivityState();
    state.observe(item('one', { version: 10 }));
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { phase: 'Stale', version: 0 })] }, ['synthetic']);
    state.observe(item('one', { version: 9, observedAt: '2026-10-13T12:00:30Z' }));
    assert.equal(state.items.get('one').phase, 'Stale');
    const beforeRead = state.revision;
    state.observe(item('one', { version: 11, observedAt: '2026-10-13T12:00:30Z' }));
    state.reconcile({ epoch: 'epoch-one', coverageComplete: true, items: [item('one', { phase: 'Stale', version: 0 })] }, ['synthetic'], beforeRead);
    assert.equal(state.items.get('one').phase, 'Receiving');
    assert.equal(state.items.get('one').accepted, false);
});

test('presentation tones follow known activity facts without using the human label as a state key', () => {
    const now = Date.parse('2026-10-13T12:00:01Z');
    for (const [patch, tone] of [
        [{}, 'receiving'],
        [{ accepted: true, phase: 'AcceptedAwaitingGrouping' }, 'received'],
        [{ accepted: true, captureId: 'known', phase: 'PreparingPreview' }, 'preparing'],
        [{ accepted: true, captureId: 'known', previewReady: true, phase: 'Ready' }, 'ready'],
        [{ phase: 'FailedUnavailable' }, 'failed'],
        [{ phase: 'Stale' }, 'stale'],
        [{ phase: 'AcceptedAwaitingGrouping' }, 'neutral'],
    ]) assert.equal(activityPresentation(item('one', patch), now, true, true).tone, tone);
    assert.equal(activityPresentation(item('one'), now, false, true).tone, 'stale');
});

test('recent counters use fresh transfers, shared pending Captures and accepted original identities', () => {
    const now = Date.parse('2026-10-13T12:00:01Z');
    const observed = (id, patch = {}) => {
        const value = item(id, patch);
        return { ...value, ...activityPresentation(value, now, true, true) };
    };
    const rows = [
        observed('receiving'), observed('expired', { observedAt: '2026-10-13T11:58:00Z' }),
        observed('raw', { assetId: 'raw', accepted: true, captureId: 'pair', phase: 'PreparingPreview' }),
        observed('jpeg', { assetId: 'jpeg', accepted: true, captureId: 'pair', phase: 'PreparingPreview' }),
        observed('waiting', { accepted: true, phase: 'AcceptedAwaitingGrouping' }),
        observed('raw-only', { accepted: true, captureId: 'raw-only', phase: 'AcceptedAwaitingGrouping' }),
        observed('failed', { accepted: true, captureId: 'failed', phase: 'FailedUnavailable' }),
        observed('stale', { accepted: true, captureId: 'stale', phase: 'Stale' }),
        observed('raw-replay', { assetId: 'raw', accepted: true, captureId: 'pair', previewReady: true, phase: 'Ready' }),
    ];
    const counts = sourceActivityCounters(rows, true);
    assert.equal(counts.receiving, 1);
    assert.equal(counts.previewPending, 2);
    assert.equal(counts.received, 6);
    assert.equal(sourceActivityCounters(rows, false).receiving, null);
    assert.equal(sourceActivityCounters(rows, false).previewPending, null);
    assert.equal(sourceActivityCounters(rows, false).received, null);
});
