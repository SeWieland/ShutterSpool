<script setup lang="ts">
import { workspaceDefaults, appearanceDefaults, defaultPreferences, decodePreferences, previewDimension } from '../lib/workspace-preferences';
import { retentionTiming, originalRemovalScope } from '../lib/retention';
import { GallerySlideshow, slideshowState } from '../lib/gallery-slideshow';
import type { DisplayState } from '../lib/gallery-slideshow';
import { FullscreenChrome, chromeState } from '../lib/fullscreen-chrome';
import { api, handleFailure, cancellable } from '../lib/api';
import { session, onAccessLost, accessGeneration } from '../lib/session';
import { useReconciliation } from '../lib/reconcile';
import { CurationSession, values } from '../lib/curation-session';
import { ratingInterval, zoomAround, clampPan, effectiveMotion, reconcileCaptures, curationTiming } from '../lib/workspace-interactions';
import type { CaptureInboxItemResponse as Capture, CaptureDetailResponse, CaptureCurationResponse, SourceConfigurationResponse } from '../api/models';
const props = defineProps<{
    captureId?: string;
}>();
const router = useRouter();
type View = 'Sources' | 'Curation' | 'Gallery' | 'Delivery' | 'Settings';
const view = ref<View>('Gallery');
const settingsDirty = ref(false);
const settingsSection = ref<'Workspace' | 'Storage'>('Workspace');
const storageVisited = ref(false);
const settingsOrigin = ref<View>('Gallery');
const settingsOriginSurface = ref<'photos' | 'trash'>('photos');
let settingsReturnFocus: HTMLElement | undefined;
let settingsReturnCapture = '';

