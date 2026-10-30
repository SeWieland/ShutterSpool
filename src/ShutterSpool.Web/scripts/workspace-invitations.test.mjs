import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { ref, shallowRef, computed, reactive } from 'vue';
import { parse } from '@vue/compiler-sfc';
import { workspaceChangeError } from '../app/lib/workspace-permissions.ts';

function component(name, api) {
    let generation = 0;
    const events = [], mounted = [], lost = [], unmounted = [];
    const context = vm.createContext({ exports: {}, ref, shallowRef, computed, reactive, AbortController, crypto,
        defineProps: () => ({ online: true, generation: 0 }), defineEmits: () => (...args) => events.push(args),
        watch() {}, onMounted: callback => mounted.push(callback), onUnmounted: callback => unmounted.push(callback),
        window: { confirm: () => true },
        require: name => name.endsWith('/session') ? { accessGeneration: () => generation, onAccessLost: callback => { lost.push(callback); return () => {}; } }
            : name.endsWith('/workspace-access') ? { mayAdminister: ref(true), refreshWorkspaceAccess: async () => true }
                : name.endsWith('/workspace-permissions') ? { workspaceChangeError }
                    : { api, cancellable: signal => ({ signal }), handleFailure: error => error.status },
    });
    const source = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { run: expression => vm.runInContext(expression, context), events,
        loseAccess() { generation++; lost.forEach(callback => callback()); }, unmount() { unmounted.forEach(callback => callback()); } };
}
const offer = { id: 'offer', workspaceId: 'workspace', workspaceName: 'Synthetic studio', role: 'Viewer', status: 'pending', revision: 2 };
const invitation = { id: 'offer', recipientId: 'recipient', recipientLabel: 'Synthetic recipient', role: 'Viewer', status: 'pending', revision: 2 };
const settle = () => new Promise(resolve => setImmediate(resolve));

test('recipient discovery never accepts an offer and Join uses its exact revision without submitting role or identity', async () => {
    const writes = [];
    const client = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => ({ items: [offer], incomplete: false }),
        byWorkspaceId: workspace => ({ byId: id => ({ accept: { post: async body => {
            writes.push({ workspace, id, body }); return { operationId: body.operationId, status: 'applied' };
        } } }) }),
    } });
    await client.run('read()'); assert.equal(writes.length, 0);
    await client.run('join(offers.value[0])');
    assert.equal(writes.length, 1); assert.equal(writes[0].workspace, 'workspace'); assert.equal(writes[0].id, 'offer');
    assert.deepEqual(Object.keys(writes[0].body).sort(), ['expectedRevision', 'operationId']);
    assert.equal(writes[0].body.expectedRevision, 2); assert.equal(client.events[0][0], 'joined');
});

test('unverified email keeps sign-in usable, refreshes offer status and never announces membership', async () => {
    const client = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => ({ items: [offer], incomplete: false }),
        byWorkspaceId: () => ({ byId: () => ({ accept: { post: async () => { throw { status: 403, errorCode: 'email-verification-required' }; } } }) }),
    } });
    await client.run('read()'); await client.run('join(offers.value[0])');
    assert.match(client.run('feedback.value'), /verification.*sign.in service/i);
    assert.equal(client.events.length, 0); assert.equal(client.run('pending.value'), undefined);
});

test('unknown Join is preserved and deliberate check rereads before retrying the identical operation', async () => {
    const calls = [], writes = [];
    let fail = true;
    const client = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => { calls.push('read'); return { items: [offer], incomplete: false }; },
        byWorkspaceId: () => ({ byId: () => ({ accept: { post: async body => {
            calls.push('write'); writes.push(body); if (fail) throw new Error('Network interrupted');
            return { operationId: body.operationId, status: 'applied' };
        } } }) }),
    } });
    await client.run('read()'); await client.run('join(offers.value[0])');
    assert.match(client.run('feedback.value'), /uncertain/); assert.equal(client.events.length, 0);
    await client.run('join(offers.value[0])'); assert.equal(writes.length, 1, 'a second action is blocked while the first is uncertain');
    fail = false; await client.run('checkPrevious()');
    assert.deepEqual(calls.slice(2, 4), ['read', 'write']); assert.deepEqual(writes[1], writes[0]);
    assert.equal(client.events[0][0], 'joined');
});

test('late offer responses cannot repopulate a signed-out interface and incomplete discovery remains explicit', async () => {
    let resolve;
    const client = component('WorkspaceInvitations', { workspaceOffers: { get: () => new Promise(done => { resolve = done; }) } });
    const read = client.run('read()'); client.loseAccess(); resolve({ items: [offer], incomplete: false }); await read;
    assert.equal(client.run('offers.value.length'), 0);
    const partial = component('WorkspaceInvitations', { workspaceOffers: { get: async () => ({ items: [offer], incomplete: true }) } });
    await partial.run('read()'); assert.equal(partial.run('incomplete.value'), true);
});

