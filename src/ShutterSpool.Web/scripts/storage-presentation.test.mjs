import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
const retention = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/retention.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, retention);
const context = vm.createContext({ exports: {}, require: () => retention.exports });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/storage-presentation.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
const { capacityComposition, settingsChanges, storageMessage, surfaceProgress, reviewEstimates } = context.exports;
const snapshot = { usedBytes: 100, activeBytes: 50, trashBytes: 20, diskPreviewBytes: 10, transientBytes: 15, quarantineBytes: 5, reservedBytes: 25, quotaBytes: 200, unknownAccounting: false, protectedBytes: 60, favoriteBytes: 40, observedAt: new Date() };
test('capacity ring partitions usage and reservations without adding overlapping protection or historical amounts', () => {
    const result = capacityComposition({ ...snapshot, purgedBytes: 999, plannedReclamationBytes: 555 }, true);
    assert.equal(result.known, true); assert.equal(result.total, 200); assert.equal(result.remaining, 75);
    assert.equal(result.segments.reduce((sum, part) => sum + part.bytes, 0), 200);
    assert.equal(result.segments.find(part => part.key === 'temporary').bytes, 20);
    assert.equal(result.segments.find(part => part.key === 'reserved').bytes, 25);
});
test('review effects explain enabling and disabling, shorter ages and equivalent duration values', () => {
    const changes = settingsChanges({ quota: { isActive: true }, settings: { automaticRemovalEnabled: true, restoreProtection: '3.00:00:00' } }, { quota: { isActive: false }, settings: { automaticRemovalEnabled: false, restoreProtection: '1.00:00:00' } });
    assert.match(changes.find(change => change.path === 'quota.isActive').effect, /Disables.*quota/);
    assert.match(changes.find(change => change.path === 'settings.automaticRemovalEnabled').effect, /Disables automatic cleanup/);
    assert.match(changes.find(change => change.format === 'duration').effect, /Shortens.*subsequent.*existing/i);
    assert.equal(settingsChanges({ settings: { restoreProtection: '01:00:00' } }, { settings: { restoreProtection: '0.01:00:00' } }).length, 0);
});
test('over-quota and inactive rings show composition with honest allowance and no invented percentage', () => {
    const over = capacityComposition({ ...snapshot, quotaBytes: 80 }, true);
    assert.equal(over.remaining, 0); assert.equal(over.excess, 45); assert.equal(over.total, 125);
    const inactive = capacityComposition(snapshot, false); assert.equal(inactive.remaining, undefined); assert.equal(inactive.total, 125);
});
test('unknown and inconsistent accounting never supplies a fabricated empty ring', () => {
    for (const value of [undefined, { ...snapshot, unknownAccounting: true }, { ...snapshot, usedBytes: 99 }, { ...snapshot, reservedBytes: null }, { ...snapshot, activeBytes: -1 }]) {
        assert.equal(capacityComposition(value, true).known, false);
    }
});
test('review summaries contain only changed allowlisted fields with contextual effects', () => {
    const before = { quota: { isActive: true, quotaBytes: 100 }, settings: { trashRecoveryWindow: '3.00:00:00', revision: 4 }, previewCache: { cacheMode: 'Disk' } };
    const after = { quota: { isActive: true, quotaBytes: 80 }, settings: { trashRecoveryWindow: '5.00:00:00', revision: 5 }, previewCache: { cacheMode: 'Memory' } };
    const changes = settingsChanges(before, after);
    assert.equal(changes.length, 3); assert.equal(changes[0].before, 100); assert.equal(changes[0].after, 80);
    assert.match(changes.find(change => change.path.endsWith('trashRecoveryWindow')).effect, /existing.*unchanged/i);
    assert.match(changes.find(change => change.path.endsWith('cacheMode')).effect, /restart.*existing disk/i);
    assert.equal(settingsChanges(before, before).length, 0);
});
test('known blockers and privacy progress use understandable wording with honest unknown fallbacks', () => {
    assert.match(storageMessage('PRESERVATION_HOLD_ACTIVE'), /preservation hold.*resolved/i);
    assert.match(storageMessage('future-server-code'), /additional.*Details/i);
    assert.equal(surfaceProgress('Completed').label, 'Completed'); assert.equal(surfaceProgress('Blocked').label, 'Blocked');
    assert.match(surfaceProgress('future').label, /not confirmed/i);
});
test('a bounded zero-valued incomplete plan remains unknown without hiding known eligibility', () => {
    const result = reviewEstimates({ complete: false, eligibleCaptureCount: 7, plannedTrashCaptureCount: 0, plannedBytes: 0, warnings: ['planned-trash-scan-bounded'] });
    assert.equal(result.eligibleCaptureCount, 7); assert.equal(result.plannedTrashCaptureCount, null); assert.equal(result.plannedBytes, null);
    const complete = reviewEstimates({ complete: true, eligibleCaptureCount: 0, plannedTrashCaptureCount: 0, plannedBytes: 0 });
    assert.equal(complete.plannedTrashCaptureCount, 0); assert.equal(complete.plannedBytes, 0);
});
