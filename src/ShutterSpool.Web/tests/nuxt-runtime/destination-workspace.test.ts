import { attemptDisabledClick, button, click, management, mountEditor } from './editor-fixture';
import { destination, destinations, field, openDestination, route, rule, submitWith } from './editor-owners';
import DeliveryWork from '../../app/components/DeliveryWork.vue';
import { flushPromises } from '@vue/test-utils';
import { afterEach, expect, test, vi } from 'vitest';

const setupKey = 'shutterspool.destinationSetup::';
afterEach(() => localStorage.removeItem(setupKey));
function incomplete(id) { localStorage.setItem(setupKey, JSON.stringify([id])); }
async function settings(wrapper, id) {
    await openDestination(wrapper, id);
    await click(wrapper, 'Settings');
}

test('Delivery displays one Capture for confirmed RAW/JPEG work and marks unavailable reads as last known', async () => {
    const id = 'destination';
    const work = vi.fn().mockResolvedValueOnce({ items: [
        { id: 'raw', captureId: 'pair', state: 'Delivered' }, { id: 'jpeg', captureId: 'pair', state: 'Delivered' },
        { id: 'other', captureId: 'pending', state: 'Running' },
    ] }).mockRejectedValueOnce({ status: 503 });
    const wrapper = await destinations([destination(id)], { endpoint: () => ({ work: { get: work } }) });
    expect(wrapper.findAll('authorized-preview-stub').map(el => el.attributes('captureid'))).toEqual(['pair']);
    await wrapper.setProps({ generation: 1 });
    await flushPromises();
    expect(wrapper.get('.destination-status').classes()).toContain('status-neutral');
    expect(wrapper.get('.destination-status').attributes('aria-label')).toContain('last known');
    expect(wrapper.findAll('authorized-preview-stub')).toHaveLength(1);
});

test('a writable provider test cannot complete setup after a concurrent saved revision change', async () => {
    const id = 'new'; incomplete(id);
    const first = Promise.withResolvers(), second = Promise.withResolvers();
    let revision = 1, unavailable = false;
    const get = vi.fn(async () => { if (unavailable) throw { status: 503 }; return destination(id, 'local', revision); });
    const provider = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const wrapper = await destinations([destination(id)], { endpoint: () => ({ get, routing: { get: () => route(id, revision) }, local: { test: { post: provider } } }) });
    await click(wrapper, 'Continue destination setup');
    await click(wrapper, 'Test saved settings');
    revision = 2;
    first.resolve({ writable: true, testedConfigurationRevision: 1 });
    await flushPromises();
    expect(wrapper.text()).toMatch(/test the current revision/i);
    expect(button(wrapper, 'Rules').element.disabled).toBe(true);
    expect(localStorage.getItem(setupKey)).toBe('["new"]');
    await click(wrapper, 'Test saved settings');
    unavailable = true;
    second.resolve({ writable: true, testedConfigurationRevision: 2 });
    await flushPromises();
    expect(button(wrapper, 'Rules').element.disabled).toBe(true);
    expect(localStorage.getItem(setupKey)).toBe('["new"]');
    unavailable = false;
    provider.mockResolvedValueOnce({ writable: true, testedConfigurationRevision: 2 });
    await click(wrapper, 'Test saved settings');
    expect(button(wrapper, 'Rules').element.disabled).toBe(false);
    expect(localStorage.getItem(setupKey)).toBe('[]');
});

test('setup recovers a failed initial disable before saving provider settings with the new revision', async () => {
    const id = 'new';
    let revision = 0, enabled = true, rejectDisable = true, path = '/photos';
    let states = [];
    const calls = [];
    const wrapper = await destinations([destination(id)], { create: async () => ({ id }), endpoint: () => ({
        get: () => ({ ...destination(id, 'local', revision), localRootPath: path, enabled }),
        work: { get: () => ({ items: states.map(state => ({ state })) }) },
        routing: { get: () => ({ ...route(id, revision), enabled }), put: async body => {
            calls.push(['disable', body.expectedRevision]);
            expect(body.enabled).toBe(false);
            if (rejectDisable) throw { status: 503 };
            enabled = false;
            return route(id, ++revision);
        } },
        local: { put: body => { calls.push(['provider', body.expectedRevision]); expect(enabled).toBe(false); path = body.rootPath; return { configurationRevision: ++revision }; },
            test: { post: () => { calls.push(['test', revision]); return { writable: true, testedConfigurationRevision: revision }; } } },
    }) });
    await field(wrapper, 'Name').setValue('New');
    await submitWith(wrapper, 'Create destination');
    expect(calls).toEqual([['disable', 0]]);
    expect(button(wrapper, 'Rules').element.disabled).toBe(true);
    rejectDisable = false;
    await click(wrapper, 'Reload saved settings');
    await field(wrapper, 'Destination folder').setValue('/new-root');
    await submitWith(wrapper, 'Save and test folder');
    expect(calls).toEqual([['disable', 0], ['disable', 0], ['provider', 1], ['test', 2]]);
    expect(localStorage.getItem(setupKey)).toBe('[]');
    let generation = 0;
    for (const [rows, status] of [[[], 'neutral'], [['Delivered'], 'success'], [['Failed', 'Delivered'], 'error'], [['Delivered', 'Failed'], 'warning'], [['Ambiguous'], 'warning'], [['Running'], 'neutral']]) {
        states = rows;
        await wrapper.setProps({ generation: ++generation });
        await flushPromises();
        expect(wrapper.get('.destination-status').classes()).toContain('status-' + status);
    }
});

