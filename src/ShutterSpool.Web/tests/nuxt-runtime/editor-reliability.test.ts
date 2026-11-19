import { attemptDisabledClick, button, click, management, mountEditor } from './editor-fixture';
import { destination, destinations, field, openDestination, route, rule, source, sources, submitWith } from './editor-owners';
import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import DeliveryWork from '../../app/components/DeliveryWork.vue';
import { readSourceActivity, readSourceAuthenticationObservations } from '../../app/lib/api';

const work = { id: 'row', assetId: 'asset', destinationId: 'destination', state: 'Failed' };
function delivery(endpoint, props = {}) {
    management.deliveries = { byId: () => endpoint };
    return mountEditor(DeliveryWork, { work, online: true, recoverySequence: 0, ...props });
}
test('Delivery resend previews supply the confirmation token and only a successful resend refreshes work after rejection', async () => {
    const preview = vi.fn().mockResolvedValueOnce({ confirmationToken: 'first' }).mockResolvedValueOnce({ confirmationToken: 'second' });
    const post = vi.fn().mockRejectedValueOnce({ status: 400 }).mockResolvedValueOnce({ status: 'Queued' });
    const wrapper = await delivery({ resend: { preview: { post: preview }, post } }, { deliveryOnly: true });
    expect(wrapper.text()).not.toContain('Preview Resend');
    await wrapper.setProps({ deliveryOnly: false });
    await click(wrapper, 'Preview Resend');
    await click(wrapper, /Confirm\s+Resend/);
    expect(wrapper.text()).toContain('Action rejected');
    expect(wrapper.emitted('refresh')).toBeUndefined();
    await click(wrapper, 'Preview Resend');
    await click(wrapper, /Confirm\s+Resend/);
    expect(post.mock.calls).toEqual([[{ confirmationToken: 'first' }], [{ confirmationToken: 'second' }]]);
    expect(wrapper.emitted('refresh')).toEqual([[]]);
});

test('Delivery command stays locked after a lost response until a new authoritative work row arrives', async () => {
    const post = vi.fn().mockRejectedValue({ status: 503 });
    const wrapper = await delivery({ retry: { post } });
    await click(wrapper, 'Retry');
    await wrapper.setProps({ work: { ...work } });
    await attemptDisabledClick(button(wrapper, 'Retry'));
    expect(wrapper.emitted('refresh')).toEqual([[]]);
    expect(post).toHaveBeenCalledTimes(1);
    await wrapper.setProps({ recoverySequence: 1 });
    expect(button(wrapper, 'Retry').element.disabled).toBe(false);
    await click(wrapper, 'Retry');
    expect(post).toHaveBeenCalledTimes(2);
});

test('Delivery attempts reads cancel superseded owner requests and cannot replace newer rows', async () => {
    const gates = [];
    const get = config => { const gate = Promise.withResolvers(); gates.push({ ...gate, signal: config.options[0].signal }); return gate.promise; };
    const wrapper = await delivery({ attempts: { get } });
    const details = wrapper.get('details');
    details.element.open = true;
    await flushPromises();
    if (!gates.length) await details.trigger('toggle');
    await details.trigger('toggle');
    expect(gates).toHaveLength(2);
    expect(gates[0].signal.aborted).toBe(true);
    gates[1].resolve({ items: [{ result: 'new attempt' }] });
    await flushPromises();
    gates[0].resolve({ items: [{ result: 'old attempt' }] });
    await flushPromises();
    expect(wrapper.get('.attempt').text()).toContain('new attempt');
    await details.trigger('toggle');
    wrapper.unmount();
    expect(gates[2].signal.aborted).toBe(true);
    gates[2].resolve({ items: [{ result: 'obsolete attempt' }] });
    await flushPromises();
});

test('mounted DeliveryWork invalidates a pending command when its editor unmounts', async () => {
    const gate = Promise.withResolvers(), refresh = vi.fn();
    const post = vi.fn(() => gate.promise);
    const wrapper = await delivery({ retry: { post } }, { onRefresh: refresh });
    await click(wrapper, 'Retry');
    expect(post).toHaveBeenCalledTimes(1);
    wrapper.unmount();
    gate.reject({ status: 503 });
    await flushPromises();
    expect(refresh).not.toHaveBeenCalled();
});

