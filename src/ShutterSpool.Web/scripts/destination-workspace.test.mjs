import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { parse } from '@vue/compiler-sfc';
import { ref, computed, reactive, watch, nextTick } from 'vue';

const activityContext = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/source-activity.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, activityContext);
const activityModule = activityContext.exports;
const readPoolContext = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/read-pool.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, readPoolContext);
const readPoolModule = readPoolContext.exports;

test('folder picker selects only a successfully read folder and fences stale reads and access loss', async () => {
    let access = 0, lost;
    const requests = [], events = [];
    const unmountCallbacks = [];
    const context = vm.createContext({ exports: {}, AbortController, ref, computed, watch, nextTick,
        defineProps: () => ({ initialPath: '', online: true }), defineEmits: () => (...event) => events.push(event),
        onMounted() {}, onUnmounted(callback) { unmountCallbacks.push(callback); },
        require: name => name.endsWith('/session') ? { accessGeneration: () => access, onAccessLost(callback) { lost = callback; return () => {}; } }
            : { api: { management: { destinations: { localFolders: { get: () => new Promise((resolve, reject) => requests.push({ resolve, reject })) } } } }, cancellable: signal => ({ signal }), handleFailure() {} },
    });
    const source = parse(readFileSync(new URL('../app/components/LocalFolderPicker.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    const older = run("browse('/old')"), newer = run("browse('/photos')");
    requests[1].resolve({ path: '/photos', parentPath: '/', directories: [] }); await newer;
    requests[0].resolve({ path: '/old', parentPath: '/', directories: [] }); await older;
    run('choose()'); assert.deepEqual(events[0], ['select', '/photos']);
    const failed = run("browse('/missing')"); run('choose()');
    assert.equal(events.length, 1, 'no selection during an unconfirmed read');
    requests[2].reject({ detail: 'This folder no longer exists.' }); await failed;
    assert.match(run('error.value'), /no longer exists/);
    run('choose()'); assert.equal(events.length, 1, 'a failed read cannot select the old folder');
    const pending = run("browse('/private')"); access++; lost();
    requests[3].resolve({ path: '/private', directories: [] }); await pending;
    assert.equal(run('listing.value'), undefined);
    assert.deepEqual(events.at(-1), ['close']);
    unmountCallbacks.forEach(callback => callback());
});

test('Source profiles load saved assignments and submit an explicit revision-guarded draft choice', async () => {
    const sources = reactive([{ id: 'camera', name: 'Camera', configurationRevision: 7, connectionProfile: 'LegacyFtps' }]);
    const requests = [];
    const context = vm.createContext({
        exports: {}, AbortController, ref, reactive, computed, onMounted() {}, onUnmounted() {},
        defineProps: () => ({ sources }), defineEmits: () => () => {},
        watch: (target, callback, options) => target && typeof target === 'object' && 'value' in target ? watch(target, callback, { ...options, flush: 'sync' }) : () => {},
        require: name => name.endsWith('/source-activity') ? activityModule : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session')
            ? { accessGeneration: () => 0, onAccessLost: () => () => {} }
                : { handleFailure() {}, api: { management: { sources: {
                post: async body => { requests.push(body); return {}; },
                byId: () => ({ put: async body => { requests.push(body); return {}; } }),
            } } } },
    });
    const source = parse(readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run("capabilityState.value = 'ready'; capabilities.value = {profiles:[{profile:'ModernFtps',enabled:true},{profile:'LegacyFtps',enabled:true}]}; connectionProfile.value = 'ModernFtps'");
    await run("command('create')");
    assert.equal(requests.at(-1).connectionProfile, 'ModernFtps', 'new Sources submit standard FTPS explicitly');
    run("chosen.value = 'camera'");
    assert.equal(run('connectionProfile.value'), 'LegacyFtps', 'existing profile must be visible on selection');
    run("connectionProfile.value = 'ModernFtps'; dirty.value = true");
    sources[0].configurationRevision = 8;
    sources[0] = { ...sources[0] };
    assert.equal(run('connectionProfile.value'), 'ModernFtps', 'refresh must retain the unsaved profile');
    assert.equal(run('conflict.value'), true);
    await run("command('save')");
    assert.equal(requests.length, 1, 'conflicted security choices cannot be submitted');
    run('conflict.value = false; revision.value = 8');
    await run("command('save')");
    assert.equal(requests.at(-1).connectionProfile, 'ModernFtps');
    assert.equal(requests.at(-1).expectedRevision, 8);
    run("connectionProfile.value = 'LegacyFtps'");
    await run("command('save')");
    assert.equal(requests.at(-1).connectionProfile, 'LegacyFtps');
    run("chosen.value = ''");
    assert.equal(run('connectionProfile.value'), 'ModernFtps', 'a new Source never inherits the previous compatibility profile');
});

test('Delivery displays one Capture for confirmed RAW/JPEG work and marks unavailable reads as last known', () => {
    const context = vm.createContext({ exports: {}, AbortController, ref, computed, watch() {}, onMounted() {}, onUnmounted() {},
        defineProps: () => ({ online: true, connected: true }), defineEmits: () => () => {},
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : { api: {}, handleFailure() {} },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run("summaries.value.destination = { items: [{id:'raw',captureId:'pair',state:'Delivered'}, {id:'jpeg',captureId:'pair',state:'Delivered'}, {id:'other',captureId:'pending',state:'Running'}] }");
    assert.equal(run("confirmedCaptures('destination').length"), 1);
    assert.equal(run("confirmedCaptures('destination')[0].captureId"), 'pair');
    run('summaryUnavailable.value.destination = true');
    assert.equal(run("loadedStatus('destination').state"), 'neutral');
    assert.match(run("loadedStatus('destination').text"), /last known/);
});

test('delayed Source commands and removals preserve a newer owner draft on success and failure', async () => {
    for (const action of ['save', 'secret', 'create', 'remove']) {
        for (const failed of [false, true]) {
            let finish; let reject; let refreshed = 0;
            const pending = () => new Promise((resolve, fail) => { finish = resolve; reject = fail; });
            const context = vm.createContext({
                exports: {}, AbortController, ref, reactive, computed, onMounted() {}, defineProps: () => ({ sources: [{ id: 'a', name: 'A', configurationRevision: 1 }, { id: 'b', name: 'B', configurationRevision: 1 }] }),
                defineEmits: () => () => { refreshed++; },
                watch: (target, callback, options) => target?.value === '' ? watch(target, callback, { ...options, flush: 'sync' }) : () => {},
                onUnmounted() {}, window: { confirm: () => true },
                require: name => name.endsWith('/source-activity') ? activityModule : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0, onAccessLost: () => () => {} } : {
                    handleFailure: () => 409,
                    api: { management: { sources: { post: pending, byId: id => ({
                        put: pending, regenerateSecret: { post: pending }, delete: pending,
                        get: async () => ({ id, name: 'Created', configurationRevision: 1 }),
                    }) } } },
                },
            });
            const source = parse(readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
            vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
            const run = expression => vm.runInContext(expression, context);
            run("capabilityState.value = 'ready'; capabilities.value = {profiles:[{profile:'ModernFtps',enabled:true}]}; connectionProfile.value = 'ModernFtps'; chosen.value = 'a'; name.value = 'A draft'; revision.value = 1; dirty.value = true");
            if (action === 'create') run("chosen.value = ''");
            if (action === 'remove') run('requestRemoval()');
            const request = run(action === 'remove' ? 'removeSource()' : `command('${action}')`);
            run("chosen.value = 'b'; name.value = 'B draft'; dirty.value = true; feedback.value = 'B draft retained'");
            const reselected = action === 'secret' && failed;
            if (reselected) run("chosen.value = 'a'; name.value = 'New A draft'; dirty.value = true; feedback.value = 'New A draft retained'");
            if (failed) reject(new Error('synthetic conflict'));
            else finish(action === 'remove' ? { outcome: 'archived' } : action === 'secret' ? { current: { id: 'a', configurationRevision: 2 }, secret: 'synthetic-only' } : action === 'create' ? { id: 'created', secret: 'synthetic-only' } : { id: 'a', configurationRevision: 2 });
            await request;
            assert.equal(run('chosen.value'), reselected ? 'a' : 'b', `${action} must retain the selected owner`);
            assert.equal(run('name.value'), reselected ? 'New A draft' : 'B draft');
            assert.equal(run('dirty.value'), true);
            assert.equal(run('feedback.value'), reselected ? 'New A draft retained' : 'B draft retained');
            assert.equal(run('conflict.value'), false);
            assert.equal(run('secret.value'), '');
            assert.equal(refreshed, 1, 'old-owner outcomes still reconcile the Source list');
        }
    }
});

test('normal Source creation retains its one-time credential after selecting the created owner', async () => {
    const context = vm.createContext({
        exports: {}, AbortController, ref, reactive, computed, onMounted() {}, defineProps: () => ({ sources: [] }), defineEmits: () => () => {},
        watch: (target, callback, options) => target?.value === '' ? watch(target, callback, { ...options, flush: 'sync' }) : () => {},
        onUnmounted() {},
        require: name => name.endsWith('/source-activity') ? activityModule : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0, onAccessLost: () => () => {} } : {
            handleFailure() {}, cancellable: signal => ({ signal }), api: { management: { sources: {
                post: async () => ({ id: 'new', secret: 'synthetic-only' }),
                byId: id => ({ get: async () => ({ id, name: 'New', configurationRevision: 0 }) }),
            } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run("capabilityState.value = 'ready'; capabilities.value = {profiles:[{profile:'ModernFtps',enabled:true}]}; connectionProfile.value = 'ModernFtps'");
    await run("command('create')");
    assert.equal(run('chosen.value'), 'new');
    assert.equal(run('secret.value'), 'synthetic-only');
});

test('a writable provider test cannot complete setup after a concurrent saved revision change', async () => {
    const stored = new Map([['shutterspool.destinationSetup::', '["new"]']]);
    let revision = 1;
    let failRead = false;
    let finishTest;
    const context = vm.createContext({
        exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
        localStorage: { getItem: key => stored.get(key), setItem: (key, value) => stored.set(key, value) },
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 503,
            api: { management: { destinations: { byId: id => ({
                get: async () => {
                    if (failRead) throw new Error('current settings unavailable');
                    return { id, name: 'New', configurationRevision: revision, kind: 'local', localRootPath: '/photos' };
                },
                routing: { get: async () => ({ destinationId: id, configurationRevision: revision, enabled: false, rules: [] }) },
                local: { test: { post: () => new Promise(resolve => { finishTest = resolve; }) } },
            }) } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    await run("open('new')");
    const pending = run("settings('test')");
    await Promise.resolve();
    revision = 2;
    finishTest({ writable: true, testedConfigurationRevision: 1 });
    await pending;
    assert.equal(run('settingsRevision.value'), 2);
    assert.equal(run('setupIncomplete.value'), true);
    assert.equal(stored.get('shutterspool.destinationSetup::'), '["new"]');
    assert.match(run('feedback.value'), /test.*current revision/i);
    failRead = true;
    const unreadable = run("settings('test')");
    await Promise.resolve();
    finishTest({ writable: true, testedConfigurationRevision: 2 });
    await unreadable;
    assert.equal(run('setupIncomplete.value'), true, 'cached matching settings cannot replace a failed authoritative read');
    assert.equal(stored.get('shutterspool.destinationSetup::'), '["new"]');
});

test('setup recovers a failed initial disable before saving provider settings with the new revision', async () => {
    let enabled = true; let revision = 0; let failDisable = true;
    const calls = [];
    const stored = new Map();
    const context = vm.createContext({
        exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
        localStorage: { getItem: key => stored.get(key), setItem: (key, value) => stored.set(key, value) },
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 503,
            api: { management: { destinations: {
                post: async () => ({ id: 'new' }),
                get: async () => ({ items: [{ id: 'new' }] }),
                byId: id => ({
                    get: async () => ({ id, name: 'New', configurationRevision: revision, kind: 'local', localRootPath: '/photos', enabled }),
                    routing: {
                        get: async () => ({ destinationId: id, configurationRevision: revision, enabled, rules: [] }),
                        put: async body => {
                            calls.push('disable');
                            assert.equal(body.expectedRevision, revision);
                            if (failDisable) throw new Error('temporarily unavailable');
                            enabled = body.enabled; revision++;
                            return { destinationId: id, configurationRevision: revision, enabled, rules: body.rules };
                        },
                    },
                    local: {
                        put: async body => { assert.equal(enabled, false); assert.equal(body.expectedRevision, revision); calls.push('provider'); revision++; },
                        test: { post: async () => { assert.equal(enabled, false); calls.push('test'); return { writable: true, testedConfigurationRevision: revision }; } },
                    },
                    work: { get: async () => ({ items: [] }) },
                }),
            } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    await run("settings('create')");
    assert.equal(run('setupIncomplete.value'), true);
    assert.equal(run('configConflict.value'), true);
    assert.deepEqual(calls, ['disable']);
    failDisable = false;
    run('configConflict.value = false; dirtySettings.value = false');
    await run('read()');
    run("root.value = '/new-root'; dirtyProvider.value = true");
    await run('saveAndTest()');
    assert.deepEqual(calls, ['disable', 'disable', 'provider', 'test']);
    assert.equal(run('setupIncomplete.value'), false);
    assert.equal(run('settingsRevision.value'), 2);
    assert.equal(run('destination.value.enabled'), false);
    for (const [states, expected] of [[[], 'neutral'], [['Delivered'], 'success'], [['Failed', 'Delivered'], 'error'], [['Delivered', 'Failed'], 'warning'], [['Ambiguous'], 'warning'], [['Running'], 'neutral']]) {
        run(`summaries.value.new = { items: ${JSON.stringify(states.map(state => ({ state })))} }`);
        assert.equal(run("loadedStatus('new').state"), expected);
    }
});

test('local setup saves the chosen folder before testing and keeps rejected folder drafts', async () => {
    const calls = [];
    let savedRoot = null, revision = 0, rejectSave = false;
    const context = vm.createContext({
        exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 400,
            api: { management: { destinations: { byId: id => ({
                get: async () => ({ id, kind: 'local', localRootPath: savedRoot, configurationRevision: revision }),
                routing: { get: async () => ({ destinationId: id, configurationRevision: revision, enabled: false, rules: [] }) },
                local: {
                    put: async body => {
                        calls.push('save');
                        assert.equal(body.expectedRevision, revision);
                        if (rejectSave) throw { detail: 'Choose an existing folder on the server.' };
                        savedRoot = body.rootPath; return { configurationRevision: ++revision };
                    },
                    test: { post: async () => { calls.push('test'); assert.equal(savedRoot, '/photos'); return { writable: true, testedConfigurationRevision: revision }; } },
                },
            }) } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    await run("open('local')");
    await run("settings('test')");
    assert.deepEqual(calls, [], 'an unconfigured destination cannot test an absent saved folder');
    run("root.value = '/photos'; dirtyProvider.value = true");
    await run("settings('test')");
    assert.deepEqual(calls, [], 'testing saved settings cannot ignore the selected folder');
    run("name.value = 'Unsaved name'; dirtyName.value = true");
    await run('saveAndTest()');
    assert.deepEqual(calls, ['save', 'test']);
    assert.equal(run('dirtyProvider.value'), false);
    assert.equal(run('name.value'), 'Unsaved name');
    assert.equal(run('dirtyName.value'), true, 'testing a folder preserves the independent name draft');
    rejectSave = true;
    run("root.value = '/missing'; dirtyProvider.value = true");
    await run('saveAndTest()');
    assert.deepEqual(calls, ['save', 'test', 'save'], 'a rejected folder is never tested');
    assert.equal(run('root.value'), '/missing');
    assert.equal(run('dirtyProvider.value'), true);
    assert.match(run('feedback.value'), /existing folder/);
});

test('Source removal sends its confirmed revision and clears the one-time credential', async () => {
    let removed; let refreshed = 0;
    const context = vm.createContext({
        exports: {}, AbortController, ref, reactive, computed, onMounted() {}, defineProps: () => ({ sources: [{ id: 'source', name: 'Camera', configurationRevision: 7 }] }),
        defineEmits: () => () => { refreshed++; }, watch() {}, onUnmounted() {},
        require: name => name.endsWith('/source-activity') ? activityModule : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0, onAccessLost: () => () => {} } : {
            api: { management: { sources: { byId: id => ({ delete: async config => { removed = { id, revision: config.queryParameters.expectedRevision }; return { outcome: 'archived' }; } }) } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run("chosen.value = 'source'; secret.value = 'one-time'; requestRemoval()");
    assert.equal(removed, undefined, 'opening confirmation never removes a Source');
    await run('removeSource()');
    assert.deepEqual(removed, { id: 'source', revision: 7 });
    assert.equal(run('chosen.value'), '');
    assert.equal(run('secret.value'), '');
    assert.match(run('feedback.value'), /archived.*preserved/);
    assert.equal(refreshed, 1);
});

test('Retry is available only in the server-supported failure states', async () => {
    let writes = 0;
    const props = reactive({ online: true, work: { id: 'work', state: 'Pending' } });
    const context = vm.createContext({
        exports: {}, AbortController, ref, reactive, computed, onMounted() {}, defineProps: () => props, defineEmits: () => () => {}, watch() {}, onUnmounted() {},
        require: name => name.endsWith('/source-activity') ? activityModule : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0, onAccessLost: () => () => {} } : {
            api: { management: { deliveries: { byId: () => ({ retry: { post: async () => { writes++; } } }) } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWork.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    for (const state of ['Pending', 'Running', 'Delivered', 'Ambiguous', 'Superseded']) {
        props.work = { ...props.work, state };
        assert.equal(run('canRetry.value'), false);
        await run("command('retry')");
    }
    assert.equal(writes, 0);
    for (const state of ['RetryWait', 'Failed', 'Disabled']) {
        props.work = { ...props.work, state };
        assert.equal(run('canRetry.value'), true);
    }
});

test('incomplete setup blocks rules and needs a successful test of the unchanged saved revision', async () => {
    const stored = new Map([['shutterspool.destinationSetup::', '["new"]']]);
    let writable = false; let writes = 0;
    const context = vm.createContext({
        exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
        localStorage: { getItem: key => stored.get(key), setItem: (key, value) => stored.set(key, value) },
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 503,
            api: { management: { destinations: { byId: id => ({
                get: async () => ({ id, name: 'New', configurationRevision: 1, kind: 'local', localRootPath: '/photos' }),
                routing: { get: async () => ({ destinationId: id, configurationRevision: 1, enabled: false, rules: [] }), put: async () => { writes++; } },
                local: { test: { post: async () => ({ writable, testedConfigurationRevision: 1 }) } },
            }) } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    await run("open('new')");
    assert.equal(run('tab.value'), 'Settings');
    await run('saveRules()');
    assert.equal(writes, 0);
    await run("settings('test')");
    assert.equal(run('setupIncomplete.value'), true);
    writable = true;
    run('dirtyProvider.value = true');
    await run("settings('test')");
    assert.equal(run('setupIncomplete.value'), true);
    run('dirtySettings.value = false');
    await run("settings('test')");
    assert.equal(run('setupIncomplete.value'), false);
    assert.equal(stored.get('shutterspool.destinationSetup::'), '[]');
    assert.equal(run("pathExample({ folderTemplate: '{year}/{month}', filenameTemplate: '{suppliedName}' })"), '/photos/2026/10/IMG_0001.jpg');
    run("root.value = '/'");
    assert.equal(run("pathExample({ filenameTemplate: '{suppliedName}' })"), '/IMG_0001.jpg');
});

test('Source delay controls preserve exact seconds and convert whole weeks', () => {
    const context = vm.createContext({
        exports: {}, AbortController, ref, reactive, computed, onMounted() {}, defineProps: () => ({ sources: [] }), defineEmits: () => () => {}, watch() {}, onUnmounted() {},
        require: name => name.endsWith('/source-activity') ? activityModule : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0, onAccessLost: () => () => {} } : {},
    });
    const source = parse(readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run('setDelay(1209600)');
    assert.equal(run('delay.value'), 2);
    assert.equal(run('delayUnit.value'), 604800);
    run('setDelay(61)');
    assert.equal(run('delay.value * delayUnit.value'), 61);
    assert.equal(run('delayUnit.value'), 1);
});

test('destination switches clear every owner-scoped draft before a delayed or failed read and block writes', async () => {
    let resolveRead; let writes = 0;
    const context = vm.createContext({
        exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
        window: { confirm: () => true },
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 503,
            api: { management: { destinations: { byId: id => ({
                get: () => id === 'a' ? Promise.resolve({ id, name: 'A', configurationRevision: 1, kind: 'local', localRootPath: '/A' }) : new Promise(resolve => { resolveRead = resolve; }),
                routing: { get: async () => ({ destinationId: id, configurationRevision: 1, rules: [{ name: id, conditions: [] }] }), put: async () => { writes++; } },
                local: { put: async () => { writes++; } },
                work: { get: async () => ({ items: [{ id: 'work-' + id }] }) },
            }) } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    await run("open('a');");
    await run('readWork()');
    assert.equal(run('authoritativeOwner.value'), 'a');
    const pending = run("open('b')");
    assert.equal(run('routing.value'), undefined);
    assert.equal(run('rules.value.length'), 0);
    assert.equal(run('work.value'), undefined);
    assert.equal(run('settingsRevision.value'), undefined);
    assert.equal(run('root.value'), '');
    await run("saveRules(); settings('provider')");
    assert.equal(writes, 0);
    resolveRead(undefined); await pending;
    assert.equal(run('authoritativeOwner.value'), '');
    await run("saveRules(); settings('provider')");
    assert.equal(writes, 0);
});

test('a delayed provider save cannot clear a newer destination password or settings draft', async () => {
    let finishSave;
    const context = vm.createContext({
        exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
        window: { confirm: () => true },
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 503,
            api: { management: { destinations: { byId: id => ({
                get: async () => ({ id, name: id, configurationRevision: 1, kind: 'local', localRootPath: '/' + id }),
                routing: { get: async () => ({ destinationId: id, configurationRevision: 1, rules: [] }) },
                local: { put: () => new Promise(resolve => { finishSave = resolve; }) },
            }) } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    await run("open('a')");
    const pending = run("settings('provider')");
    run('close()'); await run("open('b')");
    run("password.value = 'B-only draft'; dirtySettings.value = true; feedback.value = 'B draft'");
    finishSave({}); await pending;
    assert.equal(run('password.value'), 'B-only draft');
    assert.equal(run('dirtySettings.value'), true);
    assert.equal(run('feedback.value'), 'B draft');
    assert.equal(run('authoritativeOwner.value'), 'b');
});

test('destination name, provider and toggle saves preserve unrelated settings drafts', async () => {
    for (const action of ['name', 'provider', 'toggle']) {
        let finishSave;
        const context = vm.createContext({
            exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
            require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
                cancellable: signal => ({ signal }), handleFailure: () => 503,
                api: { management: { destinations: { byId: id => ({
                    get: async () => ({ id, name: 'Name draft', configurationRevision: 2, kind: 'local', localRootPath: '/provider draft' }),
                    routing: {
                        get: async () => ({ destinationId: id, configurationRevision: 2, enabled: false, rules: [] }),
                        put: () => new Promise(resolve => { finishSave = () => resolve({ destinationId: id, configurationRevision: 2, enabled: true, rules: [] }); }),
                    },
                    put: () => new Promise(resolve => { finishSave = () => resolve({ configurationRevision: 2 }); }),
                    local: { put: () => new Promise(resolve => { finishSave = () => resolve({ configurationRevision: 2 }); }) },
                }) } } },
            },
        });
        const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
        vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
        const run = expression => vm.runInContext(expression, context);
        await run("open('same')");
        run("name.value = 'Name draft'; root.value = '/provider draft'; dirtyName.value = dirtyProvider.value = true");
        const pending = run(`settings('${action}')`);
        finishSave({});
        await pending;
        assert.equal(run('name.value'), 'Name draft');
        assert.equal(run('root.value'), '/provider draft');
    }
});

test('same-destination reopen isolates delayed settings and rule outcomes on success and failure', async () => {
    for (const action of ['settings', 'rules']) {
        for (const failed of [false, true]) {
            let finish; let reject;
            const pending = () => new Promise((resolve, fail) => { finish = resolve; reject = fail; });
            const context = vm.createContext({
                exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
                require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
                    cancellable: signal => ({ signal }), handleFailure: () => 409,
                    api: { management: { destinations: { byId: id => ({
                        get: async () => ({ id, name: 'Saved', configurationRevision: 1, kind: 'local', localRootPath: '/saved' }),
                        routing: {
                            get: async () => ({ destinationId: id, configurationRevision: 1, enabled: false, rules: [{ id: 'saved-rule', name: 'Saved rule', conditions: [] }] }),
                            put: action === 'rules' ? pending : async () => ({ destinationId: id, configurationRevision: 2, enabled: true, rules: [] }),
                        },
                        local: { put: action === 'settings' ? pending : async () => {} },
                    }) } } },
                },
            });
            const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
            vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
            const run = expression => vm.runInContext(expression, context);
            await run("open('same')");
            run(action === 'settings' ? "root.value = '/old draft'; dirtyProvider.value = true" : "rules.value[0].name = 'Old draft'; dirtyRules.value = true");
            const request = run(action === 'settings' ? "settings('provider')" : 'saveRules()');
            run('dirtySettings.value = false; dirtyRules.value = false');
            run('close()');
            await run("open('same')");
            run(action === 'settings' ? "root.value = '/new visit draft'; dirtyProvider.value = true; feedback.value = 'new visit'" : "rules.value[0].name = 'New visit rule'; dirtyRules.value = true; feedback.value = 'new visit'");
            if (failed) reject(new Error('synthetic conflict'));
            else finish(action === 'settings' ? {} : { destinationId: 'same', configurationRevision: 2, enabled: false, rules: [] });
            await request;
            assert.equal(run(action === 'settings' ? 'root.value' : 'rules.value[0].name'), action === 'settings' ? '/new visit draft' : 'New visit rule');
            assert.equal(run(action === 'settings' ? 'dirtySettings.value' : 'dirtyRules.value'), true);
            assert.equal(run('feedback.value'), 'new visit');
        }
    }
});

test('selecting Work reads its page once through the tab watcher', async () => {
    let workReads = 0;
    const stops = [];
    const context = vm.createContext({
        exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true, generation: 0, motionEnabled: false }), defineEmits: () => () => {},
        watch: (...args) => { const stop = watch(...args); stops.push(stop); return stop; },
        onMounted() {}, onUnmounted() {}, nextTick,
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 503,
            api: { management: { destinations: { byId: id => ({
                get: async () => ({ id, name: 'Destination', configurationRevision: 1, kind: 'local', localRootPath: '/photos' }),
                routing: { get: async () => ({ destinationId: id, configurationRevision: 1, enabled: false, rules: [] }) },
                work: { get: async () => { workReads++; return { items: [] }; } },
            }) } } },
        },
    });
    const { template, scriptSetup } = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor;
    vm.runInContext(ts.transpileModule(scriptSetup.content, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    try {
        await run("open('destination')");
        const buttonStart = template.content.indexOf(`v-for="item in ['Rules','Work','Settings']"`);
        const buttonEnd = template.content.indexOf('</button>', buttonStart);
        const button = buttonStart >= 0 && buttonEnd >= 0 ? template.content.slice(buttonStart, buttonEnd) : '';
        const clickStart = button.indexOf('@click="');
        const handler = clickStart >= 0 ? button.slice(clickStart + 8, button.indexOf('"', clickStart + 8)) : undefined;
        assert.ok(handler, 'find the Work tab button handler');
        context.clickScope = new Proxy({}, {
            has: (_target, key) => ['item', 'tab', 'readWork'].includes(key),
            get: (_target, key) => key === 'item' ? 'Work' : key === 'tab' ? run('tab.value') : key === 'readWork' ? () => run('readWork()') : undefined,
            set: (_target, key, value) => { if (key === 'tab') run(`tab.value = ${JSON.stringify(value)}`); return true; },
        });
        vm.runInContext(`with (clickScope) { ${handler} }`, context);
        await nextTick();
        await Promise.resolve();
        assert.equal(workReads, 1);
    } finally {
        stops.forEach(stop => stop());
    }
});

test('creating a dirty destination draft opens its saved editor even when browser setup storage is unavailable', async () => {
    const context = vm.createContext({ exports: {}, AbortController, ref, computed, defineProps: () => ({ online: true }), defineEmits: () => () => {}, watch() {}, onMounted() {}, onUnmounted() {}, nextTick: async () => {},
        localStorage: { getItem() { return '[]'; }, setItem() { throw new Error('storage denied'); } },
        require: name => name.includes('kiota') ? {} : name.endsWith('/read-pool') ? readPoolModule : name.endsWith('/session') ? { accessGeneration: () => 0 } : {
            cancellable: signal => ({ signal }), handleFailure: () => 503,
            api: { management: { destinations: {
                post: async () => ({ id: 'created' }),
                get: async () => ({ items: [{ id: 'created', name: 'New archive', kind: 'local' }] }),
                byId: id => ({
                    get: async () => ({ id, name: 'New archive', configurationRevision: 1, kind: 'local' }),
                    routing: { get: async () => ({ destinationId: id, configurationRevision: 1, enabled: false, rules: [] }) },
                }),
            } } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run("name.value = 'New archive'; dirtyName.value = true");
    await run("settings('create')");
    assert.equal(run('selected.value'), 'created');
    assert.equal(run('dirtySettings.value'), false);
    assert.equal(run('setupIncomplete.value'), true, 'in-memory setup guard survives denied browser storage');
});
