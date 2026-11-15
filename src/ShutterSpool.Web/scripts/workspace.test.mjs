import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { parse, compileTemplate, compileScript } from '@vue/compiler-sfc';
import * as Vue from 'vue';
import { ref, shallowRef, reactive, computed, watch, nextTick, effectScope } from 'vue';

function moduleAt(name, overrides = {}) {
    const path = ['useGalleryGrid', 'useWorkspaceClock', 'usePhotoViewer', 'useViewerSession', 'useCurationVisit', 'useCaptureBrowser', 'useCaptureCurationActions', 'useWorkspacePreferences'].includes(name) ? `../app/composables/${name}.ts` : `../app/lib/${name}.ts`;
    const moduleWatch = name === 'useWorkspacePreferences' ? watch : () => {};
    const context = vm.createContext({ exports: {}, AbortController, URL, URLSearchParams, setTimeout, clearTimeout, ...overrides.globals,
        require: name => name === 'vue' ? overrides.vue ?? { ref, reactive, computed, watch: moduleWatch, nextTick, onMounted() {}, onUnmounted() {}, onScopeDispose() {}, provide() {}, inject: (_key, fallback) => fallback }
            : name.endsWith('/api') ? overrides.api ?? { api: {}, handleFailure: error => error.responseStatusCode }
                : name.endsWith('/session') ? overrides.session ?? { accessGeneration: () => 0 }
                    : name.endsWith('/retention') ? moduleAt('retention')
                        : name.endsWith('/workspace-preferences') ? moduleAt('workspace-preferences')
                            : name.endsWith('/curation-session') ? moduleAt('curation-session')
                                : name.endsWith('/useCurationVisit') ? moduleAt('useCurationVisit')
                                    : moduleAt('workspace-interactions'),
    });
    vm.runInContext(ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return context.exports;
}
function workspace(read, batch = async () => ({ items: [] }), browser = {}) {
    let generation = 0; let lost; let dispose;
    const session = ref({ profile: { iss: 'synthetic', sub: 'operator' } });
    const testApi = browser.api ?? { management: { captures: {
        byId: id => ({ get: options => read(id, options), favorite: { put: browser.favorite ?? (async () => {}) }, trash: { post: browser.trash ?? (async () => {}) }, curation: { put: async body => ({ ...body, revision: body.expectedRevision + 1 }) } }),
        batch: { curation: { post: batch }, hold: { post: batch }, restore: { post: batch }, release: { post: batch } },
    } } };
    class WorkspaceHTMLElement {}
    const documentState = { activeElement: undefined, fullscreenElement: undefined, querySelector: () => undefined, elementFromPoint: () => undefined, exitFullscreen: async () => {}, addEventListener() {}, removeEventListener() {}, ...browser.document };
    const modules = { curation: moduleAt('curation-session'), interactions: moduleAt('workspace-interactions'), preferences: moduleAt('workspace-preferences'), preferencesComposable: moduleAt('useWorkspacePreferences'), retention: moduleAt('retention'), slideshow: moduleAt('gallery-slideshow'), chrome: moduleAt('fullscreen-chrome'), grid: moduleAt('useGalleryGrid'), clock: moduleAt('useWorkspaceClock'), viewer: moduleAt('usePhotoViewer'), viewerSession: moduleAt('useViewerSession', { globals: { document: documentState, HTMLElement: WorkspaceHTMLElement }, vue: { ...Vue, nextTick: browser.nextTick ?? nextTick, onMounted() {}, onUnmounted() {} } }), curationVisit: moduleAt('useCurationVisit'), curationActions: moduleAt('useCaptureCurationActions', { api: { api: testApi, cancellable: signal => ({ signal }), handleFailure: error => error.responseStatusCode }, session: { accessGeneration: () => generation } }), captureBrowser: moduleAt('useCaptureBrowser') };
    const context = vm.createContext({
        exports: {}, AbortController, HTMLElement: WorkspaceHTMLElement, structuredClone, session, localStorage: browser.storage, clearTimeout, setTimeout, useRouter: () => browser.router ?? ({ replace: async () => {}, beforeEach: () => () => {} }), ref, reactive, computed, defineProps: () => browser.props ?? ({}),
        watch: browser.watch ?? (() => {}), onMounted() {}, onUnmounted(callback) { dispose = callback; }, onBeforeRouteLeave() {}, defineExpose: value => Object.assign(browser.exposed ??= {}, value), nextTick: browser.nextTick ?? (async () => {}), shallowRef,
        window: { confirm: () => true, scrollY: 640, scrollBy() {}, scrollTo() {}, addEventListener() {}, removeEventListener() {}, ...browser.window },
        document: documentState,
        require: name => name.endsWith('/api') ? { api: { management: {
            sources: { get: browser.sources ?? (async () => ({ items: [{ curationMode: 'Manual' }] })) },
            captures: { get: browser.page ?? (async () => ({ items: [], nextCursor: null })), byId: id => ({ get: options => read(id, options), curation: { put: async body => ({ ...body, revision: body.expectedRevision + 1 }) } }), batch: { curation: { post: batch }, hold: { post: batch }, restore: { post: batch }, release: { post: batch } } },
        } }, cancellable: signal => ({getKey: () => 'shutterspool.abort', signal}), handleFailure: browser.handleFailure ?? (error => error.responseStatusCode) }
            : name.endsWith('/session') ? { session, accessGeneration: () => generation, onAccessLost: callback => { lost = callback; return () => {}; } }
                    : name.endsWith('/reconcile') ? { useReconciliation: refresh => ({ online: ref(true), connected: ref(true), activity: ref(undefined), authActivity: ref(undefined), phaseActivity: ref(undefined), unassignedRejection: ref(false), recoverySequence: ref(0), connectionRecovery: ref(0), initialize: async () => {}, invalidate: (scopes, ids, previews) => refresh(ids, previews, new Set(typeof scopes === 'string' ? [scopes] : scopes)), refresh: () => refresh(undefined, undefined, new Set(['capture', 'previews', 'sources', 'delivery', 'storage', 'erasure'])) }) }
                    : name.endsWith('/workspace-preferences') ? modules.preferences : name.endsWith('/useWorkspacePreferences') ? modules.preferencesComposable : name.endsWith('/useGalleryGrid') ? modules.grid : name.endsWith('/useWorkspaceClock') ? modules.clock : name.endsWith('/usePhotoViewer') ? modules.viewer : name.endsWith('/useViewerSession') ? modules.viewerSession : name.endsWith('/useCurationVisit') ? modules.curationVisit : name.endsWith('/useCaptureCurationActions') ? modules.curationActions : name.endsWith('/useCaptureBrowser') ? modules.captureBrowser : name.endsWith('/read-pool') ? moduleAt('read-pool') : name.endsWith('/curation-session') ? modules.curation : name.endsWith('/retention') ? modules.retention : name.endsWith('/gallery-slideshow') ? modules.slideshow : name.endsWith('/fullscreen-chrome') ? modules.chrome : name.endsWith('/workspace-context') ? moduleAt('workspace-context') : modules.interactions,
        loseAccess() { generation++; session.value = null; lost(); },
    });
    const source = parse(readFileSync(new URL('../app/components/OperationalWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run.dispose = () => dispose?.();
    run.mount = () => {
        const file = readFileSync(new URL('../app/components/OperationalWorkspace.vue', import.meta.url), 'utf8');
        const result = compileTemplate({ source: parse(file).descriptor.template.content, filename: 'OperationalWorkspace.vue', id: 'workspace-lifecycle' });
        assert.deepEqual(result.errors, []);
        const template = vm.createContext({ exports: {}, require: () => Vue });
        vm.runInContext(ts.transpileModule(result.code, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, template);
        const declarations = ts.createSourceFile('workspace.ts', source, ts.ScriptTarget.Latest, true).statements;
        const names = declarations.flatMap(statement => ts.isVariableStatement(statement)
            ? statement.declarationList.declarations.flatMap(declaration => ts.isIdentifier(declaration.name) ? [declaration.name.text] : ts.isObjectBindingPattern(declaration.name) ? declaration.name.elements.map(element => element.name.getText()) : [])
            : ts.isFunctionDeclaration(statement) && statement.name ? [statement.name.text] : []);
        return mountTemplate(template.exports.render, run('({' + names.join(',') + '})'), browser.document, browser);
    };
    return run;
}

function mountTemplate(render, bindings, documentOverrides = {}, browser = {}) {
    const node = (type, text = '') => ({ type, text, tagName: type.toUpperCase(), props: {}, children: [], parent: null, style: {}, options: [], focus() {}, closest() {}, addEventListener() {}, removeEventListener() {}, setPointerCapture() {}, close() { this.props.open = false; }, showModal() { this.props.open = true; }, getAttribute(key) { return this.props[key] ?? null; } });
    const header = node('header'); const body = node('body'); const root = node('root');
    const renderer = Vue.createRenderer({
        createElement: type => node(type), createText: text => node('#text', text), createComment: text => node('#comment', text),
        setText: (element, text) => { element.text = text; },
        setElementText: (element, text) => { element.text = text; element.children = []; },
        patchProp: (element, key, _previous, value) => { element.props[key] = value; },
        insert(element, parent, anchor = null) {
            if (element.parent) element.parent.children.splice(element.parent.children.indexOf(element), 1);
            element.parent = parent;
            const position = anchor ? parent.children.indexOf(anchor) : -1;
            parent.children.splice(position < 0 ? parent.children.length : position, 0, element);
        },
        remove(element) { element.parent?.children.splice(element.parent.children.indexOf(element), 1); element.parent = null; },
        parentNode: element => element.parent,
        nextSibling: element => element.parent?.children[element.parent.children.indexOf(element) + 1] ?? null,
        querySelector: selector => selector === '.app-header' ? header : body,
    });
    const app = renderer.createApp({ setup: () => bindings, render });
    app.config.warnHandler = message => { throw new Error(`Unexpected Vue warning: ${message}`); };
    const controls = parse(readFileSync(new URL('../app/components/CurationControls.vue', import.meta.url), 'utf8')).descriptor;
    const compiled = compileScript(controls, { id: 'curation-controls', inlineTemplate: true });
    const context = vm.createContext({ exports: {}, require: () => Vue });
    vm.runInContext(ts.transpileModule(compiled.content, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    app.component('CurationControls', context.exports.default);
    const ratingFilter = parse(readFileSync(new URL('../app/components/CaptureRatingFilter.vue', import.meta.url), 'utf8')).descriptor;
    const compiledRatingFilter = compileScript(ratingFilter, { id: 'capture-rating-filter', inlineTemplate: true });
    const ratingContext = vm.createContext({ exports: {}, document: { elementFromPoint: () => undefined, ...documentOverrides }, require: name => name.endsWith('/workspace-interactions') ? moduleAt('workspace-interactions') : Vue });
    vm.runInContext(ts.transpileModule(compiledRatingFilter.content, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, ratingContext);
    app.component('CaptureRatingFilter', ratingContext.exports.default);
    if (browser.realTrashAction) {
        const trashAction = parse(readFileSync(new URL('../app/components/CaptureTrashAction.vue', import.meta.url), 'utf8')).descriptor;
        const compiledTrashAction = compileScript(trashAction, { id: 'capture-trash-action', inlineTemplate: true });
        const trashContext = vm.createContext({ exports: {}, ref, watch, window: { confirm: () => true }, require: name => name.endsWith('/retention') ? moduleAt('retention') : Vue });
        vm.runInContext(ts.transpileModule(compiledTrashAction.content, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, trashContext);
        app.component('CaptureTrashAction', trashContext.exports.default);
    }
    for (const name of ['UIcon', 'RatingStar', 'CaptureRating', 'AuthorizedPreview', 'CaptureEvidence', 'SourceWorkspace', 'DeliveryWorkspace', 'ReleaseLifetime', 'ReevaluateAction', 'RetentionLifetime', 'FavoriteControl', 'RetentionDetails', ...(browser.realTrashAction ? [] : ['CaptureTrashAction']), 'CaptureResumeAction', 'NoDeliveryIntentAction', 'PrivacyErasurePanel', 'TrashWorkspace', 'RetentionSettings', 'CollectionPicker', 'CollectionMove']) {
        app.component(name, { props: ['capture', 'captureId', 'gridMaximumDimension', 'active', 'deliveryOnly'], setup: props => () => Vue.h('mock-' + name, { captureId: props.captureId, active: props.active, deliveryOnly: props.deliveryOnly }, props.capture?.displayName ?? '') });
    }
    for (const name of ['LazySourceWorkspace', 'LazyDeliveryWorkspace', 'LazyTrashWorkspace', 'LazyRetentionSettings']) {
        app.component(name, { props: ['capture', 'captureId', 'gridMaximumDimension'], setup: props => () => Vue.h('mock-' + name, { captureId: props.captureId }, props.capture?.displayName ?? '') });
    }
    const gridPhoto = parse(readFileSync(new URL('../app/components/CaptureGridPhoto.vue', import.meta.url), 'utf8')).descriptor;
    const compiledGridPhoto = compileScript(gridPhoto, { id: 'capture-grid-photo', inlineTemplate: true });
    const gridPhotoContext = vm.createContext({ exports: {}, computed, require: () => Vue });
    vm.runInContext(ts.transpileModule(compiledGridPhoto.content, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, gridPhotoContext);
    app.component('CaptureGridPhoto', gridPhotoContext.exports.default);
    const filmstrip = parse(readFileSync(new URL('../app/components/CaptureFilmstrip.vue', import.meta.url), 'utf8')).descriptor;
    const compiledFilmstrip = compileScript(filmstrip, { id: 'capture-filmstrip', inlineTemplate: true });
    const filmstripContext = vm.createContext({ exports: {}, require: () => Vue });
    vm.runInContext(ts.transpileModule(compiledFilmstrip.content, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, filmstripContext);
    app.component('CaptureFilmstrip', filmstripContext.exports.default);
    app.component('ApertureBackground', { props: ['view'], setup: props => () => Vue.h('mock-aperture', { class: 'aperture-fixture', view: props.view }) });
    app.mount(root);
    function find(type) {
        const search = element => String(element.props.class ?? '').split(' ').includes(type) ? element : element.children.map(search).find(Boolean);
        return search(root);
    }
    return { root, find, close: () => app.unmount() };
}

test('explicit Capture links retain the requested photo despite Curation membership', async () => {
    for (const released of [false, true]) {
        const requested = { id: 'requested', displayName: 'REQUESTED.JPG', curation: { revision: 1, rating: 0, releasedAt: released ? new Date() : null } };
        const run = workspace(async id => id === 'requested' ? requested : { id, curation: { revision: 1 } }, undefined, {
            props: { captureId: 'requested' },
            page: async () => ({ items: [{ id: 'waiting', curation: { revision: 1, rating: 5 } }] }),
        });
        run('filters.Curation.rating = 5; filters.Curation.ratingMaximum = 5');
        await run('refresh()');
        assert.equal(run('view.value'), 'Gallery');
        assert.equal(run('activeId.value'), 'requested');
        assert.equal(run('capture.value.displayName'), 'REQUESTED.JPG');
    }
});

function sourceWorkspace(read, sources = [{ id: 'a', name: 'A', configurationRevision: 1 }, { id: 'b', name: 'B', configurationRevision: 1 }], authRead = async () => ({ coverageComplete: true, items: [] }), phaseRead = async () => ({ epoch: 'one', coverageComplete: true, items: [] }), management = {}, watchProperties = false) {
    const props = reactive({ sources, connected: true, gridMaximumDimension: 512 });
    let generation = 0; let accessLost;
    const context = vm.createContext({
        exports: {}, ref, reactive, computed, nextTick, Blob, URL: { createObjectURL: () => 'blob:synthetic', revokeObjectURL() {} }, clearTimeout, setTimeout, clearInterval, setInterval, onMounted() {}, AbortController, HTMLElement: class {}, window: { confirm: () => true }, document: { getElementById: () => ({ focus() {} }) },
        defineProps: () => props, defineEmits: () => () => {}, onUnmounted() {},
        watch: (target, callback, options) => Vue.isRef(target) || watchProperties ? watch(target, callback, { ...options, flush: 'sync' }) : () => {},
        require: name => name.endsWith('/read-pool') ? moduleAt('read-pool') : name.endsWith('/source-activity') ? moduleAt('source-activity') : name.endsWith('/session') ? { accessGeneration: () => generation, onAccessLost: callback => { accessLost = callback; return () => {}; } } : {
            cancellable: signal => ({getKey: () => 'shutterspool.abort', signal}), handleFailure: error => error.responseStatusCode ?? 503, readSourceAuthenticationObservations: authRead, readSourceActivity: phaseRead, api: { management: { captures: { get: read }, ...management } },
        },
    });
    const source = parse(readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const run = expression => vm.runInContext(expression, context);
    run.loseAccess = () => { generation++; accessLost(); };
    run.mount = (motionEnabled = false) => {
        const file = readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8');
        const result = compileTemplate({ source: parse(file).descriptor.template.content, filename: 'SourceWorkspace.vue', id: 'source-history' });
        assert.deepEqual(result.errors, []);
        const template = vm.createContext({ exports: {}, require: () => ({ ...Vue, TransitionGroup: { setup: (_, { slots }) => () => Vue.h('div', slots.default?.()) } }) });
        vm.runInContext(ts.transpileModule(result.code, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, template);
        const declarations = ts.createSourceFile('source.ts', source, ts.ScriptTarget.Latest, true).statements;
        const names = declarations.flatMap(statement => ts.isVariableStatement(statement)
            ? statement.declarationList.declarations.filter(declaration => ts.isIdentifier(declaration.name)).map(declaration => declaration.name.text)
            : ts.isFunctionDeclaration(statement) && statement.name ? [statement.name.text] : []);
        return mountTemplate(template.exports.render, { ...run('({' + names.join(',') + '})'), sources, connected: computed(() => props.connected), gridMaximumDimension: props.gridMaximumDimension, motionEnabled, previewVersions: {} });
    };
    return run;
}

test('erasure forgets only its Capture and fences an older pending inbox response', async () => {
    let finish;
    const run = workspace(async id => ({ id }), undefined, { page: () => new Promise(resolve => finish = resolve) });
    run("rows.value = [{id:'erased',displayName:'SYNTHETIC_ERASE.JPG'},{id:'keep',displayName:'SYNTHETIC_KEEP.JPG'}]; activeId.value='keep'; capture.value={id:'keep',displayName:'SYNTHETIC_KEEP.JPG'}; tagDrafts.set('keep','Unrelated draft'); tagDrafts.set('erased','Synthetic erased draft')");
    const pending = run('loadPage(false)'); await run("forgetUnavailableCapture('erased')");
    finish({ items: [{ id: 'erased', displayName: 'SYNTHETIC_ERASE.JPG' }], nextCursor: null }); await pending;
    assert.equal(run("rows.value.map(row=>String(row.id)).join(',')"), 'keep');
    assert.equal(run('activeId.value'), 'keep'); assert.equal(run('capture.value.displayName'), 'SYNTHETIC_KEEP.JPG');
    assert.equal(run("tagDrafts.get('keep')"), 'Unrelated draft'); assert.equal(run("tagDrafts.has('erased')"), false);
});

test('confirmed missing Capture clears private detail/drafts while a temporary outage preserves them', async () => {
    for (const status of [404, 503]) {
        const run = workspace(async () => { throw { responseStatusCode: status }; });
        run("activeId.value='missing'; capture.value={id:'missing',displayName:'SYNTHETIC.JPG'}; rows.value=[{id:'missing',displayName:'SYNTHETIC.JPG'}]; tagDrafts.set('missing','Synthetic draft')");
        await run("openCapture('missing')");
        if (status === 404) {
            assert.equal(run('capture.value'), undefined); assert.equal(run('activeId.value'), '');
            assert.equal(run("tagDrafts.has('missing')"), false); assert.equal(run('rows.value.length'), 0);
            assert.match(run('error.value'), /no longer available/);
        } else {
            assert.equal(run('capture.value.displayName'), 'SYNTHETIC.JPG'); assert.equal(run('activeId.value'), 'missing');
            assert.equal(run("tagDrafts.get('missing')"), 'Synthetic draft');
            assert.match(run('detailError.value'), /Unable to open/);
        }
    }
});

test('erasure during a targeted read cannot shift its result onto an unrelated tile', async () => {
    let finish;
    const run = workspace(id => new Promise(resolve => finish = resolve), undefined, { page: async request => {
        if (request?.queryParameters?.released === false) return { items: [] };
        throw { responseStatusCode: 503 };
    } });
    run("initialized.value=true; rows.value=[{id:'erased',displayName:'SYNTHETIC_ERASE.JPG'},{id:'read',displayName:'SYNTHETIC_OLD.JPG'},{id:'keep',displayName:'SYNTHETIC_KEEP.JPG'}]");
    const pending = run("refresh(new Set(['read']))"); await new Promise(resolve => setImmediate(resolve));
    await run("forgetUnavailableCapture('erased')"); finish({ id: 'read', displayName: 'SYNTHETIC_FRESH.JPG' }); await pending;
    assert.equal(run("rows.value.map(row=>String(row.id)).join(',')"), 'read,keep');
    assert.equal(run('rows.value[0].displayName'), 'SYNTHETIC_FRESH.JPG'); assert.equal(run('rows.value[1].displayName'), 'SYNTHETIC_KEEP.JPG');
});

test('leaving the photo browser aborts its pending page transport and fences the late response', async () => {
    let signal; let resolve;
    const run = workspace(async () => undefined, undefined, { page: request => {
        signal = request.options?.[0]?.signal;
        return new Promise(done => { resolve = done; });
    } });
    run("initialized.value = true; view.value = 'Gallery'; rows.value = [{ id: 'retained' }]");
    const pending = run('loadPage(false)');
    await settle();
    assert.ok(signal);
    await run("switchView('Sources')");
    assert.equal(signal.aborted, true);
    resolve({ items: [{ id: 'stale' }], nextCursor: null });
    await pending;
    assert.equal(run("rows.value.map(row => row.id).join(',')"), 'retained');
});

test('targeted reconciliation fences late success and failure after view, query, or workspace ownership changes', async () => {
    for (const ownerChange of ['view', 'query', 'dispose']) {
        const scope = effectScope();
        let finish; let requestSignal; let failures = 0; let fullPageReads = 0; let detailReads = 0;
        const run = scope.run(() => workspace((id, request) => {
            if (detailReads++ > 0) return Promise.resolve({ id, displayName: 'new query detail' });
            requestSignal = request?.options?.[0]?.signal;
            return new Promise((resolve, reject) => { finish = ownerChange === 'query' ? () => reject({ responseStatusCode: 503 }) : () => resolve({ id, displayName: 'stale detail' }); });
        }, undefined, {
            watch,
            handleFailure: error => { failures++; return error.responseStatusCode; },
            page: async request => {
                if (request.queryParameters.released === false) return { items: [] };
                fullPageReads++;
                return { items: [{ id: 'a', displayName: 'new query page' }], nextCursor: null };
            },
        }));
        try {
            run("view.value='Gallery'; initialized.value=true; rows.value=[{id:'a',displayName:'original row'}]");
            const pending = run("refreshData(new Set(['a']), undefined, new Set(['capture']))");
            await settle();
            assert.ok(requestSignal, 'targeted detail read starts after the waiting-count read');
            if (ownerChange === 'view') run("view.value='Sources'");
            else if (ownerChange === 'query') run("filters.Gallery.tag='changed query'");
            else run.dispose();
            await settle();
            assert.equal(requestSignal.aborted, true);
            finish();
            await pending;
            await settle();
            assert.equal(failures, 0, 'stale read failures never reach shared authentication/error handling');
            if (ownerChange === 'query') assert.equal(fullPageReads, 1, 'the changed query may start its own page read');
            assert.equal(run('rows.value[0].displayName'), 'original row', 'the old owner cannot overwrite the loaded workspace row');
        } finally { scope.stop(); }
    }
});

test('initial Immediate Sources snapshot can choose Gallery and still complete its Capture page read', async () => {
    let inboxReads = 0;
    const run = workspace(async id => ({ id }), undefined, {
        watch,
        sources: async () => ({ items: [{ id: 'source', curationMode: 'Immediate' }] }),
        page: async request => {
            if (request.queryParameters.released === false) return { items: [] };
            inboxReads++;
            return { items: [{ id: 'gallery-row' }], nextCursor: null };
        },
    });
    await run("refreshData(undefined, undefined, new Set(['sources','capture']))");
    assert.equal(run('view.value'), 'Gallery');
    assert.equal(inboxReads, 1, 'changing the initial view inside its own refresh does not abort that refresh');
    assert.equal(run('rows.value[0].id'), 'gallery-row');
});

test('Capture browser retains page cancellation ownership until continuation explicitly finishes', async () => {
    const browser = moduleAt('useCaptureBrowser').useCaptureBrowser();
    const parent = new AbortController();
    const completed = browser.requestPage({ query: {}, parentSignal: parent.signal, read: async () => ({ items: [] }) });
    await completed.response;
    const continuationSignal = browser.requestSignal(completed.request);
    assert.ok(continuationSignal, 'the request remains owned after its page GET settles');
    parent.abort();
    assert.equal(continuationSignal.aborted, true, 'the parent can cancel boundary/detail work that reuses the request signal');
    assert.equal(browser.isCurrent(completed.request), false);
    completed.finish();

    const first = browser.beginRequest();
    const signal = browser.requestSignal(first);
    browser.rows.value = [{ id: 'gallery' }];
    browser.save('Gallery');
    browser.rows.value = [{ id: 'curation' }];
    browser.save('Curation');
    const second = browser.beginRequest();
    assert.equal(signal.aborted, true);
    assert.equal(browser.isCurrent(first), false);
    browser.restore('Gallery');
    assert.equal(browser.rows.value[0].id, 'gallery');
    assert.ok(browser.requestSignal(second));
    browser.cancelRequests();
    assert.equal(browser.requestSignal(second), undefined);
});

test('page continuation boundary and detail transports abort when navigation supersedes the page owner', async () => {
    for (const continuation of ['boundary', 'detail']) {
        let detailSignal; let resolveDetail;
        const run = workspace((id, request) => {
            detailSignal = request?.options?.[0]?.signal;
            return new Promise(resolve => { resolveDetail = resolve; });
        }, undefined, {
            page: async request => request.queryParameters.released === false ? { items: [] } : continuation === 'boundary'
                ? { items: [{ id: 'incoming', acceptedAt: new Date('2026-01-01T00:00:00.000Z') }], nextCursor: 'next' }
                : { items: [{ id: 'detail-target' }], nextCursor: null },
        });
        run(continuation === 'boundary'
            ? "view.value='Gallery'; rows.value=[{id:'cached-boundary',acceptedAt:new Date('2026-01-01T00:00:00.000Z')}]; activeId.value='cached-boundary'"
            : "view.value='Gallery'; rows.value=[]; activeId.value=''");
        const pending = run(continuation === 'boundary' ? 'loadPage(false, true)' : 'loadPage(false)');
        await settle();
        assert.ok(detailSignal, `${continuation} continuation starts after the page transport resolves`);
        await run("switchView('Sources')");
        assert.equal(detailSignal.aborted, true, `${continuation} continuation shares cancellable page ownership`);
        resolveDetail({ id: 'late', displayName: 'stale continuation' });
        await pending;
        assert.equal(run('rows.value.some(row => row.displayName === "stale continuation")'), false);
    }
});

test('bulk metadata reads preserve selection order while honoring their four-request bound', async () => {
    const { mapBounded } = moduleAt('useCurationVisit');
    let active = 0; let peak = 0;
    const result = await mapBounded(Array.from({ length: 13 }, (_, index) => index), 4, async value => {
        active++;
        peak = Math.max(peak, active);
        await new Promise(resolve => setTimeout(resolve, value % 3));
        active--;
        return value * 2;
    });
    assert.equal(peak, 4);
    assert.deepEqual(Array.from(result), Array.from({ length: 13 }, (_, index) => index * 2));
});

test('exposed departure guard preserves editor drafts when the operator cancels or approves navigation', () => {
    let allow = false; let prompts = 0;
    const run = workspace(async () => undefined, undefined, { window: { confirm: () => { prompts++; return allow; } } });
    run("dirtyEditors.Sources = true; curationVisit.activeTag.value = 'unfinished tag'; bulkTag.value='unfinished bulk tag'");
    assert.equal(run('requestDeparture()'), false);
    assert.equal(prompts, 1);
    assert.equal(run('dirtyEditors.Sources'), true);
    assert.equal(run("tagDrafts.get('')"), 'unfinished tag');
    assert.equal(run('bulkTag.value'), 'unfinished bulk tag');
    assert.equal(run('unresolved.value'), true);
    allow = true;
    assert.equal(run('requestDeparture()'), true);
    assert.equal(run('dirtyEditors.Sources'), true);
    assert.equal(run("tagDrafts.get('')"), 'unfinished tag');
    assert.equal(run('bulkTag.value'), '');
});

test('cancelled photo navigation retains an unfinished bulk tag and accepted discard clears it', async () => {
    let allow = false;
    const run = workspace(async () => undefined, undefined, { window: { confirm: () => allow } });
    run("view.value='Curation'; bulkTag.value='pending bulk tag'");
    await run("switchView('Sources')");
    assert.equal(run('view.value'), 'Curation');
    assert.equal(run('bulkTag.value'), 'pending bulk tag');
    allow = true;
    await run("switchView('Sources')");
    assert.equal(run('view.value'), 'Sources');
    assert.equal(run('bulkTag.value'), '');
});

test('bulk tag guard treats only successfully submitted text as clean', async () => {
    let finishBatch;
    const run = workspace(async id => ({ id, displayName: 'Photo A', curation: { revision: 3, tags: [] } }), () => new Promise(resolve => { finishBatch = resolve; }), { watch });
    run("view.value='Curation'; rows.value=[{id:'a',displayName:'Photo A',curation:{revision:3,tags:[]}}]; selection.value=['a']; bulkTag.value='morning'");
    const pending = run("bulk('tag')");
    await settle();
    run("bulkTag.value='changed while sending'");
    finishBatch({ items: [{ id: 'a', status: 'committed', current: { revision: 4, tags: ['morning'] } }] });
    await pending;
    assert.equal(run('bulkTag.value'), 'changed while sending', 'a successful acknowledgement cannot erase newer typing');
    assert.equal(run('unresolved.value'), true);

    const completed = workspace(async id => ({ id, curation: { revision: 3, tags: [] } }), async () => ({ items: [{ id: 'a', status: 'committed', current: { revision: 4, tags: ['morning'] } }] }), { watch });
    completed("view.value='Curation'; rows.value=[{id:'a',displayName:'Photo A',curation:{revision:3,tags:[]}}]; selection.value=['a']; bulkTag.value='morning'");
    await completed("bulk('tag')");
    assert.equal(completed('bulkTag.value'), '', 'the submitted draft is clean after a complete successful apply');
    assert.equal(completed('unresolved.value'), false);
});

test('debounced tag text remains owned by the view where it was typed', async () => {
    const run = workspace(async () => undefined, undefined, { watch });
    run("view.value = 'Gallery'; filterTagDraft.value = 'morning'");
    await nextTick();
    await run("switchView('Curation')");
    await new Promise(resolve => setTimeout(resolve, 220));
    assert.equal(run('filters.Gallery.tag'), '');
    assert.equal(run('filters.Curation.tag'), '');
});

test('authentication snapshot races retain newer persisted outcomes and distinguish no record from unavailable history', async () => {
    let finish, fail = false;
    const run = sourceWorkspace(async () => ({ items: [] }), undefined, async () => {
        if (fail) throw new Error('synthetic history unavailable');
        return await new Promise(resolve => { finish = resolve; });
    });
    const pending = run("refreshAuthentication(['a', 'b'])");
    run("authentication.value.a = { outcome: 'Rejected', observedAt: '2026-10-13T12:00:02Z', version: 4, available: true, coverageComplete: true }");
    finish({ coverageComplete: true, items: [{ sourceId: 'a', outcome: 'Succeeded', observedAt: '2026-10-13T12:00:01Z', version: 3 }] });
    await pending;
    assert.equal(run('authentication.value.a.version'), 4);
    assert.match(run("authenticationStatus('a').text"), /was rejected/);
    run('authentication.value.a.coverageComplete = false');
    assert.match(run("authenticationStatus('a').text"), /was rejected.*coverage is incomplete/);
    assert.match(run("authenticationStatus('b').text"), /No saved authentication outcome/);
    fail = true; await run("refreshAuthentication(['a', 'b'])");
    assert.match(run("authenticationStatus('a').text"), /may be old/);
    assert.match(run("authenticationStatus('b').text"), /unavailable/);
});

test('a missing older transfer mapping recovers through a bounded explicit identity snapshot', async () => {
    const reads = [];
    const run = sourceWorkspace(async () => ({ items: [] }), undefined, undefined, async (_sources, _signal, transferIds) => {
        reads.push(transferIds);
        return { epoch: 'one', coverageComplete: !transferIds,
            items: transferIds ? [{ transferId: 'old', sourceId: 'a', captureId: 'known', assetId: 'asset', accepted: true,
                previewReady: false, phase: 'AcceptedAwaitingGrouping', version: 2, observedAt: '2026-10-13T12:00:00Z' }] : [] };
    });
    run("phases.observe({transferId:'old',sourceId:'a',phase:'Receiving',observedAt:'2026-10-13T12:00:00Z',version:1,accepted:false,previewReady:false})");
    await run("refreshPhases(['a'])");
    assert.equal(reads.length, 2);
    assert.equal(reads[1][0], 'old');
    assert.equal(run("phases.items.get('old').captureId"), 'known');
    assert.equal(run("phases.items.get('old').accepted"), true);
    assert.equal(run('phases.coverageComplete'), false);
});

test('cached Source labels cannot replace editor values or a newer authoritative revision', () => {
    const run = sourceWorkspace(async () => ({ items: [] }));
    run("display.value.a = {id:'a',name:'Cached label',curationMode:'Immediate',configurationRevision:1}; chosen.value = 'a'");
    assert.equal(run('displaySource(props.sources[0]).name'), 'Cached label');
    assert.equal(run('name.value'), 'A');
    assert.equal(run('mode.value'), 'Manual');
    assert.equal(run('revision.value'), 1);
    run('display.value.a.configurationRevision = 0');
    assert.equal(run('displaySource(props.sources[0]).name'), 'A');
    assert.equal(run('revision.value'), 1);
});

test('three Source dots and thumbnail structure remain fixed across phase/loading changes and hint bursts', async () => {
    const run = sourceWorkspace(async () => ({ items: [] }));
    run("selectSource('a'); phaseNow.value = Date.parse('2026-10-13T12:00:01Z'); recent.value.a = [{id:'photo',displayName:'SYNTHETIC.JPG'}]; recentState.value.a = 'ready'");
    const motion = ref(true);
    const tree = run.mount(motion);
    const frame = tree.find('recent-stream-frame');
    const thumbnail = frame.children[0].children[0];
    const connector = tree.find('source-activity');
    const counters = tree.find('source-counters').children.filter(element => element.tagName === 'SPAN');
    const dots = connector.children[0].children.filter(element => String(element.props.class).includes('flow-dot'));
    const outsideOverlay = element => element.props.popover ? '' : element.text + element.children.map(outsideOverlay).join(' ');
    try {
        assert.equal(dots.length, 3);
        assert.equal(tree.find('source-status-popover').props.popover, 'auto');
        assert.equal(tree.find('source-status-control').props.popovertarget, tree.find('source-status-popover').props.id);
        assert.equal(tree.find('source-status-control').parent, tree.find('source-summary-wrap'));
        assert.equal(tree.find('source-summary').tagName, 'BUTTON');
        assert.equal(tree.find('source-status-control').parent === tree.find('source-summary'), false);
        assert.equal(counters.length, 3);
        assert.ok(String(tree.find('source-row').props.class).includes('selected'));
        assert.equal(tree.find('source-summary').props['aria-pressed'], true);
        for (const phase of ['Receiving', 'AcceptedAwaitingGrouping', 'PreparingPreview', 'Ready', 'Stale', 'FailedUnavailable']) {
            run(`phases.clear(); phases.reconcile({epoch:'one',coverageComplete:true,items:[{transferId:'transfer',sourceId:'a',phase:'${phase}',accepted:${!['Receiving','Stale'].includes(phase)},captureId:${['PreparingPreview','Ready','FailedUnavailable'].includes(phase) ? "'known'" : 'undefined'},previewReady:${phase === 'Ready'},version:0,observedAt:'2026-10-13T12:00:00Z'}]},['a']); recentState.value.a = 'loading'`);
            await Vue.nextTick();
            assert.equal(tree.find('recent-stream-frame'), frame);
            assert.equal(frame.children[0].children[0], thumbnail);
            assert.deepEqual(connector.children[0].children.filter(element => String(element.props.class).includes('flow-dot')), dots);
            assert.deepEqual(tree.find('source-counters').children.filter(element => element.tagName === 'SPAN'), counters);
            assert.doesNotMatch(outsideOverlay(tree.find('source-stream')), /Loading|observation|Transfer|Preparing|Stale/);
        }
        run("phases.clear(); phases.reconcile({epoch:'one',coverageComplete:true,items:Array.from({length:6},(_,index)=>({transferId:'burst-'+index,sourceId:'a',phase:'Receiving',accepted:false,previewReady:false,version:0,observedAt:'2026-10-13T12:00:00Z'}))},['a'])");
        await Vue.nextTick();
        assert.equal(tree.find('transfer-observations').children.filter(element => element.tagName === 'LI').length, 6);
        assert.equal(frame.children[0].children[0], thumbnail);
        assert.ok(String(connector.props.class).includes('running'));
        motion.value = false; await Vue.nextTick();
        assert.ok(!String(connector.props.class).includes('running'));
        motion.value = true; run('props.connected = false'); await Vue.nextTick();
        assert.ok(!String(connector.props.class).includes('running'));
    } finally { tree.close(); }
});

test('Sources outside the bounded initial history read offer an explicit load action', async () => {
    const reads = [];
    const sources = Array.from({ length: 31 }, (_, index) => ({ id: String(index), name: `Source ${index}` }));
    const run = sourceWorkspace(async request => { reads.push(request.queryParameters.sourceId); return { items: [] }; }, sources);
    await run('refreshRecent(props.sources)');
    assert.equal(reads.length, 30);
    const tree = run.mount();
    try {
        const nodes = element => [element, ...element.children.flatMap(nodes)];
        const load = nodes(tree.root).find(element => element.props['aria-label'] === 'Load recent Captures for Source 30');
        assert.ok(load, 'unrequested history must not be presented as perpetual loading');
        await load.props.onClick();
        assert.equal(reads.length, 31);
        assert.equal(reads.at(-1), '30');
        assert.equal(run("recentState.value['30']"), 'ready');
    } finally { tree.close(); }
});

test('Source switching and New Source retain drafts until explicit discard', () => {
    for (const target of ['b', '']) {
        const run = sourceWorkspace(async () => ({ items: [] }));
        run("chosen.value = 'a'; name.value = 'Unsaved A'; dirty.value = true");
        run(`selectSource('${target}')`);
        assert.equal(run('chosen.value'), 'a');
        assert.equal(run('name.value'), 'Unsaved A');
        run('cancelSelection()');
        assert.equal(run('dirty.value'), true);
        run(`selectSource('${target}'); discardSelection()`);
        assert.equal(run('chosen.value'), target);
        assert.equal(run('name.value'), target ? 'B' : '');
        assert.equal(run('dirty.value'), false);
        run('discardSelection()');
        assert.equal(run('chosen.value'), target, 'a dismissed transition cannot run again');
    }
});

test('Source stream read failures preserve last-known photos and recover on Retry', async () => {
    let fail = false;
    let finish;
    const run = sourceWorkspace(async () => {
        if (fail) throw new Error('synthetic unavailable');
        return new Promise(resolve => { finish = resolve; });
    });
    const first = run('refreshRecent([props.sources[0]])');
    finish({ items: [{ id: 'photo', displayName: 'SYNTHETIC.JPG' }] });
    await first;
    fail = true;
    await run('refreshRecent(props.sources)');
    assert.equal(run("recent.value.a[0].id"), 'photo');
    assert.equal(run("recentState.value.a"), 'failed');
    assert.equal(run("recentState.value.b"), 'failed');
    fail = false;
    const retry = run('refreshRecent([props.sources[0]])');
    assert.equal(run("recentState.value.a"), 'loading');
    finish({ items: [] });
    await retry;
    assert.equal(run("recentState.value.a"), 'ready');
    assert.equal(run('recent.value.a.length'), 0);
});

test('rating filter highlights every signed value inside its selected interval', async () => {
    const run = workspace(async () => undefined);
    const tree = run.mount();
    try {
        for (const [start, end] of [[-1, 3], [-1, 0], [0, 0], [2, 4]]) {
            run(`filter.value.rating = ${start}; filter.value.ratingMaximum = ${start}`);
            await Vue.nextTick();
            const buttons = tree.find('rating-interval').children.filter(node => node.props['data-rating'] != null);
            assert.equal(buttons.length, 7);
            buttons.find(button => Number(button.props['data-rating']) === start).props.onClick({ shiftKey: false });
            buttons.find(button => Number(button.props['data-rating']) === end).props.onClick({ shiftKey: true });
            await Vue.nextTick();
            for (const button of buttons) {
                const rating = Number(button.props['data-rating']);
                assert.equal(button.props['aria-pressed'], rating >= start && rating <= end, `interval ${start}..${end}, rating ${rating}`);
            }
        }
    } finally {
        tree.close();
    }
});

test('fresh released Capture blocks all bulk curation submissions and retains the selection', async () => {
    for (const action of ['rating', 'label', 'tag', 'reset']) {
        let writes = 0;
        const run = workspace(async id => ({ id, displayName: 'synthetic released', curation: { revision: 4, releasedAt: new Date() } }), async () => { writes++; });
        run("view.value = 'Curation'; selection.value = ['released']; bulkTag.value = 'tag'");
        await run(`bulk('${action}')`);
        assert.equal(writes, 0); assert.equal(run('selection.value.length'), 1); assert.match(run('bulkResult.value'), /Released Captures are read-only/);
    }
});
test('changing active Capture cannot expose the previous detail or enable an existing draft before its read', async () => {
    let resolve; let block = false;
    const run = workspace(id => !block || id === 'a' ? Promise.resolve({ id, displayName: id.toUpperCase(), curation: { revision: 1 } }) : new Promise(done => { resolve = done; }));
    await run("openCapture('b')");
    await run("openCapture('a')");
    block = true;
    const pending = run("openCapture('b')");
    assert.equal(run('capture.value'), undefined); assert.equal(run('edit.value'), undefined);
    resolve({ id: 'b', displayName: 'B', curation: { revision: 1 } }); await pending;
    assert.equal(run('capture.value.displayName'), 'B'); assert.ok(run('edit.value'));
});
test('comparison pane IDs and typed tag drafts stay independent when focus changes', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1 } }));
    await run("openCapture('a'); compare.value = true; tag.value = 'unfinished A'");
    await run("openCapture('b', false, 'B')"); run("tag.value = 'unfinished B'");
    await run("openCapture('a', false, 'A', true)");
    assert.equal(run('paneAId.value'), 'a'); assert.equal(run('secondId.value'), 'b'); assert.equal(run('tag.value'), 'unfinished A');
});
test('authentication loss clears private Capture state and never sends a pending command', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1 } }));
    await run("openCapture('a')"); run("tag.value = 'private draft'; rows.value = [{ id: 'a' }]; loseAccess()");
    assert.equal(run('rows.value.length'), 0); assert.equal(run('tagDrafts.size'), 0); assert.equal(run('editSessions.size'), 0); assert.equal(run('capture.value'), undefined);
});

async function settle() { await nextTick(); await new Promise(resolve => setImmediate(resolve)); }

function component(name, props, api) {
    if (name === 'CaptureEvidence' && !('active' in props)) props.active = true;
    const scope = effectScope();
    const unmounted = [];
    let generation = 0;
    let accessLost;
    const context = vm.createContext({
        exports: {}, AbortController, defineProps: () => props, defineEmits: () => () => {},
        ref, shallowRef, computed, watch, nextTick, onUnmounted: callback => unmounted.push(callback), onMounted() {},
        require: moduleName => moduleName.endsWith('/api')
            ? { api, cancellable: signal => ({getKey: () => 'shutterspool.abort', signal}), handleFailure: error => error.responseStatusCode }
            : moduleName.endsWith('/workspace-interactions') ? moduleAt('workspace-interactions')
            : moduleName.endsWith('/destination-capabilities') ? moduleAt('destination-capabilities')
            : moduleName.endsWith('/read-pool') ? moduleAt('read-pool')
            : { accessGeneration: () => generation, onAccessLost: callback => { accessLost = callback; return () => {}; } },
    });
    const source = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    scope.run(() => vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context));
    return {
        props,
        read: expression => vm.runInContext(expression, context),
        loseAccess: () => { generation++; accessLost(); },
        close: () => { unmounted.forEach(callback => callback()); scope.stop(); },
    };
}

function evidenceApi(read) {
    return { management: { captures: { byId: id => Object.fromEntries(['routing', 'deliveries', 'actions'].map(kind => [kind, {
        get: options => read(kind, id, options?.queryParameters),
    }])) } } };
}

test('delivery summary groups targets and preserves older blockers while hiding duplicate matched routing', async () => {
    const props = reactive({ capture: { id: 'c', curation: { releasedAt: new Date() }, assets: [] }, generation: 0, deliveryOnly: true });
    const view = component('CaptureEvidence', props, evidenceApi(async kind => ({ items: kind === 'routing' ? [
        { id: 'matched', assetId: 'a', destinationId: 'd', destinationName: 'Archive', kind: 'Matched', isCurrent: true },
        { id: 'blocked', assetId: 'b', destinationId: 'd', destinationName: 'Archive', kind: 'DestinationDisabled', isCurrent: true },
        { id: 'past', assetId: 'b', destinationId: 'd', kind: 'NoMatch', isCurrent: false },
    ] : kind === 'deliveries' ? [
        { id: 'ok', assetId: 'a', destinationId: 'd', state: 'Delivered' },
        { id: 'failed', assetId: 'a', destinationId: 'd', state: 'Failed' },
        { id: 'old', assetId: 'a', destinationId: 'd', state: 'Superseded' },
    ] : [] })));
    try {
        await settle();
        const groups = view.read('destinationGroups.value');
        assert.equal(groups.length, 1); assert.equal(groups[0].name, 'Archive');
        assert.equal(groups[0].decisions.map(item => item.id).join(','), 'blocked');
        assert.equal(groups[0].work.map(item => item.id).join(','), 'failed,ok');
        assert.equal(groups[0].history.length, 1);
    } finally { view.close(); }
});

test('Capture evidence drops old-owner pages and cursors, but retains same-owner pages after refresh failure', async () => {
    const requests = [];
    let failOwner;
    const api = evidenceApi((kind, id, query) => {
        requests.push({ kind, id, query });
        if (id === failOwner) return Promise.reject(new Error('synthetic failure'));
        return Promise.resolve({ items: [{ id: `${id}-${kind}`, assetId: `${id}-asset` }], nextCursor: `${id}-${kind}-next` });
    });
    const view = component('CaptureEvidence', reactive({ capture: { id: 'capture-a', assets: [] }, generation: 0 }), api);
    try {
        await settle();
        assert.equal(view.read('decisions.value.items[0].id'), 'capture-a-routing');
        failOwner = 'capture-b';
        view.props.capture.id = failOwner;
        await nextTick();
        assert.equal(view.read('decisions.value'), undefined);
        assert.equal(view.read('deliveries.value'), undefined);
        assert.equal(view.read('actions.value'), undefined);
        await settle();
        assert.match(view.read('error.value'), /Unable to read delivery evidence/);
        await view.read("read('decisions')");
        assert.equal(requests.at(-1).query.cursor, undefined);

        failOwner = undefined;
        await view.read('read()');
        await settle();
        const retained = view.read('deliveries.value');
        failOwner = 'capture-b';
        view.props.generation++;
        await nextTick(); await settle();
        assert.equal(view.read('deliveries.value'), retained);
    } finally { view.close(); }
});

test('Delivery work ignores a late preview after owner change and clears private evidence on access loss', async () => {
    let resolvePreview;
    const api = { management: {
        deliveries: { byId: id => ({
            attempts: { get: async () => ({ items: [{ id: `${id}-attempt` }] }) },
            retry: { post: async () => ({ status: 'Queued' }) },
            resend: { preview: { post: () => new Promise(resolve => { resolvePreview = resolve; }) }, post: async () => ({ status: 'Queued' }) },
        }) },
        assets: { byAssetId: () => ({ destinations: { byDestinationId: () => ({ reevaluatePreview: { get: async () => ({}) } }) } }) },
    } };
    const view = component('DeliveryWork', reactive({ work: { id: 'work-a', assetId: 'asset-a', destinationId: 'destination-a' }, online: true }), api);
    try {
        const pending = view.read("command('resend')");
        view.props.work = { id: 'work-b', assetId: 'asset-b', destinationId: 'destination-b' };
        await nextTick();
        resolvePreview({ status: 'Previewed', confirmationToken: 'private-token' });
        await pending;
        assert.equal(view.read('preview.value'), undefined);
        view.read("preview.value = { status: 'Previewed', confirmationToken: 'private-token' }; attempts.value = { items: [{ id: 'private-attempt' }] }; feedback.value = 'private detail'");
        view.loseAccess();
        assert.equal(view.read('preview.value'), undefined);
        assert.equal(view.read('attempts.value'), undefined);
        assert.equal(view.read('feedback.value'), '');
    } finally { view.close(); }
});

test('Delivery work never issues an action without confirmed online state', async () => {
    let calls = 0;
    const delivery = { resend: { preview: { post: async () => { calls++; return {}; } } } };
    const reevaluatePreview = { get: async () => { calls++; return {}; } };
    const api = {
        management: {
            deliveries: { byId: () => delivery },
            assets: { byAssetId: () => ({ destinations: { byDestinationId: () => ({ reevaluatePreview }) } }) },
        },
    };
    const view = component('DeliveryWork', reactive({ work: { id: 'work-a', assetId: 'asset-a', destinationId: 'destination-a' }, online: false }), api);
    try {
        await view.read("command('resend')");
        assert.equal(calls, 0);
    } finally { view.close(); }
});

test('read-only Delivery work keeps attempt history readable and blocks commands', async () => {
    let reads = 0;
    let writes = 0;
    const delivery = {
        attempts: { get: async () => { reads++; return { items: [{ id: 'attempt-a' }] }; } },
        retry: { post: async () => { writes++; return {}; } },
    };
    const api = { management: {
        deliveries: { byId: () => delivery },
        assets: { byAssetId: () => ({ destinations: { byDestinationId: () => ({}) } }) },
    } };
    const view = component('DeliveryWork', reactive({
        work: { id: 'work-a', assetId: 'asset-a', destinationId: 'destination-a' }, online: true, readOnly: true,
    }), api);
    try {
        await view.read('readAttempts()');
        await view.read("command('retry')");
        assert.equal(reads, 1);
        assert.equal(view.read('attempts.value.items[0].id'), 'attempt-a');
        assert.equal(writes, 0);
    } finally { view.close(); }
});

test('Curation grid selection does not leak into Gallery browsing or its explicit selection mode', async () => {
    const run = workspace(async id => ({ id, displayName: 'PHOTO.JPG', curation: { revision: 1 } }), undefined, {
        page: async () => ({ items: [{ id: 'a', displayName: 'PHOTO.JPG' }] }),
        document: { querySelector: () => undefined },
    });
    run("view.value = 'Curation'; expanded.value = true; selecting.value = true; selection.value = ['a']");
    await run("switchView('Gallery')");
    const tree = run.mount();
    try {
        assert.equal(run('selecting.value'), false);
        assert.equal(run('selection.value.length'), 0);
        assert.ok(!tree.find('tile-selection'), 'Gallery must not inherit Curation grid checkboxes');
        run("tileClick('a')"); await settle();
        assert.equal(run('full.value'), false);
        assert.equal(run('galleryInspectionId.value'), 'a');
        assert.equal(run('inspectionCapture.value.id'), 'a');
        assert.equal(run('selection.value.length'), 0);
        run("full.value = false; selecting.value = true; tileClick('a')"); await Vue.nextTick();
        assert.equal(run('full.value'), false);
        assert.equal(run('selection.value[0]'), 'a');
        assert.ok(tree.find('tile-selection'));
        tree.find('bulk-bar').children.find(node => node.text === 'Select none').props.onClick();
        await Vue.nextTick();
        assert.equal(run('selecting.value'), false);
        assert.equal(run('selection.value.length'), 0);
        assert.ok(!tree.find('tile-selection'), 'Select none must leave Gallery selection mode');
        run("tileClick('a')"); await settle();
        assert.equal(run('full.value'), false);
        assert.equal(run('galleryInspectionId.value'), 'a');
        await run("switchView('Curation')"); await Vue.nextTick();
        assert.equal(run('expanded.value'), true);
    } finally { tree.close(); }
});

test('Gallery Fullscreen is explicit, uses active or first loaded Capture, and stays disabled when empty', async () => {
    const run = workspace(async id => ({ id, displayName: 'SYNTHETIC.JPG', curation: { revision: 1 } }), undefined, { document: { querySelector: () => undefined } });
    run("view.value='Gallery'; rows.value=[{id:'first',displayName:'FIRST.JPG'},{id:'active',displayName:'ACTIVE.JPG'}]");
    const tree = run.mount();
    try {
        const button = tree.find('gallery-fullscreen'); assert.equal(button.props['aria-label'], 'Fullscreen photographs'); assert.equal(button.props.disabled, false);
        await run('openGalleryFull()'); assert.equal(run('activeId.value'), 'first'); assert.equal(run('full.value'), true);
        run("full.value=false; activeId.value='active'"); await run('openGalleryFull()'); assert.equal(run('activeId.value'), 'active'); assert.equal(run('full.value'), true);
        run('full.value=false; rows.value=[]'); await Vue.nextTick(); assert.equal(tree.find('gallery-fullscreen').props.disabled, true);
        await run('openGalleryFull()'); assert.equal(run('full.value'), false);
    } finally { tree.close(); }
});

test('comparison layout toggles retain both panes and the focused Capture', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1 } }));
    await run("openCapture('a'); toggleCompare();");
    await run("openCapture('b', false, 'B')");
    run('toggleCompare(); toggleCompare()');
    assert.equal(run('paneAId.value'), 'a');
    assert.equal(run('secondId.value'), 'b');
    assert.equal(run('activeId.value'), 'b');
    assert.equal(run('activePane.value'), 'B');
});

