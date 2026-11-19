import { test } from 'vitest';
import assert from 'node:assert/strict';
import { FullscreenChrome, chromeState } from '../../app/lib/fullscreen-chrome.ts';
function fixture() {
    const state = chromeState(), timers = new Map();
    let id = 0;
    const chrome = new FullscreenChrome(state, {
        schedule: (callback, ms) => { timers.set(++id, { callback, ms }); return id; },
        cancel: timer => timers.delete(timer),
    });
    return { chrome, state, timers, expire() { const [id, timer] = [...timers][0] ?? []; assert.ok(timer); assert.equal(timer.ms, 3000); timers.delete(id); timer.callback(); } };
}
test('fullscreen chrome hides after three idle seconds and pointer movement reveals it immediately', () => {
    const f = fixture();
    f.chrome.start();
    assert.equal(f.state.visible, true);
    f.expire();
    assert.equal(f.state.visible, false);
    f.chrome.pointer();
    assert.equal(f.state.visible, true);
    f.expire();
    assert.equal(f.state.visible, false);
});
test('keyboard-focused controls stay visible while mouse-clicked controls may hide', () => {
    const f = fixture();
    f.chrome.start();
    f.chrome.keyboard();
    f.chrome.focus(true);
    assert.equal(f.timers.size, 0);
    f.chrome.pointer();
    assert.equal(f.state.visible, true);
    assert.equal(f.timers.size, 0);
    f.chrome.pointer(true);
    f.chrome.focus(true);
    f.expire();
    assert.equal(f.state.visible, false);
    f.chrome.focus(false);
    assert.equal(f.state.visible, false);
    assert.equal(f.timers.size, 0);
    f.chrome.keyboard();
    f.chrome.focus(true);
    assert.equal(f.state.visible, true);
    assert.equal(f.timers.size, 0);
    f.chrome.focus(false);
    f.expire();
    assert.equal(f.state.visible, false);
});
test('actionable recovery pins chrome until resolved and then grants a fresh idle interval', () => {
    const f = fixture();
    f.chrome.start();
    f.expire();
    f.chrome.pin(true);
    assert.equal(f.state.visible, true);
    assert.equal(f.timers.size, 0);
    f.chrome.pointer(true);
    assert.equal(f.timers.size, 0);
    f.chrome.pin(false);
    assert.equal(f.state.visible, true);
    f.expire();
    assert.equal(f.state.visible, false);
});
test('closing cancels idle work and an earlier visit cannot hide a reopened viewer', () => {
    const f = fixture();
    f.chrome.start();
    const old = [...f.timers.values()][0].callback;
    f.chrome.stop();
    assert.equal(f.timers.size, 0);
    f.chrome.start();
    old();
    assert.equal(f.state.visible, true);
    f.expire();
    assert.equal(f.state.visible, false);
    f.chrome.stop();
    f.chrome.pointer();
    f.chrome.keyboard();
    f.chrome.pin(true);
    assert.equal(f.timers.size, 0);
});
