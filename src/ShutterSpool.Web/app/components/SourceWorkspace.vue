<script setup lang="ts">
import { api, handleFailure, readSourceAuthenticationObservations, readSourceActivity, type SourceAuthenticationObservation } from '../lib/api';
import { SourceActivityState, activityPresentation, sourceActivityCounters, sourceStatusPosition, type SourceActivity } from '../lib/source-activity';
import { onAccessLost, accessGeneration } from '../lib/session';
import type { SourceConfigurationResponse, SourceDisplayResponse, CaptureInboxItemResponse } from '../api/models';
const props = defineProps<{
    gridMaximumDimension?: number;
    sources: SourceConfigurationResponse[];
    connected?: boolean;
    recoverySequence?: number;
    authActivity?: SourceAuthenticationObservation;
    phaseActivity?: SourceActivity;
    previewVersions?: Record<string, number>;
    motionEnabled?: boolean;
    activity?: { captureId: string; kind: 'captureChanged' | 'previewReady'; sequence: number };
}>();
const emit = defineEmits<{
    refresh: [
    ];
}>();
const chosen = ref('');
let disposed = false;
let ownerVersion = 0;
let statusPopup: { element: HTMLElement; trigger: HTMLElement; pointer?: { x: number; y: number } } | undefined;
function placeStatusPopup() {
    if (!statusPopup?.element.matches(':popover-open')) return;
    const badge = statusPopup.trigger.querySelector('.source-auth')!.getBoundingClientRect();
    const anchor = statusPopup.pointer ?? { x: badge.left, y: badge.top };
    const position = sourceStatusPosition(anchor, statusPopup.element.getBoundingClientRect(), { width: window.innerWidth, height: window.innerHeight });
    Object.assign(statusPopup.element.style, { left: `${position.left}px`, top: `${position.top}px` });
}
function toggleSourceStatus(event: MouseEvent, id: string) {
    event.preventDefault();
    const element = document.getElementById(`source-status-${id}`)!;
    if (element.matches(':popover-open')) { element.hidePopover(); return; }
    const trigger = event.currentTarget as HTMLElement;
    statusPopup = { element, trigger, pointer: event.detail > 0 ? { x: event.clientX, y: event.clientY } : undefined };
    trigger.focus({ preventScroll: true });
    Object.assign(element.style, { margin: '0', inset: 'auto', left: '0px', top: '0px' });
    // DOM typings predate the native invoker option.
    Reflect.apply(element.showPopover, element, [{ source: trigger }]);
    placeStatusPopup();
}
function statusToggled(event: Event) {
    if ((event as ToggleEvent).newState === 'closed' && event.currentTarget === statusPopup?.element) statusPopup = undefined;
}
const source = computed(() => props.sources.find(s => String(s.id) === chosen.value));
const name = ref('');
const mode = ref('Manual');
const collection = ref<string[]>([]);
type ConnectionProfile = 'PlainFtpLan' | 'ModernFtps' | 'LegacyFtps';
const profileLabels = { PlainFtpLan: 'Unencrypted FTP', ModernFtps: 'FTPS — recommended', LegacyFtps: 'FTPS — compatibility (legacy)' };
const connectionProfile = ref<ConnectionProfile | ''>('');
const settingsTab = ref('General');
const settingsTabs = ['General', 'Connection', 'Security'];
const capabilities = ref<Awaited<ReturnType<typeof api.management.sourceCapabilities.get>>>();
const capabilityState = ref<'loading' | 'ready' | 'failed'>('loading');
const selectedEndpoint = computed(() => capabilities.value?.profiles?.find(item => item.profile === connectionProfile.value));
const canAssignProfile = computed(() => capabilityState.value === 'ready' && !!connectionProfile.value &&
    (selectedEndpoint.value?.enabled === true || (!!source.value && connectionProfile.value === source.value.connectionProfile)));
