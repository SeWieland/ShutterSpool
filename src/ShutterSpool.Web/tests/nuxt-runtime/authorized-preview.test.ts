import assert from 'node:assert/strict';
import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, test, vi } from 'vitest';
import AuthorizedPreview from '../../app/components/AuthorizedPreview.vue';

const { derivative, accessLost } = vi.hoisted(() => ({
    derivative: vi.fn(),
    accessLost: new Set<() => void>(),
}));
vi.mock('../../app/lib/api', () => ({ derivative }));
vi.mock('../../app/lib/session', () => ({
    onAccessLost(callback: () => void) {
        accessLost.add(callback);
        return () => accessLost.delete(callback);
    },
}));

let cleanup: (() => void) | undefined;
afterEach(() => {
    cleanup?.();
    cleanup = undefined;
    derivative.mockReset();
    accessLost.clear();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
});

async function preview() {
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    let objectId = 0;
    vi.spyOn(URL, 'createObjectURL').mockImplementation(() => `blob:synthetic-preview-${++objectId}`);
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    vi.stubGlobal('IntersectionObserver', class {
        constructor(private notify: IntersectionObserverCallback) {}
        observe() { this.notify([{ isIntersecting: true } as IntersectionObserverEntry], this as unknown as IntersectionObserver); }
        disconnect() {}
    });
    const requests: Array<{
        captureId: string;
        profile: string;
        signal: AbortSignal;
        etag?: string;
        dimension?: number;
        resolve: (value: Response) => void;
        reject: (error: Error) => void;
    }> = [];
    derivative.mockImplementation((captureId, profile, signal, etag, dimension) => {
        const pending = Promise.withResolvers<Response>();
        requests.push({ captureId, profile, signal, etag, dimension, resolve: pending.resolve, reject: pending.reject });
        signal.addEventListener('abort', () => pending.reject(new DOMException('Aborted', 'AbortError')), { once: true });
        return pending.promise.then(response => ({ response, blob: response.ok ? new Blob(['synthetic preview']) : undefined }));
    });
    const wrapper = mount(AuthorizedPreview, {
        props: { captureId: 'capture', profile: 'detail', name: 'Synthetic Capture', sourceId: 'jpeg', generation: 0, forceGeneration: 0, maximumDimension: 512 },
    });
    cleanup = () => wrapper.unmount();
    return {
        wrapper, requests, revoke,
        image: () => wrapper.get('img'),
        url: () => wrapper.find('img').exists() ? wrapper.get('img').attributes('src') : undefined,
        display: () => (wrapper.emitted<[string]>('display') ?? []).map(([state]) => state),
        clearAccess: () => { accessLost.forEach(callback => callback()); },
    };
}

function ready(etag?: string) {
    return new Response('synthetic preview', { headers: { 'X-Preview-State': 'ready', ...(etag ? { ETag: etag } : {}) } });
}

test('a delayed derivative survives multiple ordinary reconciliations and reports unavailable', async () => {
    const view = await preview();
    await view.wrapper.setProps({ generation: 1 });
    await view.wrapper.setProps({ generation: 2 });
    assert.equal(view.requests.length, 1);
    assert.equal(view.requests[0]!.signal.aborted, false);
    view.requests[0]!.resolve(new Response('<svg/>', { headers: { 'X-Preview-State': 'unavailable', 'Content-Type': 'image/svg+xml' } }));
    await flushPromises();
    assert.match(view.wrapper.text(), /Preview unavailable/);
});

test('a ready hint replaces an older pending read with a fresh authorized derivative request', async () => {
    const view = await preview();
    await view.wrapper.setProps({ forceGeneration: 1 });
    assert.equal(view.requests.length, 2);
    assert.equal(view.requests[0]!.signal.aborted, true);
    assert.equal(view.requests[1]!.captureId, 'capture');
    assert.equal(view.requests[1]!.profile, 'detail');
    view.requests[1]!.resolve(ready('published'));
    await flushPromises();
    assert.equal(view.image().attributes('alt'), 'Synthetic Capture');
    const url = view.url()!;
    assert.match(url, /^blob:/);
    view.clearAccess();
    await flushPromises();
    assert.equal(view.url(), undefined);
    assert.deepEqual(view.revoke.mock.calls, [[url]]);
});

