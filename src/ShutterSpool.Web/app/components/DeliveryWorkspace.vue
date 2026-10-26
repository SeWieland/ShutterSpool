<script setup lang="ts">
import { createUntypedString, createUntypedNumber, createUntypedNull } from '@microsoft/kiota-abstractions';
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, session } from '../lib/session';
import { createReadPool } from '../lib/read-pool';
import type { DestinationResponse, DestinationRoutingResponse, RoutingDeliveryPageResponse, RoutingRuleDto } from '../api/models';
const props = defineProps<{
    gridMaximumDimension?: number;
    online: boolean;
    generation: number;
    motionEnabled: boolean;
    connected?: boolean;
    recoverySequence?: number;
    previewVersions?: Record<string, number>;
}>();
const destinations = ref<DestinationResponse[]>([]);
const editor = ref<HTMLElement>();
const destinationCursor = ref<string | null>();
const selected = ref('');
const selectedConfiguration = ref<DestinationResponse>();
const authoritativeOwner = ref('');
const tab = ref('Rules');
const folderPickerOpen = ref(false);
const setupStorageKey = `shutterspool.destinationSetup:${encodeURIComponent(session?.value?.profile.iss ?? '')}:${encodeURIComponent(session?.value?.profile.sub ?? '')}`;
function readIncomplete(): string[] {
    try {
        const value: unknown = typeof localStorage === 'undefined' ? [] : JSON.parse(localStorage.getItem(setupStorageKey) ?? '[]');
        return Array.isArray(value) ? value.filter((id): id is string => typeof id === 'string') : [];
    } catch { return []; }
}
const incomplete = ref<string[]>(readIncomplete());
const setupIncomplete = computed(() => incomplete.value.includes(selected.value));
function markSetup(id: string, pending: boolean) {
    incomplete.value = pending ? [...new Set([...incomplete.value, id])] : incomplete.value.filter(value => value !== id);
    try { localStorage.setItem(setupStorageKey, JSON.stringify(incomplete.value)); } catch { /* Keep setup state usable for this visit when browser storage is unavailable. */ }
}
const removalDialog = ref<HTMLDialogElement>();
const removal = ref<{ id: string; name: string; revision: number | null | undefined }>();
function requestRemoval() {
    if (!destination.value || authoritativeOwner.value !== selected.value) return;
    removal.value = { id: selected.value, name: destination.value.name ?? 'destination', revision: settingsRevision.value };
    removalDialog.value?.showModal();
}
async function removeDestination() {
    const target = removal.value;
    if (!target || busy.value || configConflict.value || !props.online) return;
    const access = accessGeneration();
    const generation = editorGeneration;
    const operationCurrent = () => access === accessGeneration() && generation === editorGeneration;
    const current = () => access === accessGeneration() && generation === editorGeneration && selected.value === target.id;
    busy.value = true;
    password.value = '';
    try {
        const result = await api.management.destinations.byId(target.id).delete({ queryParameters: { expectedRevision: target.revision ?? undefined } });
        if (access !== accessGeneration()) return;
        removalDialog.value?.close();
        removal.value = undefined;
        markSetup(target.id, false);
        if (selected.value === target.id) {
            selected.value = authoritativeOwner.value = '';
            routing.value = undefined;
            rules.value = [];
            work.value = undefined;
            dirtySettings.value = dirtyRules.value = false;
        }
        await list();
        if (!operationCurrent() || selected.value) return;
        feedback.value = result?.outcome === 'archived' ? `${target.name} archived. Existing delivery history is preserved.` : result?.outcome === 'deleted' ? `${target.name} deleted.` : 'Removal outcome unavailable. Refresh destinations to confirm their state.';
    } catch (failure) {
        if (!current()) return;
        const status = handleFailure(failure);
        removalDialog.value?.close();
        configConflict.value = status !== 400;
        feedback.value = status === 409 ? 'Destination settings changed. Reload saved settings before removing it.' : 'Removal outcome uncertain. Refresh destinations before retrying.';
    } finally { if (operationCurrent()) busy.value = false; }
}
const discardDialog = ref<HTMLDialogElement>();
let discardAction: (() => void) | undefined;
function confirmDiscard(action: () => void) {
    discardAction = action;
    discardDialog.value?.showModal();
}
function discard() {
    dirtyRules.value = dirtySettings.value = false;
    discardDialog.value?.close();
    discardAction?.();
    discardAction = undefined;
}
const destination = computed(() => String(selectedConfiguration.value?.id) === selected.value ? selectedConfiguration.value : destinations.value.find(d => String(d.id) === selected.value));
type Condition = {
    fact: string;
    operator: string;
    value: string;
};
type Rule = Omit<RoutingRuleDto, 'conditions'> & {
    conditions: Condition[];
};
const routing = ref<DestinationRoutingResponse>();
const rules = ref<Rule[]>([]);
const work = ref<RoutingDeliveryPageResponse>();
const workRecoverySequence = ref(0);
const metadataReads = createReadPool(4);
const state = ref('');
const summaries = ref<Record<string, RoutingDeliveryPageResponse>>({});
const summaryUnavailable = ref<Record<string, boolean>>({});
function confirmedCaptures(id: string) {
    return [...new Map((summaries.value[id]?.items ?? []).filter(row => row.state === 'Delivered' && row.captureId).map(row => [String(row.captureId), row])).values()].slice(0, 8);
}
const streamEnds = ref<Record<string, boolean>>({});
const suppressStreamMotion = ref(true);
watch(() => props.recoverySequence, () => { suppressStreamMotion.value = true; });
const streamElements = new Map<string, HTMLElement>();
const streamObservers = new Map<string, ResizeObserver>();
function measureStream(id: string) {
    const element = streamElements.get(id);
    if (element) streamEnds.value[id] = (element.querySelector('.recent-stream')?.scrollWidth ?? 0) > element.clientWidth;
}
function observeStream(element: unknown, id: string) {
    if (!(element instanceof HTMLElement)) {
        streamObservers.get(id)?.disconnect();
        streamObservers.delete(id);
        streamElements.delete(id);
        return;
    }
    if (streamObservers.has(id)) return;
    streamElements.set(id, element);
    const observer = new ResizeObserver(() => { void nextTick(() => measureStream(id)); });
    observer.observe(element);
    streamObservers.set(id, observer);
}
const dirtyName = ref(false);
const dirtyProvider = ref(false);
const localFolderNeedsSave = computed(() => destination.value?.kind === 'local' && (dirtyProvider.value || !destination.value.localRootPath));
const dirtySettings = computed({
    get: () => dirtyName.value || dirtyProvider.value,
    set: value => { dirtyName.value = dirtyProvider.value = value; },
});
const settingsRevision = ref<number | null>();
const lanes = [{ name: 'Queued', state: 'Pending' }, { name: 'Running', state: 'Running' }, { name: 'Confirmed', state: 'Delivered' }];
const name = ref('');
const kind = ref('local');
const root = ref('');
const url = ref('');
const username = ref('');
const password = ref('');
const busy = ref(false);
const feedback = ref('');
const configConflict = ref(false);
const dirtyRules = ref(false);
const dirty = computed(() => dirtySettings.value || dirtyRules.value);
const emit = defineEmits<{ 'dirty-change': [dirty: boolean] }>();
watch(dirty, value => emit('dirty-change', value), { immediate: true });
const ruleConflict = ref(false);
let sequence = 0;
let editorGeneration = 0;
let listSequence = 0;
let workSequence = 0;
let disposed = false;
let listController: AbortController | undefined;
let editorController: AbortController | undefined;
let workController: AbortController | undefined;
const facts = ['collectionName', 'collectionPath', 'source', 'selection', 'variant', 'extension', 'mime', 'suppliedName', 'cameraMake', 'cameraModel', 'cameraSerial', 'rating', 'size', 'tags', 'label', 'captureTime'];
const tokens = [...facts, 'year', 'month', 'day', 'monthName', 'weekdayName'];
const ratingOptions = [-1,0,1,2,3,4,5].map(value => ({ value: String(value), label: value === -1 ? 'Rejected' : value === 0 ? 'Not rated' : `${value} stars` }));
const knownValues: Record<string, string[]> = { selection: ['Rejected', 'Neutral', 'Picked'], variant: ['RAW', 'JPEG', 'OTHER'], label: ['Red', 'Yellow', 'Green', 'Blue', 'Purple'] };
const exampleFacts: Record<string, string> = { collectionName: 'Berlin', collectionPath: 'Events/Berlin', source: 'Camera', selection: 'Picked', variant: 'JPEG', extension: 'jpg', mime: 'image/jpeg', suppliedName: 'IMG_0001.jpg', cameraMake: 'Example', cameraModel: 'Camera', cameraSerial: '000001', rating: '3', size: '2400000', tags: 'event+portrait', label: 'Blue', captureTime: '2026-10-11T12-30-00-0000000', year: '2026', month: '10', day: '11', monthName: 'October', weekdayName: 'Sunday' };
function pathExample(rule: Rule) {
    const render = (template: string) => template.replace(/\{([A-Za-z][A-Za-z0-9]*)\}/g, (_, fact: string) => exampleFacts[fact] ?? `[unknown: ${fact}]`);
    const base = destination.value?.kind === 'local' ? root.value : url.value;
    const relative = [render(rule.folderTemplate ?? ''), render(rule.filenameTemplate ?? '')].filter(Boolean).join('/');
    return base ? `${base.replace(/[/\\]+$/, '')}/${relative}` : `(root not configured)/${relative}`;
}
function loadedStatus(id: string) {
    if (summaryUnavailable.value[id] || props.connected === false) return { icon: 'i-lucide-info', state: 'neutral', text: 'Recent work is unavailable or last known; reconnect or refresh to verify' };
    const rows = summaries.value[id]?.items;
    if (!rows) return { icon: 'i-lucide-info', state: 'neutral', text: 'Work status unavailable' };
    if (['Failed', 'RetryWait'].includes(rows[0]?.state ?? '')) return { icon: 'i-lucide-x', state: 'error', text: 'Latest recent delivery failed or is waiting to retry' };
    if (rows.some(row => ['RetryWait', 'Failed', 'Ambiguous', 'Disabled'].includes(row.state ?? ''))) return { icon: 'i-lucide-triangle-alert', state: 'warning', text: 'Recent deliveries need attention' };
    if (rows.length && rows.every(row => row.state === 'Delivered')) return { icon: 'i-lucide-check', state: 'success', text: 'Recent deliveries confirmed; open Work for older history' };
    return { icon: 'i-lucide-info', state: 'neutral', text: rows.length ? 'Recent deliveries are still in progress' : 'No recent deliveries' };
}
const operators = (fact: string) => ['rating', 'size', 'captureTime'].includes(fact) ? ['equals', 'gt', 'gte', 'lt', 'lte'] : fact === 'label' ? ['equals', 'present', 'absent'] : fact === 'tags' ? ['contains'] : fact === 'suppliedName' ? ['equals', 'glob'] : ['equals'];
let listPending: boolean | undefined;
let listing: Promise<void> | undefined;
function list(next = false): Promise<void> {
    listPending = next;
    return listing ??= (async () => {
        try {
            while (listPending !== undefined) {
                const page = listPending;
                listPending = undefined;
                await readList(page);
            }
        } finally { listing = undefined; if (!disposed) { await nextTick(); if (!disposed && !listing) suppressStreamMotion.value = false; } }
    })();
}
async function readList(next: boolean) {
    const request = ++listSequence;
    const access = accessGeneration();
    listController?.abort();
    const controller = listController = new AbortController();
    try {
        const page = await metadataReads.run(controller.signal, () => api.management.destinations.get({ queryParameters: { limit: 30, cursor: next ? destinationCursor.value ?? undefined : undefined }, options: [cancellable(controller.signal)] }));
        if (disposed || controller.signal.aborted || request !== listSequence || access !== accessGeneration())
            return;
        destinations.value = page?.items ?? [];
        const visibleIds = new Set(destinations.value.map(item => String(item.id)));
        summaries.value = Object.fromEntries(Object.entries(summaries.value).filter(([id]) => visibleIds.has(id)));
        summaryUnavailable.value = Object.fromEntries(Object.entries(summaryUnavailable.value).filter(([id]) => visibleIds.has(id)));
        destinationCursor.value = page?.nextCursor ?? null;
        const summaryRows = destinations.value.slice(0, 6);
        await Promise.all(summaryRows.map(async destination => {
            const id = String(destination.id);
            try {
                const bounded = await metadataReads.run(controller.signal, () => api.management.destinations.byId(id).work.get({ queryParameters: { limit: 30 }, options: [cancellable(controller.signal)] }));
                if (disposed || controller.signal.aborted || request !== listSequence || access !== accessGeneration()) return;
                summaryUnavailable.value[id] = !bounded;
                if (bounded) summaries.value[id] = bounded;
                void nextTick(() => measureStream(id));
            } catch (error) {
                if (disposed || controller.signal.aborted || request !== listSequence || access !== accessGeneration()) return;
                handleFailure(error);
                summaryUnavailable.value[id] = true;
            }
        }));
        if (disposed || controller.signal.aborted || request !== listSequence || access !== accessGeneration()) return;
    }
    catch (error) {
        if (disposed || controller.signal.aborted || request !== listSequence || access !== accessGeneration()) return;
        handleFailure(error);
        for (const id of Object.keys(summaries.value)) summaryUnavailable.value[id] = true;
        feedback.value = 'Unable to read destinations. Refresh to retry.';
    }
}
async function open(id: string) {
    if (dirtyRules.value || dirtySettings.value) {
        confirmDiscard(() => { void open(id); });
        return;
    }
    editorGeneration++;
    sequence++;
    busy.value = false;
    editorController?.abort();
    authoritativeOwner.value = '';
    selectedConfiguration.value = undefined;
    routing.value = undefined;
    rules.value = [];
    work.value = undefined;
    state.value = '';
    settingsRevision.value = undefined;
    name.value = root.value = url.value = username.value = '';
    selected.value = id;
    tab.value = incomplete.value.includes(id) ? 'Settings' : 'Rules';
    dirtyRules.value = false;
    dirtySettings.value = false;
    ruleConflict.value = false;
    configConflict.value = false;
    feedback.value = '';
    password.value = '';
    await nextTick();
    if (selected.value === id) editor.value?.scrollIntoView({ block: 'start', behavior: props.motionEnabled ? 'smooth' : 'auto' });
    await read();
}
function assignRules(value: DestinationRoutingResponse) {
    rules.value = (value.rules ?? []).map(rule => ({ ...rule, conditions: (rule.conditions ?? []).map(condition => ({ fact: condition.fact ?? 'variant', operator: condition.operator ?? 'equals', value: String(condition.value?.getValue() ?? '') })) }));
}
async function read(replaceDraft: 'settings' | 'rules' | 'all' | 'none' = 'none') {
    if (!selected.value)
        return;
    const request = ++sequence;
    const id = selected.value;
    const access = accessGeneration();
    editorController?.abort();
    const controller = editorController = new AbortController();
    try {
        const endpoint = api.management.destinations.byId(id);
        const options = { options: [cancellable(controller.signal)] };
        const [config, route] = await Promise.all([
            metadataReads.run(controller.signal, () => endpoint.get(options)),
            metadataReads.run(controller.signal, () => endpoint.routing.get(options)),
        ]);
        if (disposed || controller.signal.aborted || request !== sequence || access !== accessGeneration() || id !== selected.value)
            return;
        if (String(config?.id) !== id || String(route?.destinationId) !== id)
            throw new Error('Destination identity was not confirmed');
        const replaceSettings = replaceDraft === 'settings' || replaceDraft === 'all';
        const replaceRules = replaceDraft === 'rules' || replaceDraft === 'all';
        if (replaceSettings) {
            dirtySettings.value = false;
            configConflict.value = false;
            password.value = '';
        }
        if (replaceRules) {
            dirtyRules.value = false;
            ruleConflict.value = false;
        }
        if (config) {
            selectedConfiguration.value = config;
            const index = destinations.value.findIndex(d => d.id === config.id);
            if (index >= 0)
                destinations.value[index] = config;
            else
                destinations.value.push(config);
            if (dirtySettings.value && settingsRevision.value !== config.configurationRevision)
                configConflict.value = true;
            if (replaceSettings || !dirtyName.value)
                name.value = config.name ?? '';
            if (replaceSettings || !dirtyProvider.value) {
                kind.value = config.kind ?? 'local';
                root.value = config.localRootPath ?? '';
                url.value = config.webDavCollectionUrl ?? '';
                username.value = config.webDavUsername ?? '';
            }
            if (replaceSettings || !dirtySettings.value) {
                settingsRevision.value = config.configurationRevision;
            }
        }
        if (route) {
            if (dirtyRules.value && route.configurationRevision !== routing.value?.configurationRevision)
                ruleConflict.value = true;
            else if (replaceRules || !dirtyRules.value)
                assignRules(route);
            routing.value = route;
        }
        authoritativeOwner.value = id;
        if (tab.value === 'Work')
            await readWork();
        return true;
    }
    catch (error) {
        if (disposed || controller.signal.aborted || request !== sequence || access !== accessGeneration() || id !== selected.value) return false;
        handleFailure(error);
        feedback.value = 'Unable to refresh destination. Displayed state may be stale.';
        return false;
    }
}
async function reloadSettings() {
    if (busy.value || !props.online || !selected.value || authoritativeOwner.value !== selected.value) return false;
    const id = selected.value;
    const generation = editorGeneration;
    busy.value = true;
    try {
        return await read('settings') === true && id === selected.value && generation === editorGeneration;
    } finally {
        if (id === selected.value && generation === editorGeneration) busy.value = false;
    }
}
async function reloadRules() {
    if (busy.value || !props.online || !selected.value || authoritativeOwner.value !== selected.value) return false;
    const id = selected.value;
    const generation = editorGeneration;
    busy.value = true;
    try {
        return await read('rules') === true && id === selected.value && generation === editorGeneration;
    } finally {
        if (id === selected.value && generation === editorGeneration) busy.value = false;
    }
}
async function replaceRulesWithDraft() {
    if (busy.value || !props.online || !ruleConflict.value || !dirtyRules.value || !selected.value || authoritativeOwner.value !== selected.value) return false;
    const id = selected.value;
    const generation = editorGeneration;
    busy.value = true;
    try {
        const refreshed = await read('none');
        if (refreshed !== true || id !== selected.value || generation !== editorGeneration || authoritativeOwner.value !== id) return false;
        ruleConflict.value = false;
        return true;
    } finally {
        if (id === selected.value && generation === editorGeneration) busy.value = false;
    }
}
async function readWork(next = false) {
    if (!selected.value || authoritativeOwner.value !== selected.value)
        return;
    const access = accessGeneration();
    const request = ++workSequence;
    const id = selected.value;
    const filter = state.value;
    workController?.abort();
    const controller = workController = new AbortController();
    try {
        const page = await metadataReads.run(controller.signal, () => api.management.destinations.byId(id).work.get({ queryParameters: { state: filter || undefined, limit: 30, cursor: next ? work.value?.nextCursor ?? undefined : undefined }, options: [cancellable(controller.signal)] }));
        if (!disposed && !controller.signal.aborted && request === workSequence && access === accessGeneration() && id === selected.value && filter === state.value)
            { work.value = page; workRecoverySequence.value++; }
    }
    catch (error) {
        if (disposed || controller.signal.aborted || request !== workSequence || id !== selected.value || filter !== state.value || access !== accessGeneration()) return;
        handleFailure(error);
        feedback.value = 'Unable to read this destination work page. Refresh to retry.';
    }
}
watch(state, () => readWork());
watch(tab, value => {
    if (value === 'Work')
        void readWork();
});
onMounted(() => list());
watch(() => props.generation, () => {
    void list();
    if (selected.value)
        void read();
});
onUnmounted(() => {
    disposed = true;
    streamObservers.forEach(observer => observer.disconnect());
    sequence++;
    editorGeneration++;
    listSequence++;
    workSequence++;
    listController?.abort();
    editorController?.abort();
    workController?.abort();
    password.value = '';
    listPending = undefined;
    destinations.value = [];
    summaries.value = {};
    work.value = undefined;
    routing.value = undefined;
});
function addRule() {
    if (rules.value.length >= 128)
        return;
    rules.value.push({ id: crypto.randomUUID(), name: 'New rule', order: rules.value.length, conditionMode: 0, negate: false, required: true, folderTemplate: '', filenameTemplate: '{suppliedName}', conditions: [] });
    dirtyRules.value = true;
}
function move(index: number, delta: number) {
    const target = index + delta;
    if (target < 0 || target >= rules.value.length)
        return;
    const [rule] = rules.value.splice(index, 1);
    if (rule)
        rules.value.splice(target, 0, rule);
    dirtyRules.value = true;
}
let dragged = -1;
function drop(index: number) {
    if (dragged < 0 || dragged === index)
        return;
    const [rule] = rules.value.splice(dragged, 1);
    if (rule)
        rules.value.splice(index, 0, rule);
    dirtyRules.value = true;
    dragged = -1;
}
function conditionValue(condition: Condition) {
    if (['present', 'absent'].includes(condition.operator))
        return createUntypedNull();
    if (['rating', 'size'].includes(condition.fact))
        return createUntypedNumber(Number(condition.value));
    return createUntypedString(condition.value);
}
async function saveRules() {
    if (busy.value || setupIncomplete.value || ruleConflict.value || !props.online || authoritativeOwner.value !== selected.value)
        return;
    const access = accessGeneration();
    const id = selected.value;
    const generation = editorGeneration;
    busy.value = true;
    try {
        const body = { expectedRevision: routing.value?.configurationRevision, enabled: routing.value?.enabled, rules: rules.value.map((rule, index) => ({ ...rule, order: index, conditions: rule.conditions.map(condition => ({ fact: condition.fact, operator: condition.operator, value: conditionValue(condition) })) })) };
        const value = await api.management.destinations.byId(id).routing.put(body);
        if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value)
            return;
        if (value)
            routing.value = value;
        dirtyRules.value = false;
        feedback.value = 'Rules saved. First matching rule wins.';
    }
    catch (error) {
        if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value) return;
        const status = handleFailure(error);
        ruleConflict.value = status !== 400;
        feedback.value = status === 400 ? 'Rules rejected. Check typed conditions and safe relative templates.' : 'Rule outcome requires review. Refresh server values before explicitly replacing rules.';
    }
    finally {
        if (generation === editorGeneration && id === selected.value && access === accessGeneration()) busy.value = false;
    }
}
async function settings(action: 'create' | 'name' | 'provider' | 'toggle' | 'test') {
    if (busy.value || configConflict.value || !props.online || action !== 'create' && authoritativeOwner.value !== selected.value)
        return;
    if ((action === 'toggle' && (dirtyRules.value || ruleConflict.value)) || (setupIncomplete.value && ruleConflict.value && (action === 'provider' || action === 'test')))
        return;
    if (action === 'test' && localFolderNeedsSave.value) {
        feedback.value = 'Choose a destination folder, then use Save and test folder.';
        return;
    }
    const access = accessGeneration();
    const id = selected.value;
    const generation = editorGeneration;
    let ownsEditor = () => access === accessGeneration() && generation === editorGeneration && id === selected.value;
    busy.value = true;
    feedback.value = '';
    try {
        if (action === 'create') {
            const value = await api.management.destinations.post({ name: name.value, kind: kind.value });
            if (access !== accessGeneration())
                return;
            if (value?.id) {
                markSetup(String(value.id), true);
                if (!ownsEditor()) { await list(); return; }
                dirtyName.value = dirtyProvider.value = false;
                await open(String(value.id));
                const createdId = String(value.id);
                const createdGeneration = editorGeneration;
                ownsEditor = () => access === accessGeneration() && createdGeneration === editorGeneration && createdId === selected.value;
                tab.value = 'Settings';
                if (routing.value?.enabled) await ensureSetupDisabled();
                await list();
            }
            if (access !== accessGeneration() || selected.value !== String(value?.id ?? '')) return;
            feedback.value = 'Save provider settings and pass their test to complete setup.';
        }
        else {
            if (setupIncomplete.value) {
                await ensureSetupDisabled();
                if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value) return;
            }
            const endpoint = api.management.destinations.byId(id);
            const expectedRevision = settingsRevision.value;
            if (action === 'name') {
                const value = await endpoint.put({ name: name.value, expectedRevision });
                if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value) return;
                if (value?.configurationRevision != null)
                    settingsRevision.value = value.configurationRevision;
                dirtyName.value = false;
            }
            if (action === 'provider') {
                let value;
                if (destination.value?.kind === 'local')
                    value = await endpoint.local.put({ rootPath: root.value, expectedRevision });
                else
                    value = await endpoint.webdav.put({ collectionUrl: url.value, username: username.value, password: password.value || null, expectedRevision });
                if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value) return;
                if (value?.configurationRevision != null)
                    settingsRevision.value = value.configurationRevision;
                dirtyProvider.value = false;
                password.value = '';
            }
            if (action === 'toggle') {
                if (setupIncomplete.value) return;
                const value = await endpoint.routing.put({ expectedRevision: routing.value?.configurationRevision, rules: routing.value?.rules ?? [], enabled: !routing.value?.enabled });
                if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value) return;
                if (value) routing.value = value;
                if (value?.configurationRevision != null)
                    settingsRevision.value = value.configurationRevision;
            }
            if (action === 'test') {
                const result = destination.value?.kind === 'local' ? await endpoint.local.test.post() : await endpoint.webdav.test.post();
                if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value)
                    return;
                const refreshed = await read();
                if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value)
                    return;
                const currentRevisionTested = refreshed && authoritativeOwner.value === id
                    && result?.testedConfigurationRevision != null
                    && result?.testedConfigurationRevision === settingsRevision.value
                    && result?.testedConfigurationRevision === routing.value?.configurationRevision;
                if (result?.writable && currentRevisionTested && routing.value?.enabled === false && !dirtySettings.value && !configConflict.value)
                    markSetup(id, false);
                feedback.value = result?.errorCode === 'LOCAL_ROOT_NOT_CONFIGURED' ? 'No destination folder is saved. Choose a folder, then use Save and test folder.'
                    : !result?.writable ? `Saved settings test failed: ${result?.errorCode ?? 'unavailable'}. Check the destination configuration.`
                    : !currentRevisionTested ? `Saved revision ${result.testedConfigurationRevision} is writable. Refresh and test the current revision before completing setup.`
                    : `Saved revision ${result.testedConfigurationRevision} is writable.`;
            }
            else {
                feedback.value = 'Saved settings updated.';
            }
            if (action !== 'test' && access === accessGeneration() && generation === editorGeneration && id === selected.value)
                await read();
        }
    }
    catch (error) {
        if (access !== accessGeneration()) return;
        if (!ownsEditor()) { if (action === 'create') void list(); return; }
        const status = handleFailure(error);
        configConflict.value = status !== 400;
        password.value = '';
        const detail = (error as { detail?: string })?.detail;
        feedback.value = (status === 400 || status === 409) && detail ? detail
            : status === 400 ? 'Settings rejected. Check the supplied values.' : 'Settings outcome requires review. Reload saved settings before retrying.';
    }
    finally {
        if (ownsEditor()) busy.value = false;
    }
}
async function ensureSetupDisabled() {
    if (routing.value?.enabled === false) return;
    if (authoritativeOwner.value !== selected.value || !routing.value) throw new Error('Setup destination state is unavailable');
    const id = selected.value;
    const access = accessGeneration();
    const generation = editorGeneration;
    const value = await api.management.destinations.byId(id).routing.put({ expectedRevision: routing.value.configurationRevision, rules: routing.value.rules ?? [], enabled: false });
    if (access !== accessGeneration() || generation !== editorGeneration || id !== selected.value) return;
    if (String(value?.destinationId) !== id || value?.enabled !== false || value.configurationRevision == null) throw new Error('Destination disable was not confirmed');
    routing.value = value;
    settingsRevision.value = value.configurationRevision;
    const config = destinations.value.find(item => String(item.id) === id);
    if (config) {
        config.enabled = false;
        config.configurationRevision = value.configurationRevision;
    }
}
async function saveAndTest() {
    const id = selected.value;
    const access = accessGeneration();
    const generation = editorGeneration;
    dirtyProvider.value = true;
    await settings('provider');
    if (access === accessGeneration() && generation === editorGeneration && id === selected.value && !dirtyProvider.value && !configConflict.value) await settings('test');
}
async function attention(id: string) {
    await open(id);
    if (authoritativeOwner.value !== id)
        return;
    tab.value = 'Work';
    state.value = '';
    await readWork();
    await nextTick();
    const affected = work.value?.items?.find(row => ['Failed', 'Ambiguous', 'Disabled'].includes(row.state ?? ''));
    if (affected)
        document.querySelector<HTMLElement>('[data-work-id="' + String(affected.id) + '"]')?.focus();
}
function close() {
    if (dirtyRules.value || dirtySettings.value) {
        confirmDiscard(close);
        return;
    }
    editorGeneration++;
    sequence++;
    editorController?.abort();
    workController?.abort();
    selected.value = '';
    selectedConfiguration.value = undefined;
    password.value = '';
    dirtyRules.value = false;
    dirtySettings.value = false;
    name.value = root.value = url.value = username.value = '';
    kind.value = 'local';
}
</script>

