import assert from 'node:assert/strict';
import { mountSuspended } from '@nuxt/test-utils/runtime';
import { flushPromises, type DOMWrapper, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, test, vi } from 'vitest';
import WorkspaceInvitations from '../../app/components/WorkspaceInvitations.vue';
import WorkspaceInvitationManagement from '../../app/components/WorkspaceInvitationManagement.vue';

const { transport, accept, renew, cancel, refreshAccess, removed } = vi.hoisted(() => {
    const accept = vi.fn(), renew = vi.fn(), cancel = vi.fn();
    return {
        accept, renew, cancel, refreshAccess: vi.fn(), removed: { generation: 0, listeners: new Set<() => void>() },
        transport: {
            workspaceOffers: { get: vi.fn(), byWorkspaceId: vi.fn() },
            management: { invitations: { get: vi.fn(), post: vi.fn(), byId: vi.fn() } },
        },
    };
});
vi.mock('../../app/lib/api', () => ({
    api: transport, cancellable: (signal: AbortSignal) => ({ signal }),
    handleFailure: (error: { status?: number }) => error.status,
}));
vi.mock('../../app/lib/session', () => ({
    accessGeneration: () => removed.generation,
    onAccessLost(callback: () => void) {
        removed.listeners.add(callback);
        return () => { removed.listeners.delete(callback); };
    },
}));
vi.mock('../../app/lib/workspace-access', async () => {
    const { ref } = await import('vue');
    return { mayAdminister: ref(true), refreshWorkspaceAccess: refreshAccess };
});

const offer = { id: 'offer', workspaceId: 'workspace', workspaceName: 'Synthetic studio', role: 'Viewer', status: 'pending', revision: 2 };
const invitation = { id: 'offer', recipientLabel: 'Synthetic recipient', role: 'Viewer', status: 'pending', revision: 2 };
const email = 'photographer@example.test';
const offerRead = transport.workspaceOffers.get;
const offerWorkspace = transport.workspaceOffers.byWorkspaceId;
const offerId = vi.fn();
const invitations = transport.management.invitations;
const byId = invitations.byId;
const create = invitations.post;

function applied(body: { operationId: string }) { return { operationId: body.operationId, status: 'applied' }; }
let dispose: (() => void) | undefined;

beforeEach(() => {
    vi.resetAllMocks();
    removed.generation = 0;
    offerRead.mockResolvedValue({ items: [offer], incomplete: false });
    offerWorkspace.mockReturnValue({ byId: offerId });
    offerId.mockReturnValue({ accept: { post: accept } });
    accept.mockImplementation(async body => applied(body));
    invitations.get.mockResolvedValue({ items: [] });
    create.mockImplementation(async body => applied(body));
    byId.mockReturnValue({ renew: { post: renew }, cancel: { post: cancel } });
    renew.mockImplementation(async body => applied(body));
    cancel.mockImplementation(async body => applied(body));
    refreshAccess.mockResolvedValue(true);
    vi.stubGlobal('confirm', vi.fn(() => true));
});
afterEach(() => {
    unmount();
    removed.listeners.clear();
    vi.unstubAllGlobals();
});

function unmount() {
    dispose?.();
    dispose = undefined;
}
function loseAccess() {
    removed.generation++;
    removed.listeners.forEach(listener => listener());
}
function controls(wrapper: VueWrapper) {
    dispose = () => wrapper.unmount();
    function button(label: string, scope: VueWrapper | DOMWrapper<Element> = wrapper) {
        return scope.findAll('button').find(node => node.text() === label);
    }
    async function click(label: string, scope: VueWrapper | DOMWrapper<Element> = wrapper) {
        const action = button(label, scope);
        assert.ok(action, `Expected the ${label} button`);
        await action.trigger('click');
        await flushPromises();
    }
    async function dispatchBlockedClick(label: string) {
        const action = button(label);
        assert.ok(action, `Expected the ${label} button`);
        assert.equal(action.element.disabled, true);
        // A generic event reaches the handler even when the browser blocks mouse clicks.
        action.element.dispatchEvent(new Event('click', { bubbles: true }));
        await flushPromises();
    }
    return {
        wrapper, button, click, dispatchBlockedClick,
        feedback: () => wrapper.find('.notice[role="status"]').text(),
        joined: () => wrapper.emitted('joined') ?? [],
        counts: () => wrapper.emitted('count') ?? [],
    };
}
async function recipient() {
    const wrapper = await mountSuspended(WorkspaceInvitations, { route: '' });
    const view = controls(wrapper);
    await flushPromises();
    return view;
}
async function manager() {
    const wrapper = await mountSuspended(WorkspaceInvitationManagement, { route: '', props: { online: true, generation: 0 } });
    const view = controls(wrapper);
    await flushPromises();
    const form = () => wrapper.get('form');
    const address = () => form().get<HTMLInputElement>('input');
    const role = () => form().get<HTMLSelectElement>('select');
    async function enter(value = email, offeredRole = 'Viewer') {
        await address().setValue(value);
        await role().setValue(offeredRole);
    }
    async function submit() {
        await form().trigger('submit');
        await flushPromises();
    }
    return { ...view, address, role, enter, submit, create: async (value?: string, offeredRole?: string) => {
        await enter(value, offeredRole);
        await submit();
    } };
}

