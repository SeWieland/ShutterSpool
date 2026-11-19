import assert from 'node:assert/strict';
import { mount } from '@vue/test-utils';
import { onTestFinished, test, vi } from 'vitest';
import { defineComponent, h, nextTick, ref, shallowRef } from 'vue';
import { useViewerSession } from '../../app/composables/useViewerSession';
import { chromeState, FullscreenChrome } from '../../app/lib/fullscreen-chrome';

function fixture() {
    const container = document.createElement('div');
    const shell = document.createElement('div');
    shell.className = 'app-shell';
    const origin = document.createElement('button');
    origin.textContent = 'Open viewer';
    shell.append(origin);
    document.body.append(shell, container);
    const active = ref(false);
    const element = shallowRef<HTMLElement>();
    const chrome = new FullscreenChrome(chromeState());
    const start = vi.spyOn(chrome, 'start');
    const opened = vi.fn();
    const closed = vi.fn<(updateUrl: boolean, wasActive: boolean) => void>();
    const navigate = vi.fn();
    const curation = vi.fn((event: KeyboardEvent) => event.key === 'r');
    const add = vi.spyOn(document, 'addEventListener');
    const remove = vi.spyOn(document, 'removeEventListener');

    let fullscreen: Element | null = null;
    const fullscreenDescriptor = Object.getOwnPropertyDescriptor(document, 'fullscreenElement');
    const exitDescriptor = Object.getOwnPropertyDescriptor(document, 'exitFullscreen');
    const exit = vi.fn(async () => { fullscreen = null; });
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: () => fullscreen });
    Object.defineProperty(document, 'exitFullscreen', { configurable: true, value: exit });

    let session!: ReturnType<typeof useViewerSession>;
    const host = mount(defineComponent({
        setup() {
            session = useViewerSession({
                active, element, chrome, pinned: () => true,
                onOpen: opened, onClose: closed, onNavigate: navigate, onCurationKey: curation,
            });
            return () => h('div', [
                h('div', { ref: element, tabindex: -1, id: 'viewer' }, [
                    h('button', { id: 'first' }, 'First control'),
                    h('button', { id: 'last' }, 'Last control'),
                ]),
            ]);
        },
    }), { attachTo: container });

    let mounted = true;
    function unmount() {
        if (mounted) { host.unmount(); mounted = false; }
    }
    onTestFinished(() => {
        unmount();
        container.remove();
        shell.remove();
        if (fullscreenDescriptor) Object.defineProperty(document, 'fullscreenElement', fullscreenDescriptor);
        else Reflect.deleteProperty(document, 'fullscreenElement');
        if (exitDescriptor) Object.defineProperty(document, 'exitFullscreen', exitDescriptor);
        else Reflect.deleteProperty(document, 'exitFullscreen');
        vi.restoreAllMocks();
    });

    const viewer = host.get<HTMLElement>('#viewer').element;
    const first = host.get<HTMLButtonElement>('#first').element;
    const last = host.get<HTMLButtonElement>('#last').element;
    const request = vi.fn(async () => { fullscreen = viewer; });
    Object.defineProperty(viewer, 'requestFullscreen', { value: request });
    const originFocus = vi.spyOn(origin, 'focus');
    origin.focus();

    function key(key: string, init: KeyboardEventInit = {}) {
        const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...init });
        viewer.dispatchEvent(event);
        return event;
    }
    function listenersReleased() {
        for (const name of ['keydown', 'fullscreenchange']) {
            const attached = add.mock.calls.find(([event]) => event === name);
            assert.ok(attached);
            assert.ok(remove.mock.calls.some(([event, callback]) => event === name && callback === attached[1]));
        }
    }
    return { session, active, viewer, origin, first, last, shell, chrome, start, opened, closed, navigate, curation, request, exit,
        originFocus, key, unmount, listenersReleased,
        setFullscreen: (element: Element | null) => { fullscreen = element; },
    };
}

test('viewer session owns fullscreen focus, Escape, keyboard trap, and navigation delegation', async () => {
    const view = fixture();
    await view.session.enter();
    assert.equal(view.active.value, true);
    assert.equal(view.opened.mock.calls.length, 1);
    assert.deepEqual(view.start.mock.calls, [[true]]);
    assert.equal(view.shell.getAttribute('inert'), '');
    assert.equal(document.activeElement, view.viewer);

    view.chrome.state.visible = false;
    assert.equal(view.key('Tab').defaultPrevented, true);
    await nextTick();
    assert.equal(document.activeElement, view.first);
    assert.equal(view.key('Tab', { shiftKey: true }).defaultPrevented, true);
    assert.equal(document.activeElement, view.last);
    view.key('ArrowLeft');
    assert.deepEqual(view.navigate.mock.calls, [[-1]]);
    view.key('r');
    assert.equal(view.curation.mock.lastCall?.[0].key, 'r');
    assert.deepEqual(view.navigate.mock.calls, [[-1]]);

    view.key('Escape');
    await nextTick();
    assert.equal(view.active.value, false);
    assert.equal(view.shell.hasAttribute('inert'), false);
    assert.equal(document.activeElement, view.origin);
    assert.deepEqual(view.originFocus.mock.lastCall, [{ preventScroll: true }]);
    assert.deepEqual(view.closed.mock.lastCall, [true, true]);
    view.unmount();
    view.listenersReleased();
});

test('external fullscreen exit and unmount clean up the active viewer session', async () => {
    const view = fixture();
    await view.session.enter();
    view.setFullscreen(null);
    document.dispatchEvent(new Event('fullscreenchange'));
    assert.equal(view.active.value, false);
    assert.deepEqual(view.closed.mock.lastCall, [true, true]);
    await view.session.enter();
    view.unmount();
    view.listenersReleased();
    assert.equal(view.shell.hasAttribute('inert'), false);
    assert.equal(view.active.value, false);
    assert.deepEqual(view.closed.mock.lastCall, [false, true]);
});

test('a fullscreen grant that resolves after close is exited without reopening the viewer', async () => {
    const view = fixture();
    const grant = Promise.withResolvers<void>();
    view.request.mockReturnValueOnce(grant.promise);
    const entering = view.session.enter();
    await nextTick();
    assert.equal(view.request.mock.calls.length, 1);
    view.session.close(false);
    assert.equal(view.active.value, false);
    assert.equal(view.shell.hasAttribute('inert'), false);
    view.setFullscreen(view.viewer);
    grant.resolve();
    await entering;
    assert.equal(document.fullscreenElement, null);
    assert.equal(view.exit.mock.calls.length, 1);
    assert.equal(view.active.value, false);
});

test('a rejected fullscreen exit does not change logical closure or leak a rejection', async () => {
    const view = fixture();
    await view.session.enter();
    view.exit.mockRejectedValueOnce(new Error('Synthetic fullscreen exit failure'));
    view.session.close(false);
    await nextTick();
    assert.equal(view.exit.mock.calls.length, 1);
    assert.equal(view.active.value, false);
    assert.equal(view.shell.hasAttribute('inert'), false);
});
