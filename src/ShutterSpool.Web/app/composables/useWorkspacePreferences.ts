import { computed, onScopeDispose, reactive, ref, watch, type Ref } from 'vue';
import { decodePreferences, defaultPreferences } from '../lib/workspace-preferences';

type PreferenceIdentity = { profile?: { iss?: string | null; sub?: string | null } } | null;
export type WorkspacePreferenceStorage = Pick<Storage, 'getItem' | 'setItem'>;
type PreferenceDocument = ReturnType<typeof defaultPreferences>;

/** Owns account-scoped workspace settings. Media drafts and credentials do not belong here. */
export function useWorkspacePreferences(session: Readonly<Ref<PreferenceIdentity>>, storage?: WorkspacePreferenceStorage) {
    const preferences = reactive(defaultPreferences());
    const preferenceKey = computed(() => {
        const issuer = session.value?.profile?.iss;
        const subject = session.value?.profile?.sub;
        return issuer && subject ? `shutterspool-appearance-${issuer}-${subject}` : '';
    });
    const preferenceNotice = ref('');
    const writable = ref(true);
    const hydrating = ref(false);
    let activeKey = '';
    let saveTimer: ReturnType<typeof setTimeout> | undefined;
    let disposed = false;

    function cancelPreferenceSave() {
        clearTimeout(saveTimer);
        saveTimer = undefined;
    }

    function flushPreferenceSave(force = false) {
        if (saveTimer === undefined) return;
        cancelPreferenceSave();
        if (hydrating.value || !activeKey || (!force && activeKey !== preferenceKey.value) || !writable.value) return;
        try {
            if (!storage) throw new Error('Browser storage is unavailable');
            storage.setItem(activeKey, JSON.stringify(preferences));
            preferenceNotice.value = '';
        } catch {
            preferenceNotice.value = "Changes apply for this visit. This browser couldn't save them.";
        }
    }

    function queuePreferenceSave() {
        cancelPreferenceSave();
        if (disposed || hydrating.value || !activeKey || activeKey !== preferenceKey.value || !writable.value) return;
        saveTimer = setTimeout(flushPreferenceSave, 120);
    }

    function hydratePreferences() {
        cancelPreferenceSave();
        const key = preferenceKey.value;
        hydrating.value = true;
        activeKey = '';
        let loaded = { value: defaultPreferences(), writable: true, notice: '' };
        if (key) {
            try {
                if (!storage) throw new Error('Browser storage is unavailable');
                loaded = decodePreferences(storage.getItem(key));
            } catch {
                loaded.notice = "Saved preferences couldn't be read. Changes apply for this visit.";
            }
        }
        Object.assign(preferences.workspace, loaded.value.workspace);
        for (const view of ['Gallery', 'Curation'] as const) {
            Object.assign(preferences.appearances[view], loaded.value.appearances[view]);
            Object.assign(preferences.filters[view], loaded.value.filters[view]);
        }
        writable.value = loaded.writable;
        preferenceNotice.value = loaded.notice;
        activeKey = key;
        hydrating.value = false;
    }

    watch(preferences, queuePreferenceSave, { deep: true, flush: 'sync' });
    watch(preferenceKey, (_key, previousKey) => {
        if (previousKey && activeKey === previousKey) flushPreferenceSave(true);
        hydratePreferences();
    }, { flush: 'sync', immediate: true });
    onScopeDispose(() => {
        disposed = true;
        flushPreferenceSave(true);
    });

    return { preferences, preferenceKey, preferenceNotice, hydratePreferences, queuePreferenceSave, cancelPreferenceSave, flushPreferenceSave, writable, hydrating };
}

export type WorkspacePreferences = PreferenceDocument;