test('recipient discovery never accepts an offer and Join uses its exact revision without submitting role or identity', async () => {
    const view = await recipient();
    assert.match(view.wrapper.text(), /Synthetic studio.*Offered role: Viewer/);
    assert.equal(accept.mock.calls.length, 0);
    assert.deepEqual(view.counts(), [[1]]);
    await view.click('Join workspace');
    assert.deepEqual(offerWorkspace.mock.calls, [['workspace']]);
    assert.deepEqual(offerId.mock.calls, [['offer']]);
    assert.equal(accept.mock.calls.length, 1);
    assert.deepEqual(Object.keys(accept.mock.calls[0]![0]).sort(), ['expectedRevision', 'operationId']);
    assert.equal(accept.mock.calls[0]![0].expectedRevision, 2);
    assert.deepEqual(view.joined(), [['workspace']]);
});

test('unverified email keeps sign-in usable, refreshes offer status and never announces membership', async () => {
    accept.mockRejectedValueOnce({ status: 403, errorCode: 'email-verification-required' });
    const view = await recipient();
    await view.click('Join workspace');
    assert.match(view.feedback(), /verification.*sign.in service/i);
    assert.equal(offerRead.mock.calls.length, 2);
    assert.deepEqual(view.joined(), []);
    assert.equal(view.button('Check membership'), undefined);
    assert.equal(view.button('Join workspace')?.element.disabled, false);
});

test('unknown Join is preserved and deliberate check rereads before retrying the identical operation', async () => {
    accept.mockRejectedValueOnce(new Error('Network interrupted'));
    const view = await recipient();
    await view.click('Join workspace');
    assert.match(view.feedback(), /could not confirm whether you joined.*Check membership/);
    assert.deepEqual(view.joined(), []);
    await view.dispatchBlockedClick('Join workspace');
    assert.equal(accept.mock.calls.length, 1);

    await view.click('Check membership');
    assert.ok(offerRead.mock.invocationCallOrder[1]! < accept.mock.invocationCallOrder[1]!);
    assert.deepEqual(accept.mock.calls[1], accept.mock.calls[0]);
    assert.deepEqual(view.joined(), [['workspace']]);
});

test('late offer responses cannot repopulate a signed-out interface and incomplete discovery remains explicit', async () => {
    const late = Promise.withResolvers<{ items: typeof offer[]; incomplete: boolean }>();
    offerRead.mockReturnValueOnce(late.promise);
    const view = await recipient();
    assert.equal(offerRead.mock.calls.length, 1);
    const request = offerRead.mock.calls[0]![0].options[0].signal;
    loseAccess();
    assert.equal(request.aborted, true);
    late.resolve({ items: [offer], incomplete: false });
    await flushPromises();
    assert.doesNotMatch(view.wrapper.text(), /Synthetic studio/);
    assert.deepEqual(view.counts(), []);
    unmount();

    offerRead.mockResolvedValueOnce({ items: [offer], incomplete: true });
    const partial = await recipient();
    assert.match(partial.wrapper.text(), /Some invitations could not be checked/);
});

