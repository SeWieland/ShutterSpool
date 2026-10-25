import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
const context = vm.createContext({ exports: {}, AbortController, setTimeout, clearTimeout });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/gallery-slideshow.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
const { GallerySlideshow, slideshowState } = context.exports;
function fixture(pages) {
    const state = slideshowState();
    const displayed = [];
    const reads = [];
    const timers = new Map();
    let next = 0;
    const player = new GallerySlideshow(state, {
        yield: async () => {},
        read: async (cursor, startAt, signal) => {
            reads.push({cursor, startAt, signal});
            const page = pages[cursor ?? 'first'];
            if (page instanceof Error) throw page;
            return page;
        },
        show: id => displayed.push(id),
        schedule: (fn, ms) => {
            timers.set(++next, {fn, ms});
            return next;
        },
        cancel: id => timers.delete(id),
    });
    return { state, player, displayed, reads, timers, async tick(ms) { const timer = [...timers].find(([, t]) => t.ms === ms); assert.ok(timer, `expected ${ms}ms timer`); timers.delete(timer[0]); await timer[1].fn(); } };
}
const frame = id => ({ id });
test('explicit playback starts at the viewed capture and times decoded images only', async () => {
    const f = fixture({ first: { items: [frame('b'), frame('c')] } });
    assert.equal(f.state.playing, false);
    await f.player.play('b');
    assert.deepEqual(f.displayed, ['b']);
    assert.equal(f.reads[0].startAt, 'b');
    assert.equal([...f.timers.values()].some(t => t.ms === 5000), false);
    f.player.preview('ready');
    await f.tick(5000);
    assert.deepEqual(f.displayed, ['b', 'c']);
    f.player.pause();
    assert.equal(f.timers.size, 0);
    await f.player.play('c');
    f.player.preview('ready');
    assert.ok([...f.timers.values()].some(t => t.ms === 5000));
});
test('three pages including empty continuations cross boundaries and refresh on wrap', async () => {
    const f = fixture({ first: { items: [frame('a')], nextCursor: 'empty' }, empty: { items: [], nextCursor: 'last' }, last: { items: [frame('b')] } });
    await f.player.play('a');
    f.player.preview('ready');
    await f.tick(5000);
    assert.deepEqual(f.displayed, ['a', 'b']);
    f.player.preview('ready');
    await f.tick(5000);
    assert.deepEqual(f.displayed, ['a', 'b', 'a']);
    assert.deepEqual(f.reads.map(x => x.cursor), [undefined, 'empty', 'last', undefined]);
    f.player.stop();
    assert.equal(f.timers.size, 0);
});
test('preview timeout and decode failure pause and never resume themselves', async () => {
    const f = fixture({ first: { items: [frame('a'), frame('b')] } });
    await f.player.play('a');
    await f.tick(15000);
    assert.equal(f.state.playing, false);
    assert.match(f.state.message, /15 seconds/);
    f.player.preview('ready');
    assert.equal(f.timers.size, 0);
    await f.player.play('a');
    f.player.preview('unavailable');
    assert.equal(f.state.playing, false);
    assert.match(f.state.message, /preview/i);
});
test('interval changes grant a full new interval and singleton and empty sequences stop', async () => {
    const f = fixture({ first: { items: [frame('a')] } });
    await f.player.play('a');
    f.player.preview('ready');
    f.player.interval(3);
    assert.equal([...f.timers.values()].some(t => t.ms === 5000), false);
    await f.tick(3000);
    assert.equal(f.state.playing, false);
    assert.deepEqual(f.displayed, ['a']);
    const empty = fixture({ first: { items: [] } });
    await empty.player.play('x');
    assert.equal(empty.state.playing, false);
    assert.match(empty.state.message, /No photographs/);
});
test('page failure preserves position and a paused stale response cannot display a frame', async () => {
    const f = fixture({ first: { items: [frame('a')], nextCursor: 'bad' }, bad: new Error('offline') });
    await f.player.play('a');
    f.player.preview('ready');
    await f.tick(5000);
    assert.equal(f.state.playing, false);
    assert.match(f.state.message, /sequence/);
    let resolve;
    f.player.stop();
    f.player.dependencies.read = () => new Promise(r => resolve = r);
    const pending = f.player.play('a');
    f.player.pause();
    resolve({ items: [frame('late')] });
    await pending;
    assert.deepEqual(f.displayed, ['a']);
});
test('video-only pages yield before later photographs and all-video results terminate', async () => {
    const f = fixture({ first: { items: [{ id: 'video', mediaKind: 'Video' }], nextCursor: 'photos' }, photos: { items: [frame('photo')] } });
    let yields = 0;
    f.player.dependencies.yield = async () => { yields++; };
    await f.player.play('video');
    assert.deepEqual(f.displayed, ['photo']);
    assert.equal(f.state.photographsOnly, true);
    assert.equal(yields, 1);
    f.player.stop();
    const all = fixture({ first: { items: [{ id: 'clip', mediaKind: 'Video' }], nextCursor: 'last' }, last: { items: [{ id: 'clip2', mediaKind: 'Video' }] } });
    await all.player.play('clip');
    assert.equal(all.state.playing, false);
    assert.match(all.state.message, /No photographs/);
    assert.ok(all.reads.length <= 4);
    assert.equal(all.timers.size, 0);
});
test('repeated pending events and interval changes preserve the current preview deadline', async () => {
    const f = fixture({ first: { items: [frame('a'), frame('b')] } });
    await f.player.play('a');
    const timer = [...f.timers.keys()][0];
    f.player.preview('pending');
    f.player.interval(3);
    f.player.preview('pending');
    assert.equal([...f.timers.keys()][0], timer);
    await f.tick(15000);
    assert.equal(f.state.playing, false);
    f.player.stop();
});
test('pausing an outstanding page read cancels it and resume does not skip its first photograph', async () => {
    const f = fixture({ first: { items: [frame('a')], nextCursor: 'next' }, next: { items: [frame('b'), frame('c')] } });
    await f.player.play('a');
    let finish;
    const read = f.player.dependencies.read;
    f.player.dependencies.read = (...args) => args[0] === 'next' ? new Promise(r => finish = r) : read(...args);
    f.player.preview('ready');
    const advance = f.tick(5000);
    await Promise.resolve();
    f.player.pause();
    assert.equal(f.player.state.playing, false);
    assert.equal(f.player.state.loading, false);
    finish({ items: [frame('b'), frame('c')] });
    await advance;
    assert.deepEqual(f.displayed, ['a']);
    f.player.dependencies.read = read;
    await f.player.play('a');
    await f.tick(5000);
    assert.deepEqual(f.displayed, ['a', 'b']);
    f.player.stop();
});
test('known removals invalidate buffered frames and removal of the current photograph advances immediately', async () => {
    const f = fixture({ first: { items: [frame('a'), frame('b'), frame('c')] } });
    await f.player.play('a');
    f.player.remove('b');
    f.player.preview('ready');
    await f.tick(5000);
    assert.deepEqual(f.displayed, ['a', 'c']);
    await f.player.remove('c');
    assert.equal(f.displayed.at(-1), 'a');
    f.player.stop();
});
test('manual navigation uses bounded authorized pages beyond grid state and pauses first', async () => {
    const f = fixture({ first: { items: [frame('a')], nextCursor: 'next' }, next: { items: [frame('b')] } });
    await f.player.play('a');
    await f.player.navigate('a', 1);
    assert.equal(f.state.playing, false);
    assert.deepEqual(f.displayed, ['a', 'b']);
    assert.equal(f.timers.size, 0);
    assert.equal(f.reads.at(-2).startAt, 'a');
    f.player.stop();
});
test('multiple large loops retain one metadata page and no visited-id history', async () => {
    const pages = { first: { items: Array.from({ length: 30 }, (_, n) => frame(`a${n}`)), nextCursor: 'second' }, second: { items: Array.from({ length: 30 }, (_, n) => frame(`b${n}`)), nextCursor: 'third' }, third: { items: Array.from({ length: 30 }, (_, n) => frame(`c${n}`)) } };
    const f = fixture(pages);
    await f.player.play('a0');
    for (let n = 0; n < 270; n++) {
        f.player.preview('ready');
        await f.tick(5000);
        assert.ok(f.player.page.length <= 30);
        assert.ok(f.timers.size <= 1);
    }
    assert.equal(f.displayed.length, 271);
    assert.equal(f.displayed[90], 'a0');
    assert.equal(f.displayed[180], 'a0');
    assert.equal(f.reads.length, 10);
    f.player.stop();
    assert.equal(f.player.page.length, 0);
});

