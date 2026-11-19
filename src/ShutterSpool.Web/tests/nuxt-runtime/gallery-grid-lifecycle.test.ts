import assert from 'node:assert/strict';
import { mount } from '@vue/test-utils';
import { onTestFinished, test, vi } from 'vitest';
import { defineComponent, h, ref } from 'vue';
import { useGalleryGrid } from '../../app/composables/useGalleryGrid';

test('grid resize and scroll share one frame and observers and listeners are released on unmount', () => {
    const frames = new Map<number, FrameRequestCallback>();
    let frameId = 0;
    let resize!: ResizeObserverCallback;
    const observe = vi.fn();
    const disconnect = vi.fn();
    const cancel = vi.fn((id: number) => frames.delete(id));
    const add = vi.spyOn(window, 'addEventListener');
    const remove = vi.spyOn(window, 'removeEventListener');
    const events = ['scroll', 'resize'] as const;
    vi.stubGlobal('ResizeObserver', class {
        constructor(callback: ResizeObserverCallback) { resize = callback; }
        observe = observe;
        unobserve = vi.fn();
        disconnect = disconnect;
    });
    vi.stubGlobal('requestAnimationFrame', (callback: FrameRequestCallback) => {
        frames.set(++frameId, callback);
        return frameId;
    });
    vi.stubGlobal('cancelAnimationFrame', cancel);
    vi.stubGlobal('getComputedStyle', () => ({ gridTemplateColumns: '1fr 1fr 1fr', rowGap: '10px' }));
    const viewport = Object.getOwnPropertyDescriptor(window, 'innerHeight')!;
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 800 });
    let state!: ReturnType<typeof useGalleryGrid>;
    let unmount: (() => void) | undefined;
    onTestFinished(() => {
        unmount?.();
        if (state) for (const event of events) window.removeEventListener(event, state.schedule);
        Object.defineProperty(window, 'innerHeight', viewport);
        vi.unstubAllGlobals();
        vi.restoreAllMocks();
    });

    const host = mount(defineComponent({
        setup() {
            state = useGalleryGrid(ref(1000), ref(true), ref('medium|4 / 3|fill'));
            return () => h('div', { ref: state.element }, [h('div', { class: 'gallery-tile' })]);
        },
    }));
    unmount = () => host.unmount();
    const grid = host.element;
    const tile = host.get('.gallery-tile').element;
    const bounds = vi.spyOn(grid, 'getBoundingClientRect').mockReturnValue(new DOMRect());
    vi.spyOn(tile, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 0, 200));
    const drawFrame = () => {
        assert.equal(frames.size, 1);
        const [id, callback] = [...frames][0]!;
        frames.delete(id);
        callback(0);
    };
    drawFrame();
    assert.equal(state.start.value, 0);
    bounds.mockReturnValue(new DOMRect(0, -1200, 0, 0));
    window.dispatchEvent(new Event('scroll'));
    assert.equal(frames.size, 1);
    const scrollFrame = [...frames.keys()];
    resize([], {} as ResizeObserver);
    assert.deepEqual([...frames.keys()], scrollFrame);
    drawFrame();
    assert.equal(state.columns.value, 3);
    assert.equal(state.tileHeight.value, 210);
    assert.equal(state.start.value, 6);
    assert.equal(state.end.value, 42);
    assert.ok(observe.mock.calls.some(([element]) => element === tile));

    window.dispatchEvent(new Event('resize'));
    assert.equal(frames.size, 1);
    host.unmount();
    unmount = undefined;
    assert.equal(disconnect.mock.calls.length, 1);
    assert.equal(cancel.mock.calls.length, 1);
    assert.equal(frames.size, 0);
    for (const event of events) {
        assert.ok(add.mock.calls.some(([type, callback]) => type === event && callback === state.schedule));
        assert.ok(remove.mock.calls.some(([type, callback]) => type === event && callback === state.schedule),
            `the registered ${event} listener must be removed`);
        window.dispatchEvent(new Event(event));
    }
    assert.equal(frames.size, 0);
});
