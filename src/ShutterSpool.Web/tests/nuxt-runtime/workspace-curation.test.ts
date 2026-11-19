import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { nextTick } from 'vue';
import CaptureFilmstrip from '../../app/components/CaptureFilmstrip.vue';
import CaptureTrashAction from '../../app/components/CaptureTrashAction.vue';
import CurationControls from '../../app/components/CurationControls.vue';
import { session } from '../../app/lib/session';
import { buttonIn, loseAccess, mountWorkspace, photo, refresh, sessionCreated, transport } from './workspace-mount';

test('cancelled photo navigation retains an unfinished bulk tag and accepted discard clears it', async () => {
    const { view, nav, bulk } = await mountWorkspace({ curation: true });
    await bulk('a');
    await view.get('input[aria-label="Add tag"]').setValue('unfinished bulk tag');
    vi.mocked(window.confirm).mockReturnValueOnce(false);
    await nav('Sources');
    expect(view.get('h1').text()).toBe('Curation');
    expect(view.get<HTMLInputElement>('input[aria-label="Add tag"]').element.value).toBe('unfinished bulk tag');
    await nav('Sources');
    expect(view.get('h1').text()).toBe('Sources');
    await nav('Curation');
    expect(view.get<HTMLInputElement>('input[aria-label="Add tag"]').element.value).toBe('');
});

test('bulk tag guard treats only successfully submitted text as clean', async () => {
    for (const changed of [false, true]) {
        const pending = Promise.withResolvers<unknown>();
        transport.batch.mockReturnValueOnce(pending.promise);
        const { view, bulk, dispose } = await mountWorkspace({ curation: true });
        await bulk('a');
        await view.get('input[aria-label="Add tag"]').setValue('morning');
        await view.get('.tag-entry').trigger('submit');
        await flushPromises();
        expect(transport.batch.mock.lastCall?.[1].items[0].tags).toEqual(['morning']);
        if (changed) view.getComponent(CurationControls).vm.$emit('update:tag', 'changed while sending');
        pending.resolve({ items: [{ id: 'a', status: 'committed', current: { revision: 4, tags: ['morning'] } }] });
        await flushPromises();
        expect(view.get<HTMLInputElement>('input[aria-label="Add tag"]').element.value).toBe(changed ? 'changed while sending' : '');
        vi.mocked(window.confirm).mockClear().mockReturnValue(false);
        expect(view.vm.requestDeparture()).toBe(!changed);
        dispose();
    }
});

test('a duplicate bulk acknowledgement leaves the missing photograph and tag unresolved', async () => {
    transport.batch.mockResolvedValue({ items: [{ id: 'a', status: 'committed' }, { id: 'a', status: 'committed' }] });
    const { view, bulk } = await mountWorkspace({ curation: true });
    await bulk('a', 'b');
    await view.get('input[aria-label="Add tag"]').setValue('evening');
    await view.get('.tag-entry').trigger('submit');
    await flushPromises();
    expect(view.get<HTMLInputElement>('input[aria-label="Add tag"]').element.value).toBe('evening');
    expect(view.get('.bulk-inspector').text()).toMatch(/outcomes are unknown/);
    expect(view.get('.bulk-inspector h2').text()).toBe('2 selected');
});

test('fresh released Capture blocks all bulk curation submissions and retains the selection', async () => {
    const { view, bulk } = await mountWorkspace({ curation: true });
    await bulk('a');
    transport.detail.mockImplementation(async id => photo(id, { curation: { revision: 4, releasedAt: new Date() } }));
    const controls = view.getComponent(CurationControls);
    for (const [event, argument] of [['rating', 2], ['label', 'Red'], ['addTag', undefined], ['reset', true]] as const) {
        if (event === 'addTag') await view.get('input[aria-label="Add tag"]').setValue('fresh tag');
        transport.detail.mockClear();
        controls.vm.$emit(event, argument);
        await flushPromises();
        expect(transport.detail.mock.calls.map(([id]) => id)).toEqual(['a']);
        expect(transport.batch).not.toHaveBeenCalled();
        expect(view.get('.bulk-inspector h2').text()).toBe('1 selected');
        expect(view.get('.bulk-inspector').text()).toContain('Released Captures are read-only');
    }
});

test('bulk Favorites use each fresh retention revision and continue after a conflict', async () => {
    const { view, click, bulk } = await mountWorkspace({ curation: true });
    await bulk('a', 'b');
    const order: string[] = [];
    transport.detail.mockImplementation(async id => { order.push('read ' + id); return photo(id, { retention: { captureRevision: id === 'a' ? 7 : 11 } }); });
    transport.favorite.mockImplementation(async (id, body) => {
        order.push([id, body.isFavorite, body.expectedRevision].join(' '));
        if (id === 'a') throw { responseStatusCode: 409 };
    });
    await click('Mark Favorite');
    expect(order.slice(0, 4)).toEqual(['read a', 'a true 7', 'read b', 'b true 11']);
    expect(view.get('.bulk-inspector [role="alert"]').text()).toBe('Photo a: conflict; review current heart');
});

