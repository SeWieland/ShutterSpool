<script setup lang="ts">
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { mayAdminister, refreshWorkspaceAccess } from '../lib/workspace-access';
import { workspaceChangeError, type WorkspaceRole } from '../lib/workspace-permissions';
const props = defineProps<{ online: boolean; generation: number }>();
type Invitation = NonNullable<NonNullable<Awaited<ReturnType<typeof api.management.invitations.get>>>['items']>[number];
type Recipient = NonNullable<NonNullable<Awaited<ReturnType<typeof api.management.invitationRecipients.get>>>['items']>[number];
const invitations = shallowRef<Invitation[]>([]);
const recipients = shallowRef<Recipient[]>([]);
const recipientId = shallowRef('');
const role = shallowRef<Exclude<WorkspaceRole, 'Owner'>>('Viewer');
const renewalRoles = reactive<Record<string, Exclude<WorkspaceRole, 'Owner'>>>({});
const loading = shallowRef(false);
const busy = shallowRef(false);
const loaded = shallowRef(false);
const error = shallowRef('');
const feedback = shallowRef('');
type Pending = { operationId: string } & ({ kind: 'create'; recipientId: string; role: Exclude<WorkspaceRole, 'Owner'> }
    | { kind: 'renew' | 'cancel'; id: string; expectedRevision: number; role: Exclude<WorkspaceRole, 'Owner'> });
const pending = shallowRef<Pending>();
let sequence = 0;
let disposed = false;
let controller: AbortController | undefined;
async function read() {
    if (!mayAdminister.value || !props.online) return false;
    controller?.abort(); controller = new AbortController();
    const request = ++sequence, access = accessGeneration(), signal = controller.signal;
    loading.value = true; error.value = '';
    try {
        const config = { options: [cancellable(signal)] };
        const [page, references] = await Promise.all([api.management.invitations.get(config), api.management.invitationRecipients.get(config)]);
        if (disposed || signal.aborted || request !== sequence || access !== accessGeneration()) return false;
        if (!page?.items || !references?.items || references.items.some(recipient => !recipient.id || !recipient.label?.trim())
            || page.items.some(invitation => !invitation.id || !invitation.recipientId || !invitation.recipientLabel?.trim()
                || !['Administrator', 'Curator', 'Viewer'].includes(invitation.role ?? '') || !Number.isSafeInteger(invitation.revision)
                || !['pending', 'expired', 'cancelled', 'invalidated', 'accepted'].includes(invitation.status ?? ''))) throw new Error('Invitations unavailable');
        const previous = new Map(invitations.value.map(invitation => [String(invitation.id), invitation]));
        for (const invitation of page.items) {
            const id = String(invitation.id), prior = previous.get(id);
            if (!prior || prior.revision !== invitation.revision || !renewalRoles[id]) renewalRoles[id] = invitation.role as Exclude<WorkspaceRole, 'Owner'>;
        }
        invitations.value = page.items; recipients.value = references.items;
        if (!recipients.value.some(recipient => String(recipient.id) === recipientId.value)) recipientId.value = '';
        loaded.value = true;
        return true;
    } catch (failure) {
        if (!disposed && !signal.aborted && request === sequence && access === accessGeneration()) {
            handleFailure(failure); error.value = 'Invitations and approved recipients could not be loaded. Refresh invitations to try again.';
        }
        return false;
    } finally { if (request === sequence) loading.value = false; }
}
async function submit(change?: Pending) {
    if (busy.value || loading.value || error.value || !props.online || !mayAdminister.value || pending.value && change) return;
    pending.value ??= change;
    const operation = pending.value;
    if (!operation) return;
    const access = accessGeneration(); busy.value = true; feedback.value = '';
    try {
        const body = { operationId: operation.operationId };
        const result = operation.kind === 'create' ? await api.management.invitations.post({ ...body, recipientId: operation.recipientId, role: operation.role })
            : operation.kind === 'renew' ? await api.management.invitations.byId(operation.id).renew.post({ ...body, expectedRevision: operation.expectedRevision, role: operation.role })
                : await api.management.invitations.byId(operation.id).cancel.post({ ...body, expectedRevision: operation.expectedRevision });
        if (disposed || access !== accessGeneration()) return;
        if (String(result?.operationId) !== operation.operationId || !['applied', 'existingMember'].includes(result?.status ?? '')) throw new Error('Invitation outcome unavailable');
        pending.value = undefined;
        feedback.value = result?.status === 'existingMember' ? 'This recipient already has a membership. Their role was not changed.'
            : operation.kind === 'cancel' ? 'Cancellation recorded. Review the current invitation status below; existing memberships are unchanged.'
                : 'Invitation change recorded. Review the current status below. Pending invitations require the recipient to sign in and choose Join workspace.';
        if (operation.kind === 'create') { recipientId.value = ''; role.value = 'Viewer'; }
        await read();
    } catch (failure) {
        if (disposed || access !== accessGeneration()) return;
        const status = handleFailure(failure);
        if ([400, 403, 404, 409].includes(status ?? 0)) {
            pending.value = undefined;
            feedback.value = workspaceChangeError(failure) ?? 'This invitation change is no longer permitted. Review the refreshed invitations.';
            await refreshWorkspaceAccess();
            if (!disposed && access === accessGeneration()) await read();
        } else feedback.value = 'The invitation outcome is uncertain. Check the previous change before choosing another action.';
    } finally { if (!disposed && access === accessGeneration()) busy.value = false; }
}
function create() {
    if (!recipients.value.some(recipient => String(recipient.id) === recipientId.value) || !['Administrator', 'Curator', 'Viewer'].includes(role.value)) return;
    return submit({ kind: 'create', operationId: crypto.randomUUID(), recipientId: recipientId.value, role: role.value });
}
function change(invitation: Invitation, kind: 'renew' | 'cancel') {
    if (invitation.status === 'accepted' || invitation.revision == null || kind === 'cancel' && invitation.status !== 'pending') return;
    const offeredRole = renewalRoles[String(invitation.id)];
    if (!offeredRole || !['Administrator', 'Curator', 'Viewer'].includes(offeredRole)) return;
    if (kind === 'cancel' && !window.confirm(`Cancel the invitation for ${invitation.recipientLabel}? This does not remove an existing membership or cancel account setup.`)) return;
    return submit({ kind, id: String(invitation.id), operationId: crypto.randomUUID(), expectedRevision: invitation.revision, role: offeredRole });
}
async function checkPrevious() { if (!busy.value && !loading.value && pending.value && await read()) await submit(); }
watch(() => props.generation, () => { if (!busy.value && !pending.value) void read(); });
onMounted(() => { void read(); });
const stop = onAccessLost(() => { sequence++; controller?.abort(); invitations.value = []; recipients.value = []; pending.value = undefined; loaded.value = false; loading.value = false; busy.value = false; });
onUnmounted(() => { disposed = true; sequence++; controller?.abort(); pending.value = undefined; stop(); });
</script>

