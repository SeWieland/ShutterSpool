using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed record WorkspaceRecipientHandoff(string Issuer, string Subject, string Label, string OperatorIdentity);

internal sealed partial class EfAccessGrantStore : IWorkspaceInvitationStore
{
    public Task<WorkspaceInvitationRecipient[]> ListRecipientsAsync(CancellationToken token) =>
        database.Set<WorkspaceInvitationRecipientEntity>().AsNoTracking().OrderBy(item => item.Label).ThenBy(item => item.Id)
            .Select(item => new WorkspaceInvitationRecipient(item.Id, item.Label)).ToArrayAsync(token);

    public async Task<WorkspaceInvitation[]> ListInvitationsAsync(CancellationToken token)
    {
        DateTimeOffset now = await DatabaseNowAsync(token);
        return await InvitationQuery(now).ToArrayAsync(token);
    }

    public async Task<WorkspaceInvitation[]> ListOwnInvitationsAsync(string issuer, string subject, CancellationToken token)
    {
        DateTimeOffset now = await DatabaseNowAsync(token);
        return await InvitationQuery(now, issuer, subject).ToArrayAsync(token);
    }

    private IQueryable<WorkspaceInvitation> InvitationQuery(DateTimeOffset now, string? issuer = null, string? subject = null, Guid? id = null) =>
        from offer in database.Set<WorkspaceInvitationEntity>().AsNoTracking()
        join recipient in database.Set<WorkspaceInvitationRecipientEntity>().AsNoTracking() on offer.RecipientId equals recipient.Id
        where (issuer == null || recipient.Issuer == issuer && recipient.Subject == subject) && (id == null || offer.Id == id)
        orderby offer.CreatedAt, offer.Id
        select new WorkspaceInvitation(offer.Id, recipient.Id, recipient.Label, offer.Role,
            offer.Status == "pending" && offer.ExpiresAt <= now ? "expired" : offer.Status,
            offer.Revision, offer.CreatedAt, offer.ExpiresAt);

