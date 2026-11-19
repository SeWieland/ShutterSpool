import { mountSuspended } from '@nuxt/test-utils/runtime';
import { flushPromises } from '@vue/test-utils';
import { afterEach, beforeEach, expect, onTestFinished, test, vi } from 'vitest';
import CaptureEvidence from '../../app/components/CaptureEvidence.vue';
import DeliveryWork from '../../app/components/DeliveryWork.vue';
import ReevaluateAction from '../../app/components/ReevaluateAction.vue';
import DeliveryWorkspace from '../../app/components/DeliveryWorkspace.vue';
import { workspaceAccess } from '../../app/lib/workspace-access';
import { clearPrivateAccess } from '../../app/lib/session';

const endpoints = vi.hoisted(() => ({ read: vi.fn(), attempts: vi.fn(), retry: vi.fn(), preview: vi.fn(), confirm: vi.fn(), list: vi.fn() }));
vi.mock('../../app/lib/api', async importOriginal => {
    const original = await importOriginal<typeof import('../../app/lib/api')>();
    const capturePage = (kind: string, id: string) => ({ get: (options: unknown) => endpoints.read(kind, id, options) });
    return { ...original, api: { management: {
        captures: { byId: (id: string) => ({ routing: capturePage('routing', id), deliveries: capturePage('deliveries', id), actions: capturePage('actions', id), deliverySummary: capturePage('summary', id) }) },
        deliveries: { byId: (id: string) => ({ attempts: { get: () => endpoints.attempts(id) }, retry: { post: () => endpoints.retry(id) }, resend: { preview: { post: () => endpoints.preview(id) }, post: endpoints.confirm } }) },
        assets: { byAssetId: (asset: string) => ({ destinations: { byDestinationId: (destination: string) => ({
            reevaluatePreview: { get: () => endpoints.preview(asset, destination) }, reevaluate: { post: (body: unknown) => endpoints.confirm(asset, destination, body) },
        }) } }) },
        destinations: { get: endpoints.list, capabilities: { get: async () => ({ enabledKinds: ['local', 'webdav'] }) },
            byId: (id: string) => ({ get: async () => ({ id, name: id.toUpperCase(), kind: 'local', configurationRevision: 1, localRootPath: 'C:/synthetic' }),
                routing: { get: async () => ({ destinationId: id, enabled: true, configurationRevision: 1, rules: [] }) }, work: { get: async () => ({ items: [] }) } }),
        },
    } } };
});
function role(role: 'Owner' | 'Viewer' | 'Curator') {
    workspaceAccess.value = { membershipId: 'member', role, revision: 1, viewerPolicy: { revision: 1, includeUnreleased: true },
        capabilities: { canCurate: role !== 'Viewer', canAdminister: role === 'Owner', canManageMembers: role === 'Owner', canManageOwners: role === 'Owner' } };
}
beforeEach(() => { vi.resetAllMocks(); role('Owner'); endpoints.read.mockResolvedValue({ items: [] }); });
afterEach(() => { workspaceAccess.value = undefined; vi.restoreAllMocks(); });

const global = { stubs: { UIcon: true, CollectionPicker: true, AuthorizedPreview: true } };
function capture(id: string) { return { id, assets: [{ id: id + '-asset', variant: 'JPEG' }], curation: { revision: 1, releasedAt: new Date() } }; }
async function mountEvidence(extra: Partial<InstanceType<typeof CaptureEvidence>['$props']> = {}) {
    const view = await mountSuspended(CaptureEvidence, { route: '', global, props: { capture: capture('a'), active: true, online: true, generation: 0, ...extra } });
    onTestFinished(() => view.unmount());
    await flushPromises();
    return view;
}
async function mountWork(extra: Partial<InstanceType<typeof DeliveryWork>['$props']> = {}) {
    const view = await mountSuspended(DeliveryWork, { route: '', global, props: { work: { id: 'a', assetId: 'asset', destinationId: 'target', state: 'Failed' }, online: true, ...extra } });
    onTestFinished(() => view.unmount());
    return view;
}
type View = Awaited<ReturnType<typeof mountEvidence>> | Awaited<ReturnType<typeof mountWork>>;
async function click(view: View, label: string) {
    const button = view.findAll('button').find(b => b.text().trim() === label);
    if (!button) throw new Error('Missing button: ' + label);
    await button.trigger('click');
    await flushPromises();
}

