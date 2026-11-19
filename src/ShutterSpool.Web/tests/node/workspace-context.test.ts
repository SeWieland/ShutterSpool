import assert from 'node:assert/strict';
import { afterEach, test, vi } from 'vitest';
import { readWorkspaceId, workspaceRequestHeaders, workspaceSelection, withWorkspace } from '../../app/lib/workspace-context';

afterEach(() => { vi.unstubAllGlobals(); });

async function scopeAt(path: string) {
    vi.resetModules();
    vi.stubGlobal('window', { location: new URL(path, 'https://photos.example') });
    return import('../../app/lib/workspace-context');
}

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

test('auth callback initializes page scope once from its local return target, not callback query data', async () => {
    const workspace = await scopeAt(`/auth/callback?workspace=${second}`);
    assert.equal(workspace.getPageWorkspaceId(), null, 'callback URL parameters cannot choose scope');
    assert.equal(workspace.initializePageWorkspaceFromReturnTarget(`/?workspace=${first}`), true);
    assert.equal(workspace.getPageWorkspaceId(), first);
    assert.equal(workspace.initializePageWorkspaceFromReturnTarget(`/?workspace=${second}`), false);
    assert.equal(workspace.getPageWorkspaceId(), first, 'later routes cannot retarget this page context');
});

test('initial scope binding requires complete sole membership and cannot retarget an initialized workspace', async () => {
    for (const [search, expected] of [['', first], ['?workspace=invalid', ''], [`?workspace=${second}`, second]]) {
        const workspace = await scopeAt(`/${search}`);
        const other = { id: second, name: 'Location' };
        assert.equal(workspace.bindInitialWorkspace(memberships, true), false);
        assert.equal(workspace.bindInitialWorkspace([], false), false);
        assert.equal(workspace.bindInitialWorkspace([...memberships, other], false), false);
        assert.equal(workspace.bindInitialWorkspace(memberships, false), search === '');
        assert.equal(workspace.getPageWorkspaceId(), expected);
        assert.equal(workspace.bindInitialWorkspace([other], false), false);
        assert.equal(workspace.getPageWorkspaceId(), expected);
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
