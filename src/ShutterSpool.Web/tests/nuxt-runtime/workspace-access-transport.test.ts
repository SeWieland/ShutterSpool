import assert from 'node:assert/strict';
import { useNuxtApp } from '#app';
import { flushPromises } from '@vue/test-utils';
import type { User } from 'oidc-client-ts';
import { afterEach, beforeEach, test, vi } from 'vitest';

let nuxt: ReturnType<typeof useNuxtApp>;
const workspaceId = '2f2ed010-2a51-468a-a38a-a2372fba8211';
const member = '00000000-0000-0000-0000-000000000003';
const sessionUser = { access_token: 'synthetic', expires_in: 300, expired: false } as User;
let originalUrl: string;

beforeEach(() => {
    nuxt = useNuxtApp();
    originalUrl = location.pathname + location.search + location.hash;
    history.replaceState(history.state, '', '/');
    vi.resetModules();
});

afterEach(() => {
    history.replaceState(history.state, '', originalUrl);
    vi.useRealTimers();
    vi.unstubAllGlobals();
});

function access(role: 'Owner' | 'Curator' | 'Viewer') {
    return Response.json({
        membershipId: member, role, revision: 4,
        capabilities: {
            canCurate: role !== 'Viewer', canAdminister: role === 'Owner', canManageMembers: role === 'Owner', canManageOwners: role === 'Owner',
        },
        viewerPolicy: { includeUnreleased: false, revision: 2 },
    });
}

async function client() {
    const auth = await import('../../app/lib/session');
    auth.session.value = sessionUser;
    const workspace = await import('../../app/lib/workspace-access');
    const scope = await import('../../app/lib/workspace-context');
    const transport = await import('../../app/lib/api');
    return { auth, workspace, scope, transport };
}

test('API transport distinguishes denied commands, revoked membership and expired authentication from response machine codes', async () => {
    const responses = [
        access('Owner'),
        Response.json({ errorCode: 'permission-denied' }, { status: 403 }),
        Response.json({ errorCode: 'membership-revoked' }, { status: 403 }),
        Response.json({}, { status: 401 }),
    ];
    const fetch = vi.fn(async () => responses.shift()!);
    vi.stubGlobal('fetch', fetch);
    const { auth, workspace, transport } = await client();
    assert.equal(await workspace.refreshWorkspaceAccess(), true);
    const generation = auth.accessGeneration();
    await assert.rejects(transport.api.management.captures.byId(member).curation.put({}));
    assert.equal(auth.accessGeneration(), generation);
    assert.equal(workspace.workspaceAccess.value?.role, 'Owner');
    assert.equal(auth.session.value, sessionUser);

    await assert.rejects(transport.api.management.access.get());
    assert.equal(workspace.workspaceAccess.value, undefined);
    assert.equal(workspace.workspaceRevoked.value, true);
    assert.equal(auth.session.value, sessionUser);

    await assert.rejects(transport.api.management.access.get());
    assert.equal(auth.session.value, null);
    assert.equal(workspace.workspaceRevoked.value, false);
    assert.equal(fetch.mock.calls.length, 4, 'denied command is not retried');
});