test('received invitation count refreshes after Join and incomplete discovery cannot claim a zero count', async () => {
    offerRead.mockResolvedValueOnce({ items: [offer], incomplete: false })
        .mockResolvedValueOnce({ items: [], incomplete: false })
        .mockResolvedValueOnce({ items: [], incomplete: true });
    const view = await recipient();
    assert.deepEqual(view.counts(), [[1]]);
    await view.click('Join workspace');
    assert.deepEqual(view.counts(), [[1], [0]]);
    await view.click('Refresh invitations');
    assert.match(view.wrapper.text(), /Some invitations could not be checked/);
    assert.deepEqual(view.counts(), [[1], [0]], 'incomplete discovery cannot replace the known count');
});

test('expired, cancelled and accepted offers never submit a new Join, and malformed responses block stale actions', async () => {
    const nonpending = ['expired', 'cancelled', 'accepted'].map(status => ({ ...offer, id: status, status }));
    offerRead.mockResolvedValueOnce({ items: nonpending, incomplete: false })
        .mockResolvedValueOnce({ items: [offer], incomplete: false })
        .mockResolvedValueOnce({ items: undefined, incomplete: false });
    const view = await recipient();
    assert.equal(view.wrapper.findAll('.member-row').length, 3);
    assert.equal(view.button('Join workspace'), undefined);
    assert.equal(accept.mock.calls.length, 0);
    await view.click('Refresh invitations');
    assert.equal(view.button('Join workspace')?.element.disabled, false);
    await view.click('Refresh invitations');
    assert.match(view.wrapper.get('[role="alert"]').text(), /could not be checked/);
    await view.dispatchBlockedClick('Join workspace');
    assert.equal(accept.mock.calls.length, 0);
});

test('invitation creation accepts an email and defaults to Viewer without recipient provisioning', async () => {
    const view = await manager();
    assert.equal(view.role().element.value, 'Viewer');
    await view.create('   ');
    assert.equal(create.mock.calls.length, 0);
    await view.create(` ${email} `);
    assert.equal(create.mock.calls.length, 1);
    const body = create.mock.calls[0]![0];
    assert.deepEqual(Object.keys(body).sort(), ['email', 'operationId', 'role']);
    assert.equal(body.email, email);
    assert.equal(body.role, 'Viewer');
    assert.equal(view.address().element.value, '');
});

test('interrupted creation checks invitations then retries the same email, role and operation', async () => {
    create.mockRejectedValueOnce(new Error('Interrupted'));
    const view = await manager();
    await view.create(email, 'Curator');
    assert.equal(view.address().element.disabled, true);
    assert.equal(view.button('Create invitation')?.element.disabled, true);
    // Simulate submission reaching the form guard even while its controls are disabled.
    await view.submit();
    assert.equal(create.mock.calls.length, 1);
    await view.click('Check invitation');
    assert.ok(invitations.get.mock.invocationCallOrder[1]! < create.mock.invocationCallOrder[1]!);
    assert.deepEqual(create.mock.calls[1], create.mock.calls[0]);
    assert.equal(create.mock.calls[1]![0].email, email);
    assert.equal(create.mock.calls[1]![0].role, 'Curator');
    assert.equal(view.button('Check invitation'), undefined);
});

test('existing membership response explains that an invitation did not change its role', async () => {
    create.mockImplementationOnce(async body => ({ ...applied(body), status: 'existingMember' }));
    const view = await manager();
    await view.create('member@example.test', 'Administrator');
    assert.match(view.feedback(), /already has a membership.*role was not changed/);
    assert.equal(view.button('Check invitation'), undefined);
});

test('an invitation response for a different operation stays pending until a successful check', async () => {
    create.mockResolvedValueOnce({ operationId: 'different-operation', status: 'applied' });
    const view = await manager();
    await view.create();
    assert.equal(view.address().element.value, email);
    assert.match(view.feedback(), /could not confirm/);

    invitations.get.mockRejectedValueOnce(new Error('Invitation list unavailable'));
    await view.click('Check invitation');
    assert.equal(create.mock.calls.length, 1);
    assert.ok(view.button('Check invitation'));

    await view.click('Check invitation');
    assert.equal(create.mock.calls.length, 2);
    assert.deepEqual(create.mock.calls[1], create.mock.calls[0]);
    assert.equal(view.button('Check invitation'), undefined);
    assert.equal(view.address().element.value, '');
});

