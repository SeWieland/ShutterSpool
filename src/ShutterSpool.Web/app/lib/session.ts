import {
    InMemoryWebStorage,
    UserManager,
    WebStorageStateStore,
    type User,
} from "oidc-client-ts";

export const session = shallowRef<User | null>(null);
export const accessMessage = ref("");
export const restoring = ref(false);
let manager: UserManager | undefined;
let initialization: Promise<UserManager> | undefined;
let renewal: Promise<User> | undefined;
let generation = 0;
const cleared = new Set<() => void>();

async function clearTransactions() {
    const store = manager?.settings.stateStore;
    try {
        if (store) for (const key of await store.getAllKeys()) await store.remove(key);
    } catch { /* Storage can be disabled; access remains cleared and transaction expiry still applies. */ }
}

export function accessGeneration() { return generation; }

export function localReturnTarget(value: unknown) {
    if (typeof value !== "string" || !value.startsWith("/")) return "/";
    const url = new URL(value, location.origin);
    return url.origin === location.origin && !url.pathname.startsWith("/auth/")
        ? `${url.pathname}${url.search}${url.hash}` : "/";
}

export function onAccessLost(callback: () => void) {
    cleared.add(callback);
    return () => cleared.delete(callback);
}

export function clearAccess(
    message = "Your session ended. Sign in again to continue.",
) {
    generation++;
    session.value = null;
    accessMessage.value = message;
    manager?.stopSilentRenew();
    clearNuxtState(
        (key) =>
            key.startsWith("capture-draft-") ||
            key.startsWith("capture-conflict-"),
    );
    void manager?.removeUser();
    void clearTransactions();
    for (const callback of cleared) callback();
}

export function getManager() {
    return (initialization ??= (async () => {
        const response = await fetch("/auth/config", { signal: AbortSignal.timeout(15_000) });
        if (!response.ok)
            throw new Error("Sign-in configuration is unavailable.");
        const config = (await response.json()) as {
            authority: string;
            clientId: string;
        };
        manager = new UserManager({
            authority: config.authority,
            client_id: config.clientId,
            redirect_uri: `${location.origin}/auth/callback`,
            silent_redirect_uri: `${location.origin}/auth/callback`,
            post_logout_redirect_uri: `${location.origin}/auth/signout`,
            response_type: "code",
            scope: "openid profile",
            userStore: new WebStorageStateStore({
                store: new InMemoryWebStorage(),
            }),
            stateStore: new WebStorageStateStore({
                store: sessionStorage,
                prefix: "shutterspool.oidc.",
            }),
            staleStateAgeInSeconds: 300,
            requestTimeoutInSeconds: 15,
            silentRequestTimeoutInSeconds: 8,
            automaticSilentRenew: false,
        });
        await manager.clearStaleState();
        setTimeout(() => { void manager?.clearStaleState(); }, 301_000);
        manager.events.addAccessTokenExpiring(() => { void renew().catch(() => {}); });
        manager.events.addAccessTokenExpired(() => { void renew().catch(() => {}); });
        manager.events.addUserSignedOut(() => clearAccess());
        return manager;
    })().catch((error) => {
        initialization = undefined;
        throw error;
    }));
}

export async function signIn() {
    accessMessage.value = "";
    try {
        await (await getManager()).signinRedirect({
            state: { returnTo: localReturnTarget(`${location.pathname}${location.search}`) },
        });
    } catch {
        await clearTransactions();
        accessMessage.value =
            "Sign-in is unavailable. Check your connection and try again.";
    }
}

export async function signOut() {
    const active = manager;
    const idToken = session.value?.id_token;
    clearAccess("You are signed out.");
    try {
        await active?.signoutRedirect({ id_token_hint: idToken });
    } catch {
        /* Local private state is already cleared. */
    }
}

async function renew() {
    if (renewal) return renewal;
    const current = generation;
    renewal = (async () => {
        try {
            const active = await getManager();
            if (current !== generation) throw new Error("Access ended.");
            return await recoverUser(active, current);
        } catch (error) {
            await clearTransactions();
            const code = (error as { error?: string })?.error;
            if (current === generation &&
                ["invalid_grant", "login_required", "interaction_required", "consent_required", "access_denied"].includes(code ?? ""))
                clearAccess();
            else if (current === generation && session.value && !session.value.expired)
                return session.value;
            throw error;
        }
    })().finally(() => { renewal = undefined; });
    return renewal;
}

async function recoverUser(active: UserManager, current: number) {
    if (current !== generation) throw new Error("Access ended.");
    const user = await active.signinSilent();
    if (current !== generation) {
        await active.removeUser();
        throw new Error("Access ended.");
    }
    if (!user || user.expired) throw { error: "login_required" };
    session.value = user;
    return user;
}

export async function restoreSession(returnTo: string) {
    restoring.value = true;
    const current = generation;
    try {
        const active = await getManager();
        try {
            await recoverUser(active, current);
        } catch {
            await clearTransactions();
            // Third-party cookie restrictions can block iframe SSO; try the issuer once at top level.
            if (current === generation)
                await active.signinRedirect({ prompt: "none", state: { returnTo: localReturnTarget(returnTo) } });
        }
    } catch {
        await clearTransactions();
        accessMessage.value = "Unable to restore your session. Check your connection and sign in again.";
    } finally {
        restoring.value = false;
    }
}

export async function completeSignIn() {
    const active = await getManager();
    if (window.parent !== window) {
        await active.signinSilentCallback();
        return;
    }
    try {
        const user = await active.signinRedirectCallback();
        session.value = user;
        return localReturnTarget((user.state as { returnTo?: string })?.returnTo);
    } catch {
        clearAccess("Your issuer session could not be restored or sign-in was cancelled. Sign in to continue.");
        return "/";
    }
}

export async function token() {
    let user = session.value;
    if (!user) {
        clearAccess();
        throw new Error("Sign in to continue.");
    }
    if (renewal || user.expired || (user.expires_in ?? 0) <= 60) user = await renew();
    return user.access_token;
}
