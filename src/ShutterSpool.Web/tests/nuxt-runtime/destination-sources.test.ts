import { button, click, management, mountEditor } from './editor-fixture';
import { field, openSource, source, sources, submitWith } from './editor-owners';
import { clearPrivateAccess } from '../../app/lib/session';
import LocalFolderPicker from '../../app/components/LocalFolderPicker.vue';
import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';

test('folder picker selects only a successfully read folder and fences stale reads and access loss', async () => {
    const reads = [];
    management.destinations = { localFolders: { get: config => { const gate = Promise.withResolvers(); reads.push({ ...gate, config }); return gate.promise; } } };
    const wrapper = await mountEditor(LocalFolderPicker, { online: true, initialPath: '/old' });
    await wrapper.setProps({ online: false });
    await wrapper.setProps({ online: true });
    const form = wrapper.get<HTMLFormElement>('form');
    const go = form.get<HTMLButtonElement>('button');
    expect(go.attributes('type')).toBe('submit');
    const submitted = vi.fn();
    form.element.addEventListener('submit', submitted);
    const browse = async path => { await wrapper.get('input').setValue(path); await form.trigger('submit'); };
    await wrapper.get('input').setValue('/photos');
    go.element.click();
    expect(submitted).toHaveBeenCalledTimes(1);
    expect(reads.map(read => read.config.queryParameters.path)).toEqual(['/old', '/photos']);
    expect(reads[0].config.options[0].signal.aborted).toBe(true);
    reads[0].resolve({ path: '/old', directories: [] });
    await flushPromises();
    expect(wrapper.get('.folder-list').attributes('aria-busy')).toBe('true');
    expect(go.element.disabled).toBe(true);
    expect(button(wrapper, 'Use this folder').element.disabled).toBe(true);
    reads[1].resolve({ path: '/photos', directories: [] });
    await flushPromises();
    await click(wrapper, 'Use this folder');
    expect(wrapper.emitted('select')).toEqual([['/photos']]);
    expect(submitted).toHaveBeenCalledTimes(1);
    await browse('/missing');
    expect(submitted).toHaveBeenCalledTimes(2);
    expect(reads.map(read => read.config.queryParameters.path)).toEqual(['/old', '/photos', '/missing']);
    await click(wrapper, 'Use this folder');
    reads[2].reject({ detail: 'This folder no longer exists.' });
    await flushPromises();
    expect(wrapper.get('[role=alert]').text()).toContain('no longer exists');
    await click(wrapper, 'Use this folder');
    await browse('/private');
    clearPrivateAccess();
    reads[3].resolve({ path: '/private', directories: [] });
    await flushPromises();
    expect(wrapper.emitted('select')).toEqual([['/photos']]);
    expect(wrapper.emitted('close')).toBeDefined();
    expect(wrapper.text()).not.toContain('/private');
});

test('Source profiles load saved assignments and submit an explicit revision-guarded draft choice', async () => {
    let saved = { ...source('camera', 7), connectionProfile: 'LegacyFtps' };
    const create = vi.fn().mockResolvedValue({}), put = vi.fn(body => ({ ...saved, ...body }));
    const wrapper = await sources([saved], { create, endpoint: () => ({ get: async () => saved, put }) });
    await field(wrapper, 'Name').setValue('New camera');
    await submitWith(wrapper, 'Create Source');
    expect(create.mock.calls[0][0].connectionProfile).toBe('ModernFtps');
    await openSource(wrapper, 'camera');
    await click(wrapper, 'Security');
    const profile = value => wrapper.get('input[name=source-profile][value=' + value + ']');
    expect(profile('LegacyFtps').element.checked).toBe(true);
    await profile('ModernFtps').setValue();
    saved = { ...saved, configurationRevision: 8 };
    await wrapper.setProps({ sources: [saved] });
    expect(profile('ModernFtps').element.checked).toBe(true);
    expect(button(wrapper, 'Save settings').element.disabled).toBe(true);
    await submitWith(wrapper, 'Save settings');
    expect(put).not.toHaveBeenCalled();
    await click(wrapper, 'Reload saved settings');
    await profile('ModernFtps').setValue();
    await submitWith(wrapper, 'Save settings');
    await profile('LegacyFtps').setValue();
    await submitWith(wrapper, 'Save settings');
    expect(put.mock.calls.map(([body]) => [body.expectedRevision, body.connectionProfile])).toEqual([[8, 'ModernFtps'], [8, 'LegacyFtps']]);
    await click(wrapper, 'New Source');
    expect(profile('ModernFtps').element.checked).toBe(true);
});

