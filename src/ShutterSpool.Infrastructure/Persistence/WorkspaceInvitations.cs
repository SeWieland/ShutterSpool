using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Security;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed partial class EfAccessGrantStore : IWorkspaceInvitationStore
{
    public async Task<WorkspaceInvitation[]> ListInvitationsAsync(CancellationToken cancellationToken) =>
        await InvitationQuery(await DatabaseNowAsync(cancellationToken)).ToArrayAsync(cancellationToken);

    public async Task<WorkspaceInvitation[]> ListOwnInvitationsAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        await InvitationQuery(await DatabaseNowAsync(cancellationToken), issuer, subject).ToArrayAsync(cancellationToken);

    private IQueryable<WorkspaceInvitation> InvitationQuery(DateTimeOffset now, string? issuer = null, string? subject = null, Guid? id = null) =>
        from offer in database.Set<WorkspaceInvitationEntity>().AsNoTracking()
        where (issuer == null || offer.RecipientIssuer == issuer && offer.RecipientSubject == subject) && (id == null || offer.Id == id)
        orderby offer.CreatedAt, offer.Id
        select new WorkspaceInvitation(offer.Id, offer.RecipientIssuer, offer.RecipientSubject, offer.RecipientLabel, offer.Role,
            offer.Status == "pending" && offer.ExpiresAt <= now ? "expired" : offer.Status,
            offer.Revision, offer.CreatedAt, offer.ExpiresAt);

    public async Task<WorkspaceInvitationResult> ChangeInvitationAsync(WorkspaceInvitationChange change, CancellationToken cancellationToken)
    {
        if (change.OperationId == Guid.Empty || !Bounded(change.ActorIssuer, 2048) || !Bounded(change.ActorSubject, 512))
            throw new ArgumentException("Provide an operation ID and authenticated principal.");
        (string Kind, Guid? Id, long Revision) action = change switch
        {
            WorkspaceInvitationCreate create when Bounded(create.Recipient.Issuer, 2048) && Bounded(create.Recipient.Subject, 512)
                && Bounded(create.Recipient.Email, 256) && WorkspaceAccessPolicy.IsInvitationRole(create.Role) => ("create", (Guid?)null, 0),
            WorkspaceInvitationRenew renew when WorkspaceAccessPolicy.IsInvitationRole(renew.Role) => ("renew", renew.InvitationId, renew.ExpectedRevision),
            WorkspaceInvitationCancel cancel => ("cancel", cancel.InvitationId, cancel.ExpectedRevision),
            WorkspaceInvitationAccept accept => ("accept", accept.InvitationId, accept.ExpectedRevision),
            _ => throw new ArgumentException("Provide a valid invitation action, recipient and non-Owner role."),
        };
        (string kind, Guid? id, long revision) = action;
        if (id == Guid.Empty || revision < 0) throw new ArgumentException("Provide an invitation ID and current revision.");

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Join and membership removal must share the existing access-change transaction boundary.
        await LockAsync(cancellationToken);
        database.ChangeTracker.Clear();
        DateTimeOffset now = await DatabaseNowAsync(cancellationToken);
        string request = JsonSerializer.Serialize(change, change.GetType());
        WorkspaceAccessOperationEntity? recorded = await database.Set<WorkspaceAccessOperationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == change.OperationId, cancellationToken);
        if (recorded is not null)
        {
            if (recorded.Kind != "invitation-" + kind || recorded.Request != request) return new(change.OperationId, "conflict");
            return await ReplayInvitationAsync(change, JsonSerializer.Deserialize<WorkspaceInvitationResult>(recorded.Result)!, now, cancellationToken);
        }

        AccountEnrollmentIdentity? recipient = (change as WorkspaceInvitationCreate)?.Recipient;
        WorkspaceInvitationEntity? offer = recipient is not null
            ? await database.Set<WorkspaceInvitationEntity>().SingleOrDefaultAsync(item => item.RecipientIssuer == recipient.Issuer
                && item.RecipientSubject == recipient.Subject, cancellationToken)
            : await database.Set<WorkspaceInvitationEntity>().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        string? issuer = recipient?.Issuer ?? offer?.RecipientIssuer;
        string? subject = recipient?.Subject ?? offer?.RecipientSubject;
        AuthorizationGrant? member = issuer is null ? null : await database.AuthorizationGrants.SingleOrDefaultAsync(item =>
            item.Issuer == issuer && item.Subject == subject, cancellationToken);
        string before = JsonSerializer.Serialize(new { Offer = offer, Membership = member is null ? null : Membership(member) });
        WorkspaceInvitationResult result = change switch
        {
            WorkspaceInvitationCreate command => await CreateInvitationAsync(command, offer, member, now, cancellationToken),
            WorkspaceInvitationRenew command => RenewInvitation(command, offer, member, now),
            WorkspaceInvitationCancel command => CancelInvitation(command, offer, member, now),
            WorkspaceInvitationAccept command => AcceptInvitation(command, offer, member, now),
            _ => throw new ArgumentException("Unknown invitation action."),
        };
        database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = change.OperationId, Request = request, Result = JsonSerializer.Serialize(result), Kind = "invitation-" + kind,
            ActorIssuer = change.ActorIssuer, ActorSubject = change.ActorSubject, TargetId = result.Offer?.Id ?? id ?? Guid.Empty,
            RecordedAt = now, BeforeState = before,
            AfterState = JsonSerializer.Serialize(new { result.Offer, result.Membership }),
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<WorkspaceInvitationResult> ReplayInvitationAsync(WorkspaceInvitationChange change, WorkspaceInvitationResult prior,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? issuer = prior.Offer?.RecipientIssuer ?? prior.Membership?.Issuer;
        string? subject = prior.Offer?.RecipientSubject ?? prior.Membership?.Subject;
        WorkspaceMembership? member = issuer is null ? null : await GetAsync(issuer, subject!, cancellationToken);
        WorkspaceInvitation? current = prior.Offer is null ? null : await InvitationQuery(now, id: prior.Offer.Id).SingleOrDefaultAsync(cancellationToken);
        string status = prior.Status;
        if (prior.Status is "applied" or "existingMember" && (change is WorkspaceInvitationAccept || prior.Status == "existingMember") && member?.IsActive != true)
            status = "membershipRemoved";
        else if (change is not WorkspaceInvitationAccept && prior.Offer is not null && current?.Revision != prior.Offer.Revision)
            status = "conflict";
        return prior with { Status = status, Offer = current, Membership = member };
    }

    private async Task<WorkspaceInvitationResult> CreateInvitationAsync(WorkspaceInvitationCreate change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (member?.IsActive == true) return InvitationResult(change, "existingMember", offer, member, now);
        if (offer is not null && offer.Status is not ("accepted" or "invalidated")) return InvitationResult(change, "conflict", offer, member, now);
        if (offer is not null)
        {
            // Inviting a removed member requires a new offer identity; old offers never revive.
            database.Remove(offer);
            await database.SaveChangesAsync(cancellationToken);
        }
        offer = new()
        {
            RecipientIssuer = change.Recipient.Issuer, RecipientSubject = change.Recipient.Subject, RecipientLabel = change.Recipient.Email,
            Role = change.Role, Status = "pending", Revision = 1, CreatedAt = now, ExpiresAt = now.AddHours(24),
        };
        database.Add(offer);
        return InvitationResult(change, "applied", offer, member, now);
    }

    private static WorkspaceInvitationResult RenewInvitation(WorkspaceInvitationRenew change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now)
    {
        if (offer is null) return new(change.OperationId, "notFound");
        if (offer.Status is "accepted" or "invalidated") return InvitationResult(change, offer.Status, offer, member, now);
        if (offer.Revision != change.ExpectedRevision) return InvitationResult(change, "conflict", offer, member, now);
        if (member?.IsActive == true) return InvitationResult(change, "existingMember", offer, member, now);
        offer.Revision++;
        offer.Status = "pending";
        offer.Role = change.Role;
        offer.CreatedAt = now;
        offer.ExpiresAt = now.AddHours(24);
        return InvitationResult(change, "applied", offer, member, now);
    }

    private static WorkspaceInvitationResult CancelInvitation(WorkspaceInvitationCancel change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now)
    {
        if (offer is null) return new(change.OperationId, "notFound");
        if (offer.Status is "accepted" or "invalidated") return InvitationResult(change, offer.Status, offer, member, now);
        if (offer.Revision != change.ExpectedRevision) return InvitationResult(change, "conflict", offer, member, now);
        offer.Status = "cancelled";
        offer.Revision++;
        return InvitationResult(change, "applied", offer, member, now);
    }

    private WorkspaceInvitationResult AcceptInvitation(WorkspaceInvitationAccept change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now)
    {
        if (offer is null || offer.RecipientIssuer != change.ActorIssuer || offer.RecipientSubject != change.ActorSubject)
            return new(change.OperationId, "notFound");
        if (!change.EmailVerified) return new(change.OperationId, "verificationRequired");
        if (offer.Status == "accepted") return InvitationResult(change, member?.IsActive == true ? "existingMember" : "membershipRemoved", offer, member, now);
        if (offer.Revision != change.ExpectedRevision) return InvitationResult(change, "conflict", offer, member, now);
        if (offer.Status != "pending") return InvitationResult(change, offer.Status, offer, member, now);
        if (now >= offer.ExpiresAt) return InvitationResult(change, "expired", offer, member, now);
        bool existing = member?.IsActive == true;
        if (!existing)
        {
            if (member is null)
            {
                member = new() { Issuer = offer.RecipientIssuer, Subject = offer.RecipientSubject, Role = offer.Role, IsActive = true };
                database.AuthorizationGrants.Add(member);
            }
            else { member.IsActive = true; member.Role = offer.Role; member.Revision++; }
        }
        offer.Status = "accepted";
        offer.Revision++;
        return InvitationResult(change, existing ? "existingMember" : "applied", offer, member, now);
    }

    private static WorkspaceInvitationResult InvitationResult(WorkspaceInvitationChange change, string status,
        WorkspaceInvitationEntity? offer, AuthorizationGrant? member, DateTimeOffset now) =>
        new(change.OperationId, status, offer is null ? null : new(offer.Id, offer.RecipientIssuer, offer.RecipientSubject,
            offer.RecipientLabel, offer.Role, offer.Status == "pending" && now >= offer.ExpiresAt ? "expired" : offer.Status,
            offer.Revision, offer.CreatedAt, offer.ExpiresAt), member is null ? null : Membership(member));

    private Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken) =>
        database.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
    private static bool Bounded(string? value, int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl);

    private async Task<Guid[]> InvalidateRemovedMemberInvitationsAsync(AuthorizationGrant target, CancellationToken cancellationToken)
    {
        if (target.IsActive) return [];
        WorkspaceInvitationEntity[] offers = await database.Set<WorkspaceInvitationEntity>().Where(item => item.Status == "pending"
            && item.RecipientIssuer == target.Issuer && item.RecipientSubject == target.Subject).ToArrayAsync(cancellationToken);
        foreach (WorkspaceInvitationEntity offer in offers) { offer.Status = "invalidated"; offer.Revision++; }
        return [.. offers.Select(item => item.Id)];
    }
}
