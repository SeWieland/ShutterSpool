import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { nextTick } from 'vue';
import CurationControls from '../../app/components/CurationControls.vue';
import CaptureFilmstrip from '../../app/components/CaptureFilmstrip.vue';
import { buttonIn, mountWorkspace, photo, refresh, sessionCreated, transport } from './workspace-mount';

test('fullscreen and inspector curation are disabled during unresolved recovery states', async () => {
    for (const [status, label] of [[409, 'Conflict'], [400, 'Save failed'], [503, 'Checking outcome']] as const) {
        const w = await mountWorkspace({ curation: true });
        const controls = w.view.getComponent(CurationControls);
        expect(controls.props('disabled')).toBe(false);
        transport.save.mockRejectedValueOnce({ responseStatusCode: status });
        if (status === 503) transport.detail.mockRejectedValueOnce(new Error('unavailable'));
        controls.vm.$emit('rating', 4);
        await flushPromises();
        expect(w.view.get('.save-state').text()).toBe(label);
        expect(controls.props('disabled')).toBe(true);
        await w.click('Fullscreen');
        expect(buttonIn(w.full()!, 'Rejected').element.disabled).toBe(true);
        w.dispose();
    }
});

test('curation leads with choices, keeps Favorite independent, and places recovery and administration second', async () => {
    let current = photo('a');
    current = { ...current, retention: { ...current.retention, isPaused: true } };
    transport.detail.mockImplementation(async () => current);
    const w = await mountWorkspace({ curation: true });
    expect(w.view.get('.curation-markers').find('favorite-control-stub').exists()).toBe(true);
    const storage = w.view.get<HTMLDetailsElement>('.photograph-storage');
    expect(storage.element.open).toBe(false);
    expect(storage.find('privacy-erasure-panel-stub').exists()).toBe(true);
    expect(w.btn('Release now').element.disabled).toBe(true);
    current = photo('a');
    await refresh();
    expect(w.btn('Release now').element.disabled).toBe(false);
    current = photo('a', { curation: { revision: 3, releasedAt: new Date() } });
    await refresh();
    for (const group of ['.curation-rating-controls', '.curation-color-controls', '.curation-tag-controls'])
        expect(w.view.get<HTMLFieldSetElement>(group).element.disabled).toBe(true);
    expect(w.view.get('.curation-markers > favorite-control-stub').exists()).toBe(true);
});

test('R rejects the editable fullscreen Capture without stealing typing or browser shortcuts', async () => {
    const w = await mountWorkspace({ curation: true });
    await w.click('Fullscreen');
    function key(options: KeyboardEventInit, element: EventTarget = document) {
        const event = new KeyboardEvent('keydown', { key: 'r', bubbles: true, cancelable: true, ...options });
        element.dispatchEvent(event);
        return event.defaultPrevented;
    }
    for (const letter of ['r', 'R']) {
        w.view.getComponent(CurationControls).vm.$emit('rating', 3);
        await flushPromises();
        expect(key({ key: letter })).toBe(true);
        await flushPromises();
        expect(transport.save.mock.lastCall?.[1].rating).toBe(-1);
    }
    const writes = transport.save.mock.calls.length;
    for (const options of [{ ctrlKey: true }, { altKey: true }, { metaKey: true }, { isComposing: true }])
        expect(key(options)).toBe(false);
    expect(key({}, w.view.get('input[aria-label="Add tag"]').element)).toBe(false);
    await buttonIn(w.full()!, 'Close viewer').trigger('click');
    expect(key({})).toBe(false);
    await w.nav('Gallery');
    await w.click('Fullscreen photographs');
    expect(key({})).toBe(false);
    expect(transport.save).toHaveBeenCalledTimes(writes);
});

test('focused Curation retains its paging target through refresh and restores the grid footer', async () => {
    const w = await mountWorkspace({ curation: true });
    const boundary = w.view.get('.scroll-boundary');
    expect(boundary.classes()).toContain('focused-paging');
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    const active = refresh();
    await flushPromises();
    expect(w.view.get('.scroll-boundary').element).toBe(boundary.element);
    expect(boundary.text()).toContain('Loading');
    expect(boundary.isVisible()).toBe(true);
    pending.resolve({ items: [photo('a')] });
    await active;
    await w.click('Bulk grid');
    expect(boundary.classes()).not.toContain('focused-paging');
    expect(boundary.isVisible()).toBe(true);
    await w.nav('Gallery');
    await w.inspect('a');
    expect(w.view.get('.scroll-boundary').isVisible()).toBe(false);
});

