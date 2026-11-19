import assert from 'node:assert/strict';
import { mountSuspended } from '@nuxt/test-utils/runtime';
import { flushPromises, type DOMWrapper, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, test, vi } from 'vitest';
import { nextTick } from 'vue';
import WorkspaceLeave from '../../app/components/WorkspaceLeave.vue';
import WorkspaceMembers from '../../app/components/WorkspaceMembers.vue';
import { workspaceAccess } from '../../app/lib/workspace-access';

const { management, refresh, close } = vi.hoisted(() => ({
    management: {
        members: { get: vi.fn(), byId: vi.fn() },
        viewerPolicy: { get: vi.fn(), put: vi.fn() },
        membership: { leave: { post: vi.fn() } },
    },
    refresh: vi.fn(),
    close: vi.fn(),
}));
vi.mock('../../app/lib/api', () => ({
    api: { management }, cancellable: (signal: AbortSignal) => ({ signal }),
    handleFailure: (error: { status?: number }) => error.status,
}));
vi.mock('../../app/lib/session', () => ({ accessGeneration: () => 0, onAccessLost: () => () => {} }));
vi.mock('../../app/lib/workspace-access', async () => {
    const { ref } = await import('vue');
    return { workspaceAccess: ref(), mayAdminister: ref(true), refreshWorkspaceAccess: refresh, closeWorkspaceAccess: close };
});

const mockOwner = {
    membershipId: 'owner', role: 'Owner' as const, revision: 4,
    capabilities: { canManageOwners: true, canAdminister: true, canManageMembers: true, canCurate: true },
    viewerPolicy: { includeUnreleased: false, revision: 4 },
};

let unmount: (() => void) | undefined;
const confirm = vi.fn<(message?: string) => boolean>();
beforeEach(() => {
    vi.resetAllMocks();
    workspaceAccess.value = structuredClone(mockOwner);
    management.members.get.mockResolvedValue({ items: [] });
    management.viewerPolicy.get.mockResolvedValue({ includeUnreleased: false, revision: 4 });
    refresh.mockResolvedValue(true);
    vi.stubGlobal('confirm', confirm.mockReturnValue(true));
});
afterEach(() => {
    unmount?.();
    unmount = undefined;
    vi.unstubAllGlobals();
});

type SearchScope = VueWrapper | DOMWrapper<Element>;
function button(scope: SearchScope, label: string) {
    const result = scope.findAll('button').find(item => item.text() === label);
    assert.ok(result, `Expected the ${label} button`);
    return result;
}
function member(id: string, changes = {}) {
    return { id, subject: id, issuer: 'https://issuer.example.test', role: 'Viewer', revision: 2, isActive: true, ...changes };
}
async function members() {
    const wrapper = await mountSuspended(WorkspaceMembers, {
        route: '', props: { online: true, generation: 0 },
        global: { stubs: { WorkspaceInvitationManagement: true, UIcon: true } },
    });
    unmount = () => wrapper.unmount();
    await flushPromises();
    function row(id: string) {
        const match = wrapper.findAll('.member-row').find(item => item.text().includes(`Membership: ${id}`));
        assert.ok(match, `Expected the ${id} member`);
        return match;
    }
    return {
        wrapper, row,
        role: (id: string) => row(id).get<HTMLSelectElement>('select'),
        visibility: () => wrapper.get<HTMLSelectElement>('.member-policy select'),
        act: async (label: string, scope: SearchScope = wrapper) => {
            await button(scope, label).trigger('click');
            await flushPromises();
        },
    };
}

test('background membership reconciliation preserves an unsaved role or Viewer visibility choice', async () => {
    management.members.get.mockResolvedValue({ items: [member('target')] });
    const view = await members();
    assert.equal(management.members.get.mock.calls.length, 1);
    await view.role('target').setValue('Curator');
    await view.visibility().setValue('true');
    await view.wrapper.setProps({ generation: 1 });
    await flushPromises();
    assert.equal(management.members.get.mock.calls.length, 1);
    assert.equal(view.role('target').element.value, 'Curator');
    assert.equal(view.visibility().element.value, 'true');
    assert.equal(management.viewerPolicy.put.mock.calls.length, 0);
});

