import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';
import { ref, shallowRef, computed, reactive } from 'vue';
import { parse } from '@vue/compiler-sfc';
import { workspaceChangeError } from '../app/lib/workspace-permissions.ts';
import { workspaceSelection, withWorkspace } from '../app/lib/workspace-context.ts';

function component(name, api) {
    let generation = 0, accessRefreshes = 0;
    const events = [], mounted = [], lost = [], unmounted = [];
    const context = vm.createContext({ exports: {}, ref, shallowRef, computed, reactive, AbortController, crypto,
        defineProps: () => ({ online: true, generation: 0 }), defineEmits: () => (...args) => events.push(args),
        watch() {}, onMounted: callback => mounted.push(callback), onUnmounted: callback => unmounted.push(callback),
        window: { confirm: () => true },
        require: name => name.endsWith('/session') ? { accessGeneration: () => generation, onAccessLost: callback => { lost.push(callback); return () => {}; } }
            : name.endsWith('/workspace-access') ? { mayAdminister: ref(true), refreshWorkspaceAccess: async () => { accessRefreshes++; return true; } }
                : name.endsWith('/workspace-permissions') ? { workspaceChangeError }
                    : { api, cancellable: signal => ({ signal }), handleFailure: error => error.status },
    });
    const source = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { run: expression => vm.runInContext(expression, context), events, get accessRefreshes() { return accessRefreshes; },
        loseAccess() { generation++; lost.forEach(callback => callback()); }, unmount() { unmounted.forEach(callback => callback()); } };
}
const offer = { id: 'offer', workspaceId: 'workspace', workspaceName: 'Synthetic studio', role: 'Viewer', status: 'pending', revision: 2 };
const invitation = { id: 'offer', recipientLabel: 'Synthetic recipient', role: 'Viewer', status: 'pending', revision: 2 };
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
    assert.equal(writes[0].body.expectedRevision, 2); assert.deepEqual(client.events.find(event => event[0] === 'joined'), ['joined', 'workspace']);
    assert.deepEqual(client.events[0], ['count', 1]);
});

test('unverified email keeps sign-in usable, refreshes offer status and never announces membership', async () => {
    const client = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => ({ items: [offer], incomplete: false }),
        byWorkspaceId: () => ({ byId: () => ({ accept: { post: async () => { throw { status: 403, errorCode: 'email-verification-required' }; } } }) }),
    } });
    await client.run('read()'); await client.run('join(offers.value[0])');
    assert.match(client.run('feedback.value'), /verification.*sign.in service/i);
    assert.equal(client.events.filter(event => event[0] === 'joined').length, 0); assert.equal(client.run('pending.value'), undefined);
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
    assert.match(client.run('feedback.value'), /could not confirm whether you joined.*Check membership/); assert.equal(client.events.filter(event => event[0] === 'joined').length, 0);
    await client.run('join(offers.value[0])'); assert.equal(writes.length, 1, 'a second action is blocked while the first is uncertain');
    fail = false; await client.run('checkPrevious()');
    assert.deepEqual(calls.slice(2, 4), ['read', 'write']); assert.deepEqual(writes[1], writes[0]);
    assert.equal(client.events.find(event => event[0] === 'joined')[1], 'workspace');
});

test('late offer responses cannot repopulate a signed-out interface and incomplete discovery remains explicit', async () => {
    let resolve;
    const client = component('WorkspaceInvitations', { workspaceOffers: { get: () => new Promise(done => { resolve = done; }) } });
    const read = client.run('read()'); client.loseAccess(); resolve({ items: [offer], incomplete: false }); await read;
    assert.equal(client.run('offers.value.length'), 0);
    const partial = component('WorkspaceInvitations', { workspaceOffers: { get: async () => ({ items: [offer], incomplete: true }) } });
    await partial.run('read()'); assert.equal(partial.run('incomplete.value'), true);
});

