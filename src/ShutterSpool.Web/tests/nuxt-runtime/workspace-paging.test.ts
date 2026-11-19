import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { defaultPreferences } from '../../app/lib/workspace-preferences';
import { buttonIn, intersect, live, loseAccess, mountWorkspace, photo, refresh, transport } from './workspace-mount';
function initialFilters(view: 'Curation' | 'Gallery', values: Partial<ReturnType<typeof defaultPreferences>['filters']['Gallery']>) {
    const saved = defaultPreferences();
    Object.assign(saved.filters[view], values);
    localStorage.setItem('shutterspool-appearance-https://synthetic.example.test-operator', JSON.stringify(saved));
}
const at = (id: string, minute: number) => photo(id, { acceptedAt: new Date(Date.UTC(2026, 9, 12, 10, minute)) });
const attention = (id: string, amount = 1) => photo(id, { deliverySummary: { attention: amount } });

test('initial Sources snapshot completes the Capture read for its selected photo view', async () => {
    initialFilters('Curation', { tag: 'review' });
    for (const curation of [false, true]) {
        transport.page.mockClear().mockResolvedValue({ items: [photo('capture-row')] });
        const w = await mountWorkspace({ curation });
        expect(w.view.get('h1').text()).toBe(curation ? 'Curation' : 'Gallery');
        expect(transport.page.mock.calls.map(([o]) => o.queryParameters.tag)).toEqual([curation ? 'review' : undefined]);
        expect(w.view.find('authorized-preview-stub[captureid="capture-row"]').exists()).toBe(true);
        w.dispose();
    }
});

test('Viewer refresh replaces historical cached rows around its visible anchor without retargeting the active photograph', async () => {
    transport.page.mockResolvedValueOnce({ items: [photo('anchor'), photo('formerly-visible')], nextCursor: 'old' });
    const w = await mountWorkspace({ role: 'Viewer' });
    expect(w.tiles()).toContain('formerly-visible');
    transport.page.mockResolvedValue({ items: [photo('anchor')], nextCursor: 'next' });
    await refresh();
    expect(transport.page.mock.lastCall?.[0].queryParameters.startAt).toBe('anchor');
    expect(w.tiles()).toEqual(['anchor']);
    await buttonIn(w.shell, 'Open Settings').trigger('click');
    await flushPromises();
    await refresh();
    transport.page.mockResolvedValue({ items: [] });
    await w.click('Return to Gallery');
    expect(w.tiles()).toEqual([]);
    expect(transport.page.mock.lastCall?.[0].queryParameters.startAt).toBeUndefined();
});

test('erasure forgets only its Capture and fences an older pending inbox response', async () => {
    const w = await mountWorkspace({ curation: true });
    const tag = () => w.view.get<HTMLInputElement>('input[aria-label="Add tag"]');
    const thumbnails = () => w.view.findAll('.filmstrip button').map(button => button.attributes('aria-label'));
    expect(thumbnails()).toEqual(['Photo a', 'Photo b']);
    await tag().setValue('Keep draft');
    await w.click('Next photograph');
    await tag().setValue('Erased draft');
    await w.click('Previous photograph');
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise);
    const active = refresh();
    await flushPromises();
    live.activity.value = { captureId: 'b', changeKind: 'erasure', sequence: 1 };
    await flushPromises();
    pending.resolve({ items: [photo('b')] });
    await active;
    expect(w.view.get('.inspector h2').text()).toBe('Photo a');
    expect(tag().element.value).toBe('Keep draft');
    expect(thumbnails()).toEqual(['Photo a']);
    transport.page.mockResolvedValue({ items: [photo('b')] });
    await refresh();
    expect(tag().element.value).toBe('');
});

