import { flushPromises } from '@vue/test-utils';
import { nextTick } from 'vue';
import { expect, test, vi } from 'vitest';
import AuthorizedPreview from '../../app/components/AuthorizedPreview.vue';
import CurationControls from '../../app/components/CurationControls.vue';
import { defaultPreferences } from '../../app/lib/workspace-preferences';
import { buttonIn, loseAccess, mountWorkspace, photo, refresh, sessionCreated, transport } from './workspace-mount';
type Workspace = Awaited<ReturnType<typeof mountWorkspace>>;
function initialTag(more = false) {
    const saved = defaultPreferences();
    Object.assign(saved.filters.Gallery, { tag: 'morning', ...(more ? { sourceIds: ['source'], collectionIds: ['collection'], labels: ['Blue'], workflows: ['Waiting'] } : {}) });
    localStorage.setItem('shutterspool-appearance-https://synthetic.example.test-operator', JSON.stringify(saved));
}
function visiblePhoto(id: string) {
    return photo(id, { mediaKind: 'Photograph', sourceId: 'source', currentCollection: { id: 'collection' }, curation: { revision: 1, tags: ['morning'], label: 'Blue' } });
}
async function viewerClick(w: Workspace, label: string) {
    await buttonIn(w.full()!, label).trigger('click'); await flushPromises();
}
async function displayReady(w: Workspace) {
    const preview = w.view.findAllComponents(AuthorizedPreview).find(p => p.element.closest('.fullscreen-viewer'));
    if (!preview) throw new Error('Missing viewer preview');
    preview.vm.$emit('display', 'ready');
    await nextTick();
}
function hiddenTab(hidden: boolean) {
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(hidden);
    document.dispatchEvent(new Event('visibilitychange'));
}
const fakeClock = () => vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
async function expectIdleTimerCancelled(dialog: HTMLElement, leave: () => Promise<void>) {
    const clock = { setTimeout: globalThis.setTimeout, clearTimeout: globalThis.clearTimeout };
    const schedule = vi.fn(clock.setTimeout);
    const cancel = vi.fn(clock.clearTimeout);
    Object.assign(globalThis, { setTimeout: schedule, clearTimeout: cancel });
    try {
        dialog.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
        const index = schedule.mock.calls.findLastIndex(([, delay]) => delay === 3000);
        expect(index).toBeGreaterThanOrEqual(0);
        const timer = schedule.mock.results[index]!.value;
        cancel.mockClear();
        await leave();
        expect(cancel).toHaveBeenCalledWith(timer);
    } finally { Object.assign(globalThis, clock); }
}

test('Gallery playback snapshots filters, keeps grid and selection unchanged and stops on hidden tab or access loss', async () => {
    initialTag(true);
    transport.sources.mockResolvedValue({ items: [{ id: 'source', name: 'Source', curationMode: 'Immediate' }, { id: 'later', name: 'Later camera', curationMode: 'Immediate' }] });
    transport.page.mockImplementation(async ({ queryParameters }) => ({ items: queryParameters.startAt
        ? [visiblePhoto('a'), visiblePhoto('sequence-only')] : [visiblePhoto('a')] }));
    transport.detail.mockImplementation(async id => visiblePhoto(id));
    const w = await mountWorkspace();
    await w.click('Select Photo a');
    await w.click('Fullscreen photographs');
    await viewerClick(w, 'Play slideshow');
    const query = transport.page.mock.lastCall?.[0].queryParameters;
    expect(query).toMatchObject({ startAt: 'a', sourceIds: ['source'], collectionIds: ['collection'], tag: 'morning', labels: ['Blue'], workflows: ['Waiting'] });
    expect(w.tiles()).toEqual(['a']);
    expect(w.view.get('.bulk-bar').text()).toContain('1 selected');
    expect(buttonIn(w.full()!, 'Pause slideshow').element).toBeDefined();
    await displayReady(w);
    hiddenTab(true); await nextTick();
    expect(w.full()?.textContent).toContain('hidden');
    hiddenTab(false); await nextTick();
    expect(buttonIn(w.full()!, 'Play slideshow').element).toBeDefined();
    await viewerClick(w, 'Play slideshow');
    await w.click('Later camera');
    expect(transport.page.mock.lastCall?.[0].queryParameters.sourceIds).toEqual(['source', 'later']);
    expect(query.sourceIds).toEqual(['source']);
    expect(w.tiles()).toEqual(['a']);
    expect(w.view.get('.bulk-bar').text()).toContain('1 selected');
    await viewerClick(w, 'Play slideshow');
    loseAccess(); await nextTick();
    expect(w.full()).toBeNull();
});