test('received invitation count refreshes after Join and incomplete discovery cannot claim a zero count', async () => {
    let joined = false, incomplete = false;
    const client = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => ({ items: joined ? [] : [offer], incomplete }),
        byWorkspaceId: () => ({ byId: () => ({ accept: { post: async body => {
            joined = true; return { operationId: body.operationId, status: 'applied' };
        } } }) }),
    } });
    await client.run('read()');
    assert.deepEqual(client.events.at(-1), ['count', 1]);
    await client.run('join(offers.value[0])');
    assert.deepEqual(client.events.at(-1), ['count', 0]);
    const before = client.events.length;
    incomplete = true;
    await client.run('read()');
    assert.equal(client.events.length, before, 'an incomplete empty page does not overwrite known count');
});

test('expired, cancelled and accepted offers never submit a new Join, and malformed responses block stale actions', async () => {
    let malformed = false, writes = 0;
    const client = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => ({ items: malformed ? undefined : ['expired', 'cancelled', 'accepted'].map(status => ({ ...offer, status })), incomplete: false }),
        byWorkspaceId: () => { writes++; throw new Error('Must not submit a non-pending offer'); },
    } });
    await client.run('read()');
    for (let index = 0; index < 3; index++) await client.run(`join(offers.value[${index}])`);
    assert.equal(writes, 0);
    malformed = true; assert.equal(await client.run('read()'), false);
    assert.match(client.run('error.value'), /could not be checked/);
    await client.run(`join(${JSON.stringify(offer)})`); assert.equal(writes, 0);
});

test('invitation creation accepts an email and defaults to Viewer without recipient provisioning', async () => {
    const writes = [];
    const client = component('WorkspaceInvitationManagement', { management: {
        invitations: { get: async () => ({ items: [] }), post: async body => { writes.push(body); return { operationId: body.operationId, status: 'applied' }; } },
    } });
    await client.run('read()'); assert.equal(client.run('role.value'), 'Viewer');
    client.run("email.value = '   '"); await client.run('create()'); assert.equal(writes.length, 0);
    client.run("email.value = ' photographer@example.test '"); await client.run('create()');
    assert.equal(writes.length, 1); assert.equal(writes[0].role, 'Viewer'); assert.equal(writes[0].email, 'photographer@example.test');
    assert.deepEqual(Object.keys(writes[0]).sort(), ['email', 'operationId', 'role']);
    assert.equal(client.run('email.value'), '');
});

test('interrupted creation checks invitations then retries the same email, role and operation', async () => {
    const writes = [], calls = [];
    let interrupted = true;
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => { calls.push('read'); return { items: [] }; },
        post: async body => {
            calls.push('create'); writes.push(body);
            if (interrupted) throw new Error('Interrupted');
            return { operationId: body.operationId, status: 'applied' };
        },
    } } });
    await client.run('read()'); client.run("email.value = 'photographer@example.test'; role.value = 'Curator'");
    await client.run('create()');
    client.run("email.value = 'someone-else@example.test'; role.value = 'Viewer'");
    await client.run('create()'); assert.equal(writes.length, 1);
    interrupted = false; await client.run('checkPrevious()');
    assert.deepEqual(calls.slice(2, 4), ['read', 'create']); assert.deepEqual(writes[1], writes[0]);
    assert.equal(writes[1].email, 'photographer@example.test'); assert.equal(writes[1].role, 'Curator');
    assert.equal(client.run('pending.value'), undefined);
});

test('existing membership response explains that an invitation did not change its role', async () => {
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => ({ items: [] }), post: async body => ({ operationId: body.operationId, status: 'existingMember' }),
    } } });
    await client.run('read()'); client.run("email.value = 'member@example.test'; role.value = 'Administrator'");
    await client.run('create()');
    assert.match(client.run('feedback.value'), /already has a membership.*role was not changed/);
    assert.equal(client.run('pending.value'), undefined);
});

