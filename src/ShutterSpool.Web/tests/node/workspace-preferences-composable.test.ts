import assert from 'node:assert/strict';
import { afterEach, test, vi } from 'vitest';
import { effectScope, nextTick, ref } from 'vue';
import { useWorkspacePreferences } from '../../app/composables/useWorkspacePreferences';

const scopes: ReturnType<typeof effectScope>[] = [];

afterEach(() => {
    for (const scope of scopes.splice(0)) scope.stop();
    vi.useRealTimers();
});

function startPreferences(...args: Parameters<typeof useWorkspacePreferences>) {
    const scope = effectScope();
    scopes.push(scope);
    return { prefs: scope.run(() => useWorkspacePreferences(...args))!, stop: () => scope.stop() };
}

function memoryStorage(initial: Record<string, string> = {}) {
    const values = new Map(Object.entries(initial));
    const setItem = vi.fn((key: string, value: string) => values.set(key, value));
    return { values, setItem, getItem: (key: string) => values.get(key) ?? null };
}

test('mounted preferences hydrate without writing, batch edits, isolate account keys, and flush on disposal', async () => {
    vi.useFakeTimers();
    const first = 'shutterspool-appearance-issuer-one';
    const second = 'shutterspool-appearance-issuer-two';
    const storage = memoryStorage({
        [first]: JSON.stringify({ version: 2, workspace: { width: 'full' } }),
        [second]: JSON.stringify({ version: 2, appearances: { Gallery: { density: 310 } } }),
    });
    const identity = ref({ profile: { iss: 'issuer', sub: 'one' } });
    const { prefs, stop } = startPreferences(identity, storage);
    assert.equal(prefs.preferences.workspace.width, 'full');
    assert.equal(storage.setItem.mock.calls.length, 0, 'hydration must never overwrite stored preferences');
    prefs.preferences.appearances.Gallery.density = 280;
    prefs.preferences.appearances.Gallery.details = false;
    await nextTick();
    await vi.advanceTimersByTimeAsync(150);
    assert.equal(storage.setItem.mock.calls.length, 1, 'related edits are persisted as one debounced write');
    assert.equal(JSON.parse(storage.values.get(first)!).appearances.Gallery.details, false);

    prefs.preferences.appearances.Gallery.density = 420;
    identity.value = { profile: { iss: 'issuer', sub: 'two' } };
    await nextTick();
    assert.equal(prefs.preferenceKey.value, second);
    assert.equal(prefs.preferences.appearances.Gallery.density, 310);
    assert.equal(JSON.parse(storage.values.get(first)!).appearances.Gallery.density, 420, 'account switch flushes to the old account');

    prefs.preferences.appearances.Gallery.density = 400;
    stop();
    assert.equal(JSON.parse(storage.values.get(second)!).appearances.Gallery.density, 400, 'scope disposal flushes the last change');
});

test('mounted preferences report storage failures and keep edits usable for the visit', async () => {
    const storage = { getItem() { throw new Error('storage denied'); }, setItem() { throw new Error('storage denied'); } };
    const { prefs } = startPreferences(ref({ profile: { iss: 'issuer', sub: 'private-user' } }), storage);
    assert.match(prefs.preferenceNotice.value, /couldn't be read/i);
    prefs.preferences.workspace.width = 'full';
    await nextTick();
    prefs.flushPreferenceSave();
    assert.match(prefs.preferenceNotice.value, /couldn't save/i);
    assert.equal(prefs.preferences.workspace.width, 'full');
});

test('mounted preferences never overwrite a future stored format', () => {
    const key = 'shutterspool-appearance-issuer-private-user';
    const raw = JSON.stringify({ version: 99, workspace: { width: 'full' }, unknown: { retain: true } });
    const storage = memoryStorage({ [key]: raw });
    const { prefs } = startPreferences(ref({ profile: { iss: 'issuer', sub: 'private-user' } }), storage);
    assert.equal(prefs.writable.value, false);
    assert.match(prefs.preferenceNotice.value, /unsupported format/i);
    prefs.preferences.workspace.width = 'full';
    prefs.flushPreferenceSave();
    assert.equal(storage.setItem.mock.calls.length, 0);
    assert.equal(storage.values.get(key), raw);
});
