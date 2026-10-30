<script setup lang="ts">
import {
    session,
    accessMessage,
    signIn,
    signOut,
    restoreSession,
    restoring,
} from "./lib/session";
import { onAccessLost } from './lib/session';
import { pageWorkspaceId, workspaceSelection, withWorkspace, type WorkspaceMembership } from './lib/workspace-context';
const route = useRoute();
const { sftpgo } = useAppConfig();
const workspace = ref<{ requestDeparture: () => boolean } | null>(null);
const memberships = shallowRef<WorkspaceMembership[]>([]);
const selectedWorkspaceId = shallowRef(pageWorkspaceId ?? '');
const workspaceIncomplete = shallowRef(false);
const selectionRequired = shallowRef<boolean | undefined>();
const hasDiscovery = shallowRef(false);
const discoveryBusy = shallowRef(false);
const discoveryError = shallowRef(false);
let discoverySequence = 0;
let discoveryController: AbortController | undefined;
const activeWorkspaceSelection = computed(() => workspaceSelection(
    memberships.value,
    workspaceIncomplete.value,
    selectionRequired.value === true,
    pageWorkspaceId,
));
const canMountOperationalWorkspace = computed(() => hasDiscovery.value &&
    ['legacy', 'selected'].includes(activeWorkspaceSelection.value.state));
const activeWorkspace = computed(() => activeWorkspaceSelection.value.state === 'selected'
    ? activeWorkspaceSelection.value.workspace
    : undefined);
