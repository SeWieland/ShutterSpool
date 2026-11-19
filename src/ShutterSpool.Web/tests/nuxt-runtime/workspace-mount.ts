import { mountSuspended } from '@nuxt/test-utils/runtime';
import { flushPromises, DOMWrapper } from '@vue/test-utils';
import { User } from 'oidc-client-ts';
import { ref } from 'vue';
import { afterEach, beforeEach, onTestFinished, vi } from 'vitest';
import OperationalWorkspace from '../../app/components/OperationalWorkspace.vue';
import type { RefreshScope, PreviewInvalidation } from '../../app/lib/reconcile';
import { clearPrivateAccess, session } from '../../app/lib/session';
import { workspaceAccess } from '../../app/lib/workspace-access';
import type { WorkspaceRole } from '../../app/lib/workspace-permissions';

const transport = vi.hoisted(() => ({
    access: vi.fn(), sources: vi.fn(), page: vi.fn(), waiting: vi.fn(), detail: vi.fn(), save: vi.fn(),
    trash: vi.fn(), favorite: vi.fn(), batch: vi.fn(), failure: vi.fn(), tags: vi.fn(),
}));
export { transport };
const sessionCreated = vi.hoisted(() => vi.fn());
export { sessionCreated };
vi.mock('../../app/lib/curation-session', async importOriginal => {
    const original = await importOriginal<typeof import('../../app/lib/curation-session')>();
    return { ...original, CurationSession: class extends original.CurationSession {
        constructor(...args: ConstructorParameters<typeof original.CurationSession>) {
            super(...args);
            sessionCreated();
        }
    } };
});
vi.mock('../../app/lib/api', async importOriginal => {
    const original = await importOriginal<typeof import('../../app/lib/api')>();
    const batch = (action: string) => ({ post: (body: unknown) => transport.batch(action, body) });
    return { ...original,
        handleFailure: (error: unknown) => { transport.failure(error); return original.handleFailure(error); },
        api: { management: {
            access: { get: transport.access }, sources: { get: transport.sources }, tags: { get: transport.tags },
            captures: {
                get: (options: { queryParameters: { released?: boolean } }) => options.queryParameters.released === false ? transport.waiting(options) : transport.page(options),
                byId: (id: string) => ({
                    get: (options: unknown) => transport.detail(id, options),
                    curation: { put: (body: unknown) => transport.save(id, body) },
                    favorite: { put: (body: unknown) => transport.favorite(id, body) },
                    trash: { post: (body: unknown) => transport.trash(id, body) },
                }),
                batch: { curation: batch('curation'), hold: batch('hold'), restore: batch('restore'), release: batch('release') },
            },
        } },
    };
});

type Refresh = (ids?: Set<string>, previews?: PreviewInvalidation, scopes?: Set<RefreshScope>) => Promise<void>;
let reconcile: Refresh;
export const live = { online: ref(true), connected: ref(true), activity: ref(), authActivity: ref(), phaseActivity: ref(), unassignedRejection: ref(false), recoverySequence: ref(0), connectionRecovery: ref(0) };
vi.mock('../../app/lib/reconcile', async importOriginal => ({
    ...await importOriginal<typeof import('../../app/lib/reconcile')>(),
    useReconciliation(refresh: Refresh) {
        reconcile = refresh;
        return { ...live, initialize: () => refresh(), refresh: () => refresh(),
            invalidate: (scopes: RefreshScope[] | RefreshScope, ids?: Set<string>, previews?: PreviewInvalidation) =>
                refresh(ids, previews, new Set(typeof scopes === 'string' ? [scopes] : scopes)),
        };
    },
}));

