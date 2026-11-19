import { expect, test } from 'vitest';
import { useCaptureBrowser } from '../../app/composables/useCaptureBrowser';
import { mapBounded } from '../../app/composables/useCurationVisit';

test('Capture browser retains page cancellation ownership until continuation explicitly finishes', async () => {
    const browser = useCaptureBrowser();
    const parent = new AbortController();
    const completed = browser.requestPage({ query: {}, parentSignal: parent.signal, read: async () => ({ items: [] }) });
    await completed.response;
    const signal = browser.requestSignal(completed.request);
    expect(signal?.aborted).toBe(false);
    parent.abort();
    expect(signal?.aborted).toBe(true);
    expect(browser.isCurrent(completed.request)).toBe(false);
    completed.finish();

    const first = browser.beginRequest();
    const original = browser.requestSignal(first);
    browser.rows.value = [{ id: 'gallery' }];
    browser.save('Gallery');
    browser.rows.value = [{ id: 'curation' }];
    browser.save('Curation');
    const second = browser.beginRequest();
    expect(original?.aborted).toBe(true);
    expect(browser.isCurrent(first)).toBe(false);
    browser.restore('Gallery');
    expect(browser.rows.value.map(row => row.id)).toEqual(['gallery']);
    expect(browser.requestSignal(second)?.aborted).toBe(false);
    browser.cancelRequests();
    expect(browser.requestSignal(second)).toBeUndefined();
});

test('bulk metadata reads preserve selection order while honoring their four-request bound', async () => {
    const items = Array.from({ length: 13 }, (_, index) => index);
    const reads = items.map(() => Promise.withResolvers<void>());
    const starts: number[] = [];
    let active = 0;
    let peak = 0;
    const result = mapBounded(items, 4, async value => {
        starts.push(value);
        peak = Math.max(peak, ++active);
        await reads[value]!.promise;
        active--;
        return value * 2;
    });
    expect(starts).toEqual([0, 1, 2, 3]);
    for (let index = 3; index < items.length; index++) {
        reads[index]!.resolve();
        await reads[index]!.promise;
        await Promise.resolve();
    }
    for (const read of reads.slice(0, 3).reverse()) read.resolve();
    expect(await result).toEqual(items.map(value => value * 2));
    expect(peak).toBe(4);
});
