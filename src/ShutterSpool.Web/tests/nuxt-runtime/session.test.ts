import assert from 'node:assert/strict';
import { useNuxtApp } from '#app';
import { mountSuspended } from '@nuxt/test-utils/runtime';
import { flushPromises } from '@vue/test-utils';
import type { User } from 'oidc-client-ts';
import { afterEach, beforeEach, test, vi } from 'vitest';

const { oidc, userManager } = vi.hoisted(() => {
    const oidc = {
        signinSilent: vi.fn<() => Promise<User | null>>(),
        signinRedirect: vi.fn(),
        signinRedirectCallback: vi.fn(),
        removeUser: vi.fn(),
    };
    return { oidc, userManager: vi.fn() };
});
vi.mock('oidc-client-ts', () => ({
    UserManager: class {
        constructor(readonly settings: unknown) { userManager(settings); }
        events = { addAccessTokenExpiring() {}, addAccessTokenExpired() {}, addUserSignedOut() {} };
        clearStaleState = async () => {};
        signinSilent = oidc.signinSilent;
        signinRedirect = oidc.signinRedirect;
        signinRedirectCallback = oidc.signinRedirectCallback;
        removeUser = oidc.removeUser;
        stopSilentRenew() {}
    },
    WebStorageStateStore: class {
        constructor(readonly options: { store: Storage }) {}
        getAllKeys = async () => [];
        remove = async () => {};
    },
    InMemoryWebStorage: class {},
}));

const loadSession = () => import('../../app/lib/session');
let auth: Awaited<ReturnType<typeof loadSession>>;
let nuxt: ReturnType<typeof useNuxtApp>;
let originalUrl: string;
const unsubscribers: Array<() => void> = [];

beforeEach(async () => {
    nuxt = useNuxtApp();
    vi.resetModules();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    for (const mock of Object.values(oidc)) mock.mockReset();
    userManager.mockReset();
    originalUrl = location.pathname + location.search + location.hash;
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
        assert.equal(url, '/auth/config');
        return Response.json({ authority: 'https://issuer.example.test', clientId: 'spa' });
    }));
    auth = await loadSession();
});

afterEach(() => {
    for (const stop of unsubscribers.splice(0)) stop();
    history.replaceState(history.state, '', originalUrl);
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
});

function user(token: string, expiresIn: number, extras: Partial<User> = {}): User {
    return { access_token: token, expires_in: expiresIn, expired: false, profile: { sub: 'synthetic-photographer' }, ...extras } as User;
}

function renewLater() {
    const pending = Promise.withResolvers<User>();
    oidc.signinSilent.mockReturnValueOnce(pending.promise);
    auth.session.value = user('old', 10);
    return pending;
}

test('simultaneous expiring-token reads share renewal and keep session generation', async () => {
    const pending = renewLater();
    const generation = auth.accessGeneration();
    const tokens = [auth.token(), auth.token(), auth.token()];
    await flushPromises();
    assert.equal(oidc.signinSilent.mock.calls.length, 1);
    pending.resolve(user('new', 300));
    assert.deepEqual(await Promise.all(tokens), ['new', 'new', 'new']);
    assert.equal(auth.accessGeneration(), generation);
});

test('revocation during renewal cannot restore private access', async () => {
    const pending = renewLater();
    const token = auth.token();
    const rejected = assert.rejects(token, /Access ended/);
    await flushPromises();
    assert.equal(oidc.signinSilent.mock.calls.length, 1);
    nuxt.runWithContext(() => auth.clearAccess('Grant revoked'));
    pending.resolve(user('new', 300));
    await rejected;
    assert.equal(auth.session.value, null);
    assert.equal(auth.accessMessage.value, 'Grant revoked');
    assert.equal(oidc.removeUser.mock.calls.length, 2, 'also clear the user the late renewal stored');
});

test('workspace permission loss clears private consumers while retaining the shared issuer login', async () => {
    await auth.getManager();
    const shared = user('shared-login', 300);
    auth.session.value = shared;
    const cleared = vi.fn();
    unsubscribers.push(auth.onAccessLost(cleared));
    const generation = auth.accessGeneration();
    nuxt.runWithContext(() => auth.clearPrivateAccess());
    assert.equal(auth.session.value, shared);
    assert.equal(auth.accessGeneration(), generation + 1);
    assert.equal(cleared.mock.calls.length, 1);
    assert.equal(oidc.removeUser.mock.calls.length, 0);
});

test('workspace cache invalidation during token renewal does not interrupt the shared OIDC session', async () => {
    const pending = renewLater();
    const token = auth.token();
    await flushPromises();
    nuxt.runWithContext(() => auth.clearPrivateAccess());
    pending.resolve(user('renewed', 300));
    assert.equal(await token, 'renewed');
    assert.equal(auth.session.value?.access_token, 'renewed');
    assert.equal(oidc.removeUser.mock.calls.length, 0);
});