test('fullscreen and inspector curation are disabled during unresolved recovery states', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1 } }));
    await run("openCapture('a')");
    assert.equal(run('canCurate.value'), true);
    for (const state of ['Conflict', 'Save failed', 'Checking outcome']) {
        run(`edit.value.status = '${state}'`);
        assert.equal(run('canCurate.value'), false);
    }
});

test('curation leads with choices, keeps Favorite independent, and places recovery and administration second', async () => {
    const run = workspace(async id => ({ id, displayName:'SYNTHETIC.JPG', curation:{revision:1}, retention:{isPaused:true,disposition:'active'} }));
    await run("view.value='Curation'; rows.value=[{id:'synthetic'}]; openCapture('synthetic')");
    const tree = run.mount();
    const nodes = root => [root,...root.children.flatMap(nodes)];
    const text = root => root.text + root.children.map(text).join('');
    try {
        const controls = tree.find('curation-controls'), markers = tree.find('curation-markers');
        assert.ok(controls); assert.ok(markers.children.some(node => node.type === 'mock-FavoriteControl'));
        const storage = tree.find('photograph-storage'); assert.equal(storage.type,'details'); assert.ok(!storage.props.open);
        assert.ok(nodes(storage).some(node=>node.type==='mock-PrivacyErasurePanel'));
        const release = nodes(tree.root).find(node=>node.type==='button'&&text(node).trim()==='Release now'); assert.ok(release); assert.equal(release.props.disabled,true,'paused recovery cannot be bypassed by visible Release');
        run('capture.value.retention.isPaused=false'); await Vue.nextTick(); assert.equal(release.props.disabled,false);
        run('edit.value.server.releasedAt=new Date()'); await Vue.nextTick();
        assert.equal(tree.find('curation-rating-controls').props.disabled,true);
        assert.equal(tree.find('curation-color-controls').props.disabled,true);
        assert.equal(tree.find('curation-tag-controls').props.disabled,true);
        assert.ok(markers.children.some(node=>node.type==='mock-FavoriteControl'),'Favorite remains outside disabled curation fieldsets');
    } finally { tree.close(); }
});

