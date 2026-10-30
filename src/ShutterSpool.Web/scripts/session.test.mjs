import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
import { parse } from '@vue/compiler-sfc'
import { ref, shallowRef } from 'vue'

function client(silent, callbackUser) {
  const calls = { redirects: [], removed: 0, callbacks: 0 }
  let settings
  class Store {
    constructor(value) { this.options = value }
    getAllKeys = async () => []
    remove = async () => {}
  }
  class Manager {
    constructor(value) { settings = value; this.settings = value }
    events = { addAccessTokenExpiring() {}, addAccessTokenExpired() {}, addUserSignedOut() {} }
    clearStaleState = async () => {}
    signinSilent = silent
    signinRedirectCallback = async () => { calls.callbacks++; return callbackUser }
    signinRedirect = async args => { calls.redirects.push(args) }
    removeUser = async () => { calls.removed++ }
    stopSilentRenew() {}
  }
  const browserWindow = { parent: undefined }
  browserWindow.parent = browserWindow
  const context = vm.createContext({
    exports: {}, ref, shallowRef, clearNuxtState() {}, URL, AbortSignal, window: browserWindow,
    location: { origin: 'https://photos.example', pathname: '/captures/shot', search: '' },
    sessionStorage: {}, setTimeout: () => 0,
    fetch: async () => ({ ok: true, json: async () => ({ authority: 'https://issuer.example', clientId: 'spa' }) }),
    require: () => ({ UserManager: Manager, WebStorageStateStore: Store, InMemoryWebStorage: class {} }),
  })
  const source = readFileSync(new URL('../app/lib/session.ts', import.meta.url), 'utf8')
  vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context)
  return { api: context.exports, calls, settings: () => settings }
}

test('simultaneous expiring-token reads share renewal and keep session generation', async () => {
  let finish, renewals = 0
  const c = client(() => { renewals++; return new Promise(resolve => { finish = resolve }) })
  c.api.session.value = { access_token: 'old', expires_in: 10 }
  const generation = c.api.accessGeneration()
  const tokens = [c.api.token(), c.api.token(), c.api.token()]
  await new Promise(resolve => setImmediate(resolve))
  finish({ access_token: 'new', expires_in: 300 })
  assert.deepEqual(await Promise.all(tokens), ['new', 'new', 'new'])
  assert.equal(renewals, 1)
  assert.equal(c.api.accessGeneration(), generation)
})

test('revocation during renewal cannot restore private access', async () => {
  let finish
  const c = client(() => new Promise(resolve => { finish = resolve }))
  c.api.session.value = { access_token: 'old', expires_in: 10 }
  const pending = c.api.token()
  await new Promise(resolve => setImmediate(resolve))
  c.api.clearAccess('Grant revoked')
  finish({ access_token: 'new', expires_in: 300 })
  await assert.rejects(pending)
  assert.equal(c.api.session.value, null)
  assert.equal(c.api.accessMessage.value, 'Grant revoked')
})

test('workspace permission loss clears private consumers while retaining the shared issuer login', () => {
  const c = client(async () => null)
  const user = { access_token: 'shared-login', expires_in: 300 }
  c.api.session.value = user
  let cleared = 0
  c.api.onAccessLost(() => cleared++)
  const generation = c.api.accessGeneration()
  c.api.clearPrivateAccess()
  assert.equal(c.api.session.value, user)
  assert.equal(c.api.accessGeneration(), generation + 1)
  assert.equal(cleared, 1)
  assert.equal(c.calls.removed, 0, 'workspace access loss must not remove the OIDC user')
})

test('workspace cache invalidation during token renewal does not interrupt the shared OIDC session', async () => {
  let finish
  const c = client(() => new Promise(resolve => { finish = resolve }))
  c.api.session.value = { access_token: 'old', expires_in: 10 }
  const pending = c.api.token()
  await new Promise(resolve => setImmediate(resolve))
  c.api.clearPrivateAccess()
  finish({ access_token: 'renewed', expires_in: 300 })
  assert.equal(await pending, 'renewed')
  assert.equal(c.api.session.value.access_token, 'renewed')
  assert.equal(c.calls.removed, 0)
})

