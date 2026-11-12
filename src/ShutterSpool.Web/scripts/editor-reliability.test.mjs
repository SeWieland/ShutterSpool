import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { compileScript, parse } from '@vue/compiler-sfc';
import { ref, shallowRef, reactive, computed, watch, nextTick, onMounted, onUnmounted, createRenderer, h } from 'vue';
import * as Vue from 'vue';

function deferred() {
    let resolve, reject;
    const promise = new Promise((resolvePromise, rejectPromise) => { resolve = resolvePromise; reject = rejectPromise; });
    return { promise, resolve, reject };
}
async function flushMicrotasks(rounds = 12) { for (let i = 0; i < rounds; i++) await Promise.resolve(); }

function compile(name, context) {
    const source = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    if (name === 'DeliveryWorkspace') vm.runInContext("destinationCapabilityState.value = 'ready'; enabledDestinationKinds.value = ['local', 'webdav']", context);
    return expression => vm.runInContext(expression, context);
}
function compileMounted(name, context) {
    const descriptor = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor;
    const source = compileScript(descriptor, { id: `mounted-${name}`, inlineTemplate: true }).content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return context.exports.default;
}
function createTestRenderer() {
    const host = {
        createElement(type) { return { type, children: [], props: {}, handlers: {}, parent: undefined, selectedIndex: -1, multiple: false, scrollIntoView() {}, getRootNode() { return this; }, get options() { return this.children.filter(child => child.type === 'option'); }, addEventListener(name, fn) { (this.handlers[name] ??= []).push(fn); }, removeEventListener(name, fn) { this.handlers[name] = (this.handlers[name] ?? []).filter(value => value !== fn); }, dispatchEvent(event) { let current = this; while (current) { for (const handler of current.handlers?.[event.type] ?? []) handler(event); const name = `on${event.type[0].toUpperCase()}${event.type.slice(1)}`; const prop = current.props?.[name]; if (Array.isArray(prop)) prop.forEach(handler => handler(event)); else if (typeof prop === 'function') prop(event); current = current.parent; } return true; } }; },
        createText(text) { return { text, parent: undefined }; }, createComment(text) { return { comment: text, parent: undefined }; },
        setText(node, text) { node.text = text; }, setElementText(node, text) { node.textContent = text; node.children = []; },
        patchProp(node, key, _previous, value) { node.props[key] = value; },
        insert(node, parent, anchor) { if (node.parent) { const old = node.parent.children.indexOf(node); if (old >= 0) node.parent.children.splice(old, 1); } node.parent = parent; const position = anchor ? parent.children.indexOf(anchor) : -1; if (position < 0) parent.children.push(node); else parent.children.splice(position, 0, node); },
        remove(node) { if (node.parent) { const index = node.parent.children.indexOf(node); if (index >= 0) node.parent.children.splice(index, 1); node.parent = undefined; } },
        parentNode(node) { return node.parent ?? null; }, nextSibling(node) { if (!node.parent) return null; return node.parent.children[node.parent.children.indexOf(node) + 1] ?? null; },
    };
    return { host, renderer: createRenderer(host), find(root, predicate) { if (predicate(root)) return root; for (const child of root.children ?? []) { const found = this.find(child, predicate); if (found) return found; } }, text(node) { return `${node?.textContent ?? ''}${node?.text ?? ''}${(node?.children ?? []).map(child => this.text(child)).join('')}`; } };
}
const readPoolContext = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/read-pool.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, readPoolContext);
const readPoolModule = readPoolContext.exports;
const destinationCapabilityContext = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/destination-capabilities.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, destinationCapabilityContext);
const destinationCapabilityModule = destinationCapabilityContext.exports;

test('Delivery command stays locked after a lost response until a new authoritative work row arrives', async () => {
    let rejectRetry;
    let retries = 0;
    const props = reactive({ work: { id: 'delivery-1', assetId: 'asset-1', destinationId: 'destination-1', state: 'Failed' }, online: true, recoverySequence: 0 });
    const events = [];
    let unmount = () => {};
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch, nextTick,
        defineProps: () => props, defineEmits: () => (...event) => events.push(event),
        onUnmounted(callback) { unmount = callback; },
        require: name => name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
            : name.endsWith('/workspace-interactions') ? { deliveryPresentation: () => ({ tone: 'attention', label: 'Failed', icon: 'i-lucide-x' }) }
                : { api: { management: { deliveries: { byId: () => ({ retry: { post: () => { retries++; return new Promise((_resolve, reject) => { rejectRetry = reject; }); } } }) } } }, handleFailure: failure => failure.status },
    });
    const run = compile('DeliveryWork', context);
    const first = run("command('retry')");
    rejectRetry({ status: 503 });
    await first;
    assert.equal(run('uncertain.value'), true);
    assert.equal(events.some(([name]) => name === 'refresh'), true);
    await run("command('retry')");
    assert.equal(retries, 1, 'a lost reply never repeats the command');

    props.work = { ...props.work, state: 'Failed' };
    await nextTick();
    assert.equal(run('uncertain.value'), true, 'a row replacement alone is not proof that the refresh completed');
    props.recoverySequence++;
    await nextTick();
    assert.equal(run('uncertain.value'), false, 'only a newly reread row unlocks actions');
    const second = run("command('retry')");
    assert.equal(retries, 2);
    // The second request remains pending; invalidate it as unmounted state would.
    unmount();
    rejectRetry({ status: 503 });
    await second;
});

