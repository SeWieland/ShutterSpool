<script setup lang="ts">
import { api, cancellable, handleFailure } from '../lib/api';
import { accessGeneration, onAccessLost } from '../lib/session';
import { workspaceAccess, mayAdminister, refreshWorkspaceAccess } from '../lib/workspace-access';
import { workspaceChangeError, type WorkspaceRole } from '../lib/workspace-permissions';
const props = defineProps<{ online: boolean; generation: number }>();
type Member = NonNullable<NonNullable<Awaited<ReturnType<typeof api.management.members.get>>>['items']>[number];
const members = ref<Member[]>([]);
const roles = reactive<Record<string, Exclude<WorkspaceRole, 'Owner'>>>({});
const includeUnreleased = ref(false);
const savedIncludeUnreleased = ref(false);
const policyRevision = ref<number>();
const loading = ref(false);
const busy = ref(false);
const feedback = ref('');
const error = ref('');
const loaded = ref(false);
const hasDraft = computed(() => includeUnreleased.value !== savedIncludeUnreleased.value || members.value.some(member => roles[String(member.id)] !== roleChoice(member)));
type Pending = { operationId: string; expectedRevision: number } & ({ kind: 'policy'; includeUnreleased: boolean } | { kind: 'role'; id: string; role: Exclude<WorkspaceRole, 'Owner'> } | { kind: 'promote' | 'remove'; id: string });
const pending = shallowRef<Pending>();
let sequence = 0;
let disposed = false;
let controller: AbortController | undefined;
function identity(member: Member) { return member.displayName?.trim() || member.subject || 'Identity unavailable'; }
function roleChoice(member: Member) { return (member.role === 'Owner' ? 'Administrator' : member.role) as Exclude<WorkspaceRole, 'Owner'>; }
function editable(member: Member) {
    return member.isActive && (member.role !== 'Owner' || workspaceAccess.value?.capabilities.canManageOwners);
}
function validMember(member: Member) {
    return member.id && member.issuer && member.subject && Number.isSafeInteger(member.revision)
        && ['Owner', 'Administrator', 'Curator', 'Viewer'].includes(member.role ?? '') && typeof member.isActive === 'boolean';
}
function mergeRoleDrafts(savedMembers: Member[]) {
    const previous = new Map(members.value.map(member => [String(member.id), member]));
    let reset = false;
    for (const member of savedMembers) {
        const id = String(member.id);
        const prior = previous.get(id);
        const choice = roles[id];
        const draft = prior && choice !== roleChoice(prior);
        const unchanged = prior && prior.revision === member.revision && prior.role === member.role && prior.isActive === member.isActive;
        if (!(draft && unchanged && editable(member))) {
            const savedRole = roleChoice(member);
            if (draft && choice !== savedRole) reset = true;
            roles[id] = savedRole;
        }
        previous.delete(id);
    }
    for (const [id, member] of previous) {
        if (roles[id] !== roleChoice(member)) reset = true;
        delete roles[id];
    }
    return reset;
}
function mergeViewerVisibility(savedVisibility: boolean, savedRevision: number) {
    const draft = includeUnreleased.value !== savedIncludeUnreleased.value;
    const unchanged = policyRevision.value === savedRevision && savedIncludeUnreleased.value === savedVisibility;
    const reset = draft && !unchanged && includeUnreleased.value !== savedVisibility;
    if (!draft || !unchanged) includeUnreleased.value = savedVisibility;
    savedIncludeUnreleased.value = savedVisibility;
    policyRevision.value = savedRevision;
    return reset;
}
async function read() {
    if (!mayAdminister.value || !props.online) return;
    controller?.abort(); controller = new AbortController();
    const request = ++sequence, access = accessGeneration(), signal = controller.signal;
    loading.value = true; error.value = '';
    try {
        const config = { options: [cancellable(signal)] };
        const [page, policy] = await Promise.all([api.management.members.get(config), api.management.viewerPolicy.get(config)]);
        if (disposed || signal.aborted || request !== sequence || access !== accessGeneration()) return;
        if (!page?.items || !Number.isSafeInteger(policy?.revision) || typeof policy?.includeUnreleased !== 'boolean'
            || page.items.some(member => !validMember(member))) throw new Error('Membership unavailable');
        const resetRoles = mergeRoleDrafts(page.items);
        const resetPolicy = mergeViewerVisibility(policy.includeUnreleased, policy.revision!);
        members.value = page.items;
        if (resetRoles) feedback.value += `${feedback.value ? ' ' : ''}Some unsaved role choices were reset because those memberships changed or are no longer editable.`;
        if (resetPolicy) feedback.value += `${feedback.value ? ' ' : ''}Your unsaved Viewer visibility choice was reset because the policy changed elsewhere.`;
        loaded.value = true;
    } catch (failure) {
        if (!disposed && !signal.aborted && request === sequence && access === accessGeneration()) { handleFailure(failure); error.value = 'Members and Viewer visibility could not be loaded. Refresh to try again.'; }
    } finally { if (request === sequence) loading.value = false; }
}
function requestChange(operation: Pending) {
    const body = { operationId: operation.operationId, expectedRevision: operation.expectedRevision };
    if (operation.kind === 'policy') return api.management.viewerPolicy.put({ ...body, includeUnreleased: operation.includeUnreleased });

    const member = api.management.members.byId(operation.id);
    switch (operation.kind) {
        case 'role': return member.role.post({ ...body, role: operation.role });
        case 'promote': return member.promoteOwner.post({ ...body, confirmed: true });
        case 'remove': return member.remove.post(body);
    }
}
async function refreshMembership(isCurrent: () => boolean) {
    await refreshWorkspaceAccess();
    if (isCurrent()) await read();
}
async function submit(change?: Pending) {
    if (busy.value || loading.value || error.value || !props.online || !mayAdminister.value || pending.value && change) return;
    pending.value ??= change;
    const operation = pending.value;
    if (!operation) return;
    const access = accessGeneration();
    const isCurrent = () => !disposed && access === accessGeneration();
    busy.value = true;
    feedback.value = '';
    try {
        const result = await requestChange(operation);
        if (!isCurrent()) return;
        if (result?.status !== 'applied') throw new Error('Membership outcome unavailable');
        pending.value = undefined;
        feedback.value = operation.kind === 'policy' ? 'Viewer visibility saved.' : 'Membership change saved.';
        await refreshMembership(isCurrent);
    } catch (failure) {
        if (!isCurrent()) return;
        const status = handleFailure(failure);
        if (![403, 404, 409].includes(status ?? 0)) {
            feedback.value = 'The outcome is uncertain. Check the previous change before submitting another action.';
            return;
        }
        pending.value = undefined;
        feedback.value = workspaceChangeError(failure) ?? 'This change is no longer permitted. Review the refreshed membership and Viewer visibility.';
        await refreshMembership(isCurrent);
    } finally { if (isCurrent()) busy.value = false; }
}
function changeRole(member: Member) {
    if (!editable(member) || member.revision == null || !roles[String(member.id)]) return;
    if (member.role === 'Owner' && !window.confirm(`Demote ${identity(member)} from Owner to ${roles[String(member.id)]}? Another Owner must remain.`)) return;
    void submit({ kind: 'role', id: String(member.id), role: roles[String(member.id)]!, operationId: crypto.randomUUID(), expectedRevision: member.revision });
}
function promote(member: Member) {
    if (!workspaceAccess.value?.capabilities.canManageOwners || member.role === 'Owner' || member.revision == null || !window.confirm(`Make ${identity(member)} an Owner? Owners can appoint and remove other Owners and control all workspace settings.`)) return;
    void submit({ kind: 'promote', id: String(member.id), operationId: crypto.randomUUID(), expectedRevision: member.revision });
}
function remove(member: Member) {
    if (!editable(member) || member.revision == null || !window.confirm(`Remove ${identity(member)} from this workspace? Their future access will end. Work already admitted may finish. Another Owner must remain.`)) return;
    void submit({ kind: 'remove', id: String(member.id), operationId: crypto.randomUUID(), expectedRevision: member.revision });
}
function savePolicy() {
    if (policyRevision.value == null) return;
    void submit({ kind: 'policy', operationId: crypto.randomUUID(), expectedRevision: policyRevision.value, includeUnreleased: includeUnreleased.value });
}
watch(() => props.generation, () => { if (!busy.value && !pending.value && !hasDraft.value) void read(); });
onMounted(() => { void read(); });
const stop = onAccessLost(() => { sequence++; controller?.abort(); members.value = []; pending.value = undefined; });
onUnmounted(() => { disposed = true; sequence++; controller?.abort(); pending.value = undefined; stop(); });
</script>