test('Gallery inspection retains selection, returns scroll and focus, and rejects old-owner reads', async () => {
    const pending = new Map();
    const positions = []; let focused = 0;
    const origin = { isConnected: true, focus: () => { focused++; } };
    const run = workspace(id => new Promise(resolve => pending.set(id, resolve)), undefined, {
        window: { scrollTo: (...position) => positions.push(position) },
        document: { activeElement: origin },
    });
    run("selection.value = ['a']; rows.value = [{ id: 'a' }, { id: 'b' }]");
    const older = run("inspectCapture('a')");
    const newer = run("inspectCapture('b')");
    pending.get('a')({ id: 'a', curation: { revision: 1 } }); await older;
    assert.equal(run('inspectionCapture.value'), undefined);
    pending.get('b')({ id: 'b', curation: { revision: 1 } }); await newer;
    assert.equal(run('inspectionCapture.value.id'), 'b');
    assert.equal(run('selection.value[0]'), 'a');
    await run('closeGalleryInspection()');
    assert.equal(run('galleryInspectionId.value'), '');
    assert.deepEqual(positions.at(-1), [0, 640]);
    assert.equal(focused, 1);
});

test('closing inspection or losing access fences a late Gallery detail response', async () => {
    for (const exit of ['closeGalleryInspection()', 'loseAccess()']) {
        let resolve;
        const run = workspace(id => new Promise(done => { resolve = done; }));
        const pending = run("inspectCapture('a')");
        await run(exit);
        resolve({ id: 'a', curation: { revision: 1 } }); await pending;
        assert.equal(run('galleryInspectionId.value'), '');
        assert.equal(run('inspectionCapture.value'), undefined);
    }
});

