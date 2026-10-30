import assert from 'node:assert/strict';
import test from 'node:test';
import { accessFingerprint, validateWorkspaceAccess, authorizationFailure, workspaceChangeError } from '../app/lib/workspace-permissions.ts';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import ts from 'typescript';
import { ref, shallowRef, computed } from 'vue';
import { parse } from '@vue/compiler-sfc';

const access = role => ({ membershipId: 'member', role, revision: 4,
    capabilities: { canCurate: role !== 'Viewer', canAdminister: ['Owner', 'Administrator'].includes(role), canManageMembers: ['Owner', 'Administrator'].includes(role), canManageOwners: role === 'Owner' },
    viewerPolicy: { includeUnreleased: false, revision: 2 } });

test('permission context validates fixed roles and fails closed for missing or contradictory capabilities', () => {
    for (const role of ['Owner', 'Administrator', 'Curator', 'Viewer']) assert.equal(validateWorkspaceAccess(access(role)).role, role);
    assert.throws(() => validateWorkspaceAccess({ ...access('Viewer'), role: 'FutureRole' }));
    assert.throws(() => validateWorkspaceAccess({ ...access('Viewer'), capabilities: { ...access('Viewer').capabilities, canCurate: true } }));
    assert.throws(() => validateWorkspaceAccess({ ...access('Viewer'), viewerPolicy: { includeUnreleased: true } }));
});

test('membership and Viewer policy revisions invalidate cached private state even when role is unchanged', () => {
    const value = validateWorkspaceAccess(access('Viewer'));
    for (const changed of [{ ...value, revision: 5 }, { ...value, viewerPolicy: { includeUnreleased: false, revision: 3 } }, access('Curator')])
        assert.notEqual(accessFingerprint(value), accessFingerprint(validateWorkspaceAccess(changed)));
    assert.equal(accessFingerprint(value), accessFingerprint(validateWorkspaceAccess(structuredClone(value))));
    for (const role of ['Owner', 'Administrator', 'Curator']) {
        const member = validateWorkspaceAccess(access(role));
        assert.equal(accessFingerprint(member), accessFingerprint({ ...member, viewerPolicy: { includeUnreleased: true, revision: 3 } }), 'Viewer policy does not invalidate an unrelated member role or its drafts');
    }
});

test('denied actions preserve login while revoked membership and authority failure close workspace access', () => {
    assert.equal(authorizationFailure(403, 'permission-denied'), 'action');
    assert.equal(authorizationFailure(403, 'membership-revoked'), 'workspace');
    assert.equal(authorizationFailure(503, 'authorization-unavailable'), 'workspace');
    assert.equal(authorizationFailure(401), 'session');
    assert.equal(authorizationFailure(403), 'action');
    assert.equal(authorizationFailure(409), undefined);
});

test('membership conflict feedback distinguishes the last Owner safeguard from revision and role conflicts', () => {
    assert.match(workspaceChangeError({ errorCode: 'last-owner' }), /must keep an active Owner/);
    assert.match(workspaceChangeError({ errorCode: 'membership-conflict' }), /changed elsewhere/);
    assert.match(workspaceChangeError({ errorCode: 'permission-denied' }), /current role/);
    assert.match(workspaceChangeError({ errorCode: 'membership-not-found' }), /no longer available/);
    assert.equal(workspaceChangeError({ status: 503 }), undefined, 'an unknown outcome must remain uncertain');
});

