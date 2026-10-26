import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { parse, compileScript } from '@vue/compiler-sfc';
import * as Vue from 'vue';
import { ref, computed, watch, reactive, nextTick } from 'vue';
const context = vm.createContext({ exports: {}, Date });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/retention.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
const presentation = vm.createContext({ exports: {}, require: () => context.exports });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/storage-presentation.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, presentation);
const interactions = vm.createContext({ exports: {} });
vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/workspace-interactions.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, interactions);
const { retentionTiming } = context.exports;
test('removal confirmation lists every remaining variant and rejects unavailable scope', () => {
    const { originalRemovalScope } = context.exports;
    assert.equal(originalRemovalScope(undefined), undefined);
    assert.equal(originalRemovalScope({ isFavorite: false }), undefined);
    const evidence = { isFavorite: true, availableOriginals: [{ variant: 'RAW', byteLength: 2048, state: 'available' }, { variant: 'JPEG', byteLength: 1024, state: 'failedRemoval' }, { variant: 'OTHER', byteLength: 4096, state: 'removed' }] };
    assert.match(originalRemovalScope(evidence), /RAW \(2,048 bytes/); assert.match(originalRemovalScope(evidence), /JPEG \(1,024 bytes/); assert.doesNotMatch(originalRemovalScope(evidence), /OTHER/);
    evidence.availableOriginals[0].state = 'unknown'; assert.equal(originalRemovalScope(evidence), undefined);
});
const now = Date.parse('2026-10-13T12:00:00Z');
function summary(kind, duration, remaining) {
    return { asOf: new Date(now), timeline: { kind: kind, startedAt: new Date(now + remaining - duration), endsAt: new Date(now + remaining), meaning: 'Whole Capture eligibility' } };
}
test('TimeSpan settings preserve days and fractional seconds without accepting malformed values', () => {
    const { durationHours, durationFromHours } = context.exports;
    assert.equal(durationHours('180.00:00:00'), 4320);
    assert.equal(durationHours('01:30:00'), 1.5);
    assert.equal(durationFromHours(72), '3.00:00:00');
    assert.equal(durationFromHours(1.5), '0.01:30:00');
    assert.equal(durationFromHours(.5 / 3600), '0.00:00:00.5');
    assert.equal(durationHours('00:00:00.5000000'), .5 / 3600);
    assert.equal(durationHours('invalid'), undefined);
    assert.equal(durationHours('-1.00:00:00'), undefined);
    assert.equal(durationHours('25:00:00'), undefined);
    assert.throws(() => durationFromHours(Infinity));
});
test('180-day intervals retain their scale and normal warning is capped at one day', () => {
    const long = summary('normalRetention', 180 * 86400000, 2 * 86400000);
    assert.equal(retentionTiming(long, now, false).visible, false);
    assert.equal(retentionTiming(long, now, true).fraction, 2 / 180);
    assert.equal(retentionTiming(summary('normalRetention', 180 * 86400000, 86400000), now, false).tone, 'amber');
});
test('held and Trash retain exact 24-hour warning boundaries', () => {
    assert.equal(retentionTiming(summary('heldOrBlocked', 48 * 3600000, 24 * 3600000), now, false).tone, 'amber');
    assert.equal(retentionTiming(summary('trashRecovery', 72 * 3600000, 24 * 3600000), now, false).tone, 'red');
    assert.equal(retentionTiming(summary('trashRecovery', 72 * 3600000, 25 * 3600000), now, false).visible, true);
});
test('deadline-only authority never creates progress; zero means pending', () => {
    const deadline = summary('normalRetention', 10000, 1000); deadline.timeline.startedAt = null;
    assert.equal(retentionTiming(deadline, now, true).fraction, undefined);
    const expired = retentionTiming(summary('trashRecovery', 10000, 0), now, true);
    assert.match(expired.title, /pending/); assert.equal(expired.fraction, 0);
});
test('stale evidence freezes at observation; unknown and unscheduled state get no clocks', () => {
    const result = retentionTiming(summary('trashRecovery', 1000000, 500000), now + 120000, true);
    assert.equal(result.stale, true); assert.equal(result.fraction, .5); assert.match(result.title, /Last known/);
    assert.equal(retentionTiming(undefined, now, true).visible, false);
    assert.equal(retentionTiming({ asOf: new Date(now), timeline: {} }, now, true).fraction, undefined);
    const unknown = summary('trashRecovery', 10000, 5000); delete unknown.asOf;
    assert.equal(retentionTiming(unknown, now, true).fraction, undefined);
    unknown.asOf = new Date('invalid');
    assert.equal(retentionTiming(unknown, now, true).fraction, undefined);
});

function component(name, props, management, confirm = () => true) {
    let generation = 0, accessLost; const unmount = [], events = [];
    const scope = vm.createContext({ exports: {}, ref, computed, reactive, nextTick, AbortController, AbortSignal, useId: () => 'synthetic-dialog', watch: (target, callback) => watch(target, callback, { flush: 'sync' }),
        defineProps: () => props, defineEmits: () => (...event) => events.push(event), onMounted() {}, onUnmounted(callback) { unmount.push(callback); },
        window: { confirm, scrollY: 0, scrollTo() {} }, document: { activeElement: undefined }, clearInterval, setInterval,
        crypto: { randomUUID: () => '00000000-0000-0000-0000-000000000001' },
        require: source => source.endsWith('/storage-presentation') ? presentation.exports : source.endsWith('/workspace-interactions') ? interactions.exports : source.endsWith('/retention') ? context.exports : source.endsWith('/session') ? { accessGeneration: () => generation, onAccessLost(callback) { accessLost = callback; return () => { if (accessLost === callback) accessLost = undefined; }; } } : { api: { management }, cancellable: signal => ({ signal }), handleFailure: failure => failure.status },
    });
    const script = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(script, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, scope);
    return { run: expression => vm.runInContext(expression, scope), events, lose: () => { generation++; accessLost?.(); }, dispose: () => unmount.forEach(callback => callback()), render() {
        const descriptor = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor;
        const compiled = compileScript(descriptor, { id: 'retention-control', inlineTemplate: true });
        const renderScope = vm.createContext({ ...scope, exports: {}, require: source => source === 'vue' ? Vue : scope.require(source) });
        vm.runInContext(ts.transpileModule(compiled.content, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, renderScope);
        const render = renderScope.exports.default.setup(props, { expose() {}, emit: (...event) => events.push(event) });
        return () => render({}, []);
    } };
}

test('icon-only Favorite stays a named native button and handles keyboard activation conflicts without opening its photo', async () => {
    let calls = 0, stopped = 0;
    const props = reactive({ captureId:'synthetic', compact:true, evidence:{captureRevision:7,isFavorite:true,asOf:new Date()} });
    const fixture = component('FavoriteControl', props, { captures:{byId:()=>({favorite:{put:async body=>{calls++; assert.equal(body.expectedRevision,7); assert.equal(body.isFavorite,false); throw {status:409};}}})} });
    const render = fixture.render();
    const button = render().children[0];
    assert.equal(button.type,'button'); assert.equal(button.props['aria-label'],'Remove Favorite'); assert.equal(button.props['aria-pressed'],true);
    assert.match(button.props['aria-description'], /rules apply immediately.*Trash recovery window/);
    assert.equal(button.children[0].type,'svg'); assert.equal(button.children[0].props.fill,'currentColor');
    await button.props.onClick({detail:0,stopPropagation(){stopped++;}});
    assert.equal(calls,1); assert.equal(stopped,1); assert.equal(render().children[0].props.disabled,true);
    const feedback = render().children[1]; assert.match(feedback.children[0].children,/changed elsewhere/);
    await render().children[0].props.onClick({detail:1,stopPropagation(){stopped++;}}); assert.equal(calls,1,'conflict never automatically replays');
});
test('lost heart response keeps confirmed intent and prevents replay until a fresh authoritative read', async () => {
    let calls = 0;
    const props = { captureId: 'synthetic', evidence: { captureRevision: 7, isFavorite: false, asOf: new Date(now) } };
    const fixture = component('FavoriteControl', props, { captures: { byId: () => ({ favorite: { put: async body => { calls++; assert.equal(body.expectedRevision, 7); throw new Error('lost response'); } } }) } });
    await fixture.run('toggle()'); await fixture.run('toggle()');
    assert.equal(calls, 1); assert.equal(props.evidence.isFavorite, false); assert.equal(fixture.run('uncertain.value'), true);
    assert.match(fixture.run('feedback.value'), /could not confirm/); assert.equal(fixture.events[0][0], 'refresh');
});
test('unfavorite is one click with the current revision and never asks for confirmation', async () => {
    let calls = 0, confirmations = 0;
    const fixture = component('FavoriteControl', { captureId: 'synthetic', evidence: { captureRevision: 7, isFavorite: true } }, { captures: { byId: () => ({ favorite: { put: async body => { calls++; assert.equal(body.expectedRevision, 7); assert.equal(body.isFavorite, false); } } }) } }, () => { confirmations++; return false; });
    await fixture.run('toggle()'); assert.equal(calls, 1); assert.equal(confirmations, 0);
});
test('a late heart completion cannot announce or reconcile a different photograph', async () => {
    let finish;
    const props = { captureId: 'old', evidence: { captureRevision: 2, isFavorite: false } };
    const fixture = component('FavoriteControl', props, { captures: { byId: () => ({ favorite: { put: () => new Promise(resolve => finish = resolve) } }) } });
    const pending = fixture.run('toggle()'); props.captureId = 'new'; finish({}); await pending;
    assert.equal(fixture.events.length, 0); assert.notEqual(fixture.run('feedback.value'), 'Favorite saved.');
});
test('Trash child validates evidence and emits a confirmed submission for shell-owned mutation', () => {
    let confirmations = 0;
    const evidence = { captureRevision: 2, asOf: new Date(), isFavorite: true, availableOriginals: [{ variant: 'JPEG', byteLength: 5, state: 'available' }] };
    const props = reactive({ captureId: 'shot', name: 'Shot', evidence });
    const fixture = component('CaptureTrashAction', props, {}, message => { confirmations++; assert.match(message, /JPEG \(5 bytes/); assert.match(message, /Favorite: yes/); assert.match(message, /only copies/); return true; });
    fixture.run('trash()');
    assert.equal(confirmations, 1);
    assert.deepEqual(fixture.events, [['submit', 'shot', 2]], 'the child hands the confirmed id and revision to its owner');

    props.evidence = { ...evidence, availableOriginals: [{ variant: 'JPEG', byteLength: 5, state: 'unknown' }] };
    fixture.run('trash()');
    assert.deepEqual(fixture.events[1], ['refresh', 'shot', false], 'missing removal authority requests fresh evidence without submitting');
    assert.match(fixture.run('feedback.value'), /could not check the originals/);
    assert.equal(confirmations, 1);

    props.evidence = evidence;
    const rejectConfirmation = component('CaptureTrashAction', props, {}, () => false);
    rejectConfirmation.run('trash()');
    assert.equal(rejectConfirmation.events.length, 0, 'canceling confirmation emits no mutation request');
});
test('Capture evidence reads only while active, forwards cancellation, and aborts when hidden', async () => {
    let lost, unmount; const requests = [];
    const props = reactive({ capture: { id: 'shot', assets: [], curation: {} }, generation: 0, active: false, focusState: 'Attention' });
    let focusCount = 0, focusableVisible = false;
    const management = { captures: { byId: () => ({
        routing: { get: options => new Promise(resolve => requests.push({ kind: 'routing', options, resolve })) },
        deliveries: { get: options => new Promise(resolve => requests.push({ kind: 'deliveries', options, resolve })) },
        actions: { get: options => new Promise(resolve => requests.push({ kind: 'actions', options, resolve })) },
    }) } };
    const scope = vm.createContext({ exports: {}, AbortController, ref, computed, watch: (target, callback) => watch(target, callback, { flush: 'sync' }), nextTick, defineProps: () => props, defineEmits: () => () => {}, onUnmounted: callback => { unmount = callback; },
        require: name => name.endsWith('/api') ? { api: { management }, cancellable: signal => ({ signal }), handleFailure() {} } : name.endsWith('/session') ? { accessGeneration: () => 0, onAccessLost: callback => { lost = callback; return () => { if (lost === callback) lost = undefined; }; } } : { deliveryPresentation: () => ({ tone: 'neutral', icon: '', label: '' }), formatFileSize: String },
    });
    const script = parse(readFileSync(new URL('../app/components/CaptureEvidence.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(script, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, scope);
    const run = expression => vm.runInContext(expression, scope);
    scope.focusable = { dataset: { workState: 'Failed' }, closest: () => null, focus: () => focusCount++, scrollIntoView() {} };
    scope.focusableVisible = () => focusableVisible;
    run("evidence.value = { querySelectorAll: () => focusableVisible() ? [focusable] : [] }");
    await nextTick(); assert.equal(requests.length, 0, 'collapsed evidence must not load in the background');
    props.active = true; await nextTick();
    assert.equal(requests.length, 3);
    assert.ok(requests.every(request => request.options.options[0].signal instanceof AbortSignal));
    requests.forEach(request => request.resolve({ items: [], nextCursor: null }));
    await new Promise(resolve => setImmediate(resolve));
    await nextTick(); assert.equal(focusCount, 0, 'a focus request is consumed when its first eligible read has no matching work');
    assert.equal(run('deliveryReadSequence.value'), 1, 'only a successful current deliveries read advances recovery reconciliation');
    focusableVisible = true;
    run("deliveries.value = { items: [{ state: 'Failed' }] }");
    await nextTick(); assert.equal(focusCount, 0, 'later evidence arrival cannot steal focus after the request was consumed');
    props.generation++;
    assert.equal(requests.length, 6, 'visible evidence refreshes when live generation changes');
    props.focusState = 'RetryWait';
    run("deliveries.value = { items: [] }");
    props.active = false;
    await nextTick(); assert.equal(focusCount, 0, 'deactivation while a focus continuation is queued cannot move focus');
    assert.ok(requests.slice(3).every(request => request.options.options[0].signal.aborted), 'hiding the disclosure cancels pending reads');
    const savedDecisions = run('decisions.value');
    requests.slice(3).forEach(request => request.resolve({ items: [], nextCursor: null })); await nextTick();
    assert.equal(run('deliveryReadSequence.value'), 1, 'late hidden results cannot clear uncertain recovery state');
    assert.equal(run('decisions.value'), savedDecisions, 'late hidden results cannot replace already loaded evidence');
    unmount?.(); assert.equal(lost, undefined, 'unmount releases the access-loss callback');
});
test('source activity observation helpers pass their AbortSignal to Kiota requests', async () => {
    const calls = [];
    const adapter = { management: { sources: { authenticationObservations: { get: async options => { calls.push(options); return { items: [], coverageComplete: true }; } }, }, activity: { transfers: { get: async options => { calls.push(options); return { epoch: 'epoch', items: [], coverageComplete: true }; } } } } };
    const context = vm.createContext({ exports: {}, AbortController, AbortSignal, location: { origin: 'https://synthetic.example' },
        require: name => name.includes('kiota-bundle') ? { DefaultRequestAdapter: class { constructor() {} } } : name.includes('kiota-http-fetchlibrary') ? { HttpClient: class {} } : name.endsWith('/shutterSpoolClient') ? { createShutterSpoolClient: () => adapter } : name.endsWith('/session') ? { clearAccess() {}, token: async () => '', onAccessLost: () => () => {} } : { withPreviewSlot: action => action() },
    });
    vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/api.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    const signal = new AbortController().signal;
    await context.exports.readSourceAuthenticationObservations(['source'], signal);
    await context.exports.readSourceActivity(['source'], signal);
    assert.equal(calls.length, 2);
    assert.ok(calls.every(options => options.options[0].signal === signal));
});
test('Trash uncertainty and shared pending state block submit while verification is an explicit event', () => {
    const evidence = { captureRevision: 2, asOf: new Date(), isFavorite: false, availableOriginals: [{ variant: 'JPEG', byteLength: 5, state: 'available' }] };
    const props = reactive({ captureId: 'shot', name: 'Shot', evidence, uncertain: true });
    const fixture = component('CaptureTrashAction', props, {}, () => true);
    fixture.run('trash()');
    assert.equal(fixture.events.length, 0, 'uncertain outcome never submits another destructive command');
    fixture.run('verify()');
    assert.deepEqual(fixture.events, [['verify', 'shot']], 'verification is delegated to the persistent owner');

    props.pending = true;
    fixture.run('verify()'); fixture.run('trash()');
    assert.equal(fixture.events.length, 1, 'pending state disables both verification and submission');
    props.pending = false; props.verifying = true;
    fixture.run('verify()');
    assert.equal(fixture.events.length, 1, 'verification cannot be duplicated while already running');
    props.verifying = false; props.disabled = true;
    fixture.run('verify()'); fixture.run('trash()');
    assert.equal(fixture.events.length, 1, 'access or parent locks disable all child requests');
});

test('resume recovery reads forward cancellation and cannot update an action after owner replacement', async () => {
    let read, writes = 0;
    const props = reactive({ captureId: 'old', name: 'Old', evidence: {
        captureRevision: 2, asOf: new Date(), isFavorite: false,
        availableOriginals: [{ variant: 'JPEG', byteLength: 5, state: 'available' }],
        isPaused: true, disposition: 'active',
    } });
    const endpoint = {
        get: options => new Promise(resolve => { read = { options, resolve }; }),
        resume: { post: async () => { writes++; throw new Error('response lost'); } },
    };
    const fixture = component('CaptureResumeAction', props, { captures: { byId: () => endpoint } });
    await fixture.run('resume()');
    assert.equal(writes, 1);
    assert.equal(fixture.run('needsReconcile.value'), true);
    const pending = fixture.run('checkCurrent()');
    const signal = read.options.options[0].signal;
    assert.ok(signal instanceof AbortSignal);
    props.captureId = 'new';
    assert.equal(signal.aborted, true, 'the action aborts its superseded authoritative read');
    read.resolve({ retention: { captureRevision: 99 } });
    await pending;
    assert.equal(fixture.run('verifiedRevision'), undefined, 'the action ignores the late read result');
    assert.equal(fixture.events.length, 1, 'the replaced owner receives no refresh event');
    fixture.dispose();
});
test('Empty loaded Trash confirms only its frozen loaded scope and reports per-item conflicts', async () => {
    const commands = [], prompts = [];
    const fixture = component('TrashWorkspace', { online: true, generation: 0 }, { retention: { settings: { get: async () => ({ settings: { restoreProtection: '3.00:00:00' } }) } }, trash: {
        get: async () => ({ items: [], nextCursor: 'older' }),
        byId: id => ({ purge: { post: async body => { commands.push([id, body.expectedRevision]); if (id === 'protected') throw { status: 409 }; } } }),
    } }, message => { prompts.push(message); return true; });
    fixture.run("items.value = [{captureId:'allowed',captureRevision:2,displayName:'Allowed',retention:{asOf:new Date(),availableOriginals:[{variant:'RAW',byteLength:2048,state:'available'}],isFavorite:true,disposition:'trash'}},{captureId:'protected',captureRevision:3,displayName:'Protected',retention:{asOf:new Date(),availableOriginals:[{variant:'JPEG',byteLength:1024,state:'available'}],isFavorite:false,disposition:'trash'}}]; cursor.value = 'unseen'");
    await fixture.run('emptyLoaded()');
    assert.equal(commands.length, 2); assert.equal(prompts.length, 1); assert.match(prompts[0], /2 loaded/); assert.match(prompts[0], /1 Favorites/); assert.match(prompts[0], /unloaded.*excluded/); assert.match(fixture.run('feedback.value'), /Protected: changed or protected/);
    assert.match(prompts[0], /RAW \(2,048 bytes/); assert.match(prompts[0], /JPEG \(1,024 bytes/);
    assert.match(prompts[0], /only copies/); assert.match(prompts[0], /Backups and delivered or downloaded copies/); assert.match(prompts[0], /protect files from deletion/);
    fixture.run("items.value = [{captureId:'unknown',captureRevision:1,retention:{asOf:new Date(),isFavorite:false}}]");
    await fixture.run('emptyLoaded()'); assert.equal(commands.length, 2); assert.match(fixture.run('feedback.value'), /could not be checked recently/);
});
test('settings require fresh impact acknowledgement and retain drafts during capacity reconciliation', async () => {
    const saved = { settings: { revision: 5, trashRecoveryWindow: '3.00:00:00', restoreProtection: '3.00:00:00', galleryMaximum: '30.00:00:00',
        confirmed: { pressureAge: '01:00:00', ordinaryAge: '1.00:00:00' }, noDelivery: { pressureAge: '1.00:00:00', ordinaryAge: '7.00:00:00' }, waiting: { pressureAge: '1.00:00:00', ordinaryAge: '7.00:00:00' }, heldOrUnfinished: { pressureAge: '14.00:00:00', ordinaryAge: '180.00:00:00' } }, quota: { revision: 6, quotaBytes: 1073741824, highWatermarkBytes: 900000000, recoveryTargetBytes: 700000000, isActive: false } };
    let written = 0, previewed, finishSave, uncertain = true, complete = true;
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: {
        settings: { get: async () => saved, preview: { post: async body => { previewed = body; return { complete, asOf: new Date(), eligibleCaptureCount: complete ? 1 : null, plannedTrashCaptureCount: complete ? 1 : null, plannedBytes: complete ? 2048 : null, impactAcknowledgementRequired: true, quotaActivationAcknowledgementRequired: body.quota.isActive && !saved.quota.isActive }; } }, put: async () => { written++; if (uncertain) throw new Error('lost response'); return await new Promise(resolve => finishSave = resolve); } },
        capacity: { get: async () => ({ observedAt: new Date(now), usedBytes: 0 }) }, history: { get: async () => ({ items: [] }) },
    } });
    await fixture.run('load(true)'); fixture.run('quotaGiB.value = 2'); await fixture.run('load()');
    assert.equal(fixture.run('quotaGiB.value'), 2, 'background capacity reads preserve policy draft');
    await fixture.run('preview()'); await fixture.run('save()'); assert.equal(written, 0);
    assert.equal(previewed.expectedSettingsRevision, 5); assert.equal(previewed.expectedQuotaRevision, 6); assert.equal(previewed.settings.trashRecoveryWindow, '3.00:00:00');
    complete = false; fixture.run('automatic.value = true; quotaActive.value = true'); await fixture.run('preview()'); fixture.run('acknowledge.value = true'); await fixture.run('save()'); assert.equal(written, 0, 'an incomplete preview never permits automatic removal');
    assert.equal(fixture.run('impact.value.plannedBytes'), null);
    fixture.run('automatic.value = false'); await fixture.run('preview()'); await fixture.run('save()'); assert.equal(written, 0, 'quota-only adoption still needs explicit acknowledgement');
    fixture.run('acknowledge.value = true'); await fixture.run('save()'); await fixture.run('save()');
    assert.equal(written, 1, 'incomplete quota accounting can be adopted with automatic removal off'); assert.equal(fixture.run('stale.value'), true); assert.match(fixture.run('feedback.value'), /could not confirm/);
    uncertain = false; complete = true; await fixture.run('load(true)'); fixture.run('quotaGiB.value=2'); await fixture.run('preview()'); fixture.run('acknowledge.value = true');
    const pending = fixture.run('save()'), eventsBeforeDispose = fixture.events.length;
    fixture.dispose(); finishSave({ succeeded: true }); await pending;
    assert.equal(fixture.events.length, eventsBeforeDispose, 'late saves cannot reset a reopened Settings visit dirty state');
});
test('unknown storage allows acknowledged quota setup with cleanup off, then keeps cleanup blocked', async () => {
    const settingsTemplate = { revision: 5, automaticRemovalEnabled: false, trashRecoveryWindow: '3.00:00:00', restoreProtection: '3.00:00:00', galleryMaximum: '30.00:00:00',
        confirmed: { pressureAge: '01:00:00', ordinaryAge: '1.00:00:00' }, noDelivery: { pressureAge: '1.00:00:00', ordinaryAge: '7.00:00:00' }, waiting: { pressureAge: '1.00:00:00', ordinaryAge: '7.00:00:00' }, heldOrUnfinished: { pressureAge: '14.00:00:00', ordinaryAge: '180.00:00:00' } };
    let saved = { settings: settingsTemplate, quota: { revision: 6, quotaBytes: 0, highWatermarkBytes: 0, recoveryTargetBytes: 0, isActive: false } };
    const writes = [], previews = [];
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: {
        settings: { get: async () => saved, preview: { post: async body => {
            previews.push(body);
            return { complete: false, asOf: new Date(now), eligibleCaptureCount: null, plannedTrashCaptureCount: null, plannedBytes: null,
                impactAcknowledgementRequired: true, quotaActivationAcknowledgementRequired: body.quota.isActive && !saved.quota.isActive };
        } }, put: async body => { writes.push(body); saved = { settings: { ...body.settings, revision: 7 }, quota: { ...body.quota, revision: 8 } }; return { succeeded: true }; } },
        capacity: { get: async () => ({ observedAt: new Date(now), usedBytes: 0, unknownAccounting: true }) }, history: { get: async () => ({ items: [] }) },
    } });
    await fixture.run('load(true)');
    fixture.run('quotaGiB.value = 3; highGiB.value = 2; recoveryGiB.value = 1; quotaActive.value = true');
    await fixture.run('preview()'); assert.equal(fixture.run('impact.value.complete'), false);
    fixture.run('acknowledge.value = true'); await fixture.run('save()');
    assert.equal(writes.length, 1, 'an acknowledged quota-only activation can be saved with incomplete accounting');
    assert.equal(writes[0].quota.quotaBytes, 3 * 1024 ** 3); assert.equal(writes[0].quota.highWatermarkBytes, 2 * 1024 ** 3); assert.equal(writes[0].quota.recoveryTargetBytes, 1024 ** 3);
    assert.equal(writes[0].settings.automaticRemovalEnabled, false);
    fixture.run('automatic.value = true'); await fixture.run('preview()'); fixture.run('acknowledge.value = true'); await fixture.run('save()');
    assert.equal(previews.length, 2); assert.equal(previews[1].settings.automaticRemovalEnabled, true);
    assert.equal(writes.length, 1, 'automatic cleanup cannot be saved while accounting is incomplete');
});
test('capacity refresh survives a failed settings read, keeps drafts, and keeps saving blocked', async () => {
    const saved = { settings: { revision: 5 }, quota: { revision: 6, quotaBytes: 1073741824, highWatermarkBytes: 900000000, recoveryTargetBytes: 700000000, isActive: false } };
    let settingsCalls = 0, capacityCalls = 0, previews = 0;
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: {
        settings: { get: async () => { if (++settingsCalls > 1) throw new Error('settings unavailable'); return saved; }, preview: { post: async () => { previews++; return {}; } } },
        capacity: { get: async () => ({ observedAt: new Date(now + ++capacityCalls), usedBytes: capacityCalls * 100, unknownAccounting: true }) }, history: { get: async () => ({ items: [] }) },
    } });
    await fixture.run('load(true)'); fixture.run('quotaGiB.value = 2');
    await fixture.run('load()');
    assert.equal(fixture.run('capacity.value.usedBytes'), 200, 'fresh capacity is kept even when the settings read fails');
    assert.equal(fixture.run('quotaGiB.value'), 2, 'a failed settings read does not overwrite the draft');
    assert.equal(fixture.run('stale.value'), true, 'capacity alone cannot clear the settings freshness gate');
    await fixture.run('preview()'); assert.equal(previews, 0, 'stale settings cannot be reviewed or saved');
    assert.match(fixture.run('feedback.value'), /settings could not be checked/i);
});
test('a successful retry clears stale refresh feedback and shows the refreshed timestamp', async () => {
    const saved = { settings: { revision: 5 }, quota: { revision: 6, quotaBytes: 1073741824, highWatermarkBytes: 900000000, recoveryTargetBytes: 700000000, isActive: false } };
    let capacityCalls = 0, finishCapacity;
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: {
        settings: { get: async () => saved },
        capacity: { get: async () => { if (++capacityCalls === 2) throw new Error('temporary capacity failure'); if (capacityCalls === 3) return await new Promise(resolve => finishCapacity = resolve); return { observedAt: new Date(now + capacityCalls), usedBytes: 100 }; } },
        history: { get: async () => ({ items: [] }) },
    } });
    await fixture.run('load(true)');
    await fixture.run('load()');
    assert.equal(fixture.run('stale.value'), true); assert.match(fixture.run('feedback.value'), /could not check storage/i);
    const retry = fixture.run('load()'); assert.equal(fixture.run('refreshing.value'), true);
    finishCapacity({ observedAt: new Date(now + 3), usedBytes: 100 }); await retry;
    assert.equal(fixture.run('stale.value'), false); assert.equal(fixture.run('feedback.value'), '');
    assert.equal(fixture.run('capacity.value.usedBytes'), 100);
    assert.equal(fixture.run('capacity.value.observedAt.getTime()'), now + 3);
    assert.equal(fixture.run('refreshing.value'), false);
});
test('late refresh results after access loss cannot restore capacity or settings', async () => {
    let finishSettings, finishCapacity;
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: {
        settings: { get: () => new Promise(resolve => finishSettings = resolve) },
        capacity: { get: () => new Promise(resolve => finishCapacity = resolve) }, history: { get: async () => ({ items: [] }) },
    } });
    const pending = fixture.run('load(true)');
    fixture.lose(); finishSettings({ settings: { revision: 1 }, quota: {} }); finishCapacity({ observedAt: new Date(now), usedBytes: 50 });
    await pending;
    assert.equal(fixture.run('capacity.value'), undefined); assert.equal(fixture.run('settings.value'), undefined);
});
test('No delivery intended requires positive unmatched evidence and never infers permission from empty delivery', async () => {
    let sent = 0;
    const props = reactive({ capture: { id: 'unmatched', deliverySummary: { noRoute: 0 }, retention: { captureRevision: 7, disposition: 'active', canRecordNoDeliveryIntent: false } } });
    const fixture = component('NoDeliveryIntentAction', props, { captures: { byId: () => ({ noDeliveryIntent: { post: async () => { sent++; throw { status: 409 }; } } }) } });
    await fixture.run('record()'); assert.equal(sent, 0);
    props.capture.deliverySummary.noRoute = 1; await fixture.run('record()'); assert.equal(sent, 0);
    props.capture.retention.canRecordNoDeliveryIntent = true; await fixture.run('record()'); await fixture.run('record()');
    assert.equal(sent, 1); assert.match(fixture.run('feedback.value'), /cannot cancel work/); assert.equal(fixture.run('uncertain.value'), true);
});

