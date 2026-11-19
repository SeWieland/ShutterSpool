import { attemptDisabledClick, button, click, management, mountEditor } from './editor-fixture';
import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { clearPrivateAccess } from '../../app/lib/session';
import CollectionManager from '../../app/components/CollectionManager.vue';
import CollectionMove from '../../app/components/CollectionMove.vue';
import CollectionPicker from '../../app/components/CollectionPicker.vue';

function captures(get, post = vi.fn()) {
    management.captures = { byId: id => ({ get: () => get(id) }), collectionMoves: { post } };
    management.collections = { get: async () => ({ items: [{ id: 'new', name: 'Berlin' }] }), byId: () => ({ get: async () => ({ name: 'Berlin' }) }) };
}
async function target(wrapper) {
    await wrapper.get('input[type=search]').setValue('Berlin');
    await wrapper.get('input[type=search]').trigger('keydown.enter');
    await flushPromises();
    await click(wrapper, 'Berlin');
}
const nameField = wrapper => wrapper.get('input[placeholder=Berlin]');
async function submit(wrapper) { await wrapper.get('form').trigger('submit'); await flushPromises(); }
async function search(wrapper, value) {
    await wrapper.get('input[type=search]').setValue(value);
    await vi.advanceTimersByTimeAsync(200);
    await flushPromises();
}

test('Collection moves use reviewed revisions, count partial outcomes and retain failures for deliberate retry', async () => {
    let revision = 2;
    const post = vi.fn()
        .mockResolvedValueOnce({ items: [{ captureId: 'one', status: 'applied' }, { captureId: 'two', status: 'conflict' }] })
        .mockResolvedValueOnce({ items: [{ captureId: 'two', status: 'applied' }] });
    const get = vi.fn(id => ({ id, displayName: id, curation: { revision } }));
    captures(get, post);
    const wrapper = await mountEditor(CollectionMove, { ids: ['one', 'two'], online: true });
    await target(wrapper);
    await click(wrapper, 'Move 2 photographs');
    expect(post.mock.calls).toEqual([[{ targetCollectionId: 'new', items: [{ captureId: 'one', expectedRevision: 2 }, { captureId: 'two', expectedRevision: 2 }] }]]);
    expect(wrapper.text()).toMatch(/1 moved.*1 failed.*0 unknown/);
    expect(wrapper.emitted('completed')).toEqual([[['one']]]);
    await attemptDisabledClick(button(wrapper, 'Move 2 photographs'));
    expect(get.mock.calls).toEqual([['one'], ['two']]);
    expect(post).toHaveBeenCalledTimes(1);
    revision = 8;
    await wrapper.setProps({ ids: ['two'] });
    await click(wrapper, 'Check outcomes before retry');
    expect(post).toHaveBeenCalledTimes(1);
    await click(wrapper, 'Move photograph');
    expect(post.mock.calls[1]).toEqual([{ targetCollectionId: 'new', items: [{ captureId: 'two', expectedRevision: 8 }] }]);
});

test('a lost move response requires a complete successful reread before retry and never queues offline work', async () => {
    let revision = 4, unavailable = false;
    const post = vi.fn(async () => { revision++; throw { status: 503 }; });
    captures(id => { if (unavailable) throw { status: 503 }; return { id, curation: { revision } }; }, post);
    const wrapper = await mountEditor(CollectionMove, { ids: ['one'], online: true });
    await target(wrapper);
    await click(wrapper, 'Move photograph');
    unavailable = true;
    await click(wrapper, 'Check outcomes before retry');
    await click(wrapper, 'Move photograph');
    expect(post).toHaveBeenCalledTimes(1);
    unavailable = false;
    await click(wrapper, 'Check outcomes before retry');
    expect(wrapper.text()).toContain('checked');
    await wrapper.setProps({ online: false });
    await click(wrapper, 'Move photograph');
    await wrapper.setProps({ online: true });
    expect(post).toHaveBeenCalledTimes(1);
});

