import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { ref, reactive, computed, watch, onScopeDispose, createRenderer, h, nextTick } from 'vue';

function mountPreferences(identity, storage) {
    const source = readFileSync(new URL('../app/composables/useWorkspacePreferences.ts', import.meta.url), 'utf8');
    const preferencesModule = {};
    vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/workspace-preferences.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText,
        vm.createContext({ exports: preferencesModule }));
    const context = vm.createContext({ exports: {}, require: name => name === 'vue' ? { computed, onScopeDispose, reactive, ref, watch } : preferencesModule, ref, reactive, computed, watch, onScopeDispose, setTimeout, clearTimeout });
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    let result;
    const host = {
        createElement: type => ({ type, children: [] }), createText: text => ({ text }), createComment: text => ({ comment: text }),
        setText(node, text) { node.text = text; }, setElementText(node, text) { node.text = text; }, patchProp() {},
        insert(node, parent) { parent.children.push(node); }, remove() {}, parentNode: () => null, nextSibling: () => null,
    };
    const app = createRenderer(host).createApp({
        setup() {
            result = vm.runInContext('useWorkspacePreferences(identity, storage)', context);
            return () => h('div');
        },
    });
    context.identity = identity;
    context.storage = storage;
    app.mount({ children: [] });
    return { app, result: () => result };
}

function memoryStorage(initial = {}) {
    const values = new Map(Object.entries(initial));
    const writes = [];
    return { values, writes, getItem(key) { return values.get(key) ?? null; }, setItem(key, value) { writes.push([key, value]); values.set(key, value); } };
}

test('mounted preferences hydrate without writing, batch edits, isolate account keys, and flush on disposal', async () => {
    const storage = memoryStorage({
        'shutterspool-appearance-issuer-one': JSON.stringify({ version: 2, workspace: { width: 'full' } }),
        'shutterspool-appearance-issuer-two': JSON.stringify({ version: 2, appearances: { Gallery: { density: 310 } } }),
    });
    const identity = ref({ profile: { iss: 'issuer', sub: 'one' } });
    const mounted = mountPreferences(identity, storage);
    const prefs = mounted.result();
    assert.equal(prefs.preferences.workspace.width, 'full');
    assert.equal(storage.writes.length, 0, 'hydration must never write defaults back over persisted settings');
    prefs.preferences.appearances.Gallery.density = 280;
    prefs.preferences.appearances.Gallery.details = false;
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(storage.writes.length, 1, 'related edits are persisted as one debounced write');
    assert.equal(JSON.parse(storage.writes[0][1]).appearances.Gallery.details, false);

    prefs.preferences.appearances.Gallery.density = 420;
    identity.value = { profile: { iss: 'issuer', sub: 'two' } };
    await nextTick();
    assert.equal(prefs.preferenceKey.value, 'shutterspool-appearance-issuer-two');
    assert.equal(prefs.preferences.appearances.Gallery.density, 310, 'the next identity receives its own saved preferences');
    assert.equal(JSON.parse(storage.values.get('shutterspool-appearance-issuer-one')).appearances.Gallery.density, 420, 'an identity switch flushes pending edits to the old account key');
    prefs.preferences.appearances.Gallery.density = 400;
    mounted.app.unmount();
    assert.equal(JSON.parse(storage.values.get('shutterspool-appearance-issuer-two')).appearances.Gallery.density, 400, 'unmount flushes the last pending change');
});

test('mounted preferences report storage failures and keep edits usable for the visit', async () => {
    const storage = { getItem() { throw new Error('storage denied'); }, setItem() { throw new Error('storage denied'); } };
    const mounted = mountPreferences(ref({ profile: { iss: 'issuer', sub: 'private-user' } }), storage);
    const prefs = mounted.result();
    assert.match(prefs.preferenceNotice.value, /couldn't be read/i);
    prefs.preferences.workspace.width = 'full';
    await nextTick();
    prefs.flushPreferenceSave();
    assert.match(prefs.preferenceNotice.value, /couldn't save/i);
    assert.equal(prefs.preferences.workspace.width, 'full');
    mounted.app.unmount();
});

test('mounted preferences never overwrite a future stored format', () => {
    const key = 'shutterspool-appearance-issuer-private-user';
    const raw = JSON.stringify({ version: 99, workspace: { width: 'full' }, unknown: { retain: true } });
    const storage = memoryStorage({ [key]: raw });
    const mounted = mountPreferences(ref({ profile: { iss: 'issuer', sub: 'private-user' } }), storage);
    const prefs = mounted.result();
    assert.equal(prefs.writable.value, false);
    assert.match(prefs.preferenceNotice.value, /unsupported format/i);
    prefs.preferences.workspace.width = 'full';
    prefs.flushPreferenceSave();
    assert.deepEqual(storage.writes, []);
    assert.equal(storage.values.get(key), raw);
    mounted.app.unmount();
});
