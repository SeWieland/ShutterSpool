import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';

function loadGrid() {
    const lifecycle = { mounted: undefined, unmounted: undefined, watches: [] };
    const callbacks = new Map(), frames = new Map(), listeners = new Map();
    let nextFrame = 0, observer;
    class ResizeObserverMock {
        constructor(callback) { this.callback = callback; observer = this; }
        observe(element) { this.element = element; }
        unobserve() {}
        disconnect() { this.disconnected = true; }
    }
    const context = vm.createContext({ exports: {}, require: () => ({
        ref: value => ({ value }), watch: (...args) => lifecycle.watches.push(args),
        onMounted: callback => lifecycle.mounted = callback,
        onUnmounted: callback => lifecycle.unmounted = callback,
    }),
        ResizeObserver: ResizeObserverMock,
        requestAnimationFrame: callback => { const id = ++nextFrame; frames.set(id, callback); return id; },
        cancelAnimationFrame: id => callbacks.set('cancelled', id),
        getComputedStyle: () => ({ gridTemplateColumns: '1fr 1fr 1fr', rowGap: '10px' }),
        window: { innerHeight: 800, addEventListener: (name, fn) => listeners.set(name, fn), removeEventListener: name => listeners.delete(name) },
    });
    const source = readFileSync(new URL('../app/composables/useGalleryGrid.ts', import.meta.url), 'utf8');
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { api: context.exports, lifecycle, frames, listeners, get observer() { return observer; }, nextFrame() { const [id, callback] = [...frames.entries()][0] ?? []; if (callback) { frames.delete(id); callback(); } }, callbacks };
}

test('grid window keeps spacer boundaries row-aligned and clamps at both list ends', () => {
    const { api } = loadGrid();
    assert.deepEqual({ ...api.galleryGridWindow(1000, 1200, 210, 3, 800) }, { start: 6, end: 42 });
    const wideWindow = api.galleryGridWindow(1000, 9000, 210, 7, 800);
    assert.equal((wideWindow.end - wideWindow.start) % 7, 0);
    assert.ok(wideWindow.end - wideWindow.start <= 180);
    assert.deepEqual({ ...api.galleryGridWindow(10, 9000, 210, 3, 800) }, { start: 9, end: 10 });
    assert.deepEqual({ ...api.galleryGridWindow(0, 0, 0, 0, 800) }, { start: 0, end: 0 });
});

test('grid resize and scroll share one frame and observers and listeners are released on unmount', () => {
    const fixture = loadGrid();
    const grid = { getBoundingClientRect: () => ({ top: -1200 }), querySelector: () => ({ getBoundingClientRect: () => ({ height: 200 }) }) };
    const element = { value: grid };
    const count = { value: 1000 }, enabled = { value: true };
    const state = fixture.api.useGalleryGrid(count, enabled, { value: 'medium|4 / 3|fill' });
    state.element.value = element.value;
    fixture.lifecycle.mounted();
    fixture.listeners.get('scroll')();
    fixture.observer.callback();
    assert.equal(fixture.frames.size, 1);
    fixture.nextFrame();
    assert.equal(state.columns.value, 3);
    assert.equal(state.tileHeight.value, 210);
    assert.equal(state.start.value, 6);
    assert.equal(state.end.value, 42);
    fixture.lifecycle.unmounted();
    assert.equal(fixture.observer.disconnected, true);
    assert.equal(fixture.listeners.has('scroll'), false);
    assert.equal(fixture.listeners.has('resize'), false);
});
