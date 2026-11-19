import { mountSuspended } from '@nuxt/test-utils/runtime';
import { enableAutoUnmount, flushPromises } from '@vue/test-utils';
import { afterEach, beforeEach, expect, vi } from 'vitest';
import { workspaceAccess } from '../../app/lib/workspace-access';

const mocks = vi.hoisted(() => ({ management: {} }));
export const management = mocks.management;
vi.mock('../../app/lib/api', async original => ({
    ...await original(),
    api: { management: mocks.management },
    readSourceActivity: vi.fn(async () => ({ epoch: 'test', items: [], coverageComplete: true })),
    readSourceAuthenticationObservations: vi.fn(async () => ({ items: [], coverageComplete: true })),
}));
enableAutoUnmount(afterEach);
beforeEach(() => {
    // The simulated DOM has no native confirmation prompt.
    vi.stubGlobal('confirm', vi.fn(() => false));
    if (!window.confirm) Object.defineProperty(window, 'confirm', { configurable: true, value: globalThis.confirm });
    workspaceAccess.value = { capabilities: { canCurate: true, canAdminister: true } };
});
afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
    if (window !== globalThis && window.confirm === globalThis.confirm) Reflect.deleteProperty(window, 'confirm');
    vi.unstubAllGlobals();
    workspaceAccess.value = undefined;
    for (const key of Object.keys(management)) delete management[key];
});

export function button(wrapper, label) {
    const match = wrapper.findAll('button').find(node => typeof label === 'string' ? node.text() === label : label.test(node.text()));
    expect(match, String(label)).toBeDefined();
    return match;
}
export async function click(wrapper, label) {
    await button(wrapper, label).trigger('click');
    await flushPromises();
}
export async function attemptDisabledClick(control) {
    expect(control.element.disabled).toBe(true);
    // Exercise the command guard even though the browser suppresses native clicks.
    control.element.dispatchEvent(new Event('click', { bubbles: true }));
    await flushPromises();
}
export async function mountEditor(component, props = {}, options = {}) {
    const wrapper = await mountSuspended(component, { props, route: '', attachTo: document.body, global: { stubs: { UIcon: true, ...options.stubs } } });
    await flushPromises();
    return wrapper;
}
