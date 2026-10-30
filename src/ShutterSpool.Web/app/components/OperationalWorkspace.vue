<script setup lang="ts">
import { appearanceDefaults, previewDimension, workspaceDefaults } from '../lib/workspace-preferences';
import { retentionTiming } from '../lib/retention';
import { GallerySlideshow, slideshowState } from '../lib/gallery-slideshow';
import type { DisplayState } from '../lib/gallery-slideshow';
import { FullscreenChrome, chromeState } from '../lib/fullscreen-chrome';
import { api, handleFailure, cancellable } from '../lib/api';
import { session, onAccessLost, accessGeneration } from '../lib/session';
import { useReconciliation } from '../lib/reconcile';
import { useGalleryGrid } from '../composables/useGalleryGrid';
import { provideWorkspaceClock } from '../composables/useWorkspaceClock';
import { usePhotoViewer } from '../composables/usePhotoViewer';
import { useViewerSession } from '../composables/useViewerSession';
import { useCurationVisit } from '../composables/useCurationVisit';
import { useCaptureCurationActions } from '../composables/useCaptureCurationActions';
import { useWorkspacePreferences } from '../composables/useWorkspacePreferences';
import { useCaptureBrowser } from '../composables/useCaptureBrowser';
import { effectiveMotion, reconcileCaptures, curationTiming } from '../lib/workspace-interactions';
import { pageWorkspaceId, readWorkspaceId, withWorkspace } from '../lib/workspace-context';
import { mayCurate, mayAdminister, workspaceAccess, refreshWorkspaceAccess, closeWorkspaceAccess } from '../lib/workspace-access';
import type { CaptureInboxItemResponse as Capture, CaptureDetailResponse, CaptureCurationResponse, SourceConfigurationResponse } from '../api/models';
const props = defineProps<{
    captureId?: string;
}>();
const router = useRouter();
const workspaceRoute = (path: string) => withWorkspace(path, pageWorkspaceId);
type View = 'Sources' | 'Curation' | 'Gallery' | 'Delivery' | 'Settings';
const view = ref<View>('Gallery');
const settingsDirty = ref(false);
const settingsSection = ref<'Workspace' | 'Storage' | 'Members'>('Workspace');
const settingsSections = computed(() => mayAdminister.value ? ['Workspace', 'Storage', 'Members'] as const : ['Workspace'] as const);
const storageVisited = ref(false);
const settingsOrigin = ref<View>('Gallery');
const settingsOriginSurface = ref<'photos' | 'trash'>('photos');
let settingsReturnFocus: HTMLElement | undefined;
let settingsReturnCapture = '';

