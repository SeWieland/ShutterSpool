using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Security;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed partial class EfAccessGrantStore : IWorkspaceInvitationStore
{
    private const string Pending = "pending";
    private const string Expired = "expired";
    private const string Accepted = "accepted";
    private const string Invalidated = "invalidated";
    private const string ExistingMember = "existingMember";
    private const string NotFound = "notFound";
    private const string MembershipRemoved = "membershipRemoved";
    private const string Cancelled = "cancelled";

    public async Task<WorkspaceInvitation[]> ListInvitationsAsync(CancellationToken cancellationToken) =>
        await InvitationQuery(await DatabaseNowAsync(cancellationToken)).ToArrayAsync(cancellationToken);

    public async Task<WorkspaceInvitation[]> ListOwnInvitationsAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        await InvitationQuery(await DatabaseNowAsync(cancellationToken), issuer, subject).ToArrayAsync(cancellationToken);

    private IQueryable<WorkspaceInvitation> InvitationQuery(DateTimeOffset now, string? issuer = null, string? subject = null, Guid? id = null) =>
        from offer in database.Set<WorkspaceInvitationEntity>().AsNoTracking()
        where (issuer == null || offer.RecipientIssuer == issuer && offer.RecipientSubject == subject) && (id == null || offer.Id == id)
        orderby offer.CreatedAt, offer.Id
        select new WorkspaceInvitation(offer.Id, offer.RecipientIssuer, offer.RecipientSubject, offer.RecipientLabel, offer.Role,
            offer.Status == Pending && offer.ExpiresAt <= now ? Expired : offer.Status,
            offer.Revision, offer.CreatedAt, offer.ExpiresAt);

    public async Task<WorkspaceInvitationResult> ChangeInvitationAsync(WorkspaceInvitationChange change, CancellationToken cancellationToken)
    {
        (string kind, Guid? id) = ValidateInvitationAction(change);
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
            if (recorded.Kind != "invitation-" + kind || recorded.Request != request) return new(change.OperationId, Conflict);
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

    private static (string Kind, Guid? Id) ValidateInvitationAction(WorkspaceInvitationChange change)
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
        if (action.Id == Guid.Empty || action.Revision < 0) throw new ArgumentException("Provide an invitation ID and current revision.");
        return (action.Kind, action.Id);
    }

    private async Task<WorkspaceInvitationResult> ReplayInvitationAsync(WorkspaceInvitationChange change, WorkspaceInvitationResult prior,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        string? issuer = prior.Offer?.RecipientIssuer ?? prior.Membership?.Issuer;
        string? subject = prior.Offer?.RecipientSubject ?? prior.Membership?.Subject;
        WorkspaceMembership? member = issuer is null ? null : await GetAsync(issuer, subject!, cancellationToken);
        WorkspaceInvitation? current = prior.Offer is null ? null : await InvitationQuery(now, id: prior.Offer.Id).SingleOrDefaultAsync(cancellationToken);
        string status = prior.Status;
        if (prior.Status is Applied or ExistingMember && (change is WorkspaceInvitationAccept || prior.Status == ExistingMember) && member?.IsActive != true)
            status = MembershipRemoved;
        else if (change is not WorkspaceInvitationAccept && prior.Offer is not null && current?.Revision != prior.Offer.Revision)
            status = Conflict;
        return prior with { Status = status, Offer = current, Membership = member };
    }

    private async Task<WorkspaceInvitationResult> CreateInvitationAsync(WorkspaceInvitationCreate change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (member?.IsActive == true) return InvitationResult(change, ExistingMember, offer, member, now);
        if (offer is not null && offer.Status is not (Accepted or Invalidated)) return InvitationResult(change, Conflict, offer, member, now);
        if (offer is not null)
        {
            // Inviting a removed member requires a new offer identity; old offers never revive.
            database.Remove(offer);
            await database.SaveChangesAsync(cancellationToken);
        }
        offer = new()
        {
            RecipientIssuer = change.Recipient.Issuer, RecipientSubject = change.Recipient.Subject, RecipientLabel = change.Recipient.Email,
            Role = change.Role, Status = Pending, Revision = 1, CreatedAt = now, ExpiresAt = now.AddHours(24),
        };
        database.Add(offer);
        return InvitationResult(change, Applied, offer, member, now);
    }

    private static WorkspaceInvitationResult RenewInvitation(WorkspaceInvitationRenew change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now)
    {
        if (offer is null) return new(change.OperationId, NotFound);
        if (offer.Status is Accepted or Invalidated) return InvitationResult(change, offer.Status, offer, member, now);
        if (offer.Revision != change.ExpectedRevision) return InvitationResult(change, Conflict, offer, member, now);
        if (member?.IsActive == true) return InvitationResult(change, ExistingMember, offer, member, now);
        offer.Revision++;
        offer.Status = Pending;
        offer.Role = change.Role;
        offer.CreatedAt = now;
        offer.ExpiresAt = now.AddHours(24);
        return InvitationResult(change, Applied, offer, member, now);
    }

    private static WorkspaceInvitationResult CancelInvitation(WorkspaceInvitationCancel change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now)
    {
        if (offer is null) return new(change.OperationId, NotFound);
        if (offer.Status is Accepted or Invalidated) return InvitationResult(change, offer.Status, offer, member, now);
        if (offer.Revision != change.ExpectedRevision) return InvitationResult(change, Conflict, offer, member, now);
        offer.Status = Cancelled;
        offer.Revision++;
        return InvitationResult(change, Applied, offer, member, now);
    }

    private WorkspaceInvitationResult AcceptInvitation(WorkspaceInvitationAccept change, WorkspaceInvitationEntity? offer,
        AuthorizationGrant? member, DateTimeOffset now)
    {
        if (offer is null || offer.RecipientIssuer != change.ActorIssuer || offer.RecipientSubject != change.ActorSubject)
            return new(change.OperationId, NotFound);
        if (!change.EmailVerified) return new(change.OperationId, "verificationRequired");
        if (offer.Status == Accepted) return InvitationResult(change, member?.IsActive == true ? ExistingMember : MembershipRemoved, offer, member, now);
        if (offer.Revision != change.ExpectedRevision) return InvitationResult(change, Conflict, offer, member, now);
        if (offer.Status != Pending) return InvitationResult(change, offer.Status, offer, member, now);
        if (now >= offer.ExpiresAt) return InvitationResult(change, Expired, offer, member, now);
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
        offer.Status = Accepted;
        offer.Revision++;
        return InvitationResult(change, existing ? ExistingMember : Applied, offer, member, now);
    }

    private static WorkspaceInvitationResult InvitationResult(WorkspaceInvitationChange change, string status,
        WorkspaceInvitationEntity? offer, AuthorizationGrant? member, DateTimeOffset now)
    {
        WorkspaceMembership? membership = member is null ? null : Membership(member);
        if (offer is null) return new(change.OperationId, status, null, membership);
        string offerStatus = offer.Status == Pending && now >= offer.ExpiresAt ? Expired : offer.Status;
        return new(change.OperationId, status, new(offer.Id, offer.RecipientIssuer, offer.RecipientSubject, offer.RecipientLabel,
            offer.Role, offerStatus, offer.Revision, offer.CreatedAt, offer.ExpiresAt), membership);
    }

    private Task<DateTimeOffset> DatabaseNowAsync(CancellationToken cancellationToken) =>
        database.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
    private static bool Bounded(string? value, int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl);

    private async Task<Guid[]> InvalidateRemovedMemberInvitationsAsync(AuthorizationGrant target, CancellationToken cancellationToken)
    {
        if (target.IsActive) return [];
        WorkspaceInvitationEntity[] offers = await database.Set<WorkspaceInvitationEntity>().Where(item => item.Status == Pending
            && item.RecipientIssuer == target.Issuer && item.RecipientSubject == target.Subject).ToArrayAsync(cancellationToken);
        foreach (WorkspaceInvitationEntity offer in offers) { offer.Status = Invalidated; offer.Revision++; }
        return [.. offers.Select(item => item.Id)];
    }
}