test('Delivery attempts reads cancel superseded owner requests and cannot replace newer rows', async () => {
    const props = reactive({ work: { id: 'delivery-1', assetId: 'asset-1', destinationId: 'destination-1', state: 'Failed' }, online: true });
    const requests = [];
    let unmount = () => {};
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch, nextTick,
        defineProps: () => props, defineEmits: () => () => {}, onUnmounted(callback) { unmount = callback; },
        require: name => name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
            : name.endsWith('/workspace-interactions') ? { deliveryPresentation: () => ({ tone: 'attention', label: 'Failed', icon: 'i-lucide-x' }) }
                : { cancellable: signal => ({ signal }), api: { management: { deliveries: { byId: () => ({ attempts: { get: options => new Promise(resolve => requests.push({ resolve, signal: options.options[0].signal })) } }) } } }, handleFailure() {} },
    });
    const run = compile('DeliveryWork', context);
    const older = run('readAttempts()');
    const newer = run('readAttempts()');
    assert.equal(requests[0].signal.aborted, true);
    requests[1].resolve({ items: [{ result: 'new' }] });
    await newer;
    requests[0].resolve({ items: [{ result: 'old' }] });
    await older;
    assert.equal(run("attempts.value.items[0].result"), 'new');
    const pending = run('readAttempts()');
    unmount();
    assert.equal(requests[2].signal.aborted, true, 'unmount cancels the outstanding attempts transport');
    requests[2].resolve({ items: [{ result: 'late' }] });
    await pending;
    assert.equal(run("attempts.value.items[0].result"), 'new', 'a late unmounted response cannot replace the last owned page');
});

test('Source reports a newly edited Source draft through the typed dirty event', async () => {
    const events = [];
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch, nextTick,
        defineProps: () => ({ sources: [] }), defineEmits: () => (...event) => events.push(event),
        onMounted() {}, onUnmounted() {}, ResizeObserver: class { observe() {} disconnect() {} },
        require: name => name.endsWith('/source-activity') ? { SourceActivityState: class { clear() {} }, activityPresentation: () => ({}), sourceActivityCounters: () => ({}), sourceStatusPosition: () => ({}) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
                : { api: { management: { sources: { get: async () => ({ items: [] }) }, sourceCapabilities: { get: async () => ({ profiles: [] }) } } }, cancellable: signal => ({ signal }), handleFailure() {}, readSourceAuthenticationObservations: async () => ({ items: [] }), readSourceActivity: async () => ({ items: [], epoch: '', coverageComplete: true }) },
    });
    const run = compile('SourceWorkspace', context);
    run('dirty.value = true');
    await nextTick();
    assert.ok(events.some(([name, value]) => name === 'dirty-change' && value === true));
});

test('mounted New destination controls report dirty state from real template input events', async () => {
    const events = [];
    const warnings = [];
    const context = vm.createContext({ exports: {}, AbortController, setTimeout, clearTimeout, ref, shallowRef, reactive, computed, watch, nextTick, onMounted, onUnmounted, HTMLElement: class {}, Document: class {},
        require: name => name === 'vue' ? Vue : name.includes('kiota') ? { createUntypedString: value => ({ getValue: () => value }), createUntypedNumber: value => ({ getValue: () => value }), createUntypedNull: () => ({ getValue: () => null }) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, session: { value: { profile: { iss: 'issuer', sub: 'subject' } } } }
                    : { api: { management: { destinations: { get: async () => ({ items: [] }), capabilities: { get: async () => ({ enabledKinds: ['local', 'webdav'] }) } } } }, cancellable: signal => ({ signal }), handleFailure() {} },
    });
    const component = compileMounted('DeliveryWorkspace', context);
    const mounted = createTestRenderer();
    const reportDirty = dirty => events.push(dirty);
    const app = mounted.renderer.createApp({ render: () => h(component, { online: true, generation: 0, motionEnabled: false, connected: true, 'onDirty-change': reportDirty, onDirtyChange: reportDirty }) });
    app.config.warnHandler = message => warnings.push(message);
    for (const name of ['UIcon', 'AuthorizedPreview', 'DeliveryWork', 'ReevaluateAction', 'CollectionPicker', 'LocalFolderPicker', 'CaptureRating', 'USelectMenu']) app.component(name, { render: () => null });
    const root = { type: 'root', children: [] };
    app.mount(root);
    await nextTick();
    await flushMicrotasks();
    const input = mounted.find(root, node => node.type === 'input');
    assert.ok(input, 'the rendered New destination form contains its name input');
    input.value = 'Synthetic destination';
    input.dispatchEvent({ type: 'input', target: input });
    await nextTick();
    assert.ok(events.includes(true), 'typing in the visible form emits dirty-change for the shell navigation guard');
    const provider = mounted.find(root, node => node.type === 'select');
    assert.ok(provider, 'the rendered New destination form contains its provider selector');
    provider.value = 'webdav';
    provider.dispatchEvent({ type: 'change', target: provider });
    await nextTick();
    assert.ok(events.includes(true), 'changing provider remains reported as a draft');
    assert.deepEqual(warnings, [], `mounted template emitted Vue warnings: ${warnings.join('; ')}`);
    app.unmount();
});