test('Settings validation identifies the invalid field group without blaming valid quota values', () => {
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, {});
    fixture.run("quotaGiB.value = 10; highGiB.value = 9; recoveryGiB.value = 8; trashHours.value = 72; restoreHours.value = 72; galleryDays.value = 30; for(const key of lifetimeKeys){lifetimes[key].pressureHours=24;lifetimes[key].ordinaryDays=7;}");
    assert.equal(fixture.run('valid.value'), true);
    fixture.run('trashHours.value = 0'); assert.equal(fixture.run('valid.value'), false); assert.match(fixture.run('validationErrors.value.trash'), /Trash recovery.*greater than zero/); assert.equal(fixture.run('validationErrors.value.quota'), '');
    fixture.run('trashHours.value = 72; automatic.value = true'); assert.match(fixture.run('validationErrors.value.automatic'), /storage limit.*automatic cleanup/);
    fixture.run('quotaActive.value = true; lifetimes.waiting.pressureHours = 180'); assert.match(fixture.run('validationErrors.value.lifetimes.waiting'), /Space-saving cleanup.*no later than regular cleanup/);
    fixture.run('lifetimes.waiting.pressureHours = 24; memoryMiB.value = 1025'); assert.match(fixture.run('validationErrors.value.memory'), /1.*1024/);
});
test('privacy erasure freezes exact identities, checks an uncertain request without replay, and retains blocked surfaces', async () => {
    let written = 0, continued = 0, previewed;
    const props = { captureId: 'synthetic', displayName: 'SYNTHETIC.JPG', online: true, generation: 0, evidence: { captureRevision: 7, asOf: new Date(), availableOriginals: [{ assetId: 'selected', variant: 'JPEG', byteLength: 1024, state: 'available' }, { assetId: 'history', variant: 'RAW', byteLength: 2048, state: 'removed' }] } };
    const fixture = component('PrivacyErasurePanel', props, { retention: { erasures: {
        preview: { post: async body => { previewed = body; return { status: 'Pending', captureCount: 1, assetCount: 2, galleryPromiseCount: 1, preservationHoldCount: 1 }; } },
        post: async body => { written++; assert.equal(body.confirmed, true); assert.equal(body.operationId, '00000000-0000-0000-0000-000000000001'); throw new Error('lost response'); },
        byOperationId: () => ({ get: async () => ({ status: 'Pending', errorCode: 'PRESERVATION_HOLD_ACTIVE', assets: [{ assetId: 'selected', metadataRedaction: 'Completed', derivatives: 'Pending', originalsAndTransients: 'Blocked' }] }), continueEscaped: { post: async () => continued++ } }),
    } } });
    await fixture.run('previewSelection()'); assert.equal(previewed.selections[0].assetIds.join(','), 'selected,history'); assert.equal(previewed.selections[0].expectedRevision, 7);
    fixture.run('consent.value=true'); await fixture.run('begin()'); await fixture.run('begin()'); await fixture.run('continueOperation()');
    assert.equal(written, 1); assert.equal(continued, 0); assert.equal(fixture.run('stale.value'), true);
    await fixture.run('readStatus()');
    assert.equal(fixture.run('operation.value.status'), 'Pending'); assert.equal(fixture.run('operation.value.assets[0].originalsAndTransients'), 'Blocked');
    assert.equal(fixture.events[0][0], 'requested'); assert.equal(fixture.events[0][1], 'synthetic');
});
test('known removed identities offer metadata erasure without claiming available original bytes', async () => {
    let previewed, writes = 0;
    const prompts = [];
    const props = reactive({ captureId: 'purged', displayName: 'SYNTHETIC.JPG', online: true, generation: 0, evidence: { disposition: 'purged', captureRevision: 9, asOf: new Date(), availableOriginals: [{ assetId: 'removed-jpeg', variant: 'JPEG', byteLength: 1024, state: 'removed' }, { assetId: 'removed-raw', variant: 'RAW', byteLength: 2048, state: 'removed' }] } });
    const fixture = component('PrivacyErasurePanel', props, { retention: { erasures: {
        preview: { post: async body => { previewed = body; return { status: 'Pending', captureCount: 1, assetCount: 2, galleryPromiseCount: 0, preservationHoldCount: 0 }; } },
        post: async () => { writes++; return { status: 'Completed' }; },
    } } }, message => { prompts.push(message); return true; });
    await fixture.run('previewSelection()'); const scopeText=fixture.run('reviewedOriginals.value.map(asset=>`${asset.byteLength.toLocaleString(\'en\')} historical bytes`).join(\'; \')'); assert.equal(fixture.run('reviewedRemovedScope.value'),true); fixture.run('consent.value=true'); await fixture.run('begin()');
    assert.equal(previewed.selections[0].assetIds.join(','), 'removed-jpeg,removed-raw'); assert.equal(previewed.selections[0].expectedRevision, 9); assert.equal(writes, 1);
    assert.equal(prompts.length,0,'native confirmation never opens a browser confirm'); assert.match(scopeText,/1,024 historical bytes/); assert.match(scopeText,/2,048 historical bytes/);
    props.captureId = 'another-purged'; props.evidence.availableOriginals[0].state = 'missing'; await fixture.run('previewSelection()');
    assert.match(fixture.run('feedback.value'), /removal evidence is incomplete/);
});