<template>
        <div v-if="!destinations.length" class="empty">
            <h2>
                Add a destination
            </h2>
            <p>
                Configure local or mounted storage, or WebDAV / Nextcloud, then define its rules.
            </p>
        </div>
        <article
            v-for="item in destinations.filter(value => !incomplete.includes(String(value.id)))"
            :key="String(item.id)"
            class="source-stream destination-row"
        >
            <button class="source-summary destination-open" :aria-pressed="selected === String(item.id)" @click="open(String(item.id))">
                <div class="source-icon">
                    <UIcon :name="item.kind === 'webdav' ? 'i-lucide-cloud' : 'i-lucide-hard-drive'" />
                    <span class="source-auth destination-status" :class="`status-${loadedStatus(String(item.id)).state}`" :title="loadedStatus(String(item.id)).text" :aria-label="loadedStatus(String(item.id)).text"><UIcon :name="loadedStatus(String(item.id)).icon" /></span>
                </div>
                <div class="source-copy">
                    <h2>
                        {{ item.name }}
                    </h2>
                    <span>
                        {{ item.kind === 'webdav' ? 'WebDAV' : 'Local storage' }}
                        ·
                        {{ item.enabled ? 'Enabled' : 'Paused' }}
                    </span>
                </div>
            </button>
            <div class="delivery-activity flow-connection" :class="{ running: motionEnabled && !summaryUnavailable[String(item.id)] && summaries[String(item.id)]?.items?.some(row => row.state === 'Running') }">
                <div class="flow-line">
                    <span v-for="n in 3" :key="n" class="flow-dot" aria-hidden="true" />
                    <button v-if="summaries[String(item.id)]?.items?.some(row => ['Failed','Ambiguous','Disabled'].includes(row.state ?? ''))" class="attention-link icon-button" aria-label="Needs attention" title="Needs attention" @click="attention(String(item.id))"><UIcon name="i-lucide-triangle-alert" /></button>
                </div>
                <small v-if="summaryUnavailable[String(item.id)] || !connected">Last known work · updates unavailable</small>
                <small v-else-if="summaries[String(item.id)]" class="recent-work-counts" title="Recent work; open Work for older history">
                    <span>{{ summaries[String(item.id)]?.items?.filter(row => row.state === 'Pending').length ?? 0 }} Queued</span>
                    <span>{{ summaries[String(item.id)]?.items?.filter(row => row.state === 'Running').length ?? 0 }} Running</span>
                    <span v-if="summaries[String(item.id)]?.items?.some(row => row.state === 'RetryWait')">{{ summaries[String(item.id)]?.items?.filter(row => row.state === 'RetryWait').length }} Scheduled for Retry</span>
                </small>
                <small v-else>Work status unavailable</small>
            </div>
            <div :ref="element => observeStream(element, String(item.id))" class="recent-stream-frame" :class="{ 'at-stream-end': streamEnds[String(item.id)] }" aria-label="Captures with confirmed delivery work">
                <TransitionGroup :name="motionEnabled && !suppressStreamMotion ? 'stream' : undefined" tag="div" class="recent-stream">
                    <AuthorizedPreview :maximum-dimension="gridMaximumDimension" v-for="row in confirmedCaptures(String(item.id))" :key="String(row.captureId)" :capture-id="String(row.captureId)" :force-generation="previewVersions?.[String(row.captureId)] ?? 0" profile="thumbnail" name="Capture with confirmed delivery work" />
                </TransitionGroup>
            </div>
        </article>
        <button v-if="destinationCursor" @click="list(true)">
            Older destinations
        </button>
        <button v-for="id in incomplete" :key="id" @click="open(id)">Continue destination setup</button>
        <section v-if="!selected" class="settings-panel panel">
            <h2>
                New destination
            </h2>
            <form @submit.prevent="settings('create')">
                <div class="fields">
                    <label class="field">
                        Name
                        <input v-model="name" maxlength="200" required @input="dirtyName = true" />
                    </label>
                    <label class="field">
                        Provider
                        <select v-model="kind" @change="dirtyProvider = true">
                            <option value="local">
                                Local / mounted storage
                            </option>
                            <option value="webdav">
                                WebDAV / Nextcloud
                            </option>
                        </select>
                    </label>
                </div>
                <button class="primary" :disabled="busy">
                    Create destination
                </button>
            </form>
        </section>
    <section v-else ref="editor" class="settings-panel panel destination-editor" aria-label="Destination editor">
        <div class="toolbar">
            <h2>
                {{ destination?.name }}
                delivery
                {{ tab.toLowerCase() }}
            </h2>
        </div>
        <p v-if="authoritativeOwner !== selected" role="status">Destination details are unavailable until their identity is confirmed. <button @click="read()">Retry read</button></p>
        <nav class="tabs destination-tabs" aria-label="Destination workspace">
            <button
                v-for="item in ['Rules','Work','Settings']"
                :key="item"
                :aria-current="tab === item ? 'page' : undefined"
                :disabled="authoritativeOwner !== selected || setupIncomplete && item !== 'Settings'"
                @click="tab = item"
            >
                {{ item }}
            </button>
            <button class="destination-return" aria-label="New destination" title="New destination" @click="close"><UIcon name="i-lucide-plus" /></button>
        </nav>
        <section v-if="authoritativeOwner === selected && tab === 'Rules'" class="rules-workspace">
            <p class="muted">
                Ordered, first-match rules. Targets remain under the configured destination root.
            </p>
            <p v-if="ruleConflict" class="notice error">
                Server rules changed. Your draft is retained.
                <button :disabled="busy || !online" @click="reloadRules()">
                    Use server rules
                </button>
                <button :disabled="busy || !online" @click="replaceRulesWithDraft()">
                    Explicitly replace with my draft
                </button>
            </p>
            <div class="rule-list" @input="dirtyRules = true" @change="dirtyRules = true">
                <details
                    v-for="(rule,index) in rules"
                    :key="rule.id ?? index"
                    class="section-disclosure rule"
                    @dragover.prevent
                    @drop.prevent="drop(index)"
                >
                    <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />
                        <span class="drag-handle" draggable="true" @dragstart="dragged = index" aria-label="Drag rule to reorder">
                            <UIcon name="i-lucide-grip-vertical" />
                        </span>
                        <span class="rule-title">
                            {{ index + 1 }}
                            .
                            {{ rule.name }}
                        </span>
                        <span class="muted">
                            {{ rule.conditionMode === 1 ? 'OR' : 'AND' }}
                            ·
                            {{ rule.required ? 'Required' : 'Optional' }}
                        </span>
                    </summary>
                    <div class="rule-content">
                        <div class="fields">
                            <label class="field">
                                Rule name
                                <input v-model="rule.name" maxlength="128" required />
                            </label>
                            <label class="field">
                                Match
                                <select v-model.number="rule.conditionMode">
                                    <option :value="0">
                                        All conditions (AND)
                                    </option>
                                    <option :value="1">
                                        Any condition (OR)
                                    </option>
                                </select>
                            </label>
                            <label class="check">
                                <input type="checkbox" v-model="rule.required" />
                                Required delivery
                            </label>
                            <label class="check">
                                <input type="checkbox" v-model="rule.negate" />
                                Negate rule
                            </label>
                        </div>
                        <p v-if="rule.negate" class="notice error">
                            Missing or uncertain facts can make a negated rule match and route an Asset.
                        </p>
                        <div v-for="(condition,i) in rule.conditions" :key="i" class="condition-row">
                            <label>
                                Fact
                                <select
                                    v-model="condition.fact"
                                    @change="condition.operator = operators(condition.fact)[0] ?? 'equals'; condition.value = ''"
                                >
                                    <option v-for="fact in facts" :key="fact">
                                        {{ fact }}
                                    </option>
                                </select>
                            </label>
                            <label>
                                Condition
                                <select v-model="condition.operator">
                                    <option v-for="operator in operators(condition.fact)" :key="operator">
                                        {{ operator }}
                                    </option>
                                </select>
                            </label>
                            <label v-if="!['present','absent'].includes(condition.operator)">
                                Value
                                <select v-if="knownValues[condition.fact]" v-model="condition.value">
                                    <option value="" disabled>Choose a value</option>
                                    <option v-for="value in knownValues[condition.fact]" :key="value">{{ value }}</option>
                                </select>
                                <USelectMenu v-else-if="condition.fact === 'rating'" v-model="condition.value" :items="ratingOptions" value-key="value" :search-input="false" aria-label="Rule rating" @update:model-value="dirtyRules = true">
                                    <CaptureRating v-if="condition.value !== ''" :rating="Number(condition.value)" /><span v-else>Choose rating</span>
                                    <template #item-label="{ item }"><CaptureRating :rating="Number(item.value)" /><span class="sr-only">{{ item.label }}</span></template>
                                </USelectMenu>
                                <input
                                    v-else
                                    v-model="condition.value"
                                    :type="['rating','size'].includes(condition.fact) ? 'number' : condition.fact === 'captureTime' ? 'datetime-local' : 'text'"
                                />
                            </label>
                            <button @click="rule.conditions.splice(i,1); dirtyRules = true">
                                Remove condition
                            </button>
                        </div>
                        <button
                            :disabled="rule.conditions.length >= 32"
                            @click="rule.conditions.push({ fact: 'variant', operator: 'equals', value: '' }); dirtyRules = true"
                        >
                            Add condition
                        </button>
                        <div class="fields">
                            <label class="field">
                                Folder template
                                <input v-model="rule.folderTemplate" maxlength="1024" />
                            </label>
                            <label class="field">
                                Filename template
                                <input v-model="rule.filenameTemplate" maxlength="255" required />
                            </label>
                        </div>
                        <p class="muted">Collection name is the final segment (Berlin); Collection path is the full relative name (Events/Berlin), with no leading slash. {collectionName} works in folders and filenames; {collectionPath} is folder-only. Renames affect future text matches. Retry and Resend keep recorded targets; Reevaluate uses current membership.</p>
                        <p class="rule-example"><strong>Illustrative path</strong><br />{{ pathExample(rule) }}<br /><small>Synthetic facts; actual routing validates facts and safe targets.</small></p>
                        <details class="section-disclosure">
                            <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />
                                Known template tokens
                            </summary>
                            <div class="token-list">
                                <button
                                    v-for="fact in tokens"
                                    :key="fact"
                                    @click="rule.folderTemplate = `${rule.folderTemplate ?? ''}{${fact}}`; dirtyRules = true"
                                >
                                    {{ '{' + fact + '}' }}
                                </button>
                            </div>
                            <p>
                                Missing facts and unsafe relative targets are rejected during authoritative routing.
                            </p>
                        </details>
                        <div class="actions">
                            <button :disabled="index === 0" @click="move(index,-1)">
                                Move up
                            </button>
                            <button :disabled="index === rules.length - 1" @click="move(index,1)">
                                Move down
                            </button>
                            <button @click="rules.splice(index,1); dirtyRules = true">
                                Remove rule
                            </button>
                        </div>
                    </div>
                </details>
            </div>
            <div class="actions">
                <button :disabled="rules.length >= 128" @click="addRule">
                    Add rule
                </button>
                <button class="primary" :disabled="busy || !dirtyRules || ruleConflict || !online" @click="saveRules">
                    Save rules
                </button>
                <button :disabled="busy || !online" @click="reloadRules()">
                    Reload saved rules
                </button>
            </div>
        </section>
        <section v-if="tab === 'Work'">
            <div class="toolbar">
                <label>
                    State
                    <select v-model="state">
                        <option value="">
                            All states
                        </option>
                        <option
                            v-for="value in ['Pending','Running','Delivered','RetryWait','Failed','Ambiguous','Disabled','Superseded']"
                            :key="value"
                        >
                            {{ value }}
                        </option>
                    </select>
                </label>
                <button v-if="!connected" @click="readWork()">
                    Refresh work
                </button>
            </div>
            <p class="muted">
                Current bounded page ·
                {{ work?.items?.length ?? 0 }}
                work rows, not destination-wide totals.
            </p>
            <div class="work-lanes">
                <div v-for="lane in lanes" :key="lane.name">
                    <h3>
                        {{ lane.name }}
                    </h3>
                    <p>
                        {{ work?.items?.filter(item => item.state === lane.state).length ?? 0 }}
                        on this page
                    </p>
                </div>
            </div>
            <div v-for="item in work?.items" :key="String(item.id)" :data-work-id="String(item.id)" tabindex="-1">
                <DeliveryWork :work="item" :online="online" :recovery-sequence="workRecoverySequence" @refresh="readWork()" />
            </div>
            <p v-if="work && !work.items?.length" class="empty">
                No work in this bounded page.
            </p>
            <button v-if="work?.nextCursor" @click="readWork(true)">
                Older work
            </button>
        </section>
        <section v-if="tab === 'Settings'" class="destination-settings">
            <form @input="dirtyName = true" @submit.prevent="settings('name')">
                <label class="field">
                    Name
                    <input v-model="name" maxlength="200" required />
                </label>
                <button :disabled="busy || configConflict">
                    Save name
                </button>
            </form>
            <form @input="dirtyProvider = true" @submit.prevent="destination?.kind === 'local' || setupIncomplete ? saveAndTest() : settings('provider')">
                <template v-if="destination?.kind === 'local'">
                    <label class="field">
                        Destination folder
                        <input v-model="root" required maxlength="2048" :aria-describedby="feedback ? 'local-folder-help local-folder-feedback' : 'local-folder-help'" />
                    </label>
                    <button type="button" :disabled="busy || configConflict || !online" @click="folderPickerOpen = true"><UIcon name="i-lucide-folder-open" /> Browse…</button>
                    <p id="local-folder-help" class="muted">Use an existing, writable folder on the machine running ShutterSpool. Enter its full path or choose Browse, then Save and test folder.</p>
                    <p v-if="feedback" id="local-folder-feedback" role="status" class="notice">
                        {{ feedback }}
                <button v-if="configConflict" type="button" :disabled="busy || !online" @click="reloadSettings()">Reload saved settings</button>
                    </p>
                </template>
                <template v-else>
                    <label class="field">
                        WebDAV collection URL
                        <input v-model="url" type="url" required />
                    </label>
                    <label class="field">
                        Username
                        <input v-model="username" autocomplete="off" />
                    </label>
                    <label class="field">
                        Replace secret
                        <input
                            v-model="password"
                            type="password"
                            autocomplete="new-password"
                            placeholder="Leave blank to preserve the saved secret"
                        />
                    </label>
                </template>
                <div class="actions">
                    <button :disabled="busy || configConflict">
                        {{ destination?.kind === 'local' ? 'Save and test folder' : setupIncomplete ? 'Save and test provider settings' : 'Save provider settings' }}
                    </button>
                    <button type="button" :disabled="busy || configConflict || localFolderNeedsSave" @click="settings('test')">
                        Test saved settings
                    </button>
                </div>
            </form>
            <div class="actions">
                <button :disabled="busy || configConflict || dirtyRules || setupIncomplete" @click="settings('toggle')">
                    {{ routing?.enabled ? 'Disable destination' : 'Enable destination' }}
                </button>
            </div>
            <div class="actions"><button class="danger" :disabled="busy || configConflict || !online || authoritativeOwner !== selected" @click="requestRemoval"><UIcon name="i-lucide-trash-2" /> Delete destination</button></div>
            <p class="muted">
                Disabling pauses delivery; identity and history remain.
            </p>
        </section>
    </section>
    <p v-if="feedback && (tab !== 'Settings' || destination?.kind !== 'local')" role="status" class="notice">
        {{ feedback }}
        <button v-if="configConflict" :disabled="busy || !online" @click="reloadSettings()">
            Reload saved settings
        </button>
    </p>
    <LocalFolderPicker v-if="folderPickerOpen && destination?.kind === 'local'" :key="selected" :initial-path="root" :online="online" @select="root = $event; dirtyProvider = true; folderPickerOpen = false" @close="folderPickerOpen = false" />
    <dialog ref="discardDialog" class="discard-dialog" aria-labelledby="discard-destination-title">
        <h2 id="discard-destination-title">Discard unsaved destination changes?</h2>
        <p>Your saved settings and rules are preserved.</p>
        <div class="actions"><button autofocus @click="discardDialog?.close(); discardAction = undefined">Keep editing</button><button class="danger" @click="discard">Discard changes</button></div>
    </dialog>
    <dialog ref="removalDialog" class="discard-dialog" aria-labelledby="remove-destination-title">
        <h2 id="remove-destination-title">Remove {{ removal?.name }}?</h2>
        <p>An unused destination is permanently deleted. A destination with history is archived and delivery stops; its existing delivery history remains preserved.</p>
        <div class="actions"><button autofocus :disabled="busy" @click="removalDialog?.close(); removal = undefined">Cancel</button><button class="danger" :disabled="busy || configConflict || !online" @click="removeDestination">Confirm removal</button></div>
    </dialog>
</template>