test('confirmed missing Capture clears private detail/drafts while a temporary outage preserves them', async () => {
    for (const status of [404, 503]) {
        const w = await mountWorkspace({ curation: true });
        const tag = () => w.view.get<HTMLInputElement>('input[aria-label="Add tag"]');
        await tag().setValue('Synthetic draft');
        await w.click('Next photograph');
        transport.detail.mockRejectedValueOnce({ responseStatusCode: status });
        await w.click('Previous photograph');
        expect(w.view.text()).toMatch(status === 404 ? /no longer available/ : /Unable to open/);
        transport.page.mockResolvedValue({ items: [photo('a'), photo('b')] });
        await refresh();
        if (!w.view.find('input[aria-label="Add tag"]').exists() || w.view.get('.inspector h2').text() !== 'Photo a')
            await w.view.getComponent({ name: 'CaptureFilmstrip' }).vm.$emit('open', { id: 'a' });
        await flushPromises();
        expect(tag().element.value).toBe(status === 404 ? '' : 'Synthetic draft');
        w.dispose();
    }
});

test('page continuation boundary and detail transports abort when navigation supersedes the page owner', async () => {
    for (const continuation of ['boundary', 'detail']) {
        const pending = Promise.withResolvers<unknown>();
        transport.page.mockResolvedValueOnce({ items: continuation === 'boundary' ? [photo('cached')] : [] });
        const w = await mountWorkspace();
        transport.page.mockResolvedValue({ items: [photo('incoming')], nextCursor: continuation === 'boundary' ? 'next' : null });
        transport.detail.mockReturnValueOnce(pending.promise);
        const active = refresh();
        await flushPromises();
        const [id, options] = transport.detail.mock.lastCall!;
        expect(id).toBe(continuation === 'boundary' ? 'cached' : 'incoming');
        expect(options.options[0].signal.aborted).toBe(false);
        await w.nav('Sources');
        expect(options.options[0].signal.aborted).toBe(true);
        pending.resolve(photo('late'));
        await active;
        expect(w.view.text()).not.toContain('Photo late');
        w.dispose();
    }
});

test('a refresh adopts its new cursor and fills arrival gaps before retained history', async () => {
    transport.page.mockResolvedValueOnce({ items: [at('older', 1)] });
    const w = await mountWorkspace();
    transport.page.mockClear().mockResolvedValueOnce({ items: [at('newest', 3)], nextCursor: 'fresh' })
        .mockResolvedValueOnce({ items: [at('gap', 2), at('older', 1)], nextCursor: null });
    await refresh();
    expect(w.tiles()).toEqual(['newest', 'older']);
    intersect(); await flushPromises();
    expect(w.tiles()).toEqual(['newest', 'gap', 'older']);
    expect(transport.page.mock.calls.map(([o]) => o.queryParameters.cursor)).toEqual([undefined, 'fresh']);
});

test('partial Attention refresh checks ambiguous millisecond matches without dropping older history', async () => {
    initialFilters('Gallery', { workflows: ['Attention'] });
    transport.page.mockResolvedValueOnce({ items: [attention('z'), attention('a')] });
    const w = await mountWorkspace();
    transport.page.mockResolvedValue({ items: [attention('b')], nextCursor: 'next' });
    transport.detail.mockClear().mockImplementation(async id => attention(id, id === 'z' ? 1 : 0));
    await refresh();
    expect(w.tiles()).toEqual(['b', 'z']);
    expect(transport.detail.mock.calls.slice(0, 2).map(([id]) => id)).toEqual(['z', 'a']);
});

test('duplicate-only pages continue at a visible sentinel until unseen history arrives', async () => {
    transport.page.mockResolvedValueOnce({ items: [at('newest', 3), at('older', 1)], nextCursor: 'first' })
        .mockResolvedValueOnce({ items: [at('older', 1)], nextCursor: 'second' })
        .mockResolvedValueOnce({ items: [at('last', 0)] });
    const w = await mountWorkspace();
    intersect();
    await expect.poll(w.tiles).toEqual(['newest', 'older', 'last']);
    expect(transport.page.mock.calls.map(([o]) => o.queryParameters.cursor)).toEqual([undefined, 'first', 'second']);
});