test('delayed Source commands and removals preserve a newer owner draft on success and failure', async () => {
    for (const action of ['create', 'save', 'secret', 'remove']) for (const failed of [false, true]) {
        const gate = Promise.withResolvers(), request = vi.fn(() => gate.promise);
        const rows = [source('a'), source('b')];
        const wrapper = await sources(rows, { create: request, endpoint: () => ({ put: request, delete: request, regenerateSecret: { post: request } }) });
        if (action !== 'create') await openSource(wrapper, 'a');
        if (action === 'create' || action === 'save') {
            await field(wrapper, 'Name').setValue('Earlier draft');
            await submitWith(wrapper, action === 'create' ? 'Create Source' : 'Save settings');
        } else if (action === 'secret') {
            await click(wrapper, 'Connection');
            await click(wrapper, 'Replace credentials');
            await click(wrapper, 'Replace credentials...');
            await click(wrapper, 'Confirm replacement');
        } else {
            await click(wrapper, 'Delete Source');
            await click(wrapper, 'Confirm removal');
        }
        expect(request).toHaveBeenCalledTimes(1);
        await openSource(wrapper, 'b');
        if (action === 'secret' && failed) await openSource(wrapper, 'a');
        await click(wrapper, 'General');
        await field(wrapper, 'Name').setValue('Current draft');
        const feedback = wrapper.findAll('.settings-panel > p[role=status]').map(node => node.text());
        if (failed) gate.reject({ status: 409 });
        else gate.resolve(action === 'remove' ? { outcome: 'archived' } : { ...source('created'), current: rows[0], secret: 'synthetic-one-time' });
        await flushPromises();
        expect(wrapper.findAll('.settings-panel > p[role=status]').map(node => node.text())).toEqual(feedback);
        expect(field(wrapper, 'Name').element.value).toBe('Current draft');
        expect(wrapper.emitted('dirty-change')?.at(-1)).toEqual([true]);
        expect(wrapper.text()).not.toContain('synthetic-one-time');
        expect(wrapper.find('output.generated-password').exists()).toBe(false);
        expect(button(wrapper, 'Save settings').element.disabled).toBe(false);
        expect(wrapper.emitted('refresh')).toHaveLength(1);
        wrapper.unmount();
    }
});

test('normal Source creation retains its one-time credential after selecting the created owner', async () => {
    const created = source('created');
    const create = vi.fn().mockResolvedValue({ id: created.id, secret: 'synthetic-one-time' });
    const wrapper = await sources([], { create, endpoint: () => ({ get: async () => created }) });
    await field(wrapper, 'Name').setValue('Created');
    await submitWith(wrapper, 'Create Source');
    await wrapper.setProps({ sources: [created] });
    expect(wrapper.get('.source-summary').attributes('aria-pressed')).toBe('true');
    expect(wrapper.get('output.generated-password').text()).toBe('synthetic-one-time');
});

test('Source removal sends its confirmed revision and clears the one-time credential', async () => {
    const row = source('camera', 7), remove = vi.fn().mockResolvedValue({ outcome: 'archived' });
    const wrapper = await sources([row], { endpoint: () => ({ delete: remove, regenerateSecret: { post: async () => ({ current: row, secret: 'synthetic-one-time' }) } }) });
    await openSource(wrapper, row.id);
    await click(wrapper, 'Connection');
    await click(wrapper, 'Replace credentials');
    await click(wrapper, 'Replace credentials...');
    await click(wrapper, 'Confirm replacement');
    expect(wrapper.get('output.generated-password').text()).toBe('synthetic-one-time');
    await click(wrapper, 'General');
    await click(wrapper, 'Delete Source');
    expect(remove).not.toHaveBeenCalled();
    await click(wrapper, 'Confirm removal');
    expect(remove.mock.calls).toEqual([[{ queryParameters: { expectedRevision: 7 } }]]);
    expect(wrapper.text()).not.toContain('synthetic-one-time');
    expect(wrapper.text()).toContain('archived');
    expect(wrapper.emitted('refresh')).toHaveLength(2);
});

test('Source delay controls preserve exact seconds and convert whole weeks', async () => {
    const row = { ...source('camera'), curationMode: 'Delay', curationDelaySeconds: 1209600 };
    const wrapper = await sources([row]);
    await openSource(wrapper, row.id);
    expect(wrapper.get('[aria-label="Delay amount"]').element.value).toBe('2');
    expect(wrapper.get('[aria-label="Delay unit"]').element.value).toBe('604800');
    await wrapper.setProps({ sources: [{ ...row, configurationRevision: 2, curationDelaySeconds: 61 }] });
    expect(wrapper.get('[aria-label="Delay amount"]').element.value).toBe('61');
    expect(wrapper.get('[aria-label="Delay unit"]').element.value).toBe('1');
});