test('bulk rating autosave retains selection and reports only failures by photograph name', async () => {
    transport.batch.mockResolvedValue({ items: [{ id: 'a', status: 'committed', current: { revision: 2, rating: 4 } }, { id: 'b', status: 'conflict' }] });
    const { view, bulk } = await mountWorkspace({ curation: true });
    await bulk('a', 'b');
    view.getComponent(CurationControls).vm.$emit('rating', 4);
    await flushPromises();
    expect(transport.batch.mock.lastCall?.[1].items.map((item: { id: string; rating: number }) => [item.id, item.rating])).toEqual([['a', 4], ['b', 4]]);
    expect(view.get('.bulk-inspector h2').text()).toBe('2 selected');
    expect(view.get('.bulk-inspector [role="alert"]').text()).toBe('Photo b: conflict');
});

test('changing active Capture cannot expose the previous detail or enable an existing draft before its read', async () => {
    const { view, click } = await mountWorkspace({ curation: true });
    await click('Next photograph');
    await view.get('input[aria-label="Add tag"]').setValue('draft b');
    await click('Previous photograph');
    const pending = Promise.withResolvers<unknown>();
    transport.detail.mockReturnValueOnce(pending.promise);
    await click('Next photograph');
    expect(view.findComponent(CurationControls).exists()).toBe(false);
    expect(view.get('.inspector').text()).not.toContain('Photo a');
    pending.resolve(photo('b'));
    await flushPromises();
    expect(view.get('.inspector h2').text()).toBe('Photo b');
    expect(view.get<HTMLInputElement>('input[aria-label="Add tag"]').element.value).toBe('draft b');
});

test('comparison pane IDs and typed tag drafts stay independent when focus changes', async () => {
    const { view, click } = await mountWorkspace({ curation: true });
    const tag = () => view.get<HTMLInputElement>('input[aria-label="Add tag"]');
    await tag().setValue('unfinished A');
    await click('Compare photographs');
    view.getComponent(CaptureFilmstrip).vm.$emit('open', { id: 'b', shift: true });
    await flushPromises();
    await tag().setValue('unfinished B');
    await view.get('.photo-pane').trigger('click');
    await flushPromises();
    expect(tag().element.value).toBe('unfinished A');
    await click('Edit photo B');
    expect(tag().element.value).toBe('unfinished B');
    expect(view.findAll('.photo-pane authorized-preview-stub').map(pane => pane.attributes('captureid'))).toEqual(['a', 'b']);
});

test('comparison layout toggles retain both panes and the focused Capture', async () => {
    const { view, click } = await mountWorkspace({ curation: true });
    await click('Compare photographs');
    view.getComponent(CaptureFilmstrip).vm.$emit('open', { id: 'b', shift: true });
    await flushPromises();
    await click('Compare photographs');
    await click('Compare photographs');
    expect(view.findAll('.photo-pane authorized-preview-stub').map(pane => pane.attributes('captureid'))).toEqual(['a', 'b']);
    expect(view.get('.photo-pane.active').attributes('aria-label')).toBe('Edit photo B');
    expect(view.get('.inspector h2').text()).toBe('Editing B');
});

test('authentication loss clears private Capture state and never sends a pending command', async () => {
    const { view, bulk, click } = await mountWorkspace({ curation: true });
    const identity = session.value;
    const tag = () => view.get<HTMLInputElement>('input[aria-label="Add tag"]');
    await tag().setValue('private draft');
    expect(tag().element.value).toBe('private draft');
    await bulk('a');
    const pending = Promise.withResolvers<unknown>();
    transport.detail.mockReturnValueOnce(pending.promise);
    view.getComponent(CurationControls).vm.$emit('rating', 3);
    await flushPromises();
    expect(transport.detail.mock.lastCall?.[0]).toBe('a');
    transport.page.mockRejectedValue({ responseStatusCode: 403 });
    loseAccess();
    pending.resolve(photo('a'));
    await flushPromises();
    expect(transport.batch).not.toHaveBeenCalled();
    expect(view.find('.gallery-tile').exists()).toBe(false);
    expect(view.find('.gallery-inspection').exists()).toBe(false);
    const previousSessions = sessionCreated.mock.calls.length;
    session.value = identity;
    transport.page.mockResolvedValue({ items: [photo('a')] });
    await refresh();
    await click('Photo view');
    expect(tag().element.value).toBe('');
    expect(sessionCreated.mock.calls.length).toBeGreaterThan(previousSessions);
    expect(transport.batch).not.toHaveBeenCalled();
});

