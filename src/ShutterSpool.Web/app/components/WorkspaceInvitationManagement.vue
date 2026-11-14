<script setup lang="ts">
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { mayAdminister, refreshWorkspaceAccess } from '../lib/workspace-access';
import { workspaceChangeError, type WorkspaceRole } from '../lib/workspace-permissions';
const props = defineProps<{ online: boolean; generation: number }>();
type Invitation = NonNullable<NonNullable<Awaited<ReturnType<typeof api.management.invitations.get>>>['items']>[number];
const invitations = shallowRef<Invitation[]>([]);
const email = shallowRef('');
const role = shallowRef<Exclude<WorkspaceRole, 'Owner'>>('Viewer');
const renewalRoles = reactive<Record<string, Exclude<WorkspaceRole, 'Owner'>>>({});
const loading = shallowRef(false);
const busy = shallowRef(false);
const loaded = shallowRef(false);
const error = shallowRef('');
const feedback = shallowRef('');
type Pending = { operationId: string } & ({ kind: 'create'; email: string; role: Exclude<WorkspaceRole, 'Owner'> }
    | { kind: 'renew'; id: string; expectedRevision: number; role: Exclude<WorkspaceRole, 'Owner'> }
    | { kind: 'cancel'; id: string; expectedRevision: number });
const pending = shallowRef<Pending>();
const setupEmailRetry = shallowRef<Pending>();
let sequence = 0;
let disposed = false;
let controller: AbortController | undefined;
async function read() {
    if (!mayAdminister.value || !props.online) return false;
    controller?.abort(); controller = new AbortController();
    const request = ++sequence, access = accessGeneration(), signal = controller.signal;
    loading.value = true; error.value = '';
    try {
        const page = await api.management.invitations.get({ options: [cancellable(signal)] });
        if (disposed || signal.aborted || request !== sequence || access !== accessGeneration()) return false;
        if (!page?.items) throw new Error('Invitations unavailable');
        const previous = new Map(invitations.value.map(invitation => [String(invitation.id), invitation]));
        for (const invitation of page.items) {
            const id = String(invitation.id), prior = previous.get(id);
            if (!prior || prior.revision !== invitation.revision || !renewalRoles[id]) renewalRoles[id] = invitation.role as Exclude<WorkspaceRole, 'Owner'>;
        }
        invitations.value = page.items;
        loaded.value = true;
        return true;
    } catch (failure) {
        if (!disposed && !signal.aborted && request === sequence && access === accessGeneration()) {
            handleFailure(failure); error.value = 'Invitations could not be loaded. Refresh invitations to try again.';
        }
        return false;
    } finally { if (request === sequence) loading.value = false; }
}
async function submit(change?: Pending) {
    if (busy.value || loading.value || error.value || !props.online || !mayAdminister.value || pending.value && change) return;
    pending.value ??= change;
    const operation = pending.value;
    if (!operation) return;
    setupEmailRetry.value = undefined;
    const access = accessGeneration(); busy.value = true; feedback.value = '';
    try {
        const body = { operationId: operation.operationId };
        const result = operation.kind === 'create' ? await api.management.invitations.post({ ...body, email: operation.email, role: operation.role })
            : operation.kind === 'renew' ? await api.management.invitations.byId(operation.id).renew.post({ ...body, expectedRevision: operation.expectedRevision, role: operation.role })
                : await api.management.invitations.byId(operation.id).cancel.post({ ...body, expectedRevision: operation.expectedRevision });
        if (disposed || access !== accessGeneration()) return;
        if (String(result?.operationId) !== operation.operationId || !['applied', 'existingMember'].includes(result?.status ?? '')) throw new Error('Invitation outcome unavailable');
        pending.value = undefined;
        if (result?.setupEmailStatus === 'setup-email-failed') setupEmailRetry.value = operation;
        if (result?.status === 'existingMember') feedback.value = 'This recipient already has a membership. Their role was not changed.';
        else if (operation.kind === 'cancel') feedback.value = 'Cancellation recorded. Review the current invitation status below; existing memberships are unchanged.';
        else if (result?.offer && result.offer.status !== 'pending') feedback.value = 'The invitation status has changed. Review it below before choosing another action.';
        else if (result?.setupEmailStatus === 'setup-email-failed') feedback.value = 'Invitation saved, but the account setup email could not be sent. Choose Retry account setup email.';
        else if (result?.setupEmailStatus === 'setup-email-sent') feedback.value = 'Invitation saved. Account setup email sent. The recipient must finish setup, sign in and choose Join workspace.';
        else feedback.value = 'Invitation saved. Share this app’s address with the recipient. They can sign in and choose Join workspace.';
        if (operation.kind === 'create') { email.value = ''; role.value = 'Viewer'; }
        await read();
    } catch (failure) {
        if (disposed || access !== accessGeneration()) return;
        const status = handleFailure(failure);
        if ([400, 403, 404, 409].includes(status ?? 0)) {
            pending.value = undefined;
            feedback.value = workspaceChangeError(failure) ?? 'This invitation change could not be saved. Review the details before trying again.';
            if (['invalid-email', 'account-unavailable', 'enrollment-rejected'].includes((failure as { errorCode?: string })?.errorCode ?? '')) return;
            if (status === 403) await refreshWorkspaceAccess();
            if (status !== 400 && !disposed && access === accessGeneration()) await read();
        } else feedback.value = workspaceChangeError(failure) ?? 'We could not confirm the invitation change. Choose Check invitation before trying another action.';
    } finally { if (!disposed && access === accessGeneration()) busy.value = false; }
}
function create() {
    if (!email.value.trim()) return;
    return submit({ kind: 'create', operationId: crypto.randomUUID(), email: email.value.trim(), role: role.value });
}
function canRenew(invitation: Invitation) { return ['pending', 'expired', 'cancelled'].includes(invitation.status ?? ''); }
function change(invitation: Invitation, kind: 'renew' | 'cancel') {
    if (invitation.revision == null || (kind === 'cancel' ? invitation.status !== 'pending' : !canRenew(invitation))) return;
    const operation = { id: String(invitation.id), operationId: crypto.randomUUID(), expectedRevision: invitation.revision };
    if (kind === 'cancel') {
        if (!window.confirm(`Cancel the invitation for ${invitation.recipientLabel}? This does not remove an existing membership or cancel account setup.`)) return;
        return submit({ ...operation, kind });
    }
    const offeredRole = renewalRoles[String(invitation.id)];
    if (!offeredRole) return;
    return submit({ ...operation, kind, role: offeredRole });
}
async function checkPrevious() { if (!busy.value && !loading.value && pending.value && await read()) await submit(); }
watch(() => props.generation, () => { if (!busy.value && !pending.value) void read(); });
onMounted(() => { void read(); });
const stop = onAccessLost(() => { sequence++; controller?.abort(); invitations.value = []; email.value = ''; pending.value = undefined; setupEmailRetry.value = undefined; loaded.value = false; loading.value = false; busy.value = false; });
onUnmounted(() => { disposed = true; sequence++; controller?.abort(); pending.value = undefined; setupEmailRetry.value = undefined; stop(); });
</script>