test('mounted rule conflict controls keep the draft locked through failed and pending rereads', async t => {
    const previousDocument = globalThis.Document;
    const previousShadowRoot = globalThis.ShadowRoot;
    globalThis.Document ??= class {};
    globalThis.ShadowRoot ??= class {};
    t.after(() => { if (previousDocument === undefined) delete globalThis.Document; else globalThis.Document = previousDocument; if (previousShadowRoot === undefined) delete globalThis.ShadowRoot; else globalThis.ShadowRoot = previousShadowRoot; });
    const events = [];
    const warnings = [];
    const routeGates = [];
    let routeRevision = 1;
    let rejectSave = true;
    let saveCount = 0;
    let listCount = 0;
    const route = () => ({ destinationId: 'destination-1', configurationRevision: routeRevision, enabled: false, rules: [{ id: 'rule-1', order: 0, name: 'Saved rule', required: true, negate: false, conditionMode: 0, folderTemplate: '', filenameTemplate: '{suppliedName}', conditions: [] }] });
    const endpoint = () => ({
        get: async () => ({ id: 'destination-1', name: 'Destination', kind: 'local', configurationRevision: 1, localRootPath: '/synthetic' }),
        work: { get: async () => ({ items: [] }) },
        routing: {
            get: async () => { const gate = routeGates.shift(); if (gate) await gate.promise; return route(); },
            put: async body => { saveCount++; if (rejectSave) throw new Error('save reply lost'); return { ...route(), configurationRevision: routeRevision + 1, rules: body.rules }; },
        },
    });
    const context = vm.createContext({ exports: {}, AbortController, setTimeout, clearTimeout, ref, shallowRef, reactive, computed, watch, nextTick, onMounted, onUnmounted, HTMLElement: class {}, Document: class {},
        require: name => name === 'vue' ? Vue : name.includes('kiota') ? { createUntypedString: value => ({ getValue: () => value }), createUntypedNumber: value => ({ getValue: () => value }), createUntypedNull: () => ({ getValue: () => null }) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, session: { value: { profile: { iss: 'issuer', sub: 'subject' } } } }
                    : { api: { management: { destinations: { get: async () => { listCount++; return { items: [{ id: 'destination-1', name: 'Destination', kind: 'local', configurationRevision: 1, enabled: false }] }; }, capabilities: { get: async () => ({ enabledKinds: ['local', 'webdav'] }) }, byId: endpoint } } }, cancellable: signal => ({ signal }), handleFailure: () => 503 },
    });
    const component = compileMounted('DeliveryWorkspace', context);
    const mounted = createTestRenderer();
    const reportDirty = dirty => events.push(dirty);
    const app = mounted.renderer.createApp({ render: () => h(component, { online: true, generation: 0, motionEnabled: false, connected: true, 'onDirty-change': reportDirty, onDirtyChange: reportDirty }) });
    app.config.warnHandler = message => warnings.push(message);
    for (const name of ['UIcon', 'AuthorizedPreview', 'DeliveryWork', 'ReevaluateAction', 'CollectionPicker', 'LocalFolderPicker', 'CaptureRating', 'USelectMenu']) app.component(name, { render: () => null });
    const root = { type: 'root', children: [] };
    app.mount(root);
    await nextTick(); await flushMicrotasks();
    assert.equal(listCount, 1, 'mount starts and completes the destination inventory read');
    const open = mounted.find(root, node => node.type === 'button' && node.props.class?.includes('destination-open'));
    assert.ok(open, `destination row was not rendered: ${mounted.text(root)}`);
    open.dispatchEvent({ type: 'click', target: open });
    await flushMicrotasks(); await nextTick();
    const ruleName = mounted.find(root, node => node.type === 'input');
    assert.ok(ruleName);
    ruleName.value = 'Local replacement draft';
    ruleName.dispatchEvent({ type: 'input', target: ruleName });
    await nextTick();
    assert.ok(events.includes(true), 'editing the rendered rule reports dirty state');
    mounted.find(root, node => node.type === 'button' && mounted.text(node).includes('Save rules')).dispatchEvent({ type: 'click', target: root });
    await flushMicrotasks(); await nextTick();
    assert.equal(saveCount, 1);
    const replacement = () => mounted.find(root, node => node.type === 'button' && mounted.text(node).includes('Explicitly replace with my draft'));
    assert.ok(replacement(), 'the uncertain save offers an explicit replacement choice');

    let failedRead = deferred();
    routeGates.push(failedRead);
    replacement().dispatchEvent({ type: 'click', target: replacement() });
    await nextTick();
    assert.equal(replacement().props.disabled, true, 'the replacement control is locked during its authoritative reread');
    assert.equal(mounted.find(root, node => node.type === 'button' && mounted.text(node).includes('Save rules'))?.props.disabled, true, 'rule saves stay locked while recovery is pending');
    failedRead.reject(new Error('route reread failed'));
    await flushMicrotasks(); await nextTick();
    assert.ok(replacement(), 'failed reread keeps the recovery choice visible');
    assert.equal(mounted.find(root, node => node.type === 'input')?.value, 'Local replacement draft', 'failed reread preserves the visible draft');

    const successfulRead = deferred();
    routeGates.push(successfulRead);
    const button = replacement();
    button.dispatchEvent({ type: 'click', target: button });
    await nextTick();
    assert.equal(button.props.disabled, true, 'the replacement control remains locked until its read resolves');
    routeRevision = 2;
    successfulRead.resolve();
    await flushMicrotasks(); await nextTick();
    assert.equal(mounted.find(root, node => node.type === 'input')?.value, 'Local replacement draft', 'successful explicit replacement preserves local intent');
    const save = mounted.find(root, node => node.type === 'button' && mounted.text(node).includes('Save rules'));
    assert.equal(save.props.disabled, false, 'only the successful authoritative reread unlocks the retained draft');
    save.dispatchEvent({ type: 'click', target: save });
    await flushMicrotasks(); await nextTick();
    assert.equal(saveCount, 2, 'the retained draft can be submitted only after the successful route reread');
    const useServer = () => mounted.find(root, node => node.type === 'button' && mounted.text(node).includes('Use server rules'));
    let failedReload = deferred();
    routeGates.push(failedReload);
    useServer().dispatchEvent({ type: 'click', target: useServer() });
    await nextTick();
    assert.equal(useServer().props.disabled, true, 'server replacement is locked while its authoritative read is pending');
    assert.equal(mounted.find(root, node => node.type === 'button' && mounted.text(node).includes('Save rules'))?.props.disabled, true);
    failedReload.reject(new Error('route reread failed'));
    await flushMicrotasks(); await nextTick();
    assert.ok(useServer(), 'failed server replacement retains the conflict and local draft');
    assert.equal(mounted.find(root, node => node.type === 'input')?.value, 'Local replacement draft');

    const successfulReload = deferred();
    routeGates.push(successfulReload);
    const reloadButton = useServer();
    reloadButton.dispatchEvent({ type: 'click', target: reloadButton });
    await nextTick();
    assert.equal(reloadButton.props.disabled, true);
    successfulReload.resolve();
    await flushMicrotasks(); await nextTick();
    assert.equal(mounted.find(root, node => node.type === 'input')?.value, 'Saved rule', 'successful server replacement is the only action that discards this draft');
    assert.equal(mounted.find(root, node => node.type === 'button' && mounted.text(node).includes('Save rules'))?.props.disabled, true);
    assert.deepEqual(warnings, [], `mounted template emitted Vue warnings: ${warnings.join('; ')}`);
    app.unmount();
});

