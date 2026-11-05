namespace ShutterSpool.Application.Security;

public sealed record WorkspaceInvitationRecipient(Guid Id, string Label);
public sealed record WorkspaceInvitation(Guid Id, Guid RecipientId, string RecipientLabel, WorkspaceRole Role,
    string Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record WorkspaceInvitationChange(Guid OperationId, string ActorIssuer, string ActorSubject,
    string Kind, Guid TargetId, long ExpectedRevision = 0, WorkspaceRole Role = WorkspaceRole.Viewer,
    bool EmailVerified = false);
public sealed record WorkspaceInvitationResult(Guid OperationId, string Status, WorkspaceInvitation? Offer = null,
    WorkspaceMembership? Membership = null);

public interface IWorkspaceInvitationStore
{
    Task<WorkspaceInvitationRecipient[]> ListRecipientsAsync(CancellationToken token);
    Task<WorkspaceInvitation[]> ListInvitationsAsync(CancellationToken token);
    Task<WorkspaceInvitation[]> ListOwnInvitationsAsync(string issuer, string subject, CancellationToken token);
    Task<WorkspaceInvitationResult> ChangeInvitationAsync(WorkspaceInvitationChange change, CancellationToken token);
}
