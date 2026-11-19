import { flushPromises } from '@vue/test-utils';
import { nextTick } from 'vue';
import { expect, test, vi } from 'vitest';
import { session } from '../../app/lib/session';
import { defaultPreferences } from '../../app/lib/workspace-preferences';
import { buttonIn, loseAccess, mountWorkspace, photo, refresh, transport } from './workspace-mount';
import RetentionSettings from '../../app/components/RetentionSettings.vue';
import TrashWorkspace from '../../app/components/TrashWorkspace.vue';
const key = 'shutterspool-appearance-https://synthetic.example.test-operator';
function storage(initial?: string) {
    const records = new Map(initial === undefined ? [] : [[key, initial]]);
    const setItem = vi.fn((key: string, value: string) => { records.set(key, value); });
    const getItem = vi.fn((key: string) => records.get(key) ?? null);
    vi.stubGlobal('localStorage', { getItem, setItem });
    return { records, getItem, setItem };
}
type Workspace = Awaited<ReturnType<typeof mountWorkspace>>;
async function settings(workspace: Workspace) {
    await buttonIn(workspace.shell, 'Open Settings').trigger('click');
    await flushPromises();
}
const width = (w: Workspace) => w.view.get<HTMLSelectElement>('.workspace-preference-fields select');
const motion = (w: Workspace) => w.view.findAll<HTMLSelectElement>('.workspace-preference-fields select')[1]!;
const tag = (w: Workspace) => w.view.get<HTMLInputElement>('input[aria-label="Add tag"]');
async function selectGallery(w: Workspace, ids: string[]) {
    await w.click('Select Photo ' + ids[0]);
    for (const id of ids.slice(1)) await w.select(id);
}

test('numeric preview editing retains applied dimensions until a valid blur or Enter commit', async () => {
    const records = storage();
    const w = await mountWorkspace();
    await settings(w);
    const input = w.view.get<HTMLInputElement>('#preference-gridMaximumDimension');
    for (const value of ['', '9', '512.5', '4097']) {
        await input.setValue(value);
        await input.trigger('blur');
        expect(w.view.get('#preference-gridMaximumDimension-error').text()).toMatch(/64 to 4096/);
        expect(records.setItem).not.toHaveBeenCalled();
    }
    await input.setValue('1024');
    expect(records.setItem).not.toHaveBeenCalled();
    await input.trigger('keydown', { key: 'Enter' });
    await w.view.get('main').trigger('focusout');
    expect(w.view.find('#preference-gridMaximumDimension-error').exists()).toBe(false);
    expect(JSON.parse(records.records.get(key)!).workspace.gridMaximumDimension).toBe(1024);
});

test('corrupt or future preferences do not write during hydration, and failed migration keeps the old record', async () => {
    for (const raw of ['{broken', '{"version":99}', '{"appearance":{"width":"full"}}']) {
        const store = storage(raw);
        store.setItem.mockImplementation(() => { throw new Error('full'); });
        const w = await mountWorkspace();
        expect(store.setItem).not.toHaveBeenCalled();
        await settings(w);
        await motion(w).setValue('reduce');
        expect(store.records.get(key)).toBe(raw);
        expect(w.view.get('.preference-notice').text()).toBeTruthy();
        w.dispose();
    }
});

test('denied browser storage reads leave usable in-memory preferences without hydration writes', async () => {
    const store = storage();
    store.getItem.mockImplementation(() => { throw new Error('denied'); });
    store.setItem.mockImplementation(() => { throw new Error('denied'); });
    const w = await mountWorkspace();
    expect(store.setItem).not.toHaveBeenCalled();
    expect(w.view.get('.preference-notice').text()).toContain('for this visit');
    await settings(w);
    await width(w).setValue('full');
    expect(width(w).element.value).toBe('full');
    expect(store.setItem).toHaveBeenCalledTimes(1);
    expect(w.view.get('.preference-notice').text()).toContain("couldn't save");
});