test('Collection management blocks uncertain writes until saved Collections are reread', async () => {
    const get = vi.fn().mockResolvedValue({ items: [{ id: 'fixed', name: 'ShutterSpool', revision: 1, isBuiltIn: true }] });
    const post = vi.fn().mockRejectedValue({ status: 503 });
    management.collections = { get, post };
    const wrapper = await mountEditor(CollectionManager);
    await nameField(wrapper).setValue('Berlin');
    await submit(wrapper);
    await submit(wrapper);
    expect(post).toHaveBeenCalledTimes(1);
    await click(wrapper, 'Check saved Collections');
    expect(wrapper.text()).toContain('ShutterSpool');
    get.mockRejectedValue({ status: 503 });
    await wrapper.get('input[type=search]').trigger('keydown.enter');
    await flushPromises();
    await submit(wrapper);
    expect(wrapper.text()).toContain('Unable to check saved Collections');
    expect(post).toHaveBeenCalledTimes(1);
});

test('Collection selector uses bounded search pages and exact-ID OR selection', async () => {
    const get = vi.fn().mockResolvedValue({ items: [{ id: 'two', name: 'Travel/Berlin' }], nextCursor: 'next' });
    management.collections = { get, byId: () => ({ get: async () => ({ name: 'Events/Berlin' }) }) };
    const wrapper = await mountEditor(CollectionPicker, { modelValue: ['one'], multiple: true });
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    await search(wrapper, 'Berlin');
    await click(wrapper, 'More Collections');
    await search(wrapper, 'Travel');
    expect(get.mock.calls.map(([config]) => config.queryParameters)).toEqual([
        { q: 'Berlin', limit: 8, cursor: undefined }, { q: 'Berlin', limit: 8, cursor: 'next' }, { q: 'Travel', limit: 8, cursor: undefined },
    ]);
    expect(wrapper.findAll('fieldset button').filter(node => node.text() === 'Travel/Berlin')).toHaveLength(1);
    await click(wrapper, 'Travel/Berlin');
    await wrapper.get('[aria-label="Remove Events/Berlin filter"]').trigger('click');
    expect(wrapper.emitted('update:modelValue')).toEqual([[['one', 'two']], [[]]]);
});

test('unchanged nested Collection rename preserves the canonical path', async () => {
    const put = vi.fn();
    const byId = vi.fn(() => ({ put }));
    management.collections = { byId, get: async () => ({ items: [{ id: 'event', name: 'Berlin', path: 'Events/Berlin', revision: 3 }] }) };
    const wrapper = await mountEditor(CollectionManager);
    await click(wrapper, 'Events/Berlin');
    expect(nameField(wrapper).element.value).toBe('Events/Berlin');
    await submit(wrapper);
    expect(byId).toHaveBeenCalledWith('event');
    expect(put.mock.calls).toEqual([[{ expectedRevision: 3, name: 'Events/Berlin' }]]);
});

test('selected Collections distinguish equal short names using their canonical paths', async () => {
    const paths = { event: 'Events/Berlin', travel: 'Travel/Berlin' };
    management.collections = { byId: id => ({ get: async () => ({ id, name: 'Berlin', path: paths[id] }) }) };
    const wrapper = await mountEditor(CollectionPicker, { modelValue: ['event', 'travel'], multiple: true });
    expect(wrapper.get('[aria-label="Remove Travel/Berlin filter"]').text()).toContain('Travel/Berlin');
    await wrapper.get('[aria-label="Remove Events/Berlin filter"]').trigger('click');
    expect(wrapper.emitted('update:modelValue')).toEqual([[['travel']]]);
});

