import { button, click, management, mountEditor } from './editor-fixture';
import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { clearPrivateAccess } from '../../app/lib/session';

import PrivacyErasurePanel from '../../app/components/PrivacyErasurePanel.vue';

const scopeLabel = 'Erase photograph and private data';
const original = (assetId, state = 'available') => ({ assetId, state, variant: assetId === 'raw' ? 'RAW' : 'JPEG', byteLength: assetId === 'raw' ? 2048 : 1024 });
const evidence = (captureRevision = 7, availableOriginals = [original('jpeg'), original('raw', 'removed')]) => ({ captureRevision, availableOriginals, asOf: new Date() });
const previewResult = { status: 'Pending', assetCount: 2, captureCount: 1, preservationHoldCount: 1 };
function scopePanel(erasures, overrides = {}) {
    management.retention = { erasures };
    return mountEditor(PrivacyErasurePanel, { online: true, generation: 0, captureId: 'shot', displayName: 'Synthetic photo', evidence: evidence(), ...overrides });
}
async function consent(wrapper) {
    await wrapper.get('dialog input[type=checkbox]').setValue(true);
    await wrapper.get('dialog form').trigger('submit');
    await flushPromises();
}

test('privacy erasure freezes exact identities, checks an uncertain request without replay, and retains blocked surfaces', async () => {
    const operations = new Map();
    const preview = vi.fn().mockResolvedValue(previewResult);
    const post = vi.fn(async ({ operationId }) => {
        operations.set(operationId, { status: 'Pending', errorCode: 'PRESERVATION_HOLD_ACTIVE', assets: [{ assetId: 'jpeg', metadataRedaction: 'Completed', derivatives: 'Pending', originalsAndTransients: 'Blocked' }] });
        throw { status: 503 };
    });
    const resume = vi.fn(), byOperationId = vi.fn(id => ({ get: async () => operations.get(id), continueEscaped: { post: resume } }));
    const wrapper = await scopePanel({ preview: { post: preview }, post, byOperationId });
    await click(wrapper, scopeLabel);
    const selections = [{ captureId: 'shot', expectedRevision: 7, assetIds: ['jpeg', 'raw'] }];
    expect(preview.mock.calls[0][0]).toEqual({ selections });
    await consent(wrapper);
    const id = post.mock.calls[0][0].operationId;
    expect(id).toMatch(/^[\da-f-]{36}$/i);
    expect(post.mock.calls).toEqual([[{ operationId: id, confirmed: true, selections }]]);
    await click(wrapper, scopeLabel);
    await wrapper.get('dialog form').trigger('submit');
    expect(wrapper.text()).toContain('Check status before making another request');
    await click(wrapper, 'Check status');
    expect(byOperationId.mock.calls).toEqual([[id]]);
    expect(wrapper.get('.privacy-diagnostics').text()).toContain('Completed / Pending / Blocked');
    expect(wrapper.emitted('requested')).toEqual([['shot']]);
    expect(post).toHaveBeenCalledTimes(1);
    expect(resume).not.toHaveBeenCalled();
});

test('known removed identities offer metadata erasure without claiming available original bytes', async () => {
    const preview = vi.fn().mockResolvedValue(previewResult), post = vi.fn().mockResolvedValue({ status: 'Completed' });
    const confirm = vi.spyOn(window, 'confirm');
    const removed = { ...evidence(9, [original('jpeg', 'removed'), original('raw', 'removed')]), disposition: 'purged' };
    const wrapper = await scopePanel({ preview: { post: preview }, post }, { evidence: removed });
    await click(wrapper, 'Erase remaining private data');
    expect(wrapper.get('dialog').text()).toMatch(/1,024 historical bytes.*2,048 historical bytes/);
    expect(preview.mock.calls[0][0].selections).toEqual([{ captureId: 'shot', expectedRevision: 9, assetIds: ['jpeg', 'raw'] }]);
    await consent(wrapper);
    expect(post).toHaveBeenCalledTimes(1);
    expect(confirm).not.toHaveBeenCalled();
    await wrapper.setProps({ captureId: 'different', evidence: { ...removed, availableOriginals: [original('jpeg', 'missing')] } });
    await click(wrapper, scopeLabel);
    expect(wrapper.text()).toContain('removal evidence is incomplete');
    expect(preview).toHaveBeenCalledTimes(1);
});

test('unfinished erasure discovery pages explicitly and restarts from durable reads after auth loss', async () => {
    const get = vi.fn(async ({ queryParameters: { cursor } }) => cursor
        ? { operations: [{ operationId: 'older-held', status: 'Pending', errorCode: 'PRESERVATION_HOLD_ACTIVE' }] }
        : { operations: [{ operationId: 'recent', status: 'Pending' }], nextCursor: 'older' });
    const wrapper = await scopePanel({ get }, { captureId: undefined });
    expect(get).toHaveBeenCalledTimes(1);
    await click(wrapper, 'Older requests');
    expect(get.mock.calls[1][0].queryParameters).toEqual({ pendingOnly: true, pageSize: 50, cursor: 'older' });
    expect(wrapper.findAll('.privacy-request-item').map(row => row.text())).toEqual([expect.stringContaining('recent'), expect.stringContaining('PRESERVATION_HOLD_ACTIVE')]);
    clearPrivateAccess();
    await flushPromises();
    expect(wrapper.findAll('.privacy-request-item')).toHaveLength(0);
    wrapper.unmount();
    await scopePanel({ get }, { captureId: undefined });
    expect(get.mock.calls[2][0].queryParameters).toMatchObject({ pendingOnly: true, cursor: undefined });
});