test('API transport distinguishes denied commands, revoked membership and expired authentication from response machine codes', async () => {
    let transport, ended = 0;
    const closed = [], responses = [new Response(JSON.stringify({ errorCode: 'permission-denied' }), { status: 403 }), new Response(JSON.stringify({ errorCode: 'membership-revoked' }), { status: 403 }), new Response('{}', { status: 401 })];
    const context = vm.createContext({ exports: {}, AbortController, AbortSignal, location: { origin: 'https://synthetic.example' }, fetch: async () => responses.shift(),
        require: name => name.includes('kiota-bundle') ? { DefaultRequestAdapter: class {} }
            : name.includes('kiota-http-fetchlibrary') ? { HttpClient: class { constructor(_client, middleware) { transport = middleware; } } }
                : name.endsWith('/shutterSpoolClient') ? { createShutterSpoolClient: () => ({}) }
                    : name.endsWith('/workspace-permissions') ? { authorizationFailure }
                        : name.endsWith('/workspace-access') ? { closeWorkspaceAccess: revoked => closed.push(revoked) }
                            : name.endsWith('/workspace-context') ? { pageWorkspaceId: 'workspace', workspaceRequestHeaders: () => new Headers() }
                                : name.endsWith('/session') ? { clearAccess: () => ended++, token: async () => '', onAccessLost: () => () => {} } : { withPreviewSlot: callback => callback() },
    });
    vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/api.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    await transport.execute('/management/captures/a/curation', {}, {});
    assert.equal(ended, 0); assert.deepEqual(closed, []);
    await transport.execute('/management/access', {}, {});
    assert.equal(ended, 0); assert.deepEqual(closed, [true]);
    await transport.execute('/management/access', {}, {});
    assert.equal(ended, 1);
});

function accessClient() {
    let generation = 0, clears = 0;
    const session = ref({ access_token: 'synthetic' }), listeners = [], requests = [], timers = new Map();
    const context = vm.createContext({ exports: {}, Promise, AbortSignal,
        setTimeout(callback, delay) { assert.equal(delay, 10_000); timers.set(1, callback); return 1; }, clearTimeout: id => timers.delete(id),
        require: name => name === 'vue' ? { ref, shallowRef, computed }
            : name.endsWith('/session') ? { session, accessGeneration: () => generation, clearPrivateAccess() { generation++; clears++; listeners.forEach(callback => callback()); }, onAccessLost(callback) { listeners.push(callback); } }
                : name.endsWith('/workspace-permissions') ? { accessFingerprint, validateWorkspaceAccess }
                    : { api: { management: { access: { get: options => new Promise((resolve, reject) => requests.push({ options, resolve, reject })) } } }, cancellable: signal => ({ signal }), handleFailure() {} },
    });
    vm.runInContext(ts.transpileModule(readFileSync(new URL('../app/lib/workspace-access.ts', import.meta.url), 'utf8'), { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { api: context.exports, requests, session, clears: () => clears, timeout: () => timers.get(1)?.() };
}
async function settle() { await new Promise(resolve => setImmediate(resolve)); }

test('access reads coalesce, revision changes clear private state once and keep the shared login', async () => {
    const client = accessClient();
    const first = client.api.refreshWorkspaceAccess(), same = client.api.refreshWorkspaceAccess();
    await settle(); assert.equal(client.requests.length, 1);
    client.requests[0].resolve(access('Curator'));
    assert.equal(await first, true); assert.equal(await same, true); assert.equal(client.clears(), 0);
    const changed = client.api.refreshWorkspaceAccess(); await settle();
    client.requests[1].resolve(access('Viewer')); await changed;
    assert.equal(client.clears(), 1); assert.equal(client.api.mayCurate.value, false);
    assert.equal(client.api.workspaceAccessVersion.value, 1); assert.ok(client.session.value);
});

test('revocation fences an older access response and a timed-out authority read removes cached permission', async () => {
    const client = accessClient();
    const first = client.api.refreshWorkspaceAccess(); await settle();
    client.requests[0].resolve(access('Owner')); await first;
    const pending = client.api.refreshWorkspaceAccess(); await settle();
    client.api.closeWorkspaceAccess(true);
    client.requests[1].resolve(access('Owner')); assert.equal(await pending, false);
    assert.equal(client.api.workspaceAccess.value, undefined); assert.ok(client.session.value);
    const timeout = client.api.refreshWorkspaceAccess(); await settle(); client.timeout();
    assert.equal(await timeout, false); assert.equal(client.api.workspaceAccess.value, undefined);
    assert.match(client.api.workspaceAccessError.value, /could not be checked/);
    client.requests[2].resolve(access('Owner')); await settle();
    assert.equal(client.api.workspaceAccess.value, undefined, 'a late timed-out read cannot reopen the workspace');
});

function membershipComponent(name, management, capabilities = { canManageOwners: true }) {
    const events = [], confirmations = [], mounted = [], watched = [];
    let generation = 0;
    const workspaceAccess = ref({ ...access('Owner'), capabilities });
    const permissions = { workspaceAccess, mayAdminister: ref(true), refreshWorkspaceAccess: async () => true, closeWorkspaceAccess: revoked => events.push(['closed', revoked]) };
    const context = vm.createContext({ exports: {}, ref, shallowRef, computed, reactive: value => value,
        watch(_source, callback) { watched.push(callback); }, onMounted: callback => mounted.push(callback), onUnmounted() {}, defineProps: () => ({ online: true, generation: 0 }), AbortController, crypto,
        window: { confirm(message) { confirmations.push(message); return true; } },
        require: name => name.endsWith('/session') ? { accessGeneration: () => generation, onAccessLost: () => () => {} }
            : name.endsWith('/workspace-access') ? permissions
                : name.endsWith('/workspace-permissions') ? { workspaceChangeError }
                    : { api: { management }, cancellable: signal => ({ signal }), handleFailure: error => error.status },
    });
    const source = parse(readFileSync(new URL(`../app/components/${name}.vue`, import.meta.url), 'utf8')).descriptor.scriptSetup.content;
    vm.runInContext(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2023 } }).outputText, context);
    return { run: expression => vm.runInContext(expression, context), events, confirmations, permissions, refresh: () => watched.forEach(callback => callback()), revoke: () => generation++ };
}