export function refresh(ids?: string[], scopes: RefreshScope[] = ['capture']) {
    return reconcile(ids && new Set(ids), undefined, new Set(scopes));
}
export function photo(id: string, overrides = {}) {
    return { id, displayName: 'Photo ' + id, acceptedAt: new Date('2026-10-10T10:00:00Z'),
        curation: { revision: 1, rating: 0, tags: [] as string[], held: false },
        retention: { captureRevision: 7, disposition: 'active', isFavorite: false, availableOriginals: [{ variant: 'RAW', byteLength: 10, state: 'available' }], timeline: { observedAt: new Date(), endsAt: new Date(Date.now() + 60_000) } },
        ...overrides };
}
export function roleAccess(role: WorkspaceRole) {
    return { membershipId: 'member', role, revision: 1, viewerPolicy: { revision: 1, includeUnreleased: true },
        capabilities: { canCurate: role !== 'Viewer', canAdminister: role === 'Owner', canManageMembers: role === 'Owner', canManageOwners: role === 'Owner' } };
}
export let intersect: () => void;
beforeEach(() => {
    vi.clearAllMocks();
    for (const mock of Object.values(transport)) mock.mockReset();
    transport.access.mockResolvedValue(roleAccess('Owner'));
    transport.sources.mockResolvedValue({ items: [{ id: 'source', name: 'Source', curationMode: 'Immediate' }] });
    transport.waiting.mockResolvedValue({ items: [photo('a')] });
    transport.page.mockResolvedValue({ items: [photo('a'), photo('b')] });
    transport.detail.mockImplementation(async id => photo(id));
    transport.save.mockImplementation(async (_id, body) => ({ ...body, revision: body.expectedRevision + 1 }));
    transport.batch.mockResolvedValue({ items: [] });
    transport.tags.mockResolvedValue({ tags: [] });
    live.online.value = true;
    live.connected.value = true;
    live.activity.value = undefined;
    live.recoverySequence.value = 0;
    live.connectionRecovery.value = 0;
    workspaceAccess.value = undefined;
    session.value = new User({ profile: { iss: 'https://synthetic.example.test', sub: 'operator', aud: 'test', exp: 2e9, iat: 0 }, access_token: '', token_type: 'Bearer' });
    localStorage.clear();
    vi.stubGlobal('IntersectionObserver', class {
        constructor(callback: (entries: { isIntersecting: boolean }[]) => void) { intersect = () => callback([{ isIntersecting: true }]); }
        observe() {} unobserve() {} disconnect() {}
    });
    vi.stubGlobal('confirm', vi.fn(() => true));
});
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

export function loseAccess() { session.value = null; clearPrivateAccess(); }
export function buttonIn(root: ParentNode, label: string) {
    const buttons = [...root.querySelectorAll<HTMLButtonElement>('button')];
    const button = buttons.find(b => b.getAttribute('aria-label') === label || b.textContent?.trim() === label);
    if (!button) throw new Error('Missing button: ' + label);
    return new DOMWrapper(button);
}
export async function mountWorkspace(options: { role?: WorkspaceRole; curation?: boolean; captureId?: string } = {}) {
    if (options.role) transport.access.mockResolvedValue(roleAccess(options.role));
    if (options.curation) transport.sources.mockResolvedValue({ items: [{ id: 'source', curationMode: 'Manual' }] });
    const shell = document.body.appendChild(document.createElement('div'));
    shell.classList.add('app-shell');
    shell.innerHTML = '<header class="app-header"><div id="workspace-header-actions"></div><div id="brand-live-status"></div></header><div class="test-mount"></div>';
    const view = await mountSuspended(OperationalWorkspace, {
        route: options.captureId ? { name: 'captures-id', params: { id: options.captureId } } : { name: 'index' },
        attachTo: shell.querySelector('.test-mount')!, props: { captureId: options.captureId },
        global: { stubs: {
            UIcon: true, AuthorizedPreview: true, CollectionPicker: true, CollectionMove: true, FavoriteControl: true,
            CaptureEvidence: true, NoDeliveryIntentAction: true, PrivacyErasurePanel: true, CaptureResumeAction: true,
            SourceWorkspace: true, DeliveryWorkspace: true, RetentionSettings: true, TrashWorkspace: true,
            WorkspaceLeave: true, WorkspaceMembers: true,
        } },
    });
    let mounted = true;
    function dispose() { if (mounted) { mounted = false; view.unmount(); shell.remove(); } }
    onTestFinished(dispose);
    await flushPromises();
    const btn = (label: string) => buttonIn(view.element.parentNode ?? shell, label);
    const click = async (label: string) => { await btn(label).trigger('click'); await flushPromises(); };
    const nav = async (label: string) => { await buttonIn(shell.querySelector('.main-nav')!, label).trigger('click'); await flushPromises(); };
    const tiles = () => view.findAll('.gallery-tile').map(tile => tile.attributes('data-id'));
    const select = async (id: string) => { const box = view.get<HTMLInputElement>(`input[aria-label="Select Photo ${id}"]`); await box.setValue(true); };
    const bulk = async (...ids: string[]) => { await click('Bulk grid'); for (const id of ids) await select(id); };
    const inspect = async (id: string) => { await view.get(`[data-id="${id}"] .tile-open`).trigger('click'); await flushPromises(); };
    const full = () => document.querySelector<HTMLElement>('.fullscreen-viewer');
    return { shell, view, btn, click, nav, tiles, select, bulk, inspect, full, dispose };
}