const retentionSurface = ref<'photos' | 'trash'>('photos');
const initialized = ref(false);
const policyStale = ref(false);
const sourcesStale = ref(false);
const capturesStale = ref(false);
const deliveryStale = ref(false);
const storageStale = ref(false);
const waitingForCuration = ref(false);
const sources = ref<SourceConfigurationResponse[]>([]);
const captureBrowser = useCaptureBrowser();
const rows = captureBrowser.rows;
const cursor = captureBrowser.cursor;
const pageAfterId = captureBrowser.pageAfterId;
const selection = captureBrowser.selection;
const browsing = captureBrowser.snapshots;
const capture = ref<CaptureDetailResponse>();
const activeId = ref(props.captureId ?? '');
const paneAId = ref(props.captureId ?? '');
const secondId = ref('');
const activePane = ref<'A' | 'B'>('A');
const compare = ref(false);
const expanded = ref(false);
const selecting = ref(false);
let orderSelection: string[] | undefined;
const error = ref('');
const detailError = ref('');
const loading = ref(false);
const pageError = ref('');
const pageRetry = reactive({ next: false, reconcile: false });
const dirtyEditors = reactive({ Sources: false, Delivery: false });
const generation = ref(0);
const evidenceGeneration = ref(0);
const deliveryGeneration = ref(0);
const previewVersions = ref<Record<string, number>>({});
const full = ref(false);
const viewerChrome = reactive(chromeState());
const chrome = new FullscreenChrome(viewerChrome);
const viewerFavoriteFeedback = ref(false);
function viewerPointerDown() {
    viewerSession.pointer(true);
}
function viewerClick(event: MouseEvent) {
    viewerSession.click(event);
}
function viewerFocus(event: FocusEvent) {
    viewerSession.focus(event);
}
const slideshow = reactive(slideshowState());
const slideshowPreview = ref<DisplayState>('pending');
const slideshowReady = ref(false);
let slideshowQuery: ReturnType<typeof query> | undefined;
const playback = new GallerySlideshow(slideshow, {
    read: async (cursor, startAt, signal, direction) => {
        signal.throwIfAborted();
        const page = await api.management.captures.get({ queryParameters: { ...slideshowQuery, cursor, startAt, oldestFirst: direction === -1 ? !slideshowQuery?.oldestFirst : slideshowQuery?.oldestFirst }, options: [cancellable(signal)] });
        signal.throwIfAborted();
        if (!page) throw new Error('Sequence unavailable');
        return page;
    },
    show: id => {
        if (id !== activeId.value) { slideshowPreview.value = 'pending'; void openCapture(id); }
        else playback.preview(slideshowPreview.value);
        void router.replace(workspaceRoute(`/captures/${encodeURIComponent(id)}`));
    },
});
function slideshowDisplayed(state: DisplayState) {
    slideshowPreview.value = state;
    playback.preview(state);
}
async function toggleSlideshow() {
    if (slideshow.playing) { playback.pause(); return; }
    if (!slideshowReady.value || !live.online.value) return;
    slideshowQuery ??= structuredClone(query());
    const id = activeId.value;
    const playing = playback.play(id);
    const generation = playback.generation;
    await playing;
    if (generation === playback.generation && full.value && slideshow.playing && id === activeId.value) playback.preview(slideshowPreview.value);
}
function retrySlideshow() {
    if (slideshow.failure === 'sequence') { void playback.retry(activeId.value); return; }
    playback.pause();
    slideshow.retry = false;
    slideshow.message = 'Choose Play when the photograph is ready.';
    previewVersions.value[activeId.value] = (previewVersions.value[activeId.value] ?? 0) + 1;
    slideshowPreview.value = 'pending';
    slideshowQuery = undefined;
}
function endSlideshow(resetInterval = true) {
    const seconds = slideshow.seconds;
    playback.stop();
    if (!resetInterval) slideshow.seconds = seconds;
    slideshowQuery = undefined;
}
function invalidateSlide(id: string) {
    const displayed = full.value && view.value === 'Gallery' && activeId.value === id;
    if (displayed) {
        detailSequence.value++;
        detailController?.abort();
        activeId.value = '';
        capture.value = undefined;
        slideshowPreview.value = 'pending';
    }
    void playback.remove(id, displayed);
}
const fullElement = ref<HTMLElement>();
const photoViewer = usePhotoViewer(fullElement, computed(() => view.value === 'Curation'));
const { zoom, offset, setZoom, boundOffset, reset: resetZoom, wheel, pointerDown, pointerMove, pointerUp } = photoViewer;
const galleryInspectionId = ref('');
const galleryInspectionOpen = ref(false);
const inspectionDeliveryState = ref('');
const inspectionMode = ref<'details' | 'delivery'>('details');
const inspectionCapture = computed(() => String(capture.value?.id) === galleryInspectionId.value && activeId.value === galleryInspectionId.value ? capture.value : undefined);
let inspectionScroll = 0;
let inspectionFocus: HTMLElement | undefined;
let inspectionReturnId = '';
const sentinel = ref<HTMLElement>();
const filterTagDraft = ref('');
const curationEvidenceOpen = ref(false);
const galleryEvidenceOpen = ref(false);
const appearanceOverlay = ref<HTMLElement>();
const curationVisit = useCurationVisit(capture, activeId);
const editSessions = curationVisit.sessions;
const tagDrafts = curationVisit.tagDrafts;
const edit = curationVisit.activeSession;
const viewerRecovery = computed(() => viewerFavoriteFeedback.value || slideshow.retry || !!slideshow.message || view.value === 'Curation' && !!edit.value && !['Saved', 'Saving'].includes(edit.value.status));
watch(viewerRecovery, pinned => chrome.pin(pinned), { flush: 'sync' });
const viewerSession = useViewerSession({
    active: full,
    element: fullElement,
    chrome,
    pinned: () => viewerRecovery.value,
    onOpen: () => {
        endSlideshow();
        viewerFavoriteFeedback.value = false;
        resetZoom();
        if (activeId.value) void router.replace(workspaceRoute(`/captures/${encodeURIComponent(activeId.value)}`));
    },
    onClose: (updateUrl, wasActive) => {
        viewerFavoriteFeedback.value = false;
        endSlideshow();
        if (!wasActive) return;
        detailController?.abort();
        detailSequence.value++;
        if (updateUrl && session.value) void router.replace(workspaceRoute('/'));
    },
    onNavigate: direction => navigate(direction),
    onCurationKey: curationKey,
});
const enterFull = viewerSession.enter;
const closeFull = viewerSession.close;
const fullscreenChanged = viewerSession.fullscreenChanged;
const keyboard = viewerSession.keyboard;
const canCurate = computed(() => mayCurate.value && !bulkBusy.value && !!edit.value?.editable && live.online.value && !['Conflict', 'Save failed', 'Checking outcome'].includes(edit.value.status));
const phone = ref(false);
const inspectorOpen = ref(false);
const tag = curationVisit.activeTag;
const suggestions = ref<string[]>([]);
let photoVisit = 0;
const busyCapture = ref(false);
const detailSequence = ref(0);
let detailController: AbortController | undefined;
const pageSequence = captureBrowser.requestSequence;
let suggestionSequence = 0;
let observer: IntersectionObserver | undefined;
const visible = ref(true);
let workspaceActive = true;
let refreshSequence = 0;
let refreshController: AbortController | undefined;
let activeRefreshScopes: Set<string> | undefined;
let selectingInitialView = false;
const systemReduced = ref(false);
let motionQuery: MediaQueryList | undefined;
provideWorkspaceClock(visible);
let tagSearch: ReturnType<typeof setTimeout> | undefined;
let tagFilterTimer: ReturnType<typeof setTimeout> | undefined;
let pagingRetry: ReturnType<typeof setTimeout> | undefined;
function visibilityChanged() {
    visible.value = !document.hidden;
    if (!visible.value)
        cancelHold();
    if (!visible.value && slideshow.playing) playback.pause('Paused while this tab is hidden. Choose Play to continue.');
}
function motionChanged() {
    systemReduced.value = motionQuery?.matches ?? false;
}
const scrollPositions = { Sources: 0, Curation: 0, Gallery: 0, Delivery: 0, Settings: 0 };
const filmRows = computed(() => {
    const active = rows.value.findIndex(row => String(row.id) === activeId.value);
    return rows.value.slice(Math.max(0, active - 60), Math.max(120, active + 60));
});
const filmItems = computed(() => filmRows.value.map(capture => ({ capture, retentionWins: retentionWins(capture), curation: workflowCuration(capture) })));
function measureGrid() {
    phone.value = window.innerWidth <= 760;
    const overlay = appearanceOverlay.value;
    if (overlay?.closest('details')?.open) {
        const bottom = phone.value ? document.querySelector<HTMLElement>('.phone-nav')?.getBoundingClientRect().top ?? window.innerHeight : window.innerHeight;
        overlay.style.maxHeight = `${Math.max(120, bottom - overlay.getBoundingClientRect().top - 16)}px`;
    }
    galleryGrid.measure();
}
let preferenceStorage: Pick<Storage, 'getItem' | 'setItem'> | undefined;
try { preferenceStorage = localStorage; } catch { /* in-memory preferences remain available */ }
const {
    preferences, preferenceKey, preferenceNotice, flushPreferenceSave,
    hydrating: hydratingPreferences,
} = useWorkspacePreferences(session, preferenceStorage);
const workspacePreferences = preferences.workspace;
const appearances = preferences.appearances;
const appearance = computed(() => appearances[view.value === 'Curation' ? 'Curation' : 'Gallery']);
const galleryGrid = useGalleryGrid(
    computed(() => rows.value.length),
    computed(() => (view.value === 'Gallery' || view.value === 'Curation') && !galleryInspectionId.value),
    computed(() => `${appearance.value.density}|${appearance.value.ratio}|${appearance.value.fit}`),
);
const gridElement = galleryGrid.element;
const renderStart = galleryGrid.start;
const renderEnd = galleryGrid.end;
const tileHeight = galleryGrid.tileHeight;
const columns = galleryGrid.columns;
const virtualRows = computed(() => rows.value.slice(renderStart.value, renderEnd.value));
const motionEnabled = computed(() => effectiveMotion(workspacePreferences.motion, systemReduced.value, visible.value, live.online.value && live.connected.value));
const filters = preferences.filters;
const previewDrafts = reactive({ gridMaximumDimension: '512', detailMaximumDimension: '2048' });
const previewErrors = reactive({ gridMaximumDimension: '', detailMaximumDimension: '' });
watch(preferenceKey, () => {
    for (const key of ['gridMaximumDimension', 'detailMaximumDimension'] as const) {
        previewDrafts[key] = String(workspacePreferences[key]); previewErrors[key] = '';
    }
}, { flush: 'sync' });
function commitPreview(key: keyof typeof previewDrafts) {
    const value = previewDimension(previewDrafts[key]);
    previewErrors[key] = value === undefined ? 'Enter a whole number from 64 to 4096 pixels.' : '';
    if (value !== undefined) workspacePreferences[key] = value;
}
function flushPreferencesOnInteractionEnd() { flushPreferenceSave(); }
function resetWorkspace() {
    Object.assign(workspacePreferences, workspaceDefaults);
    for (const key of ['gridMaximumDimension', 'detailMaximumDimension'] as const) {
        previewDrafts[key] = String(workspacePreferences[key]); previewErrors[key] = '';
    }
}
function resetAppearance() { Object.assign(appearance.value, appearanceDefaults); }
const labels = ['Red', 'Yellow', 'Green', 'Blue', 'Purple'];
const workflows = [{ name: 'Waiting', icon: 'i-lucide-clock' }, { name: 'Held', icon: 'i-lucide-pause' }, { name: 'Released', icon: 'i-lucide-send' }, { name: 'Attention', icon: 'i-lucide-triangle-alert' }];
const filter = computed(() => filters[view.value === 'Curation' ? 'Curation' : 'Gallery']);
const hasCuration = ref(true);
const availableViews = computed(() => [...(mayAdminister.value ? ['Sources'] : []), ...(mayCurate.value && (hasCuration.value || view.value === 'Curation') ? ['Curation'] : []), 'Gallery', ...(mayAdminister.value ? ['Delivery'] : [])] as View[]);
const unresolved = computed(() => bulkBusy.value || !!bulkTag.value.trim() || uncertainTrashIds.value.length > 0 || Object.values(dirtyEditors).some(Boolean) || curationVisit.hasPendingTag.value || curationVisit.hasUnresolvedEdits.value);
const unloadRisk = computed(() => unresolved.value || settingsDirty.value);
onMounted(() => {
    void live.initialize().then(() => {
        if (props.captureId) {
            view.value = 'Gallery';
            void enterFull();
        }
    });
    phone.value = window.innerWidth <= 760;
    observer = new IntersectionObserver(entries => {
        if (entries[0]?.isIntersecting)
            void nextPage();
    }, { rootMargin: '500px' });
    if (sentinel.value)
        observer.observe(sentinel.value);
    window.addEventListener('resize', measureGrid);
    motionQuery = matchMedia('(prefers-reduced-motion: reduce)');
    motionChanged();
    visibilityChanged();
    motionQuery.addEventListener('change', motionChanged);
    document.addEventListener('visibilitychange', visibilityChanged);
});
watch(() => props.captureId, id => {
    if (!initialized.value || id === activeId.value)
        return;
    endSlideshow();
    if (id)
        void openCapture(id, true);
    else
        closeFull(false);
});
watch(sentinel, (next, previous) => {
    if (previous)
        observer?.unobserve(previous);
    if (next)
        observer?.observe(next);
});
watch(() => filter.value.tag, value => { filterTagDraft.value = value; });
watch(filterTagDraft, value => {
    clearTimeout(tagFilterTimer);
    if (hydratingPreferences.value) return;
    captureBrowser.cancelRequests();
    loading.value = false;
    const owner: 'Gallery' | 'Curation' = view.value === 'Curation' ? 'Curation' : 'Gallery';
    if (filters[owner].tag === value.trim()) return;
    tagFilterTimer = setTimeout(() => { filters[owner].tag = value.trim(); }, 200);
});
watch(view, next => {
    clearTimeout(tagFilterTimer);
    tagFilterTimer = undefined;
    filterTagDraft.value = filters[next === 'Curation' ? 'Curation' : 'Gallery'].tag;
}, { flush: 'sync' });
function refreshOwnerKey() {
    return JSON.stringify([view.value, retentionSurface.value, settingsSection.value, query(false), filterTagDraft.value]);
}
function cancelRefreshForOwnerChange() {
    if (selectingInitialView) return;
    captureBrowser.cancelRequests();
    loading.value = false;
    if (!refreshController || refreshController.signal.aborted) return;
    const scopes = activeRefreshScopes;
    refreshController.abort();
    refreshSequence++;
    activeRefreshScopes = undefined;
    loading.value = false;
    if (scopes?.has('sources')) { sourcesStale.value = true; policyStale.value = true; }
    if (scopes?.has('capture')) capturesStale.value = true;
    if (scopes?.has('delivery')) deliveryStale.value = true;
    if (scopes?.has('storage')) storageStale.value = true;
}
watch(refreshOwnerKey, cancelRefreshForOwnerChange, { flush: 'sync' });
watch(selection, () => { orderSelection = undefined; }, { deep: true, flush: 'sync' });
for (const name of ['Gallery', 'Curation'] as const) {
    watch(() => appearances[name].sort, () => {
        if (hydratingPreferences.value) return;
        if (name === 'Gallery') endSlideshow(false);
        browsing[name] = { rows: [], cursor: null, afterId: '', selection: [] };
        scrollPositions[name] = 0;
        if (view.value !== name) return;
        const retainedSelection = orderSelection ?? [...selection.value];
        rows.value = [];
        selection.value = [];
        orderSelection = retainedSelection;
        cursor.value = null;
        pageAfterId.value = '';
        captureBrowser.cancelRequests();
        if (initialized.value) void loadPage(false);
    }, { flush: 'sync' });
    watch(filters[name], () => {
        if (name === 'Gallery') endSlideshow(false);
        if (!hydratingPreferences.value && initialized.value && view.value === name) void loadPage(false);
    }, { deep: true });
}
function clearFilters() {
    Object.assign(filter.value, { workflows: [], sourceIds: [], collectionIds: [], rating: null, ratingMaximum: null, labels: [], tag: '', favoritesOnly: false });
}
function toggleFilter(key: 'workflows' | 'sourceIds', value: string) {
    const selected = filter.value[key];
    filter.value[key] = selected.includes(value) ? selected.filter(item => item !== value) : [...selected, value];
}
function cameraDescription(row: Capture) {
    return [...new Set(row.assets?.map(asset => [asset.cameraMake, asset.cameraModel].filter(Boolean).join(' ')).filter(Boolean))].join(' · ') || row.source?.name || 'Camera unavailable';
}
function query(next = false) {
    const f = filter.value;
    return captureBrowser.query({ ...f, oldestFirst: appearance.value.sort === 'oldest' }, next);
}
async function revalidateBoundary(incoming: Capture[], isCurrent: () => boolean, signal: AbortSignal) {
    const retained: Capture[] = [];
    const freshIds = new Set(incoming.map(row => String(row.id)));
    const boundaryTime = incoming.at(-1)?.acceptedAt?.getTime();
    for (const cached of rows.value.filter(matchesFilter)) {
        let row = cached;
        if (!freshIds.has(String(row.id)) && boundaryTime != null && row.acceptedAt?.getTime() === boundaryTime) {
            try {
                const current = await api.management.captures.byId(String(row.id)).get({ options: [cancellable(signal)] });
                if (!isCurrent()) return retained;
                if (current) {
                    row = { ...row, ...current };
                    if (current.curation) curationVisit.sync(String(row.id), current.curation);
                }
                else pageError.value = 'Some loaded Captures could not be verified. Refresh to retry.';
            } catch (failure) {
                if (!isCurrent()) return retained;
                const status = handleFailure(failure);
                if (status === 404) continue;
                pageError.value = 'Some loaded Captures could not be verified. Refresh to retry.';
            }
        }
        if (matchesFilter(row)) retained.push(row);
    }
    return retained;
}
async function loadPage(next: boolean, reconcile = false, parentSignal?: AbortSignal) {
    if (next && (loading.value || !cursor.value || pageError.value))
        return;
    const access = accessGeneration();
    const requestView = view.value;
    const requestSurface = retentionSurface.value;
    const requestCriteria = JSON.stringify(query(false));
    const requestTagDraft = filterTagDraft.value;
    const viewerAnchor = !mayCurate.value && !next && rows.value.length
        ? (galleryInspectionId.value || full.value ? activeId.value : gridElement.value?.querySelector<HTMLElement>('.gallery-tile')?.dataset.id ?? String(rows.value[0]?.id))
        : undefined;
    const pageRequest = captureBrowser.requestPage({
        query: { ...query(next), ...(viewerAnchor ? { startAt: viewerAnchor } : {}) },
        parentSignal,
        onCancel: () => { loading.value = false; },
        read: (queryParameters, signal) => api.management.captures.get({ queryParameters, options: [cancellable(signal)] }),
    });
    const request = pageRequest.request;
    const signal = captureBrowser.requestSignal(request);
    if (!signal) return;
    const isCurrent = () => captureBrowser.isCurrent(request) && access === accessGeneration() && workspaceActive
        && requestView === view.value && requestSurface === retentionSurface.value
        && requestCriteria === JSON.stringify(query(false)) && requestTagDraft === filterTagDraft.value;
    Object.assign(pageRetry, { next, reconcile });
    loading.value = true;
    pageError.value = '';
    try {
        const response = await pageRequest.response;
        if (!response.current || !isCurrent()) return;
        if ('error' in response) throw response.error;
        const page = response.value;
        if (!isCurrent())
            return;
        const anchor = !galleryInspectionId.value ? gridElement.value?.querySelector<HTMLElement>('.gallery-tile') : undefined;
        const anchorId = anchor?.dataset.id;
        const before = anchor?.getBoundingClientRect().top;
        const incoming = page?.items ?? [];
        const retained = reconcile && page?.nextCursor ? await revalidateBoundary(incoming, isCurrent, signal) : rows.value;
        if (!isCurrent()) return;
        rows.value = next || reconcile && !!page?.nextCursor ? reconcileCaptures(next ? rows.value : retained, incoming, next, pageAfterId.value, appearance.value.sort === 'oldest') : incoming;
        if (!mayCurate.value && !next) playback.reconcilePage(page ?? { items: [] });
        captureBrowser.acceptPage(page);
        if (!mayCurate.value) captureBrowser.save('Gallery');
        if (orderSelection) {
            const loaded = new Set(rows.value.map(row => String(row.id)));
            selection.value = orderSelection.filter(id => loaded.has(id));
            orderSelection = undefined;
        }
        evidenceGeneration.value++;
        await nextTick();
        if (!isCurrent()) return;
        measureGrid();
        if (reconcile && anchorId && before != null) {
            const after = [...(gridElement.value?.querySelectorAll<HTMLElement>('.gallery-tile') ?? [])].find(item => item.dataset.id === anchorId)?.getBoundingClientRect().top;
            if (after != null)
                window.scrollBy(0, after - before);
        }
        for (const row of rows.value)
            if (row.curation)
                curationVisit.sync(String(row.id), row.curation);
        if ((!activeId.value || view.value === 'Curation' && !rows.value.some(row => String(row.id) === activeId.value)) && rows.value[0])
            await openCapture(String(rows.value[0].id), false, undefined, false, signal);
        if (!rows.value.length && view.value === 'Curation') {
            closeFull(false);
            detailSequence.value++;
            activeId.value = paneAId.value = secondId.value = '';
            capture.value = undefined;
            compare.value = false;
        }
    }
    catch (failure) {
        if (!isCurrent()) return;
        handleFailure(failure);
        pageError.value = 'Unable to load Captures. The current photographs are retained.';
    }
    finally {
        captureBrowser.finishRequest(request);
        if (isCurrent()) {
            loading.value = false;
        }
    }
}
async function nextPage() {
    const before = rows.value.length;
    const previousCursor = cursor.value;
    const request = pageSequence.value + 1;
    await loadPage(true);
    if (captureBrowser.isCurrent(request) && previousCursor && cursor.value && previousCursor !== cursor.value && rows.value.length === before && !pageError.value && (sentinel.value?.getBoundingClientRect().top ?? Infinity) < window.innerHeight + 500) {
        clearTimeout(pagingRetry);
        pagingRetry = setTimeout(() => {
            if (captureBrowser.isCurrent(request)) void nextPage();
        }, 0);
    }
}
function matchesFilter(row: Capture) {
    const f = filter.value;
    const c = row.curation;
    const attention = row.deliverySummary?.attention || !!c?.releasedAt && row.deliverySummary?.waitingForRouting;
    return (!f.collectionIds.length || f.collectionIds.includes(String(row.currentCollection?.id))) && (!f.favoritesOnly || row.retention?.isFavorite === true) && row.retention?.disposition !== 'trash' && row.retention?.disposition !== 'purging' && row.retention?.disposition !== 'purged' && (!f.sourceIds.length || f.sourceIds.includes(String(row.sourceId))) && (f.rating == null || (c?.rating ?? 0) >= f.rating && (c?.rating ?? 0) <= (f.ratingMaximum ?? f.rating)) && (!f.labels.length || f.labels.includes(c?.label ?? 'None')) && (!f.tag || c?.tags?.includes(f.tag)) && (!f.workflows.length || f.workflows.some(workflow => workflow === 'Waiting' && !c?.releasedAt || workflow === 'Held' && !c?.releasedAt && c?.held || workflow === 'Released' && !!c?.releasedAt || workflow === 'Attention' && attention));
}
function unavailableInViewer(row: Capture) {
    return ['trash', 'purging', 'purged'].includes(row.retention?.disposition ?? '') || !!slideshowQuery && !matchesFilter(row);
}
async function refreshData(ids?: Set<string>, previews?: {
    all: boolean;
    ids: Set<string>;
}, scopes = new Set(['capture', 'previews', 'sources', 'delivery', 'storage', 'erasure'])) {
    const previousAccess = accessGeneration();
    if (!await refreshWorkspaceAccess() || previousAccess !== accessGeneration() || !workspaceActive) return;
    if (scopes.has('capture') || scopes.has('previews')) slideshowReady.value = false;
    const access = accessGeneration();
    refreshController?.abort();
    const controller = refreshController = new AbortController();
    activeRefreshScopes = scopes;
    let ownerKey = refreshOwnerKey();
    const current = () => workspaceActive && !controller.signal.aborted && access === accessGeneration() && ownerKey === refreshOwnerKey();
    const requested = ++refreshSequence;
    const viewerDeadline = !mayCurate.value ? setTimeout(() => {
        if (current()) closeWorkspaceAccess();
    }, 8_000) : undefined;
    try {
    const readSources = mayAdminister.value && scopes.has('sources');
    if (!mayAdminister.value && !initialized.value) initialized.value = true;
    if (readSources) try {
        const page = await api.management.sources.get({ queryParameters: { limit: 100 }, options: [cancellable(controller.signal)] });
        if (!current() || requested !== refreshSequence) return;
        sources.value = page?.items ?? [];
        policyStale.value = !!page?.nextCursor;
        sourcesStale.value = false;
        if (!initialized.value) {
            selectingInitialView = true;
            view.value = !props.captureId && sources.value.some(source => source.curationMode === 'Delay' || source.curationMode === 'Manual') ? 'Curation' : 'Gallery';
            initialized.value = true;
            ownerKey = refreshOwnerKey();
            selectingInitialView = false;
        }
    }
    catch (failure) {
        if (!current()) return;
        handleFailure(failure);
        policyStale.value = true;
        sourcesStale.value = true;
    }
    if (!session.value || !current() || requested !== refreshSequence)
        return;
    const captureActive = (view.value === 'Gallery' || view.value === 'Curation') && retentionSurface.value === 'photos';
    if (!mayCurate.value && !captureActive) {
        captureBrowser.clear();
        capture.value = undefined;
        activeId.value = paneAId.value = secondId.value = galleryInspectionId.value = '';
        previewVersions.value = {};
        endSlideshow();
    }
    const deliveryActive = view.value === 'Delivery';
    const storageActive = view.value === 'Gallery' && retentionSurface.value === 'trash' || view.value === 'Settings' && settingsSection.value === 'Storage';
    if (scopes.has('capture') && !captureActive) capturesStale.value = true;
    if (scopes.has('delivery') && !deliveryActive) deliveryStale.value = true;
    if (scopes.has('storage') && !storageActive) storageStale.value = true;
    if (scopes.has('delivery') && deliveryActive) { deliveryGeneration.value++; deliveryStale.value = false; }
    if (scopes.has('storage') && storageActive) { generation.value++; storageStale.value = false; }
    if (mayCurate.value && scopes.has('capture') && captureActive) {
        try {
            const waiting = await api.management.captures.get({ queryParameters: { released: false, limit: 1 }, options: [cancellable(controller.signal)] });
            if (!current() || requested !== refreshSequence) return;
            waitingForCuration.value = !!waiting?.items?.length;
        } catch (failure) {
            if (!current()) return;
            handleFailure(failure);
        }
    }
    hasCuration.value = mayCurate.value && (policyStale.value || sources.value.some(s => s.curationMode !== 'Immediate') || waitingForCuration.value);
    if (scopes.has('capture') && (scopes.has('delivery') || scopes.has('storage') || scopes.has('erasure'))) evidenceGeneration.value++;
    if (scopes.has('capture') && captureActive && ids && rows.value.length) {
        for (const id of ids) {
            const index = rows.value.findIndex(r => String(r.id) === id);
            if (index < 0)
                continue;
            try {
                const value = await api.management.captures.byId(id).get({ options: [cancellable(controller.signal)] });
                if (!current() || requested !== refreshSequence)
                    return;
                if (value) {
                    const currentIndex = rows.value.findIndex(row => String(row.id) === id);
                    if (currentIndex < 0) continue;
                    rows.value[currentIndex] = { ...rows.value[currentIndex], ...value };
                    if (value.curation)
                        curationVisit.sync(id, value.curation);
                }
            }
            catch (failure) {
                if (!current() || requested !== refreshSequence) return;
                const status = handleFailure(failure);
                if (status === 404) await forgetUnavailableCapture(id);
            }
        }
        for (const row of rows.value) if (unavailableInViewer(row)) invalidateSlide(String(row.id));
        rows.value = rows.value.filter(matchesFilter);
        evidenceGeneration.value++;
    }
    if (scopes.has('capture') && captureActive) {
        await loadPage(false, mayCurate.value && rows.value.length > 0, controller.signal);
        if (!current() || requested !== refreshSequence) return;
        capturesStale.value = false;
        evidenceGeneration.value++;
    }
    if (scopes.has('capture') && captureActive && activeId.value && (!ids || ids.has(activeId.value))) {
        await openCapture(activeId.value, false, undefined, false, controller.signal);
        if (!current() || requested !== refreshSequence) return;
    }
    if (scopes.has('previews') && previews) {
        const idsToInvalidate = previews.all || scopes.size === 6 && !previews.ids.size
            ? new Set([...rows.value.map(row => String(row.id)), activeId.value, secondId.value])
            : previews.ids;
        for (const id of idsToInvalidate)
            if (id) previewVersions.value[id] = (previewVersions.value[id] ?? 0) + 1;
    }
    if (current() && requested === refreshSequence) {
        if (!mayCurate.value && (pageError.value || detailError.value)) { closeWorkspaceAccess(); return; }
        slideshowReady.value = !pageError.value && !detailError.value;
    }
    } finally {
        clearTimeout(viewerDeadline);
        if (refreshController === controller && activeRefreshScopes === scopes)
            activeRefreshScopes = undefined;
    }
}
const live = useReconciliation(refreshData);
const curationActions = useCaptureCurationActions({
    selection, rows, sessions: editSessions,
    online: computed(() => live.online.value && mayCurate.value), view,
    visit: () => photoVisit,
    observe: observeCuration,
    forget: id => { captureBrowser.forget(id); selection.value = selection.value.filter(value => value !== id); },
    refresh: ids => refresh(ids),
    confirm: message => window.confirm(message),
});
const {
    busy: bulkBusy, result: bulkResult, rating: bulkRating, label: bulkLabel,
    tag: bulkTag, removedTag: bulkRemovedTag, selectedValues, valuesSummary: bulkValues,
    uncertainTrashIds, pendingTrashIds, trashVerificationBusy, trashVerificationMessage,
} = curationActions;
const bulkTrashUncertain = computed(() => selection.value.some(id => uncertainTrashIds.value.includes(id)));
watch(unloadRisk, risk => {
    if (risk) window.addEventListener('beforeunload', beforeUnload);
    else window.removeEventListener('beforeunload', beforeUnload);
}, { immediate: true });
watch(() => [view.value, retentionSurface.value, settingsSection.value], () => {
    if (view.value === 'Sources' && sourcesStale.value) live.invalidate('sources');
    if (view.value === 'Delivery' && deliveryStale.value) live.invalidate('delivery');
    if (view.value === 'Gallery' && retentionSurface.value === 'trash' && storageStale.value) live.invalidate('storage');
    if (view.value === 'Settings' && settingsSection.value === 'Storage' && storageStale.value) live.invalidate('storage');
    if ((view.value === 'Gallery' || view.value === 'Curation') && retentionSurface.value === 'photos' && capturesStale.value) live.invalidate('capture');
});
function refresh(ids?: Set<string>, previews?: { all: boolean; ids: Set<string> }) {
    return live.invalidate(ids ? ['capture', 'delivery', 'storage'] : ['capture', 'previews', 'sources', 'delivery', 'storage', 'erasure'], ids, previews);
}
watch(() => [live.online.value, live.connected.value, live.connectionRecovery.value], () => {
    slideshowReady.value = false;
    if (slideshow.playing) playback.pause('Connection changed. Choose Play after the Gallery has refreshed.');
}, { flush: 'sync' });
watch(() => live.activity.value, hint => {
    if (hint?.changeKind !== 'erasure') return;
    previewVersions.value[hint.captureId] = (previewVersions.value[hint.captureId] ?? 0) + 1;
    void forgetUnavailableCapture(hint.captureId);
});
async function openCapture(id: string, viewer = false, pane?: 'A' | 'B', focusOnly = false, parentSignal?: AbortSignal) {
    if (parentSignal?.aborted) return;
    detailController?.abort();
    const controller = detailController = new AbortController();
    const request = ++detailSequence.value;
    const access = accessGeneration();
    const ownerView = view.value;
    const ownerSurface = retentionSurface.value;
    const ownerKey = refreshOwnerKey();
    const cancelFromParent = () => controller.abort();
    if (parentSignal?.aborted) cancelFromParent();
    else parentSignal?.addEventListener('abort', cancelFromParent, { once: true });
    const isCurrent = () => request === detailSequence.value && access === accessGeneration() && workspaceActive
        && !controller.signal.aborted && ownerView === view.value && ownerSurface === retentionSurface.value && ownerKey === refreshOwnerKey();
    if (pane)
        activePane.value = pane;
    else if (!compare.value && !focusOnly)
        activePane.value = 'A';
    if (!focusOnly) {
        if (activePane.value === 'B' && compare.value)
            secondId.value = id;
        else
            paneAId.value = id;
    }
    activeId.value = id;
    if (view.value === 'Gallery' && galleryInspectionId.value)
        galleryInspectionId.value = id;
    if (String(capture.value?.id) !== id)
        capture.value = undefined;
    suggestions.value = [];
    suggestionSequence++;
    clearTimeout(tagSearch);
    error.value = '';
    detailError.value = '';
    if (viewer)
        void enterFull();
    try {
        const value = await api.management.captures.byId(id).get({ options: [cancellable(controller.signal)] });
        if (!isCurrent())
            return;
        capture.value = value;
        if (full.value && view.value === 'Gallery' && value && unavailableInViewer(value as Capture)) {
            invalidateSlide(id);
            return;
        }
        if (value?.curation) {
            if (full.value && view.value === 'Gallery') curationVisit.sync(id, value.curation);
            else observeCuration(id, value.curation);
        }
    }
    catch (failure) {
        if (!isCurrent()) return;
        const status = handleFailure(failure);
        if (status === 404 && isCurrent()) {
            await forgetUnavailableCapture(id);
            error.value = mayCurate.value
                ? 'This Capture is no longer available in this workspace. Open Trash or ask an administrator for current recovery/removal evidence.'
                : 'This photograph is not available to you in this workspace. Choose a visible photograph.';
        }
        else if (isCurrent())
            detailError.value = 'Unable to open this Capture. Refresh to try again.';
    } finally {
        parentSignal?.removeEventListener('abort', cancelFromParent);
    }
}
function observeCuration(id: string, current: CaptureCurationResponse) {
    if (!mayCurate.value) return;
    return curationVisit.observe(id, current, body => api.management.captures.byId(id).curation.put(body), async () => {
        const detail = await api.management.captures.byId(id).get();
        if (!detail?.curation)
            throw new Error('Capture unavailable');
        return detail.curation;
    }, handleFailure);
}
async function openSettings(section: 'Workspace' | 'Storage' = 'Workspace') {
    if (section === 'Storage' && !mayAdminister.value) return;
    const origin = view.value;
    const originSurface = retentionSurface.value;
    const focus = document.activeElement as HTMLElement | undefined;
    const returnCapture = activeId.value;
    await switchView('Settings');
    if (view.value !== 'Settings') return;
    if (origin !== 'Settings') {
        settingsOrigin.value = origin;
        settingsOriginSurface.value = originSurface;
        settingsReturnFocus = focus;
        settingsReturnCapture = returnCapture;
    }
    selectSettingsSection(section);
    await nextTick();
    document.getElementById?.('settings-section-heading')?.focus();
}
function selectSettingsSection(section: 'Workspace' | 'Storage' | 'Members') {
    if (section !== 'Workspace' && !mayAdminister.value) return;
    settingsSection.value = section;
    if (section === 'Storage') storageVisited.value = true;
}
async function returnFromSettings() {
    await switchView(settingsOrigin.value);
    if (view.value === 'Settings') return;
    if (view.value === 'Gallery') retentionSurface.value = settingsOriginSurface.value;
    await nextTick();
    const tile = [...(gridElement.value?.querySelectorAll<HTMLElement>('.gallery-tile') ?? [])].find(item => item.dataset.id === settingsReturnCapture);
    const target = settingsReturnFocus?.isConnected ? settingsReturnFocus : tile?.querySelector<HTMLElement>('.tile-open') ?? document.querySelector<HTMLElement>('.view-heading h1');
    target?.focus({ preventScroll: true });
}
async function switchView(next: View) {
    if ((next === 'Sources' || next === 'Delivery') && !mayAdminister.value || next === 'Curation' && !mayCurate.value) return;
    if (next === view.value)
        return;
    if (view.value === 'Settings' && settingsDirty.value && !window.confirm('Discard unsaved storage settings and leave Settings?')) return;
    if ((view.value === 'Sources' && dirtyEditors.Sources) || (view.value === 'Delivery' && dirtyEditors.Delivery)) {
        if (!window.confirm(`Discard unsaved ${view.value} changes and leave this workspace?`)) return;
        dirtyEditors[view.value] = false;
    }
    settingsDirty.value = false;
    if (view.value === 'Settings') storageVisited.value = false;
    const leavingPhotos = ['Sources', 'Delivery', 'Settings'].includes(next) && ['Gallery', 'Curation'].includes(view.value);
    if (leavingPhotos && unresolved.value && !window.confirm('Unresolved curation edits remain. Abandon local intent and leave the photo workspace?'))
        return;
    if (leavingPhotos) {
        curationActions.cancelReads();
        bulkTag.value = '';
        photoVisit++;
        curationVisit.clear();
    }
    captureBrowser.cancelRequests();
    loading.value = false;
    const leavingScroll = view.value === 'Gallery' && galleryInspectionId.value ? inspectionScroll : window.scrollY;
    galleryInspectionId.value = '';
    inspectionFocus = undefined;
    inspectionReturnId = '';
    detailSequence.value++;
    cancelHold();
    scrollPositions[view.value] = leavingScroll;
    if (view.value === 'Curation' || view.value === 'Gallery')
        captureBrowser.save(view.value);
    view.value = next;
    retentionSurface.value = 'photos';
    selecting.value = false;
    selection.value = [];
    orderSelection = undefined;
    closeFull();
    if (next === 'Gallery' || next === 'Curation') {
        captureBrowser.restore(next);
    }
    await nextTick();
    window.scrollTo(0, scrollPositions[next]);
    if (next === 'Gallery' || next === 'Curation') {
        await loadPage(false, rows.value.length > 0);
        if (activeId.value)
            await openCapture(activeId.value, false, undefined, true);
    }
}
function beforeUnload(event: BeforeUnloadEvent) {
    if (unloadRisk.value) {
        event.preventDefault();
        event.returnValue = '';
    }
}
function requestDeparture() {
    const allowed = !unloadRisk.value || window.confirm('Unresolved changes remain. Discard them and leave the workspace?');
    if (allowed) {
        curationActions.cancelReads();
        bulkTag.value = '';
    }
    return allowed;
}
defineExpose({ requestDeparture });
const removeRouteGuard = router.beforeEach(to => {
    const targetWorkspaceId = readWorkspaceId(`?workspace=${encodeURIComponent(String(to.query.workspace ?? ''))}`);
    if (targetWorkspaceId !== pageWorkspaceId) {
        if (!requestDeparture()) return false;
        window.location.assign(to.fullPath);
        return false;
    }
    const allowed = to.path === '/' || to.path.startsWith('/captures/') || requestDeparture();
    if (allowed && full.value && to.path !== `/captures/${encodeURIComponent(activeId.value)}`) closeFull(false);
    return allowed;
});
const unsubscribe = onAccessLost(() => {
    refreshSequence++;
    refreshController?.abort();
    refreshController = undefined;
    activeRefreshScopes = undefined;
    detailController?.abort();
    detailSequence.value++;
    settingsReturnFocus = undefined;
    settingsReturnCapture = '';
    settingsDirty.value = false;
    curationActions.clearPrivateState();
    bulkTag.value = '';
    storageVisited.value = false;
    photoVisit++;
    captureBrowser.clear();
    detailSequence.value++;
    suggestionSequence++;
    clearTimeout(tagSearch);
    clearTimeout(pagingRetry);
    curationVisit.clear();
    capture.value = undefined;
    detailError.value = '';
    pageError.value = '';
    Object.assign(pageRetry, { next: false, reconcile: false });
    galleryInspectionId.value = '';
    inspectionFocus = undefined;
    inspectionReturnId = '';
    inspectionScroll = 0;
    rows.value = [];
    cursor.value = null;
    pageAfterId.value = '';
    browsing.Curation = { rows: [], cursor: null, afterId: '', selection: [] };
    browsing.Gallery = { rows: [], cursor: null, afterId: '', selection: [] };
    sources.value = [];
    selection.value = [];
    orderSelection = undefined;
    suggestions.value = [];
    closeFull(false);
});
onUnmounted(() => {
    workspaceActive = false;
    curationActions.cancelReads();
    refreshSequence++;
    refreshController?.abort();
    captureBrowser.cancelRequests();
    chrome.stop();
    endSlideshow();
    detailController?.abort();
    photoVisit++;
    unsubscribe();
    observer?.disconnect();
    cancelHold();
    for (const value of editSessions.values()) value.stop();
    window.removeEventListener('beforeunload', beforeUnload);
    window.removeEventListener('resize', measureGrid);
    document.removeEventListener('visibilitychange', visibilityChanged);
    motionQuery?.removeEventListener('change', motionChanged);
    clearTimeout(tagSearch);
    clearTimeout(tagFilterTimer);
    clearTimeout(pagingRetry);
    removeRouteGuard();
});
function queueSuggestions() {
    clearTimeout(tagSearch);
    suggestionSequence++;
    if (!tag.value.trim()) {
        suggestions.value = [];
        return;
    }
    tagSearch = setTimeout(() => void suggest(), 180);
}
async function suggest() {
    const request = ++suggestionSequence;
    const id = activeId.value;
    const query = tag.value;
    if (!query.trim()) {
        suggestions.value = [];
        return;
    }
    try {
        const result = await api.management.tags.get({ queryParameters: { q: query } });
        if (request === suggestionSequence && id === activeId.value && query === tag.value)
            suggestions.value = (result?.tags ?? []).filter(value => !edit.value?.intent.tags.some(attached => attached.toLowerCase() === value.toLowerCase()));
    }
    catch (failure) {
        handleFailure(failure);
        if (request === suggestionSequence)
            suggestions.value = [];
    }
}
function addTag(value = tag.value) {
    if (!canCurate.value)
        return;
    value = value.trim();
    if (!value || value.length > 64 || (edit.value?.intent.tags.length ?? 0) >= 32 || edit.value?.intent.tags.some(t => t.toLowerCase() === value.toLowerCase()))
        return;
    edit.value?.change({ tags: [...edit.value.intent.tags, value] });
    tag.value = '';
    suggestions.value = [];
    suggestionSequence++;
}
function toggleLabel(label: string) {
    const index = filter.value.labels.indexOf(label);
    if (index < 0)
        filter.value.labels.push(label);
    else
        filter.value.labels.splice(index, 1);
}
function toggle(id: string) {
    if (bulkBusy.value) return;
    const index = selection.value.indexOf(id);
    if (index >= 0)
        selection.value.splice(index, 1);
    else if (selection.value.length < 100)
        selection.value.push(id);
    else
        error.value = 'Select at most 100 Captures.';
}
function selectLoaded() {
    if (bulkBusy.value) return;
    const loaded = rows.value.slice(0, 100).map(row => String(row.id));
    selection.value = loaded;
    if (rows.value.length > 100)
        error.value = 'Selected the first 100 loaded Captures. The remaining loaded Captures are unselected.';
}
function toggleCompare() {
    compare.value = !compare.value;
    if (compare.value && (!secondId.value || secondId.value === paneAId.value))
        secondId.value = String(rows.value.find(row => String(row.id) !== paneAId.value)?.id ?? '');
}
async function inspectCapture(id: string, deliveryState = '', mode: 'details' | 'delivery' = 'details') {
    if (view.value !== 'Gallery')
        return;
    inspectionDeliveryState.value = deliveryState;
    inspectionMode.value = mode;
    galleryEvidenceOpen.value = mode === 'delivery';
    if (!galleryInspectionId.value) {
        inspectionScroll = window.scrollY;
        inspectionFocus = document.activeElement as HTMLElement | undefined;
        inspectionReturnId = id;
    }
    galleryInspectionId.value = id;
    galleryInspectionOpen.value = true;
    cancelHold();
    await openCapture(id, false, undefined, true);
    if (galleryInspectionId.value !== id || !inspectionCapture.value)
        return;
    await nextTick();
    window.scrollTo(0, 0);
}
async function closeGalleryInspection() {
    if (!galleryInspectionId.value)
        return;
    galleryInspectionId.value = '';
    detailSequence.value++;
    await nextTick();
    window.scrollTo(0, inspectionScroll);
    measureGrid();
    const fallback = [...(gridElement.value?.querySelectorAll<HTMLElement>('.tile-open') ?? [])].find(button => button.closest<HTMLElement>('.gallery-tile')?.dataset.id === inspectionReturnId);
    const target = inspectionFocus?.isConnected ? inspectionFocus : fallback ?? gridElement.value;
    target?.focus({ preventScroll: true });
    inspectionFocus = undefined;
    inspectionReturnId = '';
}
let holdTimer: ReturnType<typeof setTimeout> | undefined;
let holdStart = { x: 0, y: 0 };
let held = false;
function startHold(event: PointerEvent, id: string) {
    if (!mayCurate.value) return;
    held = false;
    if (event.pointerType !== 'touch' || selecting.value)
        return;
    holdStart = { x: event.clientX, y: event.clientY };
    holdTimer = setTimeout(() => {
        selecting.value = true;
        toggle(id);
        held = true;
    }, 500);
}
function cancelHold(event?: PointerEvent) {
    if (!event || Math.hypot(event.clientX - holdStart.x, event.clientY - holdStart.y) > 8) {
        clearTimeout(holdTimer);
        holdTimer = undefined;
    }
}
function endHold() {
    clearTimeout(holdTimer);
}
function tileClick(id: string) {
    clearTimeout(holdTimer);
    if (held) {
        held = false;
        return;
    }
    if (selecting.value || view.value === 'Curation' && expanded.value)
        toggle(id);
    else if (view.value === 'Gallery')
        void inspectCapture(id);
    else
        void openCapture(id);
}
async function openGalleryFull() {
    if (view.value !== 'Gallery' || !rows.value.length) return;
    const id = rows.value.some(row => String(row.id) === activeId.value) ? activeId.value : String(rows.value[0]!.id);
    retentionSurface.value = 'photos';
    await openCapture(id, true);
}
async function bulk(action: 'rating' | 'label' | 'tag' | 'removeTag' | 'discard' | 'hold' | 'restore' | 'release' | 'reset') {
    const submittedTag = action === 'tag' ? bulkTag.value : '';
    const committed = await curationActions.run(action);
    if (action === 'tag' && committed && submittedTag && bulkTag.value === submittedTag)
        bulkTag.value = '';
}
async function captureAction(action: 'hold' | 'restore' | 'release') {
    if (!mayCurate.value || view.value !== 'Curation' || !edit.value || busyCapture.value || bulkBusy.value)
        return;
    const id = activeId.value;
    const value = edit.value;
    busyCapture.value = true;
    const access = accessGeneration();
    await value.settle();
    if (value.unresolved || value.server.releasedAt) {
        busyCapture.value = false;
        return;
    }
    try {
        const result = await api.management.captures.byId(id)[action].post({ expectedRevision: value.server.revision });
        if (access !== accessGeneration())
            return;
        if (result)
            value.observe(result);
        await refresh(new Set([id]));
    }
    catch (failure) {
        handleFailure(failure);
        if (access !== accessGeneration())
            return;
        error.value = 'Command outcome requires review. Refresh server state before retrying.';
        await value.check();
    }
    finally {
        busyCapture.value = false;
    }
}
function state(row: Capture) {
    return row.curation?.releasedAt ? row.deliverySummary?.attention ? 'Needs attention' : row.deliverySummary?.confirmed ? `${row.deliverySummary.confirmed} confirmed` : 'Released · delivery pending' : row.curation?.held ? 'Held' : 'Awaiting release';
}
function navigate(delta: number) {
    if (full.value && view.value === 'Gallery') {
        slideshowQuery ??= structuredClone(query());
        void playback.navigate(activeId.value, delta < 0 ? -1 : 1);
        return;
    }
    if (slideshow.playing) playback.pause();
    slideshowQuery = undefined;
    const index = rows.value.findIndex(row => String(row.id) === activeId.value);
    const next = rows.value[index + delta];
    if (next) {
        resetZoom();
        void openCapture(String(next.id));
        if (full.value)
            void router.replace(workspaceRoute(`/captures/${encodeURIComponent(String(next.id))}`));
    }
}
function curationKey(event: KeyboardEvent) {
    if (view.value !== 'Curation' || !mayCurate.value) return false;
    if (view.value === 'Curation' && ['+', '=', '-', 'Home'].includes(event.key)) {
        event.preventDefault();
        setZoom(event.key === 'Home' ? 1 : zoom.value * (event.key === '-' ? 1 / 1.25 : 1.25));
        return true;
    }
    if (zoom.value > 1 && ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
        event.preventDefault();
        offset.x += event.key === 'ArrowLeft' ? 40 : event.key === 'ArrowRight' ? -40 : 0;
        offset.y += event.key === 'ArrowUp' ? 40 : event.key === 'ArrowDown' ? -40 : 0;
        boundOffset();
        return true;
    }
    if (canCurate.value && /^[0-5]$/.test(event.key)) {
        edit.value?.change({ rating: Number(event.key) });
        return true;
    }
    if (canCurate.value && /^[rR]$/.test(event.key)
        && !event.ctrlKey && !event.metaKey && !event.altKey && !event.isComposing) {
        event.preventDefault();
        edit.value?.change({ rating: -1 });
        return true;
    }
    return false;
}
const paneA = computed(() => rows.value.find(row => String(row.id) === paneAId.value));
const paneB = computed(() => rows.value.find(row => String(row.id) === secondId.value));
function deliveryStatus(row: Capture) {
    const summary = row.deliverySummary;
    if (summary?.attention) return { icon: 'i-lucide-triangle-alert', title: 'Delivery needs attention', state: 'Attention' };
    if (summary?.noRoute) return { icon: 'i-lucide-info', title: 'No matching delivery rule', state: '' };
    if (summary?.retryScheduled) return { icon: 'i-lucide-timer-reset', title: 'Delivery scheduled for retry', state: 'RetryWait' };
    if (summary?.running) return { icon: 'i-lucide-loader-circle', title: 'Delivery running', state: 'Running' };
    if (summary?.queued) return { icon: 'i-lucide-clock-3', title: 'Delivery queued', state: 'Pending' };
    if (summary?.confirmed) return { icon: 'i-lucide-check-check', title: 'Delivery confirmed', state: 'Delivered' };
    return { icon: 'i-lucide-info', title: summary?.noRoute ? 'No matching delivery rule' : 'No delivery yet', state: '' };
}
async function inspectDelivery(row: Capture) {
    await inspectCapture(String(row.id), deliveryStatus(row).state, 'delivery');
}
function workflowStatus(row: Capture | CaptureDetailResponse | undefined) {
    const now = Date.now();
    return curationTiming(workflowCuration(row), retentionTiming(row?.retention, now, true, !live.online.value).stale ? row?.retention?.asOf?.getTime() ?? now : now);
}
function workflowCuration(row: Capture | CaptureDetailResponse | undefined) {
    const saved = editSessions.get(String(row?.id))?.server;
    const detail = capture.value?.id === row?.id ? capture.value?.curation : undefined;
    const current = detail && (detail.revision ?? 0) > (row?.curation?.revision ?? 0) ? detail : row?.curation;
    return saved && (saved.revision ?? 0) > (current?.revision ?? 0) ? saved : current;
}
function retentionWins(row: Capture | CaptureDetailResponse | undefined) {
    const now = Date.now();
    const timing = retentionTiming(row?.retention, now, true, !live.online.value);
    const deadline = row?.retention?.timeline?.endsAt?.getTime();
    const release = workflowStatus(row).fraction != null ? row?.curation?.releaseDueAt?.getTime() : undefined;
    return timing.visible && deadline != null && (!release || timing.tone !== 'neutral' && deadline < release);
}
async function bulkFavorite(isFavorite: boolean) {
    await curationActions.favorite(isFavorite);
}
async function afterTrash(id: string, moved: boolean) {
    if (moved && galleryInspectionId.value === id) await closeGalleryInspection();
    await refresh(new Set([id]));
}
async function forgetUnavailableCapture(id: string) {
    captureBrowser.cancelRequests(); loading.value = false;
    captureBrowser.forget(id);
    curationVisit.forget(id);
    selection.value = selection.value.filter(value => value !== id);
    if (activeId.value === id) {
        const continuing = full.value && view.value === 'Gallery';
        if (!continuing) closeFull();
        if (continuing) invalidateSlide(id);
        else { detailSequence.value++; activeId.value = ''; capture.value = undefined; }
    } else playback.remove(id);
    if (paneAId.value === id) paneAId.value = '';
    if (secondId.value === id) { secondId.value = ''; compare.value = false; }
    if (galleryInspectionId.value === id) await closeGalleryInspection();
}
async function afterErasure(id: string) {
    previewVersions.value[id] = (previewVersions.value[id] ?? 0) + 1;
    await forgetUnavailableCapture(id);
    await refresh(new Set([id]));
}
async function bulkTrash() {
    await curationActions.trash();
}
function verifySingleTrash(id: string) {
    void curationActions.verifyTrashOutcomes(new Set([id]));
}
async function submitSingleTrash(id: string, expectedRevision: number) {
    if (!mayCurate.value) return;
    const ownerView = view.value;
    const ownerSurface = retentionSurface.value;
    const moved = await curationActions.submitTrash(id, expectedRevision);
    if (moved && view.value === ownerView && retentionSurface.value === ownerSurface)
        await afterTrash(id, true);
}
</script>