test('reload uses one silent attempt then a credential-free top-level fallback', async () => {
    oidc.signinSilent.mockRejectedValueOnce(new Error('Iframe blocked'));
    await auth.restoreSession('/captures/shot');
    assert.equal(oidc.signinSilent.mock.calls.length, 1);
    assert.deepEqual(oidc.signinRedirect.mock.calls, [[{ prompt: 'none', state: { returnTo: '/captures/shot' } }]]);
    const settings = userManager.mock.lastCall?.[0];
    assert.equal(settings.automaticSilentRenew, false);
    assert.equal(settings.stateStore.options.store, sessionStorage);
    assert.notEqual(settings.userStore.options.store, settings.stateStore.options.store);
});

test('return targets cannot leave the origin or replay an auth callback', () => {
    for (const path of ['//evil.example/', 'https://evil.example', '/auth/callback?code=synthetic', '/\\evil.example'])
        assert.equal(auth.localReturnTarget(path), '/');
    assert.equal(auth.localReturnTarget('/captures/shot?view=detail'), '/captures/shot?view=detail');
    const workspace = '/?workspace=00000000-0000-0000-0000-000000000001';
    assert.equal(auth.localReturnTarget(workspace), workspace);
});

test('top-level callback keeps its token and initializes workspace scope before SPA return navigation', async () => {
    vi.useRealTimers();
    const realTimeout = globalThis.setTimeout;
    vi.spyOn(globalThis, 'setTimeout').mockImplementation((callback, timeout, ...args) => {
        if (timeout === 301_000) return 0 as unknown as ReturnType<typeof setTimeout>;
        return realTimeout(callback, timeout, ...args);
    });
    assert.equal(window.parent, window);
    const router = nuxt.$router;
    const workspaceId = '2f2ed010-2a51-468a-a38a-a2372fba8211';
    const callbackUrl = '/auth/callback?code=synthetic&workspace=6254ea93-b93b-4707-9b3d-b9b8e226b5d1';
    history.replaceState(history.state, '', callbackUrl);
    // The component and its assertion must share the same browser-initialized module.
    vi.resetModules();
    auth = await loadSession();
    const { getPageWorkspaceId } = await import('../../app/lib/workspace-context');
    const { default: Callback } = await import('../../app/pages/auth/callback.vue');
    assert.equal(getPageWorkspaceId(), null);
    const target = `/?workspace=${workspaceId}`;
    const callbackUser = user('callback-token', 300, { state: { returnTo: target } });
    const response = Promise.withResolvers<User>();
    oidc.signinRedirectCallback.mockReturnValueOnce(response.promise);
    const scopedNavigations: Array<{ scope: string | null; token: string | undefined }> = [];
    const removeGuard = router.beforeEach(to => {
        if (to.fullPath === target)
            scopedNavigations.push({ scope: getPageWorkspaceId(), token: auth.session.value?.access_token });
    });
    const replace = vi.spyOn(router, 'replace');
    const reload = vi.spyOn(location, 'replace');
    try {
        const mounted = await mountSuspended(Callback, { route: callbackUrl });
        try {
            await flushPromises();
            assert.equal(router.currentRoute.value.path, '/auth/callback');
            assert.equal(oidc.signinRedirectCallback.mock.calls.length, 1);
            response.resolve(callbackUser);
            await vi.waitFor(() => assert.equal(router.currentRoute.value.fullPath, target), { timeout: 5000 });
            assert.deepEqual(scopedNavigations, [{ scope: workspaceId, token: 'callback-token' }]);
            assert.equal(auth.session.value, callbackUser);
            assert.equal(getPageWorkspaceId(), workspaceId);
            assert.equal(oidc.signinSilent.mock.calls.length, 0);
            assert.equal(oidc.signinRedirect.mock.calls.length, 0);
            assert.deepEqual(replace.mock.calls.filter(([destination]) => destination === target), [[target]]);
            assert.equal(reload.mock.calls.length, 0);
        } finally { mounted.unmount(); }
    } finally { removeGuard(); }
});

test('missing or expired silent renewal requires login and clears the still-valid old session', async () => {
    for (const result of [null, user('expired', 0, { expired: true })]) {
        oidc.signinSilent.mockResolvedValueOnce(result);
        auth.session.value = user('still-valid', 10);
        const cleared = vi.fn();
        const stop = auth.onAccessLost(cleared);
        unsubscribers.push(stop);
        await assert.rejects(auth.token(), error => {
            assert.ok(Error.isError(error));
            assert.equal((error as Error & { error?: string }).error, 'login_required');
            return true;
        });
        assert.equal(auth.session.value, null);
        assert.equal(cleared.mock.calls.length, 1);
        stop();
    }
    assert.equal(oidc.removeUser.mock.calls.length, 2);
});

test('offline renewal retains the active session; issuer revocation ends it', async () => {
    oidc.signinSilent.mockRejectedValueOnce(new TypeError('Network unavailable'));
    auth.session.value = user('still-valid', 10);
    assert.equal(await auth.token(), 'still-valid');
    assert.ok(auth.session.value);
    assert.equal(oidc.removeUser.mock.calls.length, 0);
    oidc.signinSilent.mockRejectedValueOnce({ error: 'invalid_grant' });
    await assert.rejects(auth.token(), { error: 'invalid_grant' });
    assert.equal(auth.session.value, null);
    assert.equal(oidc.removeUser.mock.calls.length, 1);
});
