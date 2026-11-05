import { computed, ref, shallowRef } from 'vue';
import { accessGeneration, clearPrivateAccess, onAccessLost, session } from './session';
import { accessFingerprint, validateWorkspaceAccess, type WorkspaceAccess } from './workspace-permissions';

export const workspaceAccess = shallowRef<WorkspaceAccess>();
export const workspaceAccessVersion = ref(0);
export const workspaceAccessError = ref('');
export const workspaceRevoked = ref(false);
export const mayCurate = computed(() => workspaceAccess.value?.capabilities.canCurate === true);
export const mayAdminister = computed(() => workspaceAccess.value?.capabilities.canAdminister === true);
let pending: Promise<boolean> | undefined;

export function discardWorkspacePrivateState() {
    clearPrivateAccess();
    workspaceAccessVersion.value++;
}

onAccessLost(() => { if (!session.value) { workspaceAccess.value = undefined; workspaceAccessError.value = ''; workspaceRevoked.value = false; } });

export function closeWorkspaceAccess(revoked = false) {
    workspaceAccess.value = undefined;
    workspaceRevoked.value = revoked;
    workspaceAccessError.value = revoked ? 'Access to this workspace ended. Choose an available workspace or retry.' : 'Workspace permissions could not be checked. Retry to continue.';
    discardWorkspacePrivateState();
}

export function refreshWorkspaceAccess() {
    return pending ??= (async () => {
        const generation = accessGeneration();
        let deadline: ReturnType<typeof setTimeout> | undefined;
        try {
            const { api, cancellable } = await import('./api');
            const value = await Promise.race([
                api.management.access.get({ options: [cancellable(AbortSignal.timeout(10_000))] }),
                new Promise<never>((_resolve, reject) => { deadline = setTimeout(() => reject(new Error('Workspace authorization timed out.')), 10_000); }),
            ]);
            if (generation !== accessGeneration()) return false;
            const next = validateWorkspaceAccess(value);
            if (workspaceAccess.value && accessFingerprint(workspaceAccess.value) !== accessFingerprint(next)) {
                discardWorkspacePrivateState();
            }
            workspaceAccess.value = next;
            workspaceRevoked.value = false;
            workspaceAccessError.value = '';
            return true;
        } catch (error) {
            const { handleFailure } = await import('./api');
            handleFailure(error);
            if (generation === accessGeneration()) closeWorkspaceAccess();
            return false;
        } finally { clearTimeout(deadline); }
    })().finally(() => { pending = undefined; });
}