test('Curator and Viewer evidence uses only safe summaries and an uncertain Curator retry requires a fresh read', async () => {
    for (const access of ['Curator', 'Viewer'] as const) {
        role(access);
        endpoints.read.mockClear().mockResolvedValue({ items: [{ id: 'work', assetId: 'a-asset', state: 'Failed', canRetry: true }] });
        endpoints.retry.mockClear().mockRejectedValue({ responseStatusCode: 503 });
        const view = await mountEvidence();
        expect(endpoints.read.mock.calls.map(([kind]) => kind)).toEqual(['summary']);
        if (access === 'Curator') {
            await click(view, 'Retry now');
            expect(view.text()).toContain('Retry outcome could not be confirmed');
            const retry = view.findAll<HTMLButtonElement>('button').find(b => b.text() === 'Retry now')!;
            expect(retry.element.disabled).toBe(true);
            retry.element.dispatchEvent(new Event('click', { bubbles: true }));
            await flushPromises();
            expect(endpoints.retry).toHaveBeenCalledTimes(1);
            await click(view, 'Refresh evidence');
            expect(retry.element.disabled).toBe(false);
            expect(endpoints.read.mock.calls.map(([kind]) => kind)).toEqual(['summary', 'summary']);
            role('Viewer');
            retry.element.dispatchEvent(new Event('click', { bubbles: true }));
            await flushPromises();
            expect(view.text()).not.toContain('Retry now');
            expect(endpoints.retry).toHaveBeenCalledTimes(1);
            expect(endpoints.read.mock.calls.every(([kind]) => kind === 'summary')).toBe(true);
        } else {
            expect(view.text()).not.toContain('Retry now');
            expect(endpoints.retry).not.toHaveBeenCalled();
        }
    }
});

test('delivery summary groups targets and preserves older blockers while hiding duplicate matched routing', async () => {
    const target = { destinationId: 'd', destinationName: 'Archive' };
    endpoints.read.mockImplementation(async kind => ({ items: kind === 'routing' ? [
        { ...target, id: 'matched', assetId: 'a', kind: 'Matched', explanation: 'duplicate', isCurrent: true },
        { ...target, id: 'blocked', assetId: 'b', kind: 'DestinationDisabled', explanation: 'blocked asset', isCurrent: true },
        { ...target, id: 'past', assetId: 'b', kind: 'NoMatch', explanation: 'past route', isCurrent: false },
    ] : ['Delivered', 'Failed', 'Superseded'].map(state => ({ ...target, id: state, assetId: 'a', state })) }));
    const view = await mountEvidence({ deliveryOnly: true });
    expect(view.findAll('.delivery-target')).toHaveLength(1);
    expect(view.get('.delivery-target h3').text()).toBe('Archive');
    expect(view.findAll('.delivery-route').map(e => e.text())).toHaveLength(1);
    expect(view.get('.delivery-route').text()).toContain('blocked asset');
    const works = view.findAllComponents(DeliveryWork);
    expect(works.map(w => w.props('work').state)).toEqual(['Failed', 'Delivered', 'Superseded']);
    expect(works[2]!.props('readOnly')).toBe(true);
    expect(view.get('.delivery-history').text()).toContain('Earlier work · 1');
});

test('Capture evidence drops old-owner pages and cursors, but retains same-owner pages after refresh failure', async () => {
    const late = Promise.withResolvers<unknown>();
    let fail = '';
    endpoints.read.mockImplementation(async (kind, id, { queryParameters: { cursor } }) => {
        if (kind === 'deliveries' && id === 'a' && cursor) return late.promise;
        if (id === fail) throw new Error('unavailable');
        const label = cursor ?? id + '-' + kind;
        return { items: [{ id: label, assetId: id + '-asset', explanation: label, kind: label, state: label }], nextCursor: id + '-' + kind + '-next' };
    });
    const view = await mountEvidence();
    expect(view.text()).toContain('a-routing');
    await click(view, 'Earlier delivery work');
    const old = endpoints.read.mock.lastCall![2];
    expect(old.queryParameters.cursor).toBe('a-deliveries-next');
    fail = 'b';
    await view.setProps({ capture: capture('b') });
    await flushPromises();
    expect(old.options[0].signal.aborted).toBe(true);
    expect(view.text()).toContain('Unable to read delivery evidence');
    expect(view.text()).not.toMatch(/a-routing|a-deliveries|Earlier delivery decisions|Earlier delivery work|Older actions/);
    fail = '';
    await click(view, 'Refresh evidence');
    for (const [label, kind] of [['Earlier delivery decisions', 'routing'], ['Earlier delivery work', 'deliveries'], ['Older actions', 'actions']]) {
        await click(view, label!);
        expect(endpoints.read.mock.lastCall![2].queryParameters.cursor).toBe('b-' + kind + '-next');
    }
    const marker = () => expect(view.text()).toMatch(/b-routing-next[\s\S]*b-deliveries-next[\s\S]*b-actions-next/);
    marker();
    const revision = view.getComponent(DeliveryWork).props('recoverySequence');
    late.resolve({ items: [{ id: 'stale' }], nextCursor: 'stale-cursor' });
    await flushPromises();
    marker();
    fail = 'b';
    await click(view, 'Earlier delivery work');
    expect(view.text()).toContain('Unable to read delivery evidence');
    marker();
    await view.setProps({ generation: 1 });
    await flushPromises();
    marker();
    expect(view.getComponent(DeliveryWork).props('recoverySequence')).toBe(revision);
});

