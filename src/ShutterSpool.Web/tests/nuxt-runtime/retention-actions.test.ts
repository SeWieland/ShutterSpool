import { attemptDisabledClick, button, click, management, mountEditor } from './editor-fixture';
import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { clearPrivateAccess } from '../../app/lib/session';
import { workspaceAccess } from '../../app/lib/workspace-access';
import FavoriteControl from '../../app/components/FavoriteControl.vue';
import CaptureTrashAction from '../../app/components/CaptureTrashAction.vue';
import CaptureResumeAction from '../../app/components/CaptureResumeAction.vue';
import CaptureEvidence from '../../app/components/CaptureEvidence.vue';
import DeliveryWork from '../../app/components/DeliveryWork.vue';
import NoDeliveryIntentAction from '../../app/components/NoDeliveryIntentAction.vue';
import TrashWorkspace from '../../app/components/TrashWorkspace.vue';

const evidence = (isFavorite = false) => ({ captureRevision: 7, isFavorite, asOf: new Date(), availableOriginals: [{ variant: 'JPEG', byteLength: 5, state: 'available' }] });
function favorite(put, props = {}) {
    management.captures = { byId: () => ({ favorite: { put } }) };
    return mountEditor(FavoriteControl, { captureId: 'shot', evidence: evidence(), ...props });
}

test('icon-only Favorite stays a named native button and handles keyboard activation conflicts without opening its photo', async () => {
    const put = vi.fn().mockRejectedValue({ status: 409 });
    const wrapper = await favorite(put, { compact: true, evidence: evidence(true) });
    const control = wrapper.get('button[aria-label="Remove Favorite"]');
    expect(control.attributes('aria-pressed')).toBe('true');
    expect(control.attributes('aria-description')).toMatch(/rules apply immediately.*Trash recovery window/);
    expect(control.get('svg').attributes('fill')).toBe('currentColor');
    const opened = vi.fn();
    wrapper.element.addEventListener('click', opened);
    await control.trigger('click', { detail: 0 });
    await flushPromises();
    expect(control.element.disabled).toBe(true);
    expect(wrapper.text()).toContain('changed elsewhere');
    await control.trigger('click');
    expect(put.mock.calls).toEqual([[{ expectedRevision: 7, isFavorite: false }]]);
    expect(opened).not.toHaveBeenCalled();
});

test('lost heart response keeps confirmed intent and prevents replay until a fresh authoritative read', async () => {
    const saved = evidence();
    const put = vi.fn(async body => { saved.isFavorite = body.isFavorite; saved.captureRevision++; throw { status: 503 }; });
    const wrapper = await favorite(put);
    await wrapper.get('.favorite-button').trigger('click');
    await flushPromises();
    await attemptDisabledClick(wrapper.get('.favorite-button'));
    expect(wrapper.get('.favorite-button').attributes('aria-pressed')).toBe('false');
    expect(wrapper.text()).toContain('could not confirm');
    expect(wrapper.emitted('refresh')).toEqual([['shot']]);
    expect(put.mock.calls).toEqual([[{ isFavorite: true, expectedRevision: 7 }]]);
    await wrapper.setProps({ evidence: { ...saved, asOf: new Date(Date.now() + 1000) } });
    expect(wrapper.get('.favorite-button').attributes('aria-pressed')).toBe('true');
    expect(wrapper.get('.favorite-button').element.disabled).toBe(false);
    expect(put).toHaveBeenCalledTimes(1);
});

test('unfavorite is one click with the current revision and never asks for confirmation', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false), put = vi.fn();
    const wrapper = await favorite(put, { evidence: evidence(true) });
    await wrapper.get('.favorite-button').trigger('click');
    expect(put.mock.calls).toEqual([[{ expectedRevision: 7, isFavorite: false }]]);
    expect(confirm).not.toHaveBeenCalled();
});