<template>
    <section class="workspace-members" aria-label="Workspace members" :aria-busy="loading || busy">
        <div class="actions"><p class="muted">Roles apply only to this workspace.</p><button type="button" :disabled="loading || busy || !online" @click="read">Refresh members</button></div>
        <p v-if="error" class="notice error" role="alert">{{ error }}</p>
        <p v-if="feedback" class="notice" role="status">{{ feedback }}</p>
        <button v-if="pending" type="button" :disabled="busy || !online" @click="submit()">Check previous change</button>
        <p v-if="loading && !loaded" role="status">Loading members and Viewer visibility…</p>
        <template v-if="loaded">
            <section class="member-policy" aria-labelledby="viewer-visibility-title">
                <h3 id="viewer-visibility-title">Viewer visibility</h3>
                <p>Released only shows active, released photographs that are not currently rejected. The broader setting also shows unreleased and rejected active photographs. This applies to every Viewer in this workspace; neither setting includes Trash.</p>
                <label>Photographs visible to Viewers<select v-model="includeUnreleased" :disabled="busy || loading || !!error || !!pending || !online"><option :value="false">Released only</option><option :value="true">Also unreleased and rejected</option></select></label>
                <button type="button" :disabled="busy || loading || !!error || !!pending || !online" @click="savePolicy">Save Viewer visibility</button>
            </section>
            <h3>Members</h3>
            <ul class="member-list">
                <li v-for="member in members" :key="String(member.id)" class="member-row">
                    <div class="member-identity"><strong>{{ identity(member) }}<template v-if="String(member.id) === workspaceAccess?.membershipId"> · You</template></strong><span>{{ member.role }}{{ member.isActive ? '' : ' · inactive' }}</span><details class="section-disclosure"><summary><UIcon name="i-lucide-chevron-right" class="disclosure-chevron" aria-hidden="true" />Identity details</summary><p>Sign-in issuer: {{ member.issuer }}</p><p>Subject: {{ member.subject }}</p><p>Membership: {{ member.id }}</p></details></div>
                    <div v-if="editable(member)" class="member-actions">
                        <label>Role for {{ identity(member) }}<select v-model="roles[String(member.id)]" :disabled="busy || loading || !!error || !!pending || !online"><option>Administrator</option><option>Curator</option><option>Viewer</option></select></label>
                        <div class="actions"><button type="button" :disabled="busy || loading || !!error || !!pending || !online || roles[String(member.id)] === member.role" @click="changeRole(member)">{{ member.role === 'Owner' ? 'Demote Owner' : 'Save role' }}</button><button type="button" :disabled="busy || loading || !!error || !!pending || !online" @click="remove(member)">Remove member</button></div>
                        <button v-if="workspaceAccess?.capabilities.canManageOwners && member.role !== 'Owner'" type="button" :disabled="busy || loading || !!error || !!pending || !online" @click="promote(member)">Make Owner…</button>
                    </div>
                </li>
            </ul>
            <p v-if="!members.length" class="muted">No members were returned. Refresh before changing access.</p>
            <WorkspaceInvitationManagement :online="online" :generation="generation" />
        </template>
    </section>
</template>