test('Delivery inventory and editor rereads share a four-request metadata limit', async () => {
    let active = 0, maximum = 0;
    const requests = [];
    const track = (kind, value) => {
        active++; maximum = Math.max(maximum, active);
        const gate = deferred();
        const entry = { kind, gate, released: false };
        requests.push(entry);
        return gate.promise.then(() => value).finally(() => { active--; });
    };
    const page = { items: Array.from({ length: 6 }, (_, index) => ({ id: `destination-${index}`, name: `Destination ${index}`, kind: 'local', configurationRevision: 1 })) };
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch() {}, onMounted() {}, onUnmounted() {}, nextTick,
        defineProps: () => ({ online: true, generation: 0, motionEnabled: false }), defineEmits: () => () => {},
        require: name => name.includes('kiota') ? { createUntypedString: value => ({ getValue: () => value }), createUntypedNumber: value => ({ getValue: () => value }), createUntypedNull: () => ({ getValue: () => null }) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, session: { value: { profile: { iss: 'issuer', sub: 'subject' } } } }
                    : { api: { management: { destinations: {
                        get: () => track('list', page),
                        byId: id => ({
                            get: () => track('settings', { id, name: id, kind: 'local', configurationRevision: 1 }),
                            routing: { get: () => track('routing', { destinationId: id, enabled: false, configurationRevision: 1, rules: [] }) },
                            work: { get: () => track('summary', { items: [] }) },
                        }),
                    } } }, cancellable: signal => ({ signal }), handleFailure() {} },
    });
    const run = compile('DeliveryWorkspace', context);
    let listed = false, read = false;
    const listTask = run('readList(false)').finally(() => { listed = true; });
    const editorTask = run("selected.value = 'destination-0'; read()").finally(() => { read = true; });
    for (let turn = 0; turn < 100; turn++) {
        await flushMicrotasks();
        for (const entry of requests) if (!entry.released) { entry.released = true; entry.gate.resolve(); }
        if (listed && read && active === 0) break;
    }
    await Promise.all([listTask, editorTask]);
    assert.equal(requests.filter(item => item.kind === 'summary').length, 6);
    assert.equal(requests.filter(item => item.kind === 'settings').length, 1);
    assert.equal(requests.filter(item => item.kind === 'routing').length, 1);
    assert.equal(maximum, 4, 'the six summary reads cannot exceed the shared limit alongside two editor reads');
});

