import { attemptDisabledClick, button, click, management, mountEditor } from './editor-fixture';
import { flushPromises } from '@vue/test-utils';
import { expect, test, vi } from 'vitest';
import { clearPrivateAccess } from '../../app/lib/session';
import RetentionSettings from '../../app/components/RetentionSettings.vue';
import StorageCapacity from '../../app/components/StorageCapacity.vue';

const GiB = 1024 ** 3;
function storageDocument(active = true) {
    const ages = Object.fromEntries(['confirmed', 'noDelivery', 'waiting', 'heldOrUnfinished'].map(key => [key, { pressureAge: '01:00:00', ordinaryAge: '7.00:00:00' }]));
    return { settings: { revision: 5, automaticRemovalEnabled: false, trashRecoveryWindow: '3.00:00:00', restoreProtection: '3.00:00:00', galleryMaximum: '30.00:00:00', ...ages },
        quota: { revision: 6, quotaBytes: 10 * GiB, highWatermarkBytes: 9 * GiB, recoveryTargetBytes: 8 * GiB, isActive: active } };
}
async function storage({ get = vi.fn(() => storageDocument()), capacity = vi.fn(() => ({ observedAt: new Date(), unknownAccounting: false })),
    history = vi.fn(() => ({ items: [] })), preview = vi.fn(() => ({ complete: true })), put = vi.fn(), ...props } = {}) {
    management.retention = { settings: { get, put, preview: { post: preview } }, capacity: { get: capacity }, history: { get: history } };
    return mountEditor(RetentionSettings, { online: true, generation: 0, ...props }, { stubs: { PrivacyErasurePanel: true } });
}
function input(wrapper, label) {
    const field = wrapper.findAll('label').find(node => node.text().includes(label));
    expect(field, label).toBeDefined();
    return field.get('input');
}
const quota = wrapper => input(wrapper, 'Storage limit (GiB)');
async function review(wrapper) { await wrapper.get('form.storage-editor').trigger('submit'); await flushPromises(); }

test('settings require fresh impact acknowledgement and retain drafts during capacity reconciliation', async () => {
    const pending = Promise.withResolvers(), dirty = vi.fn();
    let complete = true;
    const preview = vi.fn(async () => ({ complete, impactAcknowledgementRequired: true, quotaActivationAcknowledgementRequired: false, plannedBytes: complete ? 2048 : null }));
    const put = vi.fn().mockRejectedValueOnce({ status: 503 }).mockReturnValueOnce(pending.promise);
    const wrapper = await storage({ preview, put, onDirty: dirty });
    await quota(wrapper).setValue(12);
    await click(wrapper, 'Refresh capacity');
    expect(quota(wrapper).element.value).toBe('12');
    await review(wrapper);
    expect(preview.mock.calls[0][0]).toMatchObject({ expectedSettingsRevision: 5, expectedQuotaRevision: 6, settings: { trashRecoveryWindow: '3.00:00:00' } });
    await click(wrapper, 'Save settings');
    expect(put).not.toHaveBeenCalled();
    await click(wrapper, 'Back to editing');
    complete = false;
    await input(wrapper, 'Allow automatic cleanup').setValue(true);
    await review(wrapper);
    await click(wrapper, 'Save settings');
    expect(put).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('estimate is incomplete');
    await click(wrapper, 'Back to editing');
    complete = true;
    await input(wrapper, 'Allow automatic cleanup').setValue(false);
    await review(wrapper);
    await input(wrapper, 'I understand').setValue(true);
    await click(wrapper, 'Save settings');
    expect(wrapper.text()).toContain('could not confirm');
    await review(wrapper);
    expect(put).toHaveBeenCalledTimes(1);
    await click(wrapper, 'Reload saved settings');
    await quota(wrapper).setValue(12);
    await review(wrapper);
    await input(wrapper, 'I understand').setValue(true);
    await click(wrapper, 'Save settings');
    expect(put).toHaveBeenCalledTimes(2);
    const before = dirty.mock.calls.length;
    wrapper.unmount();
    pending.resolve({ succeeded: true });
    await flushPromises();
    expect(dirty).toHaveBeenCalledTimes(before);
});

