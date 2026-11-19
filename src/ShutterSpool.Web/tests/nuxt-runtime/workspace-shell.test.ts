import assert from 'node:assert/strict';
import { useNuxtApp } from '#app';
import { mountSuspended } from '@nuxt/test-utils/runtime';
import { enableAutoUnmount, flushPromises } from '@vue/test-utils';
import { afterEach, beforeEach, test, vi } from 'vitest';
import { nextTick } from 'vue';

const fixtures = vi.hoisted(() => ({
    discovery: vi.fn(), restore: vi.fn(), signOut: vi.fn(), refresh: vi.fn(), depart: vi.fn(),
}));
vi.mock('../../app/lib/session', async () => {
    const { shallowRef, ref } = await import('vue');
    return {
        session: shallowRef(), accessMessage: ref(''), restoring: ref(false),
        onAccessLost: () => () => {}, signIn: vi.fn(), signOut: fixtures.signOut, restoreSession: fixtures.restore,
    };
});
vi.mock('../../app/lib/workspace-access', async () => {
    const { shallowRef, ref } = await import('vue');
    return {
        workspaceAccess: shallowRef(), workspaceAccessVersion: ref(0), workspaceAccessError: ref(''), workspaceRevoked: ref(false),
        refreshWorkspaceAccess: fixtures.refresh, discardWorkspacePrivateState: vi.fn(),
    };
});
vi.mock('../../app/lib/api', () => ({
    api: { workspaces: { get: fixtures.discovery } }, cancellable: (signal: AbortSignal) => ({ signal }), handleFailure: vi.fn(),
}));
vi.mock('../../app/components/OperationalWorkspace.vue', async () => {
    const { defineComponent, h, ref } = await import('vue');
    return { default: defineComponent({
        props: { captureId: String, inactive: Boolean },
        setup(props, { expose }) {
            const draft = ref('');
            expose({ requestDeparture: fixtures.depart });
            return () => h('div', { 'data-workspace': props.captureId ?? 'root', 'data-inactive': String(props.inactive) }, [
                h('input', {
                    'aria-label': 'Bulk tag draft',
                    value: draft.value,
                    onInput: (event: Event) => { draft.value = (event.target as HTMLInputElement).value; },
                }),
            ]);
        },
    }) };
});
vi.mock('../../app/components/WorkspaceInvitations.vue', async () => {
    const { defineComponent, h } = await import('vue');
    return { default: defineComponent({
        emits: ['joined'],
        setup(_props, { emit }) {
            return () => h('button', { onClick: () => emit('joined', second) }, 'Confirm synthetic Join');
        },
    }) };
});

const first = '2f2ed010-2a51-468a-a38a-a2372fba8211';
const second = '6254ea93-b93b-4707-9b3d-b9b8e226b5d1';
const memberships = [{ id: first, name: 'Studio' }];
const discovery = { items: memberships, incomplete: false };
const capture = '/captures/photo?tab=details#preview';
const scopedCapture = `/captures/photo?tab=details&workspace=${first}#preview`;
let router: ReturnType<typeof useNuxtApp>['$router'];
const restore: (() => void | Promise<unknown>)[] = [];
let unmountShells!: () => void;
// Track the wrapper even if Nuxt's suspended mount rejects before returning it.
enableAutoUnmount(unmount => { unmountShells = unmount; });

beforeEach(() => {
    router = useNuxtApp().$router;
    const anchor = document.createElement('div');
    anchor.id = 'preview';
    document.body.append(anchor);
    restore.push(() => anchor.remove());
});
afterEach(async () => {
    try {
        for (const dispose of restore.splice(0).reverse()) await dispose();
    } finally {
        vi.restoreAllMocks();
        for (const mock of Object.values(fixtures)) mock.mockReset();
    }
});