test('local setup saves the chosen folder before testing and keeps rejected folder drafts', async () => {
    const id = 'local';
    let saved = null, revision = 0;
    const calls = [];
    const wrapper = await destinations([destination(id)], { endpoint: () => ({
        get: () => ({ ...destination(id, 'local', revision), localRootPath: saved }),
        routing: { get: () => route(id, revision) },
        local: { put: async body => {
            calls.push(['save', body.rootPath, body.expectedRevision]);
            if (body.rootPath === '/missing') throw { status: 400, detail: 'Choose an existing folder.' };
            saved = body.rootPath; return { configurationRevision: ++revision };
        }, test: { post: () => { calls.push(['test', saved]); return { writable: true, testedConfigurationRevision: revision }; } } },
    }) });
    await settings(wrapper, id);
    await attemptDisabledClick(button(wrapper, 'Test saved settings'));
    await field(wrapper, 'Destination folder').setValue('/photos');
    await attemptDisabledClick(button(wrapper, 'Test saved settings'));
    expect(calls).toEqual([]);
    await field(wrapper, 'Name').setValue('Unsaved name');
    await submitWith(wrapper, 'Save and test folder');
    expect(calls).toEqual([['save', '/photos', 0], ['test', '/photos']]);
    expect(field(wrapper, 'Name').element.value).toBe('Unsaved name');
    expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
    await field(wrapper, 'Destination folder').setValue('/missing');
    await submitWith(wrapper, 'Save and test folder');
    expect(calls.at(-1)).toEqual(['save', '/missing', 1]);
    expect(calls).toHaveLength(3);
    expect(field(wrapper, 'Destination folder').element.value).toBe('/missing');
    expect(wrapper.text()).toContain('Choose an existing folder.');
});

test('Retry is available only in the server-supported failure states', async () => {
    const post = vi.fn();
    management.deliveries = { byId: () => ({ retry: { post } }) };
    const work = { id: 'work', assetId: 'asset', destinationId: 'destination' };
    const wrapper = await mountEditor(DeliveryWork, { work: { ...work, state: 'Pending' }, online: true });
    for (const state of ['Pending', 'Running', 'Delivered', 'Ambiguous', 'Superseded']) {
        await wrapper.setProps({ work: { ...work, state } });
        expect(wrapper.findAll('button').some(item => item.text() === 'Retry')).toBe(false);
    }
    expect(post).not.toHaveBeenCalled();
    for (const state of ['RetryWait', 'Failed', 'Disabled']) {
        await wrapper.setProps({ work: { ...work, state } });
        expect(button(wrapper, 'Retry').element.disabled).toBe(false);
    }
});