test('unknown storage allows acknowledged quota setup with cleanup off, then keeps cleanup blocked', async () => {
    let saved = storageDocument(false), unknown = true;
    const preview = vi.fn(body => ({ complete: false, plannedBytes: null, impactAcknowledgementRequired: true, quotaActivationAcknowledgementRequired: body.quota.isActive && !saved.quota.isActive }));
    const put = vi.fn(body => { saved = { settings: { ...body.settings, revision: 7 }, quota: { ...body.quota, revision: 8 } }; return { succeeded: true }; });
    const wrapper = await storage({ get: () => saved, capacity: () => ({ observedAt: new Date(), unknownAccounting: unknown }), preview, put });
    await quota(wrapper).setValue(3);
    await input(wrapper, 'Start cleanup above').setValue(2);
    await input(wrapper, 'Cleanup target (GiB)').setValue(1);
    await input(wrapper, 'Use a storage limit').setValue(true);
    await review(wrapper);
    await click(wrapper, 'Save settings');
    expect(put).not.toHaveBeenCalled();
    await input(wrapper, 'I understand').setValue(true);
    await click(wrapper, 'Save settings');
    expect(put.mock.calls[0][0]).toMatchObject({ quota: { quotaBytes: 3 * GiB, highWatermarkBytes: 2 * GiB, recoveryTargetBytes: GiB }, settings: { automaticRemovalEnabled: false } });
    expect(input(wrapper, 'Allow automatic cleanup').element.disabled).toBe(true);
    unknown = false;
    await click(wrapper, 'Refresh capacity');
    await input(wrapper, 'Allow automatic cleanup').setValue(true);
    await review(wrapper);
    expect(preview.mock.lastCall[0].settings.automaticRemovalEnabled).toBe(true);
    await click(wrapper, 'Save settings');
    expect(put).toHaveBeenCalledTimes(1);
});

test('active automatic cleanup needs a complete estimate even when no acknowledgement is required', async () => {
    const saved = storageDocument();
    saved.settings.automaticRemovalEnabled = true;
    let complete = false;
    const preview = vi.fn(() => ({ complete, impactAcknowledgementRequired: false, quotaActivationAcknowledgementRequired: false }));
    const put = vi.fn().mockResolvedValue({ succeeded: true });
    const wrapper = await storage({ get: () => saved, preview, put });
    await quota(wrapper).setValue(12);
    await review(wrapper);
    const reviewed = preview.mock.calls[0][0];
    expect(reviewed).toMatchObject({ settings: { automaticRemovalEnabled: true }, quota: { isActive: true, quotaBytes: 12 * GiB } });
    await attemptDisabledClick(button(wrapper, 'Save settings'));
    expect(put).not.toHaveBeenCalled();

    await click(wrapper, 'Back to editing');
    complete = true;
    await review(wrapper);
    expect(preview.mock.lastCall[0]).toEqual(reviewed);
    expect(button(wrapper, 'Save settings').element.disabled).toBe(false);
    await click(wrapper, 'Save settings');
    expect(put.mock.calls).toEqual([[{ ...reviewed, acknowledgeImpact: false }]]);
});

test('capacity refresh survives a failed settings read, keeps drafts, and keeps saving blocked', async () => {
    const get = vi.fn().mockResolvedValueOnce(storageDocument()).mockRejectedValue({ status: 503 });
    let usedBytes = 100;
    const preview = vi.fn();
    const wrapper = await storage({ get, preview, capacity: () => ({ observedAt: new Date(), usedBytes, unknownAccounting: true }) });
    await quota(wrapper).setValue(12);
    usedBytes = 200;
    await click(wrapper, 'Refresh capacity');
    expect(wrapper.getComponent(StorageCapacity).props('snapshot').usedBytes).toBe(200);
    expect(wrapper.getComponent(StorageCapacity).props('stale')).toBe(true);
    expect(quota(wrapper).element.value).toBe('12');
    expect(wrapper.text()).toContain('saved settings could not be checked');
    await review(wrapper);
    expect(preview).not.toHaveBeenCalled();
});