async function shell(
    selected: string | null,
    read = async () => discovery,
    readAccess: () => Promise<{ role: string }> = async () => ({ role: 'Owner' }),
) {
    const startingPath = router.currentRoute.value.fullPath;
    const cleanup: (() => void)[] = [];
    const dispose = async () => {
        try { unmountShells(); }
        finally {
            for (const reset of cleanup.splice(0).reverse()) reset();
            await router.push(startingPath);
        }
    };
    restore.push(dispose);
    const path = selected === null ? capture : `/captures/photo?tab=details&workspace=${selected || 'invalid'}#preview`;
    await router.push(path);
    history.replaceState(history.state, '', path);
    vi.resetModules();
    const auth = await import('../../app/lib/session');
    const access = await import('../../app/lib/workspace-access');
    const scope = await import('../../app/lib/workspace-context');
    function resetAccess() {
        auth.session.value = null;
        access.workspaceAccess.value = undefined;
        access.workspaceAccessError.value = '';
        access.workspaceRevoked.value = false;
    }
    resetAccess();
    cleanup.push(resetAccess);
    const { default: App } = await import('../../app/app.vue');
    fixtures.discovery.mockReset().mockImplementation(read);
    fixtures.restore.mockResolvedValue(undefined);
    fixtures.depart.mockReset().mockReturnValue(true);
    fixtures.refresh.mockReset().mockImplementation(async () => {
        access.workspaceAccess.value = await readAccess() as typeof access.workspaceAccess.value;
        return true;
    });

    const assign = vi.spyOn(location, 'assign').mockImplementation(() => {});
    cleanup.push(() => assign.mockRestore());
    const reload = vi.spyOn(location, 'replace').mockImplementation(() => {});
    cleanup.push(() => reload.mockRestore());
    const popover = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'hidePopover');
    Object.defineProperty(HTMLElement.prototype, 'hidePopover', { configurable: true, value() {} });
    cleanup.push(() => {
        if (popover) Object.defineProperty(HTMLElement.prototype, 'hidePopover', popover);
        else Reflect.deleteProperty(HTMLElement.prototype, 'hidePopover');
    });
    const wrapper = await mountSuspended(App, {
        route: path,
        global: { stubs: {
            UApp: { template: '<div><slot /></div>' }, UIcon: true, ApertureBackground: true,
        } },
    });
    const replace = vi.spyOn(router, 'replace');
    cleanup.push(() => replace.mockRestore());
    function signIn() { auth.session.value = { profile: { sub: 'synthetic' } } as NonNullable<typeof auth.session.value>; }
    function button(label: string) {
        const result = wrapper.findAll('button').find(item => item.text() === label);
        assert.ok(result, `Expected the ${label} button`);
        return result;
    }
    return {
        wrapper, scope, assign, reload, replace, button, signIn,
        privateContent: () => wrapper.find('[data-workspace]').exists(),
        load: async () => {
            signIn();
            await vi.waitFor(() => assert.ok(fixtures.discovery.mock.calls.length > 0), { timeout: 5000 });
            await flushPromises();
        },
        opened: () => vi.waitFor(() => assert.equal(wrapper.find('[data-workspace]').exists(), true), { timeout: 5000 }),
        signOut: () => { auth.session.value = null; },
        revoke: () => { access.workspaceAccess.value = undefined; access.workspaceAccessError.value = 'Access ended'; },
        close: async () => { restore.splice(restore.indexOf(dispose), 1); await dispose(); },
    };
}

test('sole-workspace entry binds the loaded page and preserves the capture URL before private access starts', async () => {
    const pending = Promise.withResolvers<typeof discovery>();
    const app = await shell(null, () => pending.promise, async () => {
        assert.equal(router.currentRoute.value.fullPath, scopedCapture);
        return { role: 'Owner' };
    });
    app.signIn();
    await nextTick();
    assert.match(app.wrapper.text(), /Opening ShutterSpool/);
    assert.equal(app.privateContent(), false);
    assert.equal(fixtures.refresh.mock.calls.length, 0);
    pending.resolve(discovery);
    await app.opened();
    assert.deepEqual(app.replace.mock.calls, [[scopedCapture]]);
    assert.equal(app.wrapper.get('[data-workspace]').attributes('data-workspace'), 'photo');
    assert.equal(fixtures.refresh.mock.calls.length, 1);
    assert.equal(app.assign.mock.calls.length + app.reload.mock.calls.length, 0);
    await app.close();

    const selected = await shell(first);
    await selected.load();
    await selected.opened();
    assert.equal(selected.replace.mock.calls.length, 0);
});

test('actual workspace switches and confirmed joins retain the departure guard and reload boundary', async () => {
    const app = await shell(first, async () => ({ items: [...memberships, { id: second, name: 'Location' }], incomplete: false }));
    await app.load();
    await app.opened();
    fixtures.depart.mockReturnValue(false);
    await app.button('Location').trigger('click');
    await app.button('Invitations').trigger('click');
    await (await vi.waitFor(() => app.button('Confirm synthetic Join'), { timeout: 5000 })).trigger('click');
    assert.deepEqual(app.assign.mock.calls, []);
    assert.equal(fixtures.depart.mock.calls.length, 2);
    fixtures.depart.mockReturnValue(true);
    await app.button('Confirm synthetic Join').trigger('click');
    assert.deepEqual(app.assign.mock.calls, [[`/?workspace=${second}`]]);
});

test('initial workspace binding updates Vue Router before access and survives ordinary navigation and history travel', async () => {
    const app = await shell(null, async () => discovery, async () => {
        assert.equal(router.currentRoute.value.query.workspace, first);
        return { role: 'Owner' };
    });
    await app.load();
    await app.opened();
    await router.push(`/?workspace=${first}`);
    const travel = async (direction: number) => {
        const pending = Promise.withResolvers<void>();
        const stop = router.afterEach(() => { stop(); pending.resolve(); });
        router.go(direction);
        await pending.promise;
    };
    await travel(-1);
    assert.equal(router.currentRoute.value.fullPath, scopedCapture);
    await travel(1);
    assert.equal(router.currentRoute.value.query.workspace, first);
    assert.equal(app.scope.getPageWorkspaceId(), first);
    assert.equal(app.assign.mock.calls.length + app.reload.mock.calls.length, 0);
});