test('rating pointer press retains native exact click while dragging selects an interval and consumes only its click', async () => {
    const target = value => ({ closest: () => ({ dataset: { rating: String(value) } }) });
    const run = workspace(async () => undefined, undefined, { document: { elementFromPoint: () => target(5) } });
    const tree = run.mount();
    const interval = tree.find('rating-interval');
    const button = interval.children.find(node => node.props['data-rating'] === 2);
    button.props.onClick({ shiftKey: false }); await Vue.nextTick();
    assert.equal(run('filter.value.rating'), 2); assert.equal(run('filter.value.ratingMaximum'), 2);
    let pointerCaptures = 0;
    const pointer = { button: 0, pointerId: 1, clientX: 10, clientY: 10, target: target(2), currentTarget: { setPointerCapture() { pointerCaptures++; } } };
    interval.props.onPointerdown(pointer);
    assert.equal(pointerCaptures, 0);
    interval.props.onPointerup();
    interval.props.onPointerdown(pointer);
    interval.props.onPointermove({ ...pointer, buttons: 1 });
    interval.props.onPointerup(); await Vue.nextTick();
    assert.equal(run('filter.value.rating'), 2); assert.equal(run('filter.value.ratingMaximum'), 5);
    tree.close();
});

test('Shift-clicking Rejected or Not Rated and dragging across zero retain the interval anchor', async () => {
    const target = value => ({ closest: () => ({ dataset: { rating: String(value) } }) });
    const run = workspace(async () => undefined, undefined, { document: { elementFromPoint: () => target(0) } });
    run('filter.value.rating = 4; filter.value.ratingMaximum = 5');
    const tree = run.mount();
    const interval = tree.find('rating-interval');
    const notRated = interval.children.find(node => node.props['data-rating'] === '0');
    notRated.props.onClick({ shiftKey: true }); await Vue.nextTick();
    assert.equal(run('filter.value.rating'), 0); assert.equal(run('filter.value.ratingMaximum'), 4);
    const rejected = interval.children.find(node => node.props['data-rating'] === '-1');
    rejected.props.onClick({ shiftKey: true }); await Vue.nextTick();
    assert.equal(run('filter.value.rating'), -1); assert.equal(run('filter.value.ratingMaximum'), 4);
    interval.props.onPointerdown({ button: 0, pointerId: 3, target: target(4) });
    interval.props.onPointermove({ buttons: 1, pointerId: 3, clientX: 10, clientY: 10, currentTarget: { setPointerCapture() {} } });
    interval.props.onPointerup(); await Vue.nextTick();
    assert.equal(run('filter.value.rating'), 0); assert.equal(run('filter.value.ratingMaximum'), 4);
    tree.close();
});

test('uncertain Trash stays locked until a complete explicit authoritative reread', async () => {
    let posts = 0;
    const retention = { captureRevision: 7, disposition: 'active', isFavorite: false, availableOriginals: [{ variant: 'RAW', byteLength: 10, state: 'available' }], timeline: { observedAt: new Date(), endsAt: new Date(Date.now() + 60_000) } };
    const run = workspace(async id => ({ id, displayName: 'Photo A', retention }), undefined, { trash: async () => { posts++; throw { responseStatusCode: 503 }; } });
    run("view.value='Curation'; rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; selection.value=['a']");
    await run('bulkTrash()');
    assert.equal(posts, 1);
    assert.deepEqual(Array.from(run('uncertainTrashIds.value')), ['a']);
    assert.match(run('bulkResult.value'), /outcome unconfirmed/);
    await run('bulkTrash()');
    assert.equal(posts, 1, 'a second POST is blocked while the outcome is unknown');
    const incomplete = workspace(async id => ({ id, retention: undefined }));
    incomplete("view.value='Curation'; rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; selection.value=['a']; uncertainTrashIds.value=['a']");
    await incomplete('curationActions.verifyTrashOutcomes()');
    assert.deepEqual(Array.from(incomplete('uncertainTrashIds.value')), ['a']);
    const verified = workspace(async id => ({ id, retention: { ...retention, disposition: 'active' } }));
    verified("view.value='Curation'; rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; selection.value=['a']; uncertainTrashIds.value=['a']");
    await verified('curationActions.verifyTrashOutcomes()');
    assert.deepEqual(Array.from(verified('uncertainTrashIds.value')), []);
});

test('single-photo Trash shares the per-Capture uncertainty lock with bulk and remounted controls', async () => {
    let posts = 0; let reads = 0;
    const evidence = { captureRevision: 7, disposition: 'active', isFavorite: false, availableOriginals: [{ variant: 'RAW', byteLength: 10, state: 'available' }], timeline: { observedAt: new Date(), endsAt: new Date(Date.now() + 60_000) } };
    const run = workspace(async id => { reads++; return { id, retention: evidence }; }, undefined, {
        watch, realTrashAction: true,
        trash: async () => { posts++; throw { responseStatusCode: 503 }; },
    });
    run("view.value='Gallery'; rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; selection.value=['a']; activeId.value='a'; capture.value={id:'a',displayName:'Photo A',retention:{...rows.value[0].retention}}; galleryInspectionId.value='a'; galleryInspectionOpen.value=true");
    const tree = run.mount();
    const controls = tree.find('retention-action');
    assert.ok(controls, 'the composed shell uses the actual single-photo Trash control');
    const textOf = element => element.text + element.children.map(textOf).join('');
    const button = (container, label) => container.children.find(element => element.tagName === 'BUTTON' && textOf(element).includes(label));
    const move = button(controls, 'Move to Trash');
    await move.props.onClick();
    await settle();
    assert.equal(posts, 1, 'the single-photo action submits through the shared owner');
    assert.deepEqual(Array.from(run('uncertainTrashIds.value')), ['a']);
    assert.ok(move.props.disabled, 'the child control reflects the central uncertainty lock');

    await run("switchView('Curation')");
    run("rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; selection.value=['a']");
    await run('bulkTrash()');
    assert.equal(posts, 1, 'the alternate bulk control cannot repeat the uncertain single-photo POST');
    await run("switchView('Gallery')");
    run("rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; galleryInspectionId.value='a'; galleryInspectionOpen.value=true; activeId.value='a'; capture.value={id:'a',displayName:'Photo A',retention:{...rows.value[0].retention}}");
    await nextTick();
    const remounted = tree.find('retention-action');
    assert.ok(button(remounted, 'Refresh and verify'));
    const verify = button(remounted, 'Refresh and verify');
    await verify.props.onClick();
    await settle();
    assert.ok(reads > 0, 'the rendered verification control performs an authoritative reread');
    assert.deepEqual(Array.from(run('uncertainTrashIds.value')), [], 'only the authoritative reread clears the shared lock');
    await run("switchView('Curation')");
    run("rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; selection.value=['a']");
    await run('bulkTrash()');
    assert.equal(posts, 2, 'a fresh bulk command is allowed after verification');
    await run("switchView('Gallery')");
    run("rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; galleryInspectionId.value='a'; galleryInspectionOpen.value=true; activeId.value='a'; capture.value={id:'a',displayName:'Photo A',retention:{...rows.value[0].retention}}");
    await nextTick();
    const lockedRemount = tree.find('retention-action');
    const lockedMove = button(lockedRemount, 'Move to Trash');
    assert.equal(lockedMove.props.disabled, true, 'a bulk failure locks a later single-photo remount too');
    assert.ok(button(lockedRemount, 'Refresh and verify'));
    tree.close();
});

test('pending bulk Trash keeps its per-Capture lock across confirmed departure and a late failure', async () => {
    let rejectPost; let posts = 0;
    const run = workspace(async id => ({ id, displayName: 'Photo A', retention: { captureRevision: 7 } }), undefined, {
        watch,
        trash: () => { posts++; return new Promise((_resolve, reject) => { rejectPost = reject; }); },
        window: { confirm: () => true },
    });
    run("view.value='Curation'; rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]; selection.value=['a']");
    const pending = run('bulkTrash()');
    await settle();
    assert.deepEqual(Array.from(run('pendingTrashIds.value')), ['a'], 'the shared lock is registered before the POST returns');
    await run("switchView('Sources')");
    await run("switchView('Curation')");
    rejectPost({ responseStatusCode: 503 });
    await pending;
    assert.deepEqual(Array.from(run('pendingTrashIds.value')), []);
    assert.deepEqual(Array.from(run('uncertainTrashIds.value')), ['a'], 'late failure after the visit changed remains uncertain');
    run("selection.value=['a']; rows.value=[{id:'a',displayName:'Photo A',retention:{captureRevision:7,disposition:'active',isFavorite:false,availableOriginals:[{variant:'RAW',byteLength:10,state:'available'}],timeline:{observedAt:new Date(),endsAt:new Date(Date.now()+60000)}}}]");
    await run('bulkTrash()');
    assert.equal(posts, 1, 'returning to the workspace cannot blindly replay the pending command');
});

test('Gallery inspection failure stays recoverable and never permits Capture workflow commands', async () => {
    let failed = true;
    const run = workspace(async id => {
        if (failed) throw new Error('read failed');
        return { id, curation: { revision: 1 } };
    });
    await run("inspectCapture('a')");
    assert.equal(run('galleryInspectionId.value'), 'a');
    assert.equal(run('inspectionCapture.value'), undefined);
    assert.match(run('detailError.value'), /Unable to open/);
    failed = false;
    await run("inspectCapture('a')");
    assert.equal(run('inspectionCapture.value.id'), 'a');
    await run("captureAction('release')");
    assert.equal(run('busyCapture.value'), false);
    assert.equal(run('edit.value.server.releasedAt'), undefined);
    run('phone.value = true');
    await run("inspectCapture('a', 'Attention')");
    assert.equal(run('galleryInspectionOpen.value'), true, 'work jump reveals the phone inspector');
    assert.equal(run('inspectionDeliveryState.value'), 'Attention');
});

test('leaving Gallery inspection retains the grid scroll position across workspace switches', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1 } }), undefined, {
        window: { scrollTo(_x, y) { this.scrollY = y; } },
    });
    run("rows.value = [{ id: 'a' }]");
    await run("inspectCapture('a')");
    assert.equal(run('window.scrollY'), 0);
    await run("switchView('Sources')");
    assert.equal(run('scrollPositions.Gallery'), 640);
    await run("switchView('Gallery')");
    assert.equal(run('window.scrollY'), 640);
});

test('R rejects the editable fullscreen Capture without stealing typing or browser shortcuts', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1, rating: 3 } }));
    await run("openCapture('a')");
    run("view.value = 'Curation'; full.value = true");
    for (const key of ['r', 'R']) {
        run("edit.value.intent.rating = 3");
        run(`keyboard({ key: '${key}', target: { closest: () => null }, preventDefault() {} })`);
        assert.equal(run('edit.value.intent.rating'), -1);
        await settle();
    }
    for (const event of [
        "{ key: 'r', ctrlKey: true }", "{ key: 'r', metaKey: true }", "{ key: 'r', altKey: true }",
        "{ key: 'r', isComposing: true }", "{ key: 'r', target: { closest: () => ({}) } }",
    ]) {
        run("edit.value.intent.rating = 3");
        run(`keyboard({ target: { closest: () => null }, preventDefault() {}, ...${event} })`);
        assert.equal(run('edit.value.intent.rating'), 3);
    }
    for (const guard of ["full.value = false", "view.value = 'Gallery'", "bulkBusy.value = true", "edit.value.server.releasedAt = new Date()"] ) {
        run("full.value = true; view.value = 'Curation'; bulkBusy.value = false; edit.value.intent.rating = 3");
        run(guard);
        run("keyboard({ key: 'r', target: { closest: () => null }, preventDefault() {} })");
        assert.equal(run('edit.value.intent.rating'), 3);
    }
});