test('background membership reconciliation preserves an unsaved role or Viewer visibility choice', async () => {
    let reads = 0;
    const client = membershipComponent('WorkspaceMembers', {
        members: { get: async () => { reads++; return { items: [{ id: 'target', subject: 'Synthetic photographer', issuer: 'https://issuer.example', role: 'Viewer', revision: 2, isActive: true }] }; } },
        viewerPolicy: { get: async () => ({ includeUnreleased: false, revision: 4 }) },
    });
    await client.run('read()'); assert.equal(reads, 1);
    client.run("roles.target = 'Curator'; includeUnreleased.value = true");
    client.refresh(); await settle();
    assert.equal(reads, 1); assert.equal(client.run('roles.target'), 'Curator'); assert.equal(client.run('includeUnreleased.value'), true);
});

test('saving one membership or Viewer policy preserves the other unsaved choices', async () => {
    const items = ['first', 'second'].map(id => ({ id, subject: id, issuer: 'https://issuer.example', role: 'Viewer', revision: 2, isActive: true }));
    const policy = { includeUnreleased: false, revision: 4 }, writes = [];
    const client = membershipComponent('WorkspaceMembers', {
        members: { get: async () => ({ items: structuredClone(items) }), byId: id => ({ role: { post: async body => {
            writes.push({ id, ...body }); const member = items.find(member => member.id === id);
            assert.equal(body.expectedRevision, member.revision); member.role = body.role; member.revision++;
            return { status: 'applied' };
        } } }) },
        viewerPolicy: { get: async () => ({ ...policy }), put: async body => {
            assert.equal(body.expectedRevision, policy.revision); policy.includeUnreleased = body.includeUnreleased; policy.revision++;
            return { status: 'applied' };
        } },
    });
    await client.run('read()');
    client.run("roles.first = 'Curator'; roles.second = 'Administrator'; includeUnreleased.value = true; changeRole(members.value[0])");
    await settle();
    assert.equal(client.run('members.value[0].role'), 'Curator');
    assert.equal(client.run('members.value[0].revision'), 3);
    assert.equal(client.run('roles.second'), 'Administrator', 'saving the first member keeps the second draft');
    assert.equal(client.run('includeUnreleased.value'), true, 'saving a member keeps the policy draft');
    assert.equal(client.run('savedIncludeUnreleased.value'), false);
    client.run('savePolicy()'); await settle();
    assert.equal(client.run('savedIncludeUnreleased.value'), true);
    assert.equal(client.run('policyRevision.value'), 5);
    assert.equal(client.run('roles.second'), 'Administrator', 'saving policy keeps the member draft');
    client.run('changeRole(members.value[1])'); await settle();
    assert.equal(writes[1].role, 'Administrator'); assert.equal(writes[1].expectedRevision, 2);
});

