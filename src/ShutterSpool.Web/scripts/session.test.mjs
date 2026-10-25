import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
import { ref, shallowRef } from 'vue'

function client(silent) {
  const calls = { redirects: [], removed: 0 }
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
    signinRedirect = async args => { calls.redirects.push(args) }
    removeUser = async () => { calls.removed++ }
    stopSilentRenew() {}
  }
  const context = vm.createContext({
    exports: {}, ref, shallowRef, clearNuxtState() {}, URL, AbortSignal,
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