test('focused Curation retains its paging target through refresh and restores the grid footer', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1 } }));
    run("view.value = 'Curation'; rows.value = [{ id: 'a' }]");
    await run("openCapture('a')");
    const tree = run.mount();
    try {
        const footer = tree.find('scroll-boundary');
        assert.match(footer.props.class, /focused-paging/);
        run('loading.value = true'); await Vue.nextTick();
        assert.equal(tree.find('scroll-boundary'), footer);
        assert.notEqual(footer.style.display, 'none');
        assert.match(footer.props.class, /focused-paging/);
        run('expanded.value = true'); await Vue.nextTick();
        assert.notEqual(footer.style.display, 'none');
        assert.doesNotMatch(footer.props.class, /focused-paging/);
        run("view.value = 'Gallery'"); await Vue.nextTick();
        assert.notEqual(footer.style.display, 'none');
        run("galleryInspectionId.value = 'a'"); await Vue.nextTick();
        assert.equal(footer.style.display, 'none');
    } finally { tree.close(); }
});

test('compiled Curation shell, filmstrip and unchanged comparison preview survive pending and failed owner reads', async () => {
    let resolve; let fail = false;
    const run = workspace(id => id === 'a' ? Promise.resolve({ id, displayName: 'A', curation: { revision: 1 } })
        : fail ? Promise.reject(new Error('unavailable')) : new Promise(done => { resolve = done; }));
    run("view.value = 'Curation'; rows.value = [{ id: 'a', displayName: 'A' }, { id: 'b', displayName: 'B' }]");
    await run("openCapture('a')"); run("toggleCompare()");
    const tree = run.mount();
    const layout = tree.find('curation-layout'); const strip = tree.find('filmstrip');
    assert.ok(strip, 'initial compiled template contains the filmstrip');
    const thumbnails = [...strip.children]; const paneA = tree.find('photo-pane');
    const pending = run("openCapture('b', false, 'B')"); await Vue.nextTick();
    assert.equal(run('capture.value'), undefined); assert.equal(run('edit.value'), undefined);
    assert.equal(tree.find('curation-layout'), layout); assert.equal(tree.find('filmstrip'), strip);
    assert.equal(tree.find('photo-pane'), paneA); assert.deepEqual(strip.children, thumbnails);
    assert.equal(tree.find('photo-grid'), undefined);
    assert.equal(tree.find('signed-ratings'), undefined);
    assert.equal(tree.find('capture-evidence'), undefined);
    resolve({ id: 'b', displayName: 'B', curation: { revision: 1 } }); await pending; await Vue.nextTick();
    assert.equal(tree.find('filmstrip'), strip); assert.deepEqual(strip.children, thumbnails);
    await run("openCapture('a', false, 'A')"); fail = true;
    await run("openCapture('b', false, 'B')"); await Vue.nextTick();
    assert.equal(run('capture.value'), undefined); assert.equal(run('edit.value'), undefined);
    assert.equal(tree.find('curation-layout'), layout); assert.equal(tree.find('filmstrip'), strip);
    assert.ok(!tree.find('error'), 'active detail failure remains inside the inspector');
    run("selection.value = Array.from({ length: 100 }, (_, n) => String(n)); toggle('overflow')"); await Vue.nextTick();
    assert.ok(tree.find('error'), 'selection errors remain globally visible');
    tree.close();
});

test('Gallery delivery inspection reads evidence only while its details disclosure is actually open', async () => {
    const run = workspace(async id => ({ id, curation: { revision: 1, releasedAt: new Date() }, assets: [] }));
    const tree = run.mount();
    try {
        run("view.value = 'Gallery'; activeId.value = 'capture-a'");
        await run("inspectCapture('capture-a', 'Pending', 'delivery')");
        await Vue.nextTick();
        const all = node => [node, ...node.children.flatMap(all)];
        let evidence = all(tree.root).find(node => node.type === 'mock-CaptureEvidence');
        assert.equal(evidence.props.active, true);
        assert.equal(evidence.props.deliveryOnly, true);
        const disclosure = all(tree.root).find(node => node.type === 'details' && String(node.props.class).includes('capture-evidence') && all(node).some(child => child.type === 'mock-CaptureEvidence'));
        disclosure.props.onToggle({ target: { open: false } });
        await Vue.nextTick();
        evidence = all(tree.root).find(node => node.type === 'mock-CaptureEvidence');
        assert.equal(evidence.props.active, false);
    } finally { tree.close(); }
});

test('detail failures render locally in Gallery inspection and globally when no local inspector exists', async () => {
    const text = node => node.text + node.children.map(text).join('');
    const run = workspace(async () => { throw new Error('unavailable'); });
    await run("inspectCapture('a')");
    const tree = run.mount();
    assert.ok(!tree.find('error'), 'Gallery inspector contains its own detail failure');
    assert.match(text(tree.find('inspector')), /Unable to open this Capture/);
    for (const state of ["galleryInspectionId.value = ''", "view.value = 'Curation'; expanded.value = true"]) {
        run(state); await Vue.nextTick();
        assert.ok(tree.find('error'), 'detail failure remains visible without a local inspector');
        assert.match(text(tree.find('error')), /Unable to open this Capture/);
    }
    tree.close();
});

test('an empty Curation result clears the photograph while retaining the visit baseline', async () => {
    for (const read of ['loadPage(false)', 'refresh()']) {
        const run = workspace(async id => ({ id, curation: { revision: 1, rating: 3 } }));
        run("view.value = 'Curation'; initialized.value = true; rows.value = [{ id: 'a' }]; cursor.value = 'old-cursor'");
        await run("openCapture('a')");
        await run(read);
        assert.equal(run('activeId.value'), '');
        assert.equal(run('capture.value'), undefined);
        assert.equal(run('cursor.value'), null);
        assert.equal(run("editSessions.get('a').opening.rating"), 3);
    }
});

test('release and a new arrival in one refresh select the remaining waiting photograph', async () => {
    const fresh = { id: 'new', curation: { revision: 1 }, displayName: 'New' };
    const run = workspace(async id => id === 'old' ? { id, curation: { revision: 2, releasedAt: new Date() } } : fresh,
        undefined, { page: async () => ({ items: [fresh], nextCursor: null }) });
    run("view.value = 'Curation'; initialized.value = true; activeId.value = paneAId.value = 'old'; rows.value = [{ id: 'old', curation: { revision: 1 } }]");
    await run("refresh(new Set(['old', 'new']))"); await settle();
    assert.equal(run('activeId.value'), 'new');
    assert.equal(run('paneAId.value'), 'new');
    assert.equal(run('capture.value.id'), 'new');
});

test('a complete Attention refresh removes cached photographs whose attention has cleared', async () => {
    const current = { id: 'blocked', curation: { releasedAt: new Date() }, deliverySummary: { attention: 1 } };
    const run = workspace(async id => ({ id }), undefined, { page: async () => ({ items: [current], nextCursor: null }) });
    run("filters.Gallery.workflows = ['Attention']; rows.value = [{ id: 'formerly-blocked', deliverySummary: { attention: 1 } }]; cursor.value = 'old-page'");
    await run('loadPage(false, true)');
    assert.deepEqual(Array.from(run('rows.value'), row => row.id), ['blocked']);
    assert.equal(run('cursor.value'), null);
    assert.deepEqual(Array.from(run('query().workflows')), ['Attention']);
    assert.equal(run("matchesFilter({ curation: {}, deliverySummary: { waitingForRouting: 1 } })"), false);
    assert.equal(run("matchesFilter({ curation: { releasedAt: new Date() }, deliverySummary: { confirmed: 1 } })"), false);
    assert.equal(run("matchesFilter({ curation: { releasedAt: new Date() }, deliverySummary: { noRoute: 1 } })"), false);
    assert.equal(run("deliveryStatus({ deliverySummary: { noRoute: 1 } }).state"), '');
    assert.equal(run("deliveryStatus({ deliverySummary: { noRoute: 1 } }).title"), 'No matching delivery rule');
});

test('a refresh adopts its new cursor and fills arrival gaps before retained history', async () => {
    const pages = [{ items: [{ id: 'newest', acceptedAt: new Date('2026-10-12T10:03:00Z') }], nextCursor: 'fresh' }, { items: [{ id: 'gap', acceptedAt: new Date('2026-10-12T10:02:00Z') }, { id: 'older', acceptedAt: new Date('2026-10-12T10:01:00Z') }], nextCursor: null }];
    const requests = [];
    const run = workspace(async id => ({ id }), undefined, { page: async request => { requests.push(request.queryParameters.cursor); return pages.shift(); } });
    run("rows.value = [{ id: 'older', acceptedAt: new Date('2026-10-12T10:01:00Z') }]; cursor.value = null");
    await run('loadPage(false, true)');
    assert.equal(run('cursor.value'), 'fresh');
    await run('nextPage()');
    assert.deepEqual(Array.from(run('rows.value'), row => row.id), ['newest', 'gap', 'older']);
    assert.deepEqual(requests, [undefined, 'fresh']);
});

test('partial Attention refresh checks ambiguous millisecond matches without dropping older history', async () => {
    const incoming = { id: 'b', acceptedAt: new Date('2026-10-12T10:01:00.000900Z'), deliverySummary: { attention: 1 } };
    const checked = [];
    const run = workspace(async id => { checked.push(id); return { id, acceptedAt: new Date('2026-10-12T10:01:00.000100Z'), deliverySummary: { attention: id === 'z' ? 1 : 0 } }; }, undefined,
        { page: async () => ({ items: [incoming], nextCursor: 'next' }) });
    run("filters.Gallery.workflows = ['Attention']; rows.value = [{ id: 'z', acceptedAt: new Date('2026-10-12T10:01:00.000100Z'), deliverySummary: { attention: 1 } }, { id: 'a', acceptedAt: new Date('2026-10-12T10:01:00.000999Z'), deliverySummary: { attention: 1 } }]; activeId.value = 'b'");
    await run('loadPage(false, true)');
    assert.deepEqual(Array.from(run('rows.value'), row => row.id), ['b', 'z']);
    assert.deepEqual(checked, ['z', 'a']);
});

test('duplicate-only pages continue at a visible sentinel until unseen history arrives', async () => {
    const pages = [{ items: [{ id: 'older' }], nextCursor: 'second' }, { items: [{ id: 'last' }], nextCursor: null }];
    const run = workspace(async id => ({ id }), undefined, { window: { innerHeight: 900 }, page: async () => pages.shift() });
    run("rows.value = [{ id: 'newest' }, { id: 'older' }]; cursor.value = 'first'; pageAfterId.value = 'newest'; sentinel.value = { getBoundingClientRect: () => ({ top: 500 }) }");
    await run('nextPage()'); await new Promise(resolve => setTimeout(resolve, 30));
    assert.deepEqual(Array.from(run('rows.value'), row => row.id), ['newest', 'older', 'last']);
    assert.equal(run('cursor.value'), null);
});

test('ambiguous refresh reads preserve retry feedback on failure and cannot restore state after access loss', async () => {
    for (const lose of [false, true]) {
        let reject;
        const run = workspace(() => new Promise((_resolve, fail) => { reject = fail; }), undefined,
            { page: async () => ({ items: [{ id: 'boundary', acceptedAt: new Date('2026-10-12T10:01:00Z') }], nextCursor: 'next' }) });
        run("rows.value = [{ id: 'cached', acceptedAt: new Date('2026-10-12T10:01:00Z') }]; activeId.value = 'boundary'");
        const pending = run('loadPage(false, true)'); await settle();
        if (lose) run('loseAccess()');
        reject({ responseStatusCode: 503 }); await pending;
        assert.deepEqual(Array.from(run('rows.value'), row => row.id), lose ? [] : ['boundary', 'cached']);
        assert.equal(run('cursor.value'), lose ? null : 'next');
        assert.equal(!!run('pageError.value'), !lose);
    }
});

test('Retry repeats failed boundary verification instead of advancing the partial page', async () => {
    let reads = 0; const cursors = [];
    const run = workspace(async id => { if (++reads === 1) throw { responseStatusCode: 503 }; return { id, deliverySummary: { attention: 0 } }; }, undefined,
        { page: async request => { cursors.push(request.queryParameters.cursor); return { items: [{ id: 'boundary', acceptedAt: new Date('2026-10-12T10:01:00Z'), deliverySummary: { attention: 1 } }], nextCursor: 'next' }; } });
    run("view.value = 'Curation'; filters.Curation.workflows = ['Attention']; rows.value = [{ id: 'cached', acceptedAt: new Date('2026-10-12T10:01:00Z'), deliverySummary: { attention: 1 } }]; activeId.value = 'boundary'");
    await run('loadPage(false, true)');
    assert.equal(run('pageRetry.reconcile'), true);
    assert.deepEqual(Array.from(run('rows.value'), row => row.id), ['boundary', 'cached']);
    const tree = run.mount();
    const text = node => node.text + node.children.map(text).join('');
    const find = node => node.type === 'button' && text(node).trim() === 'Retry' ? node : node.children.map(find).find(Boolean);
    await find(tree.root).props.onClick();
    await settle();
    assert.deepEqual(cursors, [undefined, undefined]);
    assert.equal(reads, 2); assert.equal(run('pageError.value'), '');
    assert.deepEqual(Array.from(run('rows.value'), row => row.id), ['boundary']);
    tree.close();
});

test('a boundary release fences the inactive edit session before filtering and a delayed save acknowledgement', async () => {
    let acknowledge; let writes = 0;
    const run = workspace(async id => ({ id, curation: { revision: 3, rating: 2, releasedAt: new Date() } }), undefined,
        { window: { write: () => { writes++; return new Promise(resolve => { acknowledge = resolve; }); } }, page: async () => ({ items: [{ id: 'boundary', acceptedAt: new Date('2026-10-12T10:01:00Z') }], nextCursor: 'next' }) });
    run("filters.Gallery.workflows = ['Waiting']; activeId.value = 'boundary'; rows.value = [{ id: 'cached', acceptedAt: new Date('2026-10-12T10:01:00Z'), curation: { revision: 1 } }]");
    const { CurationSession } = moduleAt('curation-session');
    run('editSessions').set('cached', new CurationSession({ revision: 1 }, run('window.write'), async () => ({ revision: 1 }), () => {}));
    const edit = run("editSessions.get('cached')"); edit.change({ rating: 2 }); edit.change({ rating: 4 });
    await run('loadPage(false, true)');
    assert.equal(edit.editable, false);
    acknowledge({ revision: 2, rating: 2 }); await edit.settle();
    assert.equal(writes, 1); assert.equal(edit.server.revision, 3); assert.equal(edit.intent.rating, 4); assert.equal(edit.opening.rating, 0);
});

test('boundary tag verification matches the server exact tag predicate', async () => {
    const run = workspace(async id => ({ id, curation: { tags: ['A'] } }), undefined,
        { page: async () => ({ items: [{ id: 'boundary', acceptedAt: new Date('2026-10-12T10:01:00Z'), curation: { tags: ['a'] } }], nextCursor: 'next' }) });
    run("filters.Gallery.tag = 'a'; activeId.value = 'boundary'; rows.value = [{ id: 'cached', acceptedAt: new Date('2026-10-12T10:01:00Z'), curation: { tags: ['a'] } }]");
    await run('loadPage(false, true)');
    assert.deepEqual(Array.from(run('rows.value'), row => row.id), ['boundary']);
});

test('Gallery hover shows one camera identity for paired Assets', () => {
    const run = workspace(async () => undefined);
    run("rows.value = [{ id: 'pair', displayName: 'Synthetic pair', assets: [{ variant: 'JPEG', cameraMake: 'Synthetic', cameraModel: 'Model' }, { variant: 'RAW', cameraMake: 'Synthetic', cameraModel: 'Model' }] }]");
    const tree = run.mount();
    const text = node => node.text + node.children.map(text).join('');
    assert.equal((text(tree.find('tile-detail')).match(/Synthetic Model/g) ?? []).length, 1);
    tree.close();
});

test('Gallery delivery state opens writable delivery actions while question-mark inspection remains read-only', async () => {
    const run = workspace(async id => ({ id, curation: { releasedAt: new Date() } }));
    const row = { id: 'failed', deliverySummary: { attention: 1 } };
    await run('inspectDelivery(' + JSON.stringify(row) + ')');
    assert.equal(run('inspectionMode.value'), 'delivery');
    assert.equal(run('galleryInspectionId.value'), 'failed');
    await run("inspectCapture('failed')");
    assert.equal(run('inspectionMode.value'), 'details');
    assert.notEqual(run("deliveryStatus({ deliverySummary: { retryScheduled: 1 } }).state"), 'Attention');
});

test('Reevaluate preview and confirmation are owner-fenced and require online state', async () => {
    let resolve; let previews = 0; const confirmations = [];
    const api = { management: { assets: { byAssetId: asset => ({ destinations: { byDestinationId: destination => ({
        reevaluatePreview: { get: () => { previews++; return new Promise(done => { resolve = done; }); } },
        reevaluate: { post: async body => { confirmations.push({ asset, destination, token: body.confirmationToken }); return { status: 'Queued' }; } },
    }) } }) } } };
    const view = component('ReevaluateAction', reactive({ assetId: 'a', destinationId: 'd', online: false, disabled: false }), api);
    try {
        await view.read('command()'); assert.equal(previews, 0);
        view.props.online = true;
        const stale = view.read('command()'); view.props.assetId = 'b'; await settle(); resolve({ confirmationToken: 'old-token' }); await stale;
        assert.equal(view.read('preview.value'), undefined);
        const current = view.read('command()'); resolve({ confirmationToken: 'current-token' }); await current;
        await view.read('command(true)');
        assert.deepEqual(confirmations, [{ asset: 'b', destination: 'd', token: 'current-token' }]);
        view.props.disabled = true; await settle(); await view.read('command()'); assert.equal(previews, 2);
    } finally { view.close(); }
});

