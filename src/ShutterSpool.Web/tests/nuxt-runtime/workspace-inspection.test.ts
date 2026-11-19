import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import CaptureEvidence from '../../app/components/CaptureEvidence.vue';
import { mountWorkspace, photo, transport } from './workspace-mount';

test('Curation grid selection does not leak into Gallery browsing or its explicit selection mode', async () => {
    const w = await mountWorkspace({ curation: true });
    await w.bulk('a');
    await w.nav('Gallery');
    expect(w.view.find('.tile-selection').exists()).toBe(false);
    expect(w.view.find('.bulk-bar').exists()).toBe(false);
    await w.inspect('a');
    expect(w.full()).toBeNull();
    expect(w.view.get('.gallery-inspection').text()).toContain('Photo a');
    await w.click('Return to Gallery grid');
    await w.click('Select Photo a');
    expect(w.view.get('.bulk-bar').text()).toContain('1 selected');
    expect(w.view.find('.tile-selection').exists()).toBe(true);
    await w.click('Select none');
    expect(w.view.find('.bulk-bar').exists()).toBe(false);
    expect(w.view.find('.tile-selection').exists()).toBe(false);
    await w.inspect('a');
    expect(w.full()).toBeNull();
});

test('Gallery inspection retains selection, returns scroll and focus, and rejects old-owner reads', async () => {
    const w = await mountWorkspace();
    await w.click('Select Photo a');
    window.scrollTo(0, 640);
    const origin = w.view.get<HTMLButtonElement>('[data-id="a"] .tile-delivery-action');
    origin.element.focus();
    const old = Promise.withResolvers<unknown>(), current = Promise.withResolvers<unknown>();
    transport.detail.mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise);
    await origin.trigger('click');
    await w.click('Next photograph');
    old.resolve(photo('a', { displayName: 'stale owner' }));
    await flushPromises();
    expect(w.view.get('.gallery-inspection').text()).not.toContain('stale owner');
    current.resolve(photo('b'));
    await flushPromises();
    expect(w.view.get('.gallery-inspection').text()).toContain('Photo b');
    await w.click('Return to Gallery grid');
    expect(w.view.get('.bulk-bar').text()).toContain('1 selected');
    expect(document.activeElement).toBe(origin.element);
    expect(window.scrollY).toBe(640);
});

test('Gallery inspection failure stays recoverable and never permits Capture workflow commands', async () => {
    vi.stubGlobal('innerWidth', 500);
    transport.page.mockResolvedValue({ items: [photo('a'), photo('b', { deliverySummary: { attention: 1 } })] });
    const w = await mountWorkspace();
    transport.detail.mockRejectedValueOnce(new Error('unavailable'));
    await w.inspect('b');
    expect(w.view.get('.gallery-inspection').text()).toContain('Unable to open');
    await w.click('Retry details');
    expect(w.view.get('.gallery-inspection').text()).toContain('Photo b');
    expect(w.view.findAll('button').some(b => b.text().trim() === 'Release now')).toBe(false);
    expect(transport.batch).not.toHaveBeenCalled();
    await w.click('Return to Gallery grid');
    await w.click('Delivery needs attention');
    expect(w.view.get<HTMLDetailsElement>('.inspector').element.open).toBe(true);
    expect(w.view.getComponent(CaptureEvidence).props('focusState')).toBe('Attention');
});

test('leaving Gallery inspection retains the grid scroll position across workspace switches', async () => {
    const w = await mountWorkspace();
    window.scrollTo(0, 640);
    await w.inspect('a');
    expect(window.scrollY).toBe(0);
    await w.nav('Sources');
    await w.nav('Gallery');
    expect(window.scrollY).toBe(640);
});

test('Gallery delivery inspection reads evidence only while its details disclosure is actually open', async () => {
    const w = await mountWorkspace();
    await w.view.get('.tile-delivery-action').trigger('click');
    await flushPromises();
    const evidence = w.view.getComponent(CaptureEvidence);
    expect(evidence.props('active')).toBe(true);
    expect(evidence.props('deliveryOnly')).toBe(true);
    const disclosure = w.view.get<HTMLDetailsElement>('.capture-evidence');
    disclosure.element.open = false;
    await disclosure.trigger('toggle');
    expect(evidence.props('active')).toBe(false);
});

test('detail failures render locally in Gallery inspection and globally when no local inspector exists', async () => {
    const w = await mountWorkspace();
    transport.detail.mockRejectedValueOnce(new Error('unavailable'));
    await w.inspect('b');
    expect(w.view.get('.inspector').text()).toContain('Unable to open this Capture');
    expect(w.view.find('main > [role="alert"]').exists()).toBe(false);
    await w.click('Return to Gallery grid');
    expect(w.view.get('main > [role="alert"]').text()).toContain('Unable to open this Capture');
    await w.nav('Curation');
    transport.detail.mockRejectedValueOnce(new Error('unavailable'));
    await w.click('Previous photograph');
    await w.click('Bulk grid');
    expect(w.view.get('main > [role="alert"]').text()).toContain('Unable to open this Capture');
});

test('Gallery hover shows one camera identity for paired Assets', async () => {
    const assets = ['JPEG', 'RAW'].map(variant => ({ variant, cameraMake: 'Synthetic', cameraModel: 'Model' }));
    transport.page.mockResolvedValue({ items: [photo('pair', { assets })] });
    const w = await mountWorkspace();
    expect(w.view.get('.tile-detail').text().match(/Synthetic Model/g)).toHaveLength(1);
});

test('Gallery delivery state opens writable delivery actions while question-mark inspection remains read-only', async () => {
    transport.page.mockResolvedValue({ items: [photo('a', { deliverySummary: { attention: 1 } }), photo('b', { deliverySummary: { retryScheduled: 1 } })] });
    const w = await mountWorkspace();
    await w.click('Delivery needs attention');
    expect(w.view.getComponent(CaptureEvidence).props()).toMatchObject({ deliveryOnly: true, readOnly: false, focusState: 'Attention' });
    await w.click('Return to Gallery grid');
    await w.inspect('a');
    expect(w.view.getComponent(CaptureEvidence).props()).toMatchObject({ deliveryOnly: false, readOnly: true });
    await w.click('Return to Gallery grid');
    await w.click('Delivery scheduled for retry');
    expect(w.view.getComponent(CaptureEvidence).props('focusState')).toBe('RetryWait');
});