test('incomplete setup blocks rules and needs a successful test of the unchanged saved revision', async () => {
    const id = 'new'; incomplete(id);
    let writable = false, config = { ...destination(id), localRootPath: '/photos' };
    const put = vi.fn(), provider = vi.fn(() => ({ writable, testedConfigurationRevision: 1 }));
    let template = '{year}/{month}';
    const wrapper = await destinations([config], { endpoint: () => ({ get: () => config, routing: { get: () => route(id, 1, [{ ...rule(), folderTemplate: template }]), put }, local: { test: { post: provider } } }) });
    await click(wrapper, 'Continue destination setup');
    await click(wrapper, 'Rules');
    expect(button(wrapper, 'Settings').attributes('aria-current')).toBe('page');
    expect(put).not.toHaveBeenCalled();
    await click(wrapper, 'Test saved settings');
    expect(localStorage.getItem(setupKey)).toBe('["new"]');
    writable = true;
    await field(wrapper, 'Destination folder').setValue('/draft');
    await click(wrapper, 'Test saved settings');
    expect(provider).toHaveBeenCalledTimes(1);
    expect(button(wrapper, 'Rules').element.disabled).toBe(true);
    await click(wrapper, 'Continue destination setup');
    await click(wrapper, 'Discard changes');
    await click(wrapper, 'Test saved settings');
    expect(localStorage.getItem(setupKey)).toBe('[]');
    await click(wrapper, 'Rules');
    const renderedPath = () => wrapper.get('.rule-example br').element.nextSibling.textContent;
    expect(renderedPath()).toBe('/photos/2026/10/IMG_0001.jpg');
    template = '';
    let generation = 0;
    for (const [base, prefix] of [['', '(root not configured)'], ['/', ''], ['C:\\photos\\', 'C:\\photos'], ['\\\\server\\share/\\', '\\\\server\\share'], ['/photos/a b/ ', '/photos/a b/ ']]) {
        config = { ...config, localRootPath: base };
        await wrapper.setProps({ generation: ++generation });
        await flushPromises();
        expect(renderedPath()).toBe(prefix + '/IMG_0001.jpg');
    }
    config = { ...config, kind: 'webdav', webDavCollectionUrl: 'https://example.invalid/dav/photos///' };
    await wrapper.setProps({ generation: ++generation });
    await flushPromises();
    expect(renderedPath()).toBe('https://example.invalid/dav/photos/IMG_0001.jpg');
});

test('destination switches clear every owner-scoped draft before a delayed or failed read and block writes', async () => {
    const rows = [destination('alpha', 'webdav'), destination('beta', 'webdav')], failed = Promise.withResolvers();
    const put = vi.fn();
    const get = vi.fn().mockReturnValueOnce(failed.promise).mockResolvedValue(rows[1]);
    const wrapper = await destinations(rows, { endpoint: id => ({ get: id === 'beta' ? get : () => rows[0],
        routing: { get: () => route('alpha', 1, [rule('Alpha rule')]), put }, webdav: { put },
        work: { get: () => ({ items: [{ id: 'old-work', state: 'Failed', targetPath: '/alpha-work' }] }) },
    }) });
    await openDestination(wrapper, 'alpha');
    expect(wrapper.get('.rule-list input').element.value).toBe('Alpha rule');
    await wrapper.get('.rule-list input').setValue('Alpha draft');
    const oldRules = button(wrapper, 'Save rules').element;
    await click(wrapper, 'Settings');
    await field(wrapper, 'Username').setValue('Alpha username');
    const oldProviderForm = button(wrapper, 'Save provider settings').element.closest('form');
    await click(wrapper, 'Work');
    expect(wrapper.text()).toContain('/alpha-work');
    await openDestination(wrapper, 'beta');
    async function checkLocked() {
        expect(wrapper.text()).not.toContain('/alpha-work');
        expect(wrapper.find('.rule-list input').exists()).toBe(false);
        expect(wrapper.find('.destination-settings').exists()).toBe(false);
        expect(button(wrapper, 'Rules').element.disabled).toBe(true);
        await click(wrapper, 'Settings');
        oldRules.dispatchEvent(new Event('click', { bubbles: true }));
        oldProviderForm.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
        await flushPromises();
        expect(put).not.toHaveBeenCalled();
    }
    await checkLocked();
    failed.resolve(undefined);
    await flushPromises();
    await click(wrapper, 'Retry read');
    await checkLocked();
    expect(get).toHaveBeenCalledTimes(2);
});

test('a delayed provider save cannot clear a newer destination password or settings draft', async () => {
    const rows = [destination('alpha', 'webdav'), destination('beta', 'webdav')];
    const gate = Promise.withResolvers(), put = vi.fn(() => gate.promise);
    const wrapper = await destinations(rows, { endpoint: () => ({ webdav: { put } }) });
    await settings(wrapper, 'alpha');
    await field(wrapper, 'Replace secret').setValue('synthetic-a');
    await submitWith(wrapper, 'Save provider settings');
    expect(put).toHaveBeenCalledTimes(1);
    await settings(wrapper, 'beta');
    await field(wrapper, 'Replace secret').setValue('synthetic-b');
    gate.resolve({ configurationRevision: 2 });
    await flushPromises();
    expect(field(wrapper, 'Replace secret').element.value).toBe('synthetic-b');
    expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
    expect(wrapper.text()).not.toContain('Saved settings updated.');
});