test('queued Trash requests keep the revisions captured at confirmation', async () => {
    const row = photo('b');
    row.retention.captureRevision = 8;
    transport.page.mockResolvedValue({ items: [photo('a'), row] });
    const pending = Promise.withResolvers<unknown>();
    transport.trash.mockReturnValueOnce(pending.promise);
    const { bulk, click } = await mountWorkspace({ curation: true });
    await bulk('a', 'b');
    await click('Move selected to Trash');
    expect(transport.trash.mock.calls).toEqual([['a', { expectedRevision: 7 }]]);
    row.retention.captureRevision = 9;
    pending.resolve(undefined);
    await flushPromises();
    expect(transport.trash.mock.calls).toEqual([['a', { expectedRevision: 7 }], ['b', { expectedRevision: 8 }]]);
});

test('uncertain Trash stays locked until a complete explicit authoritative reread', async () => {
    transport.trash.mockRejectedValue({ responseStatusCode: 503 });
    const { view, bulk, click, btn } = await mountWorkspace({ curation: true });
    await bulk('a');
    await click('Move selected to Trash');
    expect(transport.trash).toHaveBeenCalledTimes(1);
    expect(view.get('.bulk-inspector').text()).toContain('outcome unconfirmed');
    expect(btn('Move selected to Trash').element.disabled).toBe(true);
    btn('Move selected to Trash').element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(transport.trash).toHaveBeenCalledTimes(1);
    transport.detail.mockResolvedValueOnce({ id: 'a', retention: undefined });
    await click('Refresh and verify');
    expect(btn('Move selected to Trash').element.disabled).toBe(true);
    btn('Move selected to Trash').element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(transport.trash).toHaveBeenCalledTimes(1);
    await click('Refresh and verify');
    expect(btn('Move selected to Trash').element.disabled).toBe(false);
    expect(transport.trash).toHaveBeenCalledTimes(1);
});

test('single-photo Trash shares the per-Capture uncertainty lock with bulk and remounted controls', async () => {
    transport.trash.mockRejectedValue({ responseStatusCode: 503 });
    const { view, inspect, nav, bulk, btn, click } = await mountWorkspace();
    await inspect('a');
    const single = () => view.getComponent(CaptureTrashAction);
    await single().get('button').trigger('click');
    await flushPromises();
    expect(transport.trash.mock.calls).toEqual([['a', { expectedRevision: 7 }]]);
    expect(single().get<HTMLButtonElement>('button').element.disabled).toBe(true);
    await nav('Curation');
    await bulk('a');
    expect(btn('Move selected to Trash').element.disabled).toBe(true);
    btn('Move selected to Trash').element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(transport.trash).toHaveBeenCalledTimes(1);
    await nav('Gallery');
    await inspect('a');
    const reads = transport.detail.mock.calls.length;
    await buttonIn(single().element, 'Refresh and verify').trigger('click');
    await flushPromises();
    expect(transport.detail.mock.calls.length).toBeGreaterThan(reads);
    expect(single().get<HTMLButtonElement>('button').element.disabled).toBe(false);
    await nav('Curation');
    if (!view.get('input[aria-label="Select Photo a"]').element.checked) await view.get('input[aria-label="Select Photo a"]').setValue(true);
    await click('Move selected to Trash');
    expect(transport.trash).toHaveBeenCalledTimes(2);
    await nav('Gallery');
    await inspect('a');
    expect(single().get<HTMLButtonElement>('button').element.disabled).toBe(true);
    single().get('button').element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(transport.trash).toHaveBeenCalledTimes(2);
    expect(buttonIn(single().element, 'Refresh and verify').exists()).toBe(true);
});

test('pending bulk Trash keeps its per-Capture lock across confirmed departure and a late failure', async () => {
    const pending = Promise.withResolvers<unknown>();
    transport.trash.mockReturnValueOnce(pending.promise);
    const { view, bulk, click, nav, inspect } = await mountWorkspace({ curation: true });
    await bulk('a');
    await click('Move selected to Trash');
    expect(transport.trash).toHaveBeenCalledTimes(1);
    await nav('Sources');
    await nav('Gallery');
    await inspect('a');
    const single = view.getComponent(CaptureTrashAction);
    expect(single.props('pending')).toBe(true);
    single.get('button').element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
    expect(transport.trash).toHaveBeenCalledTimes(1);
    pending.reject({ responseStatusCode: 503 });
    await flushPromises();
    expect(single.props('pending')).toBe(false);
    expect(single.props('uncertain')).toBe(true);
    expect(single.get<HTMLButtonElement>('button').element.disabled).toBe(true);
    expect(transport.trash).toHaveBeenCalledTimes(1);
});