test('Gallery route-only reevaluation is limited to released current targets and avoids duplicate visible work actions', async () => {
    const props = reactive({ capture: { id: 'capture', curation: { releasedAt: new Date() } }, deliveryOnly: true, readOnly: false });
    const page = { get: async () => ({ items: [] }) };
    const view = component('CaptureEvidence', props, { management: { captures: { byId: () => ({ routing: page, deliveries: page, actions: page }) } } });
    try {
        await settle();
        const decision = { assetId: 'asset', destinationId: 'destination', isCurrent: true };
        const eligible = () => view.read('canReevaluateDecision(' + JSON.stringify(decision) + ')');
        assert.equal(eligible(), true);
        view.read("deliveries.value = { items: [{ assetId: 'asset', destinationId: 'destination' }] }"); assert.equal(eligible(), false);
        view.read("deliveries.value = { items: [{ assetId: 'asset', destinationId: 'destination', state: 'Superseded' }] }"); assert.equal(eligible(), true);
        view.read('deliveries.value = { items: [] }'); props.readOnly = true; assert.equal(eligible(), false);
        props.readOnly = false; props.capture.curation.releasedAt = null; assert.equal(eligible(), false);
    } finally { view.close(); }
});

test('inline destination editing keeps its confirmed owner when the visible destination page changes', async () => {
    let items = [{ id: 'a', name: 'A', kind: 'local' }];
    const api = { management: { destinations: {
        get: async () => ({ items }),
        byId: id => ({ get: async () => ({ id, name: id.toUpperCase(), kind: 'local', configurationRevision: 1, localRootPath: 'C:/synthetic' }), routing: { get: async () => ({ destinationId: id, enabled: true, configurationRevision: 1, rules: [] }) }, work: { get: async () => ({ items: [] }) } }),
    } } };
    const view = component('DeliveryWorkspace', reactive({ online: true, generation: 0, motionEnabled: false }), api);
    try {
        view.read("destinationCapabilityState.value = 'ready'; enabledDestinationKinds.value = ['local', 'webdav']");
        await settle(); await view.read("open('a')");
        items = [{ id: 'b', name: 'B', kind: 'webdav' }]; await view.read('list()');
        assert.equal(view.read('destination.value?.id'), 'a');
        assert.equal(view.read('destination.value?.kind'), 'local');
        assert.equal(view.read('authoritativeOwner.value'), 'a');
    } finally { view.close(); }
});

test('sort changes cannot repopulate old-direction history while navigating during the new first read', async () => {
    const scope = effectScope(); let resolve; let reads = 0;
    const run = scope.run(() => workspace(async () => undefined, undefined, { watch, page: async () => ++reads === 1 ? new Promise(done => { resolve = done; }) : {
        items: [{ id: 'first', acceptedAt: new Date('2026-10-12T10:00:00Z') }, { id: 'boundary', acceptedAt: new Date('2026-10-12T10:00:30Z') }], nextCursor: 'ascending-cursor',
    } }));
    try {
        run("view.value = 'Gallery'; initialized.value = true; rows.value = [{ id: 'a', acceptedAt: new Date('2026-10-12T10:03:00Z') }, { id: 'b', acceptedAt: new Date('2026-10-12T10:02:00Z') }]; cursor.value = 'descending-cursor'; pageAfterId.value = 'b'; appearance.value.sort = 'oldest'");
        await nextTick(); await run("switchView('Delivery')");
        await run("switchView('Gallery')");
        assert.equal(run('rows.value.map(row => row.id).join(",")'), 'first,boundary');
        resolve({ items: [], nextCursor: null }); await settle();
        assert.equal(run('rows.value.map(row => row.id).join(",")'), 'first,boundary');
    } finally { scope.stop(); }
});

test('bulk rating autosave retains selection and reports only failures by photograph name', async () => {
    const run = workspace(async id => ({ id, displayName: `Photo ${id}`, curation: { revision: 1 } }),
        async () => ({ items: [{ id: 'a', status: 'committed' }, { id: 'b', status: 'conflict', errorCode: 'revision_conflict' }] }));
    run("view.value = 'Curation'; expanded.value = true; selection.value = ['a', 'b']; bulkRating.value = 4");
    await run("bulk('rating')");
    assert.deepEqual(Array.from(run('selection.value')), ['a', 'b']);
    assert.doesNotMatch(run('bulkResult.value'), /committed|a:/);
    assert.match(run('bulkResult.value'), /Photo b/);
});

test('pending bulk intent guards departure and abandoned reads cannot submit or recreate visit sessions', async () => {
    for (const leave of [false, true]) {
        let resolve; let signal; let writes = 0; let prompts = 0;
        const run = workspace((id, request) => { signal = request?.options?.[0]?.signal; return new Promise(done => { resolve = () => done({ id, curation: { revision: 1 } }); }); },
            async () => { writes++; return { items: [{ id: 'a', status: 'committed' }] }; },
            { window: { confirm: () => { prompts++; return leave; } } });
        run("view.value = 'Curation'; selection.value = ['a']");
        const pending = run("bulk('reset')"); await settle();
        await run("switchView('Sources')");
        assert.equal(prompts, 1);
        assert.equal(run('view.value'), leave ? 'Sources' : 'Curation');
        if (leave) assert.equal(signal?.aborted, true, 'discarding the visit aborts pending read-only batch preparation');
        resolve(); await pending;
        assert.equal(writes, leave ? 0 : 1);
        assert.equal(run('editSessions.size'), leave ? 0 : 1);
    }
});

test('photo and fullscreen curation stay fenced while bulk reads are pending', async () => {
    let resolve; let pendingRead = false;
    const run = workspace(id => pendingRead ? new Promise(done => { resolve = () => done({ id, curation: { revision: 1 } }); })
        : Promise.resolve({ id, curation: { revision: 1 } }));
    run("view.value = 'Curation'; selection.value = ['a']");
    await run("openCapture('a')");
    assert.equal(run('canCurate.value'), true);
    pendingRead = true;
    const pending = run("bulk('reset')"); await settle();
    assert.equal(run('canCurate.value'), false);
    assert.equal(run('unresolved.value'), true);
    resolve(); await pending;
    assert.equal(run('bulkBusy.value'), false);
});
test('aperture receives the selected view after accepted workspace changes', async () => {
    const run = workspace(async id => ({ id }));
    const tree = run.mount();
    try {
        for (const view of ['Gallery', 'Sources', 'Gallery']) {
            await run(`switchView('${view}')`);
            await Vue.nextTick();
            assert.equal(tree.find('aperture-fixture').props.view, view);
        }
    } finally {
        tree.close();
    }
});
test('cancelled navigation leaves the mounted aperture on the current view', async () => {
    const run = workspace(async id => ({ id }), undefined, { window: { confirm: () => false } });
    const tree = run.mount();
    try {
        run("editSessions.set('a', { unresolved: true })");
        await run("switchView('Sources')");
        await Vue.nextTick();
        assert.equal(tree.find('aperture-fixture').props.view, 'Gallery');
    } finally {
        tree.close();
    }
});


test('Source profile defaults require confirmed modern capability and never downgrade after failure', async () => {
    let result = { profiles: [{ profile: 'ModernFtps', enabled: true }] };
    const run = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sourceCapabilities: { get: async () => { if (result instanceof Error) throw result; return result; } } });
    assert.equal(run('connectionProfile.value'), '');
    assert.equal(run('canAssignProfile.value'), false);
    await run('refreshCapabilities()');
    assert.equal(run('connectionProfile.value'), 'ModernFtps');
    result = new Error('Synthetic outage');
    await run('refreshCapabilities()');
    assert.equal(run('connectionProfile.value'), 'ModernFtps');
    assert.equal(run('capabilityState.value'), 'failed');
    assert.equal(run('canAssignProfile.value'), false);
    const plain = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sourceCapabilities: { get: async () => ({ profiles: [{ profile: 'PlainFtpLan', enabled: true }] }) } });
    await plain('refreshCapabilities()');
    assert.equal(plain('connectionProfile.value'), '');
    assert.equal(plain('canAssignProfile.value'), false);
});

test('Source profile withdrawal preserves saved choice and dirty draft while preventing a new assignment', async () => {
    const run = sourceWorkspace(async () => ({ items: [] }), [{ id: 'a', name: 'Shared upload', connectionProfile: 'LegacyFtps', configurationRevision: 1 }], undefined, undefined, { sourceCapabilities: { get: async () => ({ profiles: [{ profile: 'LegacyFtps', enabled: false }, { profile: 'ModernFtps', enabled: true }] }) } });
    run("chosen.value = 'a'; name.value = 'My retained draft'; dirty.value = true");
    await run('refreshCapabilities()');
    assert.equal(run('connectionProfile.value'), 'LegacyFtps');
    assert.equal(run('name.value'), 'My retained draft');
    assert.equal(run('canAssignProfile.value'), true, 'unchanged saved unavailable profile can retain general settings');
    run("connectionProfile.value = 'PlainFtpLan'");
    assert.equal(run('canAssignProfile.value'), false);
    run("connectionProfile.value = 'ModernFtps'");
    assert.equal(run('canAssignProfile.value'), true);
});

test('Source save sends explicit profile and retains intent after a conflict or uncertain outcome', async () => {
    for (const status of [409, 503]) {
        let body;
        const run = sourceWorkspace(async () => ({ items: [] }), [{ id: 'a', name: 'Shared upload', connectionProfile: 'ModernFtps', configurationRevision: 7 }], undefined, undefined, { sources: { byId: () => ({ put: async value => { body = value; throw { responseStatusCode: status }; } }) }, sourceCapabilities: { get: async () => ({ profiles: [{ profile: 'PlainFtpLan', enabled: true }] }) } });
        run("chosen.value = 'a'; connectionProfile.value = 'PlainFtpLan'; name.value = 'Retained'; dirty.value = true");
        await run('refreshCapabilities()');
        await run("command('save')");
        assert.equal(body.sourceType, 'CameraFtpUpload');
        assert.equal(body.connectionProfile, 'PlainFtpLan');
        assert.equal(body.expectedRevision, 7);
        assert.equal('allowLegacyFtps' in body, false);
        assert.equal(run('connectionProfile.value'), 'PlainFtpLan');
        assert.equal(run('name.value'), 'Retained');
        assert.equal(run('dirty.value'), true);
        assert.equal(run('conflict.value'), true);
        assert.match(run('feedback.value'), status === 409 ? /conflict/ : /uncertain/);
    }
});

test('Source settings tabs retain profile warnings and support keyboard navigation', async () => {
    const run = sourceWorkspace(async () => ({ items: [] }));
    run("connectionProfile.value = 'LegacyFtps'");
    const tree = run.mount();
    const text = element => element.text + element.children.map(text).join(' ');
    const visibleWarning = element => element.props.role === 'note' && element.style.display !== 'none' ? element : element.children.map(visibleWarning).find(Boolean);
    try {
        assert.match(text(visibleWarning(tree.root)), /Compatibility encryption.*remain encrypted.*lacks forward secrecy/s);
        run("changeSettingsTab({key:'ArrowRight',preventDefault(){}},0)");
        assert.equal(run('settingsTab.value'), 'Connection');
        assert.equal(run('connectionProfile.value'), 'LegacyFtps');
        run("changeSettingsTab({key:'End',preventDefault(){}},0)");
        assert.equal(run('settingsTab.value'), 'Security');
        run("connectionProfile.value = 'PlainFtpLan'");
        await Vue.nextTick();
        assert.match(text(visibleWarning(tree.root)), /Credentials and photographs travel unencrypted.*trusted local network/s);
    } finally { tree.close(); }
});

test('Source CA download uses the selected endpoint and ignores a late result after owner change', async () => {
    let resolve; const profiles = [];
    const run = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sourceCapabilities: { byProfile: profile => { profiles.push(profile); return { caRoot: { get: () => new Promise(done => resolve = done) } }; } } });
    run("connectionProfile.value = 'ModernFtps'; capabilityState.value = 'ready'; let downloads = []; document.createElement = () => ({click(){downloads.push(this.download)}})");
    const pending = run('downloadRoot()');
    run("chosen.value = 'different'");
    resolve(new Uint8Array([65, 66]).buffer); await pending;
    assert.equal(run('downloads.length'), 0);
    run("connectionProfile.value = 'ModernFtps'");
    const accepted = run('downloadRoot()');
    resolve(new Uint8Array([65, 66]).buffer); await accepted;
    assert.equal(profiles.join(','), 'ModernFtps,ModernFtps');
    assert.equal(run('downloads[0]'), 'shutterspool-ModernFtps-ca-root.pem');
});

test('Source CA failures give setup guidance without bypass or substituting a root', async () => {
    const run = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sourceCapabilities: { byProfile: () => ({ caRoot: { get: async () => { throw { responseStatusCode: 503 }; } } }) } });
    run("connectionProfile.value = 'LegacyFtps'; capabilityState.value = 'ready'");
    await run('downloadRoot()');
    assert.match(run('caFeedback.value'), /public root configured for this endpoint/);
    assert.equal(run('connectionProfile.value'), 'LegacyFtps');
    assert.equal(run('caBusy.value'), false);
});


test('Source status reconciliation retains deployment capabilities and an in-flight initial read', async () => {
    for (const initiallyReady of [true, false]) {
        let finish;
        const run = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sourceCapabilities: { get: () => new Promise(resolve => finish = resolve) } });
        const result = { profiles: [{ profile: 'ModernFtps', enabled: true, hostname: 'synthetic.invalid', controlPort: 2121, caRootAvailable: true }] };
        const pending = run('refreshCapabilities()');
        if (initiallyReady) { finish(result); await pending; }
        await run("refreshStatus(['a','new-source'])");
        if (!initiallyReady) { finish(result); await pending; }
        assert.equal(run('capabilityState.value'), 'ready');
        assert.equal(run('selectedEndpoint.value?.hostname'), 'synthetic.invalid');
        assert.equal(run('selectedEndpoint.value?.caRootAvailable'), true);
        assert.equal(run('canAssignProfile.value'), true, 'status reconciliation must keep New Source assignments available');
        assert.equal(run('connectionProfile.value'), 'ModernFtps');
        await run('refreshStatus([])');
        assert.equal(run('canAssignProfile.value'), true, 'removing the final Source must preserve deployment setup');
    }
});


test('Source profile security choices announce consequences and summary exposes the assigned profile', async () => {
    const run = sourceWorkspace(async () => ({ items: [] }), [{ id: 'a', name: 'Shared upload', connectionProfile: 'ModernFtps', configurationRevision: 1 }]);
    run("chosen.value = 'a'");
    const tree = run.mount();
    const flatten = element => [element, ...element.children.flatMap(flatten)];
    const text = element => element.text + element.children.map(text).join(' ');
    try {
        const nodes = flatten(tree.root);
        assert.match(tree.find('source-summary').props['aria-label'], /Shared upload.*Camera \/ FTP upload.*FTPS.*recommended/s);
        for (const [profile, consequence] of [['PlainFtpLan', /credentials and photographs travel unencrypted/i], ['LegacyFtps', /lacks forward secrecy.*server-key compromise/s]]) {
            const radio = nodes.find(element => element.type === 'input' && element.props.value === profile);
            const describedBy = radio?.props['aria-describedby'];
            assert.ok(describedBy, `${profile} needs its warning before selection`);
            const warning = nodes.find(element => element.props.id === describedBy);
            assert.ok(warning, `${profile} warning must exist before selection`);
            assert.match(text(warning), consequence);
        }
    } finally { tree.close(); }
});


test('Source save failure brings recovery into view only for the current draft owner', async () => {
    for (const ownerChanges of [false, true]) {
        let fail;
        const run = sourceWorkspace(async () => ({ items: [] }), [{ id: 'a', name: 'Shared upload', connectionProfile: 'ModernFtps', configurationRevision: 1 }], undefined, undefined, { sources: { byId: () => ({ put: () => new Promise((_, reject) => fail = reject) }) } });
        run("chosen.value = 'a'; capabilityState.value = 'ready'; capabilities.value = { profiles: [{ profile: 'ModernFtps', enabled: true }] }; let focusCount = 0; let placement; feedbackNotice.value = { focus() { focusCount++; }, scrollIntoView(options) { placement = options.block; } }");
        const pending = run("command('save')");
        if (ownerChanges) run("chosen.value = 'b'");
        fail({ responseStatusCode: 500 });
        await pending;
        assert.equal(run('focusCount'), ownerChanges ? 0 : 1, 'late failures must not move focus to another draft');
        assert.equal(run('placement'), ownerChanges ? undefined : 'center', 'recovery must be above fixed phone navigation');
        if (!ownerChanges) {
            const tree = run.mount();
            const flatten = element => [element, ...element.children.flatMap(flatten)];
            try {
                const notice = flatten(tree.root).find(element => element.props.role === 'status' && element.children.some(child => child.type === 'button' && child.text.includes('Reload saved settings')));
                assert.ok(notice, 'failure needs an actionable recovery notice');
                assert.equal(notice.props.tabindex, '-1', 'recovery notice must accept programmatic focus');
            } finally { tree.close(); }
        }
    }
});


test('Source setup retains loaded profiles through recovery checks until an explicit refresh', async () => {
    let reads = 0;
    const run = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sourceCapabilities: { get: async () => { reads++; return { profiles: [{ profile: 'ModernFtps', enabled: true, hostname: 'ingest.example.test', controlPort: 2121 }] }; } } }, true);
    await run('refreshCapabilities()');
    run("name.value = 'Retained setup draft'; dirty.value = true; props.recoverySequence = 1");
    await run('nextTick()');
    run('props.recoverySequence = 2');
    await run('nextTick()');
    assert.equal(reads, 1, 'foreground/recovery checks must not reload deployment setup');
    assert.equal(run('capabilityState.value'), 'ready');
    assert.equal(run('selectedEndpoint.value.hostname'), 'ingest.example.test');
    assert.equal(run('canAssignProfile.value'), true);
    assert.equal(run('name.value'), 'Retained setup draft');
    await run('refreshCapabilities()');
    assert.equal(reads, 2, 'an explicit retry remains available');
});


