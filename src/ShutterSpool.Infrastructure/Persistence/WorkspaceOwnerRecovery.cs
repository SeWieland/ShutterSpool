using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed record WorkspaceOwnerRecovery(long ExpectedAuthorityRevision, Guid RepresentativeMembershipId,
    long ExpectedRepresentativeRevision, string TargetIssuer, string TargetSubject, long ExpectedTargetRevision,
    string VerificationReference, string VerificationMethod, string OperatorIdentity, string Reason);
internal sealed record WorkspaceAccessInspection(long AuthorityRevision, WorkspaceMembership[] Memberships);

internal sealed partial class EfAccessGrantStore
{
    internal async Task<WorkspaceAccessInspection> InspectAsync(CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        long revision = await database.Set<WorkspaceViewerPolicyEntity>().Select(item => item.AuthorityRevision).SingleAsync(token);
        return new(revision, await ListAsync(token));
    }

    internal async Task<AccessChangeResult> RecoverOwnerAsync(Guid operation, WorkspaceOwnerRecovery recovery, CancellationToken token)
    {
        if (operation == Guid.Empty || recovery.ExpectedAuthorityRevision < 0 || recovery.RepresentativeMembershipId == Guid.Empty
            || recovery.ExpectedRepresentativeRevision < 0 || recovery.ExpectedTargetRevision < -1
            || string.IsNullOrWhiteSpace(recovery.TargetIssuer) || recovery.TargetIssuer.Length > 2048
            || string.IsNullOrWhiteSpace(recovery.TargetSubject) || recovery.TargetSubject.Length > 512
            || string.IsNullOrWhiteSpace(recovery.VerificationReference) || recovery.VerificationReference.Length > 512
            || string.IsNullOrWhiteSpace(recovery.VerificationMethod) || recovery.VerificationMethod.Length > 256
            || string.IsNullOrWhiteSpace(recovery.OperatorIdentity) || recovery.OperatorIdentity.Length > 512
            || string.IsNullOrWhiteSpace(recovery.Reason) || recovery.Reason.Length > 2048)
            throw new ArgumentException("Recovery requires current revisions, verified principal, protected verification reference/method, operator identity and reason.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        string request = JsonSerializer.Serialize(recovery);
        WorkspaceAccessOperationEntity? recorded = await database.Set<WorkspaceAccessOperationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operation, token);
        if (recorded is not null)
            return recorded.Kind == "operator-recovery" && recorded.Request == request
                ? JsonSerializer.Deserialize<AccessChangeResult>(recorded.Result)! : new(operation, "conflict");
        database.ChangeTracker.Clear();
        WorkspaceViewerPolicyEntity policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token);
        AuthorizationGrant? representative = await database.AuthorizationGrants.SingleOrDefaultAsync(item => item.Id == recovery.RepresentativeMembershipId, token);
        AuthorizationGrant? target = await database.AuthorizationGrants.SingleOrDefaultAsync(item =>
            item.Issuer == recovery.TargetIssuer && item.Subject == recovery.TargetSubject, token);
        string beforeState = JsonSerializer.Serialize(new { Representative = representative is null ? null : Membership(representative),
            Target = target is null ? null : Membership(target), policy.AuthorityRevision });
        AccessChangeResult result;
        if (policy.AuthorityRevision != recovery.ExpectedAuthorityRevision || representative?.IsActive != true
            || representative.Role != WorkspaceRole.Owner || representative.Revision != recovery.ExpectedRepresentativeRevision
            || (target?.Revision ?? -1) != recovery.ExpectedTargetRevision)
            result = new(operation, "conflict");
        else
        {
            if (target is null)
            {
                target = new() { Issuer = recovery.TargetIssuer, Subject = recovery.TargetSubject, Role = WorkspaceRole.Owner, IsActive = true };
                database.AuthorizationGrants.Add(target);
            }
            else { target.IsActive = true; target.Role = WorkspaceRole.Owner; target.Revision++; }
            if (representative.Id != target.Id) { representative.IsActive = false; representative.Revision++; }
            policy.AuthorityRevision++;
            result = new(operation, "applied", Membership(target));
        }
        database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = operation, Request = request, Result = JsonSerializer.Serialize(result),
            ActorIssuer = "host-operator", ActorSubject = recovery.OperatorIdentity, Kind = "operator-recovery",
            TargetId = target?.Id ?? Guid.Empty, RecordedAt = DateTimeOffset.UtcNow,
            NotificationStatus = result.Status == "applied" ? "pending" : null,
            BeforeState = beforeState, AfterState = JsonSerializer.Serialize(new { Representative = representative is null ? null : Membership(representative),
                Target = target is null ? null : Membership(target), policy.AuthorityRevision }),
        });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    internal async Task RecordRecoveryNotificationAsync(Guid operation, string status, CancellationToken token)
    {
        if (status is not ("success" or "failed")) throw new ArgumentException("Record notification success or failed after the manual attempt.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        WorkspaceAccessOperationEntity row = await database.Set<WorkspaceAccessOperationEntity>()
            .SingleAsync(item => item.Id == operation && item.Kind == "operator-recovery", token);
        if (JsonSerializer.Deserialize<AccessChangeResult>(row.Result)?.Status != "applied")
            throw new InvalidOperationException("Only committed recovery has a completion notice.");
        row.NotificationStatus = status;
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}