test('membership reads keep edits made during a delayed read and explain drafts invalidated by changed authority', async () => {
    let items = ['changed', 'removed', 'inactive', 'unchanged'].map(id => ({ id, subject: id, issuer: 'https://issuer.example', role: 'Viewer', revision: 2, isActive: true }));
    let policy = { includeUnreleased: false, revision: 4 }, release;
    const client = membershipComponent('WorkspaceMembers', {
        members: { get: async () => { if (release === null) await new Promise(resolve => { release = resolve; }); return { items: structuredClone(items) }; } },
        viewerPolicy: { get: async () => ({ ...policy }) },
    });
    await client.run('read()'); release = null;
    const delayed = client.run('read()');
    client.run("roles.unchanged = 'Curator'; includeUnreleased.value = true");
    release(); await delayed;
    assert.equal(client.run('roles.unchanged'), 'Curator', 'late response keeps the latest local edit');
    assert.equal(client.run('includeUnreleased.value'), true);
    client.run("roles.changed = 'Curator'; roles.removed = 'Curator'; roles.inactive = 'Curator'");
    items = items.filter(member => member.id !== 'removed').map(member => member.id === 'changed' ? { ...member, role: 'Administrator', revision: 3 } : member.id === 'inactive' ? { ...member, isActive: false, revision: 3 } : member);
    policy = { includeUnreleased: false, revision: 5 };
    await client.run('read()');
    assert.equal(client.run('roles.changed'), 'Administrator');
    assert.equal(client.run('roles.removed'), undefined);
    assert.equal(client.run('roles.inactive'), 'Viewer');
    assert.equal(client.run('roles.unchanged'), 'Curator', 'a changed sibling does not abandon this draft');
    assert.equal(client.run('includeUnreleased.value'), false);
    assert.match(client.run('feedback.value'), /unsaved role choices.*reset/i);
    assert.match(client.run('feedback.value'), /unsaved Viewer visibility.*reset/i);
});

test('uncertain self-leave retains its operation ID for an explicit outcome check and keeps last Owner failures actionable', async () => {
    const writes = [];
    const client = membershipComponent('WorkspaceLeave', { membership: { leave: { post: async body => { writes.push({ ...body }); if (writes.length === 1) throw { status: 503 }; if (writes.length === 2) throw { status: 409, errorCode: 'last-owner' }; return { status: 'applied' }; } } } });
    await client.run('leave()');
    assert.match(client.run('feedback.value'), /uncertain/);
    await client.run('leave()');
    assert.equal(writes[0].operationId, writes[1].operationId); assert.equal(writes[0].expectedRevision, 4);
    assert.equal(client.confirmations.length, 1, 'outcome check does not confirm or invent another command');
    assert.match(client.run('feedback.value'), /must keep an active Owner/);
    assert.equal(client.run('pending'), undefined);
    await client.run('leave()');
    assert.notEqual(writes[2].operationId, writes[1].operationId, 'a new confirmed leave after the last-Owner refusal must not replay that recorded refusal');
    assert.equal(client.confirmations.length, 2);
    assert.deepEqual(client.events, [['closed', true]]);
});

test('only an Owner can promote, promotion uses a separate explicit confirmation and uncertain checks keep the same command', async () => {
    const writes = [];
    const client = membershipComponent('WorkspaceMembers', { members: { byId: id => ({ promoteOwner: { post: async body => { writes.push({ id, ...body }); throw { status: 503 }; } } }) } });
    client.permissions.workspaceAccess.value.capabilities.canManageOwners = false;
    client.run("promote({ id: 'target', subject: 'Synthetic photographer', role: 'Curator', revision: 7 })"); await settle();
    assert.equal(writes.length, 0); assert.equal(client.confirmations.length, 0);
    client.permissions.workspaceAccess.value.capabilities.canManageOwners = true;
    client.run("promote({ id: 'target', subject: 'Synthetic photographer', role: 'Curator', revision: 7 })"); await settle();
    assert.equal(writes.length, 1); assert.equal(writes[0].confirmed, true); assert.equal(writes[0].expectedRevision, 7);
    assert.match(client.confirmations[0], /Owners can appoint and remove other Owners/);
    await client.run('submit()');
    assert.equal(writes[0].operationId, writes[1].operationId);
    client.run("changeRole({ id: 'other', role: 'Viewer', revision: 1, isActive: true })"); await settle();
    assert.equal(writes.length, 2, 'an uncertain change prevents a second command');
});