test('a successful 304 restores ready after a failed revalidation without replacing the cached blob', async () => {
    const view = await preview();
    view.requests[0]!.resolve(ready('published'));
    await flushPromises();
    const cachedUrl = view.url();
    await view.wrapper.setProps({ generation: 1 });
    view.requests[1]!.reject(new TypeError('Network unavailable'));
    await flushPromises();
    assert.match(view.wrapper.text(), /Preview unavailable/);
    assert.equal(view.url(), cachedUrl);
    await view.wrapper.setProps({ generation: 2 });
    assert.equal(view.requests[2]!.etag, 'published');
    view.requests[2]!.resolve(new Response(null, { status: 304 }));
    await flushPromises();
    assert.equal(view.image().attributes('alt'), 'Synthetic Capture');
    assert.equal(view.url(), cachedUrl);
});

test('explicit invalidation drops cached private bytes before revalidation and 404 cannot retain them', async () => {
    const view = await preview();
    view.requests[0]!.resolve(ready('published'));
    await flushPromises();
    const cachedUrl = view.url()!;
    assert.match(cachedUrl, /^blob:/);
    await view.wrapper.setProps({ forceGeneration: 1 });
    assert.equal(view.url(), undefined);
    assert.deepEqual(view.revoke.mock.calls, [[cachedUrl]]);
    assert.equal(view.requests[1]!.etag, undefined);
    view.requests[1]!.resolve(new Response(null, { status: 404 }));
    await flushPromises();
    assert.equal(view.url(), undefined);
    assert.match(view.wrapper.text(), /Preview unavailable/);
});

test('a forbidden derivative reread clears previously authorized private bytes without relying on global sign-out', async () => {
    const view = await preview();
    view.requests[0]!.resolve(ready('previous'));
    await flushPromises();
    const cachedUrl = view.url()!;
    assert.match(cachedUrl, /^blob:/);
    await view.wrapper.setProps({ generation: 1 });
    assert.equal(view.requests[1]!.etag, 'previous');
    view.requests[1]!.resolve(new Response(null, { status: 403 }));
    await flushPromises();
    assert.equal(view.url(), undefined);
    assert.deepEqual(view.revoke.mock.calls, [[cachedUrl]]);
    await view.wrapper.setProps({ generation: 2 });
    assert.equal(view.requests[2]!.etag, undefined);
});

test('dimension replacement keeps the displayed derivative through loading and failure', async () => {
    const view = await preview();
    view.requests[0]!.resolve(ready('old'));
    await flushPromises();
    const previous = view.url();
    await view.wrapper.setProps({ maximumDimension: 1024 });
    assert.equal(view.url(), previous);
    assert.equal(view.requests[1]!.etag, undefined);
    assert.equal(view.requests[1]!.dimension, 1024);
    view.requests[1]!.reject(new TypeError('Offline'));
    await flushPromises();
    assert.equal(view.url(), previous);
    await view.wrapper.setProps({ maximumDimension: 2048 });
    view.requests[2]!.resolve(ready());
    await flushPromises();
    assert.notEqual(view.url(), previous);
    assert.equal(view.image().attributes('alt'), 'Synthetic Capture');
});

test('display readiness requires browser load, cache validation cannot bypass decode, and decode failure clears bytes', async () => {
    const view = await preview();
    view.requests[0]!.resolve(ready('published'));
    await flushPromises();
    assert.equal(view.display().includes('ready'), false);
    await view.wrapper.setProps({ generation: 1 });
    view.requests[1]!.resolve(new Response(null, { status: 304 }));
    await flushPromises();
    assert.equal(view.display().includes('ready'), false);
    await view.image().trigger('load');
    assert.equal(view.display().at(-1), 'ready');
    await view.image().trigger('error');
    assert.equal(view.display().at(-1), 'unavailable');
    assert.equal(view.url(), undefined);
});