test('Source reports a newly edited Source draft through the typed dirty event', async () => {
    const wrapper = await sources([]);
    await field(wrapper, 'Name').setValue('Source draft');
    expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
});

test('mounted New destination controls report dirty state from real template input events', async () => {
    const warn = vi.spyOn(console, 'warn');
    const wrapper = await destinations([]);
    await field(wrapper, 'Name').setValue('New destination');
    await field(wrapper, 'Provider').setValue('webdav');
    expect(wrapper.emitted('dirty-change')).toEqual([[false], [true]]);
    expect(warn).not.toHaveBeenCalled();
});

test('mounted rule conflict controls keep the draft locked through failed and pending rereads', async () => {
    const row = destination('destination');
    const get = vi.fn(() => route(row.id, 1, [rule()]));
    const put = vi.fn().mockRejectedValue({ status: 503 });
    const wrapper = await destinations([row], { endpoint: () => ({ routing: { get, put } }) });
    await openDestination(wrapper, row.id);
    const name = () => wrapper.get('.rule-list input');
    await name().setValue('Local replacement draft');
    expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
    await click(wrapper, 'Save rules');
    for (const action of ['Explicitly replace with my draft', 'Use server rules']) {
        const failed = Promise.withResolvers();
        get.mockReturnValueOnce(failed.promise);
        await click(wrapper, action);
        expect(button(wrapper, action).element.disabled).toBe(true);
        expect(button(wrapper, 'Save rules').element.disabled).toBe(true);
        failed.reject({ status: 503 });
        await flushPromises();
        expect(name().element.value).toBe('Local replacement draft');
        await click(wrapper, 'Save rules');
        expect(put).toHaveBeenCalledTimes(action === 'Use server rules' ? 2 : 1);
        const success = Promise.withResolvers();
        get.mockReturnValueOnce(success.promise);
        await click(wrapper, action);
        expect(button(wrapper, action).element.disabled).toBe(true);
        success.resolve(route(row.id, 2, [rule()]));
        await flushPromises();
        if (action === 'Explicitly replace with my draft') {
            expect(name().element.value).toBe('Local replacement draft');
            await click(wrapper, 'Save rules');
            expect(put.mock.lastCall[0]).toMatchObject({ expectedRevision: 2, rules: [{ name: 'Local replacement draft' }] });
        } else {
            expect(name().element.value).toBe('Saved rule');
            expect(button(wrapper, 'Save rules').element.disabled).toBe(true);
        }
    }
});

function trackedReads() {
    const requests = [];
    let active = 0, maximum = 0;
    function track(kind, id, value, options) {
        const gate = Promise.withResolvers();
        const request = { kind, id, signal: options?.options[0].signal, finished: false, release() { request.finished = true; gate.resolve(value); } };
        requests.push(request); active++; maximum = Math.max(maximum, active);
        return gate.promise.finally(() => { active--; });
    }
    async function drain() {
        for (let n = 0; n < 40; n++) {
            await flushPromises();
            const pending = requests.filter(item => !item.finished);
            if (!pending.length) return;
            pending.forEach(item => item.release());
        }
        throw new Error('Metadata reads did not settle');
    }
    return { track, drain, requests, maximum: () => maximum };
}
const syntheticSources = count => Array.from({ length: count }, (_, i) => source('source-' + i));
function trackedSources(rows, reads, props = {}) {
    return sources(rows, {
        captureRead: options => reads.track('capture', options.queryParameters.sourceId, { items: [] }, options),
        endpoint: id => ({ display: { get: options => reads.track('display', id, source(id), options) } }), ...props,
    });
}