test('a known removal cancels an older page read and rereads its boundary without restoring removed frames', async () => {
    const f = fixture({first:{items:[frame('a')],nextCursor:'next'}});
    await f.player.play('a');
    const replies = [];
    f.player.dependencies.read = (cursor, _startAt, signal) => new Promise(resolve => replies.push({cursor,signal,resolve}));
    f.player.preview('ready');
    const oldRead = f.tick(5000);
    await Promise.resolve();
    const replacementRead = f.player.remove('b');
    try {
        assert.equal(replies[0].signal.aborted, true);
        assert.equal(replies.length, 2);
        assert.equal(replies[1].cursor, 'next');
        replies[0].resolve({items:[frame('b'),frame('c')]});
        replies[1].resolve({items:[frame('c')]});
        await Promise.all([oldRead,replacementRead]);
        assert.deepEqual(f.displayed,['a','c']);
    } finally { f.player.stop(); }
});

test('a display callback that pauses playback cannot leave a timeout armed', async () => {
    const f = fixture({first:{items:[frame('a')]}});
    f.player.dependencies.show = () => f.player.preview('unavailable');
    await f.player.play('a');
    assert.equal(f.state.playing, false);
    assert.equal(f.timers.size, 0);
});

test('ready events and interval changes cannot arm advancement during an initial metadata read', async () => {
    const f = fixture({});
    const replies = [];
    f.player.dependencies.read = (_cursor, _startAt, signal) => new Promise(resolve => replies.push({signal,resolve}));
    const playing = f.player.play('a');
    f.player.preview('ready');
    f.player.interval(3);
    try {
        assert.equal(f.timers.size, 0);
        await f.player.advance();
        assert.equal(replies.length, 1);
        assert.equal(replies[0].signal.aborted, false);
        replies[0].resolve({items:[frame('a'),frame('b')]});
        await playing;
    } finally { f.player.stop(); }
});

test('ready events cannot start overlapping metadata requests at a page boundary', async () => {
    const f = fixture({first:{items:[frame('a')],nextCursor:'next'}});
    await f.player.play('a');
    const replies = [];
    f.player.dependencies.read = (_cursor, _startAt, signal) => new Promise(resolve => replies.push({signal,resolve}));
    f.player.preview('ready');
    const boundary = f.tick(5000);
    await Promise.resolve();
    f.player.preview('ready');
    f.player.interval(10);
    try {
        assert.equal(f.timers.size, 0);
        await f.player.advance();
        assert.equal(replies.length, 1);
        assert.equal(replies[0].signal.aborted, false);
        replies[0].resolve({items:[frame('b')]});
        await boundary;
        assert.deepEqual(f.displayed,['a','b']);
    } finally { f.player.stop(); }
});