test('a late Source display response cannot replace a newer owner read', async () => {
    const requests = [];
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch() {}, nextTick,
        defineProps: () => ({ sources: [{ id: 'source-1' }] }), defineEmits: () => () => {},
        onMounted() {}, onUnmounted() {}, ResizeObserver: class { observe() {} disconnect() {} },
        require: name => name.endsWith('/source-activity') ? { SourceActivityState: class { clear() {} }, activityPresentation: () => ({}), sourceActivityCounters: () => ({}), sourceStatusPosition: () => ({}) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
                : { api: { management: { sources: { byId: () => ({ display: { get: options => new Promise(resolve => requests.push({ resolve, signal: options.options[0].signal })) } }) } } }, cancellable: signal => ({ signal }), handleFailure() {}, readSourceAuthenticationObservations: async () => ({ items: [] }), readSourceActivity: async () => ({ items: [], epoch: '', coverageComplete: true }) },
    });
    const run = compile('SourceWorkspace', context);
    const older = run("readDisplay('source-1', 1)");
    const newer = run("readDisplay('source-1', 1)");
    assert.equal(requests[0].signal.aborted, true, 'a superseded transport is cancelled');
    requests[1].resolve({ id: 'source-1', configurationRevision: 2, displayName: 'newer' });
    await newer;
    requests[0].resolve({ id: 'source-1', configurationRevision: 1, displayName: 'older' });
    await older;
    assert.equal(run("display.value['source-1'].displayName"), 'newer');
});

test('mounted DeliveryWork invalidates a pending command when its editor unmounts', async () => {
    let rejectCommand;
    let activeContext;
    const emitted = [];
    const host = {
        createElement: type => ({ type, children: [] }),
        createText: text => ({ text }),
        createComment: text => ({ comment: text }),
        setText(node, text) { node.text = text; },
        setElementText(node, text) { node.text = text; },
        patchProp() {},
        insert(node, parent) { parent.children.push(node); },
        remove(node) { const index = node.parent?.children.indexOf(node) ?? -1; if (index >= 0) node.parent.children.splice(index, 1); },
        parentNode: node => node.parent ?? null,
        nextSibling: () => null,
    };
    const renderer = createRenderer(host);
    const props = reactive({ work: { id: 'delivery-mounted', assetId: 'asset', destinationId: 'destination', state: 'Failed' }, online: true, recoverySequence: 0 });
    const source = parse(readFileSync(new URL('../app/components/DeliveryWork.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    const app = renderer.createApp({
        setup(_props, { emit }) {
            const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch, nextTick,
                onUnmounted, defineProps: () => props, defineEmits: () => (...args) => { emitted.push(args); emit(...args); },
                require: name => name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
                    : name.endsWith('/workspace-interactions') ? { deliveryPresentation: () => ({ tone: 'attention', label: 'Failed', icon: 'i-lucide-x' }) }
                        : { api: { management: { deliveries: { byId: () => ({ retry: { post: () => new Promise((_resolve, reject) => { rejectCommand = reject; }) } }) } } }, handleFailure: failure => failure.status },
            });
            vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
            activeContext = context;
            return () => h('div');
        },
    });
    const root = { children: [] };
    app.mount(root);
    const pending = vm.runInContext("command('retry')", activeContext);
    app.unmount();
    rejectCommand({ status: 503 });
    await pending;
    assert.deepEqual(emitted, [], 'a disposed editor cannot request refresh after its pending command settles');
});