test('an invitation response for a different operation stays pending until a successful check', async () => {
    const writes = [];
    let readAvailable = true;
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => {
            if (!readAvailable) throw new Error('Invitation list unavailable');
            return { items: [] };
        },
        post: async body => {
            writes.push(body);
            return { operationId: writes.length === 1 ? 'different-operation' : body.operationId, status: 'applied' };
        },
    } } });
    await client.run('read()');
    client.run("email.value = 'photographer@example.test'");
    await client.run('create()');
    assert.equal(client.run('pending.value.operationId'), writes[0].operationId);
    assert.equal(client.run('email.value'), 'photographer@example.test');
    assert.match(client.run('feedback.value'), /could not confirm/);

    readAvailable = false;
    await client.run('checkPrevious()');
    assert.equal(writes.length, 1);
    assert.equal(client.run('pending.value.operationId'), writes[0].operationId);

    readAvailable = true;
    await client.run('checkPrevious()');
    assert.equal(writes.length, 2);
    assert.deepEqual(writes[1], writes[0]);
    assert.equal(client.run('pending.value'), undefined);
    assert.equal(client.run('email.value'), '');
});

test('failed setup email preserves the saved invitation and explicitly retries the same operation', async () => {
    const writes = [];
    let failEmail = true;
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => ({ items: [invitation] }),
        post: async body => {
            writes.push(body);
            return { operationId: body.operationId, status: 'applied', setupEmailStatus: failEmail ? 'setup-email-failed' : 'setup-email-sent' };
        },
    } } });
    await client.run('read()'); client.run("email.value = 'photographer@example.test'"); await client.run('create()');
    assert.equal(client.run('pending.value'), undefined, 'invitation creation has a known successful outcome');
    assert.match(client.run('feedback.value'), /Invitation saved.*email could not be sent.*Retry account setup email/);
    assert.equal(client.run('invitations.value.length'), 1);
    failEmail = false; await client.run('submit(setupEmailRetry.value)');
    assert.deepEqual(writes[1], writes[0]); assert.equal(client.run('setupEmailRetry.value'), undefined);
    assert.match(client.run('feedback.value'), /Account setup email sent/);
});

test('verified existing accounts get a shareable app address instruction without a setup email claim', async () => {
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => ({ items: [] }),
        post: async body => ({ operationId: body.operationId, status: 'applied', setupEmailStatus: 'not-required' }),
    } } });
    await client.run('read()'); client.run("email.value = 'photographer@example.test'"); await client.run('create()');
    assert.match(client.run('feedback.value'), /Share this app.*address.*sign in.*Join workspace/);
    assert.doesNotMatch(client.run('feedback.value'), /email sent/);
    assert.equal(client.run('setupEmailRetry.value'), undefined);
});

test('setup email retry reports a subsequently cancelled invitation without claiming it can be joined', async () => {
    let current = false;
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => ({ items: [{ ...invitation, status: current ? 'cancelled' : 'pending' }] }),
        post: async body => ({ operationId: body.operationId, status: 'applied',
            offer: { ...invitation, status: current ? 'cancelled' : 'pending' },
            setupEmailStatus: current ? 'not-required' : 'setup-email-failed' }),
    } } });
    await client.run('read()'); client.run("email.value = 'photographer@example.test'"); await client.run('create()');
    current = true; await client.run('submit(setupEmailRetry.value)');
    assert.equal(client.run('setupEmailRetry.value'), undefined); assert.equal(client.run('invitations.value[0].status'), 'cancelled');
    assert.match(client.run('feedback.value'), /status has changed/); assert.doesNotMatch(client.run('feedback.value'), /Join workspace|email sent/);
});

for (const [errorCode, status, message] of [
    ['invalid-email', 400, /valid email address/],
    ['account-unavailable', 409, /account is unavailable.*workspace owner/],
    ['enrollment-rejected', 409, /Account setup was refused.*workspace owner/],
    ['enrollment-unavailable', 503, /account setup is temporarily unavailable.*Check invitation/i],
]) test(`invitation creation explains ${errorCode} and preserves the appropriate retry`, async () => {
    let reads = 0;
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => { reads++; return { items: [] }; }, post: async () => { throw { status, errorCode }; },
    } } });
    await client.run('read()'); client.run("email.value = 'photographer@example.test'; role.value = 'Curator'"); await client.run('create()');
    assert.match(client.run('feedback.value'), message); assert.equal(client.run('email.value'), 'photographer@example.test');
    assert.equal(client.run('role.value'), 'Curator'); assert.equal(reads, 1); assert.equal(client.accessRefreshes, 0);
    assert.equal(!!client.run('pending.value'), status === 503);
    assert.equal(client.run('setupEmailRetry.value'), undefined);
});

