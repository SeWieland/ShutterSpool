import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { parse } from '@vue/compiler-sfc';
import { computed, ref, shallowRef } from 'vue';
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
    const access = shallowRef();
    let accessReads = 0;
    const context = vm.createContext({ exports: {}, ref, shallowRef, computed, AbortController,
        useRoute: () => ({ path: '/captures/photo', fullPath: '/captures/photo?tab=details#preview' }),
        useAppConfig: () => ({ sftpgo: {} }), watch: () => () => {}, onMounted() {}, onUnmounted() {},
        window: { location: { replace: path => replacements.push(path) } },
        require: name => name.endsWith('/session') ? { onAccessLost: () => () => {} }
            : name.endsWith('/workspace-context') ? { pageWorkspaceId: selectedId, workspaceSelection, withWorkspace }
            : name.endsWith('/workspace-access') ? { workspaceAccess: access, workspaceAccessVersion: ref(0), workspaceAccessError: ref(''), workspaceRevoked: ref(false),
                refreshWorkspaceAccess: async () => { accessReads++; access.value = await readAccess(); return !!access.value; }, discardWorkspacePrivateState() {} }
            : { api: { workspaces: { get: async () => result } }, cancellable: () => ({}), handleFailure() {} },
    });
    const source = parse(readFileSync(new URL('../app/app.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { replacements, load: () => vm.runInContext('loadMemberships()', context), accessReads: () => accessReads, revoke: () => { access.value = undefined; },
        state: () => vm.runInContext('({ mount: canMountOperationalWorkspace.value, selection: activeWorkspaceSelection.value.state })', context) };
}

test('sole-workspace entry reloads the requested capture route before private state can mount', async () => {
    const app = shell(null, { items: memberships, incomplete: false });
    assert.equal(app.state().selection, 'loading', 'unconfirmed discovery must not show no access');
    await app.load();
    assert.deepEqual(app.replacements, [`/captures/photo?tab=details&workspace=${first}#preview`]);
    assert.equal(app.state().mount, false);
    assert.equal(app.state().selection, 'loading');
    assert.equal(app.accessReads(), 0, 'role checks cannot start before the immutable selected page loads');
    const selected = shell(first, { items: memberships, incomplete: false });
    await selected.load();
    assert.equal(selected.state().mount, true);
    assert.deepEqual(selected.replacements, []);
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
    assert.equal(workspaceRequestHeaders('/management/sources', undefined, null).has('X-ShutterSpool-Workspace'), false);
});