test('compiled Curation shell, filmstrip and unchanged comparison preview survive pending and failed owner reads', async () => {
    const w = await mountWorkspace({ curation: true });
    await w.click('Compare photographs');
    const layout = w.view.get('.curation-layout').element;
    const strip = w.view.get('.filmstrip').element;
    const thumbs = [...strip.children];
    expect(thumbs.map(node => node.getAttribute('aria-label'))).toEqual(['Photo a', 'Photo b']);
    const pane = w.view.get('.photo-pane').element;
    for (const fail of [false, true]) {
        const pending = Promise.withResolvers<unknown>();
        transport.detail.mockReturnValueOnce(pending.promise);
        w.view.getComponent(CaptureFilmstrip).vm.$emit('open', { id: 'b', shift: true });
        await flushPromises();
        const checkShell = () => {
            expect(w.view.get('.curation-layout').element).toBe(layout);
            expect(w.view.get('.photo-pane').element).toBe(pane);
            expect(w.view.get('.filmstrip').element).toBe(strip);
            expect(strip.children).toHaveLength(thumbs.length);
            expect([...strip.children].every((node, i) => node === thumbs[i])).toBe(true);
            expect(w.view.find('.photo-grid').exists()).toBe(false);
            expect(w.view.findComponent(CurationControls).exists()).toBe(false);
            expect(w.view.find('.capture-evidence').exists()).toBe(false);
        };
        checkShell();
        if (fail) pending.reject(new Error('unavailable')); else pending.resolve(photo('b'));
        await flushPromises();
        if (fail) {
            checkShell();
            expect(w.view.get('.inspector').text()).toContain('Unable to open');
            expect(w.view.find('.notice.error[role="alert"]').exists()).toBe(false);
        } else {
            expect(w.view.get('.inspector h2').text()).toBe('Editing B');
            await w.view.get('.photo-pane').trigger('click');
            await flushPromises();
        }
    }
    await w.click('Bulk grid');
    const loaded = [photo('a'), photo('b'), ...Array.from({ length: 98 }, (_, i) => photo('extra-' + i))];
    transport.page.mockResolvedValue({ items: loaded });
    transport.detail.mockImplementation(async id => { if (id === 'b') throw new Error('unavailable'); return photo(id); });
    await refresh();
    await w.click('Select all loaded');
    transport.page.mockResolvedValue({ items: [photo('overflow'), ...loaded] });
    await refresh();
    await w.select('overflow');
    expect(w.view.get('.bulk-inspector h2').text()).toBe('100 selected');
    await w.click('Photo view');
    expect(w.view.get('.inspector').text()).toContain('Unable to open');
    expect(w.view.get('.notice.error[role="alert"]').text()).toBe('Select at most 100 Captures.');
});

test('an empty Curation result clears the photograph while retaining the visit baseline', async () => {
    let current = photo('a', { curation: { revision: 1, rating: 3 } });
    transport.detail.mockImplementation(async () => current);
    transport.page.mockImplementation(async () => ({ items: [current] }));
    const w = await mountWorkspace({ curation: true });
    w.view.getComponent(CurationControls).vm.$emit('rating', 5);
    await flushPromises();
    current = photo('a', { curation: { revision: 2, rating: 5 } });
    transport.page.mockResolvedValueOnce({ items: [], nextCursor: null });
    await refresh();
    expect(w.view.find('.photo-pane').exists()).toBe(false);
    expect(w.view.findComponent(CurationControls).exists()).toBe(false);
    await refresh();
    w.view.getComponent(CurationControls).vm.$emit('reset', false);
    await flushPromises();
    expect(transport.save.mock.lastCall?.[1]).toMatchObject({ expectedRevision: 2, rating: 3 });
});

test('release and a new arrival in one refresh select the remaining waiting photograph', async () => {
    const w = await mountWorkspace({ curation: true });
    transport.detail.mockImplementation(async id => id === 'a' ? photo(id, { curation: { revision: 2, releasedAt: new Date() } }) : photo(id));
    transport.page.mockResolvedValue({ items: [photo('new')] });
    await refresh(['a', 'new']);
    expect(w.view.get('.inspector h2').text()).toBe('Photo new');
    expect(w.view.get('.photo-pane authorized-preview-stub').attributes('captureid')).toBe('new');
    expect(w.view.getComponent(CaptureFilmstrip).props('activeId')).toBe('new');
});

