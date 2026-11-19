import { mountSuspended } from '@nuxt/test-utils/runtime';
import { expect, onTestFinished, test } from 'vitest';
import ApertureBackground from '../../app/components/ApertureBackground.vue';

async function mountAperture(props: Partial<InstanceType<typeof ApertureBackground>['$props']> = {}) {
    const lens = await mountSuspended(ApertureBackground, {
        route: '',
        props: { view: 'Gallery', motionEnabled: true, hidden: false, ...props },
    });
    onTestFinished(() => lens.unmount());
    const ring = lens.get<HTMLDivElement>('.aperture-focus-ring');
    const background = lens.get<HTMLDivElement>('.aperture-background').element;

    return {
        lens,
        ring,
        background,
        rotation: () => Number(ring.element.style.transform.match(/^rotate\((.+)deg\)$/)?.[1]),
        duration: () => background.style.getPropertyValue('--aperture-duration'),
    };
}

test('the focus ring progresses slightly clockwise and returns without accumulating rotation', async () => {
    const { lens, rotation } = await mountAperture({ view: 'Sources' });
    expect(rotation()).toBe(0);
    let previous = 0;
    for (const view of ['Curation', 'Gallery', 'Delivery'] as const) {
        await lens.setProps({ view });
        const next = rotation();
        expect(next).toBeGreaterThan(previous);
        expect(next - previous).toBeLessThanOrEqual(1.5);
        previous = next;
    }
    for (const view of ['Sources', 'Delivery', 'Gallery', 'Gallery'] as const) {
        await lens.setProps({ view });
    }
    expect(rotation()).toBe(3);
});

test('reduced motion keeps the same ring position across every tab', async () => {
    const { lens, rotation, duration } = await mountAperture({ view: 'Delivery' });
    await lens.setProps({ motionEnabled: false });
    const position = rotation();
    for (const view of ['Sources', 'Curation', 'Gallery', 'Delivery'] as const) {
        await lens.setProps({ view });
        expect(rotation()).toBe(position);
        expect(duration()).toBe('0ms');
    }
});

test('ring motion takes at least five seconds and fullscreen suppresses its transition', async () => {
    const { lens, background, duration } = await mountAperture();
    expect(duration()).toMatch(/^\d+ms$/);
    expect(Number.parseInt(duration(), 10)).toBeGreaterThanOrEqual(5000);
    await lens.setProps({ hidden: true });
    expect(background.style.display).toBe('none');
    expect(duration()).toBe('0ms');
});

test('the sign-in ring pauses when motion stops or the background is hidden and resumes at the workspace angle', async () => {
    const { lens, ring, rotation } = await mountAperture({ view: 'Sources' });
    await lens.setProps({ continuous: true });
    expect(ring.classes()).toContain('continuous-ring');
    expect(ring.element.style.animationPlayState).toBe('running');
    await lens.setProps({ motionEnabled: false });
    expect(ring.element.style.animationPlayState).toBe('paused');
    await lens.setProps({ motionEnabled: true, hidden: true });
    expect(ring.element.style.animationPlayState).toBe('paused');
    await lens.setProps({ hidden: false });
    expect(ring.element.style.animationPlayState).toBe('running');
    expect(rotation()).toBe(0);
});