test('browser preferences hydrate before writes, batch edits, isolate identities and recover from a failed explicit write', async () => {
    const store = storage(JSON.stringify({ appearance: { width: 'full', density: 'spacious' } }));
    const w = await mountWorkspace();
    expect(w.view.get('main').classes()).toContain('full');
    expect(store.setItem).not.toHaveBeenCalled();
    const density = w.view.get<HTMLInputElement>('.photo-size-control input');
    density.element.value = '400';
    await density.trigger('input');
    expect(store.setItem).not.toHaveBeenCalled();
    await w.view.get('.appearance-content').trigger('pointerup');
    expect(store.setItem).toHaveBeenCalledTimes(1);
    expect(JSON.parse(store.records.get(key)!).appearances.Curation.density).toBe(310);
    session.value = { ...session.value!, profile: { ...session.value!.profile, sub: 'other' } } as typeof session.value;
    await nextTick();
    expect(w.view.get('main').classes()).not.toContain('full');
    expect(store.setItem).toHaveBeenCalledTimes(1);
    await settings(w);
    store.setItem.mockImplementationOnce(() => { throw new Error('full'); });
    await width(w).setValue('full');
    expect(w.view.get('.preference-notice').text()).toContain("couldn't save");
    const otherKey = key.replace('operator', 'other');
    expect(store.records.has(otherKey)).toBe(false);
    await motion(w).setValue('reduce');
    expect(w.view.find('.preference-notice').exists()).toBe(false);
    expect(JSON.parse(store.records.get(otherKey)!).workspace.width).toBe('full');
    const previous = store.records.get(key);
    loseAccess();
    await nextTick();
    expect(store.records.get(key)).toBe(previous);
    expect(store.records.has('')).toBe(false);
});

test('tabs keep their own presentation and hydration never overwrites the last explicit writer', async () => {
    const store = storage();
    const first = await mountWorkspace();
    await settings(first);
    const second = await mountWorkspace();
    await second.click('Workspace settings');
    expect(store.setItem).not.toHaveBeenCalled();
    await width(first).setValue('full');
    expect(width(second).element.value).toBe('standard');
    await motion(second).setValue('reduce');
    expect(width(first).element.value).toBe('full');
    expect(motion(first).element.value).toBe('system');
    const third = await mountWorkspace();
    await third.click('Workspace settings');
    expect(width(third).element.value).toBe('standard');
    expect(motion(third).element.value).toBe('reduce');
    expect(store.setItem).toHaveBeenCalledTimes(2);
});

test('personal preference scopes reset independently without touching filters or drafts', async () => {
    const saved = defaultPreferences();
    Object.assign(saved.workspace, { width: 'full' });
    Object.assign(saved.appearances.Curation, { density: 310 });
    Object.assign(saved.appearances.Gallery, { fit: 'fit' });
    saved.filters.Gallery.tag = 'keep';
    const store = storage(JSON.stringify(saved));
    const w = await mountWorkspace({ curation: true });
    await tag(w).setValue('unfinished');
    await w.nav('Gallery');
    await w.click('Reset Gallery appearance');
    const after = JSON.parse(store.records.get(key)!);
    expect(after.appearances.Gallery.fit).toBe('fill');
    expect(after.appearances.Curation.density).toBe(310);
    expect(after.workspace.width).toBe('full');
    expect(after.filters.Gallery.tag).toBe('keep');
    await w.nav('Curation');
    expect(tag(w).element.value).toBe('unfinished');
    await settings(w);
    await w.click('Reset workspace preferences');
    expect(JSON.parse(store.records.get(key)!).workspace.width).toBe('standard');
    expect(JSON.parse(store.records.get(key)!).appearances.Curation.density).toBe(310);
});

test('Settings entry respects unresolved curation, keeps Storage dirty across sections and guards departure', async () => {
    const w = await mountWorkspace({ curation: true });
    await tag(w).setValue('unfinished');
    vi.mocked(window.confirm).mockReturnValueOnce(false);
    await settings(w);
    expect(w.view.get('h1').text()).toBe('Curation');
    await settings(w);
    await w.click('Storage');
    w.view.getComponent(RetentionSettings).vm.$emit('dirty', true);
    await w.click('Workspace');
    expect(w.view.getComponent(RetentionSettings).exists()).toBe(true);
    vi.mocked(window.confirm).mockReturnValueOnce(false);
    await w.click('Return to Curation');
    expect(w.view.get('h1').text()).toBe('Settings');
    await w.click('Return to Curation');
    expect(tag(w).element.value).toBe('');
    expect(w.view.findComponent(RetentionSettings).exists()).toBe(false);
});