test('destination name, provider and toggle saves preserve unrelated settings drafts', async () => {
    for (const action of ['name', 'provider', 'toggle']) {
        let row = destination('same', 'webdav'), enabled = false;
        const calls = [];
        const wrapper = await destinations([row], { endpoint: () => ({
            get: () => row, put: body => { calls.push('name'); row = { ...row, name: body.name, configurationRevision: 2 }; return row; },
            webdav: { put: body => { calls.push('provider'); row = { ...row, webDavCollectionUrl: body.collectionUrl, configurationRevision: 2 }; return row; } },
            routing: { get: () => ({ ...route(row.id, row.configurationRevision), enabled }), put: body => { calls.push('toggle'); enabled = body.enabled; row = { ...row, configurationRevision: 2 }; return { ...route(row.id, 2), enabled }; } },
        }) });
        await settings(wrapper, 'same');
        await field(wrapper, 'Name').setValue('Name draft');
        await field(wrapper, 'WebDAV collection URL').setValue('https://example.invalid/draft');
        if (action === 'name') await submitWith(wrapper, 'Save name');
        else if (action === 'provider') await submitWith(wrapper, 'Save provider settings');
        else await click(wrapper, 'Enable destination');
        expect(calls).toEqual([action]);
        expect(field(wrapper, 'Name').element.value).toBe('Name draft');
        expect(field(wrapper, 'WebDAV collection URL').element.value).toBe('https://example.invalid/draft');
        expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
        expect(enabled).toBe(action === 'toggle');
        row = { ...row, configurationRevision: 3, name: 'Updated name', webDavCollectionUrl: 'https://example.invalid/updated' };
        await wrapper.setProps({ generation: 1 });
        await flushPromises();
        expect(field(wrapper, 'Name').element.value).toBe(action === 'name' ? 'Updated name' : 'Name draft');
        expect(field(wrapper, 'WebDAV collection URL').element.value).toBe(action === 'provider' ? 'https://example.invalid/updated' : 'https://example.invalid/draft');
        expect(calls).toEqual([action]);
        wrapper.unmount();
    }
});

test('same-destination reopen isolates delayed settings and rule outcomes on success and failure', async () => {
    for (const action of ['settings', 'rules']) for (const failed of [false, true]) {
        const row = destination('same', 'webdav'), gate = Promise.withResolvers(), put = vi.fn(() => gate.promise);
        const wrapper = await destinations([row], { endpoint: () => ({ routing: { get: () => route(row.id, 1, [rule()]), put }, webdav: { put } }) });
        async function visit() {
            await openDestination(wrapper, row.id);
            if (action === 'settings') await click(wrapper, 'Settings');
        }
        const draft = () => action === 'settings' ? field(wrapper, 'Username') : wrapper.get('.rule-list input');
        await visit();
        await draft().setValue('Old visit');
        if (action === 'settings') await submitWith(wrapper, 'Save provider settings');
        else await click(wrapper, 'Save rules');
        expect(put).toHaveBeenCalledTimes(1);
        await wrapper.get('[aria-label="New destination"]').trigger('click');
        await click(wrapper, 'Discard changes');
        await visit();
        await draft().setValue('New visit');
        const feedback = wrapper.findAll('[role=status]').map(node => node.text());
        if (failed) gate.reject({ status: 503 });
        else gate.resolve({ ...route(row.id, 2), configurationRevision: 2 });
        await flushPromises();
        expect(draft().element.value).toBe('New visit');
        expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
        expect(wrapper.findAll('[role=status]').map(node => node.text())).toEqual(feedback);
        wrapper.unmount();
    }
});

test('selecting Work reads its page once through the tab watcher', async () => {
    const work = vi.fn(() => ({ items: [] }));
    const wrapper = await destinations([destination('same')], { endpoint: () => ({ work: { get: work } }) });
    await openDestination(wrapper, 'same');
    work.mockClear();
    await click(wrapper, 'Work');
    expect(work).toHaveBeenCalledTimes(1);
});

test('creating a dirty destination draft opens its saved editor even when browser setup storage is unavailable', async () => {
    const storage = localStorage;
    const setItem = vi.fn(() => { throw new Error('Synthetic storage unavailable'); });
    vi.stubGlobal('localStorage', { getItem: () => null, setItem, removeItem: vi.fn() });
    const id = 'created', create = vi.fn(() => ({ id }));
    const wrapper = await destinations([], { create, endpoint: () => ({ get: () => destination(id), routing: { get: () => route(id) } }) });
    await field(wrapper, 'Name').setValue('New archive');
    expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
    await submitWith(wrapper, 'Create destination');
    expect(create.mock.calls[0][0]).toEqual({ name: 'New archive', kind: 'local' });
    expect(wrapper.get('.destination-editor h2').text()).toContain(id);
    expect(button(wrapper, 'Rules').element.disabled).toBe(true);
    expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([false]);
    expect(setItem).toHaveBeenCalledWith(setupKey, '["created"]');
    expect(storage.getItem(setupKey)).toBeNull();
});