test('unfinished erasure discovery pages explicitly and restarts from durable reads after auth loss', async () => {
    const queries = [];
    const fixture = component('PrivacyErasurePanel', { online: true, generation: 0 }, { retention: { erasures: { get: async request => {
        queries.push(request.queryParameters);
        return request.queryParameters.cursor ? { operations: [{ operationId: 'older-held', status: 'Pending', errorCode: 'PRESERVATION_HOLD_ACTIVE', pendingCount: 1 }], nextCursor: null } : { operations: [{ operationId: 'recent', status: 'Pending', pendingCount: 1 }], nextCursor: 'older' };
    } } } });
    await fixture.run('loadOperations()'); assert.equal(queries.length, 1, 'first page never auto-scans the catalog');
    await fixture.run('loadOperations(true)'); assert.equal(queries[1].pendingOnly, true); assert.equal(queries[1].pageSize, 50); assert.equal(queries[1].cursor, 'older');
    assert.equal(fixture.run("operations.value.operations.map(item=>item.operationId).join(',')"), 'recent,older-held');
    assert.equal(fixture.run('operations.value.operations[1].errorCode'), 'PRESERVATION_HOLD_ACTIVE');
    fixture.lose(); assert.equal(fixture.run('operations.value'), undefined);
    await fixture.run('loadOperations()'); assert.equal(queries[2].cursor, undefined); assert.equal(queries[2].pendingOnly, true);
});