test('reload uses one silent attempt then a credential-free top-level fallback', async () => {
  const c = client(async () => { throw new Error('iframe blocked') })
  await c.api.restoreSession('/captures/shot')
  assert.equal(c.calls.redirects.length, 1)
  assert.equal(c.calls.redirects[0].prompt, 'none')
  assert.equal(c.calls.redirects[0].state.returnTo, '/captures/shot')
  assert.equal(c.settings().automaticSilentRenew, false)
  assert.ok(c.settings().stateStore.options.store)
  assert.notEqual(c.settings().userStore.options.store, c.settings().stateStore.options.store)
})

test('return targets cannot leave the origin or replay an auth callback', () => {
  const c = client(async () => null)
  for (const path of ['//evil.example/', 'https://evil.example', '/auth/callback?code=secret', '/\\evil.example'])
    assert.equal(c.api.localReturnTarget(path), '/')
  assert.equal(c.api.localReturnTarget('/captures/shot?view=detail'), '/captures/shot?view=detail')
  assert.equal(c.api.localReturnTarget('/?workspace=00000000-0000-0000-0000-000000000001'), '/?workspace=00000000-0000-0000-0000-000000000001')
})

test('top-level callback keeps its token and initializes workspace scope before SPA return navigation', async () => {
  const workspaceId = '2f2ed010-2a51-468a-a38a-a2372fba8211'
  const callbackUser = { access_token: 'callback-token', state: { returnTo: `/?workspace=${workspaceId}` } }
  const c = client(async () => { throw new Error('iframe unavailable') }, callbackUser)
  const workspaceContext = vm.createContext({ exports: {}, URL, URLSearchParams,
    window: { location: { origin: 'https://photos.example', pathname: '/auth/callback', search: '?code=oidc-code&workspace=untrusted' } },
  })
  vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/workspace-context.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, workspaceContext)
  let mounted
  let reloads = 0
  const navigations = []
  const callbackContext = vm.createContext({ exports: {}, ref,
    window: { location: { replace: () => { reloads++ } } },
    onMounted: action => { mounted = action },
    navigateTo: async (target, options) => {
      assert.equal(workspaceContext.exports.pageWorkspaceId, workspaceId, 'scope is initialized before the app route changes')
      assert.equal(c.api.session.value, callbackUser, 'the OIDC token remains in the in-memory session')
      navigations.push({ target, options })
    },
    require: name => name.endsWith('/session') ? c.api : workspaceContext.exports,
  })
  const callback = parse(readFileSync(new URL('../app/pages/auth/callback.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content
  vm.runInContext(ts.transpileModule(callback, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, callbackContext)
  await mounted()
  assert.equal(c.calls.callbacks, 1)
  assert.equal(c.calls.redirects.length, 0, 'return navigation does not start another top-level prompt:none flow')
  assert.equal(c.api.session.value.access_token, 'callback-token')
  assert.equal(workspaceContext.exports.pageWorkspaceId, workspaceId)
  assert.equal(navigations.length, 1)
  assert.equal(navigations[0].target, `/?workspace=${workspaceId}`)
  assert.equal(navigations[0].options.replace, true)
  assert.equal(reloads, 0, 'callback must not discard the in-memory OIDC session with a document reload')
})

test('offline renewal retains the active session; issuer revocation ends it', async () => {
  const offline = client(async () => { throw new TypeError('network unavailable') })
  offline.api.session.value = { access_token: 'still-valid', expires_in: 10, expired: false }
  assert.equal(await offline.api.token(), 'still-valid')
  assert.ok(offline.api.session.value)
  const revoked = client(async () => { throw { error: 'invalid_grant' } })
  revoked.api.session.value = { access_token: 'revoked', expires_in: 10 }
  await assert.rejects(revoked.api.token())
  assert.equal(revoked.api.session.value, null)
})