<template>
    <section class="workspace-invitation-management" aria-labelledby="invitation-management-title" :aria-busy="loading || busy">
        <div class="actions"><h3 id="invitation-management-title">Invitations</h3><button type="button" :disabled="busy || loading || !online" @click="read">Refresh invitations</button></div>
        <p>Invite someone by email. New accounts receive an account setup email. Existing users can sign in here to see their invitation. Invitations expire after 24 hours and require the recipient to choose Join workspace.</p>
        <p v-if="error" class="notice error" role="alert">{{ error }}</p><p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
        <button v-if="pending" type="button" :disabled="busy || loading || !online" @click="checkPrevious">Check invitation</button>
        <button v-if="setupEmailRetry" type="button" :disabled="busy || loading || !!error || !online" @click="submit(setupEmailRetry)">Retry account setup email</button>
        <p v-if="loading && !loaded" role="status">Loading invitations…</p>
        <template v-if="loaded">
            <form class="invitation-create" @submit.prevent="create">
                <label>Email address<input v-model="email" type="email" autocomplete="off" required :disabled="busy || loading || !!pending || !!error || !online"></label>
                <label>Offered role<select v-model="role" :disabled="busy || loading || !!pending || !!error || !online"><option>Viewer</option><option>Curator</option><option>Administrator</option></select></label>
                <button type="submit" :disabled="!email.trim() || busy || loading || !!pending || !!error || !online">Create invitation</button>
            </form>
            <ul class="member-list">
                <li v-for="invitation in invitations" :key="String(invitation.id)" class="member-row">
                    <div class="member-identity"><strong>{{ invitation.recipientLabel }}</strong><span>{{ invitation.role }} · {{ invitation.status }}</span><span v-if="invitation.expiresAt">Offer expires: {{ invitation.expiresAt.toLocaleString() }}</span></div>
                    <div v-if="canRenew(invitation)" class="member-actions">
                        <label>Role on renewal for {{ invitation.recipientLabel }}<select v-model="renewalRoles[String(invitation.id)]" :disabled="busy || loading || !!pending || !!error || !online"><option>Viewer</option><option>Curator</option><option>Administrator</option></select></label>
                        <div class="actions"><button type="button" :disabled="busy || loading || !!pending || !!error || !online" @click="change(invitation, 'renew')">Renew invitation</button><button v-if="invitation.status === 'pending'" type="button" :disabled="busy || loading || !!pending || !!error || !online" @click="change(invitation, 'cancel')">Cancel invitation</button></div>
                    </div>
                    <p v-else-if="invitation.status === 'invalidated'" class="muted">This invitation is no longer valid. Create a new invitation by email.</p>
                    <p v-else-if="invitation.status === 'accepted'" class="muted">Accepted offers do not change a current membership. Use the Members controls to change access.</p>
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