test('lost create responses for long Collection names recover through bounded paged search without losing the draft', async () => {
    const draft = '/Events/' + 'B'.repeat(180);
    const saved = [];
    const get = vi.fn(async ({ queryParameters: { q } }) => {
        if (q?.length > 128) throw { status: 400 };
        return { items: saved, nextCursor: 'second-page' };
    });
    const post = vi.fn(async ({ name }) => { saved.push({ id: 'committed', path: name }); throw { status: 503 }; });
    management.collections = { get, post };
    const wrapper = await mountEditor(CollectionManager);
    await nameField(wrapper).setValue(draft);
    await submit(wrapper);
    await submit(wrapper);
    await click(wrapper, 'Check saved Collections');
    expect(nameField(wrapper).element.value).toBe(draft);
    expect(wrapper.text()).toContain(draft);
    const query = get.mock.lastCall[0].queryParameters.q;
    expect(query.length).toBeGreaterThan(0);
    expect(query.length).toBeLessThanOrEqual(128);
    expect(draft.slice(1).startsWith(query)).toBe(true);
    await click(wrapper, 'More Collections');
    expect(get.mock.lastCall[0].queryParameters).toMatchObject({ q: query, cursor: 'second-page' });
    expect(post.mock.calls).toEqual([[{ name: draft }]]);
});

test('collection name rejection retains an editable draft while revision conflicts require review', async () => {
    for (const [status, errorCode, rename] of [[400], [409], [409, 'COLLECTION_NAME_CONFLICT', true], [409, 'COLLECTION_REVISION_CONFLICT', true]]) {
        const save = vi.fn().mockRejectedValue({ status, errorCode });
        management.collections = { post: save, byId: () => ({ put: save }), get: async () => ({ items: [{ id: 'one', revision: 2, name: 'Old' }] }) };
        const wrapper = await mountEditor(CollectionManager);
        if (rename) await click(wrapper, 'Old');
        await nameField(wrapper).setValue('Berlin');
        await submit(wrapper);
        expect(nameField(wrapper).element.value).toBe('Berlin');
        expect(nameField(wrapper).element.disabled).toBe(errorCode === 'COLLECTION_REVISION_CONFLICT');
        expect(save).toHaveBeenCalledTimes(1);
        wrapper.unmount();
    }
});

test('typing collection search fetches bounded suggestions and ignores older responses during debounce', async () => {
    const first = Promise.withResolvers(), second = Promise.withResolvers();
    const get = vi.fn().mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    management.collections = { get };
    const wrapper = await mountEditor(CollectionPicker, { modelValue: [] });
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    await search(wrapper, 'Ber');
    await wrapper.get('input').setValue('Berlin');
    first.resolve({ items: [{ id: 'old', name: 'Bergen' }] });
    await flushPromises();
    expect(wrapper.text()).not.toContain('Bergen');
    expect(get).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(200);
    expect(get.mock.lastCall[0].queryParameters).toMatchObject({ q: 'Berlin', limit: 8 });
    second.resolve({ items: [{ id: 'new', name: 'Berlin' }] });
    await flushPromises();
    expect(button(wrapper, 'Berlin').exists()).toBe(true);
});

test('clear unused previews exact zero references, protects built-in collections and never blindly replays uncertain deletion', async () => {
    const rows = [
        { id: 'fixed', isBuiltIn: true, sourceReferences: 0, captureReferences: 0 },
        { id: 'source', sourceReferences: 1, captureReferences: 0 },
        { id: 'photo', sourceReferences: 0, captureReferences: 1 }, { id: 'unknown' },
        ...['unused', 'raced', 'uncertain', 'later'].map(id => ({ id, sourceReferences: 0, captureReferences: 0 })),
    ].map((item, index) => ({ ...item, name: item.id, revision: index + 1 }));
    const remove = vi.fn(async (id, revision) => { if (id === 'raced') throw { status: 409 }; if (id === 'uncertain') throw { status: 503 }; });
    const get = vi.fn().mockResolvedValue({ items: rows });
    management.collections = { get, byId: id => ({ delete: request => remove(id, request.queryParameters.expectedRevision) }) };
    const wrapper = await mountEditor(CollectionManager);
    await click(wrapper, 'Clear unused');
    expect(wrapper.findAll('.notice li').map(node => node.text())).toEqual(['unused', 'raced', 'uncertain', 'later']);
    expect(get.mock.lastCall[0].queryParameters.q).toBeUndefined();
    expect(remove).not.toHaveBeenCalled();
    await click(wrapper, 'Delete 4 unused Collections');
    expect(remove.mock.calls).toEqual([['unused', 5], ['raced', 6], ['uncertain', 7]]);
    expect(wrapper.text()).toMatch(/1 deleted.*1 skipped.*uncertain/i);
    expect(button(wrapper, 'Check saved Collections').element.disabled).toBe(false);
    const reads = get.mock.calls.length;
    await attemptDisabledClick(button(wrapper, 'Clear unused'));
    expect(get).toHaveBeenCalledTimes(reads);
    expect(remove).toHaveBeenCalledTimes(3);
});

