import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
const context = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/workspace-preferences.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
const { decodePreferences, previewDimension } = context.exports;
test('legacy appearance migrates into independent scopes and retains validated filters only', () => {
    const result = decodePreferences(JSON.stringify({ appearance: { density: 'dense', width: 'full', details: false, motion: 'reduce', sort: 'oldest', gridMaximumDimension: 768, secret: 'discard' }, filters: { Gallery: { sourceId: 'source', workflow: 'All', tag: 'bounded', collectionIds: ['collection', 7] } }, selection: ['private'] }));
    assert.equal(result.value.workspace.width, 'full'); assert.equal(result.value.workspace.gridMaximumDimension, 768);
    assert.equal(result.value.appearances.Gallery.density, 170); assert.equal(result.value.appearances.Curation.sort, 'oldest');
    result.value.appearances.Gallery.details = true; assert.equal(result.value.appearances.Curation.details, false);
    assert.equal(result.value.filters.Gallery.sourceIds[0], 'source'); assert.equal(result.value.filters.Gallery.collectionIds.length, 1);
    assert.equal(JSON.stringify(result.value).includes('private'), false); assert.equal(JSON.stringify(result.value).includes('secret'), false);
});
test('current records win and invalid fields fall back independently', () => {
    const first = decodePreferences(JSON.stringify({ version: 2, workspace: { width: 'full', gridMaximumDimension: 12 }, appearances: { Gallery: { density: 420, fit: 'fit' }, Curation: { density: 999 } }, appearance: { density: 100, motion: 'full' } }));
    assert.equal(first.value.workspace.width, 'full'); assert.equal(first.value.workspace.motion, 'system'); assert.equal(first.value.workspace.gridMaximumDimension, 512);
    assert.equal(first.value.appearances.Gallery.density, 420); assert.equal(first.value.appearances.Curation.density, 230);
    assert.equal(JSON.stringify(decodePreferences(JSON.stringify(first.value)).value), JSON.stringify(first.value));
});
test('corrupt and future formats use defaults without requesting a loading write', () => {
    for (const raw of ['{broken', 'null', '[]', '{"version":99,"workspace":{"width":"full"}}']) {
        const result = decodePreferences(raw); assert.equal(result.value.workspace.width, 'standard'); assert.ok(result.notice);
    }
    assert.equal(decodePreferences('{"version":99}').writable, false);
});
test('filter decoding rejects nested values and retains missing or invalid workflow defaults', () => {
    for (const workflows of [undefined, 'Waiting', [['Waiting']], [false, 3]]) {
        const decoded = decodePreferences(JSON.stringify({ version: 2, filters: { Curation: { tag: 'trip', workflows, labels: [['Red'], 'Blue', false] } } }));
        assert.deepEqual(Array.from(decoded.value.filters.Curation.workflows), ['Waiting']);
        assert.deepEqual(Array.from(decoded.value.filters.Curation.labels), ['Blue']);
        assert.equal(decoded.value.filters.Curation.tag, 'trip');
    }
    assert.equal(decodePreferences('{"filters":{"Curation":{"workflow":"All"}}}').value.filters.Curation.workflows.length, 0);
    assert.equal(decodePreferences('{"version":2,"filters":{"Curation":{"workflows":[]}}}').value.filters.Curation.workflows.length, 0);
});
test('preview dimensions commit only complete bounded integers', () => {
    for (const value of ['', ' ', '63', '4097', '512.5', 'Infinity', 'NaN', 9, 512.5]) assert.equal(previewDimension(value), undefined, String(value));
    for (const value of ['64', '512', '4096', 1024]) assert.equal(previewDimension(value), Number(value));
});
