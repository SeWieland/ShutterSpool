import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { readWorkspaceId, workspaceRequestHeaders, workspaceSelection, withWorkspace } from '../app/lib/workspace-context.ts';

const first = '2f2ed010-2a51-468a-a38a-a2372fba8211';
const second = '6254ea93-b93b-4707-9b3d-b9b8e226b5d1';
const memberships = [{ id: first, name: 'Studio' }];

test('reads one valid workspace ID from the current page URL', () => {
    assert.equal(readWorkspaceId(`?workspace=${first}`), first);
    assert.equal(readWorkspaceId(`?workspace=${first.toUpperCase()}`), first);
    assert.equal(readWorkspaceId('?workspace=not-a-guid'), null);
    assert.equal(readWorkspaceId(`?workspace=${first}&workspace=${second}`), null);
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
    assert.deepEqual(workspaceSelection(memberships, false, true, first), { state: 'selected', workspace: memberships[0] });
    assert.deepEqual(workspaceSelection(memberships, false, true, second), { state: 'unavailable' });
    assert.deepEqual(workspaceSelection(memberships, true, true, second), { state: 'incomplete' });
    assert.deepEqual(workspaceSelection([], false, false, second), { state: 'unavailable' });
});

test('distinguishes no memberships from an incomplete membership query', () => {
    assert.deepEqual(workspaceSelection([], false, true, null), { state: 'empty' });
    assert.deepEqual(workspaceSelection([], true, true, null), { state: 'incomplete' });
    assert.deepEqual(workspaceSelection(memberships, false, true, null), { state: 'choose' });
    assert.deepEqual(workspaceSelection([], false, false, null), { state: 'legacy' });
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