test('a single photograph move reads its revision and applies once without a separate review action', async () => {
    const get = vi.fn(id => ({ id, curation: { revision: 9 } }));
    const post = vi.fn().mockResolvedValue({ items: [{ captureId: 'one', status: 'applied' }] });
    captures(get, post);
    const wrapper = await mountEditor(CollectionMove, { ids: ['one'], online: true });
    await target(wrapper);
    await click(wrapper, 'Move photograph');
    expect(get.mock.calls).toEqual([['one']]);
    expect(post.mock.calls).toEqual([[{ targetCollectionId: 'new', items: [{ captureId: 'one', expectedRevision: 9 }] }]]);
});

test('clear unused streams past referenced pages and caps the confirmed candidate batch', async () => {
    const get = vi.fn(async ({ queryParameters: { cursor, limit } }) => {
        const page = Number(cursor ?? 0);
        return { items: Array.from({ length: limit }, (_, i) => ({ id: `${page}-${i}`, name: `Collection ${page}-${i}`, revision: 1, sourceReferences: page < 4 ? 1 : 0, captureReferences: 0 })), nextCursor: String(page + 1) };
    });
    management.collections = { get };
    const wrapper = await mountEditor(CollectionManager);
    get.mockClear();
    await click(wrapper, 'Clear unused');
    expect(wrapper.findAll('.notice li')).toHaveLength(100);
    expect(wrapper.text()).toContain('more may remain');
    expect(get).toHaveBeenCalledTimes(8);
    expect(get.mock.lastCall[0].queryParameters.limit).toBe(10);
});

test('searching managed collections retains the edited name and rejects stale search results', async () => {
    const first = Promise.withResolvers(), second = Promise.withResolvers();
    const get = vi.fn().mockResolvedValueOnce({ items: [{ id: 'one', name: 'Old', revision: 1 }] }).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    management.collections = { get };
    const wrapper = await mountEditor(CollectionManager);
    await click(wrapper, 'Old');
    await nameField(wrapper).setValue('Draft');
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    await search(wrapper, 'Ber');
    await search(wrapper, 'Berlin');
    second.resolve({ items: [{ id: 'new', name: 'Berlin' }] });
    await flushPromises();
    first.resolve({ items: [{ id: 'old', name: 'Bergen' }] });
    await flushPromises();
    expect(wrapper.text()).toContain('Berlin');
    expect(wrapper.text()).not.toContain('Bergen');
    expect(nameField(wrapper).element.value).toBe('Draft');
    expect(button(wrapper, 'Rename').exists()).toBe(true);
});

test('a repeated cleanup cursor cannot expose a partial confirmation', async () => {
    const remove = vi.fn();
    management.collections = { get: async () => ({ items: [{ id: 'one', revision: 1, sourceReferences: 0, captureReferences: 0 }], nextCursor: 'same' }), byId: () => ({ delete: remove }) };
    const wrapper = await mountEditor(CollectionManager);
    await click(wrapper, 'Clear unused');
    expect(wrapper.text()).toContain('Unable to check unused Collections');
    expect(wrapper.findAll('button').some(node => /^Delete \d+ unused/.test(node.text()))).toBe(false);
    expect(remove).not.toHaveBeenCalled();
});