test('failed setup email preserves the saved invitation and explicitly retries the same operation', async () => {
    invitations.get.mockResolvedValue({ items: [invitation] });
    create.mockImplementationOnce(async body => ({ ...applied(body), setupEmailStatus: 'setup-email-failed' }))
        .mockImplementationOnce(async body => ({ ...applied(body), setupEmailStatus: 'setup-email-sent' }));
    const view = await manager();
    await view.create();
    assert.equal(view.button('Check invitation'), undefined);
    assert.match(view.feedback(), /Invitation saved.*email could not be sent.*Retry account setup email/);
    assert.equal(view.wrapper.findAll('.member-row').length, 1);
    await view.click('Retry account setup email');
    assert.deepEqual(create.mock.calls[1], create.mock.calls[0]);
    assert.equal(view.button('Retry account setup email'), undefined);
    assert.match(view.feedback(), /Account setup email sent/);
});

test('verified existing accounts get a shareable app address instruction without a setup email claim', async () => {
    create.mockImplementationOnce(async body => ({ ...applied(body), setupEmailStatus: 'not-required' }));
    const view = await manager();
    await view.create();
    assert.match(view.feedback(), /Share this app.*address.*sign in.*Join workspace/);
    assert.doesNotMatch(view.feedback(), /email sent/);
    assert.equal(view.button('Retry account setup email'), undefined);
});

test('setup email retry reports a subsequently cancelled invitation without claiming it can be joined', async () => {
    const cancelled = { ...invitation, status: 'cancelled' };
    invitations.get.mockResolvedValue({ items: [invitation] });
    create.mockImplementationOnce(async body => ({ ...applied(body), offer: invitation, setupEmailStatus: 'setup-email-failed' }))
        .mockImplementationOnce(async body => ({ ...applied(body), offer: cancelled, setupEmailStatus: 'not-required' }));
    const view = await manager();
    await view.create();
    invitations.get.mockResolvedValueOnce({ items: [cancelled] });
    await view.click('Retry account setup email');
    assert.equal(view.button('Retry account setup email'), undefined);
    assert.match(view.wrapper.get('.member-row').text(), /cancelled/);
    assert.match(view.feedback(), /status has changed/);
    assert.doesNotMatch(view.feedback(), /Join workspace|email sent/);
});

test.each([
    ['invalid-email', 400, /valid email address/],
    ['account-unavailable', 409, /account is unavailable.*workspace owner/],
    ['enrollment-rejected', 409, /Account setup was refused.*workspace owner/],
    ['enrollment-unavailable', 503, /account setup is temporarily unavailable.*Check invitation/i],
] as const)('invitation creation explains %s and preserves the appropriate retry', async (errorCode, status, message) => {
    create.mockRejectedValueOnce({ errorCode, status });
    const view = await manager();
    await view.create(email, 'Curator');
    assert.match(view.feedback(), message);
    assert.equal(view.address().element.value, email);
    assert.equal(view.role().element.value, 'Curator');
    assert.equal(invitations.get.mock.calls.length, 1);
    assert.equal(refreshAccess.mock.calls.length, 0);
    assert.equal(!!view.button('Check invitation'), status === 503);
    assert.equal(view.button('Retry account setup email'), undefined);
});

test('late Join and creation replies cannot announce success after access is lost', async () => {
    const lateJoin = Promise.withResolvers<ReturnType<typeof applied>>();
    accept.mockReturnValueOnce(lateJoin.promise);
    const received = await recipient();
    await received.click('Join workspace');
    assert.equal(accept.mock.calls.length, 1);
    loseAccess();
    lateJoin.resolve(applied(accept.mock.calls[0]![0]));
    await flushPromises();
    assert.deepEqual(received.joined(), []);
    assert.equal(received.wrapper.find('.notice').exists(), false);
    assert.equal(received.button('Check membership'), undefined);
    unmount();

    const lateCreate = Promise.withResolvers<ReturnType<typeof applied>>();
    create.mockReturnValueOnce(lateCreate.promise);
    const admin = await manager();
    await admin.create();
    assert.equal(create.mock.calls.length, 1);
    loseAccess();
    lateCreate.resolve(applied(create.mock.calls[0]![0]));
    await flushPromises();
    assert.equal(admin.wrapper.find('.notice').exists(), false);
    assert.equal(admin.wrapper.find('form').exists(), false);
    assert.equal(admin.button('Check invitation'), undefined);
    await admin.click('Refresh invitations');
    assert.equal(admin.address().element.value, '');
});