test('Source custom credentials submit exact bytes independently of generated username and clear after rejection', async () => {
    const requests = [];
    const run = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sources: { post: async body => { requests.push(body); throw { responseStatusCode: 400, errors: { additionalData: { password: ['Password does not meet deployment policy.'] } } }; } } });
    run("capabilityState.value = 'ready'; connectionProfile.value = 'ModernFtps'; capabilities.value = { profiles: [{profile:'ModernFtps',enabled:true}],passwordPolicy:{minimumLength:12,maximumLength:128,generatedLength:20} }; name.value = 'Shared camera'; passwordMode.value = 'Custom'; customPassword.value = 'Exact_CASE!123'; dirty.value = true");
    await run("command('create')");
    assert.equal(requests[0].password, 'Exact_CASE!123');
    assert.equal(requests[0].username, undefined);
    assert.equal(requests[0].passwordMode, 'Custom');
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('name.value'), 'Shared camera');
    assert.equal(run('dirty.value'), true);
    assert.equal(run('credentialErrors.value.password'), 'Password does not meet deployment policy.');
    assert.equal(run('settingsTab.value'), 'Connection');
    assert.equal(run('secret.value'), '');
});

test('Source replacement keeps non-secret settings draft and revision while generated mode excludes custom password', async () => {
    let request;
    const run = sourceWorkspace(async () => ({ items: [] }), [{id:'a',name:'Saved name',username:'old_user',configurationRevision:4}], undefined, undefined, { sources: { byId: () => ({regenerateSecret:{post:async body => {request=body;return {current:{id:'a',username:'new_user',configurationRevision:5},secret:'SYNTHETIC_ONCE'};}}}) } });
    run("chosen.value = 'a'; revision.value = 4; name.value = 'Unsaved name'; username.value = 'new_user'; passwordMode.value = 'Generated'; customPassword.value = 'stale-private-input'; dirty.value = true");
    await run("command('secret')");
    assert.equal(request.expectedRevision, 4);
    assert.equal(request.username, 'new_user');
    assert.equal(request.passwordMode, 'Generated');
    assert.equal(request.password, undefined);
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('secret.value'), 'SYNTHETIC_ONCE');
    assert.equal(run('name.value'), 'Unsaved name');
    assert.equal(run('dirty.value'), true);
    assert.equal(run('revision.value'), 5);
    run("chosen.value = 'b'");
    assert.equal(run('secret.value'), '');
});

test('Source uncertain creation blocks blind replay and retains draft until a successful reread', async () => {
    let writes = 0; let reads = 0; let failRead = true;
    const run = sourceWorkspace(async () => ({ items: [] }), [], undefined, undefined, { sources: { post: async () => {writes++;throw {responseStatusCode:503};},get:async () => {reads++;if(failRead)throw {responseStatusCode:503};return {items:[]};} } });
    run("capabilityState.value='ready'; connectionProfile.value='ModernFtps'; capabilities.value={profiles:[{profile:'ModernFtps',enabled:true}]}; name.value='Retained'; username.value='chosen_user'; customPassword.value='SYNTHETIC_PRIVATE'; dirty.value=true");
    await run("command('create')");
    await run("command('create')");
    assert.equal(writes, 1);
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('conflict.value'), true);
    await run('reload()');
    assert.equal(run('conflict.value'), true);
    failRead = false;
    await run('reload()');
    assert.equal(reads, 2);
    assert.equal(run('conflict.value'), false);
    assert.equal(run('name.value'), 'Retained');
    assert.equal(run('username.value'), 'chosen_user');
    assert.equal(run('dirty.value'), true);
    assert.equal(writes, 1);
});


test('Source replacement confirmation precedes mutation and access loss clears private inputs and fences disclosures', async () => {
    let finish; let writes = 0;
    const run = sourceWorkspace(async () => ({items:[]}), [{id:'a',name:'Shared camera',username:'synthetic_user',configurationRevision:1}], undefined, undefined, {sources:{byId:()=>({regenerateSecret:{post:()=>{writes++;return new Promise(resolve=>finish=resolve);}}})}});
    run("chosen.value='a'; passwordMode.value='Custom'; customPassword.value='SYNTHETIC_PRIVATE'; secret.value='SYNTHETIC_OLD'; let confirmations=0; credentialDialog.value={showModal(){confirmations++},close(){}}; capabilities.value={passwordPolicy:{minimumLength:12,maximumLength:128,generatedLength:20}}; requestCredentialReplacement()");
    assert.equal(run('confirmations'), 1);
    assert.equal(writes, 0);
    const pending = run("command('secret')");
    assert.equal(writes, 1);
    run.loseAccess();
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('secret.value'), '');
    finish({current:{id:'a',configurationRevision:2},secret:'SYNTHETIC_LATE'});
    await pending;
    assert.equal(run('secret.value'), '');
    assert.equal(run('chosen.value'), '');
});


test('Source duplicate usernames from generated conflict extensions remain field errors for creation and replacement', async () => {
    for (const action of ['create', 'secret']) {
        for (const extensions of [
            {errors:{username:['This username is already in use.']}},
            {field:'username',errorCode:'SOURCE_USERNAME_CONFLICT'},
        ]) {
            let writes = 0; let reads = 0;
            const rejected = async () => { writes++; throw {responseStatusCode:409,title:'This username is already in use.',additionalData:extensions}; };
            const run = sourceWorkspace(async () => ({items:[]}), [{id:'a',name:'Saved camera',username:'old_name',configurationRevision:4}], undefined, undefined, {sources:{post:rejected,get:async()=>{reads++;return {items:[]};},byId:()=>({regenerateSecret:{post:rejected}})}});
            run("capabilityState.value='ready'; connectionProfile.value='ModernFtps'; capabilities.value={profiles:[{profile:'ModernFtps',enabled:true}],passwordPolicy:{minimumLength:12,maximumLength:128,generatedLength:20}};");
            if (action === 'secret') run("chosen.value='a'; revision.value=4");
            run("name.value='Retained camera draft'; username.value='duplicate_name'; passwordMode.value='Custom'; customPassword.value='SYNTHETIC_PRIVATE'; dirty.value=true;");
            await run(`command('${action}')`);
            assert.equal(writes, 1);
            assert.equal(reads, 0, 'a definitively rejected username needs no recovery read');
            assert.equal(run('credentialErrors.value.username'), 'This username is already in use.');
            assert.equal(run('conflict.value'), false);
            assert.equal(run('name.value'), 'Retained camera draft');
            assert.equal(run('username.value'), 'duplicate_name');
            assert.equal(run('customPassword.value'), '');
            assert.equal(run('secret.value'), '');
            assert.equal(run('dirty.value'), true);
            assert.equal(run('settingsTab.value'), 'Connection');
        }
    }
});


test('Source username browser pattern accepts hyphens and underscores and rejects paths or spaces', () => {
    const file = readFileSync(new URL('../app/components/SourceWorkspace.vue', import.meta.url), 'utf8');
    const pattern = file.match(/name="source-username"[^>]* pattern="([^"]+)"/)?.[1];
    assert.ok(pattern, 'the username input must declare its protocol-safe browser validation');
    const valid = new RegExp(`^(?:${pattern})$`, 'v');
    for (const username of ['a', 'Camera-7', 'camera_one', '7_camera-one', 'a'.repeat(64)]) assert.equal(valid.test(username), true, username);
    for (const username of ['', '-camera', '_camera', 'camera/root', 'camera name', 'a'.repeat(65)]) assert.equal(valid.test(username), false, username);
});


test('Source committed creation with lost response rereads the created Source before deliberate password replacement', async () => {
    const events = [];
    let committed;
    const run = sourceWorkspace(async () => ({items:[]}), [], undefined, undefined, {sources:{
        post:async body => {
            events.push('create');
            committed = {id:'committed-camera',name:body.name,username:body.username,configurationRevision:1,connectionProfile:'ModernFtps'};
            throw {responseStatusCode:503};
        },
        get:async () => { events.push(`read:${committed.id}:${committed.configurationRevision}`); return {items:[{...committed}]}; },
        byId:() => ({regenerateSecret:{post:async body => {
            events.push(`replace:${body.expectedRevision}`);
            committed = {...committed,configurationRevision:2};
            return {current:{...committed},secret:'SYNTHETIC_NEW_DISCLOSURE'};
        }}}),
    }});
    run("capabilityState.value='ready'; connectionProfile.value='ModernFtps'; capabilities.value={profiles:[{profile:'ModernFtps',enabled:true}]}; name.value='Camera draft'; username.value='chosen_user'; passwordMode.value='Generated'; customPassword.value='SYNTHETIC_PRIVATE'; dirty.value=true; secret.value='SYNTHETIC_OLDER_DISCLOSURE'");
    await run("command('create')");
    assert.equal(committed.id, 'committed-camera', 'the server committed even though the response was lost');
    assert.equal(run('chosen.value'), '');
    assert.equal(run('secret.value'), '');
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('name.value'), 'Camera draft');
    assert.equal(run('username.value'), 'chosen_user');
    assert.equal(run('dirty.value'), true);
    assert.equal(run('conflict.value'), true);
    await run("command('create')");
    assert.deepEqual(events, ['create'], 'the uncertain write cannot be replayed');
    await run('reload()');
    assert.deepEqual(events, ['create','read:committed-camera:1']);
    assert.equal(run('conflict.value'), false);
    assert.equal(run('secret.value'), '', 'a committed Source read cannot recover its password');
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('name.value'), 'Camera draft');
    assert.equal(run('username.value'), 'chosen_user');
    assert.equal(run('dirty.value'), true);
    assert.match(run('feedback.value'), /cannot be recovered/);
    // The user selects the now-visible committed Source and deliberately replaces its lost password.
    run("props.sources=[{id:'committed-camera',name:'Camera draft',username:'chosen_user',configurationRevision:1,connectionProfile:'ModernFtps'}]; chosen.value='committed-camera'");
    await run("command('secret')");
    assert.deepEqual(events, ['create','read:committed-camera:1','replace:1']);
    assert.equal(run('secret.value'), 'SYNTHETIC_NEW_DISCLOSURE');
});

test('Source custom password checks show policy boundaries and block invalid credential writes before confirmation', async () => {
    let writes = 0;
    const run = sourceWorkspace(async () => ({items:[]}), [], undefined, undefined, {sources:{post:async()=>{writes++;},byId:()=>({regenerateSecret:{post:async()=>{writes++;}}})}});
    run("capabilityState.value='ready'; connectionProfile.value='ModernFtps'; capabilities.value={profiles:[{profile:'ModernFtps',enabled:true}],passwordPolicy:{minimumLength:12,maximumLength:128,generatedLength:20}}; passwordMode.value='Custom'; let confirmations=0; credentialDialog.value={showModal(){confirmations++},close(){}}");
    for (const password of ['', 'A'.repeat(11), 'A'.repeat(129), 'Camera password12', 'CameräPassword12', 'CameraPassword12\n']) {
        run(`customPassword.value=${JSON.stringify(password)}`);
        await run("command('create')");
        await run("command('secret')");
        run('requestCredentialReplacement()');
        assert.equal(writes, 0, 'invalid credentials must not reach either write endpoint');
        assert.equal(run('customPasswordValid.value'), false);
        assert.equal(run('confirmations'), 0, 'invalid passwords must be corrected before confirmation');
        assert.equal(run('customPassword.value'), password, 'local validation must not truncate or clear the entry');
    }
    for (const [minimum, password] of [[1,'!'],[12,'CameraPassword!12'],[128,'A'.repeat(128)]]) {
        run(`capabilities.value.passwordPolicy.minimumLength=${minimum}; customPassword.value=${JSON.stringify(password)}`);
        assert.equal(run('passwordLengthValid.value'), true);
        assert.equal(run('passwordCharactersValid.value'), true);
        assert.equal(run('customPasswordValid.value'), true);
    }
    const tree = run.mount();
    try {
        const flatten = element => [element, ...element.children.flatMap(flatten)];
        const nodes = flatten(tree.root);
        assert.ok(nodes.some(element => element.props.role === 'status' && element.text.includes('Password meets requirements')));
        assert.equal(nodes.find(element => element.props.id === 'source-custom-password').props.maxlength, undefined, 'overlong passwords must be explained, not silently truncated');
    } finally { tree.close(); }
});

test('Source committed replacement with lost response rereads the advanced revision before a deliberate new replacement', async () => {
    let committed = {id:'a',name:'Saved camera',username:'saved_user',configurationRevision:4,connectionProfile:'ModernFtps'};
    const events = [];
    const run = sourceWorkspace(async () => ({items:[]}), [{...committed}], undefined, undefined, {sources:{byId:() => ({
        get:async () => { events.push(`read:${committed.configurationRevision}`); return {...committed}; },
        regenerateSecret:{post:async body => {
            events.push(`replace:${body.expectedRevision}`);
            assert.equal(body.expectedRevision, committed.configurationRevision);
            committed = {...committed,username:body.username,configurationRevision:committed.configurationRevision+1};
            if (committed.configurationRevision === 5) throw {responseStatusCode:503};
            return {current:{...committed},secret:null};
        }},
    })}});
    run("capabilities.value={passwordPolicy:{minimumLength:12,maximumLength:128,generatedLength:20}}; chosen.value='a'; name.value='Retained settings draft'; username.value='new_user'; passwordMode.value='Custom'; customPassword.value='SYNTHETIC_CUSTOM_ONE'; dirty.value=true; secret.value='SYNTHETIC_OLDER_DISCLOSURE'");
    await run("command('secret')");
    assert.equal(committed.configurationRevision, 5, 'the rejected transport response follows a committed replacement');
    assert.equal(committed.username, 'new_user');
    assert.equal(run('revision.value'), 4, 'the client cannot infer a committed revision from a failed response');
    assert.equal(run('conflict.value'), true);
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('secret.value'), '');
    await run("command('secret')");
    assert.deepEqual(events, ['replace:4'], 'no blind replacement replay');
    await run('reload()');
    assert.deepEqual(events, ['replace:4','read:5']);
    assert.equal(run('revision.value'), 5);
    assert.equal(run('conflict.value'), false);
    assert.equal(run('name.value'), 'Retained settings draft');
    assert.equal(run('username.value'), 'new_user');
    assert.equal(run('dirty.value'), true);
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('secret.value'), '', 'the authoritative reread exposes no password');
    run("customPassword.value='SYNTHETIC_CUSTOM_TWO'");
    await run("command('secret')");
    assert.deepEqual(events, ['replace:4','read:5','replace:5']);
    assert.equal(committed.configurationRevision, 6);
    assert.equal(run('revision.value'), 6);
    assert.equal(run('customPassword.value'), '');
    assert.equal(run('secret.value'), '');
    assert.equal(run('name.value'), 'Retained settings draft');
});


