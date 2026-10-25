import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { parse } from '@vue/compiler-sfc';
import { ref, computed, watch } from 'vue';

function mount(name, props, management) {
    const events = [], timers = new Map();
    let timerId = 0, unmount = () => {}, loseAccess = () => {};
    const context = vm.createContext({
        exports: {}, ref, computed, watch, onMounted() {}, onUnmounted(callback) { unmount = callback; },
        setTimeout(callback, delay) { assert.equal(delay, 200); timers.set(++timerId, callback); return timerId; },
        clearTimeout(id) { timers.delete(id); },
        defineProps: () => props, defineEmits: () => (name, ...args) => events.push([name, ...args]),
        require: name => name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: callback => { loseAccess = callback; return () => {}; } } : {
            api: { management }, handleFailure: failure => failure.status,
        },
    });
    const source = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { run: expression => vm.runInContext(expression, context), events,
        flushTimers() { for (const [id, callback] of [...timers]) { timers.delete(id); callback(); } },
        unmount: () => unmount(), loseAccess: () => loseAccess(),
    };
}

test('Collection moves use reviewed revisions, count partial outcomes and retain failures for deliberate retry', async () => {
    const writes = [];
    const props = { ids: ['one', 'two'], online: true };
    let revision = 2;
    const { run, events } = mount('CollectionMove', props, { captures: {
        byId: id => ({ get: async () => ({ id, displayName: id, curation: { revision, releasedAt: id === 'two' ? new Date() : undefined }, currentCollection: { id: 'old', name: 'Berlin' } }) }),
        collectionMoves: { post: async body => { writes.push(body); return { items: [{ captureId: 'one', status: 'applied' }, { captureId: 'two', status: 'conflict' }] }; } },
    } });
    run("target.value = ['new']");
    await run('move()');
    assert.equal(writes.length, 1, 'one action reads selected revisions and submits the bulk move');
    assert.equal(JSON.stringify(writes[0]), JSON.stringify({ targetCollectionId: 'new', items: [{ captureId: 'one', expectedRevision: 2 }, { captureId: 'two', expectedRevision: 2 }] }));
    assert.match(run('feedback.value'), /1 moved.*1 failed.*0 unknown/);
    assert.equal(JSON.stringify(events.find(event => event[0] === 'completed')?.[1]), '["one"]');
    assert.equal(run('checking.value'), true);
    await run('move()');
    assert.equal(writes.length, 1, 'conflicts cannot be blindly replayed');
    props.ids = ['two']; revision = 8;
    await run('reconcile()');
    assert.equal(run('reviewed.value[0].curation.revision'), 8);
    assert.equal(run('checking.value'), false);
});

test('a lost move response requires a complete successful reread before retry and never queues offline work', async () => {
    let writes = 0, unavailable = false;
    const props = { ids: ['one'], online: true };
    const { run } = mount('CollectionMove', props, { captures: {
        byId: id => ({ get: async () => { if (unavailable) throw { status: 503 }; return { id, curation: { revision: 4 } }; } }),
        collectionMoves: { post: async () => { writes++; throw { status: 503 }; } },
    } });
    run("target.value = ['new']");
    await run('reconcile()'); await run('move()');
    assert.equal(run('checking.value'), true);
    unavailable = true; await run('reconcile()'); await run('move()');
    assert.equal(writes, 1);
    assert.equal(run('checking.value'), true);
    unavailable = false; await run('reconcile()');
    assert.equal(run('checking.value'), false);
    props.online = false; await run('move()');
    assert.equal(writes, 1);
});

test('Collection management blocks uncertain writes until saved Collections are reread', async () => {
    let writes = 0, unavailable = false;
    const { run } = mount('CollectionManager', {}, { collections: {
        get: async () => { if (unavailable) throw { status: 503 }; return { items: [{ id: 'fixed', name: 'ShutterSpool', revision: 1, isBuiltIn: true }], nextCursor: null }; },
        post: async () => { writes++; throw { status: 503 }; },
    } });
    run("name.value = 'Berlin'"); await run('save()');
    assert.equal(run('checking.value'), true);
    await run('save()'); assert.equal(writes, 1);
    await run('read()'); assert.equal(run('checking.value'), false);
    assert.equal(run('items.value[0].name'), 'ShutterSpool');
    unavailable = true; await run('read()');
    assert.equal(run('checking.value'), true, 'a failed configuration read requires recovery before writes');
    await run('save()'); assert.equal(writes, 1);
});