test('ambiguous refresh reads preserve retry feedback on failure and cannot restore state after access loss', async () => {
    for (const lost of [false, true]) {
        transport.page.mockResolvedValueOnce({ items: [photo('cached')] });
        const w = await mountWorkspace();
        const pending = Promise.withResolvers<unknown>();
        transport.detail.mockReturnValueOnce(pending.promise);
        transport.page.mockResolvedValueOnce({ items: [photo('boundary')], nextCursor: 'next' });
        const work = refresh();
        await flushPromises();
        if (lost) { transport.page.mockRejectedValue({ responseStatusCode: 403 }); loseAccess(); }
        pending.reject({ responseStatusCode: 503 });
        await work;
        expect(w.tiles().sort()).toEqual(lost ? [] : ['boundary', 'cached']);
        expect(w.view.text().includes('could not be verified')).toBe(!lost);
        w.dispose();
    }
});

test('Retry repeats failed boundary verification instead of advancing the partial page', async () => {
    initialFilters('Gallery', { workflows: ['Attention'] });
    transport.page.mockResolvedValueOnce({ items: [attention('cached')] });
    const w = await mountWorkspace();
    transport.page.mockClear().mockResolvedValue({ items: [attention('boundary')], nextCursor: 'next' });
    transport.detail.mockRejectedValueOnce({ responseStatusCode: 503 }).mockImplementation(async id => attention(id, 0));
    await refresh();
    expect(w.tiles().sort()).toEqual(['boundary', 'cached']);
    await w.click('Retry');
    expect(transport.page.mock.calls.map(([o]) => o.queryParameters.cursor)).toEqual([undefined, undefined]);
    expect(w.tiles()).toEqual(['boundary']);
    expect(w.view.text()).not.toContain('could not be verified');
});

test('boundary tag verification matches the server exact tag predicate', async () => {
    initialFilters('Gallery', { tag: 'a' });
    const tagged = (id: string, tag: string) => photo(id, { curation: { revision: 1, tags: [tag] } });
    transport.page.mockResolvedValueOnce({ items: [tagged('cached', 'a')] });
    const w = await mountWorkspace();
    transport.page.mockResolvedValue({ items: [tagged('boundary', 'a')], nextCursor: 'next' });
    transport.detail.mockImplementation(async id => tagged(id, 'A'));
    await refresh();
    expect(w.tiles()).toEqual(['boundary']);
});

test('sort changes cannot repopulate old-direction history while navigating during the new first read', async () => {
    transport.page.mockResolvedValueOnce({ items: [at('a', 3), at('b', 2)], nextCursor: 'descending' });
    const w = await mountWorkspace();
    const pending = Promise.withResolvers<unknown>();
    transport.page.mockReturnValueOnce(pending.promise).mockResolvedValue({ items: [at('first', 0), at('boundary', 1)], nextCursor: 'ascending' });
    await w.view.get('[aria-label="Order"]').setValue('oldest');
    await w.nav('Delivery');
    await w.nav('Gallery');
    pending.resolve({ items: [at('stale', 3)] });
    await flushPromises();
    await expect.poll(w.tiles).toEqual(['first', 'boundary']);
    expect(transport.page.mock.lastCall![0].queryParameters.oldestFirst).toBe(true);
});

test('a complete Attention refresh removes cached photographs whose attention has cleared', async () => {
    initialFilters('Gallery', { workflows: ['Attention'] });
    transport.page.mockResolvedValueOnce({ items: [attention('formerly-blocked')], nextCursor: 'old' });
    const w = await mountWorkspace();
    transport.page.mockResolvedValue({ items: [attention('blocked')] });
    await refresh();
    expect(w.tiles()).toEqual(['blocked']);
    expect(transport.page.mock.lastCall![0].queryParameters.workflows).toEqual(['Attention']);
    const count = transport.page.mock.calls.length;
    intersect();
    await flushPromises();
    expect(transport.page).toHaveBeenCalledTimes(count);
    const noRoute = photo('no-route', { deliverySummary: { noRoute: 1 } });
    transport.page.mockResolvedValue({ items: [noRoute] });
    await w.click('All states');
    expect(w.view.get('.tile-delivery-action').attributes('title')).toBe('No matching delivery rule');
});