<template>
    <ApertureBackground :view="view === 'Settings' ? 'Delivery' : view" :motion-enabled="motionEnabled" :hidden="full" />
    <Teleport to=".app-header">
        <nav class="main-nav" aria-label="Workspace">
            <button
                v-for="item in availableViews"
                :key="item"
                :aria-current="view === item ? 'page' : undefined"
                @click="switchView(item)"
            >
                {{ item }}
            </button>
        </nav>
    </Teleport>
    <Teleport to="#workspace-header-actions"><button class="settings-access" :aria-pressed="view === 'Settings'" aria-label="Open Settings" title="Settings" @click="openSettings()"><UIcon name="i-lucide-settings" /></button></Teleport>
    <Teleport to="#brand-live-status">
        <span class="live-indicator" :class="{ connected: live.connected.value, offline: !live.online.value, pulsing: motionEnabled }" role="status" :aria-label="!live.online.value ? 'Offline' : live.connected.value ? 'Live updates connected' : 'Live updates reconnecting'" :title="!live.online.value ? 'Offline' : live.connected.value ? 'Live updates connected' : 'Live updates reconnecting'" />
    </Teleport>
    <main
        id="workspace"
        class="operational-workspace"
        @change="flushPreferencesOnInteractionEnd"
        @focusout="flushPreferencesOnInteractionEnd"
        :class="[{ full: workspacePreferences.width === 'full', 'reduce-effects': !motionEnabled, 'curation-viewport': view === 'Curation' && !expanded || view === 'Gallery' && !!galleryInspectionId }, `density-${appearance.density}`]"
        :style="{ '--frame-ratio': appearance.ratio, '--photo-fit': appearance.fit === 'fill' ? 'cover' : 'contain', '--tile-width': `${appearance.density}px` }"
    >
        <header class="view-heading">
            <div>
                <h1 tabindex="-1">
                    {{ view }}
                </h1>
                <p>
                    {{ view === 'Curation' ? 'Choose the photographs you want to send.' : view === 'Gallery' ? 'Your photographs, ready to browse.' : view === 'Sources' ? 'Connect your camera and bring in photographs.' : view === 'Settings' ? mayAdminister ? 'Make your workspace comfortable and manage shared storage.' : 'Make your workspace comfortable.' : 'See where your photographs are going.' }}
                </p>
            </div>
            <div class="live-controls">
                <button v-if="!live.connected.value" :disabled="loading || !live.online.value" aria-label="Refresh workspace" title="Refresh workspace" @click="live.refresh"><UIcon name="i-lucide-refresh-cw" /></button>
            </div>
        </header>
        <p v-if="!live.online.value" class="notice" role="status">
            Offline · displayed state may be stale. Edits stay here; reconnect and review before retrying.
        </p>
        <p v-if="policyStale" class="notice" role="status">
            Source policy unavailable or incomplete. Navigation retains its last known state.
        </p>
        <p v-if="live.unassignedRejection.value" class="notice" role="status">A camera authentication request was rejected without a verified Source identity.</p>
        <p v-if="error || (detailError && !(view === 'Curation' && !expanded && activeId) && !(view === 'Gallery' && galleryInspectionId))" class="notice error" role="alert">
            {{ error || detailError }}
        </p>
            <div v-if="view === 'Gallery' || view === 'Curation'" class="toolbar photo-toolbar">
                <details class="section-disclosure disclosure toolbar-disclosure" name="workspace-options">
                    <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />
                        Filters
                    </summary>
                    <div class="filter-content filter-categories">
                        <div class="filter-category"><span class="filter-title">Favorite</span><button :aria-pressed="filter.favoritesOnly" @click="filter.favoritesOnly = !filter.favoritesOnly"><UIcon name="i-lucide-heart" />Favorites only</button></div>
                        <div class="filter-category">
                            <span id="workflow-filter-title" class="filter-title">Workflow</span>
                            <div class="filter-group" role="group" aria-labelledby="workflow-filter-title">
                                <button :aria-pressed="!filter.workflows.length" @click="filter.workflows = []">All states</button>
                                <button v-for="workflow in workflows" :key="workflow.name" :aria-pressed="filter.workflows.includes(workflow.name)" @click="toggleFilter('workflows', workflow.name)"><UIcon :name="workflow.icon" />{{ workflow.name }}</button>
                            </div>
                        </div>
                        <div v-if="mayAdminister" class="filter-category">
                            <span id="source-filter-title" class="filter-title">Source</span>
                            <div class="filter-group" role="group" aria-labelledby="source-filter-title">
                                <button :aria-pressed="!filter.sourceIds.length" @click="filter.sourceIds = []">All sources</button>
                                <button v-for="source in sources" :key="String(source.id)" :aria-pressed="filter.sourceIds.includes(String(source.id))" :disabled="filter.sourceIds.length >= 30 && !filter.sourceIds.includes(String(source.id))" @click="toggleFilter('sourceIds', String(source.id))"><UIcon name="i-lucide-camera" />{{ source.name }}</button>
                            </div>
                        </div>
                        <div class="filter-category">
                            <span id="rating-filter-title" class="filter-title">Rating</span>
                            <CaptureRatingFilter :rating="filter.rating" :maximum="filter.ratingMaximum" @change="filter.rating = $event.rating; filter.ratingMaximum = $event.maximum" />
                        </div>
                        <div class="filter-category">
                            <span id="color-filter-title" class="filter-title">Color label</span>
                            <div class="filter-group color-filter" role="group" aria-labelledby="color-filter-title">
                                <button :aria-pressed="!filter.labels.length" @click="filter.labels = []">
                                    All colors
                                </button>
                                <button
                                    v-for="label in ['None', ...labels]"
                                    :key="label"
                                    :aria-label="label"
                                    :aria-pressed="filter.labels.includes(label)"
                                    @click="toggleLabel(label)"
                                >
                                    <span v-if="label !== 'None'" class="label-dot" :class="label.toLowerCase()" />
                                    <UIcon v-else name="i-lucide-circle-slash" />
                                    {{ label }}
                                </button>
                            </div>
                        </div>
                        <div class="filter-category">
                            <label for="capture-filter-tag" class="filter-title">Tag</label>
                            <input id="capture-filter-tag" v-model="filterTagDraft" maxlength="64" placeholder="Search tags" />
                        </div>
                        <div class="filter-category">
                            <span class="filter-title">Collection</span>
                            <CollectionPicker v-model="filter.collectionIds" multiple compact />
                        </div>
                        <div class="filter-clear"><button @click="clearFilters"><UIcon name="i-lucide-filter-x" />Clear</button></div>
                    </div>
                </details>
                <details class="section-disclosure disclosure toolbar-disclosure" name="workspace-options" @toggle="measureGrid">
                    <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Appearance</summary>
                    <div ref="appearanceOverlay" class="filter-content appearance-content" @pointerup="flushPreferencesOnInteractionEnd">
                        <p class="appearance-scope">{{ view }} only.</p>
                        <div class="appearance-fields">
                            <label v-if="!phone" class="photo-size">Desktop photo size<span class="photo-size-control"><UIcon name="i-lucide-grid-3x3" /><input v-model.number="appearance.density" type="range" min="100" max="520" step="10" /><UIcon name="i-lucide-square" /></span></label>
                            <label>Aspect ratio<select v-model="appearance.ratio"><option value="3 / 2">3:2</option><option value="4 / 3">4:3</option><option value="16 / 9">16:9</option><option value="1 / 1">1:1</option></select></label>
                            <label>Thumbnail fit<select v-model="appearance.fit"><option value="fill">Fill frame</option><option value="fit">Show whole photo</option></select></label>
                        </div>
                        <div class="appearance-fields">
                            <label class="appearance-check"><input v-model="appearance.details" type="checkbox" />Show details</label>
                            <label>Order<select v-model="appearance.sort" aria-label="Order"><option value="newest">Newest first</option><option value="oldest">Oldest first</option></select></label>
                        </div>
                        <div class="appearance-footer"><button @click="resetAppearance(); flushPreferencesOnInteractionEnd()">Reset {{ view }} appearance</button><button @click="openSettings()">Workspace settings<UIcon name="i-lucide-arrow-up-right" /></button></div>
                    </div>
                </details>
                <span class="spacer" />
                <div v-if="view === 'Gallery'" class="gallery-toolbar-actions">
                    <div v-if="mayCurate" class="retention-navigation" role="group" aria-label="Gallery views"><button :aria-pressed="retentionSurface === 'photos'" @click="retentionSurface = 'photos'"><UIcon name="i-lucide-grid-2x2" />Photos</button><button :aria-pressed="retentionSurface === 'trash'" @click="retentionSurface = 'trash'"><UIcon name="i-lucide-trash-2" />Trash</button></div>
                    <button class="gallery-fullscreen icon-button" :disabled="!rows.length" aria-label="Fullscreen photographs" title="Fullscreen photographs" @click="openGalleryFull"><UIcon name="i-lucide-maximize" /></button>
                </div>
                <template v-if="view === 'Curation'">
                    <button :aria-pressed="expanded" :aria-label="expanded ? 'Photo view' : 'Bulk grid'" :title="expanded ? 'Photo view' : 'Bulk grid'" @click="expanded = !expanded; selecting = expanded; selection = []">
                        <UIcon :name="expanded ? 'i-lucide-image' : 'i-lucide-grid-2x2'" />
                    </button>
                </template>
            </div>
        <p v-if="preferenceNotice" class="notice preference-notice" role="status">{{ preferenceNotice }}</p>
        <LazySourceWorkspace :grid-maximum-dimension="workspacePreferences.gridMaximumDimension" v-if="view === 'Sources'" :sources="sources" :activity="live.activity.value" :auth-activity="live.authActivity.value" :phase-activity="live.phaseActivity.value" :recovery-sequence="live.recoverySequence.value" :preview-versions="previewVersions" :connected="live.connected.value" :motion-enabled="motionEnabled" @refresh="refresh()" @dirty-change="dirtyEditors.Sources = $event" />
        <LazyDeliveryWorkspace
            :grid-maximum-dimension="workspacePreferences.gridMaximumDimension"
            v-else-if="view === 'Delivery'"
            :online="live.online.value"
            :connected="live.connected.value"
            :recovery-sequence="live.recoverySequence.value"
            :preview-versions="previewVersions"
            :generation="deliveryGeneration"
            :motion-enabled="motionEnabled"
            @dirty-change="dirtyEditors.Delivery = $event"
        />
        <LazyTrashWorkspace :grid-maximum-dimension="workspacePreferences.gridMaximumDimension" v-else-if="mayCurate && view === 'Gallery' && retentionSurface === 'trash'" :online="live.online.value" :generation="generation" @refresh="refresh()" @erased="afterErasure" @storage-settings="openSettings('Storage')" />
        <section v-else-if="view === 'Settings'" class="settings-home" aria-label="Settings">
            <nav class="settings-sections" aria-label="Settings sections">
                <button v-for="section in settingsSections" :key="section" :aria-current="settingsSection === section ? 'page' : undefined" @click="selectSettingsSection(section)"><UIcon :name="section === 'Workspace' ? 'i-lucide-panels-top-left' : section === 'Members' ? 'i-lucide-users' : 'i-lucide-hard-drive'" />{{ section }}</button>
            </nav>
            <div class="settings-content">
                <button class="settings-return" @click="returnFromSettings"><UIcon name="i-lucide-arrow-left" />Return to {{ settingsOrigin }}</button>
                <h2 id="settings-section-heading" tabindex="-1">{{ settingsSection }}</h2>
                <template v-if="settingsSection === 'Workspace'">
                    <p class="muted">For your account in this browser.</p>
                    <div class="workspace-preference-fields">
                        <label>Workspace width<select v-model="workspacePreferences.width"><option value="standard">Standard</option><option value="full">Full width</option></select></label>
                        <label>Motion<select v-model="workspacePreferences.motion"><option value="system">Follow device setting</option><option value="reduce">Reduced</option><option value="full">Full</option></select></label>
                    </div>
                    <details class="settings-advanced section-disclosure preview-quality"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Preview quality</summary>
                        <p class="muted">Larger previews can show more detail and use more data. Originals stay unchanged.</p>
                        <div class="workspace-preference-fields">
                            <label v-for="key in (['gridMaximumDimension', 'detailMaximumDimension'] as const)" :key="key" :for="`preference-${key}`">{{ key === 'gridMaximumDimension' ? 'Grid preview size' : 'Photo preview size' }}<span class="muted">Pixels · 64–4096</span><input :id="`preference-${key}`" v-model="previewDrafts[key]" type="number" min="64" max="4096" step="1" :aria-invalid="!!previewErrors[key]" :aria-describedby="previewErrors[key] ? `preference-${key}-error` : undefined" @blur="commitPreview(key)" @keydown.enter.prevent="commitPreview(key)" /><span v-if="previewErrors[key]" :id="`preference-${key}-error`" class="preference-error" role="status">{{ previewErrors[key] }}</span></label>
                        </div>
                    </details>
                    <WorkspaceLeave :online="live.online.value" /><div class="workspace-reset"><button @click="resetWorkspace(); flushPreferencesOnInteractionEnd()">Reset workspace preferences</button></div>
                </template>
                <div v-if="mayAdminister" v-show="settingsSection === 'Storage'">
                    <p class="muted">Shared settings for photograph storage and recovery.</p>
                    <LazyRetentionSettings v-if="storageVisited" :online="live.online.value" :generation="generation" @dirty="settingsDirty = $event" />
                </div>
                <WorkspaceMembers v-if="mayAdminister && settingsSection === 'Members'" :online="live.online.value" :generation="evidenceGeneration" />
            </div>
        </section>
        <template v-else>

            <div v-if="view === 'Gallery' && (selection.length || selecting)" class="bulk-bar" role="region" aria-label="Selected photographs">
                <strong>{{ selection.length }} selected · maximum 100</strong>
                <button :disabled="bulkBusy" @click="selectLoaded">Select all loaded</button>
                <button :disabled="bulkBusy" @click="selection = []; selecting = false">Select none</button>
                <span class="muted">Downloads are not available in this alpha.</span>
                <CollectionMove v-if="mayCurate" :ids="selection" :online="live.online.value" :disabled="bulkBusy || unresolved" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" @completed="selection = selection.filter(id => !$event.includes(id))" />
            </div>
            <p v-if="view === 'Gallery' && bulkResult" class="notice" role="status">{{ bulkResult }}</p>
            <p v-if="pendingTrashIds.length || uncertainTrashIds.length" class="notice error" role="status">
                <template v-if="pendingTrashIds.length">A Move to Trash request is still pending for {{ pendingTrashIds.length }} photograph{{ pendingTrashIds.length === 1 ? '' : 's' }}. Other controls stay locked until each request completes.</template>
                <template v-else>The result of a previous Move to Trash request is uncertain for {{ uncertainTrashIds.length }} photograph{{ uncertainTrashIds.length === 1 ? '' : 's' }}. Do not submit those photographs again until their current state is verified.</template>
                <button v-if="uncertainTrashIds.some(id => !pendingTrashIds.includes(id))" :disabled="trashVerificationBusy || !live.online.value" @click="() => curationActions.verifyTrashOutcomes()">Refresh and verify</button>
            </p>
            <p v-if="pageError" class="notice error" role="alert">
                {{ pageError }}
                <button @click="pageError = ''; loadPage(pageRetry.next, pageRetry.reconcile)">
                    Retry
                </button>
            </p>
            <div v-if="!rows.length && loading" class="empty" aria-busy="true">
                Loading photographs…
            </div>
            <div v-else-if="!rows.length" class="empty">
                <h2>
                    No photographs in this view
                </h2>
                <p>
                    {{ mayAdminister ? 'Change filters or connect a Source to send photographs.' : 'Change filters or check again when photographs are available to you.' }}
                </p>
            </div>
            <div v-if="view === 'Curation' && !expanded && activeId && rows.length" class="curation-layout">
                <div class="photo-workspace">
                    <div class="photo-stage" :class="{ comparing: compare }">
                        <button
                            class="photo-pane"
                            :class="{ active: !compare || activePane === 'A' }"
                            :aria-label="compare ? `Editing A · ${paneA?.displayName ?? 'Capture A'}` : capture?.displayName ?? 'Open Capture'"
                            @click="compare && paneA ? openCapture(String(paneA.id), false, 'A', true) : enterFull()"
                        >
                            <RetentionLifetime v-if="retentionWins(compare ? paneA : capture ?? paneA)" :evidence="(compare ? paneA : capture ?? paneA)?.retention" details :stale="!live.online.value" /><ReleaseLifetime v-else :curation="workflowCuration(compare ? paneA : capture ?? paneA)" />
                            <span v-if="compare" class="pane-name">
                                {{ activePane === 'A' ? 'Editing A' : 'A' }}
                            </span>
                            <AuthorizedPreview
                                :capture-id="compare ? paneAId : activeId"
                                profile="detail"
                                :name="compare ? paneA?.displayName ?? 'Capture A' : capture?.displayName ?? 'Capture'"
                                :maximum-dimension="workspacePreferences.detailMaximumDimension"
                                :force-generation="previewVersions[compare ? paneAId : activeId] ?? 0"
                            />
                        </button>
                        <button
                            v-if="compare && secondId"
                            class="photo-pane"
                            :class="{ active: activePane === 'B' }"
                            aria-label="Edit photo B"
                            @click="openCapture(secondId, false, 'B', true)"
                        >
                            <RetentionLifetime v-if="retentionWins(paneB)" :evidence="paneB?.retention" details :stale="!live.online.value" /><ReleaseLifetime v-else :curation="workflowCuration(paneB)" />
                            <span class="pane-name">
                                {{ activePane === 'B' ? 'Editing B' : 'B' }}
                            </span>
                            <AuthorizedPreview
                                :force-generation="previewVersions[secondId] ?? 0" :capture-id="secondId"
                                profile="detail"
                                name="Comparison B"
                                :maximum-dimension="workspacePreferences.detailMaximumDimension"
                            />
                        </button>
                    </div>
                    <div class="photo-actions">
                        <span>
                            {{ capture?.displayName ?? 'Loading photograph…' }}
                        </span>
                        <span v-if="capture || paneA" role="img" :aria-label="workflowStatus(capture ?? paneA).title" :title="workflowStatus(capture ?? paneA).title"><UIcon :name="workflowStatus(capture ?? paneA).icon" /></span>
                        <button aria-label="Compare photographs" title="Compare photographs" :aria-pressed="compare" @click="toggleCompare"><UIcon name="i-lucide-columns-2" /></button>
                        <button aria-label="Fullscreen" title="Fullscreen" @click="enterFull">
                            <UIcon name="i-lucide-maximize" />
                        </button>
                        <button aria-label="Previous photograph" title="Previous photograph" @click="navigate(-1)">
                            <UIcon name="i-lucide-chevron-left" />
                        </button>
                        <button aria-label="Next photograph" title="Next photograph" @click="navigate(1)">
                            <UIcon name="i-lucide-chevron-right" />
                        </button>
                    </div>
                    <CaptureFilmstrip
                        :items="filmItems" :active-id="activeId" :stale="!live.online.value"
                        :maximum-dimension="workspacePreferences.gridMaximumDimension" :preview-versions="previewVersions"
                        @open="openCapture($event.id, false, compare && $event.shift ? 'B' : undefined)"
                    />
                </div>
                <details class="section-disclosure inspector" :open="!phone || inspectorOpen" @toggle="phone && (inspectorOpen = ($event.target as HTMLDetailsElement).open)">
                    <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Choose this photograph</summary>
                    <template v-if="capture && String(capture.id) === activeId">
                        <h2>
                            {{ compare ? activePane === 'B' ? 'Editing B' : 'Editing A' : capture.displayName }}
                        </h2>
                        <p class="muted">
                            {{ capture.assets?.map(asset => asset.variant).join(' + ') }}
                            ·
                            {{ state(capture) }}
                        </p>
                        <p v-if="edit && edit.status !== 'Saved'" class="save-state" role="status">
                            {{ edit.status }}
                        </p>
                        <div v-if="edit && edit.status !== 'Saved' && edit.status !== 'Saving'" class="notice">
                            <p>
                                Server:
                                {{ edit.server.rating }}
                                ·
                                {{ edit.server.label ?? 'None' }}
                                ·
                                {{ edit.server.tags?.join(' · ') || 'No tags' }}
                            </p>
                            <p>
                                Your edits:
                                {{ edit.intent.rating }}
                                ·
                                {{ edit.intent.label ?? 'None' }}
                                ·
                                {{ edit.intent.tags.join(' · ') || 'No tags' }}
                            </p>
                            <button @click="edit.check">
                                Check outcome
                            </button>
                            <button @click="edit.useServer">
                                Use server values
                            </button>
                            <button :disabled="!!edit.server.releasedAt" @click="edit.reapply">
                                Reapply my edits
                            </button>
                        </div>

                        <CurationControls
                            :rating="edit?.intent.rating" :label="edit?.intent.label" :tags="edit?.intent.tags ?? []"
                            v-model:tag="tag" :suggestions="suggestions" :disabled="!canCurate"
                            @rating="edit?.change({ rating: $event })" @label="edit?.change({ label: $event })"
                            @update:tag="queueSuggestions" @add-tag="addTag($event)"
                            @remove-tag="edit?.change({ tags: edit.intent.tags.filter(tag => tag !== $event) })"
                            @reset="edit?.reset($event)"
                        >
                            <template #favorite><FavoriteControl v-if="mayCurate" compact :capture-id="activeId" :evidence="capture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" /></template>
                            <template #collection>
                                <CollectionMove v-if="mayCurate" :key="String(capture.id)" :ids="[String(capture.id)]" :online="live.online.value" :disabled="edit?.unresolved || bulkBusy || busyCapture" :current-name="capture.currentCollection?.path ?? capture.currentCollection?.name ?? undefined" :released="!!capture.curation?.releasedAt" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" />
                            </template>
                        </CurationControls>
                        <div class="actions">
                            <button
                                :disabled="edit?.unresolved || !!edit?.server.releasedAt || capture.retention?.isPaused || busyCapture || bulkBusy || !live.online.value"
                                :title="edit?.server.held ? 'Return to this source’s waiting and hold settings. Manual sources stay on hold until you release the photograph.' : undefined"
                                @click="captureAction(edit?.server.held ? 'restore' : 'hold')"
                            >
                                {{ edit?.server.held ? 'Use source settings' : 'Hold' }}
                            </button>
                            <button
                                class="primary"
                                :disabled="edit?.unresolved || !!edit?.server.releasedAt || capture.retention?.isPaused || busyCapture || bulkBusy || !live.online.value"
                                @click="captureAction('release')"
                            >
                                Release now
                            </button>
                        </div>
                        <CaptureResumeAction v-if="mayAdminister" :capture-id="activeId" :evidence="capture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                        <p v-if="edit?.server.releaseDueAt && !edit.server.releasedAt && !edit.server.held" class="muted">
                            {{ workflowStatus(capture).title }}
                        </p>
                        <details class="capture-evidence section-disclosure" @toggle="curationEvidenceOpen = ($event.target as HTMLDetailsElement).open">
                            <summary>
                                <UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details
                            </summary>
                            <CaptureEvidence :capture="capture" :generation="evidenceGeneration" :online="live.online.value" :active="curationEvidenceOpen" @refresh="refresh(new Set([$event]))" />
                        </details>
                        <details class="photograph-storage section-disclosure">
                            <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Storage and recovery</summary>
                            <RetentionDetails :evidence="capture.retention" :stale="!live.online.value" expanded />
                            <NoDeliveryIntentAction v-if="mayAdminister" :capture="capture" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                        <CaptureTrashAction v-if="mayCurate" :capture-id="activeId" :name="capture.displayName ?? 'Photograph'" :evidence="capture.retention" :disabled="!live.online.value || bulkBusy" :uncertain="uncertainTrashIds.includes(activeId)" :pending="pendingTrashIds.includes(activeId)" :verifying="trashVerificationBusy" :verification-message="trashVerificationMessage" @submit="submitSingleTrash" @verify="verifySingleTrash" @refresh="afterTrash" />
                            <PrivacyErasurePanel v-if="mayAdminister" :capture-id="activeId" :display-name="capture.displayName ?? 'Photograph'" :evidence="capture.retention" :online="live.online.value" :generation="evidenceGeneration" @requested="afterErasure" />
                        </details>
                    </template>
                    <div v-else role="status" :aria-busy="!detailError">
                        <p>{{ detailError || 'Loading photograph details…' }}</p>
                        <button v-if="detailError" @click="openCapture(activeId, false, undefined, true)">
                            Retry details
                        </button>
                    </div>
                </details>
            </div>
            <div v-else class="gallery-surface" :class="{ 'curation-layout bulk-layout': view === 'Curation' && expanded }">
                <section v-if="galleryInspectionId && view === 'Gallery'" class="curation-layout gallery-inspection" aria-label="Photograph inspection">
                    <div class="photo-workspace">
                        <div class="photo-stage">
                            <button class="photo-pane" :aria-label="'Fullscreen ' + (inspectionCapture?.displayName ?? 'photograph')" @click="enterFull">
                                <RetentionLifetime :evidence="inspectionCapture?.retention" details :stale="!live.online.value" />
                                <AuthorizedPreview :force-generation="previewVersions[galleryInspectionId] ?? 0" :capture-id="galleryInspectionId" profile="detail" :name="inspectionCapture?.displayName ?? 'Capture'" :maximum-dimension="workspacePreferences.detailMaximumDimension" />
                            </button>
                        </div>
                        <div class="photo-actions">
                            <span>{{ inspectionCapture?.displayName ?? 'Loading photograph…' }}</span>
                            <button aria-label="Fullscreen" title="Fullscreen" @click="enterFull">
                                <UIcon name="i-lucide-maximize" />
                            </button>
                            <button aria-label="Previous photograph" title="Previous photograph" @click="navigate(-1)">
                                <UIcon name="i-lucide-chevron-left" />
                            </button>
                            <button aria-label="Next photograph" title="Next photograph" @click="navigate(1)">
                                <UIcon name="i-lucide-chevron-right" />
                            </button>
                            <button aria-label="Return to Gallery grid" title="Return to Gallery grid" @click="closeGalleryInspection">
                                <UIcon name="i-lucide-grid-2x2" />
                            </button>
                        </div>
                    </div>
                    <details class="section-disclosure inspector" :open="!phone || galleryInspectionOpen" @toggle="phone && (galleryInspectionOpen = ($event.target as HTMLDetailsElement).open)">
                        <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />{{ inspectionMode === 'delivery' ? 'Delivery' : 'About this photograph' }}</summary>
                        <h2>{{ inspectionCapture?.displayName ?? 'Photograph details' }}</h2>
                        <template v-if="inspectionCapture">
                            <div class="photograph-markers"><CaptureRating :rating="inspectionCapture.curation?.rating" :label="inspectionCapture.curation?.label" /><FavoriteControl v-if="mayCurate" compact :capture-id="galleryInspectionId" :evidence="inspectionCapture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" /></div>
                            <p class="muted">{{ inspectionCapture.assets?.map(asset => asset.variant).join(' + ') }} · {{ cameraDescription(inspectionCapture) }}</p>
                            <p v-if="inspectionCapture.curation?.tags?.length" class="muted">{{ inspectionCapture.curation.tags.join(' · ') }}</p>
                            <CaptureResumeAction v-if="mayAdminister" :capture-id="galleryInspectionId" :evidence="inspectionCapture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                            <CollectionMove v-if="mayCurate && inspectionMode !== 'delivery'" :key="String(inspectionCapture.id)" :ids="[String(inspectionCapture.id)]" :online="live.online.value" :disabled="bulkBusy" :current-name="inspectionCapture.currentCollection?.path ?? inspectionCapture.currentCollection?.name ?? undefined" :released="!!inspectionCapture.curation?.releasedAt" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" />
                        <details class="capture-evidence section-disclosure" :open="galleryEvidenceOpen" @toggle="galleryEvidenceOpen = ($event.target as HTMLDetailsElement).open"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />{{ inspectionMode === 'delivery' ? 'Delivery details' : 'Details' }}</summary><CaptureEvidence :capture="inspectionCapture" :generation="evidenceGeneration" :online="live.online.value" :active="galleryEvidenceOpen" :read-only="inspectionMode !== 'delivery'" :delivery-only="inspectionMode === 'delivery'" :focus-state="inspectionDeliveryState" @refresh="refresh(new Set([$event]))" /></details>
                            <details v-if="mayCurate" class="photograph-storage section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Storage and recovery</summary>
                                <RetentionDetails :evidence="inspectionCapture.retention" :stale="!live.online.value" expanded />
                                <NoDeliveryIntentAction v-if="mayAdminister" :capture="inspectionCapture" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                                <CaptureTrashAction v-if="mayCurate" :capture-id="galleryInspectionId" :name="inspectionCapture.displayName ?? 'Photograph'" :evidence="inspectionCapture.retention" :disabled="!live.online.value || bulkBusy" :uncertain="uncertainTrashIds.includes(galleryInspectionId)" :pending="pendingTrashIds.includes(galleryInspectionId)" :verifying="trashVerificationBusy" :verification-message="trashVerificationMessage" @submit="submitSingleTrash" @verify="verifySingleTrash" @refresh="afterTrash" />
                                <PrivacyErasurePanel v-if="mayAdminister" :capture-id="galleryInspectionId" :display-name="inspectionCapture.displayName ?? 'Photograph'" :evidence="inspectionCapture.retention" :online="live.online.value" :generation="evidenceGeneration" @requested="afterErasure" />
                            </details>
                        </template>
                        <div v-else role="status">
                            <p>{{ detailError || 'Loading photograph details…' }}</p>
                            <button v-if="detailError" @click="inspectCapture(galleryInspectionId, inspectionDeliveryState, inspectionMode)">
                                Retry details
                            </button>
                        </div>
                    </details>
                </section>
                <div v-show="!galleryInspectionId" ref="gridElement" class="photo-grid" tabindex="-1">
                    <div
                        v-if="renderStart"
                        class="grid-spacer"
                        :style="{ height: `${Math.floor(renderStart / columns) * tileHeight}px` }"
                    />
                    <CaptureGridPhoto
                        v-for="row in virtualRows"
                        :key="String(row.id)"
                        :capture="row"
                        :selected="selection.includes(String(row.id))"
                        :gallery="view === 'Gallery'"
                        :retention-preferred="retentionWins(row)"
                        :workflow="workflowCuration(row)"
                        :details="appearance.details"
                        :stale="!live.online.value"
                        :maximum-dimension="workspacePreferences.gridMaximumDimension"
                        :preview-generation="previewVersions[String(row.id)] ?? 0"
                        :camera="cameraDescription(row)"
                        @start-hold="startHold"
                        @cancel-hold="cancelHold"
                        @end-hold="endHold"
                        @open="tileClick"
                    >
                        <template #actions="{ capture: row }">
                        <FavoriteControl v-if="mayCurate" class="photo-favorite-action" :class="{ 'details-visible': appearance.details }" compact :capture-id="String(row.id)" :evidence="row.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                        <button v-if="appearance.details && view === 'Gallery'" class="tile-delivery-action" :aria-label="deliveryStatus(row).title" :title="deliveryStatus(row).title" @click="inspectDelivery(row)"><UIcon :name="deliveryStatus(row).icon" /></button>
                        <span v-if="view === 'Curation'" class="tile-workflow-state" :class="{ 'without-caption': !appearance.details }" role="img" :aria-label="workflowStatus(row).title" :title="workflowStatus(row).title"><UIcon :name="workflowStatus(row).icon" /></span>
                        <label v-if="mayCurate && (selecting || view === 'Curation' && expanded)" class="tile-selection">
                            <input
                                type="checkbox"
                                :checked="selection.includes(String(row.id))"
                                @change="toggle(String(row.id))"
                                :aria-label="`Select ${row.displayName}`"
                            />
                        </label>
                        <button
                            v-else-if="mayCurate"
                            class="photo-select-action"
                            :aria-label="`Select ${row.displayName}`"
                            @click="selecting = true; toggle(String(row.id))"
                        >
                            <UIcon name="i-lucide-check" />
                        </button>
                        </template>
                    </CaptureGridPhoto>
                    <div
                        v-if="renderEnd < rows.length"
                        class="grid-spacer"
                        :style="{ height: `${Math.ceil((rows.length - renderEnd) / columns) * tileHeight}px` }"
                    />
                </div>

                <aside v-if="view === 'Curation' && expanded" class="inspector bulk-inspector" aria-label="Selected photograph curation">
                    <h2>{{ selection.length }} selected</h2>
                    <div class="actions">
                        <button :disabled="bulkBusy" @click="selectLoaded">Select all loaded</button>
                        <button :disabled="bulkBusy" @click="selection = []">Select none</button>
                    </div>
                    <div class="actions"><button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulkFavorite(true)">Mark Favorite</button><button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulkFavorite(false)">Remove Favorite</button><button :disabled="bulkBusy || bulkTrashUncertain || !selection.length || !live.online.value" @click="bulkTrash">Move selected to Trash</button></div>
                    <CurationControls
                        :rating="bulkValues.rating" :label="bulkValues.label" :tags="bulkValues.tags" :mixed="bulkValues.mixed"
                        v-model:tag="bulkTag" :disabled="!selection.length || !live.online.value" :busy="bulkBusy"
                        @rating="bulkRating = $event; bulk('rating')" @label="bulkLabel = $event; bulk('label')"
                        @add-tag="bulk('tag')" @remove-tag="bulkRemovedTag = $event; bulk('removeTag')"
                        @reset="bulk($event ? 'reset' : 'discard')"
                    >
                        <template #collection>
                            <CollectionMove v-if="mayCurate" :ids="selection" :online="live.online.value" :disabled="bulkBusy" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" @completed="selection = selection.filter(id => !$event.includes(id))" />
                        </template>
                    </CurationControls>
                    <div class="actions">
                        <button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulk('hold')">Hold</button>
                        <button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulk('restore')">Restore policy</button>
                        <button class="primary" :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulk('release')">Release now</button>
                    </div>
                    <p v-if="bulkResult" class="notice error" role="alert">{{ bulkResult }}</p>
                    <p v-if="pendingTrashIds.length || uncertainTrashIds.length" class="notice error" role="status">
                        <template v-if="pendingTrashIds.length">A Move to Trash request is still pending for {{ pendingTrashIds.length }} photograph{{ pendingTrashIds.length === 1 ? '' : 's' }}. Other controls stay locked until each request completes.</template>
                        <template v-else>A previous Move to Trash outcome is uncertain for {{ uncertainTrashIds.length }} photograph{{ uncertainTrashIds.length === 1 ? '' : 's' }}. Do not submit them again until their state is verified.</template>
                        <button v-if="uncertainTrashIds.some(id => !pendingTrashIds.includes(id))" :disabled="trashVerificationBusy || !live.online.value" @click="() => curationActions.verifyTrashOutcomes()">Refresh and verify</button>
                    </p>
                </aside>
            </div>
            <div v-show="!galleryInspectionId" ref="sentinel" class="scroll-boundary" :class="{ 'focused-paging': view === 'Curation' && !expanded }" role="status">
                <span v-if="loading">
                    Loading next photographs…
                </span>

            </div>
        </template>
    </main>
    <nav class="phone-nav" aria-label="Workspace">
        <button
            v-for="item in availableViews"
            :key="item"
            :aria-current="view === item ? 'page' : undefined"
            @click="switchView(item)"
        >
            <UIcon
                :name="item === 'Sources' ? 'i-lucide-camera' : item === 'Curation' ? 'i-lucide-image' : item === 'Gallery' ? 'i-lucide-grid-2x2' : 'i-lucide-send'"
            />
            {{ item }}
        </button>
    </nav>
    <Teleport to="body">
        <section
            v-if="full"
            ref="fullElement"
            class="fullscreen-viewer"
            :class="{ 'reduce-effects': !motionEnabled, 'gallery-viewer': view === 'Gallery' }"
            role="dialog"
            aria-modal="true"
            aria-label="Photograph viewer"
            aria-describedby="viewer-keys"
            tabindex="-1"
            @pointermove="chrome.pointer()"
            @pointerdown.capture="viewerPointerDown"
            @click.capture="viewerClick"
            @focusin="viewerFocus"
            @focusout="chrome.focus(false)"
        >
            <p id="viewer-keys" class="sr-only">
                Left and Right browse. In Curation, 0 clears the rating, 1–5 rate, R rejects, plus and minus zoom, Home fits, and arrows pan while zoomed. Escape closes.
            </p>
            <div
                class="zoom-surface"
                @wheel="wheel"
                @pointerdown="pointerDown"
                @pointermove="pointerMove"
                @pointerup="pointerUp"
                @pointercancel="pointerUp"
            >
                <RetentionLifetime v-if="view === 'Gallery' || retentionWins(capture)" :evidence="capture?.retention" :details="view === 'Curation' || appearance.details" :stale="!live.online.value" /><ReleaseLifetime v-else :curation="workflowCuration(capture)" />
                <AuthorizedPreview v-if="activeId"
                    :key="activeId" :force-generation="previewVersions[activeId] ?? 0" :capture-id="activeId"
                    profile="detail"
                    :name="capture?.displayName ?? 'Capture'"
                    :maximum-dimension="workspacePreferences.detailMaximumDimension"
                    @display="slideshowDisplayed"
                    :style="{ transform: `translate(${offset.x}px, ${offset.y}px) scale(${zoom})` }"
                />
            </div>
            <div class="viewer-chrome" :inert="!viewerChrome.visible" :aria-hidden="!viewerChrome.visible">
                <button class="viewer-close" aria-label="Close viewer" @click="closeFull()">
                    <UIcon name="i-lucide-x" />
                </button>
                <button class="viewer-previous" aria-label="Previous photograph" @click="navigate(-1)">
                    <UIcon name="i-lucide-chevron-left" />
                </button>
                <button class="viewer-next" aria-label="Next photograph" @click="navigate(1)">
                    <UIcon name="i-lucide-chevron-right" />
                </button>
                <div v-if="view === 'Curation' && edit && edit.status !== 'Saved' && edit.status !== 'Saving'" class="viewer-save-state" role="status">
                    {{ edit.status }}
                    <template v-if="edit.status !== 'Saved' && edit.status !== 'Saving'">
                        <span>
                            Server {{ edit.server.rating }} / {{ edit.server.label ?? 'None' }} /
                            {{ edit.server.tags?.join(' · ') || 'No tags' }};
                            your edits {{ edit.intent.rating }} / {{ edit.intent.label ?? 'None' }} /
                            {{ edit.intent.tags.join(' · ') || 'No tags' }}
                        </span>
                        <button :disabled="!live.online.value" @click="edit.check">Check outcome</button>
                        <button @click="edit.useServer">Use server values</button>
                        <button :disabled="!live.online.value || !!edit.server.releasedAt" @click="edit.reapply">Reapply my edits</button>
                    </template>
                </div>
                <div v-if="view === 'Gallery'" class="viewer-favorite"><FavoriteControl v-if="mayCurate" compact :capture-id="activeId" :evidence="capture?.retention" :disabled="!live.online.value" @feedback="viewerFavoriteFeedback = $event" @refresh="refresh(new Set([$event]))" /></div>
                <div v-if="view === 'Gallery'" class="viewer-playback">
                    <div v-if="slideshow.message || slideshow.photographsOnly" class="slideshow-message" role="status">
                        <span>{{ slideshow.message || 'Slideshow shows photographs only.' }}</span>
                        <button v-if="slideshow.retry" :disabled="!live.online.value" @click="retrySlideshow">Retry</button>
                    </div>
                    <div class="slideshow-transport" role="group" aria-label="Slideshow controls">
                        <button class="slideshow-play" :disabled="!slideshow.playing && (!slideshowReady || !live.online.value)" :aria-label="slideshow.playing ? 'Pause slideshow' : 'Play slideshow'" :aria-pressed="slideshow.playing" @click="toggleSlideshow">
                            <UIcon :name="slideshow.playing ? 'i-lucide-pause' : 'i-lucide-play'" />
                            <span>{{ slideshow.playing ? 'Pause' : 'Play' }}</span>
                        </button>
                        <div class="slideshow-intervals" role="group" aria-label="Time per photograph">
                            <button v-for="seconds in [3, 5, 10]" :key="seconds" :aria-label="`${seconds} seconds per photograph`" :aria-pressed="slideshow.seconds === seconds" @click="playback.interval(seconds)">{{ seconds }}<span aria-hidden="true">s</span></button>
                        </div>
                    </div>
                    <span v-if="slideshow.playing && (slideshow.loading || slideshowPreview !== 'ready')" class="slideshow-waiting">{{ slideshow.loading ? 'Finding the next photograph…' : 'Waiting for the preview…' }}</span>
                </div>
                <div v-if="view === 'Curation'" class="viewer-curation">
                    <div class="viewer-rating-group" role="group" aria-label="Rating">
                    <button aria-label="Rejected" title="Rejected (R)" aria-keyshortcuts="R" :aria-pressed="edit?.intent.rating === -1" :disabled="!canCurate" @click="edit?.change({ rating: -1 })">
                        <UIcon name="i-lucide-star-off" />
                    </button>
                    <button aria-label="Not Rated" :aria-pressed="edit?.intent.rating === 0" :disabled="!canCurate" @click="edit?.change({ rating: 0 })">
                        <UIcon name="i-lucide-circle-minus" />
                    </button>
                    <button
                        v-for="n in 5"
                        :key="n"
                        :aria-label="`${n} stars`"
                        :aria-pressed="(edit?.intent.rating ?? 0) >= n"
                        :disabled="!canCurate"
                        @click="edit?.change({ rating: n })"
                    >
                        <RatingStar />
                    </button>
                    </div>
                    <FavoriteControl v-if="mayCurate" compact :capture-id="activeId" :evidence="capture?.retention" :disabled="!live.online.value" @feedback="viewerFavoriteFeedback = $event" @refresh="refresh(new Set([$event]))" />
                    <div class="viewer-label-group" role="group" aria-label="Color label">
                    <button aria-label="None" :aria-pressed="edit?.intent.label === null" :disabled="!canCurate" @click="edit?.change({ label: null })">
                        <UIcon name="i-lucide-circle-slash" />
                    </button>
                    <button
                        v-for="label in labels"
                        :key="label"
                        :aria-label="label"
                        :aria-pressed="edit?.intent.label === label"
                        :disabled="!canCurate"
                        @click="edit?.change({ label })"
                    >
                        <span class="label-dot" :class="label.toLowerCase()" />
                    </button>
                    </div>
                </div>
            </div>
        </section>
    </Teleport>
</template>