test('native erasure confirmation requires deliberate consent and cancellation clears exact scope and returns focus', async () => {
    const post = vi.fn().mockResolvedValue({ status: 'Completed' });
    const wrapper = await scopePanel({ preview: { post: async () => previewResult }, post });
    const trigger = button(wrapper, scopeLabel);
    trigger.element.focus();
    await click(wrapper, scopeLabel);
    expect(wrapper.get('dialog').element.open).toBe(true);
    expect(document.activeElement).toBe(wrapper.get('dialog h2').element);
    await wrapper.get('dialog form').trigger('submit');
    expect(post).not.toHaveBeenCalled();
    await click(wrapper, 'Cancel');
    expect(wrapper.get('dialog').element.open).toBe(false);
    expect(document.activeElement).toBe(trigger.element);
    expect(wrapper.get('dialog').text()).not.toContain('shot · revision 7');
    await wrapper.get('dialog form').trigger('submit');
    await click(wrapper, scopeLabel);
    await wrapper.get('dialog input[type=checkbox]').setValue(true);
    await wrapper.setProps({ evidence: evidence(8) });
    expect(wrapper.get('dialog').element.open).toBe(false);
    expect(wrapper.get('dialog input[type=checkbox]').element.checked).toBe(false);
    await wrapper.get('dialog input[type=checkbox]').setValue(true);
    await wrapper.get('dialog form').trigger('submit');
    expect(post).not.toHaveBeenCalled();
    await click(wrapper, scopeLabel);
    expect(wrapper.get('dialog input[type=checkbox]').element.checked).toBe(false);
    await wrapper.get('dialog form').trigger('submit');
    expect(post).not.toHaveBeenCalled();
    await consent(wrapper);
    expect(post.mock.calls[0][0].selections[0].expectedRevision).toBe(8);
});

test('erasure continuation uses its saved operation identity and fences uncertain outcomes behind a status check', async () => {
    const id = 'saved-operation';
    const read = vi.fn().mockResolvedValue({ operationId: id, status: 'Blocked' });
    const post = vi.fn().mockRejectedValue({ status: 503 });
    const byOperationId = vi.fn(() => ({ get: read, continueEscaped: { post } }));
    const wrapper = await scopePanel({ get: async () => ({ operations: [{ operationId: id, status: 'Blocked' }] }), byOperationId }, { captureId: undefined });
    await click(wrapper, 'Check removal progress');
    await click(wrapper, 'Continue removal');
    await wrapper.get('dialog form').trigger('submit');
    expect(post).not.toHaveBeenCalled();
    await consent(wrapper);
    expect(post.mock.calls).toEqual([[{ confirmed: true }]]);
    expect(button(wrapper, 'Continue removal').element.disabled).toBe(true);
    await wrapper.get('dialog form').trigger('submit');
    expect(post).toHaveBeenCalledTimes(1);
    await click(wrapper, 'Check status');
    expect(read).toHaveBeenCalledTimes(2);
    expect(byOperationId.mock.calls.every(([operationId]) => operationId === id)).toBe(true);
    expect(post).toHaveBeenCalledTimes(1);
});

test('a revision change fences pending privacy reads without leaving the next review stuck busy', async () => {
    const old = Promise.withResolvers(), latest = Promise.withResolvers();
    const preview = vi.fn().mockReturnValueOnce(old.promise).mockReturnValueOnce(latest.promise);
    const wrapper = await scopePanel({ preview: { post: preview } });
    await click(wrapper, scopeLabel);
    await wrapper.setProps({ evidence: evidence(8) });
    expect(preview.mock.calls[0][1].options[0].signal.aborted).toBe(true);
    await click(wrapper, scopeLabel);
    old.resolve({ status: 'Pending', assetCount: 99 });
    await flushPromises();
    expect(wrapper.get('dialog').element.open).toBe(false);
    expect(button(wrapper, 'Checking removal scope…').element.disabled).toBe(true);
    latest.resolve(previewResult);
    await flushPromises();
    expect(wrapper.get('dialog').element.open).toBe(true);
    expect(wrapper.get('dialog').text()).toContain('shot · revision 8');
});

test('revision changes during irreversible submission retain operation identity and require status reconciliation', async () => {
    const submission = Promise.withResolvers(), post = vi.fn(() => submission.promise);
    const preview = vi.fn().mockResolvedValue(previewResult), byOperationId = vi.fn(() => ({ get: async () => ({ status: 'Completed' }) }));
    const wrapper = await scopePanel({ preview: { post: preview }, post, byOperationId });
    await click(wrapper, scopeLabel);
    await consent(wrapper);
    const id = post.mock.calls[0][0].operationId;
    await wrapper.setProps({ evidence: evidence(8) });
    expect(wrapper.get('.privacy-progress').text()).toContain(id);
    await click(wrapper, scopeLabel);
    expect(preview).toHaveBeenCalledTimes(1);
    submission.resolve({ status: 'Pending' });
    await flushPromises();
    expect(wrapper.get('.privacy-progress').text()).toContain('Last known removal progress');
    await click(wrapper, 'Check status');
    expect(byOperationId.mock.calls).toEqual([[id]]);
    expect(post).toHaveBeenCalledTimes(1);
});
