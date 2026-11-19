import { mountSuspended } from '@nuxt/test-utils/runtime';
import { expect, onTestFinished, test } from 'vitest';
import CaptureRatingFilter from '../../app/components/CaptureRatingFilter.vue';

test('rating filter extends the interval once per Shift+Enter or Shift+Space press', async () => {
    const filter = await mountSuspended(CaptureRatingFilter, {
        route: '',
        props: { rating: null, maximum: null },
    });
    onTestFinished(() => filter.unmount());
    const button = (rating: number) => filter.get<HTMLButtonElement>(`button[data-rating="${rating}"]`).element;
    const allRatings = filter.get<HTMLButtonElement>('button:not([data-rating])').element;
    const changes = () => (filter.emitted<[{ rating: number | null; maximum: number | null }]>('change') ?? [])
        .map(([{ rating, maximum }]) => [rating, maximum]);

    let spaceArmed = false;
    function dispatchKey(phase: 'keydown' | 'keyup', target: HTMLButtonElement, key: string, modifiers: KeyboardEventInit = {}) {
        const event = new KeyboardEvent(phase, { key, bubbles: true, cancelable: true, ...modifiers });
        target.dispatchEvent(event);
        // Model the browser's ordinary button activation with an unmodified click.
        if (phase === 'keydown' && !event.defaultPrevented && key === 'Enter') target.click();
        if (phase === 'keydown' && !event.defaultPrevented && key === ' ') spaceArmed = true;
        if (phase === 'keyup' && key === ' ') {
            if (!event.defaultPrevented && spaceArmed) target.click();
            spaceArmed = false;
        }
        return event.defaultPrevented;
    }
    const keyDown = (target: HTMLButtonElement, key: string, modifiers?: KeyboardEventInit) => dispatchKey('keydown', target, key, modifiers);
    const keyUp = (target: HTMLButtonElement, key: string) => dispatchKey('keyup', target, key);

    for (const [key, end, range] of [['Enter', -1, [-1, 3]], [' ', 5, [3, 5]]] as const) {
        const start = changes().length;
        const endpoint = button(end);
        expect(keyDown(button(3), key)).toBe(false);
        expect(keyUp(button(3), key)).toBe(false);
        expect(keyDown(endpoint, key, { shiftKey: true })).toBe(true);
        expect(keyDown(endpoint, key, { shiftKey: true, repeat: true })).toBe(true);
        expect(keyUp(endpoint, 'Shift')).toBe(false);
        const repeatPrevented = keyDown(endpoint, key, { repeat: true });
        const releasePrevented = keyUp(endpoint, key);
        expect(changes().slice(start)).toEqual([[3, 3], range]);
        expect(repeatPrevented).toBe(true);
        expect(releasePrevented).toBe(true);
        expect(keyDown(button(2), key)).toBe(false);
        expect(keyUp(button(2), key)).toBe(false);
        expect(changes().at(-1)).toEqual([2, 2]);
    }

    expect(keyDown(button(2), 'Tab', { shiftKey: true })).toBe(false);
    expect(keyDown(allRatings, 'Enter', { shiftKey: true })).toBe(false);
    expect(changes().at(-1)).toEqual([null, null]);

    expect(keyDown(button(2), 'Enter', { shiftKey: true })).toBe(true);
    button(2).dispatchEvent(new FocusEvent('focusout', { bubbles: true, relatedTarget: null }));
    expect(keyUp(button(2), 'Enter')).toBe(false);

    expect(keyDown(button(2), ' ', { shiftKey: true })).toBe(true);
    expect(keyDown(button(4), ' ')).toBe(false);
    expect(keyUp(button(4), ' ')).toBe(false);
    expect(changes().at(-1)).toEqual([4, 4]);
});