const retentionSurface = ref<'photos' | 'trash'>('photos');
const initialized = ref(false);
const policyStale = ref(false);
const sources = ref<SourceConfigurationResponse[]>([]);
const rows = ref<Capture[]>([]);
const capture = ref<CaptureDetailResponse>();
const activeId = ref(props.captureId ?? '');
const paneAId = ref(props.captureId ?? '');
const secondId = ref('');
const activePane = ref<'A' | 'B'>('A');
const compare = ref(false);
const expanded = ref(false);
const selecting = ref(false);
const selection = ref<string[]>([]);
let orderSelection: string[] | undefined;
const error = ref('');
const detailError = ref('');
const loading = ref(false);
const pageError = ref('');
const pageRetry = reactive({ next: false, reconcile: false });
const cursor = ref<string | null>();
const pageAfterId = ref('');
const generation = ref(0);
const previewVersions = ref<Record<string, number>>({});
const full = ref(false);
const viewerChrome = reactive(chromeState());
const chrome = new FullscreenChrome(viewerChrome);
const viewerFavoriteFeedback = ref(false);
let suppressViewerClick = false;
function viewerPointerDown() {
    suppressViewerClick = !viewerChrome.visible;
    chrome.pointer(true);
}
function viewerClick(event: MouseEvent) {
    if (!suppressViewerClick) return;
    suppressViewerClick = false;
    event.preventDefault();
    event.stopPropagation();
}
function viewerFocus(event: FocusEvent) {
    chrome.focus(!!(event.target as HTMLElement)?.closest('.viewer-chrome'));
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
        void router.replace(`/captures/${encodeURIComponent(id)}`);
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
const zoom = ref(1);
const offset = reactive({ x: 0, y: 0 });
const fullElement = ref<HTMLElement>();
const galleryInspectionId = ref('');
const galleryInspectionOpen = ref(false);
const inspectionDeliveryState = ref('');
const inspectionMode = ref<'details' | 'delivery'>('details');
const inspectionCapture = computed(() => String(capture.value?.id) === galleryInspectionId.value && activeId.value === galleryInspectionId.value ? capture.value : undefined);
let inspectionScroll = 0;
let inspectionFocus: HTMLElement | undefined;
let inspectionReturnId = '';
const sentinel = ref<HTMLElement>();
const gridElement = ref<HTMLElement>();
const appearanceOverlay = ref<HTMLElement>();
const editSessions = reactive(new Map<string, CurationSession>());
const tagDrafts = reactive(new Map<string, string>());
const edit = computed(() => String(capture.value?.id ?? '') === activeId.value ? editSessions.get(activeId.value) : undefined);
const viewerRecovery = computed(() => viewerFavoriteFeedback.value || slideshow.retry || !!slideshow.message || view.value === 'Curation' && !!edit.value && !['Saved', 'Saving'].includes(edit.value.status));
watch(viewerRecovery, pinned => chrome.pin(pinned), { flush: 'sync' });
const canCurate = computed(() => !bulkBusy.value && !!edit.value?.editable && live.online.value && !['Conflict', 'Save failed', 'Checking outcome'].includes(edit.value.status));
const phone = ref(false);
const inspectorOpen = ref(false);
const tag = computed({ get: () => tagDrafts.get(activeId.value) ?? '', set: value => tagDrafts.set(activeId.value, value) });
const suggestions = ref<string[]>([]);
const bulkRating = ref(0);
const bulkLabel = ref<string | null>(null);
const bulkTag = ref('');
const bulkBusy = ref(false);
let photoVisit = 0;
const bulkResult = ref('');
const bulkRemovedTag = ref('');
const selectedValues = computed(() => selection.value.map(id => editSessions.get(id)?.intent ?? values(rows.value.find(row => String(row.id) === id)?.curation ?? {})));
const bulkValues = computed(() => {
    const items = selectedValues.value;
    return {
        rating: items.length && items.every(item => item.rating === items[0]!.rating) ? items[0]!.rating : undefined,
        label: items.length && items.every(item => item.label === items[0]!.label) ? items[0]!.label : undefined,
        tags: items[0]?.tags.filter(tag => items.every(item => item.tags.includes(tag))) ?? [],
        mixed: items.length > 1 && items.some(item => JSON.stringify(item) !== JSON.stringify(items[0])),
    };
});
const busyCapture = ref(false);
const detailSequence = ref(0);
let detailController: AbortController | undefined;
let pageSequence = 0;
let suggestionSequence = 0;
let observer: IntersectionObserver | undefined;
let focusBeforeViewer: HTMLElement | null = null;
const visible = ref(true);
const systemReduced = ref(false);
let motionQuery: MediaQueryList | undefined;
const now = ref(Date.now());
let countdown: ReturnType<typeof setInterval> | undefined;
let tagSearch: ReturnType<typeof setTimeout> | undefined;
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
const browsing = { Curation: { rows: [] as Capture[], cursor: null as string | null | undefined, afterId: '' }, Gallery: { rows: [] as Capture[], cursor: null as string | null | undefined, afterId: '' } };
const renderStart = ref(0);
const renderEnd = ref(90);
const tileHeight = ref(220);
const columns = ref(4);
const virtualRows = computed(() => rows.value.slice(renderStart.value, renderEnd.value));
const filmRows = computed(() => {
    const active = rows.value.findIndex(row => String(row.id) === activeId.value);
    return rows.value.slice(Math.max(0, active - 60), Math.max(120, active + 60));
});
function measureGrid() {
    phone.value = window.innerWidth <= 760;
    const overlay = appearanceOverlay.value;
    if (overlay?.closest('details')?.open) {
        const bottom = phone.value ? document.querySelector<HTMLElement>('.phone-nav')?.getBoundingClientRect().top ?? window.innerHeight : window.innerHeight;
        overlay.style.maxHeight = `${Math.max(120, bottom - overlay.getBoundingClientRect().top - 16)}px`;
    }
    if (galleryInspectionId.value)
        return;
    const grid = gridElement.value;
    if (!grid)
        return;
    columns.value = getComputedStyle(grid).gridTemplateColumns.split(' ').length;
    const tile = grid.querySelector('.gallery-tile');
    if (tile)
        tileHeight.value = tile.getBoundingClientRect().height + parseFloat(getComputedStyle(grid).rowGap || '0');
    const passed = Math.max(0, -grid.getBoundingClientRect().top);
    renderStart.value = Math.max(0, Math.floor(passed / tileHeight.value) - 3) * columns.value;
    renderEnd.value = Math.min(rows.value.length, renderStart.value + Math.min(180, (Math.ceil(window.innerHeight / tileHeight.value) + 8) * columns.value));
}
const preferences = reactive(defaultPreferences());
const workspacePreferences = preferences.workspace;
const appearances = preferences.appearances;
const appearance = computed(() => appearances[view.value === 'Curation' ? 'Curation' : 'Gallery']);
const motionEnabled = computed(() => effectiveMotion(workspacePreferences.motion, systemReduced.value, visible.value, live.online.value && live.connected.value));
const filters = preferences.filters;
const preferenceNotice = ref('');
let hydratedIdentity = '';
let preferencesWritable = true;
let hydratingPreferences = false;
const previewDrafts = reactive({ gridMaximumDimension: '512', detailMaximumDimension: '2048' });
const previewErrors = reactive({ gridMaximumDimension: '', detailMaximumDimension: '' });
function commitPreview(key: keyof typeof previewDrafts) {
    const value = previewDimension(previewDrafts[key]);
    previewErrors[key] = value === undefined ? 'Enter a whole number from 64 to 4096 pixels.' : '';
    if (value !== undefined) workspacePreferences[key] = value;
}
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
const availableViews = computed(() => ['Sources', ...(hasCuration.value || view.value === 'Curation' ? ['Curation'] : []), 'Gallery', 'Delivery'] as View[]);
const unresolved = computed(() => bulkBusy.value || [...editSessions.values()].some(value => value.unresolved));
const preferenceKey = computed(() => session.value?.profile.iss && session.value?.profile.sub
    ? `shutterspool-appearance-${session.value.profile.iss}-${session.value.profile.sub}` : '');
function savePreferences() {
    if (hydratingPreferences || !preferenceKey.value || hydratedIdentity !== preferenceKey.value || !preferencesWritable) return;
    try {
        localStorage.setItem(hydratedIdentity, JSON.stringify(preferences));
        preferenceNotice.value = '';
    } catch { preferenceNotice.value = "Changes apply for this visit. This browser couldn't save them."; }
}
function hydratePreferences() {
    hydratingPreferences = true;
    hydratedIdentity = '';
    let loaded = { value: defaultPreferences(), writable: true, notice: '' };
    if (preferenceKey.value) {
        try { loaded = decodePreferences(localStorage.getItem(preferenceKey.value)); }
        catch { loaded.notice = "Changes apply for this visit. This browser couldn't save them."; }
    }
    Object.assign(workspacePreferences, loaded.value.workspace);
    for (const view of ['Gallery', 'Curation'] as const) {
        Object.assign(appearances[view], loaded.value.appearances[view]); Object.assign(filters[view], loaded.value.filters[view]);
    }
    preferencesWritable = loaded.writable;
    preferenceNotice.value = loaded.notice;
    hydratedIdentity = preferenceKey.value;
    for (const key of ['gridMaximumDimension', 'detailMaximumDimension'] as const) {
        previewDrafts[key] = String(workspacePreferences[key]); previewErrors[key] = '';
    }
    hydratingPreferences = false;
}
watch(preferenceKey, hydratePreferences, { flush: 'sync' });
onMounted(() => {
    hydratePreferences();
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
    window.addEventListener('beforeunload', beforeUnload);
    document.addEventListener('keydown', keyboard);
    document.addEventListener('fullscreenchange', fullscreenChanged);
    window.addEventListener('scroll', measureGrid, { passive: true });
    window.addEventListener('resize', measureGrid);
    motionQuery = matchMedia('(prefers-reduced-motion: reduce)');
    motionChanged();
    visibilityChanged();
    motionQuery.addEventListener('change', motionChanged);
    document.addEventListener('visibilitychange', visibilityChanged);
    countdown = setInterval(() => { if (visible.value) now.value = Date.now(); }, 1000);
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
watch(preferences, savePreferences, { deep: true, flush: 'sync' });
watch(selection, () => { orderSelection = undefined; }, { deep: true, flush: 'sync' });
for (const name of ['Gallery', 'Curation'] as const) {
    watch(() => appearances[name].sort, () => {
        if (hydratingPreferences) return;
        if (name === 'Gallery') endSlideshow(false);
        browsing[name] = { rows: [], cursor: null, afterId: '' };
        scrollPositions[name] = 0;
        if (view.value !== name) return;
        const retainedSelection = orderSelection ?? [...selection.value];
        rows.value = [];
        selection.value = [];
        orderSelection = retainedSelection;
        cursor.value = null;
        pageAfterId.value = '';
        pageSequence++;
        if (initialized.value) void loadPage(false);
    }, { flush: 'sync' });
    watch(filters[name], () => {
        if (name === 'Gallery') endSlideshow(false);
        if (!hydratingPreferences && initialized.value && view.value === name) void loadPage(false);
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
    return { favoritesOnly: f.favoritesOnly || undefined, limit: 30, cursor: next ? cursor.value ?? undefined : undefined, sourceIds: f.sourceIds.length ? [...f.sourceIds] : undefined, collectionIds: f.collectionIds.length ? [...f.collectionIds] : undefined, rating: f.rating ?? undefined, ratingMaximum: f.ratingMaximum ?? undefined, labels: f.labels.length ? [...f.labels] : undefined, tag: f.tag || undefined, workflows: f.workflows.length ? [...f.workflows] : undefined, oldestFirst: appearance.value.sort === 'oldest' };
}
async function revalidateBoundary(incoming: Capture[], request: number, access: number) {
    const retained: Capture[] = [];
    const freshIds = new Set(incoming.map(row => String(row.id)));
    const boundaryTime = incoming.at(-1)?.acceptedAt?.getTime();
    for (const cached of rows.value.filter(matchesFilter)) {
        let row = cached;
        if (!freshIds.has(String(row.id)) && boundaryTime != null && row.acceptedAt?.getTime() === boundaryTime) {
            try {
                const current = await api.management.captures.byId(String(row.id)).get();
                if (request !== pageSequence || access !== accessGeneration()) return retained;
                if (current) {
                    row = { ...row, ...current };
                    if (current.curation) editSessions.get(String(row.id))?.observe(current.curation);
                }
                else pageError.value = 'Some loaded Captures could not be verified. Refresh to retry.';
            } catch (failure) {
                const status = handleFailure(failure);
                if (request !== pageSequence || access !== accessGeneration()) return retained;
                if (status === 404) continue;
                pageError.value = 'Some loaded Captures could not be verified. Refresh to retry.';
            }
        }
        if (matchesFilter(row)) retained.push(row);
    }
    return retained;
}
async function loadPage(next: boolean, reconcile = false) {
    if (next && (loading.value || !cursor.value || pageError.value))
        return;
    const request = ++pageSequence;
    const access = accessGeneration();
    Object.assign(pageRetry, { next, reconcile });
    loading.value = true;
    pageError.value = '';
    try {
        const page = await api.management.captures.get({ queryParameters: query(next) });
        if (request !== pageSequence || access !== accessGeneration())
            return;
        const anchor = !galleryInspectionId.value ? gridElement.value?.querySelector<HTMLElement>('.gallery-tile') : undefined;
        const anchorId = anchor?.dataset.id;
        const before = anchor?.getBoundingClientRect().top;
        const incoming = page?.items ?? [];
        const retained = reconcile && page?.nextCursor ? await revalidateBoundary(incoming, request, access) : rows.value;
        if (request !== pageSequence || access !== accessGeneration()) return;
        rows.value = next || reconcile && !!page?.nextCursor ? reconcileCaptures(next ? rows.value : retained, incoming, next, pageAfterId.value, appearance.value.sort === 'oldest') : incoming;
        if (orderSelection) {
            const loaded = new Set(rows.value.map(row => String(row.id)));
            selection.value = orderSelection.filter(id => loaded.has(id));
            orderSelection = undefined;
        }
        cursor.value = page?.nextCursor ?? null;
        pageAfterId.value = String(incoming.at(-1)?.id ?? '');
        generation.value++;
        await nextTick();
        measureGrid();
        if (reconcile && anchorId && before != null) {
            const after = [...(gridElement.value?.querySelectorAll<HTMLElement>('.gallery-tile') ?? [])].find(item => item.dataset.id === anchorId)?.getBoundingClientRect().top;
            if (after != null)
                window.scrollBy(0, after - before);
        }
        for (const row of rows.value)
            if (row.curation)
                editSessions.get(String(row.id))?.observe(row.curation);
        if ((!activeId.value || view.value === 'Curation' && !rows.value.some(row => String(row.id) === activeId.value)) && rows.value[0])
            void openCapture(String(rows.value[0].id));
        if (!rows.value.length && view.value === 'Curation') {
            closeFull(false);
            detailSequence.value++;
            activeId.value = paneAId.value = secondId.value = '';
            capture.value = undefined;
            compare.value = false;
        }
    }
    catch (failure) {
        handleFailure(failure);
        if (request === pageSequence && access === accessGeneration())
            pageError.value = 'Unable to load Captures. The current photographs are retained.';
    }
    finally {
        if (request === pageSequence)
            loading.value = false;
    }
}
async function nextPage() {
    const before = rows.value.length;
    const previousCursor = cursor.value;
    const request = pageSequence + 1;
    await loadPage(true);
    if (request === pageSequence && previousCursor && cursor.value && previousCursor !== cursor.value && rows.value.length === before && !pageError.value && (sentinel.value?.getBoundingClientRect().top ?? Infinity) < window.innerHeight + 500) {
        clearTimeout(pagingRetry);
        pagingRetry = setTimeout(() => {
            if (request === pageSequence) void nextPage();
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
async function refresh(ids?: Set<string>, previews?: {
    all: boolean;
    ids: Set<string>;
}) {
    slideshowReady.value = false;
    const access = accessGeneration();
    try {
        const page = await api.management.sources.get({ queryParameters: { limit: 100 } });
        if (access !== accessGeneration())
            return;
        sources.value = page?.items ?? [];
        policyStale.value = !!page?.nextCursor;
        const waiting = await api.management.captures.get({ queryParameters: { released: false, limit: 1 } });
        if (access !== accessGeneration())
            return;
        hasCuration.value = policyStale.value || sources.value.some(s => s.curationMode !== 'Immediate') || !!waiting?.items?.length;
        if (!initialized.value) {
            view.value = !props.captureId && sources.value.some(source => source.curationMode === 'Delay' || source.curationMode === 'Manual') ? 'Curation' : 'Gallery';
            initialized.value = true;
        }
    }
    catch (failure) {
        handleFailure(failure);
        policyStale.value = true;
    }
    if (!session.value || access !== accessGeneration())
        return;
    if (ids && rows.value.length) {
        for (const id of ids) {
            const index = rows.value.findIndex(r => String(r.id) === id);
            if (index < 0)
                continue;
            try {
                const value = await api.management.captures.byId(id).get();
                if (access !== accessGeneration())
                    return;
                if (value) {
                    const currentIndex = rows.value.findIndex(row => String(row.id) === id);
                    if (currentIndex < 0) continue;
                    rows.value[currentIndex] = { ...rows.value[currentIndex], ...value };
                    if (value.curation)
                        editSessions.get(id)?.observe(value.curation);
                }
            }
            catch (failure) {
                const status = handleFailure(failure);
                if (status === 404 && access === accessGeneration()) await forgetUnavailableCapture(id);
            }
        }
        for (const row of rows.value) if (unavailableInViewer(row)) invalidateSlide(String(row.id));
        rows.value = rows.value.filter(matchesFilter);
        generation.value++;
    }
    await loadPage(false, rows.value.length > 0);
    if (activeId.value && (!ids || ids.has(activeId.value)))
        await openCapture(activeId.value, false);
    if (previews) {
        const ids = previews.all ? new Set([...rows.value.map(row => String(row.id)), activeId.value, secondId.value]) : previews.ids;
        for (const id of ids)
            if (id) previewVersions.value[id] = (previewVersions.value[id] ?? 0) + 1;
    }
    slideshowReady.value = !pageError.value && !detailError.value;
}
const live = useReconciliation(refresh);
watch(() => [live.online.value, live.connected.value, live.connectionRecovery.value], () => {
    slideshowReady.value = false;
    if (slideshow.playing) playback.pause('Connection changed. Choose Play after the Gallery has refreshed.');
}, { flush: 'sync' });
watch(() => live.activity.value, hint => {
    if (hint?.changeKind !== 'erasure') return;
    previewVersions.value[hint.captureId] = (previewVersions.value[hint.captureId] ?? 0) + 1;
    void forgetUnavailableCapture(hint.captureId);
});
async function openCapture(id: string, viewer = false, pane?: 'A' | 'B', focusOnly = false) {
    detailController?.abort();
    const controller = detailController = new AbortController();
    const request = ++detailSequence.value;
    const access = accessGeneration();
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
        if (request !== detailSequence.value || access !== accessGeneration())
            return;
        capture.value = value;
        if (full.value && view.value === 'Gallery' && value && unavailableInViewer(value as Capture)) {
            invalidateSlide(id);
            return;
        }
        if (value?.curation) {
            if (full.value && view.value === 'Gallery') editSessions.get(id)?.observe(value.curation);
            else observeCuration(id, value.curation);
        }
    }
    catch (failure) {
        if (controller.signal.aborted) return;
        const status = handleFailure(failure);
        if (status === 404 && request === detailSequence.value && access === accessGeneration()) {
            await forgetUnavailableCapture(id);
            error.value = 'This Capture is no longer available in this workspace. Open Trash or Settings privacy requests for current recovery/removal evidence.';
        }
        else if (request === detailSequence.value && access === accessGeneration())
            detailError.value = 'Unable to open this Capture. Refresh to try again.';
    }
}
function observeCuration(id: string, current: CaptureCurationResponse) {
    const existing = editSessions.get(id);
    if (existing) {
        existing.observe(current);
        return;
    }
    editSessions.set(id, new CurationSession(current, body => api.management.captures.byId(id).curation.put(body), async () => {
        const detail = await api.management.captures.byId(id).get();
        if (!detail?.curation)
            throw new Error('Capture unavailable');
        return detail.curation;
    }, handleFailure));
}
async function openSettings(section: 'Workspace' | 'Storage' = 'Workspace') {
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
function selectSettingsSection(section: 'Workspace' | 'Storage') {
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
    if (next === view.value)
        return;
    if (view.value === 'Settings' && settingsDirty.value && !window.confirm('Discard unsaved storage settings and leave Settings?')) return;
    settingsDirty.value = false;
    if (view.value === 'Settings') storageVisited.value = false;
    const leavingPhotos = ['Sources', 'Delivery', 'Settings'].includes(next) && ['Gallery', 'Curation'].includes(view.value);
    if (leavingPhotos && unresolved.value && !window.confirm('Unresolved curation edits remain. Abandon local intent and leave the photo workspace?'))
        return;
    if (leavingPhotos) {
        photoVisit++;
        for (const value of editSessions.values())
            value.stop();
        editSessions.clear();
        tagDrafts.clear();
    }
    const leavingScroll = view.value === 'Gallery' && galleryInspectionId.value ? inspectionScroll : window.scrollY;
    galleryInspectionId.value = '';
    inspectionFocus = undefined;
    inspectionReturnId = '';
    detailSequence.value++;
    cancelHold();
    scrollPositions[view.value] = leavingScroll;
    if (view.value === 'Curation' || view.value === 'Gallery')
        browsing[view.value] = { rows: rows.value, cursor: cursor.value, afterId: pageAfterId.value };
    view.value = next;
    retentionSurface.value = 'photos';
    selecting.value = false;
    selection.value = [];
    orderSelection = undefined;
    closeFull();
    if (next === 'Gallery' || next === 'Curation') {
        rows.value = browsing[next].rows;
        cursor.value = browsing[next].cursor;
        pageAfterId.value = browsing[next].afterId;
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
    if (unresolved.value || settingsDirty.value) {
        event.preventDefault();
        event.returnValue = '';
    }
}
const removeRouteGuard = router.beforeEach(to => {
    const allowed = to.path === '/' || to.path.startsWith('/captures/') || !(unresolved.value || settingsDirty.value) || window.confirm('Unresolved edits remain. Abandon local intent and leave?');
    if (allowed && full.value && to.path !== `/captures/${encodeURIComponent(activeId.value)}`) closeFull(false);
    return allowed;
});
const unsubscribe = onAccessLost(() => {
    hydratedIdentity = '';
    settingsReturnFocus = undefined;
    settingsReturnCapture = '';
    settingsDirty.value = false;
    storageVisited.value = false;
    photoVisit++;
    pageSequence++;
    detailSequence.value++;
    suggestionSequence++;
    clearTimeout(tagSearch);
    clearTimeout(pagingRetry);
    for (const value of editSessions.values())
        value.stop();
    editSessions.clear();
    tagDrafts.clear();
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
    browsing.Curation = { rows: [], cursor: null, afterId: '' };
    browsing.Gallery = { rows: [], cursor: null, afterId: '' };
    sources.value = [];
    selection.value = [];
    orderSelection = undefined;
    suggestions.value = [];
    closeFull();
});
onUnmounted(() => {
    chrome.stop();
    endSlideshow();
    detailController?.abort();
    photoVisit++;
    unsubscribe();
    observer?.disconnect();
    cancelHold();
    for (const value of editSessions.values())
        value.stop();
    window.removeEventListener('beforeunload', beforeUnload);
    window.removeEventListener('scroll', measureGrid);
    window.removeEventListener('resize', measureGrid);
    document.removeEventListener('keydown', keyboard);
    document.removeEventListener('fullscreenchange', fullscreenChanged);
    document.removeEventListener('visibilitychange', visibilityChanged);
    motionQuery?.removeEventListener('change', motionChanged);
    clearInterval(countdown);
    clearTimeout(tagSearch);
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
let ratingAnchor: number | undefined;
let ratingDragged = false;
let suppressRatingClick = false;
function ratingRange(value: number, extend = false) {
    Object.assign(filter.value, ratingInterval(extend && filter.value.rating != null ? filter.value.rating : value, value));
}
function ratingSelected(value: number) {
    return filter.value.rating != null && value >= filter.value.rating && value <= (filter.value.ratingMaximum ?? filter.value.rating);
}
function beginRatingDrag(event: PointerEvent) {
    const value = (event.target as HTMLElement).closest<HTMLElement>('[data-rating]')?.dataset.rating;
    if (value == null || event.button !== 0)
        return;
    ratingAnchor = Number(value);
    ratingDragged = false;
}
function moveRatingDrag(event: PointerEvent) {
    if (event.buttons === 0) {
        cancelRatingDrag();
        return;
    }
    if (ratingAnchor == null)
        return;
    const value = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('[data-rating]')?.dataset.rating;
    if (value == null || Number(value) === ratingAnchor && !ratingDragged)
        return;
    if (!ratingDragged)
        (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    ratingDragged = true;
    Object.assign(filter.value, ratingInterval(ratingAnchor, Number(value)));
}
function endRatingDrag() {
    suppressRatingClick = ratingDragged;
    ratingAnchor = undefined;
    ratingDragged = false;
}
function cancelRatingDrag() {
    ratingAnchor = undefined;
    ratingDragged = false;
    suppressRatingClick = false;
}
function finishRatingClick(event: MouseEvent) {
    if (!suppressRatingClick)
        return;
    suppressRatingClick = false;
    event.preventDefault();
    event.stopPropagation();
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
function bulkItem(action: string, id: string, detail: CaptureDetailResponse | null | undefined) {
    const item = { id, expectedRevision: detail?.curation?.revision, ...values(detail?.curation ?? {}) };
    switch (action) {
        case 'reset': return { ...item, rating: 0, tags: [], label: null };
        case 'discard': return { ...item, ...editSessions.get(id)?.opening };
        case 'rating': return { ...item, rating: bulkRating.value };
        case 'label': return { ...item, label: bulkLabel.value };
        case 'tag': return { ...item, tags: bulkTag.value.trim() ? [...item.tags, bulkTag.value.trim()] : item.tags };
        case 'removeTag': return { ...item, tags: item.tags.filter(tag => tag !== bulkRemovedTag.value) };
        default: return item;
    }
}
async function bulk(action: 'rating' | 'label' | 'tag' | 'removeTag' | 'discard' | 'hold' | 'restore' | 'release' | 'reset') {
    if (view.value !== 'Curation' || !live.online.value || !selection.value.length || bulkBusy.value)
        return;
    if (action === 'release' && !window.confirm(`Release ${selection.value.length} explicitly selected Captures?`))
        return;
    const ids = [...selection.value];
    const access = accessGeneration();
    const visit = photoVisit;
    const current = () => access === accessGeneration() && visit === photoVisit;
    bulkBusy.value = true;
    bulkResult.value = '';
    try {
        for (const id of ids) {
            await editSessions.get(id)?.settle();
            if (!current())
                return;
        }
        if (ids.some(id => editSessions.get(id)?.unresolved))
            throw new Error('Resolve selected Capture edits before batching.');
        const details = await Promise.all(ids.map(id => api.management.captures.byId(id).get()));
        if (!current())
            return;
        const released = details.filter(detail => detail?.curation?.releasedAt);
        if (released.length) {
            bulkResult.value = `No batch submitted. Released Captures are read-only here: ${released.map(detail => detail?.displayName ?? 'Photograph').join(', ')}. Remove them from the selection and try again.`;
            return;
        }
        for (const detail of details) {
            if (!detail?.id || !detail.curation) throw new Error('Capture unavailable');
            const id = String(detail.id);
            observeCuration(id, detail.curation);
        }
        if (action === 'discard' && ids.some(id => editSessions.get(id)?.external))
            throw new Error('Values changed elsewhere; review before restoring.');
        const items = details.map((detail, index) => bulkItem(action, ids[index]!, detail));
        const batch = api.management.captures.batch;
        const result = ['reset', 'discard', 'rating', 'label', 'tag', 'removeTag'].includes(action) ? await batch.curation.post({ items }) : await batch[action as 'hold' | 'restore' | 'release'].post({ items });
        if (!current())
            return;
        const failures = (result?.items ?? []).filter(item => item.status !== 'committed');
        for (const item of result?.items ?? [])
            if (item.status === 'committed' && item.current && ids.includes(String(item.id)))
                editSessions.get(String(item.id))?.acceptBatch(item.current);
        bulkResult.value = failures.map(item => `${details.find(detail => String(detail?.id) === String(item.id))?.displayName ?? 'Photograph'}: ${item.errorCode ?? item.status}`).join(' · ');
        if (!result?.items || result.items.length !== ids.length || new Set(result.items.map(item => String(item.id))).size !== ids.length || result.items.some(item => !ids.includes(String(item.id))))
            bulkResult.value = 'Some outcomes are unknown. Review the selected photographs before retrying.';
        await refresh(new Set(ids));
    }
    catch (failure) {
        handleFailure(failure);
        if (!current())
            return;
        bulkResult.value = 'Batch did not complete or its outcome is uncertain. Selected photographs were retained; refresh and review before trying again.';
        await refresh(new Set(ids));
    }
    finally {
        bulkBusy.value = false;
    }
}
async function captureAction(action: 'hold' | 'restore' | 'release') {
    if (view.value !== 'Curation' || !edit.value || busyCapture.value || bulkBusy.value)
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
async function enterFull() {
    if (full.value) return;
    endSlideshow();
    focusBeforeViewer = document.activeElement as HTMLElement;
    full.value = true;
    viewerFavoriteFeedback.value = false;
    suppressViewerClick = false;
    chrome.start(viewerRecovery.value);
    zoom.value = 1;
    offset.x = offset.y = 0;
    if (activeId.value)
        void router.replace(`/captures/${encodeURIComponent(activeId.value)}`);
    document.querySelector('.app-shell')?.setAttribute('inert', '');
    await nextTick();
    fullElement.value?.focus();
    try {
        await fullElement.value?.requestFullscreen?.();
    }
    catch {
    }
}
function closeFull(updateUrl = true) {
    chrome.stop();
    viewerFavoriteFeedback.value = false;
    suppressViewerClick = false;
    endSlideshow();
    if (!full.value)
        return;
    detailController?.abort();
    detailSequence.value++;
    full.value = false;
    document.querySelector('.app-shell')?.removeAttribute('inert');
    if (updateUrl && session.value)
        void router.replace('/');
    if (document.fullscreenElement)
        void document.exitFullscreen();
    nextTick(() => focusBeforeViewer?.focus({ preventScroll: true }));
}
function fullscreenChanged() {
    if (!document.fullscreenElement)
        closeFull();
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
        zoom.value = 1;
        offset.x = offset.y = 0;
        void openCapture(String(next.id));
        if (full.value)
            void router.replace(`/captures/${encodeURIComponent(String(next.id))}`);
    }
}
function keyboard(event: KeyboardEvent) {
    if (!full.value) return;
    const hidden = !viewerChrome.visible;
    suppressViewerClick = false;
    chrome.keyboard();
    if ((event.target as HTMLElement)?.closest('input,textarea,select')) return;
    if (event.key === 'Tab') {
        if (hidden) {
            event.preventDefault();
            nextTick(() => {
                const buttons = [...(fullElement.value?.querySelectorAll<HTMLElement>('button:not(:disabled),select:not(:disabled)') ?? [])];
                (event.shiftKey ? buttons.at(-1) : buttons[0])?.focus();
            });
            return;
        }
        const buttons = [...(fullElement.value?.querySelectorAll<HTMLElement>('button:not(:disabled),select:not(:disabled)') ?? [])];
        const first = buttons[0];
        const last = buttons.at(-1);
        if (event.shiftKey && (document.activeElement === first || document.activeElement === fullElement.value)) {
            event.preventDefault();
            last?.focus();
        }
        else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first?.focus();
        }
    }
    if (event.key === 'Escape')
        closeFull();
    if (view.value === 'Curation' && ['+', '=', '-', 'Home'].includes(event.key)) {
        event.preventDefault();
        setZoom(event.key === 'Home' ? 1 : zoom.value * (event.key === '-' ? 1 / 1.25 : 1.25));
    }
    else if (view.value === 'Curation' && zoom.value > 1 && ['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) {
        event.preventDefault();
        offset.x += event.key === 'ArrowLeft' ? 40 : event.key === 'ArrowRight' ? -40 : 0;
        offset.y += event.key === 'ArrowUp' ? 40 : event.key === 'ArrowDown' ? -40 : 0;
        boundOffset();
    }
    else {
        if (event.key === 'ArrowLeft')
            navigate(-1);
        if (event.key === 'ArrowRight')
            navigate(1);
    }
    if (view.value === 'Curation' && canCurate.value && /^[0-5]$/.test(event.key))
        edit.value?.change({ rating: Number(event.key) });
    else if (view.value === 'Curation' && canCurate.value && /^[rR]$/.test(event.key)
        && !event.ctrlKey && !event.metaKey && !event.altKey && !event.isComposing) {
        event.preventDefault();
        edit.value?.change({ rating: -1 });
    }
}
function frameDimensions() {
    const viewport = { width: fullElement.value?.clientWidth ?? 1, height: fullElement.value?.clientHeight ?? 1 };
    const image = fullElement.value?.querySelector('img');
    const ratio = image?.naturalWidth && image.naturalHeight ? image.naturalWidth / image.naturalHeight : viewport.width / viewport.height;
    const width = Math.min(viewport.width, viewport.height * ratio);
    return { viewport, photo: { width, height: width / ratio } };
}
function boundOffset() {
    const { viewport, photo } = frameDimensions();
    Object.assign(offset, clampPan(offset, zoom.value, photo, viewport));
}
function setZoom(requested: number, point = { x: 0, y: 0 }) {
    const result = zoomAround(zoom.value, offset, requested, point);
    zoom.value = result.zoom;
    Object.assign(offset, result.offset);
    boundOffset();
}
function wheel(event: WheelEvent) {
    if (view.value !== 'Curation')
        return;
    event.preventDefault();
    const frame = fullElement.value?.getBoundingClientRect();
    setZoom(zoom.value * Math.exp(-event.deltaY * .002), { x: event.clientX - (frame?.left ?? 0) - (frame?.width ?? 0) / 2, y: event.clientY - (frame?.top ?? 0) - (frame?.height ?? 0) / 2 });
}
const pointers = new Map<number, {
    x: number;
    y: number;
}>();
let distance = 0;
function pointerDown(event: PointerEvent) {
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    (event.currentTarget as HTMLElement).setPointerCapture(event.pointerId);
    if (pointers.size === 2) {
        const points = [...pointers.values()];
        distance = Math.hypot(points[0]!.x - points[1]!.x, points[0]!.y - points[1]!.y);
    }
}
function pointerMove(event: PointerEvent) {
    const previous = pointers.get(event.pointerId);
    if (!previous || view.value !== 'Curation')
        return;
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pointers.size === 2) {
        const points = [...pointers.values()];
        const next = Math.hypot(points[0]!.x - points[1]!.x, points[0]!.y - points[1]!.y);
        const frame = fullElement.value?.getBoundingClientRect();
        if (distance)
            setZoom(zoom.value * next / distance, { x: (points[0]!.x + points[1]!.x) / 2 - (frame?.left ?? 0) - (frame?.width ?? 0) / 2, y: (points[0]!.y + points[1]!.y) / 2 - (frame?.top ?? 0) - (frame?.height ?? 0) / 2 });
        distance = next;
    }
    else if (zoom.value > 1) {
        offset.x += event.clientX - previous.x;
        offset.y += event.clientY - previous.y;
        boundOffset();
    }
}
function pointerUp(event: PointerEvent) {
    pointers.delete(event.pointerId);
    distance = 0;
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
    const saved = editSessions.get(String(row?.id))?.server;
    const detail = capture.value?.id === row?.id ? capture.value?.curation : undefined;
    const current = detail && (detail.revision ?? 0) > (row?.curation?.revision ?? 0) ? detail : row?.curation;
    return curationTiming(saved && (saved.revision ?? 0) > (current?.revision ?? 0) ? saved : current, retentionTiming(row?.retention, now.value, true, !live.online.value).stale ? row?.retention?.asOf?.getTime() ?? now.value : now.value);
}
function retentionWins(row: Capture | CaptureDetailResponse | undefined) {
    const timing = retentionTiming(row?.retention, now.value, true, !live.online.value);
    const deadline = row?.retention?.timeline?.endsAt?.getTime();
    const release = workflowStatus(row).fraction != null ? row?.curation?.releaseDueAt?.getTime() : undefined;
    return timing.visible && deadline != null && (!release || timing.tone !== 'neutral' && deadline < release);
}
async function bulkFavorite(isFavorite: boolean) {
    if (bulkBusy.value || !selection.value.length || !live.online.value) return;
    const ids = [...selection.value], access = accessGeneration();
    bulkBusy.value = true; bulkResult.value = '';
    const outcomes: string[] = [];
    try {
        for (const id of ids) {
            if (access !== accessGeneration()) return;
            try {
                const current = await api.management.captures.byId(id).get();
                if (access !== accessGeneration()) return;
                if (current?.retention?.captureRevision == null) throw new Error('Retention unavailable');
                await api.management.captures.byId(id).favorite.put({ expectedRevision: current.retention.captureRevision, isFavorite });
            } catch (failure) {
                const status = handleFailure(failure);
                outcomes.push(`${rows.value.find(row => String(row.id) === id)?.displayName ?? 'Photograph'}: ${status === 409 ? 'conflict; review current heart' : 'outcome unconfirmed; review before retrying'}`);
            }
        }
        if (access === accessGeneration()) { bulkResult.value = outcomes.join(' · ') || `${ids.length} Favorites saved.`; await refresh(new Set(ids)); }
    } finally { bulkBusy.value = false; }
}
async function afterTrash(id: string, moved: boolean) {
    if (moved && galleryInspectionId.value === id) await closeGalleryInspection();
    await refresh(new Set([id]));
}
async function forgetUnavailableCapture(id: string) {
    pageSequence++; loading.value = false;
    rows.value = rows.value.filter(row => String(row.id) !== id);
    for (const key of ['Curation', 'Gallery'] as const) browsing[key].rows = browsing[key].rows.filter(row => String(row.id) !== id);
    editSessions.get(id)?.stop(); editSessions.delete(id); tagDrafts.delete(id);
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
    if (bulkBusy.value || !selection.value.length || !live.online.value) return;
    const scope = selection.value.map(id => rows.value.find(row => String(row.id) === id)).filter((row): row is Capture => !!row);
    if (scope.length !== selection.value.length || scope.some(row => row.retention?.captureRevision == null || !originalRemovalScope(row.retention) || retentionTiming(row.retention, now.value, true).stale)) {
        bulkResult.value = 'We could not check the selected originals. Refresh before moving them to Trash.'; return;
    }
    const variants = scope.map(row => `${row.displayName ?? 'Photograph'}: ${originalRemovalScope(row.retention)}`).join('\n');
    if (!window.confirm(`Move ${scope.length} selected photographs to Trash, including ${scope.filter(row => row.retention?.isFavorite).length} Favorites?\n\nOriginal variants:\n${variants}\nTheir recovery window starts once the move is safely recorded. Trash still uses storage. These may be your only copies; gallery promises and preservation holds still apply. Backups and delivered or downloaded copies remain separate.`)) return;
    const access = accessGeneration(), outcomes: string[] = [];
    bulkBusy.value = true;
    try {
        for (const row of scope) {
            if (access !== accessGeneration()) return;
            try { await api.management.captures.byId(String(row.id)).trash.post({ expectedRevision: row.retention!.captureRevision! }); }
            catch (failure) { const status = handleFailure(failure); outcomes.push(`${row.displayName ?? 'Photograph'}: ${status === 409 ? 'conflict/protected' : 'outcome unconfirmed'}`); }
        }
        if (access === accessGeneration()) { bulkResult.value = outcomes.join(' · ') || `${scope.length} selected photographs moved to Trash.`; await refresh(new Set(scope.map(row => String(row.id)))); }
    } finally { bulkBusy.value = false; }
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
        :class="[{ full: workspacePreferences.width === 'full', 'reduce-effects': !motionEnabled, 'curation-viewport': view === 'Curation' && !expanded || view === 'Gallery' && !!galleryInspectionId }, `density-${appearance.density}`]"
        :style="{ '--frame-ratio': appearance.ratio, '--photo-fit': appearance.fit === 'fill' ? 'cover' : 'contain', '--tile-width': `${appearance.density}px` }"
    >
        <header class="view-heading">
            <div>
                <h1 tabindex="-1">
                    {{ view }}
                </h1>
                <p>
                    {{ view === 'Curation' ? 'Choose the photographs you want to send.' : view === 'Gallery' ? 'Your photographs, ready to browse.' : view === 'Sources' ? 'Connect your camera and bring in photographs.' : view === 'Settings' ? 'Make your workspace comfortable and manage shared storage.' : 'See where your photographs are going.' }}
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
                        <div class="filter-category">
                            <span id="source-filter-title" class="filter-title">Source</span>
                            <div class="filter-group" role="group" aria-labelledby="source-filter-title">
                                <button :aria-pressed="!filter.sourceIds.length" @click="filter.sourceIds = []">All sources</button>
                                <button v-for="source in sources" :key="String(source.id)" :aria-pressed="filter.sourceIds.includes(String(source.id))" :disabled="filter.sourceIds.length >= 30 && !filter.sourceIds.includes(String(source.id))" @click="toggleFilter('sourceIds', String(source.id))"><UIcon name="i-lucide-camera" />{{ source.name }}</button>
                            </div>
                        </div>
                        <div class="filter-category">
                            <span id="rating-filter-title" class="filter-title">Rating</span>
                            <div
                                class="filter-group rating-interval"
                                role="group"
                                aria-labelledby="rating-filter-title"
                                @pointerdown="beginRatingDrag"
                                @pointermove="moveRatingDrag"
                                @pointerup="endRatingDrag"
                                @pointercancel="cancelRatingDrag"
                                @click.capture="finishRatingClick"
                            >
                                <button :aria-pressed="filter.rating == null" @click="filter.rating = null; filter.ratingMaximum = null">
                                    All ratings
                                </button>
                                <button data-rating="-1" :aria-pressed="ratingSelected(-1)" @click="ratingRange(-1, $event.shiftKey)">
                                    <UIcon name="i-lucide-star-off" />
                                    <span class="sr-only">
                                        Rejected
                                    </span>
                                </button>
                                <button data-rating="0" :aria-pressed="ratingSelected(0)" @click="ratingRange(0, $event.shiftKey)">
                                    <UIcon name="i-lucide-circle-minus" />
                                    <span class="sr-only">
                                        Not Rated
                                    </span>
                                </button>
                                <button
                                    v-for="n in 5"
                                    :key="n"
                                    :data-rating="n"
                                    :aria-label="`${n} stars; Shift selects interval`"
                                    :aria-pressed="ratingSelected(n)"
                                    @click="ratingRange(n, $event.shiftKey)"
                                >
                                    <RatingStar />
                                </button>
                            </div>
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
                            <input id="capture-filter-tag" v-model="filter.tag" maxlength="64" placeholder="Search tags" />
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
                    <div ref="appearanceOverlay" class="filter-content appearance-content">
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
                        <div class="appearance-footer"><button @click="resetAppearance">Reset {{ view }} appearance</button><button @click="openSettings()">Workspace settings<UIcon name="i-lucide-arrow-up-right" /></button></div>
                    </div>
                </details>
                <span class="spacer" />
                <div v-if="view === 'Gallery'" class="gallery-toolbar-actions">
                    <div class="retention-navigation" role="group" aria-label="Gallery views"><button :aria-pressed="retentionSurface === 'photos'" @click="retentionSurface = 'photos'"><UIcon name="i-lucide-grid-2x2" />Photos</button><button :aria-pressed="retentionSurface === 'trash'" @click="retentionSurface = 'trash'"><UIcon name="i-lucide-trash-2" />Trash</button></div>
                    <button class="gallery-fullscreen icon-button" :disabled="!rows.length" aria-label="Fullscreen photographs" title="Fullscreen photographs" @click="openGalleryFull"><UIcon name="i-lucide-maximize" /></button>
                </div>
                <template v-if="view === 'Curation'">
                    <button :aria-pressed="expanded" :aria-label="expanded ? 'Photo view' : 'Bulk grid'" :title="expanded ? 'Photo view' : 'Bulk grid'" @click="expanded = !expanded; selecting = expanded; selection = []">
                        <UIcon :name="expanded ? 'i-lucide-image' : 'i-lucide-grid-2x2'" />
                    </button>
                </template>
            </div>
        <p v-if="preferenceNotice" class="notice preference-notice" role="status">{{ preferenceNotice }}</p>
        <SourceWorkspace :grid-maximum-dimension="workspacePreferences.gridMaximumDimension" v-if="view === 'Sources'" :sources="sources" :activity="live.activity.value" :auth-activity="live.authActivity.value" :phase-activity="live.phaseActivity.value" :recovery-sequence="live.recoverySequence.value" :preview-versions="previewVersions" :connected="live.connected.value" :motion-enabled="motionEnabled" @refresh="refresh()" />
        <DeliveryWorkspace
            :grid-maximum-dimension="workspacePreferences.gridMaximumDimension"
            v-else-if="view === 'Delivery'"
            :online="live.online.value"
            :connected="live.connected.value"
            :recovery-sequence="live.recoverySequence.value"
            :preview-versions="previewVersions"
            :generation="generation"
            :motion-enabled="motionEnabled"
        />
        <TrashWorkspace :grid-maximum-dimension="workspacePreferences.gridMaximumDimension" v-else-if="view === 'Gallery' && retentionSurface === 'trash'" :online="live.online.value" :generation="generation" @refresh="refresh()" @erased="afterErasure" @storage-settings="openSettings('Storage')" />
        <section v-else-if="view === 'Settings'" class="settings-home" aria-label="Settings">
            <nav class="settings-sections" aria-label="Settings sections">
                <button v-for="section in (['Workspace', 'Storage'] as const)" :key="section" :aria-current="settingsSection === section ? 'page' : undefined" @click="selectSettingsSection(section)"><UIcon :name="section === 'Workspace' ? 'i-lucide-panels-top-left' : 'i-lucide-hard-drive'" />{{ section }}</button>
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
                    <div class="workspace-reset"><button @click="resetWorkspace">Reset workspace preferences</button></div>
                </template>
                <div v-show="settingsSection === 'Storage'">
                    <p class="muted">Shared settings for photograph storage and recovery.</p>
                    <RetentionSettings v-if="storageVisited" :online="live.online.value" :generation="generation" @dirty="settingsDirty = $event" />
                </div>
            </div>
        </section>
        <template v-else>

            <div v-if="view === 'Gallery' && (selection.length || selecting)" class="bulk-bar" role="region" aria-label="Selected photographs">
                <strong>{{ selection.length }} selected · maximum 100</strong>
                <button :disabled="bulkBusy" @click="selectLoaded">Select all loaded</button>
                <button :disabled="bulkBusy" @click="selection = []; selecting = false">Select none</button>
                <span class="muted">Downloads are not available in this alpha.</span>
                <CollectionMove :ids="selection" :online="live.online.value" :disabled="bulkBusy || unresolved" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" @completed="selection = selection.filter(id => !$event.includes(id))" />
            </div>
            <p v-if="view === 'Gallery' && bulkResult" class="notice" role="status">{{ bulkResult }}</p>
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
                    Change filters or connect a Source to send photographs.
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
                            <RetentionLifetime v-if="retentionWins(compare ? paneA : capture ?? paneA)" :evidence="(compare ? paneA : capture ?? paneA)?.retention" :now="now" details :stale="!live.online.value" /><ReleaseLifetime v-else :state="workflowStatus(compare ? paneA : capture ?? paneA)" />
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
                            <RetentionLifetime v-if="retentionWins(paneB)" :evidence="paneB?.retention" :now="now" details :stale="!live.online.value" /><ReleaseLifetime v-else :state="workflowStatus(paneB)" />
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
                    <div class="filmstrip" aria-label="Capture filmstrip">
                        <button
                            v-for="row in filmRows"
                            :key="String(row.id)"
                            :aria-label="row.displayName ?? 'Capture'"
                            :aria-pressed="String(row.id) === activeId"
                            @click="openCapture(String(row.id), false, compare && $event.shiftKey ? 'B' : undefined)"
                        >
                            <RetentionLifetime v-if="retentionWins(row)" :evidence="row.retention" :now="now" details :stale="!live.online.value" /><ReleaseLifetime v-else :state="workflowStatus(row)" />
                            <AuthorizedPreview
                                :capture-id="String(row.id)"
                                profile="thumbnail"
                                :name="row.displayName ?? 'Capture'"
                                :maximum-dimension="workspacePreferences.gridMaximumDimension"
                                :force-generation="previewVersions[String(row.id)] ?? 0"
                            />
                        </button>
                    </div>
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
                            <template #favorite><FavoriteControl compact :capture-id="activeId" :evidence="capture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" /></template>
                            <template #collection>
                                <CollectionMove :key="String(capture.id)" :ids="[String(capture.id)]" :online="live.online.value" :disabled="edit?.unresolved || bulkBusy || busyCapture" :current-name="capture.currentCollection?.path ?? capture.currentCollection?.name ?? undefined" :released="!!capture.curation?.releasedAt" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" />
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
                        <CaptureResumeAction :capture-id="activeId" :evidence="capture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                        <p v-if="edit?.server.releaseDueAt && !edit.server.releasedAt && !edit.server.held" class="muted">
                            {{ workflowStatus(capture).title }}
                        </p>
                        <details class="capture-evidence section-disclosure">
                            <summary>
                                <UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Details
                            </summary>
                            <CaptureEvidence :capture="capture" :generation="generation" :online="live.online.value" @refresh="refresh(new Set([$event]))" />
                        </details>
                        <details class="photograph-storage section-disclosure">
                            <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Storage and recovery</summary>
                            <RetentionDetails :evidence="capture.retention" :now="now" :stale="!live.online.value" expanded />
                            <NoDeliveryIntentAction :capture="capture" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                            <CaptureTrashAction :capture-id="activeId" :name="capture.displayName ?? 'Photograph'" :evidence="capture.retention" :disabled="!live.online.value" @refresh="afterTrash" />
                            <PrivacyErasurePanel :capture-id="activeId" :display-name="capture.displayName ?? 'Photograph'" :evidence="capture.retention" :online="live.online.value" :generation="generation" @requested="afterErasure" />
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
                                <RetentionLifetime :evidence="inspectionCapture?.retention" :now="now" details :stale="!live.online.value" />
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
                            <div class="photograph-markers"><CaptureRating :rating="inspectionCapture.curation?.rating" :label="inspectionCapture.curation?.label" /><FavoriteControl compact :capture-id="galleryInspectionId" :evidence="inspectionCapture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" /></div>
                            <p class="muted">{{ inspectionCapture.assets?.map(asset => asset.variant).join(' + ') }} · {{ cameraDescription(inspectionCapture) }}</p>
                            <p v-if="inspectionCapture.curation?.tags?.length" class="muted">{{ inspectionCapture.curation.tags.join(' · ') }}</p>
                            <CaptureResumeAction :capture-id="galleryInspectionId" :evidence="inspectionCapture.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                            <CollectionMove v-if="inspectionMode !== 'delivery'" :key="String(inspectionCapture.id)" :ids="[String(inspectionCapture.id)]" :online="live.online.value" :disabled="bulkBusy" :current-name="inspectionCapture.currentCollection?.path ?? inspectionCapture.currentCollection?.name ?? undefined" :released="!!inspectionCapture.curation?.releasedAt" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" />
                            <details class="capture-evidence section-disclosure" :open="inspectionMode === 'delivery'"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />{{ inspectionMode === 'delivery' ? 'Delivery details' : 'Details' }}</summary><CaptureEvidence :capture="inspectionCapture" :generation="generation" :online="live.online.value" :read-only="inspectionMode !== 'delivery'" :delivery-only="inspectionMode === 'delivery'" :focus-state="inspectionDeliveryState" @refresh="refresh(new Set([$event]))" /></details>
                            <details class="photograph-storage section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Storage and recovery</summary>
                                <RetentionDetails :evidence="inspectionCapture.retention" :now="now" :stale="!live.online.value" expanded />
                                <NoDeliveryIntentAction :capture="inspectionCapture" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                                <CaptureTrashAction :capture-id="galleryInspectionId" :name="inspectionCapture.displayName ?? 'Photograph'" :evidence="inspectionCapture.retention" :disabled="!live.online.value" @refresh="afterTrash" />
                                <PrivacyErasurePanel :capture-id="galleryInspectionId" :display-name="inspectionCapture.displayName ?? 'Photograph'" :evidence="inspectionCapture.retention" :online="live.online.value" :generation="generation" @requested="afterErasure" />
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
                    <article
                        v-for="row in virtualRows"
                        :key="String(row.id)"
                        class="gallery-tile"
                        :data-id="String(row.id)"
                        :class="{ selected: selection.includes(String(row.id)) }"
                    >
                        <button
                            class="tile-open"
                            :aria-label="view === 'Gallery' ? `Photo details for ${row.displayName}` : `Open ${row.displayName}`"
                            @pointerdown="startHold($event, String(row.id))"
                            @pointermove="cancelHold"
                            @pointerup="endHold"
                            @pointercancel="cancelHold()"
                            @click="tileClick(String(row.id))"
                        >
                            <RetentionLifetime v-if="view === 'Gallery' || retentionWins(row)" :evidence="row.retention" :now="now" :details="appearance.details" :stale="!live.online.value" /><ReleaseLifetime v-else :state="workflowStatus(row)" />
                            <AuthorizedPreview
                                :capture-id="String(row.id)"
                                profile="thumbnail"
                                :name="row.displayName ?? 'Capture'"
                                :maximum-dimension="workspacePreferences.gridMaximumDimension"
                                :force-generation="previewVersions[String(row.id)] ?? 0"
                            />
                            <div v-if="appearance.details" class="tile-information">
                                <span>
                                    {{ row.displayName }}
                                </span>
                                <CaptureRating :rating="row.curation?.rating" :label="row.curation?.label" />
                                <div class="tile-detail">
                                    <span>
                                        {{ row.assets?.map(asset => asset.variant).join(' + ') }}
                                    </span>
                                    <span>
                                        {{ cameraDescription(row) }}
                                    </span>
                                    <span>
                                        {{ row.curation?.tags?.join(' · ') || 'No tags' }}
                                    </span>
                                </div>
                            </div>
                        </button>
                        <FavoriteControl class="photo-favorite-action" :class="{ 'details-visible': appearance.details }" compact :capture-id="String(row.id)" :evidence="row.retention" :disabled="!live.online.value" @refresh="refresh(new Set([$event]))" />
                        <button v-if="appearance.details && view === 'Gallery'" class="tile-delivery-action" :aria-label="deliveryStatus(row).title" :title="deliveryStatus(row).title" @click="inspectDelivery(row)"><UIcon :name="deliveryStatus(row).icon" /></button>
                        <span v-if="view === 'Curation'" class="tile-workflow-state" :class="{ 'without-caption': !appearance.details }" role="img" :aria-label="workflowStatus(row).title" :title="workflowStatus(row).title"><UIcon :name="workflowStatus(row).icon" /></span>
                        <label v-if="selecting || view === 'Curation' && expanded" class="tile-selection">
                            <input
                                type="checkbox"
                                :checked="selection.includes(String(row.id))"
                                @change="toggle(String(row.id))"
                                :aria-label="`Select ${row.displayName}`"
                            />
                        </label>
                        <button
                            v-else
                            class="photo-select-action"
                            :aria-label="`Select ${row.displayName}`"
                            @click="selecting = true; toggle(String(row.id))"
                        >
                            <UIcon name="i-lucide-check" />
                        </button>
                    </article>
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
                    <div class="actions"><button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulkFavorite(true)">Mark Favorite</button><button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulkFavorite(false)">Remove Favorite</button><button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulkTrash">Move selected to Trash</button></div>
                    <CurationControls
                        :rating="bulkValues.rating" :label="bulkValues.label" :tags="bulkValues.tags" :mixed="bulkValues.mixed"
                        v-model:tag="bulkTag" :disabled="!selection.length || !live.online.value" :busy="bulkBusy"
                        @rating="bulkRating = $event; bulk('rating')" @label="bulkLabel = $event; bulk('label')"
                        @add-tag="bulk('tag')" @remove-tag="bulkRemovedTag = $event; bulk('removeTag')"
                        @reset="bulk($event ? 'reset' : 'discard')"
                    >
                        <template #collection>
                            <CollectionMove :ids="selection" :online="live.online.value" :disabled="bulkBusy" @busy="bulkBusy = $event" @refresh="refresh(new Set($event))" @completed="selection = selection.filter(id => !$event.includes(id))" />
                        </template>
                    </CurationControls>
                    <div class="actions">
                        <button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulk('hold')">Hold</button>
                        <button :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulk('restore')">Restore policy</button>
                        <button class="primary" :disabled="bulkBusy || !selection.length || !live.online.value" @click="bulk('release')">Release now</button>
                    </div>
                    <p v-if="bulkResult" class="notice error" role="alert">{{ bulkResult }}</p>
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
                <RetentionLifetime v-if="view === 'Gallery' || retentionWins(capture)" :evidence="capture?.retention" :now="now" :details="view === 'Curation' || appearance.details" :stale="!live.online.value" /><ReleaseLifetime v-else :state="workflowStatus(capture)" />
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
                <div v-if="view === 'Gallery'" class="viewer-favorite"><FavoriteControl compact :capture-id="activeId" :evidence="capture?.retention" :disabled="!live.online.value" @feedback="viewerFavoriteFeedback = $event" @refresh="refresh(new Set([$event]))" /></div>
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
                    <FavoriteControl compact :capture-id="activeId" :evidence="capture?.retention" :disabled="!live.online.value" @feedback="viewerFavoriteFeedback = $event" @refresh="refresh(new Set([$event]))" />
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
