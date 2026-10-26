import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import * as Vue from 'vue';

let testDocument;
class MockElement {
    constructor(name, buttons = []) { this.name = name; this.buttons = buttons; this.isConnected = true; this.attrs = new Map(); this.focuses = []; }
    setAttribute(name, value) { this.attrs.set(name, value); }
    removeAttribute(name) { this.attrs.delete(name); }
    getAttribute(name) { return this.attrs.get(name); }
    focus(options) { testDocument.activeElement = this; this.focuses.push(options); }
    requestFullscreen() {
        if (this.requestFullscreenHandler) return this.requestFullscreenHandler();
        testDocument.fullscreenElement = this;
        return Promise.resolve();
    }
    closest() { return null; }
    querySelectorAll() { return this.buttons; }
}

function fixture() {
    const listeners = new Map();
    const shell = new MockElement('app-shell');
    const first = new MockElement('first-button'), last = new MockElement('last-button');
    const viewer = new MockElement('viewer', [first, last]);
    const origin = new MockElement('origin');
    const document = {
        activeElement: origin,
        fullscreenElement: undefined,
        addEventListener: (name, callback) => listeners.set(name, callback),
        removeEventListener: name => listeners.delete(name),
        querySelector: () => shell,
        exitCalls: 0,
        exitFullscreen() { this.exitCalls++; this.fullscreenElement = undefined; },
    };
    testDocument = document;
    const chrome = {
        state: { visible: true }, starts: [], stops: 0,
        start(pinned) { this.state.visible = true; this.starts.push(pinned); },
        stop() { this.state.visible = true; this.stops++; },
        keyboard() { this.state.visible = true; },
        pointer() { this.state.visible = true; },
        focus() { this.state.visible = true; },
    };
    const context = vm.createContext({ exports: {}, document, HTMLElement: MockElement, require: () => Vue });
    const source = readFileSync(new URL('../app/composables/useViewerSession.ts', import.meta.url), 'utf8');
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    let session, opened = 0;
    const closed = [];
    const active = Vue.ref(false), element = Vue.shallowRef(viewer);
    const renderer = Vue.createRenderer({
        createElement: type => ({ type, children: [] }),
        createText: text => ({ text }),
        createComment: text => ({ comment: text }),
        setText(node, text) { node.text = text; },
        setElementText(node, text) { node.text = text; },
        patchProp() {},
        insert(node, parent) { parent.children ??= []; parent.children.push(node); },
        remove(node) { node.removed = true; },
        parentNode() { return null; },
        nextSibling() { return null; },
    });
    const app = renderer.createApp({
        setup() {
            session = context.exports.useViewerSession({
                active, element, chrome, pinned: () => true,
                onOpen: () => opened++, onClose: (updateUrl, wasActive) => closed.push({ updateUrl, wasActive }),
                onNavigate: direction => closed.push({ navigate: direction }),
                onCurationKey: event => event.key === 'r',
            });
            return () => null;
        },
    });
    app.mount({ children: [] });
    function key(key, extra = {}) {
        const event = { key, target: viewer, shiftKey: false, prevented: false, stopped: false,
            preventDefault() { this.prevented = true; }, stopPropagation() { this.stopped = true; }, ...extra };
        listeners.get('keydown')(event);
        return event;
    }
    return { session, active, viewer, origin, shell, first, last, chrome, document, listeners, closed, app, key, get opened() { return opened; } };
}

test('viewer session owns fullscreen focus, Escape, keyboard trap, and navigation delegation', async () => {
    const view = fixture();
    await view.session.enter();
    assert.equal(view.active.value, true);
    assert.equal(view.opened, 1);
    assert.deepEqual(view.chrome.starts, [true]);
    assert.equal(view.shell.getAttribute('inert'), '');
    assert.equal(view.viewer.focuses.length, 1);

    view.chrome.state.visible = false;
    const hiddenTab = view.key('Tab');
    assert.equal(hiddenTab.prevented, true);
    await Vue.nextTick();
    assert.equal(view.first.focuses.length, 1, 'Tab reveals chrome and focuses its first control');

    const backTab = view.key('Tab', { shiftKey: true });
    assert.equal(backTab.prevented, true);
    assert.equal(view.last.focuses.length, 1, 'Shift+Tab wraps from the viewer to the last control');
    view.key('ArrowLeft');
    assert.deepEqual(view.closed.at(-1), { navigate: -1 });
    view.key('r');
    assert.deepEqual(view.closed.at(-1), { navigate: -1 }, 'curation keys remain delegated to the shell');

    view.key('Escape');
    await Vue.nextTick();
    assert.equal(view.active.value, false);
    assert.equal(view.shell.getAttribute('inert'), undefined);
    assert.equal(view.origin.focuses.at(-1)?.preventScroll, true);
    assert.deepEqual(view.closed.at(-1), { updateUrl: true, wasActive: true });
    view.app.unmount();
    assert.equal(view.listeners.size, 0);
});

test('external fullscreen exit and unmount clean up the active viewer session', async () => {
    const view = fixture();
    await view.session.enter();
    view.document.fullscreenElement = view.viewer;
    view.document.fullscreenElement = undefined;
    view.listeners.get('fullscreenchange')();
    assert.equal(view.active.value, false);
    assert.deepEqual(view.closed.at(-1), { updateUrl: true, wasActive: true });
    await view.session.enter();
    view.app.unmount();
    assert.equal(view.listeners.size, 0);
    assert.equal(view.shell.getAttribute('inert'), undefined);
    assert.equal(view.active.value, false);
    assert.deepEqual(view.closed.at(-1), { updateUrl: false, wasActive: true });
});

test('a fullscreen grant that resolves after close is exited without reopening the viewer', async () => {
    const view = fixture();
    let grant;
    view.viewer.requestFullscreenHandler = () => new Promise(resolve => {
        grant = () => { view.document.fullscreenElement = view.viewer; resolve(); };
    });
    const entering = view.session.enter();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(typeof grant, 'function', 'the browser fullscreen request is pending');
    view.session.close(false);
    assert.equal(view.active.value, false);
    assert.equal(view.shell.getAttribute('inert'), undefined);
    grant();
    await entering;
    assert.equal(view.document.fullscreenElement, undefined, 'the late fullscreen grant is exited');
    assert.equal(view.document.exitCalls, 1);
    assert.equal(view.active.value, false);
    view.app.unmount();
});

test('a rejected fullscreen exit does not change logical closure or leak a rejection', async () => {
    const view = fixture();
    await view.session.enter();
    view.document.exitFullscreen = () => Promise.reject(new Error('synthetic fullscreen exit failure'));
    view.session.close(false);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(view.active.value, false);
    assert.equal(view.shell.getAttribute('inert'), undefined);
    view.app.unmount();
});