test('renew and cancel use current invitation revision and an uncertain renewal blocks competing actions', async () => {
    invitations.get.mockResolvedValue({ items: [invitation] });
    renew.mockRejectedValueOnce(new Error('Interrupted'));
    const view = await manager();
    await view.click('Renew invitation');
    assert.equal(renew.mock.calls[0]![0].expectedRevision, 2);
    assert.equal(renew.mock.calls[0]![0].role, 'Viewer');
    await view.dispatchBlockedClick('Cancel invitation');
    assert.equal(cancel.mock.calls.length, 0);
    await view.click('Check invitation');
    assert.ok(invitations.get.mock.invocationCallOrder[1]! < renew.mock.invocationCallOrder[1]!);
    assert.deepEqual(renew.mock.calls[1], renew.mock.calls[0]);
    assert.equal(view.button('Check invitation'), undefined);
    assert.equal(cancel.mock.calls.length, 0);
});

test('revision conflict refreshes the current offer and requires a new deliberate decision', async () => {
    invitations.get.mockResolvedValueOnce({ items: [invitation] })
        .mockResolvedValue({ items: [{ ...invitation, revision: 3 }] });
    cancel.mockRejectedValueOnce({ status: 409, errorCode: 'invitation-conflict' });
    const view = await manager();
    await view.click('Cancel invitation');
    assert.equal(cancel.mock.calls.length, 1);
    assert.equal(cancel.mock.calls[0]![0].expectedRevision, 2);
    assert.equal(view.button('Check invitation'), undefined);
    assert.match(view.feedback(), /changed/);
    assert.equal(refreshAccess.mock.calls.length, 0);
    await view.click('Cancel invitation');
    assert.equal(cancel.mock.calls[1]![0].expectedRevision, 3);
});

test('permission denial refreshes capabilities and invitations while preserving the invitation form', async () => {
    create.mockRejectedValueOnce({ status: 403, errorCode: 'permission-denied' });
    const view = await manager();
    await view.create(email, 'Curator');
    assert.equal(refreshAccess.mock.calls.length, 1);
    assert.equal(invitations.get.mock.calls.length, 2);
    assert.equal(view.button('Check invitation'), undefined);
    assert.equal(view.address().element.value, email);
    assert.equal(view.role().element.value, 'Curator');
    assert.match(view.feedback(), /current role does not permit/);
});

test('only pending, expired and cancelled invitations can renew and invalidated recipients require fresh Create', async () => {
    const statuses = ['pending', 'expired', 'cancelled', 'invalidated', 'accepted'];
    invitations.get.mockResolvedValue({ items: statuses.map(status => ({ ...invitation, id: status, recipientLabel: status, status })) });
    const view = await manager();
    for (const status of statuses) {
        const row = view.wrapper.findAll('.member-row').find(item => item.get('strong').text() === status)!;
        if (['pending', 'expired', 'cancelled'].includes(status)) await view.click('Renew invitation', row);
        else assert.equal(view.button('Renew invitation', row), undefined);
        if (status !== 'pending') assert.equal(view.button('Cancel invitation', row), undefined);
    }
    assert.deepEqual(byId.mock.calls, [['pending'], ['expired'], ['cancelled']]);
    assert.equal(cancel.mock.calls.length, 0);
    await view.create();
    assert.equal(create.mock.calls.length, 1);
    assert.equal(create.mock.calls[0]![0].email, email);
});

test('cancellation needs no renewal role and retains only its own operation and revision on interruption', async () => {
    invitations.get.mockResolvedValue({ items: [{ ...invitation, role: undefined }] });
    cancel.mockRejectedValueOnce(new Error('Interrupted'));
    const view = await manager();
    await view.click('Cancel invitation');
    assert.equal(cancel.mock.calls.length, 1);
    assert.deepEqual(Object.keys(cancel.mock.calls[0]![0]).sort(), ['expectedRevision', 'operationId']);
    assert.equal(cancel.mock.calls[0]![0].expectedRevision, 2);
    await view.click('Check invitation');
    assert.deepEqual(byId.mock.calls, [['offer'], ['offer']]);
    assert.deepEqual(cancel.mock.calls[1], cancel.mock.calls[0]);
    assert.equal(renew.mock.calls.length, 0);
});