test('Delivery work ignores a late preview after owner change and clears private evidence on access loss', async () => {
    const pending = Promise.withResolvers<unknown>();
    endpoints.preview.mockReturnValueOnce(pending.promise).mockResolvedValueOnce({ confirmationToken: 'synthetic', explanation: 'private preview' });
    endpoints.attempts.mockResolvedValue({ items: [{ result: 'synthetic attempt' }] });
    const view = await mountWork();
    await click(view, 'Preview Resend');
    await view.setProps({ work: { id: 'b', assetId: 'other', destinationId: 'target' } });
    pending.resolve({ confirmationToken: 'old', explanation: 'late preview' });
    await flushPromises();
    expect(view.text()).not.toContain('late preview');
    await click(view, 'Preview Resend');
    const disclosure = view.get<HTMLDetailsElement>('details');
    disclosure.element.open = true;
    await disclosure.trigger('toggle');
    await flushPromises();
    expect(view.text()).toContain('private preview');
    expect(view.get('.attempt').text()).toContain('synthetic attempt');
    clearPrivateAccess();
    await flushPromises();
    expect(view.text()).not.toMatch(/private preview|synthetic attempt/);
});

test('Delivery work never issues an action without confirmed online state', async () => {
    const view = await mountWork({ online: false });
    for (const label of ['Retry', 'Preview Resend', 'Reevaluate']) {
        const action = view.findAll<HTMLButtonElement>('button').find(b => b.text().trim() === label)!;
        expect(action.element.disabled).toBe(true);
        action.element.dispatchEvent(new Event('click', { bubbles: true }));
        await flushPromises();
    }
    expect(endpoints.retry).not.toHaveBeenCalled();
    expect(endpoints.preview).not.toHaveBeenCalled();
});

test('read-only Delivery work keeps attempt history readable and blocks commands', async () => {
    endpoints.attempts.mockResolvedValue({ items: [{ result: 'synthetic attempt' }] });
    const view = await mountWork();
    const retry = view.findAll('button').find(b => b.text().trim() === 'Retry')!;
    await view.setProps({ readOnly: true });
    retry.element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    const disclosure = view.get<HTMLDetailsElement>('details');
    disclosure.element.open = true;
    await disclosure.trigger('toggle');
    await flushPromises();
    expect(endpoints.attempts).toHaveBeenCalledWith('a');
    expect(view.get('.attempt').text()).toContain('synthetic attempt');
    expect(view.findAll('button')).toHaveLength(0);
    expect(endpoints.retry).not.toHaveBeenCalled();
});

test('Reevaluate preview and confirmation are owner-fenced and require online state', async () => {
    const pending = Promise.withResolvers<unknown>();
    endpoints.preview.mockReturnValueOnce(pending.promise).mockResolvedValueOnce({ confirmationToken: 'current', explanation: 'current preview' });
    endpoints.confirm.mockResolvedValue({ status: 'Queued' });
    const view = await mountSuspended(ReevaluateAction, { route: '', global, props: { assetId: 'a', destinationId: 'd', online: false } });
    onTestFinished(() => view.unmount());
    expect(view.get<HTMLButtonElement>('button').element.disabled).toBe(true);
    view.get('button').element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(endpoints.preview).not.toHaveBeenCalled();
    await view.setProps({ online: true });
    await view.get('button').trigger('click');
    await view.setProps({ assetId: 'b' });
    pending.resolve({ confirmationToken: 'old', explanation: 'stale' });
    await flushPromises();
    expect(view.text()).not.toContain('stale');
    await view.get('button').trigger('click');
    await flushPromises();
    await view.setProps({ online: false });
    const confirmation = view.get<HTMLButtonElement>('.actions button');
    expect(confirmation.element.disabled).toBe(true);
    confirmation.element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(endpoints.confirm).not.toHaveBeenCalled();
    await view.setProps({ online: true });
    await confirmation.trigger('click');
    expect(endpoints.confirm).toHaveBeenCalledWith('b', 'd', { confirmationToken: 'current' });
    await view.setProps({ disabled: true });
    view.get('button').element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(endpoints.preview).toHaveBeenCalledTimes(2);
});

