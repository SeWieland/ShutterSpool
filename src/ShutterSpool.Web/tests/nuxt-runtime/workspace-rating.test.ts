import { mountSuspended } from '@nuxt/test-utils/runtime';
import { expect, onTestFinished, test, vi } from 'vitest';
import CaptureRatingFilter from '../../app/components/CaptureRatingFilter.vue';
import { mountWorkspace, transport } from './workspace-mount';

async function mountFilter(rating: number | null = null, maximum: number | null = rating) {
    const filter = await mountSuspended(CaptureRatingFilter, { route: '', props: { rating, maximum } });
    onTestFinished(() => filter.unmount());
    const button = (value: number) => filter.get<HTMLButtonElement>(`[data-rating="${value}"]`);
    const changes = () => filter.emitted<[{ rating: number; maximum: number }]>('change')?.map(([range]) => [range.rating, range.maximum]) ?? [];
    const fieldset = filter.get('fieldset').element;
    function pointer(type: string, target: Element, buttons = 0) {
        target.dispatchEvent(new PointerEvent(type, { bubbles: true, button: 0, pointerId: 3, buttons, clientX: 10, clientY: 10 }));
    }
    return { filter, button, changes, fieldset, pointer };
}

test('rating filter highlights every signed value inside its selected interval', async () => {
    const { view } = await mountWorkspace();
    const filter = view.getComponent(CaptureRatingFilter);
    const button = (value: number) => filter.get(`[data-rating="${value}"]`);
    for (const [start, end] of [[-1, 3], [-1, 0], [0, 0], [2, 4]] as const) {
        await button(start).trigger('click');
        await button(end).trigger('click', { shiftKey: true });
        expect(transport.page.mock.lastCall?.[0].queryParameters).toMatchObject({ rating: start, ratingMaximum: end });
        const buttons = filter.findAll('button[data-rating]');
        expect(buttons).toHaveLength(7);
        for (const option of buttons) {
            const rating = Number(option.attributes('data-rating'));
            expect(option.attributes('aria-pressed')).toBe(String(rating >= start && rating <= end));
        }
    }
});

test('rating pointer press retains native exact click while dragging selects an interval and consumes only its click', async () => {
    const { button, changes, fieldset, pointer } = await mountFilter();
    const capture = vi.fn();
    Object.defineProperty(fieldset, 'setPointerCapture', { configurable: true, value: capture });
    vi.spyOn(document, 'elementFromPoint').mockReturnValue(button(5).element);
    onTestFinished(() => vi.restoreAllMocks());
    pointer('pointerdown', button(2).element, 1);
    expect(capture).not.toHaveBeenCalled();
    pointer('pointerup', button(2).element);
    await button(2).trigger('click');
    expect(changes()).toEqual([[2, 2]]);
    pointer('pointerdown', button(2).element, 1);
    pointer('pointermove', fieldset, 1);
    pointer('pointerup', fieldset);
    await button(5).trigger('click');
    expect(changes()).toEqual([[2, 2], [2, 5]]);
    expect(capture).toHaveBeenCalledWith(3);
    await button(4).trigger('click');
    expect(changes().at(-1)).toEqual([4, 4]);
});

test('Shift-clicking Rejected or Not Rated and dragging across zero retain the interval anchor', async () => {
    const { button, changes, fieldset, pointer } = await mountFilter(4, 5);
    Object.defineProperty(fieldset, 'setPointerCapture', { configurable: true, value: vi.fn() });
    vi.spyOn(document, 'elementFromPoint').mockReturnValue(button(0).element);
    onTestFinished(() => vi.restoreAllMocks());
    await button(0).trigger('click', { shiftKey: true });
    await button(-1).trigger('click', { shiftKey: true });
    pointer('pointerdown', button(4).element, 1);
    pointer('pointermove', fieldset, 1);
    pointer('pointerup', fieldset);
    expect(changes()).toEqual([[0, 4], [-1, 4], [0, 4]]);
});