test('Delivery inventory and editor rereads share a four-request metadata limit', async () => {
    const rows = Array.from({ length: 6 }, (_, i) => destination('destination-' + i)), reads = trackedReads();
    const wrapper = await destinations(rows, { endpoint: id => ({
        get: options => reads.track('settings', id, destination(id), options),
        routing: { get: options => reads.track('routing', id, route(id), options) },
        work: { get: options => reads.track('summary', id, { items: [] }, options) },
    }) });
    expect(reads.requests).toHaveLength(4);
    await openDestination(wrapper, rows[0].id);
    await reads.drain();
    expect(reads.requests.map(row => row.kind).sort()).toEqual(['settings', 'routing', ...rows.map(() => 'summary')].sort());
    expect(reads.maximum()).toBe(4);
});

test('Source refresh bounds all capture and display metadata requests to four in flight', async () => {
    const rows = syntheticSources(9), reads = trackedReads();
    await trackedSources(rows, reads);
    await reads.drain();
    for (const kind of ['capture', 'display']) expect(reads.requests.filter(row => row.kind === kind).map(row => row.id).sort()).toEqual(rows.map(row => row.id).sort());
    expect(reads.maximum()).toBe(4);
});

test('Source refresh submits later owners while a first-wave transport remains pending', async () => {
    const rows = syntheticSources(6), reads = trackedReads();
    await trackedSources(rows, reads);
    expect(reads.requests.map(item => item.kind)).toEqual(['capture', 'capture', 'capture', 'capture']);
    reads.requests[0].release();
    await flushPromises();
    expect(reads.requests.map(item => item.id)).toEqual(rows.slice(0, 5).map(item => item.id));
    expect(reads.requests.map(item => item.kind)).toEqual(Array(5).fill('capture'));
    expect(reads.requests[1].finished).toBe(false);
    await reads.drain();
    expect(reads.requests).toHaveLength(12);
    expect(reads.maximum()).toBe(4);
});

test('a late Source display response cannot replace a newer owner read', async () => {
    const older = Promise.withResolvers(), newer = Promise.withResolvers();
    const display = vi.fn().mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);
    const row = source('camera');
    const wrapper = await sources([row], { endpoint: () => ({ display: { get: display } }) });
    await wrapper.setProps({ sources: [] });
    await wrapper.setProps({ sources: [row] });
    expect(display.mock.calls[0][0].options[0].signal.aborted).toBe(true);
    older.resolve({ ...row, name: 'obsolete label' });
    await flushPromises();
    expect(wrapper.get('.source-summary').attributes('aria-label')).not.toContain('obsolete label');
    newer.resolve({ ...row, name: 'current label' });
    await flushPromises();
    expect(wrapper.get('.source-summary').attributes('aria-label')).toContain('current label');
});

test('same-ID Source recovery cancels pending metadata and starts a fresh pass', async () => {
    const old = Promise.withResolvers(), latest = Promise.withResolvers();
    const get = vi.fn().mockReturnValueOnce(old.promise).mockReturnValueOnce(latest.promise);
    const display = vi.fn().mockResolvedValue(source('camera'));
    const wrapper = await sources([source('camera')], { captureRead: get, endpoint: () => ({ display: { get: display } }) });
    await wrapper.setProps({ recoverySequence: 1 });
    const signal = get.mock.calls[0][0].options[0].signal;
    expect(signal.aborted).toBe(true);
    old.reject(new Error('cancelled'));
    await flushPromises();
    expect(get).toHaveBeenCalledTimes(2);
    latest.resolve({ items: [] });
    await flushPromises();
    expect(display).toHaveBeenCalledTimes(1);
});