test('explicit destination reload keeps each draft locked until its matching authoritative read succeeds', async () => {
    let failSettings = false;
    let failRules = false;
    let releaseSettings;
    let releaseRules;
    let pendingSettings = false;
    let pendingRules = false;
    const ruleSaves = [];
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch() {}, onMounted() {}, onUnmounted() {}, nextTick,
        defineProps: () => ({ online: true, generation: 0, motionEnabled: false }), defineEmits: () => () => {},
        require: name => name.includes('kiota') ? { createUntypedString: value => ({ getValue: () => value }), createUntypedNumber: value => ({ getValue: () => value }), createUntypedNull: () => ({ getValue: () => null }) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
            : name.endsWith('/session') ? { accessGeneration: () => 1, session: { value: { profile: { iss: 'issuer', sub: 'subject' } } } }
                : { cancellable: signal => ({ signal }), handleFailure: () => 503, api: { management: { destinations: {
                    get: async () => ({ items: [] }),
                    byId: id => ({
                        get: async () => { if (failSettings) throw new Error('settings unavailable'); if (pendingSettings) await new Promise(resolve => { releaseSettings = resolve; }); return { id, name: 'Saved', configurationRevision: 2, kind: 'local', localRootPath: '/saved' }; },
                        routing: {
                            get: async () => { if (failRules) throw new Error('rules unavailable'); if (pendingRules) await new Promise(resolve => { releaseRules = resolve; }); return { destinationId: id, configurationRevision: 2, enabled: false, rules: [{ name: 'Saved rule', conditions: [] }] }; },
                            put: async body => { ruleSaves.push(body); return { destinationId: id, configurationRevision: 3, enabled: false, rules: body.rules }; },
                        },
                    }),
                } } } },
    });
    const run = compile('DeliveryWorkspace', context);
    await run("open('destination')");
    run('dirtySettings.value = true; dirtyRules.value = true; configConflict.value = true; ruleConflict.value = true');

    pendingSettings = true;
    const waiting = run('reloadSettings()');
    assert.equal(run('dirtySettings.value'), true);
    assert.equal(run('configConflict.value'), true, 'pending rereads keep settings mutation locked');
    assert.equal(run('busy.value'), true, 'the pending authoritative reread locks editor actions');
    releaseSettings();
    pendingSettings = false;
    await waiting;
    assert.equal(run('dirtySettings.value'), false);
    assert.equal(run('dirtyRules.value'), true);

    run('dirtySettings.value = true; configConflict.value = true');

    failSettings = true;
    await run('reloadSettings()');
    assert.equal(run('dirtySettings.value'), true);
    assert.equal(run('configConflict.value'), true, 'failed setting reads do not unlock writes');
    assert.equal(run('dirtyRules.value'), true, 'reloading settings does not discard rule drafts');

    failSettings = false;
    await run('reloadSettings()');
    assert.equal(run('dirtySettings.value'), false, 'successful settings reread explicitly replaces settings draft');
    assert.equal(run('configConflict.value'), false);
    assert.equal(run('dirtyRules.value'), true);
    assert.equal(run('ruleConflict.value'), true);

    failRules = true;
    await run('replaceRulesWithDraft()');
    assert.equal(run('dirtyRules.value'), true);
    assert.equal(run('ruleConflict.value'), true, 'a failed pre-replacement reread does not unlock an uncertain rule save');
    failRules = false;
    pendingRules = true;
    const replacing = run('replaceRulesWithDraft()');
    assert.equal(run('busy.value'), true);
    assert.equal(run('dirtyRules.value'), true);
    assert.equal(run('ruleConflict.value'), true, 'pending explicit replacement remains locked');
    releaseRules();
    pendingRules = false;
    assert.equal(await replacing, true);
    assert.equal(run('dirtyRules.value'), true, 'explicit replacement keeps the local draft');
    assert.equal(run('ruleConflict.value'), false, 'only a successful current route read unlocks explicit replacement');
    await run('saveRules()');
    assert.equal(ruleSaves.length, 1);
    assert.equal(ruleSaves[0].expectedRevision, 2, 'save uses the newly confirmed route revision');

    run('dirtyRules.value = true; ruleConflict.value = true');
    failRules = true;
    assert.equal(await run('reloadRules()'), false);
    assert.equal(run('dirtyRules.value'), true);
    assert.equal(run('ruleConflict.value'), true, 'failed discard/reload never clears local rule intent');
    failRules = false;
    assert.equal(await run('reloadRules()'), true);
    assert.equal(run('dirtyRules.value'), false);
    assert.equal(run('ruleConflict.value'), false);
});

test('Source refresh bounds all capture and display metadata requests to four in flight', async () => {
    const sources = Array.from({ length: 9 }, (_, index) => ({ id: `source-${index}`, name: `Source ${index}` }));
    let active = 0;
    let maximum = 0;
    let requests = 0;
    const pending = [];
    const request = () => {
        active++;
        requests++;
        maximum = Math.max(maximum, active);
        const gate = deferred();
        const item = { gate, released: false };
        pending.push(item);
        return gate.promise.finally(() => { active--; });
    };
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch() {}, onMounted() {}, onUnmounted() {}, nextTick, ResizeObserver: class { observe() {} disconnect() {} },
        defineProps: () => ({ sources }), defineEmits: () => () => {},
        require: name => name.endsWith('/source-activity') ? { SourceActivityState: class { clear() {} }, activityPresentation: () => ({}), sourceActivityCounters: () => ({}), sourceStatusPosition: () => ({}) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
                : { api: { management: { captures: { get: request }, sources: { byId: () => ({ display: { get: request } }) } } }, cancellable: signal => ({ signal }), handleFailure() {}, readSourceAuthenticationObservations: async () => ({ items: [] }), readSourceActivity: async () => ({ items: [], epoch: '', coverageComplete: true }) },
    });
    const run = compile('SourceWorkspace', context);
    let finished = false;
    const refresh = run('refreshRecent(props.sources)').finally(() => { finished = true; });
    for (let turn = 0; turn < 100 && !finished; turn++) {
        await flushMicrotasks();
        for (const item of pending) if (!item.released) { item.released = true; item.gate.resolve({ items: [] }); }
    }
    await refresh;
    assert.equal(requests, sources.length * 2);
    assert.ok(maximum <= 4, `observed ${maximum} in-flight metadata reads`);
});

