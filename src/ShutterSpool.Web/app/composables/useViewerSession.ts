import { nextTick, onMounted, onUnmounted, type Ref } from 'vue';
import type { FullscreenChrome } from '../lib/fullscreen-chrome';

export interface ViewerSessionOptions {
    active: Ref<boolean>;
    element: Ref<HTMLElement | undefined>;
    chrome: FullscreenChrome;
    pinned: () => boolean;
    onOpen: () => void;
    onClose: (updateUrl: boolean, wasActive: boolean) => void;
    onNavigate: (direction: -1 | 1) => void;
    onCurationKey: (event: KeyboardEvent) => boolean;
}

export function useViewerSession(options: ViewerSessionOptions) {
    let focusBeforeViewer: HTMLElement | null = null;
    let sequence = 0;
    let suppressClick = false;
    let fullscreenAttempt: { request: number; element: HTMLElement } | undefined;
    let fullscreenOwner: { request: number; element: HTMLElement } | undefined;

    async function exitFullscreenSafely() {
        try {
            await document.exitFullscreen?.();
        } catch {
            // Logical viewer closure must not depend on browser fullscreen support.
        }
    }

    async function enter() {
        if (options.active.value || typeof document === 'undefined') return;
        const request = ++sequence;
        focusBeforeViewer = document.activeElement instanceof HTMLElement ? document.activeElement : null;
        options.active.value = true;
        options.chrome.start(options.pinned());
        options.onOpen();
        document.querySelector('.app-shell')?.setAttribute('inert', '');
        await nextTick();
        const viewer = options.element.value;
        if (request !== sequence || !options.active.value || !viewer) return;
        viewer.focus();
        try {
            fullscreenAttempt = { request, element: viewer };
            await viewer.requestFullscreen?.();
        } catch {
            // The dialog remains available when browser fullscreen is denied.
        } finally {
            if (fullscreenAttempt?.request === request) {
                fullscreenAttempt = undefined;
                if (request !== sequence || !options.active.value) {
                    if (document.fullscreenElement === viewer) await exitFullscreenSafely();
                } else if (document.fullscreenElement === viewer) {
                    fullscreenOwner = { request, element: viewer };
                }
            }
        }
    }

    function close(updateUrl = true) {
        const wasActive = options.active.value;
        options.chrome.stop();
        options.onClose(updateUrl, wasActive);
        suppressClick = false;
        if (!wasActive) return;

        ++sequence;
        options.active.value = false;
        document.querySelector('.app-shell')?.removeAttribute('inert');
        const owner = fullscreenOwner;
        fullscreenOwner = undefined;
        if (owner && document.fullscreenElement === owner.element) {
            void exitFullscreenSafely();
        }
        const returnFocus = focusBeforeViewer;
        focusBeforeViewer = null;
        const request = sequence;
        void nextTick(() => {
            if (request === sequence && !options.active.value && returnFocus?.isConnected)
                returnFocus.focus({ preventScroll: true });
        });
    }

    function fullscreenChanged() {
        if (!document.fullscreenElement && options.active.value) close();
    }

    function viewerButtons() {
        return [...(options.element.value?.querySelectorAll<HTMLElement>('button:not(:disabled),select:not(:disabled)') ?? [])];
    }

    function keyboard(event: KeyboardEvent) {
        if (!options.active.value) return;
        const hidden = !options.chrome.state.visible;
        suppressClick = false;
        options.chrome.keyboard();
        const target = event.target as HTMLElement | null;
        if (target?.closest?.('input,textarea,select')) return;

        if (event.key === 'Tab') {
            const request = sequence;
            const buttons = viewerButtons();
            const first = buttons[0];
            const last = buttons.at(-1);
            if (hidden) {
                event.preventDefault();
                void nextTick(() => {
                    if (request !== sequence || !options.active.value) return;
                    const visibleButtons = viewerButtons();
                    (event.shiftKey ? visibleButtons.at(-1) : visibleButtons[0])?.focus();
                });
                return;
            }
            if (event.shiftKey && (document.activeElement === first || document.activeElement === options.element.value)) {
                event.preventDefault();
                last?.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
                event.preventDefault();
                first?.focus();
            }
            return;
        }
        if (event.key === 'Escape') {
            close();
            return;
        }
        if (options.onCurationKey(event)) return;
        if (event.key === 'ArrowLeft') options.onNavigate(-1);
        if (event.key === 'ArrowRight') options.onNavigate(1);
    }

    function focus(event: FocusEvent) {
        const target = event.target as HTMLElement | null;
        options.chrome.focus(!!target?.closest?.('.viewer-chrome'));
    }

    function pointer(down = false) {
        if (down) suppressClick = !options.chrome.state.visible;
        options.chrome.pointer(down);
    }

    function click(event: MouseEvent) {
        if (!suppressClick) return;
        suppressClick = false;
        event.preventDefault();
        event.stopPropagation();
    }

    function dispose() {
        document.removeEventListener('keydown', keyboard);
        document.removeEventListener('fullscreenchange', fullscreenChanged);
        const wasActive = options.active.value;
        options.chrome.stop();
        options.active.value = false;
        document.querySelector('.app-shell')?.removeAttribute('inert');
        const owner = fullscreenOwner;
        fullscreenOwner = undefined;
        if (owner && document.fullscreenElement === owner.element) {
            void exitFullscreenSafely();
        }
        if (wasActive) options.onClose(false, true);
        focusBeforeViewer = null;
        sequence++;
    }

    onMounted(() => {
        document.addEventListener('keydown', keyboard);
        document.addEventListener('fullscreenchange', fullscreenChanged);
    });
    onUnmounted(dispose);

    return { enter, close, fullscreenChanged, keyboard, focus, pointer, click };
}