test('mounted Source watchers refresh unchanged IDs after recovery, activity, and revision replacement', async () => {
    const row = source('camera'), reads = trackedReads();
    vi.mocked(readSourceActivity).mockImplementation((_ids, signal) => reads.track('activity', row.id, { epoch: 'synthetic', items: [], coverageComplete: true }, { options: [{ signal }] }));
    vi.mocked(readSourceAuthenticationObservations).mockImplementation((_ids, signal) => reads.track('auth', row.id, { items: [], coverageComplete: true }, { options: [{ signal }] }));
    try {
        const wrapper = await trackedSources([row], reads, { profiles: options => reads.track('profiles', '', { profiles: [] }, options) });
        let passes = 0;
        for (const update of [undefined, { recoverySequence: 1 }, { activity: { captureId: 'capture', kind: 'captureChanged', sequence: 1 } }, { sources: [{ ...row, configurationRevision: 2 }] }]) {
            if (update) await wrapper.setProps(update);
            await reads.drain();
            passes++;
            expect(reads.requests.filter(item => item.kind === 'capture')).toHaveLength(passes);
            expect(reads.requests.filter(item => item.kind === 'display')).toHaveLength(passes);
        }
        for (const kind of ['profiles', 'auth', 'activity']) expect(reads.requests.some(item => item.kind === kind)).toBe(true);
        expect(reads.maximum()).toBeLessThanOrEqual(4);
    } finally {
        vi.mocked(readSourceActivity).mockReset();
        vi.mocked(readSourceAuthenticationObservations).mockReset();
    }
});

test('explicit destination reload keeps each draft locked until its matching authoritative read succeeds', async () => {
    const row = destination('same');
    let revision = 1;
    const settings = vi.fn(() => ({ ...row, configurationRevision: revision }));
    const routing = vi.fn(() => route(row.id, revision, [rule()]));
    const put = vi.fn(body => route(row.id, ++revision, body.rules));
    const saveName = vi.fn().mockRejectedValueOnce({ status: 503 }).mockImplementation(body => {
        row.name = body.name;
        return { ...row, configurationRevision: ++revision };
    });
    const wrapper = await destinations([row], { endpoint: () => ({ get: settings, put: saveName, routing: { get: routing, put } }) });
    await openDestination(wrapper, row.id);
    await wrapper.get('.rule-list input').setValue('Unsaved rule');
    await click(wrapper, 'Settings');
    await field(wrapper, 'Name').setValue('Unsaved name');
    await submitWith(wrapper, 'Save name');
    expect(saveName).toHaveBeenCalledTimes(1);
    revision = 2;
    await wrapper.setProps({ generation: 1 });
    await flushPromises();
    expect(button(wrapper, 'Save name').element.disabled).toBe(true);
    const fresh = Promise.withResolvers();
    settings.mockRejectedValueOnce({ status: 503 });
    await click(wrapper, 'Reload saved settings');
    expect(field(wrapper, 'Name').element.value).toBe('Unsaved name');
    await submitWith(wrapper, 'Save name');
    expect(saveName).toHaveBeenCalledTimes(1);
    settings.mockReturnValueOnce(fresh.promise);
    await click(wrapper, 'Reload saved settings');
    expect(button(wrapper, 'Save name').element.disabled).toBe(true);
    await submitWith(wrapper, 'Save name');
    expect(saveName).toHaveBeenCalledTimes(1);
    fresh.resolve({ ...row, configurationRevision: revision });
    await flushPromises();
    expect(field(wrapper, 'Name').element.value).toBe(row.name);
    expect(button(wrapper, 'Save name').element.disabled).toBe(false);
    await field(wrapper, 'Name').setValue('Reviewed name');
    await submitWith(wrapper, 'Save name');
    expect(saveName.mock.calls[1]).toEqual([{ name: 'Reviewed name', expectedRevision: 2 }]);
    await click(wrapper, 'Rules');
    expect(wrapper.get('.rule-list input').element.value).toBe('Unsaved rule');
    expect(button(wrapper, 'Save rules').element.disabled).toBe(true);
    routing.mockRejectedValueOnce({ status: 503 });
    await click(wrapper, 'Explicitly replace with my draft');
    expect(button(wrapper, 'Save rules').element.disabled).toBe(true);
    const current = Promise.withResolvers();
    routing.mockReturnValueOnce(current.promise);
    await click(wrapper, 'Explicitly replace with my draft');
    expect(button(wrapper, 'Save rules').element.disabled).toBe(true);
    expect(put).not.toHaveBeenCalled();
    current.resolve(route(row.id, revision, [rule()]));
    await flushPromises();
    await click(wrapper, 'Save rules');
    expect(put.mock.calls[0][0]).toMatchObject({ expectedRevision: 3, rules: [{ name: 'Unsaved rule' }] });
});
