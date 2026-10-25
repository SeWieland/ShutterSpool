import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
const context = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/workspace-interactions.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
const { ratingInterval, zoomAround, clampPan, effectiveMotion, reconcileCaptures, curationTiming, deliveryPresentation } = context.exports;

test('file sizes keep missing values distinct from zero and use readable binary units', () => {
    const { formatFileSize } = context.exports;
    assert.equal(formatFileSize(null), 'Unavailable');
    assert.equal(formatFileSize(0), '0 B');
    assert.equal(formatFileSize(1023), '1,023 B');
    assert.equal(formatFileSize(1024), '1 KiB');
    assert.equal(formatFileSize(1572864), '1.5 MiB');
    assert.equal(formatFileSize(1099511627776), '1 TiB');
});
test('delivery labels separate real blockers, scheduled retries and confirmed delivery', () => {
    assert.equal(deliveryPresentation('Delivered').label, 'Delivered');
    assert.equal(deliveryPresentation('Delivered').tone, 'success');
    assert.equal(deliveryPresentation('RetryWait').label, 'Retry scheduled');
    assert.equal(deliveryPresentation('RetryWait').tone, 'pending');
    for (const state of ['Failed', 'Ambiguous', 'Disabled', 'NoMatch', 'DestinationDisabled', 'InvalidTarget'])
        assert.equal(deliveryPresentation(state).tone, 'attention');
    assert.equal(deliveryPresentation('new-server-state').label, 'Status unavailable');
});
test('fresh authority wins duplicate IDs while retaining loaded history in order', () => {
    const rows = reconcileCaptures([{ id: 'same', revision: 1 }, { id: 'older', revision: 1 }], [{ id: 'new', revision: 1 }, { id: 'same', revision: 2 }], false);
    assert.equal(rows.map(row => row.id).join(','), 'new,same,older'); assert.equal(rows[1].revision, 2);
    const appended = reconcileCaptures(rows, [{ id: 'older', revision: 2 }, { id: 'last', revision: 1 }], true);
    assert.equal(appended.map(row => row.id).join(','), 'new,same,older,last'); assert.equal(appended[2].revision, 2);
});
test('a refreshed first page evicts absent cached matches inside its keyset range and keeps older loaded history', () => {
    const rows = reconcileCaptures([
        { id: 'stale', acceptedAt: '2026-10-12T10:03:00Z' },
        { id: 'same', acceptedAt: '2026-10-12T10:02:00Z' },
        { id: 'older', acceptedAt: '2026-10-12T10:00:00Z' },
    ], [{ id: 'same', acceptedAt: new Date('2026-10-12T10:02:00Z') }, { id: 'boundary', acceptedAt: '2026-10-12T10:01:00Z' }], false);
    assert.equal(rows.map(row => row.id).join(','), 'same,boundary,older');
    const tied = reconcileCaptures([
        { id: 'c', acceptedAt: new Date('2026-10-12T10:01:00.001Z') },
        { id: 'a', acceptedAt: new Date('2026-10-12T10:01:00.001Z') },
        { id: 'earlier', acceptedAt: new Date('2026-10-12T10:01:00.000Z') },
        { id: 'later', acceptedAt: new Date('2026-10-12T10:01:00.002Z') },
    ], [{ id: 'b', acceptedAt: '2026-10-12T10:01:00.001Z' }], false);
    assert.equal(tied.map(row => row.id).join(','), 'b,c,a,earlier');
    const gap = reconcileCaptures([{ id: 'first' }, { id: 'anchor' }, { id: 'old' }], [{ id: 'gap-z' }, { id: 'gap-a' }, { id: 'old', revision: 2 }], true, 'anchor');
    assert.equal(gap.map(row => row.id).join(','), 'first,anchor,gap-z,gap-a,old');
    assert.equal(gap.at(-1).revision, 2);
    const ascending = reconcileCaptures([{ id: 'gone', acceptedAt: '2026-10-12T10:00:00Z' }, { id: 'newer', acceptedAt: '2026-10-12T10:03:00Z' }], [{ id: 'first', acceptedAt: '2026-10-12T10:01:00Z' }, { id: 'boundary', acceptedAt: '2026-10-12T10:02:00Z' }], false, '', true);
    assert.equal(ascending.map(row => row.id).join(','), 'first,boundary,newer');
});
test('rating drag spans rejected, unrated and stars in either direction', () => {
    assert.equal(JSON.stringify(ratingInterval(5, -1)), JSON.stringify({ rating: -1, ratingMaximum: 5 }));
    assert.equal(JSON.stringify(ratingInterval(0, 3)), JSON.stringify({ rating: 0, ratingMaximum: 3 }));
    assert.equal(JSON.stringify(ratingInterval(2, 2)), JSON.stringify({ rating: 2, ratingMaximum: 2 }));
});
test('zoom preserves a pointed photo coordinate and respects the supported bounds', () => {
    const result = zoomAround(1, { x: 0, y: 0 }, 2, { x: 100, y: -50 });
    assert.equal(result.zoom, 2); assert.equal(result.offset.x, -100); assert.equal(result.offset.y, 50);
    assert.equal(zoomAround(2, { x: 20, y: 20 }, .5, { x: 0, y: 0 }).zoom, 1);
    assert.equal(zoomAround(2, { x: 20, y: 20 }, 12, { x: 0, y: 0 }).zoom, 6);
});
test('pan never removes the photograph from the viewport or moves an unzoomed frame', () => {
    assert.equal(JSON.stringify(clampPan({ x: 2000, y: -2000 }, 2, { width: 1000, height: 500 }, { width: 1000, height: 800 })), JSON.stringify({ x: 500, y: -100 }));
    assert.equal(JSON.stringify(clampPan({ x: 50, y: 50 }, 1, { width: 500, height: 1000 }, { width: 1000, height: 1000 })), JSON.stringify({ x: 0, y: 0 }));
});
test('explicit motion preference overrides the system; hidden or offline flow remains still', () => {
    assert.equal(effectiveMotion('full', true, true, true), true);
    assert.equal(effectiveMotion('reduce', false, true, true), false);
    assert.equal(effectiveMotion('system', true, true, true), false);
    assert.equal(effectiveMotion('full', false, false, true), false);
    assert.equal(effectiveMotion('full', false, true, false), false);
});

test('curation timing distinguishes held and manual state from a real captured deadline without implying release', () => {
    const now = Date.parse('2026-10-12T12:00:00Z');
    const timed = curationTiming({ delaySeconds: 1000, releaseDueAt: new Date(now + 200000) }, now);
    assert.equal(timed.icon, 'i-lucide-clock'); assert.equal(timed.fraction, .2); assert.equal(timed.urgent, true); assert.match(timed.title, /3m 20s/);
    const held = curationTiming({ held: true, delaySeconds: 1000, releaseDueAt: new Date(now + 200000) }, now);
    assert.equal(held.icon, 'i-lucide-pause'); assert.equal(held.fraction, undefined); assert.equal(held.urgent, false);
    assert.equal(curationTiming({}, now).fraction, undefined);
    assert.equal(curationTiming({ delaySeconds: 1000, releaseDueAt: new Date(now - 1000) }, now).fraction, 0);
    assert.match(curationTiming({ delaySeconds: 1000, releaseDueAt: new Date(now - 1000) }, now).title, /waiting for server/);
    assert.equal(curationTiming({ releasedAt: new Date(now) }, now).icon, 'i-lucide-check');
    assert.equal(curationTiming({ delaySeconds: 1000, releaseDueAt: new Date(now + 2000000) }, now).fraction, 1);
});