test('a late heart completion cannot announce or reconcile a different photograph', async () => {
    const response = Promise.withResolvers();
    const wrapper = await favorite(() => response.promise);
    await wrapper.get('.favorite-button').trigger('click');
    await wrapper.setProps({ captureId: 'other' });
    response.resolve({});
    await flushPromises();
    expect(wrapper.emitted('refresh')).toBeUndefined();
    expect(wrapper.text()).not.toContain('Favorite saved.');
});

test('compact Favorite recovery pins are cleared after authoritative resolution and unmount', async () => {
    const feedback = vi.fn();
    const wrapper = await favorite(vi.fn().mockRejectedValue({ status: 409 }), { compact: true, onFeedback: feedback });
    await wrapper.get('.favorite-button').trigger('click');
    await flushPromises();
    expect(wrapper.emitted('feedback')).toEqual([[true]]);
    await wrapper.setProps({ evidence: { ...evidence(), asOf: new Date(Date.now() + 1000) } });
    expect(wrapper.emitted('feedback')).toEqual([[true], [false]]);
    wrapper.unmount();
    expect(feedback.mock.calls).toEqual([[true], [false], [false]]);
});

test('Trash child validates evidence and emits a confirmed submission for shell-owned mutation', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const wrapper = await mountEditor(CaptureTrashAction, { captureId: 'shot', name: 'Shot', evidence: evidence(true) });
    await click(wrapper, 'Move to Trash');
    expect(confirm.mock.calls[0][0]).toMatch(/JPEG \(5 bytes.*Favorite: yes.*only copies/s);
    expect(wrapper.emitted('submit')).toEqual([['shot', 7]]);
    await wrapper.setProps({ evidence: { ...evidence(), availableOriginals: [{ state: 'unknown', variant: 'JPEG' }] } });
    await click(wrapper, 'Move to Trash');
    expect(wrapper.emitted('refresh')).toEqual([['shot', false]]);
    expect(wrapper.text()).toContain('could not check the originals');
    expect(confirm).toHaveBeenCalledTimes(1);
    await wrapper.setProps({ evidence: evidence() });
    confirm.mockReturnValue(false);
    await click(wrapper, 'Move to Trash');
    expect(wrapper.emitted('submit')).toHaveLength(1);
});

test('Trash uncertainty and shared pending state block submit while verification is an explicit event', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const wrapper = await mountEditor(CaptureTrashAction, { captureId: 'shot', name: 'Shot', evidence: evidence(), uncertain: true });
    await attemptDisabledClick(button(wrapper, 'Move to Trash'));
    await click(wrapper, 'Refresh and verify');
    expect(wrapper.emitted('verify')).toEqual([['shot']]);
    await wrapper.setProps({ uncertain: false, pending: true });
    await attemptDisabledClick(button(wrapper, 'Move to Trash'));
    expect(wrapper.text()).not.toContain('Refresh and verify');
    await wrapper.setProps({ uncertain: true, pending: false, verifying: true });
    await attemptDisabledClick(button(wrapper, 'Verifying Trash outcome…'));
    await wrapper.setProps({ verifying: false, disabled: true });
    await attemptDisabledClick(button(wrapper, 'Refresh and verify'));
    await wrapper.setProps({ uncertain: false });
    await attemptDisabledClick(button(wrapper, 'Move to Trash'));
    expect(confirm).not.toHaveBeenCalled();
    expect(wrapper.emitted('verify')).toHaveLength(1);
    expect(wrapper.emitted('submit')).toBeUndefined();
});

