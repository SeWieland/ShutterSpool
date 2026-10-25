import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
import vm from 'node:vm';
const context = vm.createContext({ exports: {}, structuredClone });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/curation-session.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
const { CurationSession } = context.exports;
const initial = { revision: 1, rating: 0, tags: [], label: null };

test('acknowledged bulk curation preserves opening values for discard', async () => {
    const writes = [];
    const edit = new CurationSession(initial, async body => {
        writes.push(body); return { ...body, revision: body.expectedRevision + 1 };
    }, async () => initial);
    edit.acceptBatch({ ...initial, revision: 2, rating: 4, label: 'Blue' });
    assert.equal(edit.intent.rating, 4);
    assert.equal(edit.external, false);
    assert.equal(edit.opening.rating, 0);
    edit.reset(false); await edit.settle();
    assert.equal(writes[0].expectedRevision, 2);
    assert.equal(writes[0].rating, 0);
    assert.equal(writes[0].label, null);
});
test('serial writes coalesce rapid intent and retain the opening baseline', async () => {
    const pending = []; const writes = [];
    const edit = new CurationSession(initial, body => { writes.push(body); return new Promise(resolve => pending.push(resolve)); }, async () => initial);
    edit.change({ rating: 1 }); edit.change({ rating: 3 }); edit.change({ label: 'Blue' });
    assert.equal(writes.length, 1);
    pending.shift()({ ...writes[0], revision: 2 }); await new Promise(resolve => setImmediate(resolve));
    assert.equal(writes.length, 2); assert.equal(writes[1].rating, 3); assert.equal(writes[1].label, 'Blue');
    pending.shift()({ ...writes[1], revision: 3 }); await edit.settle();
    assert.equal(edit.status, 'Saved'); assert.equal(edit.opening.rating, 0);
});
test('conflicts preserve intent and restoration requires explicit review of external changes', async () => {
    const edit = new CurationSession(initial, async () => { throw { responseStatusCode: 409 }; }, async () => ({ ...initial, revision: 2, rating: 5 }));
    edit.change({ rating: 3 }); await edit.settle();
    assert.equal(edit.status, 'Conflict'); assert.equal(edit.intent.rating, 3); assert.equal(edit.server.rating, 5);
    edit.useServer(); edit.reset(false); await edit.settle(); assert.equal(edit.status, 'Conflict'); assert.equal(edit.intent.rating, 0);
});
test('lost responses reread without replay and cannot attribute the revision to this visit', async () => {
    let count = 0;
    const edit = new CurationSession(initial, async () => { count++; throw new Error('lost response'); }, async () => ({ ...initial, revision: 2, rating: 3 }));
    edit.change({ rating: 3 }); await edit.settle();
    assert.equal(count, 1); assert.equal(edit.status, 'Saved'); assert.equal(edit.external, true);
    edit.reset(false); assert.equal(edit.status, 'Conflict');
});
test('release wins stop further autosave while retaining unresolved intent', async () => {
    const edit = new CurationSession(initial, async () => { throw { responseStatusCode: 409 }; }, async () => ({ ...initial, revision: 2, releasedAt: new Date() }));
    edit.change({ rating: 4 }); await edit.settle();
    assert.equal(edit.intent.rating, 4); assert.equal(edit.unresolved, true); edit.reapply(); await edit.settle(); assert.equal(edit.status, 'Released · unresolved edits');
});

test('an observed release fences queued intent and wins over a delayed save acknowledgement', async () => {
    let acknowledge; let writes = 0;
    const edit = new CurationSession(initial, () => {
        writes++;
        return new Promise(resolve => { acknowledge = resolve; });
    }, async () => initial);
    edit.change({ rating: 2 });
    edit.change({ rating: 4 });
    edit.observe({ ...initial, revision: 3, rating: 2, releasedAt: new Date() });
    assert.equal(edit.editable, false);
    acknowledge({ ...initial, revision: 2, rating: 2 });
    await edit.settle();
    assert.equal(writes, 1);
    assert.equal(edit.server.revision, 3);
    assert.ok(edit.server.releasedAt);
    assert.equal(edit.intent.rating, 4);
    assert.equal(edit.status, 'Released · unresolved edits');
    edit.reset(false); edit.reapply(); await edit.settle();
    assert.equal(writes, 1);
});

test('a newer external revision during a save is retained without attributing it to this visit', async () => {
    let acknowledge;
    const edit = new CurationSession(initial, () => new Promise(resolve => { acknowledge = resolve; }), async () => initial);
    edit.change({ rating: 2 });
    edit.observe({ ...initial, revision: 3, rating: 5 });
    acknowledge({ ...initial, revision: 2, rating: 2 });
    await edit.settle();
    assert.equal(edit.server.rating, 5);
    assert.equal(edit.intent.rating, 2);
    assert.equal(edit.status, 'Conflict');
    assert.equal(edit.external, true);
});

test('restoration after an external change waits for explicit reapply even behind a pending write', async () => {
    const pending = []; const writes = [];
    const edit = new CurationSession(initial, body => {
        writes.push(body);
        return new Promise(resolve => pending.push(resolve));
    }, async () => initial);
    edit.observe({ ...initial, revision: 2, rating: 5 });
    edit.change({ rating: 3 });
    edit.reset(false);
    pending.shift()({ ...initial, revision: 3, rating: 3 });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(writes.length, 1);
    await edit.settle();
    assert.equal(edit.status, 'Conflict');
    assert.equal(edit.intent.rating, 0);
    edit.reapply();
    assert.equal(writes.length, 2);
    assert.equal(writes[1].expectedRevision, 3);
    assert.equal(writes[1].rating, 0);
    pending.shift()({ ...initial, revision: 4 });
    await edit.settle();
    assert.equal(edit.status, 'Saved');
});

test('checking a matching authoritative outcome resumes autosave for subsequent edits', async () => {
    let acknowledge; let writes = 0; let result = { ...initial, revision: 3, rating: 5 };
    const edit = new CurationSession(initial, body => {
        writes++;
        if (writes > 1) return Promise.resolve({ ...body, revision: 5 });
        return new Promise(resolve => { acknowledge = resolve; });
    }, async () => result);
    edit.change({ rating: 2 });
    edit.observe(result);
    acknowledge({ ...initial, revision: 2, rating: 2 });
    await edit.settle();
    result = { ...initial, revision: 4, rating: 2 };
    await edit.check();
    assert.equal(edit.status, 'Saved');
    edit.change({ rating: 4 });
    await edit.settle();
    assert.equal(writes, 2);
    assert.equal(edit.server.rating, 4);
    assert.equal(edit.status, 'Saved');
});

test('a matching reread after a lost response resolves a paused restoration and permits the next edit', async () => {
    let reject; let writes = 0;
    const edit = new CurationSession(initial, body => {
        writes++;
        if (writes > 1) return Promise.resolve({ ...body, revision: 4 });
        return new Promise((resolve, fail) => { reject = fail; });
    }, async () => ({ ...initial, revision: 3 }));
    edit.observe({ ...initial, revision: 2, rating: 5 });
    edit.change({ rating: 0 });
    edit.reset(false);
    reject(new Error('lost acknowledgement'));
    await edit.settle();
    assert.equal(edit.status, 'Saved');
    edit.change({ rating: 4 });
    await edit.settle();
    assert.equal(writes, 2);
    assert.equal(edit.server.rating, 4);
});