test('a boundary release fences the inactive edit session before filtering and a delayed save acknowledgement', async () => {
    const w = await mountWorkspace({ curation: true });
    const pending = Promise.withResolvers<unknown>();
    transport.save.mockReturnValueOnce(pending.promise);
    const controls = w.view.getComponent(CurationControls);
    controls.vm.$emit('rating', 2);
    controls.vm.$emit('rating', 4);
    await w.click('Next photograph');
    const release = photo('a', { curation: { revision: 3, rating: 2, releasedAt: new Date() } });
    transport.page.mockResolvedValue({ items: [photo('b')], nextCursor: 'next' });
    transport.detail.mockImplementation(async id => id === 'a' ? release : photo(id));
    await refresh();
    expect(w.view.getComponent(CaptureFilmstrip).props('items').map(item => item.capture.id)).toEqual(['b']);
    pending.resolve({ revision: 2, rating: 2 });
    await flushPromises();
    expect(transport.save).toHaveBeenCalledTimes(1);
    transport.page.mockResolvedValue({ items: [release, photo('b')] });
    await w.click('All states');
    w.view.getComponent(CaptureFilmstrip).vm.$emit('open', { id: 'a' });
    await flushPromises();
    expect(w.view.get('.save-state').text()).toContain('Released');
    expect(w.view.getComponent(CurationControls).props()).toMatchObject({ disabled: true, rating: 4 });
    expect(w.view.get('.inspector .notice').text()).toMatch(/Server:\s*2.*Your edits:\s*4/s);
    expect(transport.save).toHaveBeenCalledTimes(1);
});

test('pending bulk intent guards departure and abandoned reads cannot submit or recreate visit sessions', async () => {
    for (const leave of [false, true]) {
        const w = await mountWorkspace({ curation: true });
        await w.bulk('b');
        sessionCreated.mockClear();
        const pending = Promise.withResolvers<unknown>();
        transport.detail.mockReturnValueOnce(pending.promise);
        transport.batch.mockClear().mockResolvedValue({ items: [{ id: 'b', status: 'committed' }] });
        w.view.getComponent(CurationControls).vm.$emit('reset', true);
        await flushPromises();
        const signal = transport.detail.mock.lastCall![1].options[0].signal;
        vi.mocked(window.confirm).mockReturnValueOnce(leave);
        await w.nav('Sources');
        expect(signal.aborted).toBe(leave);
        expect(w.view.get('h1').text()).toBe(leave ? 'Sources' : 'Curation');
        pending.resolve(photo('b'));
        await flushPromises();
        expect(transport.batch).toHaveBeenCalledTimes(leave ? 0 : 1);
        expect(sessionCreated).toHaveBeenCalledTimes(leave ? 0 : 1);
        w.dispose();
    }
});

test('photo and fullscreen curation stay fenced while bulk reads are pending', async () => {
    const w = await mountWorkspace({ curation: true });
    await w.bulk('b');
    const pending = Promise.withResolvers<unknown>();
    transport.detail.mockReturnValueOnce(pending.promise);
    w.view.getComponent(CurationControls).vm.$emit('reset', true);
    await flushPromises();
    expect(w.view.getComponent(CurationControls).props('busy')).toBe(true);
    expect(w.view.get<HTMLButtonElement>('.bulk-inspector button').element.disabled).toBe(true);
    vi.mocked(window.confirm).mockReturnValueOnce(false);
    expect(w.view.vm.requestDeparture()).toBe(false);
    await w.view.setProps({ captureId: 'b' });
    await flushPromises();
    expect(buttonIn(w.full()!, 'Rejected').element.disabled).toBe(true);
    const key = new KeyboardEvent('keydown', { key: 'r', bubbles: true, cancelable: true });
    document.dispatchEvent(key);
    expect(key.defaultPrevented).toBe(false);
    expect(transport.save).not.toHaveBeenCalled();
    pending.resolve(photo('b'));
    await flushPromises();
    expect(w.view.getComponent(CurationControls).props('busy')).toBe(false);
    expect(buttonIn(w.full()!, 'Rejected').element.disabled).toBe(false);
});