test('a successful retry clears stale refresh feedback and shows the refreshed timestamp', async () => {
    const retry = Promise.withResolvers();
    const capacity = vi.fn().mockResolvedValueOnce({ observedAt: new Date(), usedBytes: 50 }).mockRejectedValueOnce({ status: 503 }).mockReturnValueOnce(retry.promise);
    const wrapper = await storage({ capacity });
    await click(wrapper, 'Refresh capacity');
    expect(wrapper.text()).toContain('could not check storage');
    await click(wrapper, 'Refresh capacity');
    expect(button(wrapper, 'Refreshing…').attributes('aria-busy')).toBe('true');
    const observedAt = new Date();
    retry.resolve({ observedAt, usedBytes: 100 });
    await flushPromises();
    expect(wrapper.text()).not.toContain('could not check storage');
    expect(wrapper.getComponent(StorageCapacity).props()).toMatchObject({ snapshot: { observedAt, usedBytes: 100 }, stale: false });
    expect(button(wrapper, 'Refresh capacity').attributes('aria-busy')).toBe('false');
});

test('late refresh results after access loss cannot restore capacity or settings', async () => {
    const settings = Promise.withResolvers(), capacity = Promise.withResolvers();
    const wrapper = await storage({ get: () => settings.promise, capacity: () => capacity.promise });
    clearPrivateAccess();
    settings.resolve(storageDocument());
    capacity.resolve({ observedAt: new Date(), usedBytes: 50 });
    await flushPromises();
    expect(wrapper.find('form.storage-editor').exists()).toBe(false);
    expect(wrapper.getComponent(StorageCapacity).props('snapshot')).toBeUndefined();
});

test('access loss during settings hydration leaves refresh feedback cleared and skips history', async () => {
    const history = vi.fn();
    const onDirty = vi.fn();
    onDirty.mockImplementationOnce(() => clearPrivateAccess());
    const wrapper = await storage({ history, capacity: () => Promise.reject({ status: 503 }), onDirty });
    expect(onDirty).toHaveBeenCalled();
    expect(history).not.toHaveBeenCalled();
    expect(wrapper.find('form.storage-editor').exists()).toBe(false);
    expect(wrapper.find('.storage-settings > [role=status]').exists()).toBe(false);
});

test('settings access failure discards capacity from the same refresh and skips history', async () => {
    const history = vi.fn(() => ({ items: [] }));
    const get = vi.fn().mockResolvedValueOnce(storageDocument()).mockRejectedValueOnce({ status: 401 });
    const wrapper = await storage({ history, get, capacity: () => ({ observedAt: new Date(), usedBytes: 1024 }) });
    expect(wrapper.getComponent(StorageCapacity).props('snapshot').usedBytes).toBe(1024);
    await click(wrapper, 'Refresh capacity');
    expect(history).toHaveBeenCalledTimes(1);
    expect(wrapper.find('form.storage-editor').exists()).toBe(false);
    expect(wrapper.getComponent(StorageCapacity).props('snapshot')).toBeUndefined();
    expect(wrapper.find('.storage-settings > [role=status]').exists()).toBe(false);
});

test('Settings validation identifies the invalid field group without blaming valid quota values', async () => {
    const wrapper = await storage();
    await input(wrapper, 'Trash recovery (hours)').setValue(0);
    expect(wrapper.get('#retention-trash-error').text()).toMatch(/greater than zero/);
    expect(wrapper.find('#retention-quota-error').exists()).toBe(false);
    await input(wrapper, 'Trash recovery (hours)').setValue(72);
    await input(wrapper, 'Allow automatic cleanup').setValue(true);
    await input(wrapper, 'Use a storage limit').setValue(false);
    expect(wrapper.get('#retention-automatic-error').text()).toMatch(/storage limit.*automatic cleanup/);
    await input(wrapper, 'Use a storage limit').setValue(true);
    const age = wrapper.findAll('.cleanup-age-group')[2].get('input');
    await age.setValue(180);
    expect(wrapper.get('#retention-waiting-error').text()).toMatch(/no later than regular cleanup/);
    await age.setValue(24);
    await input(wrapper, 'Memory budget').setValue(1025);
    expect(wrapper.get('#retention-memory-error').text()).toMatch(/1.*1024/);
});

