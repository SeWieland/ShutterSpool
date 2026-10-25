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
    for (const key of ['width', 'motion'] as const) {
        const allowed = key === 'width' ? ['standard', 'full'] : ['system', 'reduce', 'full'];
        const candidate = shared[key];
        if (allowedString(candidate, allowed)) value.workspace[key] = candidate;
    }
    for (const key of ['gridMaximumDimension', 'detailMaximumDimension'] as const)
        if (integer(shared[key], 64, 4096)) value.workspace[key] = shared[key];
    for (const view of ['Gallery', 'Curation'] as const) {
        const source = object(legacy ? stored.appearance : object(stored.appearances)[view]);
        const appearance = value.appearances[view];
        const density = legacy && typeof source.density === 'string' ? ({ dense: 170, medium: 230, spacious: 310 } as Record<string, number>)[source.density] : source.density;
        if (integer(density, 100, 520)) appearance.density = density;
        const enums = { ratio: ['3 / 2', '4 / 3', '16 / 9', '1 / 1'], fit: ['fill', 'fit'], sort: ['newest', 'oldest'] };
        for (const key of ['ratio', 'fit', 'sort'] as const) {
            const candidate = source[key];
            if (allowedString(candidate, enums[key])) appearance[key] = candidate;
        }
        if (typeof source.details === 'boolean') appearance.details = source.details;
        const filter = object(object(stored.filters)[view]);
        if (!Object.keys(filter).length) continue;
        const target = value.filters[view];
        const workflows = Array.isArray(filter.workflows) ? filter.workflows : legacy && typeof filter.workflow === 'string' ? filter.workflow === 'All' ? [] : [filter.workflow] : undefined;
        if (workflows) {
            const valid = workflows.filter((item): item is string => allowedString(item, ['Waiting', 'Held', 'Released', 'Attention']));
            if (!workflows.length || valid.length) target.workflows = valid;
        }
        target.favoritesOnly = filter.favoritesOnly === true;
        for (const key of ['sourceIds', 'collectionIds'] as const) {
            const candidate = filter[key];
            const ids = Array.isArray(candidate) ? candidate : legacy && key === 'sourceIds' && typeof filter.sourceId === 'string' ? [filter.sourceId] : [];
            target[key] = ids.filter((id): id is string => typeof id === 'string').slice(0, 30);
        }
        if (typeof filter.tag === 'string' && filter.tag.length <= 64) target.tag = filter.tag;
        if (integer(filter.rating, -1, 5) && integer(filter.ratingMaximum, -1, 5) && filter.rating <= filter.ratingMaximum) {
            target.rating = filter.rating; target.ratingMaximum = filter.ratingMaximum;
        }
        if (Array.isArray(filter.labels)) target.labels = filter.labels.filter((item): item is string => allowedString(item, ['Red', 'Yellow', 'Green', 'Blue', 'Purple', 'None']));
    }
    return { value, writable: true, notice: '' };
}