test('Collection selector uses bounded search pages and exact-ID OR selection', async () => {
    const reads = [], selected = [];
    const props = { modelValue: ['one'], multiple: true };
    const { run, events, flushTimers } = mount('CollectionPicker', props, { collections: { get: async request => {
        reads.push(request.queryParameters); return { items: [{ id: 'two', name: 'Travel/Berlin' }], nextCursor: 'next' };
    } } });
    run("search.value = 'Berlin'"); flushTimers(); await new Promise(setImmediate); await run('read(true)');
    assert.equal(reads[0].limit, 8); assert.equal(reads[0].q, 'Berlin'); assert.equal(reads[1].cursor, 'next');
    run("search.value = 'Travel'"); flushTimers(); await new Promise(setImmediate);
    assert.equal(reads[2].q, 'Travel'); assert.equal(reads[2].cursor, undefined, 'a new search cannot reuse the previous query cursor');
    assert.equal(run('items.value.length'), 1, 'a changed search replaces rather than appends the page');
    run("choose('two')"); selected.push(...events.at(-1)[1]);
    assert.equal(selected.join(','), 'one,two');
    run("choose('one')"); assert.equal(JSON.stringify(events.at(-1)[1]), '[]');
});

test('unchanged nested Collection rename preserves the canonical path', async () => {
    const writes = [];
    const item = { id: 'event', name: 'Berlin', path: 'Events/Berlin', revision: 3 };
    const { run } = mount('CollectionManager', {}, { collections: {
        get: async () => ({ items: [item] }),
        byId: id => ({ put: async body => writes.push({ id, ...body }) }),
    } });
    await run('read()'); run('choose(items.value[0])');
    assert.equal(run('name.value'), 'Events/Berlin');
    await run('save()');
    assert.deepEqual(writes, [{ id: 'event', expectedRevision: 3, name: 'Events/Berlin' }]);
});

test('selected Collections distinguish equal short names using their canonical paths', async () => {
    const paths = { event: 'Events/Berlin', travel: 'Travel/Berlin' };
    const { run, events } = mount('CollectionPicker', { modelValue: ['event', 'travel'], multiple: true }, { collections: {
        byId: id => ({ get: async () => ({ id, name: 'Berlin', path: paths[id] }) }),
    } });
    await run('readSelected()');
    assert.equal(run('selectedNames.value.event'), 'Events/Berlin');
    assert.equal(run('selectedNames.value.travel'), 'Travel/Berlin');
    run("choose('event')");
    assert.equal(JSON.stringify(events.at(-1)[1]), '["travel"]');
});

test('lost create responses for long Collection names recover through bounded paged search without losing the draft', async () => {
    const draft = '/Events/' + 'B'.repeat(180), reads = [];
    let writes = 0;
    const { run } = mount('CollectionManager', {}, { collections: {
        post: async () => { writes++; throw { status: 503 }; },
        get: async request => {
            reads.push(request.queryParameters);
            if (request.queryParameters.q.length > 128) throw { status: 400 };
            return { items: [{ id: 'committed', name: 'B'.repeat(180), path: draft }], nextCursor: 'second-page' };
        },
    } });
    run(`name.value = '${draft}'`); await run('save()'); await run('save()');
    assert.equal(writes, 1, 'an uncertain create cannot be replayed');
    await run('read()');
    assert.equal(run('checking.value'), false);
    assert.equal(run('name.value'), draft);
    assert.ok(reads[0].q.length <= 128);
    assert.ok(draft.slice(1).startsWith(reads[0].q));
    await run('read(true)');
    assert.equal(reads[1].q, reads[0].q);
    assert.equal(reads[1].cursor, 'second-page');
    assert.equal(writes, 1, 'recovery only rereads authoritative state');
});


test('collection name rejection retains an editable draft while revision conflicts require review', async () => {
    for (const scenario of [
        { status: 400 },
        { status: 409 },
        { status: 409, errorCode: 'COLLECTION_NAME_CONFLICT', rename: true },
        { status: 409, errorCode: 'COLLECTION_REVISION_CONFLICT', rename: true },
    ]) {
        const { run } = mount('CollectionManager', {}, { collections: {
            post: async () => { throw scenario; },
            byId: () => ({ put: async () => { throw scenario; } }),
        } });
        if (scenario.rename) run("choose({ id: 'one', revision: 2, name: 'Old name' })");
        run("name.value = 'Berlin'");
        await run('save()');
        assert.equal(run('name.value'), 'Berlin');
        assert.equal(run('checking.value'), scenario.errorCode === 'COLLECTION_REVISION_CONFLICT');
    }
});

