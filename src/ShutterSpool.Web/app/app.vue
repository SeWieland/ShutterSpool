<script setup lang="ts">
import {
    session,
    accessMessage,
    signIn,
    signOut,
    restoreSession,
    restoring,
} from "./lib/session";
const route = useRoute();
const { sftpgo } = useAppConfig();
const workspace = ref<{ requestDeparture: () => boolean } | null>(null);
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
function updateSignInVisibility() { signInVisible.value = !document.hidden; }
onMounted(() => {
    updateSignInVisibility();
    document.addEventListener('visibilitychange', updateSignInVisibility);
    if (!route.path.startsWith("/auth/"))
        void restoreSession(route.fullPath);
});
onUnmounted(() => document.removeEventListener('visibilitychange', updateSignInVisibility));
</script>
<template>
    <UApp
        ><div class="app-shell">
            <ApertureBackground v-if="!session && !route.path.startsWith('/auth/')" view="Sources" :motion-enabled="signInVisible" :hidden="false" continuous />
            <header class="app-header">
                <NuxtLink to="/" class="brand"
                    ><img
                        src="/shutter-color.png"
                        alt=""
                        width="56"
                        height="56"
                    /><span class="brand-name">ShutterSpool</span><span id="brand-live-status" /></NuxtLink
                ><div id="workspace-header-actions"><button ref="aboutTrigger" class="about-access" type="button" aria-label="About & licenses" title="About & licenses" @click="openAbout"><UIcon name="i-lucide-info" /><span class="about-label">About &amp; licenses</span></button><UButton
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
            <dialog ref="aboutDialog" class="discard-dialog about-dialog" aria-labelledby="about-title" @close="restoreAboutFocus">
                <div class="about-heading"><h2 id="about-title">About &amp; licenses</h2><button type="button" @click="aboutDialog?.close()">Close</button></div>
                <p>ShutterSpool is licensed under the MIT License. <a href="https://github.com/SeWieland/ShutterSpool/blob/778eb89e41cb148081a0a50fd49203cd2a9a2602/LICENSE" target="_blank" rel="noopener noreferrer">Read the license</a>.</p>
                <p>SFTPGo® is a registered trademark of Nicola Murino. Copyright Nicola Murino and contributors. ShutterSpool is not affiliated with or endorsed by Nicola Murino or the SFTPGo project.</p>
                <p>The packaged camera FTP/FTPS edge uses SFTPGo v{{ sftpgo.version.replace(/^v/, '') }}, licensed under GNU AGPL-3.0-only with additional terms. Read the <a :href="sftpgo.license" target="_blank" rel="noopener noreferrer">license</a> and <a :href="sftpgo.notice" target="_blank" rel="noopener noreferrer">notice</a>.</p>
                <p><a :href="sftpgo.source" target="_blank" rel="noopener noreferrer">Source code for SFTPGo v{{ sftpgo.version.replace(/^v/, '') }}</a></p>
            </dialog>
            <NuxtPage v-if="route.path.startsWith('/auth/')" />
            <Suspense v-else-if="session">
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
        </div></UApp
    >
</template>
