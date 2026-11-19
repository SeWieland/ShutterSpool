<script setup lang="ts">
import {
    session,
    accessMessage,
    onAccessLost,
    signIn,
    signOut,
    restoreSession,
    restoring,
} from "./lib/session";
import { getPageWorkspaceId, bindInitialWorkspace, workspaceSelection, withWorkspace, type WorkspaceMembership } from './lib/workspace-context';
import { workspaceAccess, workspaceAccessVersion, workspaceAccessError, workspaceRevoked, refreshWorkspaceAccess, discardWorkspacePrivateState } from './lib/workspace-access';
const route = useRoute();
const router = useRouter();
const { sftpgo } = useAppConfig();
const workspace = ref<{ requestDeparture: () => boolean } | null>(null);
const memberships = shallowRef<WorkspaceMembership[]>([]);
const selectedWorkspaceId = shallowRef(getPageWorkspaceId() ?? '');
const workspaceIncomplete = shallowRef(false);
const hasDiscovery = shallowRef(false);
const discoveryBusy = shallowRef(false);
const discoveryError = shallowRef(false);
const invitationsVisible = shallowRef(false);
const invitationCount = shallowRef<number>();
const profileOpen = ref(false);
const profilePanel = ref<HTMLDivElement>();
const profileTrigger = ref<HTMLButtonElement>();
const startup = ref(!route.path.startsWith('/auth/'));
const identity = computed(() => session.value?.profile.name?.trim() || session.value?.profile.preferred_username?.trim() || session.value?.profile.sub || 'Signed in');
const email = computed(() => typeof session.value?.profile.email === 'string' ? session.value.profile.email : '');
let discoverySequence = 0;
let discoveryController: AbortController | undefined;
const activeWorkspaceSelection = computed(() => hasDiscovery.value ? workspaceSelection(
    memberships.value,
    workspaceIncomplete.value,
    getPageWorkspaceId(),
) : { state: 'loading' } as const);
const hasSelectedWorkspace = computed(() => activeWorkspaceSelection.value.state === 'selected' &&
    activeWorkspaceSelection.value.workspace.id === getPageWorkspaceId());
const canMountOperationalWorkspace = computed(() => hasSelectedWorkspace.value && !!workspaceAccess.value);
const activeWorkspace = computed(() => activeWorkspaceSelection.value.state === 'selected'
    ? activeWorkspaceSelection.value.workspace
    : undefined);
