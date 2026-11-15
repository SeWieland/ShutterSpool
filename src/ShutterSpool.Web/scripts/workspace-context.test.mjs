import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { parse } from '@vue/compiler-sfc';
import { computed, reactive, ref, shallowRef } from 'vue';
import { createMemoryHistory, createRouter } from 'vue-router';
import { readWorkspaceId, workspaceRequestHeaders, workspaceSelection, withWorkspace } from '../app/lib/workspace-context.ts';

const first = '2f2ed010-2a51-468a-a38a-a2372fba8211';
const second = '6254ea93-b93b-4707-9b3d-b9b8e226b5d1';
const memberships = [{ id: first, name: 'Studio' }];

test('reads one valid workspace ID from the current page URL', () => {
    assert.equal(readWorkspaceId(`?workspace=${first}`), first);
    assert.equal(readWorkspaceId(`?workspace=${first.toUpperCase()}`), first);
    assert.equal(readWorkspaceId(''), null);
    assert.equal(readWorkspaceId('?workspace=not-a-guid'), '');
    assert.equal(readWorkspaceId('?workspace='), '');
    assert.equal(readWorkspaceId(`?workspace=${first}&workspace=${second}`), '');
});

test('auth callback initializes page scope once from its local return target, not callback query data', () => {
    const context = vm.createContext({ exports: {}, URL, URLSearchParams,
        window: { location: { origin: 'https://photos.example', pathname: '/auth/callback', search: `?workspace=${second}` } },
    });
    const source = readFileSync(new URL('../app/lib/workspace-context.ts', import.meta.url), 'utf8');
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const workspace = context.exports;
    assert.equal(workspace.pageWorkspaceId, null, 'callback URL parameters cannot choose scope');
    assert.equal(workspace.initializePageWorkspaceFromReturnTarget(`/?workspace=${first}`), true);
    assert.equal(workspace.pageWorkspaceId, first);
    assert.equal(workspace.initializePageWorkspaceFromReturnTarget(`/?workspace=${second}`), false);
    assert.equal(workspace.pageWorkspaceId, first, 'later routes cannot retarget this page context');
});

test('initial scope binding requires complete sole membership and cannot retarget an initialized workspace', () => {
    const source = readFileSync(new URL('../app/lib/workspace-context.ts', import.meta.url), 'utf8');
    for (const search of ['', '?workspace=invalid', `?workspace=${second}`]) {
        const context = vm.createContext({ exports: {}, URL, URLSearchParams,
            window: { location: { origin: 'https://photos.example', pathname: '/', search } },
        });
        vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
        const scope = context.exports;
        assert.equal(scope.bindInitialWorkspace(memberships, true), false);
        assert.equal(scope.bindInitialWorkspace([], false), false);
        assert.equal(scope.bindInitialWorkspace([...memberships, { id: second, name: 'Location' }], false), false);
        assert.equal(scope.bindInitialWorkspace(memberships, false), search === '');
        assert.equal(scope.pageWorkspaceId, search === '' ? first : search.includes('invalid') ? '' : second);
        assert.equal(scope.bindInitialWorkspace([{ id: second, name: 'Location' }], false), false);
    }
});

test('requires membership confirmation before the selected workspace can open', () => {
    assert.deepEqual(workspaceSelection(memberships, false, first), { state: 'selected', workspace: memberships[0] });
    assert.deepEqual(workspaceSelection(memberships, true, first), { state: 'selected', workspace: memberships[0] });
    assert.deepEqual(workspaceSelection(memberships, false, second), { state: 'unavailable' });
    assert.deepEqual(workspaceSelection(memberships, false, ''), { state: 'unavailable' });
    assert.deepEqual(workspaceSelection(memberships, true, second), { state: 'incomplete' });
    assert.deepEqual(workspaceSelection([], false, second), { state: 'unavailable' });
});

test('opens the sole workspace only after complete discovery and leaves multiple memberships to an explicit choice', () => {
    assert.deepEqual(workspaceSelection(memberships, false, null), { state: 'selected', workspace: memberships[0] });
    assert.deepEqual(workspaceSelection(memberships, true, null), { state: 'choose' });
    assert.deepEqual(workspaceSelection([...memberships, { id: second, name: 'Location' }], false, null), { state: 'choose' });
    assert.deepEqual(workspaceSelection([], false, null), { state: 'empty' });
    assert.deepEqual(workspaceSelection([], true, null), { state: 'incomplete' });
});