test('late Join and creation replies cannot announce success after access is lost', async () => {
    let resolveJoin, resolveCreate;
    const recipient = component('WorkspaceInvitations', { workspaceOffers: {
        get: async () => ({ items: [offer], incomplete: false }),
        byWorkspaceId: () => ({ byId: () => ({ accept: { post: body => new Promise(resolve => { resolveJoin = () => resolve({ operationId: body.operationId, status: 'applied' }); }) } }) }),
    } });
    const manager = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => ({ items: [] }),
        post: body => new Promise(resolve => { resolveCreate = () => resolve({ operationId: body.operationId, status: 'applied' }); }),
    } } });
    await recipient.run('read()'); await manager.run('read()'); manager.run("email.value = 'photographer@example.test'");
    const join = recipient.run('join(offers.value[0])'), create = manager.run('create()');
    recipient.loseAccess(); manager.loseAccess(); resolveJoin(); resolveCreate(); await Promise.all([join, create]);
    assert.equal(recipient.events.filter(event => event[0] === 'joined').length, 0); assert.equal(recipient.run('feedback.value'), '');
    assert.equal(manager.run('feedback.value'), ''); assert.equal(manager.run('email.value'), '');
    assert.equal(manager.run('pending.value'), undefined); assert.equal(recipient.run('pending.value'), undefined);
});

test('renew and cancel use current invitation revision and an uncertain renewal blocks competing actions', async () => {
    const writes = [], calls = [];
    let uncertain = true, cancellations = 0;
    const client = component('WorkspaceInvitationManagement', { management: {
        invitations: { get: async () => { calls.push('read'); return { items: [invitation] }; }, byId: id => ({
            renew: { post: async body => { calls.push('renew'); writes.push({ id, body }); if (uncertain) throw new Error('Interrupted'); return { operationId: body.operationId, status: 'applied' }; } },
            cancel: { post: async () => { cancellations++; throw new Error('Must not cancel while renewal is uncertain'); } },
        }) },
    } });
    await client.run('read()'); await client.run("change(invitations.value[0], 'renew')");
    assert.equal(writes[0].body.expectedRevision, 2); assert.equal(writes[0].body.role, 'Viewer');
    await client.run("change(invitations.value[0], 'cancel')"); assert.equal(writes.length, 1);
    uncertain = false; await client.run('checkPrevious()');
    assert.deepEqual(calls.slice(2, 4), ['read', 'renew']); assert.deepEqual(writes[1], writes[0]);
    assert.equal(client.run('pending.value'), undefined);
    assert.equal(cancellations, 0, 'an uncertain renewal must not submit a competing cancellation');
});

test('revision conflict refreshes the current offer and requires a new deliberate decision', async () => {
    let reads = 0, writes = 0;
    const client = component('WorkspaceInvitationManagement', { management: {
        invitations: { get: async () => ({ items: [{ ...invitation, revision: ++reads + 1 }] }), byId: () => ({
            cancel: { post: async () => { writes++; throw { status: 409, errorCode: 'invitation-conflict' }; } },
        }) },
    } });
    await client.run('read()'); await client.run("change(invitations.value[0], 'cancel')"); await settle();
    assert.equal(writes, 1); assert.equal(client.run('invitations.value[0].revision'), 3);
    assert.equal(client.run('pending.value'), undefined); assert.match(client.run('feedback.value'), /changed/);
    assert.equal(client.accessRefreshes, 0, 'a stale invitation needs its own refresh, not another access check');
});

test('permission denial refreshes capabilities and invitations while preserving the invitation form', async () => {
    let reads = 0;
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => { reads++; return { items: [] }; },
        post: async () => { throw { status: 403, errorCode: 'permission-denied' }; },
    } } });
    await client.run('read()'); client.run("email.value = 'photographer@example.test'; role.value = 'Curator'"); await client.run('create()');
    assert.equal(client.accessRefreshes, 1); assert.equal(reads, 2); assert.equal(client.run('pending.value'), undefined);
    assert.equal(client.run('email.value'), 'photographer@example.test'); assert.equal(client.run('role.value'), 'Curator');
    assert.match(client.run('feedback.value'), /current role does not permit/);
});