const signInVisible = ref(true);
const aboutDialog = ref<HTMLDialogElement>();
function openAbout() {
    profilePanel.value?.hidePopover();
    aboutDialog.value?.showModal();
}
function restoreAboutFocus() { profileTrigger.value?.focus({ preventScroll: true }); }
function leaveSession() {
    if (workspace.value?.requestDeparture() === false) return;
    profilePanel.value?.hidePopover();
    void signOut();
}
function openInvitations() {
    profilePanel.value?.hidePopover();
    invitationsVisible.value = true;
}
function joinedWorkspace(id: string) {
    if (getPageWorkspaceId() === id && canMountOperationalWorkspace.value) invitationsVisible.value = false;
    else {
        if (workspace.value?.requestDeparture() === false) return;
        window.location.assign(withWorkspace('/', id));
    }
}
function validMemberships(value: unknown): WorkspaceMembership[] {
    if (!Array.isArray(value)) throw new Error('Workspace membership response is invalid.');
    return value.map(item => {
        if (!item || typeof item.id !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(item.id) ||
            typeof item.name !== 'string' || !item.name.trim())
            throw new Error('Workspace membership response is invalid.');
        return { id: item.id.toLowerCase(), name: item.name };
    });
}
async function loadMemberships() {
    discoveryController?.abort();
    const controller = new AbortController();
    discoveryController = controller;
    const current = ++discoverySequence;
    discoveryBusy.value = true;
    discoveryError.value = false;
    try {
        const { api, cancellable } = await import('./lib/api');
        const result = await api.workspaces.get({ options: [cancellable(controller.signal)] });
        controller.signal.throwIfAborted();
        if (current !== discoverySequence) return;
        const items = validMemberships(result?.items);
        if (typeof result?.incomplete !== 'boolean')
            throw new Error('Workspace membership response is invalid.');
        const bound = bindInitialWorkspace(items, result.incomplete);
        const workspaceId = getPageWorkspaceId();
        if (bound || workspaceId && !hasDiscovery.value && route.query.workspace !== workspaceId) {
            const failure = await router.replace(withWorkspace(route.fullPath, workspaceId));
            controller.signal.throwIfAborted();
            if (current !== discoverySequence) return;
            if (failure) throw new Error('Workspace selection could not be opened.');
        }
        memberships.value = items;
        workspaceIncomplete.value = result.incomplete;
        hasDiscovery.value = true;
        selectedWorkspaceId.value = items.find(item => item.id === getPageWorkspaceId())?.id ?? '';
        if (hasSelectedWorkspace.value) await refreshWorkspaceAccess();
    } catch (error) {
        if (current !== discoverySequence || controller.signal.aborted) return;
        const { handleFailure } = await import('./lib/api');
        handleFailure(error);
        discoveryError.value = true;
    } finally {
        if (current === discoverySequence) discoveryBusy.value = false;
    }
}
function chooseWorkspace() {
    if (!memberships.value.some(item => item.id === selectedWorkspaceId.value)) return;
    window.location.assign(withWorkspace('/', selectedWorkspaceId.value));
}
function switchWorkspace(next: string) {
    if (!next || next === getPageWorkspaceId() || !memberships.value.some(item => item.id === next)) return;
    if (workspace.value?.requestDeparture() === false) return;
    window.location.assign(withWorkspace('/', next));
}
const stopWatchingSession = watch([session, () => route.path], ([value]) => {
    if (value && !route.path.startsWith('/auth/')) void loadMemberships();
    else {
        discoveryController?.abort();
        discoverySequence++;
        memberships.value = [];
        workspaceIncomplete.value = false;
        hasDiscovery.value = false;
        discoveryBusy.value = false;
        discoveryError.value = false;
        selectedWorkspaceId.value = getPageWorkspaceId() ?? '';
        invitationsVisible.value = false;
        invitationCount.value = undefined;
        profileOpen.value = false;
    }
});
const unsubscribeAccessLost = onAccessLost(() => {
    if (session.value) return;
    discoveryController?.abort();
    discoverySequence++;
    memberships.value = [];
    workspaceIncomplete.value = false;
    hasDiscovery.value = false;
});
watch(workspaceRevoked, revoked => { if (revoked && session.value) void loadMemberships(); });
let accessTimer: ReturnType<typeof setInterval> | undefined;
function updateSignInVisibility() {
    signInVisible.value = !document.hidden;
    if (document.hidden && workspaceAccess.value?.role === 'Viewer') discardWorkspacePrivateState();
}
onMounted(() => {
    // Check hidden views too: cached permission may not keep private media open indefinitely.
    accessTimer = setInterval(() => {
        if (!session.value || !hasSelectedWorkspace.value) return;
        if (document.hidden && workspaceAccess.value?.role === 'Viewer') discardWorkspacePrivateState();
        void refreshWorkspaceAccess();
    }, 40_000);
    updateSignInVisibility();
    document.addEventListener('visibilitychange', updateSignInVisibility);
    if (!route.path.startsWith("/auth/"))
        void restoreSession(route.fullPath).finally(() => { startup.value = false; });
});
onUnmounted(() => document.removeEventListener('visibilitychange', updateSignInVisibility));
onUnmounted(() => { clearInterval(accessTimer); discoveryController?.abort(); stopWatchingSession(); unsubscribeAccessLost(); });
</script>
<template>
    <UApp
        ><div class="app-shell">
            <ApertureBackground v-if="!session && !route.path.startsWith('/auth/')" view="Sources" :motion-enabled="signInVisible" :hidden="false" continuous />
            <header class="app-header">
                <NuxtLink :to="withWorkspace('/', getPageWorkspaceId())" class="brand"
                    ><img
                        src="/shutter-color.png"
                        alt=""
                        width="56"
                        height="56"
                    /><span class="brand-name">ShutterSpool</span><span id="brand-live-status" /></NuxtLink
                ><div id="workspace-header-actions">
                        <button ref="profileTrigger" class="profile-access" type="button" popovertarget="profile-panel" :aria-expanded="profileOpen" aria-controls="profile-panel" aria-label="Profile" title="Profile"><UIcon name="i-lucide-user-round" /><span v-if="invitationCount" class="profile-badge"><span class="sr-only">{{ invitationCount }} pending invitations</span></span></button>
                            <div id="profile-panel" ref="profilePanel" popover class="profile-panel" @toggle="profileOpen = ($event as ToggleEvent).newState === 'open'">
                                <div v-if="session" class="profile-identity"><strong>{{ identity }}</strong><span v-if="email && email !== identity">{{ email }}</span></div>
                                <div v-if="session && memberships.length" class="profile-workspaces">
                                    <p class="muted">{{ memberships.length === 1 ? 'Workspace' : 'Workspaces' }}</p>
                                    <p v-if="memberships.length === 1"><strong>{{ memberships[0]?.name }}</strong><span v-if="workspaceAccess && activeWorkspace">{{ workspaceAccess.role }}</span></p>
                                    <template v-else><button v-for="item in memberships" :key="item.id" type="button" :aria-current="activeWorkspace?.id === item.id ? 'true' : undefined" @click="switchWorkspace(item.id)"><span>{{ item.name }}</span><span v-if="activeWorkspace?.id === item.id" class="muted">Current · {{ workspaceAccess?.role }}</span></button></template>
                                </div>
                                <button v-if="session" type="button" @click="openInvitations"><UIcon name="i-lucide-mail" />Invitations<span v-if="invitationCount" class="invitation-count">{{ invitationCount }}</span></button>
                                <button type="button" @click="openAbout"><UIcon name="i-lucide-info" />About &amp; licenses</button>
                                <button v-if="session" class="profile-signout" type="button" @click="leaveSession"><UIcon name="i-lucide-log-out" />Sign out</button>
                            </div>
                </div>
            </header>
            <dialog ref="aboutDialog" class="discard-dialog about-dialog" aria-labelledby="about-title" @close="restoreAboutFocus">
                <div class="about-heading"><h2 id="about-title">About &amp; licenses</h2><button type="button" @click="aboutDialog?.close()">Close</button></div>
                <p>ShutterSpool is licensed under the MIT License. <a href="https://github.com/SeWieland/ShutterSpool/blob/778eb89e41cb148081a0a50fd49203cd2a9a2602/LICENSE" target="_blank" rel="noopener noreferrer">Read the license</a>.</p>
                <p>SFTPGo® is a registered trademark of Nicola Murino. Copyright Nicola Murino and contributors. ShutterSpool is not affiliated with or endorsed by Nicola Murino or the SFTPGo project.</p>
                <p>The packaged camera FTP/FTPS edge uses SFTPGo v{{ sftpgo.version.replace(/^v/, '') }}, licensed under GNU AGPL-3.0-only with additional terms. Read the <a :href="sftpgo.license" target="_blank" rel="noopener noreferrer">license</a> and <a :href="sftpgo.notice" target="_blank" rel="noopener noreferrer">notice</a>.</p>
                <p><a :href="sftpgo.source" target="_blank" rel="noopener noreferrer">Source code for SFTPGo v{{ sftpgo.version.replace(/^v/, '') }}</a></p>
                <p>Lucide icons include ISC and MIT-licensed artwork. <a href="/lucide-LICENSE.txt" target="_blank" rel="noopener noreferrer">Read the icon licenses and notices</a>.</p>
            </dialog>
            <NuxtPage v-if="route.path.startsWith('/auth/')" />
            <template v-else>
            <p v-if="session && canMountOperationalWorkspace && (workspaceIncomplete || discoveryError)" class="workspace-incomplete" :role="discoveryError ? 'alert' : 'status'"><template v-if="discoveryError">Workspace memberships could not be refreshed. Access to {{ activeWorkspace?.name ?? 'this workspace' }} remains confirmed.</template><template v-else>Some workspace memberships could not be checked. Access to {{ activeWorkspace?.name ?? 'this workspace' }} is confirmed.</template><button type="button" :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</button></p>
            <main v-if="session" v-show="invitationsVisible || activeWorkspaceSelection.state === 'empty'" class="invitation-page" :class="{ 'invitation-welcome': activeWorkspaceSelection.state === 'empty' }">
                <button v-if="invitationsVisible && activeWorkspaceSelection.state !== 'empty'" type="button" class="back" @click="invitationsVisible = false">{{ canMountOperationalWorkspace ? 'Back to workspace' : 'Back to workspace access' }}</button>
                <div v-if="activeWorkspaceSelection.state === 'empty'" class="welcome-heading"><h1>Join a workspace to get started.</h1></div>
                <LazyWorkspaceInvitations :welcome="activeWorkspaceSelection.state === 'empty'" :email="email" @joined="joinedWorkspace" @count="invitationCount = $event" />
            </main>
            <Suspense v-if="session && canMountOperationalWorkspace">
                <LazyOperationalWorkspace
                    :key="workspaceAccessVersion"
                    ref="workspace"
                    :capture-id="route.params.id ? String(route.params.id) : undefined"
                    :inactive="invitationsVisible"
                    @navigate="invitationsVisible = false"
                />
                <template #fallback>
                    <main class="empty" aria-busy="true" role="status">
                        <h1>Opening ShutterSpool…</h1>
                    </main>
                </template>
            </Suspense>
            <main v-else-if="session && !invitationsVisible && activeWorkspaceSelection.state !== 'empty'">
            <section v-if="hasSelectedWorkspace && workspaceAccessError" class="empty" role="alert">
                <h1>Workspace access needs checking</h1>
                <p>{{ workspaceAccessError }}</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">Retry access check</UButton>
                <form v-if="memberships.length" class="workspace-choice" @submit.prevent="chooseWorkspace">
                    <label for="workspace-access-choice">Choose an available workspace</label>
                    <select id="workspace-access-choice" v-model="selectedWorkspaceId"><option value="">Choose a workspace</option><option v-for="item in memberships" :key="item.id" :value="item.id">{{ item.name }}</option></select>
                    <UButton type="submit" :disabled="!memberships.some(item => item.id === selectedWorkspaceId)">Open workspace</UButton>
                </form>
            </section>
            <section v-else-if="discoveryBusy && !hasDiscovery" class="empty" aria-busy="true" role="status">
                <h1>Opening ShutterSpool…</h1>
            </section>
            <section v-else-if="discoveryError && !hasDiscovery" class="empty" role="alert">
                <h1>Workspaces could not be checked</h1>
                <p>Workspace access could not be confirmed. Retry to continue.</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</UButton>
            </section>
            <section v-else-if="activeWorkspaceSelection.state === 'incomplete'" class="empty" role="alert">
                <h1>{{ getPageWorkspaceId() !== null ? 'Workspace access could not be confirmed' : 'Workspace memberships could not be checked' }}</h1>
                <p>{{ getPageWorkspaceId() !== null ? 'Retry to check this workspace again. No other workspace has been opened.' : 'No workspace was confirmed in this incomplete check. Retry to continue.' }}</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</UButton>
                <form v-if="memberships.length" class="workspace-choice" @submit.prevent="chooseWorkspace">
                    <label for="workspace-choice-incomplete">Or choose a listed workspace</label>
                    <select id="workspace-choice-incomplete" v-model="selectedWorkspaceId"><option value="">Choose a workspace</option><option v-for="item in memberships" :key="item.id" :value="item.id">{{ item.name }}</option></select>
                    <UButton type="submit" :disabled="!memberships.some(item => item.id === selectedWorkspaceId)">Open workspace</UButton>
                </form>
            </section>
            <section v-else-if="activeWorkspaceSelection.state === 'unavailable'" class="empty" role="alert">
                <h1>This workspace is unavailable</h1>
                <p>Access to the selected workspace could not be confirmed. Choose one you can access or retry.</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</UButton>
                <form v-if="memberships.length" class="workspace-choice" @submit.prevent="chooseWorkspace">
                    <label for="workspace-choice-unavailable">Choose a workspace</label>
                    <select id="workspace-choice-unavailable" v-model="selectedWorkspaceId"><option value="">Choose a workspace</option><option v-for="item in memberships" :key="item.id" :value="item.id">{{ item.name }}</option></select>
                    <UButton type="submit" :disabled="!memberships.some(item => item.id === selectedWorkspaceId)">Open workspace</UButton>
                </form>
            </section>
            <section v-else-if="activeWorkspaceSelection.state === 'choose'" class="signin workspace-choice-page">
                <img class="signin-mark" src="/shutter-color.png" alt="" width="112" height="112" />
                <h1>Choose a workspace</h1>
                <p>Open a workspace assigned to your sign-in.</p>
                <p v-if="workspaceIncomplete" role="status" class="notice">Some memberships could not be checked. You can open a listed workspace or retry.</p>
                <p v-if="discoveryError" role="alert" class="notice error">The last membership check failed. Listed access remains available; retry before continuing if the selection is unexpected.</p>
                <form class="workspace-choice" @submit.prevent="chooseWorkspace">
                    <label for="workspace-choice">Workspace</label>
                    <select id="workspace-choice" v-model="selectedWorkspaceId"><option value="">Choose a workspace</option><option v-for="item in memberships" :key="item.id" :value="item.id">{{ item.name }}</option></select>
                    <UButton type="submit" :disabled="!memberships.some(item => item.id === selectedWorkspaceId)">Open workspace</UButton>
                </form>
                <UButton v-if="workspaceIncomplete" variant="ghost" :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry membership check' }}</UButton>
            </section>
            <section v-else class="empty" aria-busy="true" role="status">
                <h1>Opening ShutterSpool…</h1>
            </section>
            </main>
            <main v-else-if="!session && (startup || restoring)" class="empty" aria-busy="true" role="status">
                <h1>Opening ShutterSpool…</h1>
            </main>
            <main v-else-if="!session" class="signin">
                <img class="signin-mark" src="/shutter-color.png" alt="" width="112" height="112" />
                <h1>Your photographs.<br />Your next decision.</h1>
                <p>
                    Sign in to choose your photographs and follow their delivery to
                    your destinations.
                </p>
                <p v-if="accessMessage" role="alert" class="notice">
                    {{ accessMessage }}
                </p>
                <UButton size="xl" @click="signIn">Sign in</UButton
                ><small>Continue with your configured sign-in service.</small>
            </main>
            </template>
        </div></UApp
    >
</template>