test('collection searches coalesce keystrokes and cancel pending reads on disposal', async () => {
    for (const component of [CollectionPicker, CollectionManager]) {
        for (const dispose of ['unmount', 'access']) {
            const get = vi.fn().mockResolvedValue({ items: [] });
            management.collections = { get };
            const wrapper = await mountEditor(component, component === CollectionPicker ? { modelValue: [] } : {});
            get.mockClear();
            vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
            const field = wrapper.get('input[type=search]');
            for (const q of ['B', 'Ber', 'Berlin']) await field.setValue(q);
            expect(get).not.toHaveBeenCalled();
            await vi.advanceTimersByTimeAsync(200);
            expect(get.mock.calls.map(([config]) => config.queryParameters.q)).toEqual(['Berlin']);
            await field.setValue('Travel');
            if (dispose === 'access') clearPrivateAccess();
            else wrapper.unmount();
            await vi.advanceTimersByTimeAsync(200);
            expect(get).toHaveBeenCalledTimes(1);
            if (dispose === 'access') wrapper.unmount();
            vi.useRealTimers();
        }
    }
});

test('collection mutations cancel queued search reads before changing recovery state', async () => {
    for (const action of ['create', 'delete', 'cleanup']) {
        const row = { id: 'one', name: 'Berlin', revision: 1, sourceReferences: 0, captureReferences: 0 };
        const get = vi.fn().mockResolvedValue({ items: [row] });
        const save = vi.fn().mockRejectedValue({ status: 503 });
        management.collections = { get, post: save, byId: () => ({ delete: save }) };
        const wrapper = await mountEditor(CollectionManager);
        if (action === 'delete') await click(wrapper, 'Berlin');
        if (action === 'cleanup') await click(wrapper, 'Clear unused');
        if (action === 'create') await nameField(wrapper).setValue('Berlin');
        get.mockClear();
        vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
        await wrapper.get('input[type=search]').setValue('Berlin');
        if (action === 'create') await submit(wrapper);
        else await click(wrapper, action === 'delete' ? 'Delete Collection' : 'Delete 1 unused Collections');
        await vi.advanceTimersByTimeAsync(200);
        expect(get).not.toHaveBeenCalled();
        expect(save).toHaveBeenCalledTimes(1);
        expect(button(wrapper, 'Check saved Collections').exists()).toBe(true);
        wrapper.unmount();
        vi.useRealTimers();
    }
});

test('bulk move never submits a partial selection after a failed or missing authoritative read', async () => {
    for (const status of [404, 503]) {
        const post = vi.fn();
        captures(id => { if (id === 'two') throw { status }; return { id, curation: { revision: 3 } }; }, post);
        const wrapper = await mountEditor(CollectionMove, { ids: ['one', 'two'], online: true });
        await target(wrapper);
        await click(wrapper, 'Move 2 photographs');
        expect(wrapper.findAll('.collection-move > ul li')).toHaveLength(0);
        if (status === 404) expect(wrapper.text()).toMatch(/photograph 2.*unavailable/i);
        expect(button(wrapper, 'Move 2 photographs').element.disabled).toBe(true);
        await click(wrapper, 'Move 2 photographs');
        expect(post).not.toHaveBeenCalled();
        wrapper.unmount();
    }
});

test('bulk move enforces the 100-selection limit and online requirement before any read', async () => {
    for (const props of [{ ids: Array.from({ length: 101 }, (_, i) => String(i)), online: true }, { ids: ['one', 'two'], online: false }]) {
        const get = vi.fn(), post = vi.fn();
        captures(get, post);
        const wrapper = await mountEditor(CollectionMove, { ids: ['one'], online: true });
        await target(wrapper);
        await wrapper.setProps(props);
        await click(wrapper, `Move ${props.ids.length} photographs`);
        expect(get).not.toHaveBeenCalled();
        expect(post).not.toHaveBeenCalled();
        wrapper.unmount();
    }
});