test('session loss while initial URL replacement is pending cannot mount private content or read access', async () => {
    const app = await shell(null);
    const entering = Promise.withResolvers<void>();
    const pending = Promise.withResolvers<void>();
    restore.push(router.beforeEach(to => {
        if (!to.query.workspace) return;
        entering.resolve();
        return pending.promise;
    }));
    app.signIn();
    await entering.promise;
    assert.equal(fixtures.refresh.mock.calls.length, 0);
    app.signOut();
    await nextTick();
    pending.resolve();
    await flushPromises();
    assert.equal(fixtures.refresh.mock.calls.length, 0);
    assert.equal(app.privateContent(), false);
});

test('cancelled initial navigation stays retryable and synchronizes Router before opening private content', async () => {
    const app = await shell(null);
    let cancel = true;
    restore.push(router.beforeEach(to => to.query.workspace && cancel ? false : undefined));
    await app.load();
    await vi.waitFor(() => assert.match(app.wrapper.text(), /Workspaces could not be checked/), { timeout: 5000 });
    assert.equal(app.privateContent(), false);
    assert.equal(fixtures.refresh.mock.calls.length, 0);
    cancel = false;
    await app.button('Retry').trigger('click');
    await app.opened();
    assert.equal(router.currentRoute.value.query.workspace, first);
    assert.equal(app.privateContent(), true);
    assert.equal(fixtures.refresh.mock.calls.length, 1);
});

test('confirmed Join opens the authoritative workspace even when membership discovery is unavailable', async () => {
    const app = await shell(null, async () => { throw new Error('Membership discovery unavailable'); });
    await app.load();
    await vi.waitFor(() => assert.match(app.wrapper.text(), /Workspaces could not be checked/), { timeout: 5000 });
    await app.button('Invitations').trigger('click');
    await (await vi.waitFor(() => app.button('Confirm synthetic Join'), { timeout: 5000 })).trigger('click');
    assert.deepEqual(app.assign.mock.calls, [[`/?workspace=${second}`]]);
    assert.equal(fixtures.refresh.mock.calls.length, 0);
});

test('discovery never redirects explicit unavailable selections, incomplete membership lists or accounts without access', async () => {
    for (const [selected, result, message] of [
        [second, discovery, /This workspace is unavailable/],
        ['', discovery, /This workspace is unavailable/],
        [null, { items: memberships, incomplete: true }, /Some memberships could not be checked/],
        [null, { items: [], incomplete: false }, /Join a workspace to get started/],
    ] as const) {
        const app = await shell(selected, async () => result);
        await app.load();
        assert.deepEqual(app.replace.mock.calls, []);
        assert.deepEqual(app.assign.mock.calls, []);
        assert.equal(app.privateContent(), false);
        await vi.waitFor(() => assert.match(app.wrapper.text(), message), { timeout: 5000 });
        assert.equal(fixtures.refresh.mock.calls.length, 0);
        await app.close();
    }
});

test('confirmed page selection mounts only after role access resolves and closes on permission loss without fallback', async () => {
    const access = Promise.withResolvers<{ role: string }>();
    const app = await shell(first, async () => discovery, () => access.promise);
    await app.load();
    await vi.waitFor(() => assert.equal(fixtures.refresh.mock.calls.length, 1), { timeout: 5000 });
    assert.equal(app.privateContent(), false);
    access.resolve({ role: 'Viewer' });
    await app.opened();
    app.revoke();
    await nextTick();
    assert.equal(app.privateContent(), false);
    assert.match(app.wrapper.text(), /Workspace access needs checking/);
    assert.deepEqual(app.replace.mock.calls, []);
    assert.deepEqual(app.assign.mock.calls, []);
});

test('browsing invitations preserves same-workspace drafts while Sign out still requires departure', async () => {
    const app = await shell(first);
    await app.load();
    await app.opened();
    const draft = () => app.wrapper.get<HTMLInputElement>('input[aria-label="Bulk tag draft"]');
    const originalInput = draft().element;
    await draft().setValue('unfinished bulk tag');
    fixtures.depart.mockReturnValue(false);
    await app.button('Invitations').trigger('click');
    assert.equal(app.wrapper.get('[data-workspace]').attributes('data-inactive'), 'true');
    assert.equal(draft().element, originalInput);
    assert.equal(draft().element.value, 'unfinished bulk tag');
    assert.equal(fixtures.depart.mock.calls.length, 0);
    await app.button('Back to workspace').trigger('click');
    assert.equal(app.wrapper.get('[data-workspace]').attributes('data-inactive'), 'false');
    assert.equal(draft().element, originalInput);
    assert.equal(draft().element.value, 'unfinished bulk tag');
    await app.button('Sign out').trigger('click');
    assert.equal(fixtures.depart.mock.calls.length, 1);
    assert.equal(fixtures.signOut.mock.calls.length, 0);
    assert.deepEqual(app.assign.mock.calls, []);
});
