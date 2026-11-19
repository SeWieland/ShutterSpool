export const workspaceDefaults = { width: 'standard', motion: 'system', gridMaximumDimension: 512, detailMaximumDimension: 2048 };
export const appearanceDefaults = { density: 230, ratio: '4 / 3', fit: 'fill', details: true, sort: 'newest' };
export function defaultFilters(curation = false) {
    return { workflows: curation ? ['Waiting'] : [] as string[], sourceIds: [] as string[], collectionIds: [] as string[], rating: null as number | null, ratingMaximum: null as number | null, labels: [] as string[], tag: '', favoritesOnly: false };
}
export function defaultPreferences() {
    return { version: 2, workspace: { ...workspaceDefaults }, appearances: { Gallery: { ...appearanceDefaults }, Curation: { ...appearanceDefaults } }, filters: { Gallery: defaultFilters(), Curation: defaultFilters(true) } };
}
const isObject = (value: unknown): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value);
const object = (value: unknown) => isObject(value) ? value : {};
const integer = (value: unknown, minimum: number, maximum: number): value is number => typeof value === 'number' && Number.isInteger(value) && value >= minimum && value <= maximum;
const allowedString = (value: unknown, choices: readonly string[]): value is string => typeof value === 'string' && choices.includes(value);
export function previewDimension(value: string | number) {
    return String(value).trim() && integer(Number(value), 64, 4096) ? Number(value) : undefined;
}
export function decodePreferences(raw: string | null) {
    const value = defaultPreferences();
    if (raw === null) return { value, writable: true, notice: '' };
    let stored: Record<string, unknown>;
    try {
        const parsed: unknown = JSON.parse(raw);
        if (!isObject(parsed)) throw new Error('Invalid preferences');
        stored = parsed;
    } catch { return { value, writable: true, notice: "Saved preferences couldn't be read. Changes apply for this visit." }; }
    if (stored.version !== undefined && stored.version !== 2)
        return { value, writable: false, notice: 'Saved preferences use an unsupported format. Changes apply for this visit.' };
    const legacy = stored.version === undefined;
    const shared = object(legacy ? stored.appearance : stored.workspace);
    decodeWorkspace(shared, value.workspace);
    for (const view of ['Gallery', 'Curation'] as const) {
        const appearance = legacy ? shared : object(object(stored.appearances)[view]);
        decodeAppearance(appearance, value.appearances[view], legacy);
        decodeFilters(object(object(stored.filters)[view]), value.filters[view], legacy);
    }
    return { value, writable: true, notice: '' };
}

function decodeWorkspace(source: Record<string, unknown>, target: typeof workspaceDefaults) {
    if (allowedString(source.width, ['standard', 'full'])) target.width = source.width;
    if (allowedString(source.motion, ['system', 'reduce', 'full'])) target.motion = source.motion;
    for (const key of ['gridMaximumDimension', 'detailMaximumDimension'] as const)
        if (integer(source[key], 64, 4096)) target[key] = source[key];
}

function decodeAppearance(source: Record<string, unknown>, target: typeof appearanceDefaults, legacy: boolean) {
    const densityNames: Record<string, number> = { dense: 170, medium: 230, spacious: 310 };
    const density = legacy && typeof source.density === 'string' ? densityNames[source.density] : source.density;
    if (integer(density, 100, 520)) target.density = density;
    if (allowedString(source.ratio, ['3 / 2', '4 / 3', '16 / 9', '1 / 1'])) target.ratio = source.ratio;
    if (allowedString(source.fit, ['fill', 'fit'])) target.fit = source.fit;
    if (allowedString(source.sort, ['newest', 'oldest'])) target.sort = source.sort;
    if (typeof source.details === 'boolean') target.details = source.details;
}

function decodeWorkflows(filter: Record<string, unknown>, fallback: string[], legacy: boolean) {
    const allowed = ['Waiting', 'Held', 'Released', 'Attention'];
    if (Array.isArray(filter.workflows)) {
        const valid = filter.workflows.filter((item): item is string => allowedString(item, allowed));
        return filter.workflows.length && !valid.length ? fallback : valid;
    }
    if (!legacy || typeof filter.workflow !== 'string') return fallback;
    if (filter.workflow === 'All') return [];
    return allowedString(filter.workflow, allowed) ? [filter.workflow] : fallback;
}

function decodeIds(candidate: unknown, legacyId?: unknown) {
    if (Array.isArray(candidate)) return candidate.filter((id): id is string => typeof id === 'string').slice(0, 30);
    return typeof legacyId === 'string' ? [legacyId] : [];
}

function decodeFilters(filter: Record<string, unknown>, target: ReturnType<typeof defaultFilters>, legacy: boolean) {
    target.workflows = decodeWorkflows(filter, target.workflows, legacy);
    target.favoritesOnly = filter.favoritesOnly === true;
    target.sourceIds = decodeIds(filter.sourceIds, legacy ? filter.sourceId : undefined);
    target.collectionIds = decodeIds(filter.collectionIds);
    if (typeof filter.tag === 'string' && filter.tag.length <= 64) target.tag = filter.tag;
    if (integer(filter.rating, -1, 5) && integer(filter.ratingMaximum, -1, 5) && filter.rating <= filter.ratingMaximum) {
        target.rating = filter.rating;
        target.ratingMaximum = filter.ratingMaximum;
    }
    if (Array.isArray(filter.labels))
        target.labels = filter.labels.filter((item): item is string => allowedString(item, ['Red', 'Yellow', 'Green', 'Blue', 'Purple', 'None']));
}