function storageDocument() {
    return { settings: { revision: 5, automaticRemovalEnabled: false, trashRecoveryWindow: '3.00:00:00', restoreProtection: '3.00:00:00', galleryMaximum: '30.00:00:00', ...Object.fromEntries(['confirmed', 'noDelivery', 'waiting', 'heldOrUnfinished'].map(key => [key, { pressureAge: '01:00:00', ordinaryAge: '7.00:00:00' }])) }, quota: { revision: 6, quotaBytes: 10 * 1024 ** 3, highWatermarkBytes: 9 * 1024 ** 3, recoveryTargetBytes: 8 * 1024 ** 3, isActive: true } };
}
test('dedicated review ignores unchanged settings and fences old estimates after editing and newer requests', async () => {
    const pending = []; let writes = 0;
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: { settings: { get: async () => storageDocument(), preview: { post: body => new Promise(resolve => pending.push({ body, resolve })) }, put: async () => writes++ }, capacity: { get: async () => ({ observedAt: new Date(), unknownAccounting: false }) }, history: { get: async () => ({ items: [] }) } } });
    await fixture.run('load(true)'); await fixture.run('preview()'); assert.equal(pending.length, 0);
    fixture.run('quotaGiB.value=11; quotaGiB.value=10'); assert.equal(fixture.events.at(-1)[1], false, 'reverting all changes resolves the unsaved draft');
    fixture.run('quotaGiB.value=12'); const older = fixture.run('preview()'); await new Promise(resolve => setImmediate(resolve));
    assert.equal(fixture.run('stage.value'), 'review'); assert.equal(Object.isFrozen(pending[0].body), true);
    await fixture.run('backToEditing()'); assert.equal(fixture.run('quotaGiB.value'), 12);
    fixture.run('quotaGiB.value=13'); const newer = fixture.run('preview()'); await new Promise(resolve => setImmediate(resolve));
    pending[1].resolve({ complete: false, warnings: ['newer-estimate'] }); await newer;
    pending[0].resolve({ complete: true, warnings: ['old-estimate'] }); await older;
    assert.equal(fixture.run("impact.value.warnings.join(',')"), 'newer-estimate'); assert.equal(fixture.run('reviewDraft.value.quota.quotaBytes'), 13 * 1024 ** 3);
    fixture.run('quotaGiB.value=14'); await fixture.run('save()'); assert.equal(writes, 0); assert.equal(fixture.run('impact.value'), undefined);
});
test('late storage review after identity loss never restores the review or draft', async () => {
    let finish;
    const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: { settings: { get: async () => storageDocument(), preview: { post: () => new Promise(resolve => finish = resolve) } }, capacity: { get: async () => ({ observedAt: new Date(), unknownAccounting: false }) }, history: { get: async () => ({ items: [] }) } } });
    await fixture.run('load(true)'); fixture.run('quotaGiB.value=12'); const pending = fixture.run('preview()'); await new Promise(resolve => setImmediate(resolve));
    fixture.lose(); finish({ complete: true }); await pending;
    assert.equal(fixture.run('impact.value'), undefined); assert.equal(fixture.run('reviewDraft.value'), undefined); assert.equal(fixture.run('settings.value'), undefined);
});
test('native erasure confirmation requires deliberate consent and cancellation clears exact scope and returns focus', async () => {
    let writes = 0;
    const props = reactive({ captureId: 'private', online: true, generation: 0, evidence: { captureRevision: 9, asOf: new Date(), availableOriginals: [{ assetId: 'raw', variant: 'RAW', byteLength: 2048, state: 'available' }] } });
    const fixture = component('PrivacyErasurePanel', props, { retention: { erasures: { preview: { post: async () => ({ status: 'Pending', assetCount: 1 }) }, post: async () => { writes++; return { status: 'Completed' }; } } } });
    fixture.run("document.activeElement={isConnected:true,focused:false,focus(){this.focused=true}}; confirmation.value={open:false,showModal(){this.open=true},close(){this.open=false}}; confirmationHeading.value={focused:false,focus(){this.focused=true}}");
    await fixture.run('previewSelection()'); assert.equal(fixture.run('confirmation.value.open'), true); assert.equal(fixture.run('confirmationHeading.value.focused'), true, 'initial focus starts at the scope heading');
    await fixture.run('begin()'); assert.equal(writes, 0);
    await fixture.run('cancelConfirmation()'); assert.equal(fixture.run('selection.value'), undefined); assert.equal(fixture.run('document.activeElement.focused'), true);
    await fixture.run('begin()'); assert.equal(writes, 0);
    await fixture.run('previewSelection()'); props.evidence.captureRevision++;
    fixture.run('consent.value=true'); await fixture.run('begin()'); assert.equal(writes, 0, 'changed revision invalidates exact reviewed scope');
    await fixture.run('previewSelection()'); fixture.run('consent.value=true'); await fixture.run('begin()'); assert.equal(writes, 1);
});
test('erasure continuation uses its saved operation identity and fences uncertain outcomes behind a status check', async () => {
    const continued = [];
    const fixture = component('PrivacyErasurePanel', { online: true, generation: 0 }, { retention: { erasures: { byOperationId: id => ({ continueEscaped: { post: async body => { continued.push({ id, body }); throw Error('lost response'); } } }) } } });
    fixture.run("operationId.value='saved-operation'; operation.value={status:'Blocked',errorCode:'PRESERVATION_HOLD_ACTIVE'}");
    await fixture.run('continueOperation()'); assert.equal(continued.length, 0);
    fixture.run("requestContinuation(); consent.value=true"); await fixture.run('continueOperation()'); await fixture.run('continueOperation()');
    assert.equal(continued.length, 1); assert.equal(continued[0].id, 'saved-operation'); assert.equal(continued[0].body.confirmed, true); assert.equal(fixture.run('stale.value'), true);
});
test('a revision change fences pending privacy reads without leaving the next review stuck busy', async () => {
    const requests = [];
    const props = reactive({ captureId: 'private', online: true, generation: 0, evidence: { captureRevision: 1, asOf: new Date(), availableOriginals: [{ assetId: 'raw', state: 'available' }] } });
    const fixture = component('PrivacyErasurePanel', props, { retention: { erasures: { preview: { post: body => new Promise(resolve => requests.push({ body, resolve })) } } } });
    const older = fixture.run('previewSelection()'); props.evidence.captureRevision++;
    assert.equal(fixture.run('busy.value'), false);
    const newer = fixture.run('previewSelection()'); requests[0].resolve({ status: 'Pending', assetCount: 99 }); await older;
    assert.equal(fixture.run('preview.value'), undefined); assert.equal(fixture.run('busy.value'), true);
    requests[1].resolve({ status: 'Pending', assetCount: 1 }); await newer;
    assert.equal(fixture.run('busy.value'), false); assert.equal(fixture.run('selection.value.selections[0].expectedRevision'), 2);
});
test('revision changes during irreversible submission retain operation identity and require status reconciliation', async () => {
    let finish; let previews = 0;
    const props = reactive({ captureId: 'private', online: true, generation: 0, evidence: { captureRevision: 1, asOf: new Date(), availableOriginals: [{ assetId: 'raw', state: 'available' }] } });
    const fixture = component('PrivacyErasurePanel', props, { retention: { erasures: { preview: { post: async () => { previews++; return { status: 'Pending', assetCount: 1 }; } }, post: () => new Promise(resolve => finish = resolve) } } });
    await fixture.run('previewSelection()'); fixture.run('consent.value=true'); const pending = fixture.run('begin()'); props.evidence.captureRevision++;
    assert.equal(fixture.run('busy.value'), false); assert.equal(fixture.run('stale.value'), true); assert.ok(fixture.run('operationId.value'));
    await fixture.run('previewSelection()'); assert.equal(previews, 1);
    finish({ status: 'Pending' }); await pending; assert.equal(fixture.run('stale.value'), true); assert.equal(fixture.run('operation.value'), undefined);
});
test('capacity refresh follows authoritative activation and deactivation without changing the editable baseline', async () => {
    for (const initial of [false, true]) {
        let current = initial, failSettings = false, failCapacity = false;
        const fixture = component('RetentionSettings', { online: true, generation: 0 }, { retention: { settings: { get: async () => { if (failSettings) throw Error('unavailable'); const document = storageDocument(); document.quota.isActive = current; document.quota.revision = current === initial ? 6 : 7; return document; } }, capacity: { get: async () => { if (failCapacity) throw Error('unavailable'); return { observedAt: new Date(), unknownAccounting: false, blockers: current ? [] : ['quota-not-active'] }; } }, history: { get: async () => ({ items: [] }) } } });
        await fixture.run('load(true)'); fixture.run('quotaGiB.value=12'); current = !initial;
        await fixture.run('load()');
        assert.equal(fixture.run('capacityQuotaActive.value'), current); assert.equal(fixture.run('settings.value.quota.isActive'), initial); assert.equal(fixture.run('quotaGiB.value'), 12); assert.equal(fixture.run('policyChanged.value'), true);
        failSettings = true; await fixture.run('load()'); assert.equal(fixture.run('capacityQuotaActive.value'), current); assert.equal(fixture.run('stale.value'), true);
        failSettings = false; failCapacity = true; current = initial; await fixture.run('load()');
        assert.equal(fixture.run('capacityQuotaActive.value'), !initial, 'failed capacity reads retain the dated authoritative snapshot'); assert.equal(fixture.run('stale.value'), true); assert.equal(fixture.run('quotaGiB.value'), 12);
    }
});

test('compact Favorite recovery pins are cleared after authoritative resolution and unmount', async () => {
    const props=reactive({captureId:'synthetic',compact:true,evidence:{captureRevision:7,isFavorite:false,asOf:new Date(now)}});
    const fixture=component('FavoriteControl',props,{captures:{byId:()=>({favorite:{put:async()=>{throw {status:409}}}})}});
    await fixture.run('toggle()');
    assert.ok(fixture.events.some(event=>event[0]==='feedback'&&event[1]===true));
    props.evidence.asOf=new Date(now+1000);
    assert.ok(fixture.events.some(event=>event[0]==='feedback'&&event[1]===false));
    fixture.dispose();
    assert.deepEqual(fixture.events.at(-1),['feedback',false]);
});