test('a workspace storage limit stays active and cannot exceed its assigned allocation', async () => {
    const saved = { ...storageDocument(), workspaceAllocatedBytes: 11 * GiB };
    const preview = vi.fn().mockResolvedValue({ complete: true });
    const wrapper = await storage({ get: () => saved, preview });
    expect(input(wrapper, 'Use a storage limit').element.disabled).toBe(true);
    expect(input(wrapper, 'Use a storage limit').element.checked).toBe(true);
    await quota(wrapper).setValue(12);
    expect(wrapper.get('#retention-quota-error').text()).toMatch(/assigned allocation/);
    await review(wrapper);
    expect(preview).not.toHaveBeenCalled();
    await quota(wrapper).setValue(11);
    expect(wrapper.find('#retention-quota-error').exists()).toBe(false);
    await review(wrapper);
    expect(preview.mock.calls[0][0].quota).toMatchObject({ isActive: true, quotaBytes: 11 * GiB });
});

test('dedicated review ignores unchanged settings and fences old estimates after editing and newer requests', async () => {
    const older = Promise.withResolvers(), newer = Promise.withResolvers();
    const preview = vi.fn().mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise), put = vi.fn();
    const wrapper = await storage({ preview, put });
    await review(wrapper);
    expect(preview).not.toHaveBeenCalled();
    await quota(wrapper).setValue(11);
    await quota(wrapper).setValue(10);
    expect(wrapper.emitted('dirty')?.at(-1)).toEqual([false]);
    await quota(wrapper).setValue(12);
    await review(wrapper);
    expect(Object.isFrozen(preview.mock.calls[0][0])).toBe(true);
    await click(wrapper, 'Back to editing');
    expect(quota(wrapper).element.value).toBe('12');
    await quota(wrapper).setValue(13);
    await review(wrapper);
    newer.resolve({ complete: false, warnings: ['newer-estimate'] });
    await flushPromises();
    older.resolve({ complete: true, warnings: ['old-estimate'] });
    await flushPromises();
    expect(wrapper.text()).toContain('newer-estimate');
    expect(wrapper.text()).not.toContain('old-estimate');
    expect(preview.mock.calls[1][0].quota.quotaBytes).toBe(13 * GiB);
    await click(wrapper, 'Back to editing');
    await quota(wrapper).setValue(14);
    expect(wrapper.find('.storage-review').exists()).toBe(false);
    expect(put).not.toHaveBeenCalled();
});

test('late storage review after identity loss never restores the review or draft', async () => {
    const pending = Promise.withResolvers(), put = vi.fn();
    const wrapper = await storage({ preview: () => pending.promise, put });
    await quota(wrapper).setValue(12);
    await review(wrapper);
    clearPrivateAccess();
    pending.resolve({ complete: true });
    await flushPromises();
    expect(wrapper.find('.storage-review').exists()).toBe(false);
    expect(wrapper.find('form.storage-editor').exists()).toBe(false);
    expect(put).not.toHaveBeenCalled();
});

test('capacity refresh follows authoritative activation and deactivation without changing the editable baseline', async () => {
    for (const initial of [false, true]) {
        let active = initial, fail = '';
        const get = () => {
            if (fail === 'settings') throw { status: 503 };
            const saved = storageDocument(active);
            saved.quota.revision = active === initial ? 6 : 7;
            return saved;
        };
        const capacity = async () => { if (fail === 'capacity') throw { status: 503 }; return { observedAt: new Date(), unknownAccounting: false, blockers: active ? [] : ['quota-not-active'] }; };
        const wrapper = await storage({ get: async () => get(), capacity });
        await quota(wrapper).setValue(12);
        active = !initial;
        await click(wrapper, 'Refresh capacity');
        expect(wrapper.getComponent(StorageCapacity).props('quotaActive')).toBe(active);
        expect(input(wrapper, 'Use a storage limit').element.checked).toBe(initial);
        expect(quota(wrapper).element.value).toBe('12');
        expect(button(wrapper, /^Review changes/).element.disabled).toBe(true);
        fail = 'settings';
        await click(wrapper, 'Refresh capacity');
        expect(wrapper.getComponent(StorageCapacity).props()).toMatchObject({ quotaActive: active, stale: true });
        fail = 'capacity';
        active = initial;
        await click(wrapper, 'Refresh capacity');
        expect(wrapper.getComponent(StorageCapacity).props()).toMatchObject({ quotaActive: !initial, stale: true });
        expect(quota(wrapper).element.value).toBe('12');
        wrapper.unmount();
    }
});