test('personal preference scopes reset independently without touching filters or drafts', () => {
    const run = workspace(async () => undefined);
    run("workspacePreferences.width='full'; appearances.Curation.density=310; appearances.Gallery.fit='fit'; filters.Gallery.tag='keep'; tagDrafts.set('a','unfinished'); resetAppearance()");
    assert.equal(run('appearances.Gallery.fit'), 'fill'); assert.equal(run('appearances.Curation.density'), 310);
    assert.equal(run('workspacePreferences.width'), 'full'); assert.equal(run('filters.Gallery.tag'), 'keep'); assert.equal(run("tagDrafts.get('a')"), 'unfinished');
    run("appearances.Gallery.fit='fit'; resetWorkspace()");
    assert.equal(run('workspacePreferences.width'), 'standard'); assert.equal(run('appearances.Gallery.fit'), 'fit'); assert.equal(run('appearances.Curation.density'), 310);
});
test('browser preferences hydrate before writes, batch edits, isolate identities and recover from a failed explicit write', async () => {
    const scope = effectScope(); const records = new Map(); let denied = false; let writes = 0;
    const first = 'shutterspool-appearance-synthetic-operator', second = 'shutterspool-appearance-synthetic-other';
    records.set(first, JSON.stringify({ appearance: { width:'full', density:'spacious' } }));
    const run = scope.run(() => workspace(async () => undefined, undefined, { watch, storage: { getItem: key => records.get(key) ?? null, setItem: (key,value) => { writes++; if(denied) throw Error('full'); records.set(key,value); } } }));
    try {
        assert.equal(writes,0); assert.equal(run('workspacePreferences.width'),'full');
        run('appearances.Gallery.density=400'); run('flushPreferenceSave()'); assert.equal(writes,1); assert.equal(JSON.parse(records.get(first)).appearances.Curation.density,310);
        run("session.value={profile:{iss:'synthetic',sub:'other'}}"); assert.equal(writes,1); assert.equal(run('workspacePreferences.width'),'standard');
        denied=true; run("workspacePreferences.width='full'"); run('flushPreferenceSave()'); assert.match(run('preferenceNotice.value'), /couldn't save/); assert.equal(records.has(second),false);
        denied=false; run("workspacePreferences.motion='reduce'"); run('flushPreferenceSave()'); assert.equal(run('preferenceNotice.value'),''); assert.equal(JSON.parse(records.get(second)).workspace.width,'full');
        const previous=records.get(first); run('loseAccess(); workspacePreferences.width="standard"'); assert.equal(records.get(first),previous); assert.equal(records.has(''),false);
    } finally { scope.stop(); }
});
test('corrupt or future preferences do not write during hydration, and failed migration keeps the old record', async () => {
    for (const raw of ['{broken','{"version":99}', '{"appearance":{"width":"full"}}']) {
        const scope=effectScope(); let saved=raw; let writes=0;
        const run=scope.run(()=>workspace(async()=>undefined, undefined, {watch,storage:{getItem:()=>saved,setItem:()=>{writes++;throw Error('full');}}}));
        try { assert.equal(writes,0); run("workspacePreferences.motion='reduce'"); run('flushPreferenceSave()'); assert.equal(saved,raw); assert.ok(run('preferenceNotice.value')); }
        finally {scope.stop();}
    }
});
test('numeric preview editing retains applied dimensions until a valid blur or Enter commit', () => {
    const run=workspace(async()=>undefined);
    for(const text of ['', '9','512.5','4097']) {
        run(`previewDrafts.gridMaximumDimension=${JSON.stringify(text)}; commitPreview('gridMaximumDimension')`);
        assert.equal(run('workspacePreferences.gridMaximumDimension'),512); assert.match(run('previewErrors.gridMaximumDimension'),/64 to 4096/);
    }
    run("previewDrafts.gridMaximumDimension='1024'"); assert.equal(run('workspacePreferences.gridMaximumDimension'),512);
    run("commitPreview('gridMaximumDimension')"); assert.equal(run('workspacePreferences.gridMaximumDimension'),1024); assert.equal(run('previewErrors.gridMaximumDimension'),'');
});
test('Settings entry respects unresolved curation, keeps Storage dirty across sections and guards departure', async () => {
    let allow=false;
    const run=workspace(async()=>undefined,undefined,{window:{confirm:()=>allow}});
    run("tagDrafts.set('a','unfinished'); editSessions.set('a',{unresolved:true,stop(){}})");
    await run("openSettings()"); assert.equal(run('view.value'),'Gallery');
    allow=true; await run('openSettings()'); assert.equal(run('settingsSection.value'),'Workspace'); assert.equal(run('tagDrafts.size'),0);
    run("selectSettingsSection('Storage'); settingsDirty.value=true; selectSettingsSection('Workspace')");
    assert.equal(run('storageVisited.value'),true); assert.equal(run('settingsDirty.value'),true);
    allow=false; await run('returnFromSettings()'); assert.equal(run('view.value'),'Settings');
    allow=true; await run('returnFromSettings()'); assert.equal(run('view.value'),'Gallery'); assert.equal(run('settingsDirty.value'),false); assert.equal(run('storageVisited.value'),false);
});
test('changing Gallery order leaves Curation browsing and intent intact and never selects unloaded photos', async () => {
    const scope=effectScope(); const run=scope.run(()=>workspace(async()=>undefined,undefined,{watch}));
    try {
        run("browsing.Curation={rows:[{id:'curation'}],cursor:'keep',afterId:'curation'}; selection.value=['old']; tagDrafts.set('a','intent'); appearances.Gallery.sort='oldest'");
        assert.equal(run('browsing.Curation.cursor'),'keep'); assert.equal(run('appearances.Curation.sort'),'newest'); assert.equal(run('selection.value.length'),0); assert.equal(run("tagDrafts.get('a')"),'intent');
    } finally {scope.stop();}
});
test('appearance order reset restores only selections still loaded by its asynchronous first page', async () => {
    const scope = effectScope(); let finish;
    const run = scope.run(() => workspace(async id => ({ id }), undefined, { watch, page: () => new Promise(resolve => { finish = resolve; }) }));
    try {
        run("hydratingPreferences.value=true; appearances.Gallery.sort='oldest'; initialized.value=true; rows.value=[{id:'a'},{id:'b'}]; selection.value=['a','b']; hydratingPreferences.value=false; resetAppearance()");
        assert.equal(run('selection.value.length'), 0, 'unloaded membership is never claimed during the read');
        finish({ items: [{ id: 'a' }, { id: 'c' }] }); await settle();
        assert.equal(run("selection.value.join(',')"), 'a');
    } finally { scope.stop(); }
});
test('explicit Select none during an order read prevents a late response from restoring selection', async () => {
    const scope = effectScope(); let finish;
    const run = scope.run(() => workspace(async id => ({ id }), undefined, { watch, page: () => new Promise(resolve => { finish = resolve; }) }));
    try {
        run("initialized.value=true; rows.value=[{id:'a'}]; selection.value=['a']; appearances.Gallery.sort='oldest'; selection.value=[]");
        finish({ items: [{ id: 'a' }] }); await settle();
        assert.equal(run('selection.value.length'), 0);
    } finally { scope.stop(); }
});
test('Trash Storage entry obeys curation departure guards and returns to the originating Trash surface', async () => {
    let allow = false;
    const run = workspace(async () => undefined, undefined, { window: { confirm: () => allow } });
    run("retentionSurface.value='trash'; editSessions.set('a',{unresolved:true,stop(){}})");
    await run("openSettings('Storage')"); assert.equal(run('view.value'), 'Gallery'); assert.equal(run('retentionSurface.value'), 'trash');
    allow = true; await run("openSettings('Storage')");
    assert.equal(run('settingsSection.value'), 'Storage'); assert.equal(run('storageVisited.value'), true);
    await run('returnFromSettings()'); assert.equal(run('view.value'), 'Gallery'); assert.equal(run('retentionSurface.value'), 'trash');
});
test('denied browser storage reads leave usable in-memory preferences without hydration writes', async () => {
    const scope = effectScope(); let writes = 0;
    const run = scope.run(() => workspace(async () => undefined, undefined, { watch, storage: { getItem() { throw Error('denied'); }, setItem() { writes++; throw Error('denied'); } } }));
    try {
        assert.equal(writes, 0); assert.ok(run('preferenceNotice.value'));
        run("workspacePreferences.width='full'; appearances.Gallery.fit='fit'"); run('flushPreferenceSave()');
        assert.equal(run('workspacePreferences.width'), 'full'); assert.equal(run('appearances.Gallery.fit'), 'fit'); assert.equal(writes, 1);
    } finally { scope.stop(); }
});
test('tabs keep their own presentation and hydration never overwrites the last explicit writer', async () => {
    let saved = null; let writes = 0;
    const storage = { getItem: () => saved, setItem: (_key, value) => { saved = value; writes++; } };
    const scopes = [effectScope(), effectScope(), effectScope()];
    try {
        const first = scopes[0].run(() => workspace(async () => undefined, undefined, { watch, storage }));
        const second = scopes[1].run(() => workspace(async () => undefined, undefined, { watch, storage }));
        assert.equal(writes, 0);
        first("workspacePreferences.width='full'"); first('flushPreferenceSave()'); assert.equal(writes, 1); assert.equal(second('workspacePreferences.width'), 'standard');
        second("workspacePreferences.motion='reduce'"); second('flushPreferenceSave()'); assert.equal(writes, 2); assert.equal(first('workspacePreferences.motion'), 'system');
        const reload = scopes[2].run(() => workspace(async () => undefined, undefined, { watch, storage }));
        assert.equal(writes, 2); assert.equal(reload('workspacePreferences.width'), 'standard'); assert.equal(reload('workspacePreferences.motion'), 'reduce');
        assert.equal(first('workspacePreferences.width'), 'full');
    } finally { for (const scope of scopes) scope.stop(); }
});

test('Gallery playback snapshots filters, keeps grid and selection unchanged and stops on hidden tab or access loss', async () => {
 const a={id:'a',displayName:'A',curation:{revision:1,tags:[]}},b={id:'b',displayName:'B',curation:{revision:1,tags:[]}};
 const reads=[];
 const run=workspace(async id=>id==='a'?a:b,undefined,{page:async config=>{reads.push(config);return {items:[a,b]};}});
 run("view.value='Gallery'; full.value=true; activeId.value='a'; rows.value=[{id:'a'}]; selection.value=['a']; filters.Gallery.sourceIds=['source']; filters.Gallery.collectionIds=['collection']; filters.Gallery.labels=['Blue']; filters.Gallery.workflows=['Waiting']; filters.Gallery.tag='morning'; slideshowReady.value=true; slideshowPreview.value='ready'");
 try {
  await run('toggleSlideshow()');
  assert.equal(run('slideshow.playing'),true);
  assert.equal(reads[0].queryParameters.startAt,'a');
  assert.deepEqual(Array.from(reads[0].queryParameters.sourceIds),['source']);
  assert.deepEqual(Array.from(reads[0].queryParameters.collectionIds),['collection']);
  assert.equal(reads[0].queryParameters.tag,'morning');
  assert.equal(run('rows.value.length'),1);assert.equal(run('selection.value.join()'),'a');
  run("filters.Gallery.sourceIds.push('later')");assert.deepEqual(Array.from(reads[0].queryParameters.sourceIds),['source']);
  run('slideshowDisplayed("ready")');
  run('document.hidden=true; visibilityChanged()');assert.equal(run('slideshow.playing'),false);assert.match(run('slideshow.message'),/hidden/);
  run('document.hidden=false; visibilityChanged()');assert.equal(run('slideshow.playing'),false);
  await run('toggleSlideshow()');run('loseAccess()');assert.equal(run('slideshow.playing'),false);assert.equal(run('slideshow.seconds'),5);assert.equal(run('full.value'),false);
 } finally {run('endSlideshow()');}
});

test('closing the Gallery viewer fences slideshow reads and reopening resets its interval', async () => {
 let complete;const run=workspace(async id=>({id,curation:{revision:1,tags:[]}}),undefined,{page:()=>new Promise(resolve=>complete=resolve)});
 run("view.value='Gallery'; full.value=true; activeId.value='a'; slideshowReady.value=true; playback.interval(10)");
 const pending=run('toggleSlideshow()');run('closeFull(false)');complete({items:[{id:'late'}]});await pending;
 assert.equal(run('full.value'),false);assert.equal(run('activeId.value'),'a');assert.equal(run('slideshow.playing'),false);assert.equal(run('slideshow.seconds'),5);assert.equal(run('slideshowQuery'),undefined);
});

test('route guard preserves internal slideshow URLs and immediately cancels navigation away despite stale route props', () => {
    let guard;
    const run = workspace(async id => ({ id }), undefined, { router: { replace: async () => {}, beforeEach: callback => { guard = callback; return () => {}; } } });
    run("full.value=true; view.value='Gallery'; activeId.value='a'; slideshow.playing=true");
    assert.equal(guard({path:'/captures/a', query:{}}), true);
    assert.equal(run('slideshow.playing'), true);
    assert.equal(guard({path:'/', query:{}}), true);
    assert.equal(run('slideshow.playing'), false);
    assert.equal(run('full.value'), false);
});

test('Gallery uses image readiness received while its first slideshow page is still pending', async () => {
    let complete;
    const scheduled = [];
    const run = workspace(async id => ({id}), undefined, {page: () => new Promise(resolve => complete = resolve), window: {scheduled}});
    run("full.value=true; view.value='Gallery'; activeId.value='a'; slideshowReady.value=true; playback.dependencies.schedule=(_callback,ms)=>{window.scheduled.push(ms);return 0}; playback.dependencies.cancel=()=>{}");
    try {
        const playing = run('toggleSlideshow()');
        run("slideshowDisplayed('ready')");
        complete({items:[{id:'a'},{id:'b'}]});
        await playing;
        assert.equal(scheduled.at(-1), 5000);
        assert.equal(run('slideshow.playing'), true);
    } finally { run('endSlideshow()'); }
});

test('paused Gallery clears a displayed purged Capture after authoritative detail reconciliation', async () => {
    const run = workspace(async id => ({id, retention:{disposition:'purged'},curation:{revision:1,tags:[]}}));
    run("full.value=true; view.value='Gallery'; activeId.value='a'; capture.value={id:'a'}; slideshow.playing=false; slideshowPreview.value='ready'");
    try {
        await run("openCapture('a')");
        assert.equal(run('capture.value'), undefined);
        assert.notEqual(run('activeId.value'), 'a');
        assert.equal(run('slideshowPreview.value'), 'pending');
        assert.equal(run('slideshow.playing'), false);
    } finally { run('endSlideshow()'); }
});

test('300 read-only Gallery detail opens retain existing Curation baselines without accumulating sessions', async () => {
    const run = workspace(async id => ({id,retention:{disposition:'active'},curation:{revision:1,rating:0,tags:[]}}));
    run("observeCuration('existing',{revision:1,rating:3,tags:['keep']}); const existingSession=editSessions.get('existing'); tagDrafts.set('existing','unfinished'); full.value=true; view.value='Gallery'");
    for (let index=0; index<300; index++) await run(`openCapture('slide-${index}')`);
    assert.equal(run('editSessions.size'), 1);
    assert.equal(run("editSessions.get('existing')===existingSession"), true);
    assert.equal(run("editSessions.get('existing').intent.rating"), 3);
    assert.equal(run("tagDrafts.get('existing')"), 'unfinished');
    run('endSlideshow()');
});

test('active direct Capture links remain visible outside Gallery filters before explicit playback', async () => {
    const run = workspace(async id => ({id,retention:{disposition:'active'},curation:{revision:1,rating:0,tags:['other']}}));
    run("full.value=true; view.value='Gallery'; activeId.value='direct'; filters.Gallery.tag='morning'");
    await run("openCapture('direct')");
    assert.equal(run('activeId.value'), 'direct');
    assert.equal(run('capture.value.id'), 'direct');
    assert.equal(run('slideshowQuery'), undefined);
    assert.equal(run('full.value'), true);
    run('endSlideshow()');
});

test('both fullscreen workspaces wake hidden controls without activating the wake click or blocking zoom pointers', () => {
    for (const view of ['Gallery','Curation']) {
        const counters={prevented:0,stopped:0,captured:0};
        const run=workspace(async()=>({}),undefined,{window:{counters}});
        run(`full.value=true;view.value='${view}';chrome.start();viewerChrome.visible=false`);
        try {
        run("fullElement.value={clientWidth:200,clientHeight:100,querySelector:()=>null,getBoundingClientRect:()=>({left:0,top:0,width:200,height:100})}; viewerPointerDown({pointerType:'touch'}); pointerDown({pointerId:1,clientX:20,clientY:30,currentTarget:{setPointerCapture(){window.counters.captured++}}}); pointerDown({pointerId:2,clientX:30,clientY:30,currentTarget:{setPointerCapture(){window.counters.captured++}}}); pointerMove({pointerId:2,clientX:50,clientY:30}); viewerClick({preventDefault(){window.counters.prevented++},stopPropagation(){window.counters.stopped++}})");
            assert.equal(run('viewerChrome.visible'),true);
            assert.deepEqual(counters,{prevented:1,stopped:1,captured:2});
            if (view === 'Curation') assert.ok(run('zoom.value') > 1);
            run("viewerPointerDown({pointerType:'touch'});viewerClick({preventDefault(){window.counters.prevented++},stopPropagation(){window.counters.stopped++}})");
            assert.equal(counters.stopped,1);
        } finally {run('chrome.stop()');}
    }
});

test('hidden fullscreen controls are revealed before Tab focuses the first control and lifecycle cancels idle work', () => {
    const timers=new Map();let sequence=0,focused=0,prevented=0;
    const run=workspace(async()=>({}),undefined,{nextTick:callback=>callback?.(),window:{timers,schedule:(callback,ms)=>{timers.set(++sequence,{callback,ms});return sequence},cancel:id=>timers.delete(id),prevent:()=>prevented++,focus:()=>focused++}});
    run("full.value=true;chrome.clock.schedule=window.schedule;chrome.clock.cancel=window.cancel;chrome.start();viewerChrome.visible=false");
    run('fullElement.value={querySelectorAll:()=>[{focus:()=>window.focus()}]}');
    run("keyboard({key:'Tab',shiftKey:false,target:{closest:()=>null},preventDefault:window.prevent})");
    assert.equal(run('viewerChrome.visible'),true);
    assert.equal(prevented,1);
    assert.equal(focused,1);
    run('closeFull(false)');assert.equal(timers.size,0);
});

test('automatic Gallery frame changes do not reveal idle controls and access loss clears the timer', async () => {
    const timers=new Map();let sequence=0;
    const run=workspace(async id=>({id,retention:{disposition:'active'},curation:{revision:1,rating:0,tags:[]}}),undefined,{window:{timers,schedule:callback=>{timers.set(++sequence,callback);return sequence},cancel:id=>timers.delete(id)}});
    run("full.value=true;view.value='Gallery';chrome.clock.schedule=window.schedule;chrome.clock.cancel=window.cancel;chrome.start();viewerChrome.visible=false");
    await run("openCapture('next-slide')");
    assert.equal(run('viewerChrome.visible'),false);
    run('chrome.pointer()');assert.equal(timers.size,1);
    run('loseAccess()');assert.equal(timers.size,0);
    assert.equal(run('full.value'),false);
});

test('fullscreen recovery messages remain actionable after interruption and successful sequence retry', async () => {
    const run=workspace(async id=>({id}),undefined,{page:async()=>({items:[{id:'a'}]})});
    run("full.value=true;view.value='Gallery';slideshow.playing=true;document.hidden=true");
    run('visibilityChanged()');
    assert.equal(run('slideshow.retry'),false);
    assert.match(run('slideshow.message'),/Choose Play/);
    assert.equal(run('viewerRecovery.value'),true);
    await run("playback.retry('a')");
    assert.equal(run('slideshow.retry'),false);
    assert.match(run('slideshow.message'),/Sequence available.*Choose Play/);
    assert.equal(run('viewerRecovery.value'),true);
    run("slideshow.message='';slideshow.photographsOnly=true");
    assert.equal(run('viewerRecovery.value'),false);
    run('endSlideshow()');
});
