using ShutterSpool.Domain.Security;

namespace ShutterSpool.Application.Security;

public sealed record WorkspaceInvitation(Guid Id, string RecipientIssuer, string RecipientSubject, string RecipientLabel,
    WorkspaceRole Role, string Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public abstract record WorkspaceInvitationChange(Guid OperationId, string ActorIssuer, string ActorSubject);
public sealed record WorkspaceInvitationCreate(Guid OperationId, string ActorIssuer, string ActorSubject,
    AccountEnrollmentIdentity Recipient, WorkspaceRole Role = WorkspaceRole.Viewer)
    : WorkspaceInvitationChange(OperationId, ActorIssuer, ActorSubject);
public sealed record WorkspaceInvitationRenew(Guid OperationId, string ActorIssuer, string ActorSubject,
    Guid InvitationId, long ExpectedRevision, WorkspaceRole Role = WorkspaceRole.Viewer)
    : WorkspaceInvitationChange(OperationId, ActorIssuer, ActorSubject);
public sealed record WorkspaceInvitationCancel(Guid OperationId, string ActorIssuer, string ActorSubject,
    Guid InvitationId, long ExpectedRevision) : WorkspaceInvitationChange(OperationId, ActorIssuer, ActorSubject);
public sealed record WorkspaceInvitationAccept(Guid OperationId, string ActorIssuer, string ActorSubject,
    Guid InvitationId, long ExpectedRevision, bool EmailVerified) : WorkspaceInvitationChange(OperationId, ActorIssuer, ActorSubject);

public sealed record WorkspaceInvitationResult(Guid OperationId, string Status, WorkspaceInvitation? Offer = null,
    WorkspaceMembership? Membership = null, string? SetupEmailStatus = null);

public interface IWorkspaceInvitationStore
{
    Task<WorkspaceInvitation[]> ListInvitationsAsync(CancellationToken cancellationToken);
    Task<WorkspaceInvitation[]> ListOwnInvitationsAsync(string issuer, string subject, CancellationToken cancellationToken);
    Task<WorkspaceInvitationResult> ChangeInvitationAsync(WorkspaceInvitationChange change, CancellationToken cancellationToken);
}

public sealed class WorkspaceInvitationService(IWorkspaceInvitationStore store, IAccountEnrollment enrollment)
{
    public async Task<WorkspaceInvitationResult> CreateAsync(Guid operationId, string actorIssuer, string actorSubject,
        string email, WorkspaceRole role, CancellationToken cancellationToken)
    {
        AccountEnrollmentResult resolved = await enrollment.ResolveAsync(email, cancellationToken);
        if (resolved.Account is null) return new(operationId, resolved.Status);
        WorkspaceInvitationResult result = await store.ChangeInvitationAsync(
            new WorkspaceInvitationCreate(operationId, actorIssuer, actorSubject, resolved.Account, role), cancellationToken);
        return await SendSetupAsync(result, cancellationToken);
    }

    public async Task<WorkspaceInvitationResult> RenewAsync(WorkspaceInvitationRenew change, CancellationToken cancellationToken) =>
        await SendSetupAsync(await store.ChangeInvitationAsync(change, cancellationToken), cancellationToken);

    private async Task<WorkspaceInvitationResult> SendSetupAsync(WorkspaceInvitationResult result, CancellationToken cancellationToken)
    {
        if (result.Status != "applied" || result.Offer is not { Status: "pending" } offer) return result;
        string status = await enrollment.SendSetupEmailAsync(new(offer.RecipientIssuer, offer.RecipientSubject, offer.RecipientLabel), cancellationToken);
        return result with { SetupEmailStatus = status };
    }
}
