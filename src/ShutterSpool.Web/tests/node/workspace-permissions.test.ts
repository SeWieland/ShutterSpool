import assert from 'node:assert/strict';
import { test } from 'vitest';
import { accessFingerprint, validateWorkspaceAccess, authorizationFailure, workspaceChangeError, type WorkspaceRole } from '../../app/lib/workspace-permissions';

const access = (role: WorkspaceRole) => ({ membershipId: 'member', role, revision: 4,
    capabilities: { canCurate: role !== 'Viewer', canAdminister: ['Owner', 'Administrator'].includes(role), canManageMembers: ['Owner', 'Administrator'].includes(role), canManageOwners: role === 'Owner' },
    viewerPolicy: { includeUnreleased: false, revision: 2 } });

test('permission context validates its shape and trusts server capabilities independently of role', () => {
    for (const role of ['Owner', 'Administrator', 'Curator', 'Viewer'] as const) assert.equal(validateWorkspaceAccess(access(role)).role, role);
    assert.throws(() => validateWorkspaceAccess({ ...access('Viewer'), role: 'FutureRole' }));
    const serverCapabilities = { canCurate: false, canAdminister: true, canManageMembers: false, canManageOwners: true };
    assert.deepEqual(validateWorkspaceAccess({ ...access('Viewer'), capabilities: serverCapabilities }).capabilities, serverCapabilities);
    assert.throws(() => validateWorkspaceAccess({ ...access('Viewer'), capabilities: { ...serverCapabilities, canManageOwners: undefined } }));
    assert.throws(() => validateWorkspaceAccess({ ...access('Viewer'), viewerPolicy: { includeUnreleased: true } }));
});

test('membership and Viewer policy revisions invalidate cached private state even when role is unchanged', () => {
    const value = validateWorkspaceAccess(access('Viewer'));
    for (const changed of [{ ...value, revision: 5 }, { ...value, viewerPolicy: { includeUnreleased: false, revision: 3 } }, access('Curator')])
        assert.notEqual(accessFingerprint(value), accessFingerprint(validateWorkspaceAccess(changed)));
    assert.equal(accessFingerprint(value), accessFingerprint(validateWorkspaceAccess(structuredClone(value))));
    for (const role of ['Owner', 'Administrator', 'Curator'] as const) {
        const member = validateWorkspaceAccess(access(role));
        assert.equal(accessFingerprint(member), accessFingerprint({ ...member, viewerPolicy: { includeUnreleased: true, revision: 3 } }), 'Viewer policy does not invalidate an unrelated member role or its drafts');
    }
    assert.notEqual(accessFingerprint(value), accessFingerprint({ ...value, capabilities: { ...value.capabilities, canManageMembers: true } }), 'server permission changes invalidate cached private state');
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
