export type WorkspaceRole = 'Owner' | 'Administrator' | 'Curator' | 'Viewer';
export interface WorkspaceAccess {
    membershipId: string;
    role: WorkspaceRole;
    revision: number;
    capabilities: { canCurate: boolean; canAdminister: boolean; canManageMembers: boolean; canManageOwners: boolean };
    viewerPolicy: { includeUnreleased: boolean; revision: number };
}

export function validateWorkspaceAccess(value: unknown): WorkspaceAccess {
    const access = value as WorkspaceAccess | undefined;
    if (!access || typeof access.membershipId !== 'string' || !access.membershipId || !['Owner', 'Administrator', 'Curator', 'Viewer'].includes(access.role)
        || !Number.isSafeInteger(access.revision) || !Number.isSafeInteger(access.viewerPolicy?.revision)
        || typeof access.viewerPolicy?.includeUnreleased !== 'boolean') throw new Error('Workspace access could not be confirmed.');
    const expected = { canCurate: access.role !== 'Viewer', canAdminister: ['Owner', 'Administrator'].includes(access.role),
        canManageMembers: ['Owner', 'Administrator'].includes(access.role), canManageOwners: access.role === 'Owner' };
    for (const key of Object.keys(expected) as (keyof typeof expected)[])
        if (access.capabilities?.[key] !== expected[key]) throw new Error('Workspace permissions could not be confirmed.');
    return access;
}

export function accessFingerprint(access: WorkspaceAccess) {
    return JSON.stringify([access.membershipId, access.role, access.revision,
        ...(access.role === 'Viewer' ? [access.viewerPolicy.revision, access.viewerPolicy.includeUnreleased] : [])]);
}

export function authorizationFailure(status?: number, code?: string) {
    if (status === 401) return 'session';
    if (code === 'membership-revoked' || code === 'authorization-unavailable') return 'workspace';
    if (status === 403) return 'action';
}

export function workspaceChangeError(error: unknown) {
    switch ((error as { errorCode?: string })?.errorCode) {
        case 'last-owner': return 'This workspace must keep an active Owner. Appoint another Owner before leaving, demoting or removing the last Owner.';
        case 'membership-conflict': return 'Membership or Viewer visibility changed elsewhere. Review the refreshed values before trying again.';
        case 'permission-denied': return 'Your current role does not permit this change. Review current workspace access.';
        case 'membership-not-found': return 'This membership is no longer available. Review the refreshed member list.';
    }
}