test('only pending, expired and cancelled invitations can renew and invalidated recipients require fresh Create', async () => {
    const renewals = [], creations = [];
    const statuses = ['pending', 'expired', 'cancelled', 'invalidated', 'accepted'];
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => ({ items: statuses.map(status => ({ ...invitation, id: status, status })) }),
        post: async body => { creations.push(body); return { operationId: body.operationId, status: 'applied' }; },
        byId: id => ({ renew: { post: async body => { renewals.push(id); return { operationId: body.operationId, status: 'applied' }; } } }),
    } } });
    await client.run('read()');
    for (let index = 0; index < statuses.length; index++) await client.run(`change(invitations.value[${index}], 'renew')`);
    assert.deepEqual(renewals, ['pending', 'expired', 'cancelled']);
    await client.run("change(invitations.value[3], 'cancel')");
    client.run("email.value = 'photographer@example.test'"); await client.run('create()');
    assert.equal(creations.length, 1); assert.equal(creations[0].email, 'photographer@example.test');
});

test('cancellation needs no renewal role and retains only its own operation and revision on interruption', async () => {
    const writes = [];
    const client = component('WorkspaceInvitationManagement', { management: { invitations: {
        get: async () => ({ items: [invitation] }), byId: id => ({ cancel: { post: async body => {
            writes.push({ id, body }); throw new Error('Interrupted');
        } } }),
    } } });
    await client.run('read()'); client.run("delete renewalRoles['offer']"); await client.run("change(invitations.value[0], 'cancel')");
    assert.equal(writes.length, 1); assert.equal(writes[0].id, 'offer'); assert.equal(writes[0].body.expectedRevision, 2);
    assert.deepEqual(Object.keys(writes[0].body).sort(), ['expectedRevision', 'operationId']);
    assert.deepEqual(Object.keys(client.run('pending.value')).sort(), ['expectedRevision', 'id', 'kind', 'operationId']);
});

test('browsing invitations preserves same-workspace drafts while Sign out still requires departure', () => {
    const workspaceId = '2f2ed010-2a51-468a-a38a-a2372fba8211';
    const navigations = [];
    const context = vm.createContext({ exports: {}, ref, shallowRef, computed,
        useRoute: () => ({ path: '/', params: {} }), useRouter: () => ({}), useAppConfig: () => ({ sftpgo: {} }), watch: () => () => {}, onMounted() {}, onUnmounted() {},
        window: { location: { assign: path => navigations.push(path), replace: path => navigations.push(path) } },
        require: name => name.endsWith('/session') ? { session: ref({}), onAccessLost: () => () => {} }
            : name.endsWith('/workspace-access') ? { workspaceAccess: ref({}), workspaceRevoked: ref(false) }
                : { getPageWorkspaceId: () => workspaceId, workspaceSelection, withWorkspace },
    });
    const source = parse(readFileSync(new URL('../app/app.vue', import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    vm.runInContext(`memberships.value = [{ id: '${workspaceId}', name: 'Synthetic studio' }]; hasDiscovery.value = true`, context);
    assert.equal(vm.runInContext('canMountOperationalWorkspace.value', context), true);
    vm.runInContext("let draft = 'unfinished bulk tag'; let departures = 0; workspace.value = { requestDeparture: () => { departures++; draft = ''; return false; } }; openInvitations()", context);
    assert.equal(vm.runInContext('invitationsVisible.value', context), true);
    assert.equal(vm.runInContext('draft', context), 'unfinished bulk tag');
    assert.equal(vm.runInContext('departures', context), 0);
    vm.runInContext('invitationsVisible.value = false', context);
    assert.equal(vm.runInContext('draft', context), 'unfinished bulk tag');
    vm.runInContext('leaveSession()', context);
    assert.equal(vm.runInContext('departures', context), 1);
    assert.deepEqual(navigations, [], 'opening invitations keeps the immutable selected page and uses explicit Join separately');
});