    internal async Task<WorkspaceInvitationRecipient> RecordRecipientAsync(Guid operation, WorkspaceRecipientHandoff handoff, CancellationToken token)
    {
        if (operation == Guid.Empty || !Bounded(handoff.Issuer, 2048) || !Bounded(handoff.Subject, 512)
            || !Bounded(handoff.Label, 256) || !Bounded(handoff.OperatorIdentity, 512))
            throw new ArgumentException("A recipient handoff requires a bounded trusted issuer, subject, label and host operator identity.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        database.ChangeTracker.Clear();
        string request = JsonSerializer.Serialize(handoff);
        WorkspaceAccessOperationEntity? recorded = await database.Set<WorkspaceAccessOperationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operation, token);
        if (recorded is not null)
        {
            if (recorded.Kind != "operator-recipient" || recorded.Request != request)
                throw new ArgumentException("The operation ID belongs to a different recipient handoff.");
            return JsonSerializer.Deserialize<WorkspaceInvitationRecipient>(recorded.Result)!;
        }
        WorkspaceInvitationRecipientEntity? recipient = await database.Set<WorkspaceInvitationRecipientEntity>()
            .SingleOrDefaultAsync(item => item.Issuer == handoff.Issuer && item.Subject == handoff.Subject, token);
        string before = JsonSerializer.Serialize(recipient);
        if (recipient is null)
        {
            recipient = new() { Issuer = handoff.Issuer, Subject = handoff.Subject, Label = handoff.Label };
            database.Add(recipient);
        }
        else if (recipient.Label != handoff.Label)
            throw new ArgumentException("Recipient references are immutable; use a new explicit principal handoff to correct the recipient.");
        var result = new WorkspaceInvitationRecipient(recipient.Id, recipient.Label);
        database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = operation, Request = request, Result = JsonSerializer.Serialize(result), Kind = "operator-recipient",
            ActorIssuer = "host-operator", ActorSubject = handoff.OperatorIdentity, TargetId = recipient.Id,
            RecordedAt = await DatabaseNowAsync(token), BeforeState = before, AfterState = JsonSerializer.Serialize(recipient),
        });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    public async Task<WorkspaceInvitationResult> ChangeInvitationAsync(WorkspaceInvitationChange change, CancellationToken token)
    {
        if (change.OperationId == Guid.Empty || change.TargetId == Guid.Empty || change.ExpectedRevision < 0
            || !Bounded(change.ActorIssuer, 2048) || !Bounded(change.ActorSubject, 512))
            throw new ArgumentException("Provide an operation, target, revision and authenticated principal.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        database.ChangeTracker.Clear();
        if (workspace?.Enabled == true)
        {
            var selected = workspace.Required;
            if (registry is null || await registry.FindReadyAsync(selected.Id, token) != selected)
                throw new InvalidOperationException("The selected workspace is unavailable or changed.");
            Workspaces.WorkspaceRuntime.ValidateRoot(selected);
            await Workspaces.WorkspaceRuntime.ValidateDatabaseAsync(database, token, selected);
        }
        // clock_timestamp advances while waiting for the tenant lock; transaction-start time does not.
        DateTimeOffset now = await DatabaseNowAsync(token);
        string request = JsonSerializer.Serialize(change);
        string kind = "invitation-" + change.Kind;
        WorkspaceAccessOperationEntity? recorded = await database.Set<WorkspaceAccessOperationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == change.OperationId, token);
        if (recorded is not null)
        {
            if (recorded.Kind != kind || recorded.Request != request) return new(change.OperationId, "conflict");
            WorkspaceInvitationResult prior = JsonSerializer.Deserialize<WorkspaceInvitationResult>(recorded.Result)!;
            // Completed Join is a durable fact, never permission to restore or rewrite today's membership.
            if (change.Kind == "accept")
            {
                WorkspaceMembership? membership = await GetAsync(change.ActorIssuer, change.ActorSubject, token);
                WorkspaceInvitation? current = await InvitationQuery(now, change.ActorIssuer, change.ActorSubject).SingleOrDefaultAsync(token);
                return prior with { Offer = current, Membership = membership,
                    Status = prior.Status is "applied" or "existingMember" && membership?.IsActive != true ? "membershipRemoved" : prior.Status };
            }
            Guid? recordedRecipientId = prior.Offer?.RecipientId ?? (change.Kind == "create" ? change.TargetId : null);
            WorkspaceInvitationRecipientEntity? recordedRecipient = recordedRecipientId is null ? null
                : await database.Set<WorkspaceInvitationRecipientEntity>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == recordedRecipientId, token);
            WorkspaceMembership? currentMember = recordedRecipient is null ? null : await GetAsync(recordedRecipient.Issuer, recordedRecipient.Subject, token);
            return prior with
            {
                Offer = prior.Offer is null ? null : await InvitationQuery(now, id: prior.Offer.Id).SingleOrDefaultAsync(token),
                Membership = currentMember,
                Status = prior.Status == "existingMember" && currentMember?.IsActive != true ? "membershipRemoved" : prior.Status,
            };
        }

        AuthorizationGrant? actor = change.Kind == "accept" ? null : await database.AuthorizationGrants.SingleOrDefaultAsync(item =>
            item.Issuer == change.ActorIssuer && item.Subject == change.ActorSubject && item.IsActive, token);
        WorkspaceInvitationEntity? offer = change.Kind == "create" ? null
            : await database.Set<WorkspaceInvitationEntity>().SingleOrDefaultAsync(item => item.Id == change.TargetId, token);
        WorkspaceInvitationRecipientEntity? recipient = await database.Set<WorkspaceInvitationRecipientEntity>()
            .SingleOrDefaultAsync(item => item.Id == (change.Kind == "create" ? change.TargetId : offer == null ? Guid.Empty : offer.RecipientId), token);
        AuthorizationGrant? member = recipient is null ? null : await database.AuthorizationGrants.SingleOrDefaultAsync(item =>
            item.Issuer == recipient.Issuer && item.Subject == recipient.Subject, token);
        if (change.Kind == "create" && recipient is not null)
            offer = await database.Set<WorkspaceInvitationEntity>().SingleOrDefaultAsync(item => item.RecipientId == recipient.Id, token);
        string before = JsonSerializer.Serialize(new { Offer = offer, Membership = member is null ? null : Membership(member) });

        WorkspaceInvitationResult result;
        if (change.Kind == "accept")
            result = await AcceptInvitationAsync(change, offer, recipient, member, now, token);
        else if (!CanInvite(actor)) result = new(change.OperationId, "denied");
        else if (change.Kind is not ("create" or "renew" or "cancel") || change.Kind != "cancel" && !InvitationRole(change.Role))
            result = new(change.OperationId, "denied");
        else if (recipient is null || change.Kind != "create" && offer is null) result = new(change.OperationId, "notFound");
        else if (offer?.Status == "accepted" && change.Kind != "create")
            result = InvitationResult(change, "accepted", offer, recipient, member, now);
        else if (change.Kind != "create" && offer!.Revision != change.ExpectedRevision)
            result = InvitationResult(change, "conflict", offer, recipient, member, now);
        else if (member?.IsActive == true)
            result = InvitationResult(change, "existingMember", offer, recipient, member, now);
        else if (change.Kind == "create" && offer is not null && offer.Status != "accepted")
            result = InvitationResult(change, "conflict", offer, recipient, member, now);
        else
        {
            if (change.Kind == "create")
            {
                if (offer is not null)
                {
                    // A removed member needs a new offer identity; an accepted offer is never revived.
                    database.Remove(offer);
                    await database.SaveChangesAsync(token);
                }
                offer = new() { RecipientId = recipient.Id, InviterId = actor!.Id, Role = change.Role,
                    Status = "pending", Revision = 1, CreatedAt = now, ExpiresAt = now.AddHours(24) };
                database.Add(offer);
            }
            else
            {
                offer!.Revision++;
                offer.Status = change.Kind == "cancel" ? "cancelled" : "pending";
                if (change.Kind == "renew")
                {
                    offer.InviterId = actor!.Id;
                    offer.Role = change.Role;
                    offer.CreatedAt = now;
                    offer.ExpiresAt = now.AddHours(24);
                }
            }
            result = InvitationResult(change, "applied", offer, recipient, member, now);
        }

        WorkspaceViewerPolicyEntity policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token);
        if (database.ChangeTracker.HasChanges()) policy.AuthorityRevision++;
        database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = change.OperationId, Request = request, Result = JsonSerializer.Serialize(result), Kind = kind,
            ActorIssuer = change.ActorIssuer, ActorSubject = change.ActorSubject, TargetId = result.Offer?.Id ?? change.TargetId,
            RecordedAt = now, BeforeState = before,
            AfterState = JsonSerializer.Serialize(new { Offer = offer, Membership = result.Membership, policy.AuthorityRevision }),
        });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    private async Task<WorkspaceInvitationResult> AcceptInvitationAsync(WorkspaceInvitationChange change,
        WorkspaceInvitationEntity? offer, WorkspaceInvitationRecipientEntity? recipient, AuthorizationGrant? member,
        DateTimeOffset now, CancellationToken token)
    {
        if (offer is null || recipient?.Issuer != change.ActorIssuer || recipient.Subject != change.ActorSubject)
            return new(change.OperationId, "notFound");
        if (!change.EmailVerified) return new(change.OperationId, "verificationRequired");
        if (offer.Status == "accepted") return InvitationResult(change, member?.IsActive == true ? "existingMember" : "membershipRemoved", offer, recipient, member, now);
        if (offer.Revision != change.ExpectedRevision) return InvitationResult(change, "conflict", offer, recipient, member, now);
        if (offer.Status != "pending") return InvitationResult(change, offer.Status, offer, recipient, member, now);
        AuthorizationGrant? inviter = await database.AuthorizationGrants.SingleOrDefaultAsync(item => item.Id == offer.InviterId, token);
        if (!CanInvite(inviter) || !InvitationRole(offer.Role))
        {
            offer.Status = "invalidated";
            offer.Revision++;
            return InvitationResult(change, "invalidated", offer, recipient, member, now);
        }
        now = await DatabaseNowAsync(token);
        if (now >= offer.ExpiresAt) return InvitationResult(change, "expired", offer, recipient, member, now);
        bool existing = member?.IsActive == true;
        if (!existing)
        {
            if (member is null)
            {
                member = new() { Issuer = recipient.Issuer, Subject = recipient.Subject, Role = offer.Role, IsActive = true };
                database.AuthorizationGrants.Add(member);
            }
            else { member.IsActive = true; member.Role = offer.Role; member.Revision++; }
        }
        offer.Status = "accepted";
        offer.Revision++;
        return InvitationResult(change, existing ? "existingMember" : "applied", offer, recipient, member, now);
    }

    private static WorkspaceInvitationResult InvitationResult(WorkspaceInvitationChange change, string status,
        WorkspaceInvitationEntity? offer, WorkspaceInvitationRecipientEntity recipient, AuthorizationGrant? member, DateTimeOffset now) =>
        new(change.OperationId, status, offer is null ? null : Invitation(offer, recipient, now), member is null ? null : Membership(member));

    private static WorkspaceInvitation Invitation(WorkspaceInvitationEntity offer, WorkspaceInvitationRecipientEntity recipient, DateTimeOffset now) =>
        new(offer.Id, recipient.Id, recipient.Label, offer.Role, offer.Status == "pending" && now >= offer.ExpiresAt ? "expired" : offer.Status,
            offer.Revision, offer.CreatedAt, offer.ExpiresAt);

    private Task<DateTimeOffset> DatabaseNowAsync(CancellationToken token) =>
        database.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
    private static bool Bounded(string? value, int length) => !string.IsNullOrWhiteSpace(value) && value.Length <= length && !value.Any(char.IsControl);
    private static bool InvitationRole(WorkspaceRole role) => role is WorkspaceRole.Administrator or WorkspaceRole.Curator or WorkspaceRole.Viewer;
    private static bool CanInvite(AuthorizationGrant? member) => member?.IsActive == true && member.Role is WorkspaceRole.Owner or WorkspaceRole.Administrator;

    private async Task<Guid[]> InvalidateInvitationsAsync(AuthorizationGrant target, CancellationToken token)
    {
        bool authorityLost = !CanInvite(target);
        Guid? recipient = !target.IsActive ? await database.Set<WorkspaceInvitationRecipientEntity>().Where(item =>
            item.Issuer == target.Issuer && item.Subject == target.Subject).Select(item => (Guid?)item.Id).SingleOrDefaultAsync(token) : null;
        if (!authorityLost && recipient is null) return [];
        WorkspaceInvitationEntity[] offers = await database.Set<WorkspaceInvitationEntity>().Where(item => item.Status == "pending"
            && (authorityLost && item.InviterId == target.Id || recipient != null && item.RecipientId == recipient)).ToArrayAsync(token);
        foreach (WorkspaceInvitationEntity offer in offers) { offer.Status = "invalidated"; offer.Revision++; }
        return [.. offers.Select(item => item.Id)];
    }
}
