import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { buttonIn, live, loseAccess, mountWorkspace, photo, refresh, sessionCreated, transport } from './workspace-mount';
import ApertureBackground from '../../app/components/ApertureBackground.vue';
import CurationControls from '../../app/components/CurationControls.vue';

test('aperture receives the selected view after accepted workspace changes', async () => {
    const { view, nav } = await mountWorkspace();
    const lens = view.getComponent(ApertureBackground);
    expect(lens.props('view')).toBe('Gallery');
    await nav('Sources');
    expect(lens.props('view')).toBe('Sources');
    await nav('Gallery');
    expect(lens.props('view')).toBe('Gallery');
});

test('cancelled navigation leaves the mounted aperture on the current view', async () => {
    const { view, nav } = await mountWorkspace({ curation: true });
    await view.get('input[aria-label="Add tag"]').setValue('unfinished');
    vi.mocked(window.confirm).mockReturnValue(false);
    await nav('Sources');
    expect(view.getComponent(ApertureBackground).props('view')).toBe('Curation');
    expect(view.get<HTMLInputElement>('input[aria-label="Add tag"]').element.value).toBe('unfinished');
});

test('explicit Capture links retain the requested photo despite Curation membership', async () => {
    for (const released of [false, true]) {
        transport.detail.mockImplementation(async id => photo(id, { curation: { revision: 1, rating: 0, releasedAt: released ? new Date() : null } }));
        const { view, full, dispose } = await mountWorkspace({ curation: true, captureId: 'requested' });
        expect(view.get('h1').text()).toBe('Gallery');
        expect(full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('requested');
        expect(full()?.querySelector('authorized-preview-stub')?.getAttribute('name')).toBe('Photo requested');
        dispose();
    }
});

test('Curator and Viewer refreshes avoid Source configuration, Viewer opens no autosave session or keyboard mutation', async () => {
    for (const role of ['Curator', 'Viewer'] as const) {
        transport.waiting.mockClear();
        sessionCreated.mockClear();
        const { view, shell, click, dispose } = await mountWorkspace({ role });
        expect(transport.sources).not.toHaveBeenCalled();
        expect(transport.waiting).toHaveBeenCalledTimes(role === 'Curator' ? 1 : 0);
        expect(shell.querySelector('.main-nav')?.textContent).not.toMatch(/Sources|Delivery/);
        if (role === 'Viewer') {
            expect(shell.querySelector('.main-nav')?.textContent).not.toContain('Curation');
            await click('Fullscreen photographs');
            const key = new KeyboardEvent('keydown', { key: 'r', bubbles: true, cancelable: true });
            document.dispatchEvent(key);
            expect(key.defaultPrevented).toBe(false);
            expect(view.findComponent(CurationControls).exists()).toBe(false);
            expect(transport.save).not.toHaveBeenCalled();
            expect(transport.batch).not.toHaveBeenCalled();
            expect(sessionCreated).not.toHaveBeenCalled();
        }
        dispose();
    }
});

test('Viewer rendered inspection hides Favorite, Collection moves, Trash and administrator actions', async () => {
    const { view, inspect } = await mountWorkspace({ role: 'Viewer' });
    await inspect('a');
    expect(view.get('.gallery-inspection').text()).toContain('Photo a');
    for (const name of ['favorite-control-stub', 'collection-move-stub', '.retention-action', 'capture-resume-action-stub', 'no-delivery-intent-action-stub', 'privacy-erasure-panel-stub', '[aria-label="Gallery views"]', '.photo-select-action']) {
        expect(view.find(name).exists(), name).toBe(false);
    }
});

test('leaving the photo browser aborts its pending page transport and fences the late response', async () => {
    const { view, nav } = await mountWorkspace();
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    const active = refresh();
    await flushPromises();
    const signal = transport.page.mock.lastCall![0].options[0].signal;
    expect(signal.aborted).toBe(false);
    await nav('Sources');
    expect(signal.aborted).toBe(true);
    pending.resolve({ items: [photo('late')] });
    await active;
    expect(view.get('h1').text()).toBe('Sources');
    await nav('Gallery');
    expect(view.find('[data-id="late"]').exists()).toBe(false);
});

test('targeted reconciliation fences late success and failure after view, query, or workspace ownership changes', async () => {
    for (const change of ['view', 'query', 'dispose']) {
        const { view, nav, dispose } = await mountWorkspace();
        const pending = Promise.withResolvers<unknown>();
        transport.detail.mockReturnValueOnce(pending.promise);
        const before = transport.page.mock.calls.length;
        const active = refresh(['a']);
        await flushPromises();
        const signal = transport.detail.mock.lastCall![1].options[0].signal;
        expect(signal.aborted).toBe(false);
        if (change === 'view') await nav('Sources');
        if (change === 'query') await view.get('button[aria-label="Red"]').trigger('click');
        if (change === 'dispose') dispose();
        await flushPromises();
        expect(signal.aborted).toBe(true);
        if (change === 'query') pending.reject({ responseStatusCode: 503 });
        else pending.resolve(photo('a', { displayName: 'stale detail' }));
        await active;
        expect(transport.failure).not.toHaveBeenCalled();
        expect(transport.page.mock.calls.length).toBe(before + Number(change === 'query'));
        if (change !== 'dispose') expect(view.text()).not.toContain('stale detail');
        dispose();
    }
});

test('closing inspection or losing access fences a late Gallery detail response', async () => {
    for (const exit of ['close', 'access']) {
        const { view, inspect, dispose } = await mountWorkspace();
        const late = Promise.withResolvers<unknown>();
        transport.detail.mockReturnValueOnce(late.promise);
        await inspect('b');
        expect(view.get('.gallery-inspection').text()).toContain('Loading photograph');
        if (exit === 'access') loseAccess();
        else await view.get('.gallery-inspection .photo-actions button:last-child').trigger('click');
        await flushPromises();
        late.resolve(photo('b', { displayName: 'late private row' }));
        await flushPromises();
        expect(view.find('.gallery-inspection').exists()).toBe(false);
        expect(view.text()).not.toContain('late private row');
        dispose();
    }
});

test('Gallery Fullscreen is explicit, uses active or first loaded Capture, and stays disabled when empty', async () => {
    const { view, inspect, full, click } = await mountWorkspace();
    expect(full()).toBeNull();
    await click('Fullscreen photographs');
    expect(full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('a');
    await buttonIn(full()!, 'Close viewer').trigger('click');
    await flushPromises();
    await inspect('b');
    expect(full()).toBeNull();
    await click('Fullscreen photographs');
    expect(full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('b');
    await buttonIn(full()!, 'Close viewer').trigger('click');
    transport.page.mockResolvedValue({ items: [] });
    await refresh();
    expect(view.get<HTMLButtonElement>('.gallery-fullscreen').element.disabled).toBe(true);
});

test('erasure during a targeted read cannot shift its result onto an unrelated tile', async () => {
    transport.page.mockResolvedValue({ items: [photo('a'), photo('b'), photo('c')] });
    const { tiles, view } = await mountWorkspace();
    const pending = Promise.withResolvers<unknown>();
    transport.detail.mockReturnValueOnce(pending.promise);
    const active = refresh(['b']);
    await flushPromises();
    expect(transport.detail.mock.lastCall?.[0]).toBe('b');
    live.activity.value = { captureId: 'a', changeKind: 'erasure', sequence: 1 };
    await flushPromises();
    expect(tiles()).toEqual(['b', 'c']);
    const page = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(page.promise);
    const updated = photo('b', { displayName: 'Updated middle photo' });
    pending.resolve(updated);
    await flushPromises();
    expect(tiles()).toEqual(['b', 'c']);
    expect(view.get('[data-id="b"] .tile-open').attributes('aria-label')).toContain('Updated middle photo');
    expect(view.get('[data-id="c"] .tile-open').attributes('aria-label')).toContain('Photo c');
    page.resolve({ items: [updated, photo('c')] });
    await active;
});
