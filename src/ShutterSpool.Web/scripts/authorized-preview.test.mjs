import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import ts from 'typescript'
import { parse } from '@vue/compiler-sfc'
import { ref, reactive, watch, nextTick, effectScope } from 'vue'

function preview() {
  const file = readFileSync(new URL('../app/components/AuthorizedPreview.vue', import.meta.url), 'utf8')
  const source = parse(file).descriptor.scriptSetup.content
  const props = reactive({ captureId: 'capture', profile: 'detail', name: 'Synthetic Capture', sourceId: 'jpeg', generation: 0, forceGeneration: 0, maximumDimension: 512 })
  const mounted = []
  const unmounted = []
  const requests = []
  const events = []
  const document = Object.assign(new EventTarget(), { hidden: false })
  const scope = effectScope()
  let accessLost
  const context = vm.createContext({
    exports: {}, defineEmits: () => (...args) => events.push(args), defineProps: () => props, ref, watch, document, AbortController, URL,
    onMounted: callback => mounted.push(callback), onUnmounted: callback => unmounted.push(callback),
    IntersectionObserver: class { constructor(callback) { this.callback = callback } observe() { this.callback([{ isIntersecting: true }]) } disconnect() {} },
    require: name => name.endsWith('/api') ? {
      derivative: (captureId, profile, signal, etag) => new Promise((resolve, reject) => {
        requests.push({ captureId, profile, signal, etag, resolve, reject })
        signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true })
      }).then(async response => ({ response, blob: response.ok ? await response.blob() : undefined })),
    } : { onAccessLost: callback => { accessLost = callback; return () => {} } },
  })
  scope.run(() => vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context))
  vm.runInContext('element.value = {}', context)
  mounted.forEach(callback => callback())
  return {
    props, requests, document, events,
    read: expression => vm.runInContext(expression, context),
    clearAccess: () => accessLost(),
    close: () => { unmounted.forEach(callback => callback()); scope.stop() },
  }
}

async function settle() { await nextTick(); await new Promise(resolve => setImmediate(resolve)) }

test('a delayed derivative survives multiple ordinary reconciliations and reports unavailable', async () => {
  const view = preview()
  try {
    view.props.generation++
    await settle()
    view.props.generation++
    await settle()
    assert.equal(view.requests.length, 1)
    assert.equal(view.requests[0].signal.aborted, false)
    view.requests[0].resolve(new Response('<svg/>', { headers: { 'X-Preview-State': 'unavailable', 'Content-Type': 'image/svg+xml' } }))
    await settle()
    assert.equal(view.read('state.value'), 'unavailable')
  } finally { view.close() }
})

test('a ready hint replaces an older pending read with a fresh authorized derivative request', async () => {
  const view = preview()
  try {
    view.props.forceGeneration++
    await settle()
    assert.equal(view.requests.length, 2)
    assert.equal(view.requests[0].signal.aborted, true)
    assert.equal(view.requests[1].captureId, 'capture')
    assert.equal(view.requests[1].profile, 'detail')
    view.requests[1].resolve(new Response('synthetic preview', { headers: { 'X-Preview-State': 'ready', ETag: 'published' } }))
    await settle()
    assert.equal(view.read('state.value'), 'ready')
    assert.match(view.read('url.value'), /^blob:/)
    view.clearAccess()
    assert.equal(view.read('url.value'), '')
  } finally { view.close() }
})

test('a successful 304 restores ready after a failed revalidation without replacing the cached blob', async () => {
  const view = preview()
  try {
    view.requests[0].resolve(new Response('synthetic preview', { headers: { 'X-Preview-State': 'ready', ETag: 'published' } }))
    await settle()
    const cachedUrl = view.read('url.value')
    view.props.generation++
    await settle()
    view.requests[1].reject(new TypeError('Network unavailable'))
    await settle()
    assert.equal(view.read('state.value'), 'unavailable')
    assert.equal(view.read('url.value'), cachedUrl)
    view.props.generation++
    await settle()
    assert.equal(view.requests[2].etag, 'published')
    view.requests[2].resolve(new Response(null, { status: 304 }))
    await settle()
    assert.equal(view.read('state.value'), 'ready')
    assert.equal(view.read('url.value'), cachedUrl)
  } finally { view.close() }
})

test('explicit invalidation drops cached private bytes before revalidation and 404 cannot retain them', async () => {
  const view = preview()
  try {
    view.requests[0].resolve(new Response('synthetic preview', { headers: { 'X-Preview-State': 'ready', ETag: 'published' } }))
    await settle()
    assert.match(view.read('url.value'), /^blob:/)
    view.props.forceGeneration++
    await settle()
    assert.equal(view.read('url.value'), '')
    assert.equal(view.requests[1].etag, undefined)
    view.requests[1].resolve(new Response(null, { status: 404 }))
    await settle()
    assert.equal(view.read('url.value'), '')
    assert.equal(view.read('state.value'), 'unavailable')
  } finally { view.close() }
})

test('dimension replacement keeps the displayed derivative through loading and failure', async () => {
    const view=preview();
    try {
        view.requests[0].resolve(new Response('synthetic preview',{headers:{'X-Preview-State':'ready',ETag:'old'}})); await settle();
        const previous=view.read('url.value'); view.props.maximumDimension=1024; await settle();
        assert.equal(view.read('url.value'),previous); assert.equal(view.requests[1].etag,undefined);
        view.requests[1].reject(new TypeError('offline')); await settle(); assert.equal(view.read('url.value'),previous);
        view.props.maximumDimension=2048; await settle(); view.requests[2].resolve(new Response('replacement',{headers:{'X-Preview-State':'ready'}})); await settle();
        assert.notEqual(view.read('url.value'),previous); assert.equal(view.read('state.value'),'ready');
    } finally {view.close();}
});


test('display readiness requires browser load, cache validation cannot bypass decode, and decode failure clears bytes', async () => {
  const view = preview();
  try {
    view.requests[0].resolve(new Response('synthetic preview', {headers:{'X-Preview-State':'ready',ETag:'published'}}));
    await settle();
    assert.equal(view.events.some(event=>event[1]==='ready'),false);
    view.props.generation++; await settle(); view.requests[1].resolve(new Response(null,{status:304})); await settle();
    assert.equal(view.events.some(event=>event[1]==='ready'),false);
    view.read('displayed()'); assert.equal(view.events.at(-1)[1],'ready');
    view.read('decodeFailed()'); assert.equal(view.events.at(-1)[1],'unavailable'); assert.equal(view.read('url.value'),'');
  } finally { view.close(); }
});
