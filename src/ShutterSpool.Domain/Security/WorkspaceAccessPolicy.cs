namespace ShutterSpool.Domain.Security;

public static class WorkspaceAccessPolicy
{
    public static bool CanAdminister(WorkspaceRole role) => role is WorkspaceRole.Owner or WorkspaceRole.Administrator;

    public static bool CanManageMember(WorkspaceRole actor, WorkspaceRole target) =>
        CanAdminister(actor) && Enum.IsDefined(target) && (target != WorkspaceRole.Owner || actor == WorkspaceRole.Owner);

    public static bool CanChangeRole(WorkspaceRole actor, WorkspaceRole target, WorkspaceRole? nextRole) =>
        CanManageMember(actor, target) && nextRole is { } role && IsInvitationRole(role);

    public static bool IsInvitationRole(WorkspaceRole role) =>
        role is WorkspaceRole.Administrator or WorkspaceRole.Curator or WorkspaceRole.Viewer;

    public static bool CanPromoteOwner(WorkspaceRole actor, bool confirmed) => actor == WorkspaceRole.Owner && confirmed;
}