test('Source refresh submits later owners while a first-wave transport remains pending', async () => {
    const sources = Array.from({ length: 6 }, (_, index) => ({ id: `source-${index}` }));
    const captures = [];
    const displays = [];
    const getCapture = options => new Promise((resolve, reject) => {
        const item = { resolve, reject, released: false, signal: options.options[0].signal };
        captures.push(item);
        item.signal.addEventListener('abort', () => reject(new Error('aborted')));
    });
    const getDisplay = options => new Promise((resolve, reject) => {
        const item = { resolve, reject, released: false, signal: options.options[0].signal };
        displays.push(item);
        item.signal.addEventListener('abort', () => reject(new Error('aborted')));
    });
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch() {}, onMounted() {}, onUnmounted() {}, nextTick, ResizeObserver: class { observe() {} disconnect() {} },
        defineProps: () => ({ sources }), defineEmits: () => () => {},
        require: name => name.endsWith('/source-activity') ? { SourceActivityState: class { clear() {} }, activityPresentation: () => ({}), sourceActivityCounters: () => ({}), sourceStatusPosition: () => ({}) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
                    : { api: { management: { captures: { get: getCapture }, sources: { byId: () => ({ display: { get: getDisplay } }) } } }, cancellable: signal => ({ signal }), handleFailure() {}, readSourceAuthenticationObservations: async () => ({ items: [] }), readSourceActivity: async () => ({ items: [], epoch: '', coverageComplete: true }) },
    });
    const run = compile('SourceWorkspace', context);
    const refresh = run('refreshRecent(props.sources)');
    assert.equal(captures.length, 4, 'the pool starts only four capture transports');
    captures[0].resolve({ items: [] });
    await flushMicrotasks();
    assert.equal(captures.length, 5, 'the queued fifth Source begins while the other first-wave reads remain pending');
    assert.equal(displays.length, 0, 'the display read queues behind already-submitted capture reads');
    captures[0].released = true;
    let finished = false;
    void refresh.then(() => { finished = true; });
    for (let turn = 0; turn < 100 && !finished; turn++) {
        await flushMicrotasks();
        for (const item of captures) if (!item.released) { item.released = true; item.resolve({ items: [] }); }
        for (const item of displays) if (!item.released) { item.released = true; item.resolve({ id: 'source', configurationRevision: 1 }); }
    }
    await refresh;
});

test('same-ID Source recovery cancels pending metadata and starts a fresh pass', async () => {
    const source = { id: 'source-1', name: 'Camera' };
    const requests = [];
    const get = options => new Promise((resolve, reject) => {
        const signal = options.options[0].signal;
        const entry = { resolve, signal };
        requests.push(entry);
        signal.addEventListener('abort', () => reject(new Error('aborted')));
    });
    const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch() {}, onMounted() {}, onUnmounted() {}, nextTick, ResizeObserver: class { observe() {} disconnect() {} },
        defineProps: () => ({ sources: [source] }), defineEmits: () => () => {},
        require: name => name.endsWith('/source-activity') ? { SourceActivityState: class { clear() {} }, activityPresentation: () => ({}), sourceActivityCounters: () => ({}), sourceStatusPosition: () => ({}) }
            : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                : name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
                : { api: { management: { captures: { get }, sources: { byId: () => ({ display: { get } }) } } }, cancellable: signal => ({ signal }), handleFailure() {}, readSourceAuthenticationObservations: async () => ({ items: [] }), readSourceActivity: async () => ({ items: [], epoch: '', coverageComplete: true }) },
    });
    const run = compile('SourceWorkspace', context);
    const first = run('refreshRecent([props.sources[0]])');
    assert.equal(requests.length, 1);
    const second = run('refreshRecent([props.sources[0]])');
    assert.equal(requests[0].signal.aborted, true, 'a same-ID authoritative refresh cancels its obsolete transport');
    await flushMicrotasks();
    assert.equal(requests.length, 2, 'the replacement pass starts after cancellation');
    requests[1].resolve({ items: [] });
    await flushMicrotasks();
    assert.equal(requests.length, 3, 'display metadata is read after the bounded capture request');
    requests[2].resolve({ id: 'source-1', configurationRevision: 1 });
    await Promise.all([first, second]);
});