test('management and queued previews use the scope at dispatch while workspace discovery and offers stay unscoped', async () => {
    const requests: Array<{ path: string; workspace: string | null; bearer: string | null }> = [];
    const held = Array.from({ length: 4 }, () => Promise.withResolvers<Response>());
    vi.stubGlobal('fetch', vi.fn((url: string, { headers }: RequestInit) => {
        const path = new URL(url, location.origin).pathname;
        const request = new Headers(headers);
        requests.push({ path, workspace: request.get('X-ShutterSpool-Workspace'), bearer: request.get('Authorization') });
        if (path.startsWith('/media/previews/hold-')) return held[Number(path.split('-').at(-1)?.split('/')[0])]!.promise;
        if (path === '/management/access') return Promise.resolve(access('Owner'));
        if (path.startsWith('/media/previews/')) return Promise.resolve(new Response('synthetic'));
        return Promise.resolve(Response.json({ items: [], incomplete: false }));
    }));
    const { scope, transport } = await client();
    const signal = new AbortController().signal;
    const fillers = held.map((_, index) => transport.derivative(`hold-${index}`, 'thumbnail', signal));
    const preview = transport.derivative('capture', 'thumbnail', signal);
    await flushPromises();
    assert.equal(requests.length, 4, 'the fifth derivative waits for a slot');
    assert.equal(scope.bindInitialWorkspace([{ id: workspaceId, name: 'Synthetic studio' }], false), true);
    try {
        await transport.api.management.access.get();
        await transport.api.workspaces.get();
        await transport.api.workspaceOffers.get();
    } finally {
        held.forEach(slot => slot.resolve(new Response('synthetic')));
        await Promise.all([...fillers, preview]);
    }
    assert.deepEqual(requests.slice(4), [
        { path: '/management/access', workspace: workspaceId, bearer: 'Bearer synthetic' },
        { path: '/workspaces', workspace: null, bearer: 'Bearer synthetic' },
        { path: '/workspace-offers', workspace: null, bearer: 'Bearer synthetic' },
        { path: '/media/previews/capture/grid', workspace: workspaceId, bearer: 'Bearer synthetic' },
    ]);
});

function transportReads() {
    const requests: ReturnType<typeof Promise.withResolvers<Response>>[] = [];
    vi.stubGlobal('fetch', vi.fn(() => {
        const request = Promise.withResolvers<Response>();
        requests.push(request);
        return request.promise;
    }));
    return requests;
}

test('access reads coalesce, revision changes clear private state once and keep the shared login', async () => {
    const requests = transportReads();
    const { auth, workspace } = await client();
    const cleared = vi.fn();
    const stop = auth.onAccessLost(cleared);
    try {
        const first = workspace.refreshWorkspaceAccess();
        const same = workspace.refreshWorkspaceAccess();
        await flushPromises();
        assert.equal(requests.length, 1);
        requests[0]!.resolve(access('Curator'));
        assert.equal(await first, true);
        assert.equal(await same, true);
        assert.equal(cleared.mock.calls.length, 0);
        const changed = workspace.refreshWorkspaceAccess();
        await flushPromises();
        requests[1]!.resolve(access('Viewer'));
        assert.equal(await changed, true);
        assert.equal(cleared.mock.calls.length, 1);
        assert.equal(workspace.mayCurate.value, false);
        assert.equal(workspace.workspaceAccessVersion.value, 1);
        assert.equal(auth.session.value, sessionUser);
    } finally { stop(); }
});

test('revocation fences an older access response and a timed-out authority read removes cached permission', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    const requests = transportReads();
    const { auth, workspace } = await client();
    const first = workspace.refreshWorkspaceAccess();
    await flushPromises();
    requests[0]!.resolve(access('Owner'));
    assert.equal(await first, true);
    const pending = workspace.refreshWorkspaceAccess();
    await flushPromises();
    nuxt.runWithContext(() => workspace.closeWorkspaceAccess(true));
    requests[1]!.resolve(access('Owner'));
    assert.equal(await pending, false);
    assert.equal(workspace.workspaceAccess.value, undefined);
    assert.equal(auth.session.value, sessionUser);

    const restored = workspace.refreshWorkspaceAccess();
    await flushPromises();
    requests[2]!.resolve(access('Owner'));
    assert.equal(await restored, true);
    assert.equal(workspace.workspaceAccess.value?.role, 'Owner');
    const timeout = workspace.refreshWorkspaceAccess();
    await flushPromises();
    await vi.advanceTimersByTimeAsync(10_000);
    assert.equal(await timeout, false);
    assert.equal(workspace.workspaceAccess.value, undefined);
    assert.match(workspace.workspaceAccessError.value, /could not be checked/);
    requests[3]!.resolve(access('Owner'));
    await flushPromises();
    assert.equal(workspace.workspaceAccess.value, undefined, 'a late timed-out read cannot reopen the workspace');
});