test('typing collection search fetches bounded suggestions and ignores older responses during debounce', async () => {
    const reads = [];
    const { run, flushTimers } = mount('CollectionPicker', { modelValue: [] }, { collections: {
        get: request => new Promise(resolve => reads.push({ query: request.queryParameters, resolve })),
    } });
    run("search.value = 'Ber'"); flushTimers();
    run("search.value = 'Berlin'");
    reads[0].resolve({ items: [{ id: 'old', name: 'Bergen' }] });
    await new Promise(setImmediate);
    assert.equal(run('items.value.length'), 0, 'the previous request is invalid before the new timer fires');
    flushTimers();
    assert.equal(reads.length, 2);
    assert.equal(reads[1].query.limit, 8);
    reads[1].resolve({ items: [{ id: 'new', name: 'Berlin' }] });
    await new Promise(setImmediate);
    assert.equal(run('items.value[0].id'), 'new');
});

test('clear unused previews exact zero references, protects built-in collections and never blindly replays uncertain deletion', async () => {
    const writes = [], reads = [];
    const candidates = [
        { id: 'fixed', revision: 1, isBuiltIn: true, sourceReferences: 0, captureReferences: 0 },
        { id: 'source', revision: 2, sourceReferences: 1, captureReferences: 0 },
        { id: 'photo', revision: 3, sourceReferences: 0, captureReferences: 1 },
        { id: 'unknown', revision: 4 },
        { id: 'unused', revision: 5, sourceReferences: 0, captureReferences: 0 },
        { id: 'raced', revision: 6, sourceReferences: 0, captureReferences: 0 },
        { id: 'uncertain', revision: 7, sourceReferences: 0, captureReferences: 0 },
        { id: 'later', revision: 8, sourceReferences: 0, captureReferences: 0 },
    ];
    const { run } = mount('CollectionManager', {}, { collections: {
        get: async request => { reads.push(request.queryParameters); return { items: candidates }; },
        byId: id => ({ delete: async request => {
            writes.push({ id, revision: request.queryParameters.expectedRevision });
            if (id === 'raced') throw { status: 409 };
            if (id === 'uncertain') throw { status: 503 };
        } }),
    } });
    await run('prepareClearUnused()');
    assert.deepEqual(JSON.parse(run('JSON.stringify(unused.value.map(item => item.id))')), ['unused', 'raced', 'uncertain', 'later']);
    assert.equal(reads[0].q, undefined, 'cleanup candidates are independent from filtered search');
    assert.equal(writes.length, 0, 'preview cannot delete anything');
    await run('clearUnused()');
    assert.deepEqual(writes, [{ id: 'unused', revision: 5 }, { id: 'raced', revision: 6 }, { id: 'uncertain', revision: 7 }]);
    assert.equal(run('checking.value'), true);
    assert.match(run('feedback.value'), /1 deleted.*1 skipped.*uncertain/i);
    await run('clearUnused()');
    assert.equal(writes.length, 3);
});

test('a single photograph move reads its revision and applies once without a separate review action', async () => {
    const writes = [];
    const { run } = mount('CollectionMove', { ids: ['one'], online: true }, { captures: {
        byId: id => ({ get: async () => ({ id, curation: { revision: 9 } }) }),
        collectionMoves: { post: async body => { writes.push(body); return { items: [{ captureId: 'one', status: 'applied' }] }; } },
    } });
    run("target.value = ['new']");
    await run('move()');
    assert.equal(writes.length, 1);
    assert.equal(writes[0].items[0].expectedRevision, 9);
});


test('clear unused streams past referenced pages and caps the confirmed candidate batch', async () => {
    const reads = [];
    const { run } = mount('CollectionManager', {}, { collections: {
        get: async request => {
            const { cursor, limit } = request.queryParameters;
            reads.push({ cursor, limit });
            const page = Number(cursor ?? 0);
            return { items: Array.from({ length: limit }, (_, index) => ({ id: `${page}-${index}`, revision: 1, sourceReferences: page < 4 ? 1 : 0, captureReferences: 0 })), nextCursor: String(page + 1) };
        },
    } });
    await run('prepareClearUnused()');
    assert.equal(run('unused.value.length'), 100);
    assert.equal(reads.length, 8, 'reference-only pages cannot hide later unused Collections');
    assert.equal(reads.at(-1).limit, 10);
    assert.equal(run('unusedLimited.value'), true);
});