test('mounted Source watchers refresh unchanged IDs after recovery, activity, and revision replacement', async () => {
    const props = reactive({ sources: [{ id: 'source-1', configurationRevision: 1 }], recoverySequence: 0, activity: undefined, connected: true });
    let metadataReads = 0;
    let active = 0;
    let maximum = 0;
    const started = [];
    const tracked = (kind, value) => {
        active++; maximum = Math.max(maximum, active);
        const gate = deferred();
        const request = { kind, release: () => gate.resolve(value), released: false };
        started.push(request);
        return gate.promise.finally(() => { active--; });
    };
    const request = kind => { metadataReads++; return tracked(kind, { items: [] }); };
    const host = {
        createElement: type => ({ type, children: [] }), createText: text => ({ text }), createComment: text => ({ comment: text }),
        setText(node, text) { node.text = text; }, setElementText(node, text) { node.text = text; }, patchProp() {},
        insert(node, parent) { parent.children.push(node); }, remove() {}, parentNode: () => null, nextSibling: () => null,
    };
    const renderer = createRenderer(host);
    const source = parse(readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    const app = renderer.createApp({ setup() {
        const context = vm.createContext({ exports: {}, AbortController, ref, shallowRef, reactive, computed, watch, nextTick, onUnmounted, clearInterval, clearTimeout, setInterval: () => 1,
            onMounted: callback => callback(), defineProps: () => props, defineEmits: () => () => {}, ResizeObserver: class { observe() {} disconnect() {} },
            window: { addEventListener() {}, removeEventListener() {} },
            require: name => name.endsWith('/source-activity') ? { SourceActivityState: class { constructor() { this.items = new Map(); this.available = true; this.revision = 0; } clear() {} reconcile() {} observe() {} }, activityPresentation: () => ({}), sourceActivityCounters: () => ({}), sourceStatusPosition: () => ({}) }
                : name.endsWith('/destination-capabilities') ? destinationCapabilityModule : name.endsWith('/read-pool') ? readPoolModule
                    : name.endsWith('/session') ? { accessGeneration: () => 1, onAccessLost: () => () => {} }
                    : { api: { management: { captures: { get: () => request('capture') }, sourceCapabilities: { get: () => tracked('capabilities', { profiles: [] }) }, sources: { byId: () => ({ display: { get: () => request('display') } }) } } }, cancellable: signal => ({ signal }), handleFailure() {}, readSourceAuthenticationObservations: async () => tracked('authentication', { items: [] }), readSourceActivity: async () => tracked('activity', { items: [], epoch: '', coverageComplete: true }) },
        });
        vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
        return () => h('div');
    } });
    app.mount({ children: [] });
    const settle = async () => {
        let quiet = 0, previous = -1;
        for (let turn = 0; turn < 100; turn++) {
            await nextTick();
            await flushMicrotasks();
            for (const request of started) if (!request.released) { request.released = true; request.release(); }
            await flushMicrotasks();
            if (active === 0 && previous === started.length) {
                if (++quiet === 3) return;
            } else quiet = 0;
            previous = started.length;
        }
        assert.fail('Source metadata reads did not settle through deferred completion gates');
    };
    await settle();
    assert.equal(metadataReads, 2, 'initial inventory reads captures and display once');
    props.recoverySequence++;
    await nextTick(); await settle();
    assert.equal(metadataReads, 4, 'same-ID reconnect/resync performs another authoritative pass');
    props.activity = { captureId: 'capture-1', kind: 'captureChanged', sequence: 1 };
    await nextTick(); await settle();
    assert.equal(metadataReads, 6, 'same-ID capture changes refresh the recent strip');
    props.sources[0].configurationRevision++;
    await nextTick(); await settle();
    assert.equal(metadataReads, 8, 'replacement metadata with the same ID is observed');
    assert.ok(maximum <= 4, `captures, display, activity, authentication and capabilities peaked at ${maximum} concurrent reads`);
    app.unmount();
});

test('read pool cancels queued work, does not grant its permit, and releases a slot after rejection', async () => {
    const pool = readPoolModule.createReadPool(1);
    const active = new AbortController();
    const cancelled = new AbortController();
    const survivor = new AbortController();
    let rejectFirst;
    let resolveSurvivor;
    let startedSurvivor;
    const survivorStarted = new Promise(resolve => { startedSurvivor = resolve; });
    const started = [];
    const first = pool.run(active.signal, () => { started.push('first'); return new Promise((_resolve, reject) => { rejectFirst = reject; }); });
    const queued = pool.run(cancelled.signal, async () => { started.push('cancelled'); return 'wrong'; });
    const last = pool.run(survivor.signal, () => { started.push('survivor'); startedSurvivor(); return new Promise(resolve => { resolveSurvivor = resolve; }); });
    cancelled.abort();
    await assert.rejects(queued);
    assert.deepEqual(started, ['first'], 'cancelled queued work never consumes the available slot');
    rejectFirst(new Error('request failed'));
    await assert.rejects(first, /request failed/);
    await survivorStarted;
    assert.deepEqual(started, ['first', 'survivor'], 'a rejection releases its permit to the next live waiter');
    resolveSurvivor('done');
    assert.equal(await last, 'done');
});