test('closing the Gallery viewer fences slideshow reads and reopening resets its interval', async () => {
    const w = await mountWorkspace();
    await w.click('Fullscreen photographs');
    await viewerClick(w, '10 seconds per photograph');
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    await viewerClick(w, 'Play slideshow');
    const signal = transport.page.mock.lastCall?.[0].options[0].signal;
    await viewerClick(w, 'Close viewer');
    expect(signal.aborted).toBe(true);
    pending.resolve({ items: [photo('late')] }); await flushPromises();
    expect(w.full()).toBeNull();
    await w.click('Fullscreen photographs');
    expect(buttonIn(w.full()!, '5 seconds per photograph').attributes('aria-pressed')).toBe('true');
    expect(w.full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('a');
});

test('route guard preserves internal slideshow URLs and immediately cancels navigation away despite stale route props', async () => {
    const w = await mountWorkspace({ captureId: 'a' });
    await viewerClick(w, 'Play slideshow');
    const router = w.view.vm.$router;
    await router.replace('/captures/a?view=photo');
    expect(w.full()).not.toBeNull();
    expect(buttonIn(w.full()!, 'Pause slideshow').exists()).toBe(true);
    await router.replace('/');
    expect(w.view.props('captureId')).toBe('a');
    expect(w.full()).toBeNull();
});

test('Gallery uses image readiness received while its first slideshow page is still pending', async () => {
    const w = await mountWorkspace();
    fakeClock();
    await w.click('Fullscreen photographs');
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    await viewerClick(w, 'Play slideshow');
    await displayReady(w);
    pending.resolve({ items: [photo('a'), photo('b')] });
    await flushPromises();
    expect(buttonIn(w.full()!, 'Pause slideshow').exists()).toBe(true);
    await vi.advanceTimersByTimeAsync(4999);
    expect(w.full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('a');
    await vi.advanceTimersByTimeAsync(1);
    await flushPromises();
    expect(w.full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('b');
});

test('paused Gallery clears a displayed purged Capture after authoritative detail reconciliation', async () => {
    const w = await mountWorkspace();
    await w.click('Fullscreen photographs');
    transport.detail.mockImplementation(async id => photo(id, { retention: { disposition: 'purged' } }));
    await refresh(['a']);
    expect(w.full()?.querySelector('authorized-preview-stub[captureid="a"]')).toBeNull();
    expect(buttonIn(w.full()!, 'Play slideshow').exists()).toBe(true);
});

test('300 read-only Gallery detail opens retain existing Curation baselines without accumulating sessions', async () => {
    transport.detail.mockImplementation(async id => photo(id, { curation: { revision: 1, rating: 3, tags: ['keep'] } }));
    const w = await mountWorkspace({ curation: true });
    await w.view.get('input[aria-label="Add tag"]').setValue('unfinished');
    await w.nav('Gallery');
    await w.click('Fullscreen photographs');
    const baseline = sessionCreated.mock.calls.length;
    expect(baseline).toBe(1);
    transport.detail.mockClear();
    for (let i = 0; i < 300; i++) {
        await w.view.setProps({ captureId: 'slide-' + i });
        await flushPromises();
    }
    expect(transport.detail.mock.calls.map(([id]) => id)).toEqual(Array.from({ length: 300 }, (_, i) => 'slide-' + i));
    expect(sessionCreated).toHaveBeenCalledTimes(baseline);
    expect(w.full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('slide-299');
    await viewerClick(w, 'Close viewer');
    await w.nav('Curation');
    expect(w.view.get<HTMLInputElement>('input[aria-label="Add tag"]').element.value).toBe('unfinished');
    expect(w.view.getComponent(CurationControls).props()).toMatchObject({ rating: 3, tags: ['keep'] });
});

test('active direct Capture links remain visible outside Gallery filters before explicit playback', async () => {
    initialTag();
    transport.page.mockResolvedValue({ items: [visiblePhoto('a')] });
    transport.detail.mockImplementation(async id => photo(id, { curation: { revision: 1, tags: ['other'] } }));
    const w = await mountWorkspace({ captureId: 'direct' });
    expect(transport.page.mock.lastCall?.[0].queryParameters.tag).toBe('morning');
    expect(w.full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('direct');
    expect(w.full()?.querySelector('authorized-preview-stub')?.getAttribute('name')).toBe('Photo direct');
    expect(transport.page.mock.calls.every(([o]) => !o.queryParameters.startAt)).toBe(true);
});

test('both fullscreen workspaces wake hidden controls without activating the wake click or blocking zoom pointers', async () => {
    for (const curation of [false, true]) {
        const w = await mountWorkspace({ curation });
        fakeClock();
        await w.click(curation ? 'Fullscreen' : 'Fullscreen photographs');
        const dialog = w.full()!;
        Object.defineProperties(dialog, { clientWidth: { value: 200 }, clientHeight: { value: 100 } });
        await vi.advanceTimersByTimeAsync(3000);
        const chrome = dialog.querySelector('.viewer-chrome')!;
        expect(chrome.getAttribute('aria-hidden')).toBe('true');
        const surface = dialog.querySelector<HTMLElement>('.zoom-surface')!;
        const capture = vi.fn();
        Object.defineProperty(surface, 'setPointerCapture', { value: capture });
        function pointer(type: string, id: number, x: number) {
            surface.dispatchEvent(new PointerEvent(type, { bubbles: true, pointerId: id, clientX: x, clientY: 30, pointerType: 'touch' }));
        }
        pointer('pointerdown', 1, 20);
        pointer('pointerup', 1, 20);
        const click = () => { const event = new MouseEvent('click', { bubbles: true, cancelable: true }); surface.dispatchEvent(event); return event.defaultPrevented; };
        expect(click()).toBe(true);
        pointer('pointerdown', 1, 20);
        pointer('pointerdown', 2, 30);
        pointer('pointermove', 2, 50);
        await nextTick();
        expect(chrome.getAttribute('aria-hidden')).toBe('false');
        expect(capture.mock.calls).toEqual([[1], [1], [2]]);
        if (curation) expect(surface.querySelector<HTMLElement>('authorized-preview-stub')?.style.transform).toContain('scale(3)');
        pointer('pointerdown', 1, 20);
        expect(click()).toBe(false);
        w.dispose();
        vi.useRealTimers();
    }
});

test('hidden fullscreen controls are revealed before Tab focuses the first control and lifecycle cancels idle work', async () => {
    const w = await mountWorkspace();
    fakeClock();
    await w.click('Fullscreen photographs');
    const dialog = w.full()!;
    await vi.advanceTimersByTimeAsync(3000);
    expect(dialog.querySelector('.viewer-chrome')?.getAttribute('aria-hidden')).toBe('true');
    const event = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    dialog.dispatchEvent(event); await nextTick();
    expect(event.defaultPrevented).toBe(true);
    expect(document.activeElement).toBe(dialog.querySelector('.viewer-close'));
    await expectIdleTimerCancelled(dialog, () => viewerClick(w, 'Close viewer'));
    await vi.advanceTimersByTimeAsync(3000);
    expect(w.full()).toBeNull();
});

test('automatic Gallery frame changes do not reveal idle controls and access loss clears the timer', async () => {
    const w = await mountWorkspace();
    fakeClock();
    await w.click('Fullscreen photographs');
    await viewerClick(w, 'Play slideshow');
    await displayReady(w);
    await vi.advanceTimersByTimeAsync(5000);
    await flushPromises();
    expect(w.full()?.querySelector('authorized-preview-stub')?.getAttribute('captureid')).toBe('b');
    expect(w.full()?.querySelector('.viewer-chrome')?.getAttribute('aria-hidden')).toBe('true');
    w.full()!.dispatchEvent(new PointerEvent('pointermove', { bubbles: true }));
    await nextTick();
    expect(w.full()?.querySelector('.viewer-chrome')?.getAttribute('aria-hidden')).toBe('false');
    await expectIdleTimerCancelled(w.full()!, async () => { loseAccess(); await nextTick(); });
    expect(w.full()).toBeNull();
    await vi.advanceTimersByTimeAsync(5000);
    expect(w.full()).toBeNull();
});

test('fullscreen recovery messages remain actionable after interruption and successful sequence retry', async () => {
    const w = await mountWorkspace();
    fakeClock();
    await w.click('Fullscreen photographs');
    await viewerClick(w, 'Play slideshow');
    hiddenTab(true); await nextTick();
    expect(w.full()?.textContent).toContain('Choose Play');
    await vi.advanceTimersByTimeAsync(3000);
    expect(w.full()?.querySelector('.viewer-chrome')?.getAttribute('aria-hidden')).toBe('false');
    hiddenTab(false);
    await viewerClick(w, 'Close viewer');
    await w.click('Fullscreen photographs');
    transport.page.mockRejectedValueOnce(new Error('sequence unavailable'));
    await viewerClick(w, 'Play slideshow');
    expect(buttonIn(w.full()!, 'Retry').exists()).toBe(true);
    await viewerClick(w, 'Retry');
    expect(w.full()?.textContent).toContain('Sequence available. Choose Play');
    await vi.advanceTimersByTimeAsync(3000);
    expect(w.full()?.querySelector('.viewer-chrome')?.getAttribute('aria-hidden')).toBe('false');
});