test('Trash Storage entry obeys curation departure guards and returns to the originating Trash surface', async () => {
    const w = await mountWorkspace({ curation: true });
    await tag(w).setValue('unfinished');
    await w.nav('Gallery');
    await w.click('Trash');
    const trash = w.view.getComponent(TrashWorkspace);
    vi.mocked(window.confirm).mockReturnValueOnce(false);
    trash.vm.$emit('storage-settings');
    await flushPromises();
    expect(w.view.get('h1').text()).toBe('Gallery');
    expect(w.view.getComponent(TrashWorkspace).exists()).toBe(true);
    trash.vm.$emit('storage-settings');
    await flushPromises();
    expect(w.view.get('#settings-section-heading').text()).toBe('Storage');
    await w.click('Return to Gallery');
    expect(w.view.getComponent(TrashWorkspace).exists()).toBe(true);
});

test('changing Gallery order leaves Curation browsing and intent intact and never selects unloaded photos', async () => {
    const w = await mountWorkspace({ curation: true });
    await tag(w).setValue('intent');
    await w.nav('Gallery');
    await selectGallery(w, ['a']);
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    await w.view.get('[aria-label="Order"]').setValue('oldest');
    await flushPromises();
    expect(w.view.get('.bulk-bar').text()).toContain('0 selected');
    expect(w.tiles()).toEqual([]);
    expect(transport.page.mock.lastCall?.[0].queryParameters.oldestFirst).toBe(true);
    await w.nav('Curation');
    pending.resolve({ items: [photo('b')] });
    await flushPromises();
    expect(w.view.get<HTMLSelectElement>('[aria-label="Order"]').element.value).toBe('newest');
    expect(tag(w).element.value).toBe('intent');
});

test('appearance order reset restores only selections still loaded by its asynchronous first page', async () => {
    const w = await mountWorkspace();
    await w.view.get('[aria-label="Order"]').setValue('oldest');
    await flushPromises();
    await selectGallery(w, ['a', 'b']);
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    await w.click('Reset Gallery appearance');
    expect(w.view.get('.bulk-bar').text()).toContain('0 selected');
    pending.resolve({ items: [photo('a'), photo('c')] });
    await flushPromises();
    expect(w.view.get('.bulk-bar').text()).toContain('1 selected');
    expect(w.view.get('[data-id="a"]').classes()).toContain('selected');
});

test('explicit Select none during an order read prevents a late response from restoring selection', async () => {
    const w = await mountWorkspace();
    await selectGallery(w, ['a']);
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    await w.view.get('[aria-label="Order"]').setValue('oldest');
    await w.click('Select none');
    pending.resolve({ items: [photo('a')] });
    await flushPromises();
    expect(w.view.find('.bulk-bar').exists()).toBe(false);
    expect(w.view.get('[data-id="a"]').classes()).not.toContain('selected');
});

test('exposed departure guard preserves editor drafts when the operator cancels or approves navigation', async () => {
    const w = await mountWorkspace({ curation: true });
    await tag(w).setValue('photo draft');
    await w.bulk('a');
    await tag(w).setValue('bulk draft');
    vi.mocked(window.confirm).mockReturnValueOnce(false);
    expect(w.view.vm.requestDeparture()).toBe(false);
    expect(tag(w).element.value).toBe('bulk draft');
    expect(w.view.vm.requestDeparture()).toBe(true);
    await nextTick();
    expect(tag(w).element.value).toBe('');
    await w.click('Photo view');
    expect(tag(w).element.value).toBe('photo draft');
});

test('debounced tag text remains owned by the view where it was typed', async () => {
    const w = await mountWorkspace();
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
    await w.view.get('#capture-filter-tag').setValue('morning');
    await w.nav('Curation');
    await vi.advanceTimersByTimeAsync(220);
    expect(w.view.get<HTMLInputElement>('#capture-filter-tag').element.value).toBe('');
    await w.nav('Gallery');
    expect(w.view.get<HTMLInputElement>('#capture-filter-tag').element.value).toBe('');
    expect(transport.page.mock.calls.some(([p]) => p.queryParameters.tag === 'morning')).toBe(false);
});