test('member identity uses the IdP display name and falls back to its subject in rows and confirmations', async () => {
    management.members.get.mockResolvedValue({ items: [
        member('ada', { displayName: '  Ada Example  ', subject: 'sub-ada', role: 'Curator', revision: 7 }),
        member('blank', { displayName: '   ', subject: 'sub-blank' }),
        member('null', { displayName: null, subject: 'sub-null' }),
    ] });
    const view = await members();
    assert.equal(view.row('ada').get('strong').text(), 'Ada Example');
    assert.equal(view.row('blank').get('strong').text(), 'sub-blank');
    assert.equal(view.row('null').get('strong').text(), 'sub-null');
    confirm.mockReturnValueOnce(false);
    await view.act('Make Owner…', view.row('ada'));
    assert.match(String(confirm.mock.lastCall?.[0]), /Make Ada Example an Owner/);
    assert.equal(management.members.byId.mock.calls.length, 0);
});

test('saving one membership or Viewer policy preserves the other unsaved choices', async () => {
    const items = ['first', 'second'].map(id => member(id));
    const policy = { includeUnreleased: false, revision: 4 };
    const roles = vi.fn(async (id: string, body: { role: string; expectedRevision: number }) => {
        const saved = items.find(item => item.id === id)!;
        assert.equal(body.expectedRevision, saved.revision);
        saved.role = body.role;
        saved.revision++;
        return { status: 'applied' };
    });
    management.members.get.mockImplementation(async () => ({ items: structuredClone(items) }));
    management.members.byId.mockImplementation((id: string) => ({ role: { post: (body: Parameters<typeof roles>[1]) => roles(id, body) } }));
    management.viewerPolicy.get.mockImplementation(async () => ({ ...policy }));
    management.viewerPolicy.put.mockImplementation(async body => {
        assert.equal(body.expectedRevision, policy.revision);
        policy.includeUnreleased = body.includeUnreleased;
        policy.revision++;
        return { status: 'applied' };
    });
    const view = await members();
    await view.role('first').setValue('Curator');
    await view.role('second').setValue('Administrator');
    await view.visibility().setValue('true');
    await view.act('Save role', view.row('first'));
    assert.match(view.row('first').get('.member-identity span').text(), /Curator/);
    assert.equal(button(view.row('first'), 'Save role').element.disabled, true);
    assert.equal(view.role('second').element.value, 'Administrator');
    assert.equal(view.visibility().element.value, 'true');
    assert.equal(policy.includeUnreleased, false);

    await view.act('Save Viewer visibility');
    assert.equal(policy.revision, 5);
    assert.equal(policy.includeUnreleased, true);
    assert.equal(view.role('second').element.value, 'Administrator');
    await view.act('Save role', view.row('second'));
    assert.equal(button(view.row('second'), 'Save role').element.disabled, true);

    await view.role('first').setValue('Viewer');
    await view.act('Save role', view.row('first'));
    assert.equal(button(view.row('first'), 'Save role').element.disabled, true);
    assert.deepEqual(roles.mock.calls.map(([id, body]) => [id, body.role, body.expectedRevision]), [
        ['first', 'Curator', 2], ['second', 'Administrator', 2], ['first', 'Viewer', 3],
    ]);
    await view.visibility().setValue('false');
    await view.act('Save Viewer visibility');
    assert.deepEqual(management.viewerPolicy.put.mock.calls.map(([body]) => [body.includeUnreleased, body.expectedRevision]), [
        [true, 4], [false, 5],
    ]);
    assert.match(view.wrapper.get('.notice[role="status"]').text(), /Viewer visibility saved/);
});

