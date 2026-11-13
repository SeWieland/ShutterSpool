using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Security;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfAccessGrantStore(ShutterSpoolDbContext database) : IAccessGrantStore
{
    public async Task<WorkspaceMembership?> GetAsync(string issuer, string subject, CancellationToken token)
    {
        AuthorizationGrant? grant = await database.AuthorizationGrants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Issuer == issuer && item.Subject == subject, token);
        return grant is null ? null : Membership(grant);
    }

    public async Task<WorkspaceMembership[]> ListAsync(CancellationToken token) =>
        [.. (await database.AuthorizationGrants.AsNoTracking().OrderBy(item => item.Subject).ThenBy(item => item.Id).ToArrayAsync(token))
            .Select(Membership)];

    public async Task UpdateDisplayNameAsync(string issuer, string subject, string? displayName, CancellationToken token)
    {
        if (displayName?.Length > 256) throw new ArgumentException("Display names must be at most 256 characters.", nameof(displayName));
        // Update only the label so concurrent permission changes cannot be overwritten.
        await database.AuthorizationGrants.Where(item => item.Issuer == issuer && item.Subject == subject && item.IsActive)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.DisplayName, displayName), token);
    }

    public async Task<WorkspaceViewerPolicy> GetViewerPolicyAsync(CancellationToken token)
    {
        WorkspaceViewerPolicyEntity? row = await database.Set<WorkspaceViewerPolicyEntity>().AsNoTracking().SingleOrDefaultAsync(token);
        if (row is null && database.Database.IsRelational()) throw new InvalidOperationException("The workspace access policy is unavailable.");
        return row is null ? new(false, 0) : new(row.IncludeUnreleased, row.Revision);
    }

    public async Task<AccessChangeResult> ChangeAsync(AccessChange change, CancellationToken token)
    {
        if (change.OperationId == Guid.Empty || change.ExpectedRevision < 0)
            throw new ArgumentException("Provide an operation ID and current revision.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        string request = JsonSerializer.Serialize(change);
        WorkspaceAccessOperationEntity? recorded = await database.Set<WorkspaceAccessOperationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == change.OperationId, token);
        if (recorded is not null)
            return recorded.Request == request ? JsonSerializer.Deserialize<AccessChangeResult>(recorded.Result)!
                : new(change.OperationId, "conflict");

        // Always read current authority inside the tenant serialization boundary.
        database.ChangeTracker.Clear();
        AuthorizationGrant? actor = await database.AuthorizationGrants.SingleOrDefaultAsync(item =>
            item.Issuer == change.ActorIssuer && item.Subject == change.ActorSubject && item.IsActive, token);
        AuthorizationGrant? target = change.Kind == AccessChangeKind.Leave ? actor
            : await database.AuthorizationGrants.SingleOrDefaultAsync(item => item.Id == change.TargetId, token);
        WorkspaceViewerPolicyEntity policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token);
        string beforeState = JsonSerializer.Serialize(new { Actor = actor is null ? null : Membership(actor),
            Target = target is null ? null : Membership(target), Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision) });
        AccessChangeResult result = await ApplyChangeAsync(change, actor, target, policy, token);
        RecordChange(change, request, result, target, policy, beforeState);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    private async Task<AccessChangeResult> ApplyChangeAsync(AccessChange change, AuthorizationGrant? actor,
        AuthorizationGrant? target, WorkspaceViewerPolicyEntity policy, CancellationToken token)
    {
        if (actor is null || !Enum.IsDefined(actor.Role)) return new(change.OperationId, "denied");
        if (change.Kind == AccessChangeKind.ViewerPolicy) return ChangeViewerPolicy(change, actor, policy);
        if (target is null || !target.IsActive) return new(change.OperationId, "notFound");
        if (change.Kind != AccessChangeKind.Leave && !WorkspaceAccessPolicy.CanManageMember(actor.Role, target.Role))
            return new(change.OperationId, "denied");
        return change.Kind switch
        {
            AccessChangeKind.Role => await ChangeRoleAsync(change, actor, target, token),
            AccessChangeKind.PromoteOwner => PromoteOwner(change, actor, target),
            AccessChangeKind.Remove or AccessChangeKind.Leave => await RemoveOrLeaveAsync(change, target, token),
            _ => new(change.OperationId, "denied"),
        };
    }

    private static AccessChangeResult ChangeViewerPolicy(AccessChange change, AuthorizationGrant actor, WorkspaceViewerPolicyEntity policy)
    {
        if (!WorkspaceAccessPolicy.CanAdminister(actor.Role)) return new(change.OperationId, "denied");
        if (policy.Revision != change.ExpectedRevision)
            return new(change.OperationId, "conflict", ViewerPolicy: new(policy.IncludeUnreleased, policy.Revision));
        policy.IncludeUnreleased = change.IncludeUnreleased;
        policy.Revision++;
        return new(change.OperationId, "applied", ViewerPolicy: new(policy.IncludeUnreleased, policy.Revision));
    }

    private async Task<AccessChangeResult> ChangeRoleAsync(AccessChange change, AuthorizationGrant actor, AuthorizationGrant target, CancellationToken token)
    {
        if (!WorkspaceAccessPolicy.CanChangeRole(actor.Role, target.Role, change.Role)) return new(change.OperationId, "denied");
        if (target.Revision != change.ExpectedRevision) return new(change.OperationId, "conflict", Membership(target));
        if (target.Role == WorkspaceRole.Owner && !await HasOtherOwnerAsync(target.Id, token))
            return new(change.OperationId, "lastOwner", Membership(target));
        target.Role = change.Role!.Value;
        target.Revision++;
        return new(change.OperationId, "applied", Membership(target));
    }

    private static AccessChangeResult PromoteOwner(AccessChange change, AuthorizationGrant actor, AuthorizationGrant target)
    {
        if (!WorkspaceAccessPolicy.CanPromoteOwner(actor.Role, change.Confirmed)) return new(change.OperationId, "denied");
        if (target.Revision != change.ExpectedRevision) return new(change.OperationId, "conflict", Membership(target));
        target.Role = WorkspaceRole.Owner;
        target.Revision++;
        return new(change.OperationId, "applied", Membership(target));
    }

    private async Task<AccessChangeResult> RemoveOrLeaveAsync(AccessChange change, AuthorizationGrant target, CancellationToken token)
    {
        if (target.Revision != change.ExpectedRevision) return new(change.OperationId, "conflict", Membership(target));
        if (target.Role == WorkspaceRole.Owner && !await HasOtherOwnerAsync(target.Id, token))
            return new(change.OperationId, "lastOwner", Membership(target));
        target.IsActive = false;
        target.Revision++;
        return new(change.OperationId, "applied", Membership(target));
    }

    private Task<bool> HasOtherOwnerAsync(Guid target, CancellationToken token) => database.AuthorizationGrants
        .AnyAsync(item => item.IsActive && item.Role == WorkspaceRole.Owner && item.Id != target, token);

    private void RecordChange(AccessChange change, string request, AccessChangeResult result, AuthorizationGrant? target,
        WorkspaceViewerPolicyEntity policy, string beforeState) => database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = change.OperationId, Request = request, Result = JsonSerializer.Serialize(result),
            ActorIssuer = change.ActorIssuer, ActorSubject = change.ActorSubject, Kind = change.Kind.ToString(),
            TargetId = target?.Id ?? change.TargetId, RecordedAt = DateTimeOffset.UtcNow,
            BeforeState = beforeState, AfterState = JsonSerializer.Serialize(new { Target = target is null ? null : Membership(target),
                Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision) }),
        });

    private Task<int> LockAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-workspace-access', 0))", token);
    private static WorkspaceMembership Membership(AuthorizationGrant grant) =>
        new(grant.Id, grant.Issuer, grant.Subject, grant.Role, grant.Revision, grant.IsActive, grant.DisplayName);

    public Task<bool> IsActiveAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        database.AuthorizationGrants.AsNoTracking().AnyAsync(grant => grant.IsActive && grant.Issuer == issuer && grant.Subject == subject
            && (grant.Role == WorkspaceRole.Owner || grant.Role == WorkspaceRole.Administrator
                || grant.Role == WorkspaceRole.Curator || grant.Role == WorkspaceRole.Viewer), cancellationToken);

    internal async Task PrepareInitialOwnerAsync(Guid operation, string issuer, string subject,
        string? operatorIdentity, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(operatorIdentity) || operatorIdentity.Length > 512)
            throw new ArgumentException("Initial Owner setup requires a trusted host operator identity of at most 512 characters.");
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Initial Owner setup must share the tenant provisioning transaction.");
        await LockAsync(token);
        WorkspaceViewerPolicyEntity policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token);
        if (await database.AuthorizationGrants.AnyAsync(token)
            || await database.Set<WorkspaceAccessOperationEntity>().AnyAsync(item => item.Id == operation, token))
            throw new InvalidOperationException("An uncompleted initial setup cannot replace existing workspace authority or its operation record.");
        var owner = new AuthorizationGrant { Issuer = issuer, Subject = subject, Role = WorkspaceRole.Owner, IsActive = true, IsBootstrap = true };
        string beforeState = JsonSerializer.Serialize(new { Target = (WorkspaceMembership?)null,
            Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision) });
        database.AuthorizationGrants.Add(owner);
        var result = new AccessChangeResult(operation, "applied", Membership(owner));
        database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = operation, Kind = "operator-setup", ActorIssuer = "host-operator", ActorSubject = operatorIdentity,
            TargetId = owner.Id, RecordedAt = DateTimeOffset.UtcNow,
            Request = JsonSerializer.Serialize(new { TargetIssuer = issuer, TargetSubject = subject, OperatorIdentity = operatorIdentity }),
            Result = JsonSerializer.Serialize(result), BeforeState = beforeState,
            AfterState = JsonSerializer.Serialize(new { Target = Membership(owner),
                Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision) }),
        });
    }

}
