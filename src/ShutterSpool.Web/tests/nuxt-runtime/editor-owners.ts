import { button, click, management, mountEditor } from './editor-fixture';
import { flushPromises } from '@vue/test-utils';
import { vi } from 'vitest';
import SourceWorkspace from '../../app/components/SourceWorkspace.vue';
import DeliveryWorkspace from '../../app/components/DeliveryWorkspace.vue';

export const source = (id, revision = 1) => ({ id, name: id, configurationRevision: revision, connectionProfile: 'ModernFtps' });
export const destination = (id, kind = 'local', revision = 1) => ({ id, name: id, kind, configurationRevision: revision, localRootPath: '/saved' });
export const route = (id, revision = 1, rules = []) => ({ destinationId: id, configurationRevision: revision, enabled: false, rules });
export const rule = (name = 'Saved rule') => ({ id: 'rule', name, order: 0, required: true, conditionMode: 0, negate: false, filenameTemplate: '{suppliedName}', folderTemplate: '', conditions: [] });
export const emptyPage = async () => ({ items: [] });
export const readProfiles = async () => ({ profiles: ['ModernFtps', 'LegacyFtps'].map(profile => ({ profile, enabled: true })), passwordPolicy: { minimumLength: 8, maximumLength: 64, generatedLength: 16 } });

export function sources(rows, { endpoint = () => ({}), captureRead = emptyPage, create = vi.fn(), profiles = readProfiles, ...props } = {}) {
    management.sourceCapabilities = { get: profiles };
    management.captures = { get: captureRead };
    management.sources = { get: async () => ({ items: rows }), post: create, byId: id => ({
        get: async () => rows.find(row => row.id === id) ?? source(id),
        display: { get: async () => rows.find(row => row.id === id) },
        ...endpoint(id),
    }) };
    return mountEditor(SourceWorkspace, { sources: rows, connected: true, recoverySequence: 0, ...props }, { stubs: { AuthorizedPreview: true } });
}
export function destinations(rows, { endpoint = () => ({}), list = async () => ({ items: rows }), create = vi.fn(), ...props } = {}) {
    management.destinations = {
        capabilities: { get: async () => ({ enabledKinds: ['local', 'webdav'] }) },
        get: list, post: create, byId: id => ({
            get: async () => rows.find(row => row.id === id) ?? destination(id),
            routing: { get: async () => route(id) }, work: { get: emptyPage }, ...endpoint(id),
        }),
    };
    return mountEditor(DeliveryWorkspace, { online: true, generation: 0, connected: true, motionEnabled: false, ...props }, { stubs: { AuthorizedPreview: true } });
}
export async function openDestination(wrapper, id) {
    const row = wrapper.findAll('.destination-open').find(node => node.text().includes(id));
    await row.trigger('click');
    await flushPromises();
    if (wrapper.find('dialog[open] button.danger').exists()) await click(wrapper, 'Discard changes');
}
export async function openSource(wrapper, id) {
    const row = wrapper.findAll('.source-summary').find(node => node.attributes('aria-label')?.startsWith(id + ','));
    await row.trigger('click');
    await flushPromises();
    if (wrapper.find('dialog[open] button.danger').exists()) await click(wrapper, 'Discard changes');
}
export function field(wrapper, name) {
    return wrapper.findAll('label').find(node => node.text().trim().startsWith(name)).get('input, select');
}
export async function submitWith(wrapper, label) {
    const form = button(wrapper, label).element.closest('form');
    form.dispatchEvent(new Event('submit', { cancelable: true, bubbles: true }));
    await flushPromises();
}
