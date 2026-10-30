<script setup lang="ts">
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { workspaceChangeError } from '../lib/workspace-permissions';
const emit = defineEmits<{ joined: [] }>();
type Offer = NonNullable<NonNullable<Awaited<ReturnType<typeof api.workspaceOffers.get>>>['items']>[number];
const offers = shallowRef<Offer[]>([]);
const incomplete = shallowRef(false);
const loading = shallowRef(false);
const busy = shallowRef(false);
const loaded = shallowRef(false);
const error = shallowRef('');
const feedback = shallowRef('');
const pending = shallowRef<{ id: string; workspaceId: string; operationId: string; expectedRevision: number }>();
let controller: AbortController | undefined;
let sequence = 0;
let disposed = false;
async function read() {
    controller?.abort(); controller = new AbortController();
    const request = ++sequence, access = accessGeneration(), signal = controller.signal;
    loading.value = true; error.value = '';
    try {
        const page = await api.workspaceOffers.get({ options: [cancellable(signal)] });
        if (disposed || signal.aborted || request !== sequence || access !== accessGeneration()) return false;
        if (!page?.items || typeof page.incomplete !== 'boolean' || page.items.some(offer => !offer.id || !offer.workspaceId || !offer.workspaceName?.trim()
            || !['Administrator', 'Curator', 'Viewer'].includes(offer.role ?? '') || !Number.isSafeInteger(offer.revision)
            || !['pending', 'expired', 'cancelled', 'invalidated', 'accepted'].includes(offer.status ?? ''))) throw new Error('Invitations unavailable');
        offers.value = page.items; incomplete.value = page.incomplete; loaded.value = true;
        return true;
    } catch (failure) {
        if (!disposed && !signal.aborted && request === sequence && access === accessGeneration()) {
            handleFailure(failure); error.value = 'Workspace invitations could not be checked. Refresh invitations to try again.';
        }
        return false;
    } finally { if (request === sequence) loading.value = false; }
}
async function join(offer?: Offer) {
    if (busy.value || loading.value || error.value || pending.value && offer) return;
    if (offer && offer.status === 'pending' && offer.revision != null) pending.value = {
        id: String(offer.id), workspaceId: String(offer.workspaceId), operationId: crypto.randomUUID(), expectedRevision: offer.revision,
    };
    const operation = pending.value;
    if (!operation) return;
    const access = accessGeneration(); busy.value = true; feedback.value = '';
    try {
        const result = await api.workspaceOffers.byWorkspaceId(operation.workspaceId).byId(operation.id).accept.post({
            operationId: operation.operationId, expectedRevision: operation.expectedRevision,
        });
        if (disposed || access !== accessGeneration()) return;
        if (String(result?.operationId) !== operation.operationId || !['applied', 'existingMember'].includes(result?.status ?? '')) throw new Error('Join outcome unavailable');
        pending.value = undefined;
        feedback.value = 'Join checked. Refreshing your workspace memberships…';
        emit('joined');
        await read();
    } catch (failure) {
        if (disposed || access !== accessGeneration()) return;
        const status = handleFailure(failure);
        if ([400, 403, 404, 409].includes(status ?? 0)) {
            pending.value = undefined;
            feedback.value = workspaceChangeError(failure) ?? 'This invitation cannot be joined. Review its refreshed status or contact the workspace administrator.';
            await read();
        } else feedback.value = 'The Join outcome is uncertain. Check the previous Join before choosing another action.';
    } finally { if (!disposed && access === accessGeneration()) busy.value = false; }
}
async function checkPrevious() { if (!busy.value && !loading.value && pending.value && await read()) await join(); }
onMounted(() => { void read(); });
const stop = onAccessLost(() => { sequence++; controller?.abort(); offers.value = []; pending.value = undefined; loaded.value = false; loading.value = false; busy.value = false; });
onUnmounted(() => { disposed = true; sequence++; controller?.abort(); pending.value = undefined; stop(); });
</script>

<template>
    <section class="workspace-invitations" aria-labelledby="workspace-invitations-title" :aria-busy="loading || busy">
        <div class="actions"><h2 id="workspace-invitations-title">Workspace invitations</h2><button type="button" :disabled="loading || busy" @click="read">Refresh invitations</button></div>
        <p>Join only a workspace you recognize. Signing in or completing account setup does not join a workspace.</p>
        <p v-if="error" class="notice error" role="alert">{{ error }}</p>
        <p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
        <button v-if="pending" type="button" :disabled="busy || loading" @click="checkPrevious">Check previous Join</button>
        <p v-if="loading && !loaded" role="status">Checking your invitations…</p>
        <p v-if="incomplete" class="notice" role="status">Some invitations could not be checked. Refresh to check again; listed invitations remain available.</p>
        <ul class="member-list">
            <li v-for="offer in offers" :key="`${offer.workspaceId}:${offer.id}`" class="member-row">
                <div class="member-identity"><strong>{{ offer.workspaceName }}</strong><span>Offered role: {{ offer.role }}</span><span>Status: {{ offer.status }}</span></div>
                <div class="member-actions"><button v-if="offer.status === 'pending'" type="button" :disabled="busy || loading || !!error || !!pending" @click="join(offer)">Join workspace</button><p v-else-if="offer.status !== 'accepted'" class="muted">Ask a workspace administrator for a new invitation.</p><p v-else class="muted">Membership is checked through the workspace list.</p></div>
            </li>
        </ul>
        <p v-if="loaded && !offers.length && !error" class="muted">No invitations were returned for this sign-in. Contact the operator if you expected one.</p>
    </section>
</template>

<style scoped>
.workspace-invitations { max-width: 720px; width: calc(100% - 32px); margin: 32px auto; text-align: left; overflow-wrap: anywhere; }
.workspace-invitations > p { margin-block: 16px; }
.workspace-invitations .actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 12px; }
.workspace-invitations button { padding: 8px 12px; color: var(--text); background: var(--raised); border: 1px solid var(--border); border-radius: 6px; }
.workspace-invitations button:hover:enabled { border-color: var(--accent); }
.workspace-invitations button:disabled { opacity: .6; cursor: not-allowed; }
</style>