<template>
    <section class="workspace-invitation-management" aria-labelledby="invitation-management-title" :aria-busy="loading || busy">
        <div class="actions"><h3 id="invitation-management-title">Invitations</h3><button type="button" :disabled="busy || loading || !online" @click="read">Refresh invitations</button></div>
        <p>Choose a recipient supplied by the operator for this workspace. Invitations expire after 24 hours and require the recipient to choose Join workspace. Account setup and email verification stay with the sign-in service.</p>
        <p v-if="error" class="notice error" role="alert">{{ error }}</p><p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
        <button v-if="pending" type="button" :disabled="busy || loading || !online" @click="checkPrevious">Check previous invitation change</button>
        <p v-if="loading && !loaded" role="status">Loading invitations and approved recipients…</p>
        <template v-if="loaded">
            <form v-if="recipients.length" class="invitation-create" @submit.prevent="create">
                <label>Approved recipient<select v-model="recipientId" :disabled="busy || loading || !!pending || !!error || !online"><option value="">Choose a recipient</option><option v-for="recipient in recipients" :key="String(recipient.id)" :value="String(recipient.id)">{{ recipient.label }}</option></select></label>
                <label>Offered role<select v-model="role" :disabled="busy || loading || !!pending || !!error || !online"><option>Viewer</option><option>Curator</option><option>Administrator</option></select></label>
                <button type="submit" :disabled="!recipientId || busy || loading || !!pending || !!error || !online">Create invitation</button>
            </form>
            <p v-else class="muted">No approved recipients are available. Ask the operator to supply a recipient for this workspace.</p>
            <ul class="member-list">
                <li v-for="invitation in invitations" :key="String(invitation.id)" class="member-row">
                    <div class="member-identity"><strong>{{ invitation.recipientLabel }}</strong><span>{{ invitation.role }} · {{ invitation.status }}</span><span v-if="invitation.expiresAt">Offer expires: {{ invitation.expiresAt.toLocaleString() }}</span></div>
                    <div v-if="invitation.status !== 'accepted'" class="member-actions">
                        <label>Role on renewal for {{ invitation.recipientLabel }}<select v-model="renewalRoles[String(invitation.id)]" :disabled="busy || loading || !!pending || !!error || !online"><option>Viewer</option><option>Curator</option><option>Administrator</option></select></label>
                        <div class="actions"><button type="button" :disabled="busy || loading || !!pending || !!error || !online" @click="change(invitation, 'renew')">Renew invitation</button><button v-if="invitation.status === 'pending'" type="button" :disabled="busy || loading || !!pending || !!error || !online" @click="change(invitation, 'cancel')">Cancel invitation</button></div>
                    </div>
                    <p v-else class="muted">Accepted offers do not change a current membership. Use the Members controls to change access.</p>
                </li>
            </ul>
            <p v-if="!invitations.length" class="muted">No workspace invitations have been created.</p>
        </template>
    </section>
</template>

<style scoped>
.workspace-invitation-management { margin-top: 32px; }
.workspace-invitation-management > p { margin-block: 16px; }
.invitation-create { display: grid; gap: 16px; margin-block: 24px; }
.invitation-create label { display: grid; gap: 8px; }
.invitation-create button { justify-self: start; }
</style>