test('expired, cancelled and accepted offers never submit a new Join, and malformed responses block stale actions', async () => {
    let malformed = false, writes = 0;
    const client = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => ({ items: malformed ? [{ ...offer, role: 'Owner' }] : ['expired', 'cancelled', 'accepted'].map(status => ({ ...offer, status })), incomplete: false }),
        byWorkspaceId: () => { writes++; throw new Error('Must not submit a non-pending offer'); },
    } });
    await client.run('read()');
    for (let index = 0; index < 3; index++) await client.run(`join(offers.value[${index}])`);
    assert.equal(writes, 0);
    malformed = true; assert.equal(await client.run('read()'), false);
    assert.match(client.run('error.value'), /could not be checked/);
    await client.run(`join(${JSON.stringify(offer)})`); assert.equal(writes, 0);
});

test('invitation creation defaults to Viewer and rejects recipients outside the protected list', async () => {
    const writes = [];
    const client = component('WorkspaceInvitationManagement', { management: {
        invitationRecipients: { get: async () => ({ items: [{ id: 'recipient', label: 'Synthetic recipient' }] }) },
        invitations: { get: async () => ({ items: [] }), post: async body => { writes.push(body); return { operationId: body.operationId, status: 'applied' }; } },
    } });
    await client.run('read()'); assert.equal(client.run('role.value'), 'Viewer');
    client.run("recipientId.value = 'unknown'"); await client.run('create()'); assert.equal(writes.length, 0);
    client.run("recipientId.value = 'recipient'"); await client.run('create()');
    assert.equal(writes.length, 1); assert.equal(writes[0].role, 'Viewer'); assert.equal(writes[0].recipientId, 'recipient');
    assert.deepEqual(Object.keys(writes[0]).sort(), ['operationId', 'recipientId', 'role']);
});

test('renew and cancel use current invitation revision and an uncertain renewal blocks competing actions', async () => {
    const writes = [], calls = [];
    let uncertain = true;
    const client = component('WorkspaceInvitationManagement', { management: {
        invitationRecipients: { get: async () => ({ items: [{ id: 'recipient', label: 'Synthetic recipient' }] }) },
        invitations: { get: async () => { calls.push('read'); return { items: [invitation] }; }, byId: id => ({
            renew: { post: async body => { calls.push('renew'); writes.push({ id, body }); if (uncertain) throw new Error('Interrupted'); return { operationId: body.operationId, status: 'applied' }; } },
            cancel: { post: async () => { throw new Error('Must not cancel while renewal is uncertain'); } },
        }) },
    } });
    await client.run('read()'); await client.run("change(invitations.value[0], 'renew')");
    assert.equal(writes[0].body.expectedRevision, 2); assert.equal(writes[0].body.role, 'Viewer');
    await client.run("change(invitations.value[0], 'cancel')"); assert.equal(writes.length, 1);
    uncertain = false; await client.run('checkPrevious()');
    assert.deepEqual(calls.slice(2, 4), ['read', 'renew']); assert.deepEqual(writes[1], writes[0]);
    assert.equal(client.run('pending.value'), undefined);
});

test('revision conflict refreshes the current offer and requires a new deliberate decision', async () => {
    let reads = 0, writes = 0;
    const client = component('WorkspaceInvitationManagement', { management: {
        invitationRecipients: { get: async () => ({ items: [] }) },
        invitations: { get: async () => ({ items: [{ ...invitation, revision: ++reads + 1 }] }), byId: () => ({
            cancel: { post: async () => { writes++; throw { status: 409, errorCode: 'invitation-conflict' }; } },
        }) },
    } });
    await client.run('read()'); await client.run("change(invitations.value[0], 'cancel')"); await settle();
    assert.equal(writes, 1); assert.equal(client.run('invitations.value[0].revision'), 3);
    assert.equal(client.run('pending.value'), undefined); assert.match(client.run('feedback.value'), /changed/);
});

test('opening invitations honors the existing workspace departure guard before private controls unmount', () => {
    const context = vm.createContext({ exports: {}, ref, shallowRef, computed,
        useRoute: () => ({ path: '/', params: {} }), useAppConfig: () => ({ sftpgo: {} }), watch: () => () => {}, onMounted() {}, onUnmounted() {},
        require: name => name.endsWith('/session') ? { session: ref({}), onAccessLost: () => () => {} }
            : name.endsWith('/workspace-access') ? { workspaceAccess: ref({}), workspaceRevoked: ref(false) }
                : { pageWorkspaceId: null, workspaceSelection: () => ({ state: 'choose' }) },
    });
    const source = parse(readFileSync(new URL('../app/app.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    vm.runInContext('workspace.value = { requestDeparture: () => false }; openInvitations()', context);
    assert.equal(vm.runInContext('invitationsVisible.value', context), false);
    vm.runInContext('workspace.value = { requestDeparture: () => true }; openInvitations()', context);
    assert.equal(vm.runInContext('invitationsVisible.value', context), true);
});