test('membership reads keep edits made during a delayed read and explain drafts invalidated by changed authority', async () => {
    let items = ['changed', 'removed', 'inactive', 'unchanged'].map(id => member(id));
    let policy = { includeUnreleased: false, revision: 4 };
    management.members.get.mockImplementation(async () => ({ items: structuredClone(items) }));
    management.viewerPolicy.get.mockImplementation(async () => ({ ...policy }));
    const view = await members();
    const delayed = Promise.withResolvers<{ items: typeof items }>();
    management.members.get.mockReturnValueOnce(delayed.promise);
    // Dispatch edits queued in the same UI turn, before Vue renders the busy controls.
    const refresh = button(view.wrapper, 'Refresh members').trigger('click');
    await Promise.all([view.role('unchanged').setValue('Curator'), view.visibility().setValue('true'), refresh]);
    assert.equal(management.members.get.mock.calls.length, 2);
    delayed.resolve({ items: structuredClone(items) });
    await flushPromises();
    assert.equal(view.role('unchanged').element.value, 'Curator');
    assert.equal(view.visibility().element.value, 'true');

    for (const id of ['changed', 'removed', 'inactive']) await view.role(id).setValue('Curator');
    items = items.filter(item => item.id !== 'removed').map(item => {
        if (item.id === 'changed') return { ...item, role: 'Administrator', revision: 3 };
        if (item.id === 'inactive') return { ...item, isActive: false, revision: 3 };
        return item;
    });
    policy = { includeUnreleased: false, revision: 5 };
    await view.act('Refresh members');
    assert.equal(view.role('changed').element.value, 'Administrator');
    assert.doesNotMatch(view.wrapper.text(), /Membership: removed/);
    assert.equal(view.row('inactive').find('select').exists(), false);
    assert.equal(view.role('unchanged').element.value, 'Curator');
    assert.equal(view.visibility().element.value, 'false');
    assert.match(view.wrapper.text(), /unsaved role choices.*reset/i);
    assert.match(view.wrapper.text(), /unsaved Viewer visibility.*reset/i);
});

test('uncertain self-leave retains its operation ID for an explicit outcome check and keeps last Owner failures actionable', async () => {
    const leave = management.membership.leave.post;
    leave.mockRejectedValueOnce({ status: 503 })
        .mockRejectedValueOnce({ status: 409, errorCode: 'last-owner' })
        .mockResolvedValueOnce({ status: 'applied' });
    const wrapper = await mountSuspended(WorkspaceLeave, { route: '', props: { online: true }, global: { stubs: { UIcon: true } } });
    unmount = () => wrapper.unmount();
    await button(wrapper, 'Leave workspace').trigger('click');
    await flushPromises();
    assert.match(wrapper.text(), /uncertain/);
    await button(wrapper, 'Check previous leave request').trigger('click');
    await flushPromises();
    assert.deepEqual(leave.mock.calls[1], leave.mock.calls[0]);
    assert.equal(leave.mock.calls[0]?.[0].expectedRevision, 4);
    assert.equal(confirm.mock.calls.length, 1);
    assert.match(wrapper.text(), /must keep an active Owner/);
    assert.equal(close.mock.calls.length, 0);
    await button(wrapper, 'Leave workspace').trigger('click');
    await flushPromises();
    assert.notEqual(leave.mock.calls[2]?.[0].operationId, leave.mock.calls[1]?.[0].operationId);
    assert.equal(confirm.mock.calls.length, 2);
    assert.deepEqual(close.mock.calls, [[true]]);
});

test('only an Owner can promote, promotion uses a separate explicit confirmation and uncertain checks keep the same command', async () => {
    const promote = vi.fn().mockRejectedValue({ status: 503 });
    const changeRole = vi.fn();
    management.members.get.mockResolvedValue({ items: [member('target', { role: 'Curator', revision: 7 }), member('other')] });
    management.members.byId.mockReturnValue({ promoteOwner: { post: promote }, role: { post: changeRole } });
    const view = await members();
    const stale = button(view.row('target'), 'Make Owner…');
    workspaceAccess.value!.capabilities.canManageOwners = false;
    await stale.trigger('click');
    assert.equal(promote.mock.calls.length, 0);
    assert.equal(confirm.mock.calls.length, 0);
    assert.doesNotMatch(view.wrapper.text(), /Make Owner/);
    workspaceAccess.value!.capabilities.canManageOwners = true;
    await nextTick();
    await view.role('other').setValue('Curator');
    await view.act('Make Owner…', view.row('target'));
    assert.equal(promote.mock.calls.length, 1);
    assert.equal(promote.mock.calls[0]?.[0].confirmed, true);
    assert.equal(promote.mock.calls[0]?.[0].expectedRevision, 7);
    assert.match(String(confirm.mock.lastCall?.[0]), /Owners can appoint and remove other Owners/);
    await view.act('Check previous change');
    assert.deepEqual(promote.mock.calls[1], promote.mock.calls[0]);
    const competing = button(view.row('other'), 'Save role').element;
    assert.equal(competing.disabled, true);
    competing.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    assert.equal(changeRole.mock.calls.length, 0);
    assert.equal(promote.mock.calls.length, 2);
});