let capabilitySequence = 0;
async function refreshCapabilities() {
    const sequence = ++capabilitySequence;
    const access = accessGeneration();
    capabilityState.value = 'loading';
    try {
        const value = await api.management.sourceCapabilities.get();
        if (disposed || sequence !== capabilitySequence || access !== accessGeneration()) return;
        if (!value?.profiles) throw new Error('Connection profiles unavailable.');
        capabilities.value = value;
        capabilityState.value = 'ready';
        if (!chosen.value && !dirty.value && !connectionProfile.value && value.profiles.some(item => item.profile === 'ModernFtps' && item.enabled))
            connectionProfile.value = 'ModernFtps';
    } catch (error) {
        handleFailure(error);
        if (!disposed && sequence === capabilitySequence && access === accessGeneration()) capabilityState.value = 'failed';
    }
}
function changeSettingsTab(event: KeyboardEvent, index: number) {
    const step = event.key === 'ArrowRight' ? 1 : event.key === 'ArrowLeft' ? -1 : 0;
    if (!step && event.key !== 'Home' && event.key !== 'End') return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? 2 : (index + step + 3) % 3;
    settingsTab.value = settingsTabs[next]!;
    document.getElementById(`source-tab-${next}`)?.focus();
}
const caBusy = ref(false);
const caFeedback = ref('');
async function downloadRoot() {
    if (caBusy.value || !connectionProfile.value || capabilityState.value !== 'ready') return;
    const access = accessGeneration();
    const profile = connectionProfile.value;
    const owner = ownerVersion;
    caBusy.value = true;
    caFeedback.value = '';
    try {
        const bytes = await api.management.sourceCapabilities.byProfile(profile).caRoot.get();
        if (disposed || access !== accessGeneration() || owner !== ownerVersion || profile !== connectionProfile.value) return;
        if (!bytes) throw new Error('CA root unavailable.');
        const url = URL.createObjectURL(new Blob([bytes], { type: 'application/x-pem-file' }));
        const link = document.createElement('a');
        link.href = url;
        link.download = `shutterspool-${profile}-ca-root.pem`;
        link.click();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) {
        handleFailure(error);
        if (access === accessGeneration() && owner === ownerVersion && profile === connectionProfile.value) caFeedback.value = 'CA root unavailable. Ask the operator to check the public root configured for this endpoint, then retry.';
    } finally { caBusy.value = false; }
}
onMounted(() => { void refreshCapabilities(); });
const delay = ref(30);
const delayUnit = ref(1);
const delayUnits = [{ label: 'Seconds', seconds: 1 }, { label: 'Minutes', seconds: 60 }, { label: 'Hours', seconds: 3600 }, { label: 'Days', seconds: 86400 }, { label: 'Weeks', seconds: 604800 }];
function setDelay(seconds: number) {
    delayUnit.value = [...delayUnits].reverse().find(unit => seconds % unit.seconds === 0)?.seconds ?? 1;
    delay.value = seconds / delayUnit.value;
}
const streamEnds = ref<Record<string, boolean>>({});
const suppressStreamMotion = ref(true);
watch(() => props.recoverySequence, () => { suppressStreamMotion.value = true; });
const busy = ref(false);
const phases = reactive(new SourceActivityState());
const phaseLoading = ref(false);
const phaseNow = ref(Date.now());
let phaseClock: ReturnType<typeof setInterval> | undefined;
let phaseSequence = 0;
let phaseController: AbortController | undefined;
onMounted(() => { phaseClock = setInterval(() => phaseNow.value = Date.now(), 1000); window.addEventListener('resize', placeStatusPopup); });
const phasePresentations = computed(() => {
    const grouped: Record<string, (SourceActivity & ReturnType<typeof activityPresentation>)[]> = {};
    for (const item of phases.items.values()) {
        (grouped[item.sourceId] ??= []).push({ ...item,
            ...activityPresentation(item, phaseNow.value, props.connected ?? false, phases.available) });
    }
    return grouped;
});
async function refreshPhases(ids: string[]) {
    const sequence = ++phaseSequence;
    phaseController?.abort();
    if (!ids.length) { phases.clear(); return; }
    const access = accessGeneration();
    phaseController = new AbortController();
    phaseLoading.value = true;
    const readRevision = phases.revision;
    try {
        const unresolved = [...phases.items.values()].filter(item => !item.captureId && item.phase !== 'FailedUnavailable' && item.phase !== 'Stale').map(item => item.transferId);
        const [result, known] = await Promise.all([readSourceActivity(ids, phaseController.signal),
            unresolved.length ? readSourceActivity(ids, phaseController.signal, unresolved) : undefined]);
        if (sequence !== phaseSequence || access !== accessGeneration()) return;
        if (known) {
            if (known.epoch !== result.epoch) throw new Error('Activity changed during the read.');
            const identities = new Set(known.items.map(item => item.transferId));
            result.items = [...known.items, ...result.items.filter(item => !identities.has(item.transferId))];
            result.coverageComplete &&= known.coverageComplete;
        }
        phases.reconcile(result, ids, readRevision);
    } catch (error) {
        handleFailure(error);
        if (sequence === phaseSequence && access === accessGeneration()) phases.available = false;
    } finally { if (sequence === phaseSequence) phaseLoading.value = false; }
}
watch(() => props.phaseActivity, hint => {
    if (hint && props.sources.some(item => String(item.id) === hint.sourceId)) phases.observe(hint);
}, { flush: 'sync' });
const feedback = ref('');
const feedbackNotice = ref<HTMLElement>();
const secret = ref('');
const username = ref('');
const passwordMode = ref<'Generated' | 'Custom'>('Generated');
const customPassword = ref('');
const replacingCredential = ref(false);
const credentialErrors = ref<Record<string, string>>({});
const copyFeedback = ref('');
const passwordPolicy = computed(() => capabilities.value?.passwordPolicy);
const passwordLengthValid = computed(() => !!passwordPolicy.value && customPassword.value.length >= passwordPolicy.value.minimumLength! && customPassword.value.length <= passwordPolicy.value.maximumLength!);
const passwordCharactersValid = computed(() => /^[!-~]+$/.test(customPassword.value));
const customPasswordValid = computed(() => passwordLengthValid.value && passwordCharactersValid.value);
const customPasswordInput = ref<HTMLInputElement>();
const credentialDialog = ref<HTMLDialogElement>();
function validatePasswordInput() {
    if (passwordMode.value !== 'Custom' || customPasswordValid.value) return true;
    credentialErrors.value.password = 'Check the password requirements below.';
    settingsTab.value = 'Connection';
    void nextTick(() => customPasswordInput.value?.focus());
    return false;
}
function clearCredentialSecrets() {
    customPassword.value = '';
    secret.value = '';
    copyFeedback.value = '';
    credentialDialog.value?.close();
}
function credentialOptions() {
    return { username: username.value || undefined, passwordMode: passwordMode.value,
        password: passwordMode.value === 'Custom' ? customPassword.value : undefined };
}
function requestCredentialReplacement() {
    if (busy.value || conflict.value || !passwordPolicy.value || !validatePasswordInput()) return;
    credentialDialog.value?.showModal();
}
async function copyConnection(value: string | number | null | undefined, label: string) {
    if (value === undefined || value === null) return;
    const owner = ownerVersion;
    const access = accessGeneration();
    try {
        await navigator.clipboard.writeText(String(value));
        if (owner === ownerVersion && access === accessGeneration() && !disposed) copyFeedback.value = `${label} copied.`;
    } catch {
        if (owner === ownerVersion && access === accessGeneration() && !disposed) copyFeedback.value = `Could not copy ${label.toLowerCase()}. Select and copy it manually.`;
    }
}
function showInvalidField(event: Event) {
    const field = event.target as HTMLElement;
    settingsTab.value = field.closest('#source-panel-1') ? 'Connection' : 'General';
    void nextTick(() => field.focus());
}
const removalDialog = ref<HTMLDialogElement>();
const removal = ref<{ id: string; name: string; revision: number | null | undefined }>();
function requestRemoval() {
    if (!source.value) return;
    removal.value = { id: String(source.value.id), name: source.value.name ?? 'Source', revision: source.value.configurationRevision };
    removalDialog.value?.showModal();
}
async function removeSource() {
    const target = removal.value;
    if (!target || target.id !== chosen.value || busy.value || conflict.value) return;
    const access = accessGeneration();
    const version = ownerVersion;
    const isCurrentOwner = () => chosen.value === target.id && version === ownerVersion;
    busy.value = true;
    secret.value = '';
    try {
        const result = await api.management.sources.byId(target.id).delete({ queryParameters: { expectedRevision: target.revision ?? undefined } });
        if (access !== accessGeneration()) return;
        emit('refresh');
        if (!isCurrentOwner()) return;
        removalDialog.value?.close();
        removal.value = undefined;
        if (chosen.value === target.id) chosen.value = '';
        dirty.value = false;
        feedback.value = result?.outcome === 'archived' ? `${target.name} archived. Existing Captures are preserved.` : result?.outcome === 'deleted' ? `${target.name} deleted.` : 'Removal outcome unavailable. Refresh Sources to confirm their state.';
    } catch (failure) {
        const status = handleFailure(failure);
        if (access !== accessGeneration()) return;
        if (!isCurrentOwner()) {
            emit('refresh');
            return;
        }
        removalDialog.value?.close();
        conflict.value = status !== 400;
        feedback.value = status === 409 ? 'Source settings changed. Reload saved settings before removing it.' : 'Removal outcome uncertain. Refresh Sources before retrying.';
    } finally { busy.value = false; }
}
const conflict = ref(false);
const dirty = ref(false);
const selectionDialog = ref<HTMLDialogElement>();
let pendingSelection: string | undefined;
function selectSource(id: string) {
    if (id === chosen.value) return;
    if (dirty.value) {
        pendingSelection = id;
        selectionDialog.value?.showModal();
        return;
    }
    chosen.value = id;
}
function cancelSelection() {
    pendingSelection = undefined;
    selectionDialog.value?.close();
}
function discardSelection() {
    const id = pendingSelection;
    cancelSelection();
    if (id !== undefined) chosen.value = id;
}
const revision = ref<number | null>();
const streamCapacity = ref<Record<string, number>>({});
const streamElements = new Map<string, HTMLElement>();
function measureStream(id: string) {
    const element = streamElements.get(id);
    if (element) streamEnds.value[id] = (element.firstElementChild?.scrollWidth ?? 0) > element.clientWidth;
}
const streamObservers = new Map<string, ResizeObserver>();
function observeStream(element: unknown, id: string) {
    if (!(element instanceof HTMLElement)) {
        streamObservers.get(id)?.disconnect();
        streamObservers.delete(id);
        streamElements.delete(id);
        return;
    }
    if (streamObservers.has(id)) return;
    streamElements.set(id, element);
    const observer = new ResizeObserver(entries => {
        const width = entries[0]?.contentRect.width ?? 0;
        streamCapacity.value[id] = Math.min(30, Math.max(1, Math.ceil(width / (window.innerWidth < 760 ? 120 : 152)) + 1));
        void nextTick(() => measureStream(id));
    });
    observer.observe(element);
    streamObservers.set(id, observer);
}
const recent = ref<Record<string, CaptureInboxItemResponse[]>>({});
const display = ref<Record<string, SourceDisplayResponse>>({});
function displaySource(item: SourceConfigurationResponse) {
    const cached = display.value[String(item.id)];
    return cached && cached.configurationRevision === item.configurationRevision ? cached : item;
}
async function readDisplay(id: string, access: number) {
    try {
        const value = await api.management.sources.byId(id).display.get();
        if (disposed || access !== accessGeneration()) return;
        if (String(value?.id) === id) display.value[id] = value!;
        else delete display.value[id];
    } catch (error) {
        handleFailure(error);
        if (!disposed && access === accessGeneration()) delete display.value[id];
    }
}
const recentState = ref<Record<string, 'loading' | 'ready' | 'failed'>>({});
type AuthenticationState = { outcome?: 'Succeeded' | 'Rejected'; observedAt?: string; version: number; available: boolean; coverageComplete: boolean; stale?: boolean };
const authentication = ref<Record<string, AuthenticationState>>({});
const authLoading = ref(false);
const authPulses = ref<Record<string, 'Succeeded' | 'Rejected'>>({});
const authTimers = new Map<string, ReturnType<typeof setTimeout>>();
let authReadSequence = 0;
let authController: AbortController | undefined;
let pendingStatus: string[] | undefined;
let statusRead: Promise<void> | undefined;
function authenticationStatus(id: string) {
    const value = authentication.value[id];
    if (!value && authLoading.value) return { state: 'neutral', icon: 'i-lucide-info', text: 'Reading authentication history' };
    if (!value?.available)
        return { state: 'unavailable', icon: 'i-lucide-info', text: 'Authentication history unavailable' };
    if (!value.outcome)
        return { state: 'neutral', icon: 'i-lucide-circle-help', text: value.coverageComplete ? 'No saved authentication outcome' : 'No saved outcome; observation coverage is incomplete' };
    const outcome = value.outcome === 'Succeeded' ? 'succeeded' : 'was rejected';
    const date = value.observedAt ? new Date(value.observedAt).toLocaleString() : 'at an unknown time';
    return { state: value.stale ? 'unavailable' : value.outcome === 'Succeeded' ? 'success' : 'error',
        icon: value.outcome === 'Succeeded' ? 'i-lucide-circle-check' : 'i-lucide-circle-x',
        text: `Last recorded camera authentication ${outcome} ${date}${value.stale ? '; authentication history unavailable; saved result may be old' : ''}${!value.coverageComplete ? '; observation coverage is incomplete' : ''}` };
}
const authenticationPresentations = computed(() => Object.fromEntries(props.sources.map(item => [String(item.id), authenticationStatus(String(item.id))])));
const sourceRows = computed(() => props.sources.map(item => {
    const id = String(item.id);
    const observations = phasePresentations.value[id] ?? [];
    const current = observations.find(item => item.active) ?? observations[0];
    const activitySummary = phaseLoading.value && !phases.available ? 'Reading activity' : !phases.available ? 'Activity unavailable' : current?.label ?? 'No current transfer observation';
    const tone = !phases.available ? 'stale' : current?.tone ?? 'neutral';
    const counts = sourceActivityCounters(observations, phases.available && !!props.connected);
    return { ...item, id, display: displaySource(item), authentication: authenticationPresentations.value[id]!,
        observations: observations.slice(0, 6), observationCount: observations.length,
        activitySummary, tone, counters: [
            { label: 'Receiving', value: counts.receiving, description: 'Fresh receiving transfers in recent observations.' },
            { label: 'Preview pending', value: counts.previewPending, description: 'Distinct Capture previews or transfers awaiting grouping in recent observations.' },
            { label: 'Received', value: counts.received, description: 'Accepted originals in recent observations, independent of preview and delivery.' },
        ],
        active: observations.some(item => item.active) };
}));
watch(sourceRows, () => { if (statusPopup) void nextTick(placeStatusPopup); });
async function refreshAuthentication(ids: string[]) {
    const sequence = ++authReadSequence;
    const access = accessGeneration();
    authController?.abort();
    if (!ids.length || ids.length > 100) return;
    authController = new AbortController();
    authLoading.value = true;
    try {
        const result = await readSourceAuthenticationObservations(ids, authController.signal);
        if (sequence !== authReadSequence || access !== accessGeneration()) return;
        const updated = { ...authentication.value };
        for (const id of ids) {
            const saved = result.items.find(item => item.sourceId.toLowerCase() === id.toLowerCase());
            const previous = updated[id];
            if (saved && (!previous?.version || saved.version >= previous.version))
                updated[id] = { outcome: saved.outcome, observedAt: saved.observedAt, version: saved.version, available: true,
                    coverageComplete: result.coverageComplete };
            else if (!previous?.outcome)
                updated[id] = { version: previous?.version ?? 0, available: true, coverageComplete: result.coverageComplete };
            else updated[id] = { ...previous, coverageComplete: result.coverageComplete, stale: false };
        }
        authentication.value = updated;
    } catch (error) {
        handleFailure(error);
        if (sequence !== authReadSequence || access !== accessGeneration()) return;
        const updated = { ...authentication.value };
        for (const id of ids) {
            const previous = updated[id];
            updated[id] = previous?.outcome ? { ...previous, stale: true } :
                { version: previous?.version ?? 0, available: false, coverageComplete: false };
        }
        authentication.value = updated;
    } finally { if (sequence === authReadSequence) authLoading.value = false; }
}
watch(() => props.authActivity, hint => {
    if (!hint) return;
    const id = props.sources.find(item => String(item.id).toLowerCase() === hint.sourceId.toLowerCase())?.id?.toString();
    if (!id) return;
    const previous = authentication.value[id];
    if (previous && hint.version <= previous.version) return;
    authentication.value = { ...authentication.value, [id]: { outcome: hint.outcome, observedAt: hint.observedAt,
        version: hint.version, available: true, coverageComplete: previous?.coverageComplete ?? true } };
    if (props.connected && !document.hidden) {
        authPulses.value[id] = hint.outcome;
        clearTimeout(authTimers.get(id));
        authTimers.set(id, setTimeout(() => { delete authPulses.value[id]; authTimers.delete(id); }, 2000));
    }
}, { flush: 'sync' });
watch(() => props.sources.map(item => String(item.id)), ids => {
    authentication.value = Object.fromEntries(Object.entries(authentication.value).filter(([id]) => ids.includes(id)));
    for (const id of authTimers.keys()) if (!ids.includes(id)) { clearTimeout(authTimers.get(id)); authTimers.delete(id); delete authPulses.value[id]; }
    void refreshStatus(ids);
}, { immediate: true });
watch(chosen, () => {
    ownerVersion++;
    caFeedback.value = '';
    dirty.value = false;
    conflict.value = false;
    clearCredentialSecrets();
    replacingCredential.value = false;
    credentialErrors.value = {};
    username.value = '';
    passwordMode.value = 'Generated';
    removal.value = undefined;
    removalDialog.value?.close();
}, { flush: 'sync' });
watch(source, value => {
    if (dirty.value) {
        if (value?.configurationRevision !== revision.value) {
            conflict.value = true;
            feedback.value = 'Saved Source settings changed. Your draft is retained; reload before saving.';
        }
        return;
    }
    username.value = value?.username ?? '';
    name.value = value?.name ?? '';
    mode.value = value?.curationMode ?? 'Manual';
    collection.value = value?.collectionId ? [String(value.collectionId)] : [];
    connectionProfile.value = value?.connectionProfile as ConnectionProfile ?? (capabilityState.value === 'ready' && capabilities.value?.profiles?.some(item => item.profile === 'ModernFtps' && item.enabled) ? 'ModernFtps' : '');
    setDelay(value?.curationDelaySeconds ?? 30);
    revision.value = value?.configurationRevision;
}, { immediate: true });
let pendingSources: SourceConfigurationResponse[] | undefined;
let readingRecent = false;
async function refreshRecent(items: SourceConfigurationResponse[]) {
    pendingSources = items;
    if (readingRecent)
        return;
    readingRecent = true;
    const access = accessGeneration();
    try {
        while (pendingSources && access === accessGeneration()) {
            const pass = pendingSources;
            pendingSources = undefined;
            for (const source of pass.slice(0, 30)) {
                const id = String(source.id);
                recentState.value[id] = 'loading';
                try {
                    const [page] = await Promise.all([api.management.captures.get({ queryParameters: { sourceId: id, limit: 30 } }), readDisplay(id, access)]);
                    if (disposed || access !== accessGeneration())
                        return;
                    recent.value[id] = page?.items ?? [];
                    recentState.value[id] = 'ready';
                    void nextTick(() => measureStream(String(source.id)));
                }
                catch (failure) {
                    handleFailure(failure);
                    if (access !== accessGeneration()) return;
                    recentState.value[id] = 'failed';
                }
                if (pendingSources)
                    break;
            }
        }
    }
    finally {
        readingRecent = false;
        await nextTick();
        if (!readingRecent) suppressStreamMotion.value = false;
    }
}
watch(() => props.sources, items => {
    const ids = new Set(items.map(item => String(item.id)));
    recent.value = Object.fromEntries(Object.entries(recent.value).filter(([id]) => ids.has(id)));
    recentState.value = Object.fromEntries(Object.entries(recentState.value).filter(([id]) => ids.has(id)));
    streamEnds.value = Object.fromEntries(Object.entries(streamEnds.value).filter(([id]) => ids.has(id)));
    streamCapacity.value = Object.fromEntries(Object.entries(streamCapacity.value).filter(([id]) => ids.has(id)));
    display.value = Object.fromEntries(Object.entries(display.value).filter(([id]) => ids.has(id)));
    void refreshRecent(items);
}, { immediate: true });
const unsubscribe = onAccessLost(() => {
    capabilitySequence++;
    capabilities.value = undefined;
    authReadSequence++;
    phaseSequence++;
    phaseController?.abort();
    phases.clear();
    authController?.abort();
    authentication.value = {};
    display.value = {};
    authPulses.value = {};
    authTimers.forEach(clearTimeout);
    authTimers.clear();
    pendingSources = undefined;
    clearCredentialSecrets();
    replacingCredential.value = false;
    credentialErrors.value = {};
    username.value = '';
    passwordMode.value = 'Generated';
    removal.value = undefined;
    removalDialog.value?.close();
    name.value = '';
    chosen.value = '';
    feedback.value = '';
    recent.value = {};
    recentState.value = {};
    cancelSelection();
});
onUnmounted(() => {
    window.removeEventListener('resize', placeStatusPopup);
    statusPopup = undefined;
    disposed = true;
    pendingSources = undefined;
    pendingStatus = undefined;
    phaseSequence++;
    phaseController?.abort();
    clearInterval(phaseClock);
    authReadSequence++;
    authController?.abort();
    authTimers.forEach(clearTimeout);
    phases.clear();
    authentication.value = {};
    authPulses.value = {};
    recent.value = {};
    display.value = {};
    ownerVersion++;
    clearCredentialSecrets();
    unsubscribe();
    streamObservers.forEach(observer => observer.disconnect());
});
async function command(action: 'save' | 'enabled' | 'secret' | 'create') {
    if (busy.value || conflict.value)
        return;
    if ((action === 'create' || action === 'secret') && !validatePasswordInput()) return;
    const generation = accessGeneration();
    const owner = chosen.value;
    const version = ownerVersion;
    const isCurrentOwner = () => owner === chosen.value && version === ownerVersion;
    const encryptionChanged = connectionProfile.value !== source.value?.connectionProfile;
    if ((action === 'save' || action === 'create') && !canAssignProfile.value) return;
    busy.value = true;
    feedback.value = '';
    secret.value = '';
    credentialErrors.value = {};
    credentialDialog.value?.close();
    const credentials = credentialOptions();
    try {
        let value: SourceConfigurationResponse | null | undefined;
        let returnedSecret: string | undefined;
        if (action === 'create') {
            const result = await api.management.sources.post({ collectionId: collection.value[0], name: name.value, curationMode: mode.value, delaySeconds: mode.value === 'Delay' ? delay.value * delayUnit.value : null, sourceType: 'CameraFtpUpload', connectionProfile: connectionProfile.value as ConnectionProfile, ...credentials });
            if (accessGeneration() !== generation)
                return;
            if (!isCurrentOwner()) {
                emit('refresh');
                return;
            }
            returnedSecret = result?.secret ?? undefined;
            if (result?.id)
                value = await api.management.sources.byId(String(result.id)).get();
        }
        else {
            const endpoint = api.management.sources.byId(owner);
            const expectedRevision = revision.value;
            if (action === 'save')
                value = await endpoint.put({ collectionId: collection.value[0], name: name.value, curationMode: mode.value, delaySeconds: mode.value === 'Delay' ? delay.value * delayUnit.value : null, expectedRevision, sourceType: 'CameraFtpUpload', connectionProfile: connectionProfile.value as ConnectionProfile });
            if (action === 'enabled')
                value = await endpoint.ingress.put({ expectedRevision, enabled: !source.value?.ingressEnabled });
            if (action === 'secret') {
                const result = await endpoint.regenerateSecret.post({ expectedRevision, ...credentials });
                value = result?.current;
                returnedSecret = result?.secret ?? undefined;
            }
        }
        if (accessGeneration() !== generation)
            return;
        emit('refresh');
        if (!isCurrentOwner()) return;
        if (value?.id)
            chosen.value = String(value.id);
        secret.value = returnedSecret ?? '';
        replacingCredential.value = false;
        if (value) { revision.value = value.configurationRevision; username.value = value.username ?? username.value; }
        if (action !== 'secret') dirty.value = false;
        feedback.value = action === 'secret' ? 'Credentials replaced. Update every device using this Source.' : action === 'create' ? (returnedSecret ? 'Source created. Copy its password now.' : 'Source created. Use your chosen password in each device.') : action === 'save' && encryptionChanged
            ? 'Saved. Reconnect every client using this Source to apply its connection profile. The credential is unchanged.'
            : 'Saved. Policy changes apply to future Captures.';
    }
    catch (error) {
        const status = handleFailure(error);
        if (accessGeneration() !== generation)
            return;
        if (!isCurrentOwner()) {
            emit('refresh');
            return;
        }
        type FieldErrors = { additionalData?: Record<string, unknown> } & Record<string, unknown>;
        const problem = error as { title?: string; errors?: FieldErrors; additionalData?: { errors?: FieldErrors; field?: string; errorCode?: string } };
        const validation = problem.errors ?? problem.additionalData?.errors;
        const errors = validation?.additionalData ?? validation ?? {};
        for (const field of ['username', 'password', 'passwordMode']) {
            const messages = errors[field] ?? errors[field[0]!.toUpperCase() + field.slice(1)];
            if (messages) credentialErrors.value[field] = Array.isArray(messages) ? messages.join(' ') : String(messages);
        }
        if (status === 409 && problem.additionalData?.field === 'username' && problem.additionalData.errorCode === 'SOURCE_USERNAME_CONFLICT')
            credentialErrors.value.username ??= problem.title || 'This username is already in use.';
        const fieldRejected = (status === 400 || status === 409) && Object.keys(credentialErrors.value).length > 0;
        conflict.value = status !== 400 && !fieldRejected;
        feedback.value = fieldRejected ? 'Check the highlighted credential fields.' : status === 409 ? 'Configuration conflict. Reload saved settings before editing again.' : status === 400 ? 'Settings were not accepted. Check your entries.' : 'Outcome uncertain. Reread saved Sources before trying again. A lost generated password cannot be recovered; replace it deliberately.';
        if (status !== 400 && !fieldRejected) emit('refresh');
        if (fieldRejected) settingsTab.value = 'Connection';
        await nextTick();
        if (accessGeneration() === generation && isCurrentOwner()) {
            feedbackNotice.value?.focus({ preventScroll: true });
            feedbackNotice.value?.scrollIntoView({ block: 'center' });
        }
    }
    finally {
        if (isCurrentOwner()) customPassword.value = '';
        busy.value = false;
    }
}
async function reload() {
    if (busy.value) return;
    const owner = ownerVersion;
    const access = accessGeneration();
    busy.value = true;
    try {
        const saved = chosen.value ? await api.management.sources.byId(chosen.value).get() : await api.management.sources.get();
        if (!saved) throw new Error('Saved Sources unavailable.');
        if (owner !== ownerVersion || access !== accessGeneration() || disposed) return;
        if ('configurationRevision' in saved) revision.value = saved.configurationRevision;
        conflict.value = false;
        feedback.value = 'Saved Sources reread. Check the list before submitting again. Passwords cannot be recovered; replace a lost generated password deliberately.';
        emit('refresh');
    } catch (error) {
        handleFailure(error);
        if (owner === ownerVersion && access === accessGeneration()) feedback.value = 'Saved Sources unavailable. Your draft is retained. Retry the reread before submitting.';
    } finally { busy.value = false; }
}
function refreshStatus(ids: string[]): Promise<void> {
    pendingStatus = ids;
    authReadSequence++;
    phaseSequence++;
    authController?.abort();
    phaseController?.abort();
    return statusRead ??= (async () => {
        try {
            while (pendingStatus) {
                const sources = pendingStatus;
                pendingStatus = undefined;
                await Promise.all([refreshAuthentication(sources), refreshPhases(sources)]);
            }
        } finally { statusRead = undefined; }
    })();
}
function retryStatus() {
    void refreshStatus(props.sources.map(item => String(item.id)));
}
</script>

