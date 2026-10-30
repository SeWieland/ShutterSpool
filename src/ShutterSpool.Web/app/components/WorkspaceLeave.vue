<script setup lang="ts">
import { api, handleFailure } from '../lib/api';
import { accessGeneration } from '../lib/session';
import { closeWorkspaceAccess, workspaceAccess, refreshWorkspaceAccess } from '../lib/workspace-access';
import { workspaceChangeError } from '../lib/workspace-permissions';
const props = defineProps<{ online: boolean }>();
const busy = ref(false);
const feedback = ref('');
let pending: { operationId: string; expectedRevision: number } | undefined;
let disposed = false;
async function leave() {
    if (busy.value || !props.online || !workspaceAccess.value) return;
    if (!pending && !window.confirm('Leave this workspace? You will lose access to its photographs. Another Owner must remain. You will stay signed in.')) return;
    pending ??= { operationId: crypto.randomUUID(), expectedRevision: workspaceAccess.value.revision };
    const access = accessGeneration();
    busy.value = true;
    try {
        const result = await api.management.membership.leave.post(pending);
        if (disposed || access !== accessGeneration()) return;
        if (result?.status !== 'applied') throw new Error('Leave outcome unavailable');
        pending = undefined;
        closeWorkspaceAccess(true);
    } catch (failure) {
        if (disposed || access !== accessGeneration()) return;
        const status = handleFailure(failure);
        if (status === 409 || status === 403 || status === 404) {
            pending = undefined;
            feedback.value = workspaceChangeError(failure) ?? 'Leaving was denied. Refresh workspace access to check your current membership.';
            await refreshWorkspaceAccess();
        } else feedback.value = 'The outcome is uncertain. Check the previous leave request before choosing another action.';
    } finally { if (!disposed && access === accessGeneration()) busy.value = false; }
}
onUnmounted(() => { disposed = true; pending = undefined; });
</script>

<template>
    <details class="settings-advanced section-disclosure">
        <summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Workspace membership</summary>
        <p>Your role: <strong>{{ workspaceAccess?.role }}</strong>. Leaving removes your access to this workspace.</p>
        <button type="button" :disabled="busy || !online" @click="leave">{{ busy ? 'Checking…' : pending ? 'Check previous leave request' : 'Leave workspace' }}</button>
        <p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
    </details>
</template>