test('searching managed collections retains the edited name and rejects stale search results', async () => {
    const reads = [];
    const { run, flushTimers } = mount('CollectionManager', {}, { collections: {
        get: request => new Promise(resolve => reads.push({ query: request.queryParameters.q, resolve })),
    } });
    run("choose({ id: 'one', name: 'Old', revision: 1 }); name.value = 'Draft'; search.value = 'Ber'"); flushTimers();
    run("search.value = 'Berlin'"); flushTimers();
    reads[1].resolve({ items: [{ id: 'new', name: 'Berlin' }] });
    await new Promise(setImmediate);
    reads[0].resolve({ items: [{ id: 'old', name: 'Bergen' }] });
    await new Promise(setImmediate);
    assert.equal(run('items.value[0].id'), 'new');
    assert.equal(run('name.value'), 'Draft');
    assert.equal(run('selected.value.id'), 'one');
});

test('a repeated cleanup cursor cannot expose a partial confirmation', async () => {
    const { run } = mount('CollectionManager', {}, { collections: {
        get: async () => ({ items: [{ id: 'one', revision: 1, sourceReferences: 0, captureReferences: 0 }], nextCursor: 'same' }),
    } });
    await run('prepareClearUnused()');
    assert.equal(run('unusedReady.value'), false);
    assert.match(run('feedback.value'), /Unable to check/);
});


test('collection searches coalesce keystrokes and cancel pending reads on disposal', async () => {
    for (const component of ['CollectionPicker', 'CollectionManager']) {
        for (const dispose of ['unmount', 'loseAccess']) {
            const reads = [];
            const instance = mount(component, { modelValue: [] }, { collections: {
                get: async request => { reads.push(request.queryParameters.q); return { items: [] }; },
            } });
            instance.run("search.value = 'B'; search.value = 'Ber'; search.value = 'Berlin'");
            assert.equal(reads.length, 0, `${component} waits for the debounce`);
            instance.flushTimers();
            await new Promise(setImmediate);
            assert.deepEqual(reads, ['Berlin']);
            instance.run("search.value = 'Travel'");
            instance[dispose]();
            instance.flushTimers();
            assert.deepEqual(reads, ['Berlin'], `${component} cancels on ${dispose}`);
        }
    }
});


test('collection mutations cancel queued search reads before changing recovery state', async () => {
    for (const operation of ['save()', 'save(true)', 'clearUnused()']) {
        let reads = 0;
        const { run, flushTimers } = mount('CollectionManager', {}, { collections: {
            get: async () => { reads++; return { items: [] }; },
            post: async () => { throw { status: 503 }; },
            byId: () => ({ delete: async () => { throw { status: 503 }; } }),
        } });
        run("name.value = 'Berlin'; search.value = 'Berlin'");
        if (operation === 'save(true)') run("selected.value = { id: 'one', revision: 1 }");
        if (operation === 'clearUnused()') run("unusedReady.value = true; unused.value = [{ id: 'one', revision: 1 }]");
        const pending = run(operation);
        flushTimers();
        await pending;
        assert.equal(reads, 0, operation);
        assert.equal(run('checking.value'), true, operation);
    }
});


test('bulk move never submits a partial selection after a failed or missing authoritative read', async () => {
    for (const status of [404, 503]) {
        let writes = 0;
        const { run } = mount('CollectionMove', { ids: ['one', 'two'], online: true }, { captures: {
            byId: id => ({ get: async () => { if (id === 'two') throw { status }; return { id, curation: { revision: 3 } }; } }),
            collectionMoves: { post: async () => { writes++; } },
        } });
        run("target.value = ['new']");
        await run('move()');
        assert.equal(writes, 0);
        assert.equal(run('checking.value'), true);
        assert.equal(run('reviewed.value.length'), 0);
        if (status === 404) assert.match(run('feedback.value'), /photograph 2.*unavailable/i);
        await run('move()');
        assert.equal(writes, 0);
    }
});

test('bulk move enforces the 100-selection limit and online requirement before any read', async () => {
    for (const props of [{ ids: Array.from({ length: 101 }, (_, index) => String(index)), online: true }, { ids: ['one', 'two'], online: false }]) {
        let reads = 0;
        const { run } = mount('CollectionMove', props, { captures: {
            byId: () => ({ get: async () => { reads++; return {}; } }),
        } });
        run("target.value = ['new']");
        await run('move()');
        assert.equal(reads, 0);
    }
});