<template>
    <div v-if="!sources.length" class="empty">
        <h2>
            Add your camera Source
        </h2>
        <p>
            Create a Source, then use its credentials in your camera or ingest client.
        </p>
    </div>
    <section v-for="item in sourceRows" :key="item.id" class="source-stream source-row" :class="{ paused: !item.ingressEnabled, selected: chosen === item.id }">
        <div class="source-summary-wrap">
        <button class="source-summary" :aria-pressed="chosen === item.id" :aria-label="`${item.display.name}, Camera / FTP upload, ${profileLabels[item.connectionProfile as ConnectionProfile] ?? 'Profile unavailable'} settings`" :aria-describedby="`auth-${item.id}`" @click="selectSource(item.id)">
            <div class="source-icon">
                <UIcon name="i-lucide-network" />
            </div>
            <div class="source-copy">
                <h2>
                    {{ item.name }}
                </h2>
                <span>
                    {{ item.ingressEnabled ? 'Ingress enabled' : 'Ingress paused' }}
                    ·
                    {{ item.curationMode }}
                    <span class="source-protocol">Camera / FTP upload · {{ profileLabels[item.connectionProfile as ConnectionProfile] ?? 'Profile unavailable' }}</span>
                </span>
            </div>
        </button>
        <button class="source-status-control source-auth-control" :popovertarget="`source-status-${item.id}`" :aria-label="`Inspect Source status for ${item.name}`" :title="item.authentication.text" @click="toggleSourceStatus($event, item.id)"><span class="source-auth" :class="[`status-${item.authentication.state}`, { 'auth-pulse': motionEnabled && authPulses[item.id], 'auth-rejected': authPulses[item.id] === 'Rejected' }]" aria-hidden="true"><UIcon :name="item.authentication.icon" /></span></button>
            <div :id="`source-status-${item.id}`" popover="auto" class="source-status-popover" role="region" :aria-labelledby="`source-status-title-${item.id}`" @toggle="statusToggled">
                <h3 :id="`source-status-title-${item.id}`">{{ item.name }} status</h3>
                <p :id="`auth-${item.id}`">{{ item.authentication.text }}. Historical outcome; camera presence and upload success are separate.</p>
                <p>{{ item.activitySummary }}<span v-if="phases.available && !phases.coverageComplete"> · Partial activity coverage</span></p>
                <p>Recent bounded observations: Receiving counts transfers, Preview pending counts shared Capture previews once or transfers awaiting grouping, and Received counts accepted originals.</p>
                <ol class="transfer-observations">
                    <li v-for="phase in item.observations" :key="phase.transferId"><strong>Transfer {{ phase.transferId.slice(0, 8) }}</strong><span>{{ phase.label }}</span></li>
                </ol>
                <p v-if="item.observationCount > item.observations.length">Showing {{ item.observations.length }} of {{ item.observationCount }} observations.</p>
                <button v-if="!phaseLoading && !authLoading && (!phases.available || !authentication[item.id]?.available || authentication[item.id]?.stale)" :aria-label="`Retry status for ${item.name}`" @click="retryStatus">Retry status</button>
                <div class="stream-feedback" role="status">
                    <template v-if="recentState[item.id] === 'failed'"><p>{{ recent[item.id]?.length ? 'Last known photographs. Recent Captures unavailable.' : 'Recent Captures unavailable.' }}</p><button :aria-label="`Retry recent Captures for ${item.name}`" @click="refreshRecent([item])">Retry</button></template>
                    <p v-else-if="recentState[item.id] === 'loading'">{{ recent[item.id]?.length ? 'Updating recent Captures…' : 'Loading recent Captures…' }}</p>
                    <p v-else-if="recentState[item.id] === 'ready' && !recent[item.id]?.length">No recent Captures.</p>
                    <template v-else-if="recentState[item.id] !== 'ready'"><p>Recent Captures not loaded.</p><button :aria-label="`Load recent Captures for ${item.name}`" @click="refreshRecent([item])">Load</button></template>
                </div>
            </div>
        </div>
        <div class="flow-connection source-activity" :class="[{ running: motionEnabled && connected && item.active }, `activity-${item.tone}`]" role="status" :aria-label="`${item.name}: ${item.activitySummary}`">
            <div class="flow-line"><span v-for="n in 3" :key="n" class="flow-dot" aria-hidden="true" /></div>
            <small class="recent-work-counts source-counters">
                <span v-for="counter in item.counters" :key="counter.label" role="status" :title="counter.description" :aria-label="`${counter.label}: ${counter.value ?? 'unavailable'}. ${counter.description}`"><span class="counter-number">{{ counter.value ?? '—' }}</span><span>{{ counter.label }}</span></span>
            </small>
        </div>
        <div :ref="element => observeStream(element, String(item.id))" class="recent-stream-frame" :class="{ 'at-stream-end': streamEnds[String(item.id)] }">
        <TransitionGroup :name="motionEnabled && !suppressStreamMotion ? 'stream' : undefined" tag="div" class="recent-stream">
            <AuthorizedPreview :maximum-dimension="gridMaximumDimension"
                v-for="row in (recent[String(item.id)] ?? []).slice(0, streamCapacity[String(item.id)] ?? 6)"
                :key="String(row.id)"
                :capture-id="String(row.id)"
                :force-generation="previewVersions?.[String(row.id)] ?? 0"
                profile="thumbnail"
                :name="row.displayName ?? 'Capture'"
            />
            <small v-if="!recent[String(item.id)]?.length" key="empty">
                Recent Captures appear here after grouping.
            </small>
        </TransitionGroup>
        </div>
    </section>
    <section class="settings-panel panel">
        <div class="toolbar">
            <h2>
                {{ source ? `${source.name} settings` : 'New Source' }}
            </h2>
            <button v-if="chosen" @click="selectSource('')">
                New Source
            </button>
        </div>
        <p class="muted">Camera / FTP upload · {{ profileLabels[connectionProfile as ConnectionProfile] ?? 'Choose an available profile in Security' }}</p>
        <div class="source-settings-tabs" role="tablist" aria-label="Source settings">
            <button v-for="(tab, index) in settingsTabs" :id="`source-tab-${index}`" :key="tab" type="button" role="tab" :aria-selected="settingsTab === tab" :tabindex="settingsTab === tab ? 0 : -1" :aria-controls="`source-panel-${index}`" @click="settingsTab = tab" @keydown="changeSettingsTab($event, index)">{{ tab }}</button>
        </div>
        <p v-if="capabilityState !== 'ready'" class="notice" role="status">
            {{ capabilityState === 'loading' ? 'Loading deployment connection profiles…' : 'Connection profiles unavailable. Saved settings and your draft are retained.' }}
            <button v-if="capabilityState === 'failed'" type="button" @click="refreshCapabilities">Retry connection profiles</button>
        </p>
        <p v-else-if="selectedEndpoint && !selectedEndpoint.enabled" class="notice" role="status">Unavailable in this deployment. This Source keeps its saved profile, credential and enabled setting. Ask the operator to enable its endpoint or explicitly choose another available profile.</p>
        <form @invalid.capture="showInvalidField" @input="dirty = true" @change="dirty = true" @submit.prevent="command(source ? 'save' : 'create')">
            <section id="source-panel-0" v-show="settingsTab === 'General'" role="tabpanel" aria-labelledby="source-tab-0" tabindex="0">
                <p class="muted">Source type: Camera / FTP upload</p>
                <p>Several devices can share a Source, its credentials and policy. Use separate Sources for independent revocation, policy and troubleshooting.</p>
                <div class="fields">
                    <label class="field">Name<input v-model="name" required maxlength="200" /></label>
                    <CollectionPicker v-model="collection" :disabled="busy || conflict" :current-name="source?.collectionName ?? 'ShutterSpool (default for new Sources)'" label="Incoming Collection default" @update:model-value="dirty = true" />
                    <p class="muted">New photographs use this default when grouped. Existing photographs keep their Collection.</p>
                    <label class="field">Curation policy<select v-model="mode"><option>Immediate</option><option>Delay</option><option>Manual</option></select></label>
                    <label v-if="mode === 'Delay'" class="field">Delay<div class="delay-fields"><input v-model.number="delay" type="number" min="1" :max="Math.floor(31536000 / delayUnit)" step="1" required aria-label="Delay amount" /><select v-model.number="delayUnit" aria-label="Delay unit"><option v-for="unit in delayUnits" :key="unit.seconds" :value="unit.seconds">{{ unit.label }}</option></select></div></label>
                </div>
                <p>Existing Captures retain their policy, deadline and Hold.</p>
                <div v-if="source" class="actions">
                    <p>{{ source.ingressEnabled ? 'Ingress enabled' : 'Ingress paused' }}</p>
                    <button type="button" :disabled="busy || conflict" @click="command('enabled')">{{ source.ingressEnabled ? 'Disable ingress' : 'Enable ingress' }}</button>
                    <button type="button" class="danger" :disabled="busy || conflict" @click="requestRemoval"><UIcon name="i-lucide-trash-2" /> Delete Source</button>
                </div>
            </section>
            <section id="source-panel-1" v-show="settingsTab === 'Connection'" role="tabpanel" aria-labelledby="source-tab-1" tabindex="0" class="source-connection">
                <h3>Client setup</h3>
                <p>{{ profileLabels[connectionProfile as ConnectionProfile] ?? 'Choose an available profile in Security.' }}</p>
                <dl v-if="capabilityState === 'ready' && selectedEndpoint?.enabled" class="source-endpoint"><dt>Hostname</dt><dd><span>{{ selectedEndpoint.hostname }}</span><button type="button" aria-label="Copy hostname" @click="copyConnection(selectedEndpoint.hostname, 'Hostname')"><UIcon name="i-lucide-copy" /></button></dd><dt>Control port</dt><dd><span>{{ selectedEndpoint.controlPort }}</span><button type="button" aria-label="Copy port" @click="copyConnection(selectedEndpoint.controlPort, 'Port')"><UIcon name="i-lucide-copy" /></button></dd><dt>Security</dt><dd>{{ connectionProfile === 'PlainFtpLan' ? 'Unencrypted FTP' : 'Explicit FTPS · encrypted control and data' }}</dd></dl>
                <div v-if="source?.username" class="source-username"><span>Username: {{ source.username }}</span><button type="button" aria-label="Copy username" @click="copyConnection(source.username, 'Username')"><UIcon name="i-lucide-copy" /></button></div>
                <p class="muted">Use passive transfers. Keep FTPS certificate validation on.</p>
                <button v-if="source && !replacingCredential" type="button" :disabled="busy || conflict || !passwordPolicy" @click="replacingCredential = true">Replace credentials</button>
                <fieldset v-if="!source || replacingCredential" class="source-credentials" :disabled="busy || conflict || !passwordPolicy">
                    <legend>{{ source ? 'Replace credentials' : 'Camera credentials' }}</legend>
                    <p v-if="source">Every device sharing this Source must reconnect with the new credentials.</p>
                    <div class="fields">
                        <div class="field">
                            <label for="source-username">Username</label>
                            <input id="source-username" v-model="username" name="source-username" autocomplete="off" maxlength="64" pattern="[A-Za-z0-9][A-Za-z0-9_\-]{0,63}" :placeholder="source ? 'Keep current username' : 'Generate automatically'" :aria-invalid="!!credentialErrors.username" aria-describedby="source-username-help source-username-error" />
                            <small id="source-username-help">1–64 letters, numbers, underscores or hyphens. Start with a letter or number.</small>
                            <small v-if="credentialErrors.username" id="source-username-error" class="credential-error">{{ credentialErrors.username }}</small>
                        </div>
                        <div class="field">
                            <label for="source-password-mode">Password</label>
                            <select id="source-password-mode" v-model="passwordMode" @change="customPassword = ''; credentialErrors.password = ''"><option value="Generated">Generate for me</option><option value="Custom">Choose my own</option></select>
                            <small v-if="passwordPolicy && passwordMode === 'Generated'">Creates a password with {{ passwordPolicy.generatedLength }} characters.</small>
                        </div>
                    </div>
                    <p v-if="!passwordPolicy" class="notice">Password requirements unavailable. Retry connection profiles above.</p>
                    <div v-if="passwordMode === 'Custom'" class="field">
                        <label for="source-custom-password">Custom password</label>
                        <input id="source-custom-password" ref="customPasswordInput" v-model="customPassword" type="password" name="source-password" autocomplete="new-password" spellcheck="false" autocapitalize="none" :required="!source" :minlength="passwordPolicy?.minimumLength ?? undefined" pattern="[!-~]+" :aria-invalid="!!credentialErrors.password" aria-describedby="source-password-help source-password-error" @input="credentialErrors.password = ''" />
                        <ul v-if="passwordPolicy" id="source-password-help" class="password-requirements">
                            <li :class="{ 'criterion-met': passwordLengthValid }"><UIcon v-if="passwordLengthValid" name="i-lucide-circle-check" aria-hidden="true" /><UIcon v-else name="i-lucide-circle" aria-hidden="true" />{{ passwordPolicy.minimumLength }}–{{ passwordPolicy.maximumLength }} characters</li>
                            <li :class="{ 'criterion-met': passwordCharactersValid }"><UIcon v-if="passwordCharactersValid" name="i-lucide-circle-check" aria-hidden="true" /><UIcon v-else name="i-lucide-circle" aria-hidden="true" />English letters, numbers or keyboard symbols; no spaces</li>
                        </ul>
                        <small role="status" class="password-status" :class="{ 'criterion-met': customPasswordValid }">{{ customPasswordValid ? 'Password meets requirements.' : '' }}</small>
                        <small v-if="credentialErrors.password" id="source-password-error" class="credential-error">{{ credentialErrors.password }}</small>
                    </div>
                    <p v-if="passwordPolicy?.weakerProtection" class="muted">This installation allows short passwords, which offer less protection.</p>
                    <p v-if="credentialErrors.passwordMode" class="credential-error">{{ credentialErrors.passwordMode }}</p>
                    <div v-if="source" class="actions"><button type="button" :disabled="busy || conflict || (passwordMode === 'Custom' && !customPassword)" @click="requestCredentialReplacement">Replace credentials...</button><button type="button" @click="replacingCredential = false; customPassword = ''; credentialErrors = {}">Cancel</button></div>
                </fieldset>
                <details class="section-disclosure source-setup-help">
                    <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Connection help</summary>
                    <ul>
                        <li>Enter the hostname, port, username and password in each device.</li>
                        <li>Use passive mode. For FTPS, keep certificate validation on and try the camera's existing trust settings first.</li>
                        <li>If the connection fails, check the address, credentials and certificate trust before changing profiles.</li>
                    </ul>
                </details>
            </section>
            <section id="source-panel-2" v-show="settingsTab === 'Security'" role="tabpanel" aria-labelledby="source-tab-2" tabindex="0">
                <fieldset class="source-security" :disabled="busy || conflict || capabilityState !== 'ready'">
                    <legend>Connection profile</legend>
                    <p>Check the manufacturer’s latest applicable firmware and try standard FTPS first. Firmware updates are optional. <a href="https://github.com/SeWieland/ShutterSpool/blob/main/docs/camera-compatibility.md" target="_blank" rel="noopener noreferrer">Camera compatibility guide</a></p>
                    <label v-for="(label, profile) in profileLabels" :key="profile" class="source-security-choice"><input v-model="connectionProfile" type="radio" name="source-profile" :value="profile" :aria-describedby="profile === 'PlainFtpLan' ? 'source-plain-warning' : profile === 'LegacyFtps' ? 'source-legacy-warning' : undefined" :disabled="!capabilities?.profiles?.some(item => item.profile === profile && item.enabled)" /><span>{{ label }}<small v-if="!capabilities?.profiles?.some(item => item.profile === profile && item.enabled)"> · Unavailable in this deployment</small></span></label>
                    <p>Each Source uses exactly one profile. A change keeps its secret and media, invalidates existing logins and requires every client to reconnect.</p>
                </fieldset>
                <div v-if="connectionProfile && connectionProfile !== 'PlainFtpLan'" class="source-connection">
                    <h3>Server certificate trust</h3>
                    <p>Try existing camera trust first. If your client needs this root, download it, verify its SHA-256 fingerprint and import it using the manufacturer’s certificate instructions. Keep certificate validation enabled.</p>
                    <template v-if="capabilityState === 'ready' && selectedEndpoint">
                        <p v-if="selectedEndpoint.setupError" class="notice" role="status">{{ selectedEndpoint.setupError }}</p>
                        <dl v-if="selectedEndpoint.caRootAvailable" class="source-endpoint"><dt>Root identity</dt><dd>{{ selectedEndpoint.caRootIdentity }}</dd><dt>SHA-256 fingerprint</dt><dd>{{ selectedEndpoint.caRootFingerprint }}</dd></dl>
                        <button type="button" :disabled="caBusy || !selectedEndpoint.enabled || !selectedEndpoint.caRootAvailable" @click="downloadRoot">{{ caBusy ? 'Downloading CA root…' : 'Download CA root' }}</button>
                        <p v-if="selectedEndpoint.caRootReferenceUrl"><a :href="selectedEndpoint.caRootReferenceUrl" target="_blank" rel="noopener noreferrer">Official CA root information</a></p>
                    </template>
                    <p v-if="caFeedback" role="status" class="notice">{{ caFeedback }}</p>
                </div>
            </section>
            <div id="source-plain-warning" v-show="connectionProfile === 'PlainFtpLan'" class="source-security-warning source-profile-warning" role="note"><strong>Unencrypted transfers</strong><p>Credentials and photographs travel unencrypted. Use only on a trusted local network.</p></div>
            <div id="source-legacy-warning" v-show="connectionProfile === 'LegacyFtps'" class="source-security-warning source-profile-warning" role="note"><strong>Compatibility encryption</strong><p>Required by some cameras. Transfers remain encrypted, but protection is weaker than the default and lacks forward secrecy. A later server-key compromise could expose recorded transfers. Enable only when your camera requires it.</p><p class="muted">Installation eligibility is deployment-controlled. Saving opts in for this Source; it does not qualify public compatibility hosting.</p></div>
            <div class="actions"><button class="primary" :disabled="busy || conflict || !canAssignProfile || (!source && !passwordPolicy)">{{ busy ? 'Saving…' : source ? 'Save settings' : 'Create Source' }}</button></div>
        </form>
        <p v-if="feedback" ref="feedbackNotice" role="status" class="notice" tabindex="-1">
            {{ feedback }}
            <button v-if="conflict" @click="reload">
                Reload saved settings
            </button>
        </p>
        <p v-if="copyFeedback && !secret" role="status" class="muted">{{ copyFeedback }}</p>
        <section v-if="secret" class="notice source-password-disclosure" aria-labelledby="generated-password-title">
            <div class="password-disclosure-heading">
                <h3 id="generated-password-title">Generated password</h3>
                <button type="button" class="password-hide" aria-label="Hide password" @click="secret = ''">Hide</button>
            </div>
            <p id="generated-password-help">Copy and save it now. It won’t be shown again after you hide it or leave this Source.</p>
            <div class="generated-password-row">
                <output aria-label="Generated password" aria-describedby="generated-password-help" class="generated-password">{{ secret }}</output>
                <button type="button" @click="copyConnection(secret, 'Password')"><UIcon name="i-lucide-copy" /> Copy password</button>
            </div>
            <p v-if="copyFeedback" role="status">{{ copyFeedback }}</p>
        </section>
    </section>
    <dialog ref="credentialDialog" class="discard-dialog" aria-labelledby="replace-source-credentials-title">
        <h2 id="replace-source-credentials-title">Replace credentials for {{ source?.name }}?</h2>
        <p>The old credentials stop working for every device sharing this Source. Update and reconnect each device. Media, history and connection profile stay unchanged.</p>
        <div class="actions"><button autofocus @click="credentialDialog?.close()">Keep credentials</button><button class="danger" :disabled="busy || conflict" @click="command('secret')">Confirm replacement</button></div>
    </dialog>
    <dialog ref="selectionDialog" class="discard-dialog" aria-labelledby="source-discard-title" @cancel="cancelSelection" @close="cancelSelection">
        <h2 id="source-discard-title">Discard unsaved Source settings?</h2>
        <p>Your changes have not been saved.</p>
        <div class="actions"><button autofocus @click="cancelSelection">Keep editing</button><button class="danger" @click="discardSelection">Discard changes</button></div>
    </dialog>
    <dialog ref="removalDialog" class="discard-dialog" aria-labelledby="remove-source-title">
        <h2 id="remove-source-title">Remove {{ removal?.name }}?</h2>
        <p>An unused Source is permanently deleted. A Source with history is archived and stops accepting new media; its existing Captures remain preserved.</p>
        <div class="actions"><button autofocus :disabled="busy" @click="removalDialog?.close(); removal = undefined">Cancel</button><button class="danger" :disabled="busy || conflict" @click="removeSource">Confirm removal</button></div>
    </dialog>
</template>