const signInVisible = ref(true);
const aboutDialog = ref<HTMLDialogElement>();
const aboutTrigger = ref<HTMLButtonElement>();
function openAbout() {
    aboutDialog.value?.showModal();
}
function restoreAboutFocus() { aboutTrigger.value?.focus({ preventScroll: true }); }
function leaveSession() {
    if (workspace.value?.requestDeparture() === false) return;
    void signOut();
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
        const { api, cancellable, handleFailure } = await import('./lib/api');
        const result = await api.workspaces.get({ options: [cancellable(controller.signal)] });
        controller.signal.throwIfAborted();
        if (current !== discoverySequence) return;
        const items = validMemberships(result?.items);
        if (typeof result?.incomplete !== 'boolean' || typeof result.selectionRequired !== 'boolean')
            throw new Error('Workspace membership response is invalid.');
        if (!result.selectionRequired && items.length)
            throw new Error('Workspace membership response is invalid.');
        memberships.value = items;
        workspaceIncomplete.value = result.incomplete;
        selectionRequired.value = result.selectionRequired;
        hasDiscovery.value = true;
        selectedWorkspaceId.value = items.some(item => item.id === pageWorkspaceId) ? pageWorkspaceId! : '';
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
function switchWorkspace(event: Event) {
    const select = event.target as HTMLSelectElement;
    const next = select.value;
    if (!next || next === pageWorkspaceId || !memberships.value.some(item => item.id === next)) {
        selectedWorkspaceId.value = activeWorkspace.value?.id ?? '';
        select.value = activeWorkspace.value?.id ?? '';
        return;
    }
    if (workspace.value?.requestDeparture() === false) {
        selectedWorkspaceId.value = activeWorkspace.value?.id ?? '';
        select.value = activeWorkspace.value?.id ?? '';
        return;
    }
    window.location.assign(withWorkspace('/', next));
}
const stopWatchingSession = watch([session, () => route.path], ([value]) => {
    if (value && !route.path.startsWith('/auth/')) void loadMemberships();
    else {
        discoveryController?.abort();
        discoverySequence++;
        memberships.value = [];
        workspaceIncomplete.value = false;
        selectionRequired.value = undefined;
        hasDiscovery.value = false;
        discoveryBusy.value = false;
        discoveryError.value = false;
        selectedWorkspaceId.value = pageWorkspaceId ?? '';
    }
});
const unsubscribeAccessLost = onAccessLost(() => {
    discoveryController?.abort();
    discoverySequence++;
    memberships.value = [];
    workspaceIncomplete.value = false;
    selectionRequired.value = undefined;
    hasDiscovery.value = false;
});
function updateSignInVisibility() { signInVisible.value = !document.hidden; }
onMounted(() => {
    updateSignInVisibility();
    document.addEventListener('visibilitychange', updateSignInVisibility);
    if (!route.path.startsWith("/auth/"))
        void restoreSession(route.fullPath);
});
onUnmounted(() => document.removeEventListener('visibilitychange', updateSignInVisibility));
onUnmounted(() => { discoveryController?.abort(); stopWatchingSession(); unsubscribeAccessLost(); });
</script>
<template>
    <UApp
        ><div class="app-shell">
            <ApertureBackground v-if="!session && !route.path.startsWith('/auth/')" view="Sources" :motion-enabled="signInVisible" :hidden="false" continuous />
            <header class="app-header">
                <NuxtLink :to="withWorkspace('/', pageWorkspaceId)" class="brand"
                    ><img
                        src="/shutter-color.png"
                        alt=""
                        width="56"
                        height="56"
                    /><span class="brand-name">ShutterSpool</span><span id="brand-live-status" /></NuxtLink
                ><div id="workspace-header-actions"><label v-if="session && activeWorkspace" class="workspace-switcher"><span class="sr-only">Workspace</span><select aria-label="Workspace" :value="activeWorkspace.id" @change="switchWorkspace"><option v-for="item in memberships" :key="item.id" :value="item.id">{{ item.name }}</option></select></label><button ref="aboutTrigger" class="about-access" type="button" aria-label="About & licenses" title="About & licenses" @click="openAbout"><UIcon name="i-lucide-info" /><span class="about-label">About &amp; licenses</span></button><UButton
                    v-if="session"
                    class="signout-button"
                    aria-label="Sign out"
                    title="Sign out"
                    color="neutral"
                    variant="ghost"
                    @click="leaveSession"
                    ><UIcon class="signout-icon" name="i-lucide-log-out" /><span class="signout-label">Sign out</span></UButton
                ></div>
            </header>
            <div v-if="session && activeWorkspace" class="workspace-identity">
                <span>Current workspace</span>
                <strong>{{ activeWorkspace.name }}</strong>
            </div>
            <dialog ref="aboutDialog" class="discard-dialog about-dialog" aria-labelledby="about-title" @close="restoreAboutFocus">
                <div class="about-heading"><h2 id="about-title">About &amp; licenses</h2><button type="button" @click="aboutDialog?.close()">Close</button></div>
                <p>ShutterSpool is licensed under the MIT License. <a href="https://github.com/SeWieland/ShutterSpool/blob/778eb89e41cb148081a0a50fd49203cd2a9a2602/LICENSE" target="_blank" rel="noopener noreferrer">Read the license</a>.</p>
                <p>SFTPGo® is a registered trademark of Nicola Murino. Copyright Nicola Murino and contributors. ShutterSpool is not affiliated with or endorsed by Nicola Murino or the SFTPGo project.</p>
                <p>The packaged camera FTP/FTPS edge uses SFTPGo v{{ sftpgo.version.replace(/^v/, '') }}, licensed under GNU AGPL-3.0-only with additional terms. Read the <a :href="sftpgo.license" target="_blank" rel="noopener noreferrer">license</a> and <a :href="sftpgo.notice" target="_blank" rel="noopener noreferrer">notice</a>.</p>
                <p><a :href="sftpgo.source" target="_blank" rel="noopener noreferrer">Source code for SFTPGo v{{ sftpgo.version.replace(/^v/, '') }}</a></p>
            </dialog>
            <NuxtPage v-if="route.path.startsWith('/auth/')" />
            <template v-else>
            <p v-if="session && canMountOperationalWorkspace && (workspaceIncomplete || discoveryError)" class="workspace-incomplete" :role="discoveryError ? 'alert' : 'status'"><template v-if="discoveryError">Workspace memberships could not be refreshed. Access to {{ activeWorkspace?.name ?? 'this workspace' }} remains confirmed.</template><template v-else>Some workspace memberships could not be checked. Access to {{ activeWorkspace?.name ?? 'this workspace' }} is confirmed.</template><button type="button" :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</button></p>
            <Suspense v-if="session && canMountOperationalWorkspace">
                <LazyOperationalWorkspace
                    ref="workspace"
                    :capture-id="route.params.id ? String(route.params.id) : undefined"
                />
                <template #fallback>
                    <main class="empty" aria-busy="true" role="status">
                        <h1>Opening your workspace…</h1>
                    </main>
                </template>
            </Suspense>
            <main v-else-if="session && discoveryBusy && !hasDiscovery" class="empty" aria-busy="true" role="status">
                <h1>Checking workspace access…</h1>
                <p>Only workspaces assigned to your sign-in can be opened.</p>
            </main>
            <main v-else-if="session && discoveryError && !hasDiscovery" class="empty" role="alert">
                <h1>Workspaces could not be checked</h1>
                <p>Workspace access could not be confirmed. Retry to continue.</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</UButton>
            </main>
            <main v-else-if="session && activeWorkspaceSelection.state === 'empty'" class="empty">
                <h1>No workspace access</h1>
                <p>No workspace is assigned to this sign-in yet. Contact the operator if you expected access.</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</UButton>
            </main>
            <main v-else-if="session && activeWorkspaceSelection.state === 'incomplete'" class="empty" role="alert">
                <h1>{{ pageWorkspaceId ? 'Workspace access could not be confirmed' : 'Workspace memberships could not be checked' }}</h1>
                <p>{{ pageWorkspaceId ? 'Retry to check this workspace again. No other workspace has been opened.' : 'No workspace was confirmed in this incomplete check. Retry to continue.' }}</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</UButton>
                <form v-if="memberships.length" class="workspace-choice" @submit.prevent="chooseWorkspace">
                    <label for="workspace-choice-incomplete">Or choose a listed workspace</label>
                    <select id="workspace-choice-incomplete" v-model="selectedWorkspaceId"><option value="">Choose a workspace</option><option v-for="item in memberships" :key="item.id" :value="item.id">{{ item.name }}</option></select>
                    <UButton type="submit" :disabled="!memberships.some(item => item.id === selectedWorkspaceId)">Open workspace</UButton>
                </form>
            </main>
            <main v-else-if="session && activeWorkspaceSelection.state === 'unavailable'" class="empty" role="alert">
                <h1>This workspace is unavailable</h1>
                <p>Access to the selected workspace could not be confirmed. Choose one you can access or retry.</p>
                <UButton :disabled="discoveryBusy" @click="loadMemberships">{{ discoveryBusy ? 'Checking…' : 'Retry' }}</UButton>
                <form v-if="memberships.length" class="workspace-choice" @submit.prevent="chooseWorkspace">
                    <label for="workspace-choice-unavailable">Choose a workspace</label>
                    <select id="workspace-choice-unavailable" v-model="selectedWorkspaceId"><option value="">Choose a workspace</option><option v-for="item in memberships" :key="item.id" :value="item.id">{{ item.name }}</option></select>
                    <UButton type="submit" :disabled="!memberships.some(item => item.id === selectedWorkspaceId)">Open workspace</UButton>
                </form>
            </main>
            <main v-else-if="session && activeWorkspaceSelection.state === 'choose'" class="signin workspace-choice-page">
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
            </main>
            <main v-else-if="session" class="empty" aria-busy="true" role="status">
                <h1>Checking workspace access…</h1>
            </main>
            <main v-else-if="restoring" class="empty" aria-busy="true" role="status">
                <h1>Restoring your session…</h1>
                <p>Checking your sign-in. Your photographs will open when you are ready.</p>
            </main>
            <main v-else class="signin">
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