test('resume recovery reads forward cancellation and cannot update an action after owner replacement', async () => {
    const stale = Promise.withResolvers();
    const current = Promise.withResolvers();
    const get = vi.fn().mockReturnValueOnce(stale.promise).mockReturnValueOnce(current.promise);
    const post = vi.fn().mockRejectedValue({ status: 503 });
    management.captures = { byId: () => ({ get, resume: { post } }) };
    const wrapper = await mountEditor(CaptureResumeAction, { captureId: 'shot', evidence: { ...evidence(), isPaused: true, disposition: 'active' } });
    await click(wrapper, 'Resume delivery');
    await click(wrapper, 'Check current details');
    const signal = get.mock.calls[0][0].options[0].signal;
    expect(signal).toBeInstanceOf(AbortSignal);
    await wrapper.setProps({ captureId: 'other' });
    expect(signal.aborted).toBe(true);
    await click(wrapper, 'Resume delivery');
    await click(wrapper, 'Check current details');
    stale.resolve({ retention: { captureRevision: 99 } });
    await flushPromises();
    expect(wrapper.emitted('refresh')).toEqual([['shot'], ['other']]);
    expect(wrapper.text()).not.toContain('Current details checked');
    expect(button(wrapper, 'Checking current details…').element.disabled).toBe(true);
    current.resolve({ retention: { captureRevision: 8 } });
    await flushPromises();
    expect(wrapper.emitted('refresh')).toEqual([['shot'], ['other'], ['other']]);
    expect(wrapper.text()).toContain('Current details checked');
    expect(button(wrapper, 'Check current details').element.disabled).toBe(false);
    expect(post.mock.calls).toEqual([[{ expectedRevision: 7 }], [{ expectedRevision: 7 }]]);
});

test('No delivery intended requires positive unmatched evidence and never infers permission from empty delivery', async () => {
    const post = vi.fn().mockRejectedValue({ status: 409 });
    management.captures = { byId: () => ({ noDeliveryIntent: { post } }) };
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    const capture = { id: 'shot', retention: { captureRevision: 7, disposition: 'active', canRecordNoDeliveryIntent: false }, deliverySummary: { noRoute: 0 } };
    const wrapper = await mountEditor(NoDeliveryIntentAction, { capture });
    expect(wrapper.find('button').exists()).toBe(false);
    await wrapper.setProps({ capture: { ...capture, deliverySummary: { noRoute: 1 } } });
    expect(wrapper.find('button').exists()).toBe(false);
    expect(post).not.toHaveBeenCalled();
    await wrapper.setProps({ capture: { ...capture, deliverySummary: { noRoute: 1 }, retention: { ...capture.retention, canRecordNoDeliveryIntent: true } } });
    await click(wrapper, 'Mark delivery not needed');
    await attemptDisabledClick(button(wrapper, 'Mark delivery not needed'));
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(post.mock.calls).toEqual([[{ expectedRevision: 7 }]]);
    expect(wrapper.text()).toContain('cannot cancel work');
});

function trashRow(captureId, captureRevision, raw = false) {
    return { captureId, captureRevision, displayName: captureId, recoverableUntil: new Date(Date.now() + 86400000),
        retention: { ...evidence(raw), disposition: 'trash', availableOriginals: [{ variant: raw ? 'RAW' : 'JPEG', byteLength: raw ? 2048 : 1024, state: 'available' }] } };
}
function trash(stubs = {}) {
    return mountEditor(TrashWorkspace, { online: true, generation: 0, gridMaximumDimension: 100 }, { stubs: { AuthorizedPreview: true, ...stubs } });
}

