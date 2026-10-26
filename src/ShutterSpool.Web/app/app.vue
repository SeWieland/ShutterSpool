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
const workspace = ref<{ requestDeparture: () => boolean } | null>(null);
const signInVisible = ref(true);
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
                    />ShutterSpool<span id="brand-live-status" /></NuxtLink
                ><div v-if="session" id="workspace-header-actions"><UButton
                    class="signout-button"
                    aria-label="Sign out"
                    title="Sign out"
                    color="neutral"
                    variant="ghost"
                    @click="leaveSession"
                    ><UIcon class="signout-icon" name="i-lucide-log-out" /><span class="signout-label">Sign out</span></UButton
                ></div>
            </header>
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
