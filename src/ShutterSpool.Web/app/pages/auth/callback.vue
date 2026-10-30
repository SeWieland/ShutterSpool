<script setup lang="ts">
import { completeSignIn, accessMessage } from '../../lib/session'
import { initializePageWorkspaceFromReturnTarget } from '../../lib/workspace-context'
const failed = ref(false)
onMounted(async () => {
    try {
        const target = await completeSignIn()
        if (target) {
            initializePageWorkspaceFromReturnTarget(target)
            await navigateTo(target, { replace: true })
        }
    } catch {
        failed.value = true
        accessMessage.value = 'Sign-in could not complete. Return to ShutterSpool and try again.'
    }
})
</script>
<template><main class="empty"><h1>{{ failed ? 'Sign-in did not complete' : 'Completing sign-in…' }}</h1><NuxtLink v-if="failed" to="/" class="back">Return to ShutterSpool</NuxtLink></main></template>