test('Empty loaded Trash confirms only its frozen loaded scope and reports per-item conflicts', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true);
    let rows = [trashRow('allowed', 2, true), trashRow('protected', 3), trashRow('later', 4)];
    const purge = vi.fn(async (id, revision) => { if (id === 'protected') throw { status: 409 }; });
    management.retention = { settings: { get: async () => ({ settings: { restoreProtection: '3.00:00:00' } }) } };
    management.trash = { get: async () => ({ items: rows, nextCursor: 'older' }), byId: id => ({ purge: { post: body => purge(id, body.expectedRevision) } }) };
    const wrapper = await trash();
    await click(wrapper, 'Permanently delete loaded photographs');
    expect(purge.mock.calls).toEqual([['allowed', 2], ['protected', 3]]);
    expect(confirm).toHaveBeenCalledTimes(1);
    for (const text of [/3 loaded/, /1 Favorites/, /unloaded.*excluded/, /RAW \(2,048 bytes/, /JPEG \(1,024 bytes/, /only copies/, /Backups and delivered or downloaded copies/, /protect files from deletion/]) expect(confirm.mock.calls[0][0]).toMatch(text);
    expect(wrapper.text()).toContain('protected: changed or protected');
    rows = [{ ...trashRow('unknown', 1), retention: { asOf: new Date(), isFavorite: false } }];
    await click(wrapper, 'Refresh Trash');
    await click(wrapper, 'Permanently delete loaded photographs');
    expect(wrapper.text()).toContain('could not be checked recently');
    expect(purge).toHaveBeenCalledTimes(2);
});

test('Curator Trash restores without reading policy and blocks every permanent-removal path', async () => {
    workspaceAccess.value = { capabilities: { canCurate: true, canAdminister: false } };
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const policy = vi.fn(), restore = vi.fn().mockResolvedValue({}), purge = vi.fn();
    management.retention = { settings: { get: policy } };
    management.trash = { get: async () => ({ items: [trashRow('shot', 4)] }), byId: () => ({ restore: { post: restore }, purge: { post: purge } }) };
    const wrapper = await trash();
    await click(wrapper, 'Restore photograph');
    expect(restore.mock.calls).toEqual([[{ expectedRevision: 4 }]]);
    expect(wrapper.text()).toContain('administrator to resume');
    expect(wrapper.findAll('button').some(item => /permanent|delete/i.test(item.text()))).toBe(false);
    expect(policy).not.toHaveBeenCalled();
    expect(purge).not.toHaveBeenCalled();
});

test('Capture evidence reads only while active, forwards cancellation, and aborts when hidden', async () => {
    const requests = [];
    const endpoint = Object.fromEntries(['routing', 'deliveries', 'actions'].map(kind => [kind, { get: config => {
        const gate = Promise.withResolvers();
        requests.push({ ...gate, kind, signal: config.options[0].signal });
        return gate.promise;
    } }]));
    management.captures = { byId: () => endpoint };
    const focus = vi.spyOn(HTMLElement.prototype, 'focus');
    const wrapper = await mountEditor(CaptureEvidence, { capture: { id: 'shot', assets: [{ id: 'asset' }], curation: {} }, active: false, generation: 0, focusState: 'Attention' });
    expect(requests).toHaveLength(0);
    await wrapper.setProps({ active: true });
    expect(requests.map(row => row.kind)).toEqual(['routing', 'deliveries', 'actions']);
    expect(requests.every(row => row.signal instanceof AbortSignal)).toBe(true);
    requests.forEach(row => row.resolve({ items: [], nextCursor: row.kind === 'deliveries' ? 'next' : undefined }));
    await flushPromises();
    expect(focus).not.toHaveBeenCalled();
    await click(wrapper, 'Earlier delivery work');
    requests.at(-1).resolve({ items: [{ id: 'row', assetId: 'asset', state: 'Failed' }] });
    await flushPromises();
    expect(wrapper.getComponent(DeliveryWork).props('recoverySequence')).toBe(2);
    expect(focus).not.toHaveBeenCalled();
    await wrapper.setProps({ generation: 1 });
    const next = requests.slice(4);
    expect(next).toHaveLength(3);
    await wrapper.setProps({ focusState: 'RetryWait', active: false });
    expect(next.every(row => row.signal.aborted)).toBe(true);
    next.forEach(row => row.resolve({ items: [] }));
    await flushPromises();
    expect(wrapper.getComponent(DeliveryWork).props('recoverySequence')).toBe(2);
    expect(focus).not.toHaveBeenCalled();
    wrapper.unmount();
    clearPrivateAccess();
    expect(requests).toHaveLength(7);
});