test.each([
    { phase: 'preview', status: 409, message: 'State changed.', refresh: 1 },
    { phase: 'confirm', status: 409, message: 'State changed.', refresh: 1 },
    { phase: 'confirm', status: 404, message: 'Outcome uncertain.', refresh: 1 },
    { phase: 'preview', status: 404, message: 'This destination or original is unavailable.', refresh: 0 },
    { phase: 'preview', status: 503, message: 'Unable to preview Reevaluate.', refresh: 0 },
])('Reevaluate $phase handles $status and waits for a deliberate retry', async ({ phase, status, message, refresh }) => {
    if (phase === 'confirm') {
        endpoints.preview.mockResolvedValueOnce({ confirmationToken: 'first', explanation: 'First preview' });
        endpoints.confirm.mockRejectedValueOnce({ status });
    } else {
        endpoints.preview.mockRejectedValueOnce({ status });
    }
    const view = await mountSuspended(ReevaluateAction, { route: '', global, props: { assetId: 'asset', destinationId: 'destination', online: true } });
    onTestFinished(() => view.unmount());
    await view.get('button').trigger('click');
    await flushPromises();
    if (phase === 'confirm') {
        await view.get('.actions button').trigger('click');
        await flushPromises();
        expect(endpoints.confirm).toHaveBeenCalledExactlyOnceWith('asset', 'destination', { confirmationToken: 'first' });
    }
    expect(view.get('[role="status"]').text()).toContain(message);
    expect(view.find('.actions').exists()).toBe(false);
    expect(view.emitted('refresh') ?? []).toHaveLength(refresh);
    expect(endpoints.preview).toHaveBeenCalledTimes(1);

    endpoints.preview.mockResolvedValueOnce({ confirmationToken: 'retry', explanation: 'New preview' });
    await view.get('button').trigger('click');
    await flushPromises();
    expect(view.text()).toContain('New preview');
    expect(endpoints.preview).toHaveBeenCalledTimes(2);
    expect(endpoints.confirm).toHaveBeenCalledTimes(phase === 'confirm' ? 1 : 0);
});

test('Gallery route-only reevaluation is limited to released current targets and avoids duplicate visible work actions', async () => {
    const target = { assetId: 'asset', destinationId: 'destination' };
    let work: object[] = [];
    endpoints.read.mockImplementation(async kind => ({ items: kind === 'routing' ? [{ ...target, id: 'decision', isCurrent: true, kind: 'DestinationDisabled' }] : work }));
    const view = await mountEvidence({ deliveryOnly: true });
    const eligible = () => view.get('.delivery-route').findComponent(ReevaluateAction).exists();
    expect(eligible()).toBe(true);
    work = [{ ...target, id: 'work', state: 'Failed' }];
    await view.setProps({ generation: 1 });
    await flushPromises();
    expect(eligible()).toBe(false);
    work = [{ ...target, id: 'work', state: 'Superseded' }];
    await view.setProps({ generation: 2 });
    await flushPromises();
    expect(eligible()).toBe(true);
    await view.setProps({ readOnly: true });
    expect(eligible()).toBe(false);
    await view.setProps({ readOnly: false, capture: { ...capture('a'), curation: { releasedAt: null } } });
    expect(eligible()).toBe(false);
});

test('inline destination editing keeps its confirmed owner when the visible destination page changes', async () => {
    endpoints.list.mockResolvedValueOnce({ items: [{ id: 'a', name: 'A', kind: 'local' }] }).mockResolvedValue({ items: [{ id: 'b', name: 'B', kind: 'webdav' }] });
    const view = await mountSuspended(DeliveryWorkspace, { route: '', global, props: { online: true, motionEnabled: false, generation: 0 } });
    onTestFinished(() => view.unmount());
    await flushPromises();
    await view.get('.destination-open').trigger('click');
    await flushPromises();
    await view.setProps({ generation: 1 });
    await flushPromises();
    expect(view.get('.destination-open').text()).toContain('B');
    expect(view.text()).not.toContain('Destination details are unavailable until');
    expect(view.get('.destination-return').exists()).toBe(true);
    expect(view.findAll('h2').map(h => h.text())).toContain('A');
});
