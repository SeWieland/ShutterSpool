<script setup lang="ts">
import { getManager, clearAccess } from '../../lib/session'
onMounted(async () => {
    clearAccess('You are signed out.')
    try { await (await getManager()).signoutRedirectCallback() }
    catch { /* Local private state is already cleared. */ }
    await navigateTo('/', { replace: true })
})
</script>
<template><main class="empty"><h1>Signed out</h1><p>Returning to ShutterSpool…</p></main></template>