function shell(selectedId, result, readAccess = async () => ({ role: 'Owner' })) {
    const replacements = [];
    const assignments = [];
    const url = new URL(withWorkspace('/captures/photo?tab=details#preview', selectedId), 'https://photos.example');
    if (selectedId === '') url.searchParams.set('workspace', 'invalid');
    const scopeContext = vm.createContext({ exports: {}, URL, URLSearchParams, window: { location: url } });
    vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/workspace-context.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, scopeContext);
    const scope = scopeContext.exports;
    const route = reactive({ path: url.pathname, fullPath: `${url.pathname}${url.search}${url.hash}`, params: {} });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: {} }] });
    router.afterEach((to, _from, failure) => { if (!failure) Object.assign(route, to); });
    const ready = router.push(route.fullPath);
    const replace = router.replace.bind(router);
    router.replace = target => { replacements.push(target); return replace(target); };
    const access = shallowRef();
    const signedIn = ref({});
    const watchers = [];
    let accessReads = 0;
    const context = vm.createContext({ exports: {}, ref, shallowRef, computed, AbortController,
        useRoute: () => route, useRouter: () => router,
        useAppConfig: () => ({ sftpgo: {} }), watch: (_source, callback) => { watchers.push(callback); return () => {}; }, onMounted() {}, onUnmounted() {},
        window: { location: { assign: path => assignments.push(path), replace() { throw new Error('Initial entry must not reload'); } } },
        require: name => name.endsWith('/session') ? { session: signedIn, onAccessLost: () => () => {} }
            : name.endsWith('/workspace-context') ? scope
            : name.endsWith('/workspace-access') ? { workspaceAccess: access, workspaceAccessVersion: ref(0), workspaceAccessError: ref(''), workspaceRevoked: ref(false),
                refreshWorkspaceAccess: async () => { accessReads++; access.value = await readAccess(); return !!access.value; }, discardWorkspacePrivateState() {} }
            : { api: { workspaces: { get: async () => typeof result === 'function' ? result() : result } }, cancellable: () => ({}), handleFailure() {} },
    });
    const source = parse(readFileSync(new URL('../app/app.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { replacements, assignments, router, scope, run: expression => vm.runInContext(expression, context), load: async () => { await ready; return vm.runInContext('loadMemberships()', context); }, accessReads: () => accessReads, revoke: () => { access.value = undefined; },
        signOut: () => { signedIn.value = null; watchers[0]([null]); },
        state: () => vm.runInContext('({ mount: canMountOperationalWorkspace.value, selection: activeWorkspaceSelection.value.state })', context) };
}

test('sole-workspace entry binds the loaded page and preserves the capture URL before private access starts', async () => {
    const app = shell(null, { items: memberships, incomplete: false });
    assert.equal(app.state().selection, 'loading', 'unconfirmed discovery must not show no access');
    await app.load();
    assert.deepEqual(app.replacements, [`/captures/photo?tab=details&workspace=${first}#preview`]);
    assert.equal(app.state().mount, true);
    assert.equal(app.state().selection, 'selected');
    assert.equal(app.accessReads(), 1, 'role checks start after the confirmed scope is bound');
    const selected = shell(first, { items: memberships, incomplete: false });
    await selected.load();
    assert.equal(selected.state().mount, true);
    assert.deepEqual(selected.replacements, []);
});

test('actual workspace switches and confirmed joins retain the departure guard and reload boundary', async () => {
    const app = shell(first, { items: [...memberships, { id: second, name: 'Location' }], incomplete: false });
    await app.load();
    app.run('workspace.value = { requestDeparture: () => false }');
    app.run(`switchWorkspace('${second}')`);
    await app.run(`joinedWorkspace('${second}')`);
    assert.deepEqual(app.assignments, [], 'cancelling preserves the open workspace and its drafts');
    app.run('workspace.value = { requestDeparture: () => true }');
    await app.run(`joinedWorkspace('${second}')`);
    assert.deepEqual(app.assignments, [`/?workspace=${second}`], 'confirmed membership still crosses a document boundary to isolate private state');
});

test('initial workspace binding updates Vue Router before access and survives ordinary navigation and history travel', async () => {
    const app = shell(null, { items: memberships, incomplete: false }, async () => {
        assert.equal(app.router.currentRoute.value.query.workspace, first, 'router scope must be current before private access');
        return { role: 'Owner' };
    });
    await app.load();
    const initial = app.router.currentRoute.value.fullPath;
    assert.equal(app.state().mount, true);
    await app.router.push(withWorkspace('/', first));
    const travel = direction => new Promise(resolve => {
        const stop = app.router.afterEach(() => { stop(); resolve(); });
        app.router.go(direction);
    });
    await travel(-1);
    assert.equal(app.router.currentRoute.value.fullPath, initial);
    assert.equal(app.router.currentRoute.value.query.workspace, first);
    await travel(1);
    assert.equal(app.router.currentRoute.value.query.workspace, first);
    assert.equal(app.scope.pageWorkspaceId, first);
    assert.deepEqual(app.assignments, [], 'within-workspace navigation and history do not restart the document');
});

test('session loss while initial URL replacement is pending cannot mount private content or read access', async () => {
    const app = shell(null, { items: memberships, incomplete: false });
    let finish;
    app.router.beforeEach(to => to.query.workspace ? new Promise(resolve => { finish = resolve; }) : undefined);
    const pending = app.load();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(typeof finish, 'function');
    assert.equal(app.accessReads(), 0);
    app.signOut(); finish();
    await pending;
    assert.equal(app.accessReads(), 0);
    assert.equal(app.state().mount, false);
});

test('cancelled initial navigation stays retryable and synchronizes Router before opening private content', async () => {
    const app = shell(null, { items: memberships, incomplete: false });
    let cancel = true;
    app.router.beforeEach(to => to.query.workspace && cancel ? false : undefined);
    await app.load();
    assert.equal(app.state().mount, false);
    assert.equal(app.accessReads(), 0);
    cancel = false;
    await app.load();
    assert.equal(app.router.currentRoute.value.query.workspace, first);
    assert.equal(app.state().mount, true);
    assert.equal(app.accessReads(), 1);
});

test('confirmed Join opens the authoritative workspace even when membership discovery is unavailable', async () => {
    const app = shell(null, () => { throw new Error('Membership discovery unavailable'); });
    await app.load();
    await app.run(`joinedWorkspace('${second}')`);
    assert.deepEqual(app.assignments, [`/?workspace=${second}`]);
    assert.equal(app.accessReads(), 0, 'the next document must independently reauthorize workspace access');
});

test('discovery never redirects explicit unavailable selections, incomplete membership lists or accounts without access', async () => {
    for (const [selectedId, result, state] of [
        [second, { items: memberships, incomplete: false }, 'unavailable'],
        ['', { items: memberships, incomplete: false }, 'unavailable'],
        [null, { items: memberships, incomplete: true }, 'choose'],
        [null, { items: [], incomplete: false }, 'empty'],
    ]) {
        const app = shell(selectedId, result);
        await app.load();
        assert.deepEqual(app.replacements, []);
        assert.equal(app.state().mount, false);
        assert.equal(app.state().selection, state);
        assert.equal(app.accessReads(), 0, 'unconfirmed membership cannot start a scoped role check');
    }
});

test('confirmed page selection mounts only after role access resolves and closes on permission loss without fallback', async () => {
    let finish;
    const app = shell(first, { items: memberships, incomplete: false }, () => new Promise(resolve => { finish = resolve; }));
    const pending = app.load();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(app.accessReads(), 1);
    assert.equal(app.state().selection, 'selected');
    assert.equal(app.state().mount, false, 'membership discovery alone cannot expose operational controls');
    finish({ role: 'Viewer' });
    await pending;
    assert.equal(app.state().mount, true);
    app.revoke();
    assert.equal(app.state().mount, false);
    assert.equal(app.state().selection, 'selected');
    assert.deepEqual(app.replacements, [], 'permission loss cannot choose another workspace');
});

test('keeps workspace context on gallery routes and other URL state', () => {
    assert.equal(withWorkspace('/captures/a%2Fb?tab=details#photo', first), `/captures/a%2Fb?tab=details&workspace=${first}#photo`);
    assert.equal(withWorkspace(`/?workspace=${second}&tab=storage`, first), `/?workspace=${first}&tab=storage`);
    assert.equal(withWorkspace('/captures/a', null), '/captures/a');
});

test('scopes management and preview requests but leaves membership discovery unselected', () => {
    const headers = workspaceRequestHeaders('/management/sources', { Authorization: 'Bearer token' }, first);
    assert.equal(headers.get('Authorization'), 'Bearer token');
    assert.equal(headers.get('X-ShutterSpool-Workspace'), first);
    assert.equal(workspaceRequestHeaders('/media/previews/id/grid', undefined, first).get('X-ShutterSpool-Workspace'), first);
    assert.equal(workspaceRequestHeaders('/workspaces', undefined, first).has('X-ShutterSpool-Workspace'), false);
    assert.equal(workspaceRequestHeaders('/workspace-offers', undefined, first).has('X-ShutterSpool-Workspace'), false);
    assert.equal(workspaceRequestHeaders(`/workspace-offers/${second}/offer/accept`, undefined, first).has('X-ShutterSpool-Workspace'), false, 'own offers use their explicit workspace path rather than the open workspace header');
    assert.equal(workspaceRequestHeaders('/management/sources', undefined, null).has('X-ShutterSpool-Workspace'), false);
});
