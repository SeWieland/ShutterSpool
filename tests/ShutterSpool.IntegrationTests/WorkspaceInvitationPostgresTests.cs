using ShutterSpool.Domain.Security;
using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using ShutterSpool.Infrastructure.Persistence;
using Npgsql;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Theory]
    [InlineData("cancel")]
    [InlineData("renew")]
    public async Task Workspace_invitation_accept_and_admin_transition_serialize_in_both_orders(string kind)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        for (int index = 0; index < 2; index++)
        {
            await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
            var store = new EfAccessGrantStore(database);
            string subject = kind + index;
            WorkspaceInvitation offer = await CreateInvitationAsync(store, subject, token);
            var accept = new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", subject, offer.Id, offer.Revision, EmailVerified: true);
            var admin = AdminChange(kind, offer, WorkspaceRole.Administrator);
            if (index == 0)
            {
                WorkspaceInvitationResult changed = await store.ChangeInvitationAsync(admin, token);
                Assert.Equal("applied", changed.Status);
                WorkspaceInvitationResult rejected = await store.ChangeInvitationAsync(accept, token);
                Assert.Equal("conflict", rejected.Status);
                Assert.Null(await store.GetAsync("https://issuer.test", subject, token));
                if (kind == "renew")
                {
                    Assert.True(changed.Offer!.CreatedAt > offer.CreatedAt);
                    Assert.Equal(TimeSpan.FromHours(24), changed.Offer.ExpiresAt - changed.Offer.CreatedAt);
                    Assert.Equal(WorkspaceRole.Administrator, changed.Offer.Role);
                    Assert.Equal("applied", (await store.ChangeInvitationAsync(accept with { OperationId = Guid.NewGuid(), ExpectedRevision = changed.Offer.Revision }, token)).Status);
                }
            }
            else
            {
                Assert.Equal("applied", (await store.ChangeInvitationAsync(accept, token)).Status);
                WorkspaceInvitationResult changed = await store.ChangeInvitationAsync(admin, token);
                Assert.Equal("accepted", changed.Status);
                Assert.Equal(WorkspaceRole.Viewer, changed.Membership!.Role);
                Assert.True(changed.Membership.IsActive);
                Assert.Equal(WorkspaceRole.Viewer, (await store.GetAsync("https://issuer.test", subject, token))!.Role);
            }
        }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("renew")]
    [InlineData("accept")]
    public async Task Workspace_invitation_competing_accept_and_change_have_one_serial_outcome(string kind)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var first = new ShutterSpoolDbContext(fixture.Database.Options);
        await using var second = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(first);
        WorkspaceInvitation offer = await CreateInvitationAsync(store, "racing", token);
        WorkspaceInvitationResult[] results = await Task.WhenAll(
            store.ChangeInvitationAsync(new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "racing", offer.Id, offer.Revision, EmailVerified: true), token),
            new EfAccessGrantStore(second).ChangeInvitationAsync(kind == "accept" ? new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "racing", offer.Id, offer.Revision, true) : AdminChange(kind, offer), token));
        Assert.Single(results, item => item.Status == "applied");
        WorkspaceInvitation current = Assert.Single(await store.ListOwnInvitationsAsync("https://issuer.test", "racing", token));
        WorkspaceMembership? membership = await store.GetAsync("https://issuer.test", "racing", token);
        if (kind == "accept")
        {
            Assert.Single(results, item => item.Status == "existingMember");
            Assert.True(membership!.IsActive);
            Assert.Single(await first.AuthorizationGrants.Where(item => item.Subject == "racing").ToArrayAsync(token));
            return;
        }
        if (current.Status == "accepted") { Assert.True(membership!.IsActive); Assert.Equal("accepted", results[1].Status); }
        else { Assert.Null(membership); Assert.Equal("conflict", results[0].Status); Assert.Equal(kind == "cancel" ? "cancelled" : "pending", current.Status); }
    }

    [Fact]
    public async Task Workspace_invitation_database_clock_is_read_after_lock_wait_and_expired_operation_cannot_be_replayed_as_success()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        CancellationToken token = deadline.Token;
        await using var gate = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(gate);
        WorkspaceInvitation offer = await CreateInvitationAsync(store, "clock-wait", token);
        await gate.Database.ExecuteSqlAsync($"UPDATE workspace_invitations SET \"ExpiresAt\"=clock_timestamp() + interval '1 second' WHERE \"Id\"={offer.Id}", token);
        await using var transaction = await gate.Database.BeginTransactionAsync(token);
        await gate.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-workspace-access', 0))", token);
        await using var waiter = new ShutterSpoolDbContext(fixture.Database.Options);
        await waiter.Database.OpenConnectionAsync(token);
        int process = ((NpgsqlConnection)waiter.Database.GetDbConnection()).ProcessID;
        var accept = new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "clock-wait", offer.Id, offer.Revision, EmailVerified: true);
        Task<WorkspaceInvitationResult> pending = new EfAccessGrantStore(waiter).ChangeInvitationAsync(accept, token);
        await WaitForAccessLockAsync(gate, process, token);
        await gate.Database.ExecuteSqlRawAsync("SELECT pg_sleep(1.1)", token);
        await transaction.CommitAsync(token);
        Assert.Equal("expired", (await pending).Status);
        Assert.Equal("expired", (await new EfAccessGrantStore(waiter).ChangeInvitationAsync(accept, token)).Status);
        Assert.Null(await store.GetAsync("https://issuer.test", "clock-wait", token));
        WorkspaceInvitationResult renewed = await store.ChangeInvitationAsync(new WorkspaceInvitationRenew(Guid.NewGuid(), "https://issuer.test", "operator", offer.Id, offer.Revision), token);
        Assert.Equal(TimeSpan.FromHours(24), renewed.Offer!.ExpiresAt - renewed.Offer.CreatedAt);
        Assert.Equal("expired", (await new EfAccessGrantStore(waiter).ChangeInvitationAsync(accept, token)).Status);
        Assert.Equal("pending", (await new EfAccessGrantStore(waiter).ChangeInvitationAsync(accept, token)).Offer!.Status);
    }

    [Theory]
    [InlineData(WorkspaceRole.Administrator)]
    [InlineData(WorkspaceRole.Curator)]
    public async Task Workspace_invitation_survives_inviter_role_loss(WorkspaceRole next)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        database.AuthorizationGrants.Add(new() { Issuer = "https://issuer.test", Subject = "remaining-owner", Role = WorkspaceRole.Owner, IsActive = true });
        await database.SaveChangesAsync(token);
        WorkspaceInvitation offer = await CreateInvitationAsync(store, "authority-test", token);
        WorkspaceMembership owner = (await store.GetAsync("https://issuer.test", "operator", token))!;
        Assert.Equal("applied", (await store.ChangeAsync(new(Guid.NewGuid(), owner.Issuer, "remaining-owner", owner.Id, owner.Revision, AccessChangeKind.Role, next), token)).Status);
        Assert.Equal("pending", Assert.Single(await store.ListOwnInvitationsAsync(owner.Issuer, "authority-test", token)).Status);
        WorkspaceInvitationResult result = await store.ChangeInvitationAsync(new WorkspaceInvitationAccept(Guid.NewGuid(), owner.Issuer, "authority-test", offer.Id, offer.Revision, true), token);
        Assert.Equal("applied", result.Status);
        Assert.True(result.Membership!.IsActive);
    }

    [Fact]
    public async Task Workspace_invitation_accept_waits_for_role_change_and_removal_and_never_overwrites_either()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        foreach (string kind in new[] { "role", "remove" })
        {
            await using var gate = new ShutterSpoolDbContext(fixture.Database.Options);
            var store = new EfAccessGrantStore(gate);
            WorkspaceInvitation offer = await CreateInvitationAsync(store, "member-" + kind, token);
            gate.AuthorizationGrants.Add(new() { Issuer = "https://issuer.test", Subject = "member-" + kind, Role = WorkspaceRole.Viewer, IsActive = true });
            await gate.SaveChangesAsync(token);
            await using var transaction = await gate.Database.BeginTransactionAsync(token);
            await gate.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-workspace-access', 0))", token);
            await using var waiter = new ShutterSpoolDbContext(fixture.Database.Options);
            await waiter.Database.OpenConnectionAsync(token);
            int process = ((NpgsqlConnection)waiter.Database.GetDbConnection()).ProcessID;
            Task<WorkspaceInvitationResult> pending = new EfAccessGrantStore(waiter).ChangeInvitationAsync(new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "member-" + kind, offer.Id, offer.Revision, EmailVerified: true), token);
            await WaitForAccessLockAsync(gate, process, token);
            if (kind == "role") await gate.Database.ExecuteSqlAsync($"UPDATE authorization_grants SET \"Role\"='Curator', \"Revision\"=1 WHERE \"Subject\"={"member-" + kind}", token);
            else
            {
                await gate.Database.ExecuteSqlAsync($"UPDATE authorization_grants SET \"IsActive\"=false, \"Revision\"=1 WHERE \"Subject\"={"member-" + kind}", token);
                await gate.Database.ExecuteSqlAsync($"UPDATE workspace_invitations SET \"Status\"='invalidated', \"Revision\"=\"Revision\"+1 WHERE \"Id\"={offer.Id}", token);
            }
            await transaction.CommitAsync(token);
            WorkspaceInvitationResult result = await pending;
            Assert.Equal(kind == "role" ? "existingMember" : "conflict", result.Status);
            WorkspaceMembership current = (await store.GetAsync("https://issuer.test", "member-" + kind, token))!;
            Assert.Equal(kind == "role", current.IsActive);
            Assert.Equal(kind == "role" ? WorkspaceRole.Curator : WorkspaceRole.Viewer, current.Role);
            Assert.Equal(1, current.Revision);
        }
    }

    [Fact]
    public async Task Workspace_invitation_audit_failure_rolls_back_grant_and_offer_and_restart_retry_reads_current_state()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        WorkspaceInvitation offer = await CreateInvitationAsync(store, "audit-failure", token);
        var accept = new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "audit-failure", offer.Id, offer.Revision, EmailVerified: true);
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE workspace_access_operations ADD CONSTRAINT reject_invitation_audit CHECK (false) NOT VALID", token);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ChangeInvitationAsync(accept, token));
        await using var restarted = new ShutterSpoolDbContext(fixture.Database.Options);
        var resumed = new EfAccessGrantStore(restarted);
        Assert.Null(await resumed.GetAsync("https://issuer.test", "audit-failure", token));
        Assert.Equal("pending", Assert.Single(await resumed.ListOwnInvitationsAsync("https://issuer.test", "audit-failure", token)).Status);
        await restarted.Database.ExecuteSqlRawAsync("ALTER TABLE workspace_access_operations DROP CONSTRAINT reject_invitation_audit", token);
        WorkspaceInvitationResult applied = await resumed.ChangeInvitationAsync(accept, token);
        Assert.Equal("applied", applied.Status);
        Assert.Equal("conflict", (await resumed.ChangeInvitationAsync(accept with { ActorSubject = "wrong" }, token)).Status);
        WorkspaceAccessOperationEntity audit = await restarted.Set<WorkspaceAccessOperationEntity>().SingleAsync(item => item.Id == accept.OperationId, token);
        Assert.Equal("invitation-accept", audit.Kind);
        using JsonDocument before = JsonDocument.Parse(audit.BeforeState!);
        using JsonDocument after = JsonDocument.Parse(audit.AfterState!);
        Assert.Equal("pending", before.RootElement.GetProperty("Offer").GetProperty("Status").GetString());
        Assert.Equal("accepted", after.RootElement.GetProperty("Offer").GetProperty("Status").GetString());
        Assert.True(after.RootElement.GetProperty("Membership").GetProperty("IsActive").GetBoolean());
        await using var uncertain = new ShutterSpoolDbContext(fixture.Database.Options);
        WorkspaceInvitationResult replay = await new EfAccessGrantStore(uncertain).ChangeInvitationAsync(accept, token);
        Assert.Equal(applied, replay);
        Assert.Single(await uncertain.AuthorizationGrants.Where(item => item.Subject == "audit-failure").ToArrayAsync(token));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("renew")]
    [InlineData("expire")]
    public async Task Workspace_invitation_setup_follows_commit_and_replay_never_sends_for_changed_or_expired_offer(string action)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        int sends = 0;
        var enrollment = new SetupEnrollment(async account =>
        {
            await using var committed = new ShutterSpoolDbContext(fixture.Database.Options);
            WorkspaceInvitationEntity saved = await committed.Set<WorkspaceInvitationEntity>().SingleAsync(token);
            Assert.Equal(account.Subject, saved.RecipientSubject);
            Assert.Equal("pending", saved.Status);
            Assert.Empty(await committed.AuthorizationGrants.Where(item => item.Subject == account.Subject).ToArrayAsync(token));
            return ++sends == 1 ? "setup-email-failed" : "setup-email-sent";
        });
        var service = new WorkspaceInvitationService(store, enrollment);
        Guid operation = Guid.NewGuid();
        WorkspaceInvitationResult first = await service.CreateAsync(operation, "https://issuer.test", "admitted-admin", "setup@example.test", WorkspaceRole.Viewer, token);
        Assert.Equal("applied", first.Status);
        Assert.Equal("setup-email-failed", first.SetupEmailStatus);
        WorkspaceInvitationResult retry = await service.CreateAsync(operation, "https://issuer.test", "admitted-admin", "setup@example.test", WorkspaceRole.Viewer, token);
        Assert.Equal(first.Offer, retry.Offer);
        Assert.Equal("setup-email-sent", retry.SetupEmailStatus);
        Assert.Equal(2, sends);
        WorkspaceInvitation offer = first.Offer!;
        if (action == "expire")
            await database.Database.ExecuteSqlAsync($"UPDATE workspace_invitations SET \"ExpiresAt\"=clock_timestamp()-interval '1 second' WHERE \"Id\"={offer.Id}", token);
        else
            Assert.Equal("applied", (await store.ChangeInvitationAsync(AdminChange(action, offer), token)).Status);
        WorkspaceInvitationResult stale = await service.CreateAsync(operation, "https://issuer.test", "admitted-admin", "setup@example.test", WorkspaceRole.Viewer, token);
        Assert.Null(stale.SetupEmailStatus);
        Assert.Equal(action == "expire" ? "expired" : action == "cancel" ? "cancelled" : "pending", stale.Offer!.Status);
        Assert.Equal(2, sends);
        if (action == "renew")
        {
            var renewal = new WorkspaceInvitationRenew(Guid.NewGuid(), "https://issuer.test", "admitted-admin", offer.Id, stale.Offer.Revision, WorkspaceRole.Curator);
            WorkspaceInvitationResult renewed = await service.RenewAsync(renewal, token);
            Assert.Equal("setup-email-sent", renewed.SetupEmailStatus);
            Assert.Equal(WorkspaceRole.Curator, renewed.Offer!.Role);
            Assert.Equal(TimeSpan.FromHours(24), renewed.Offer.ExpiresAt - renewed.Offer.CreatedAt);
            Assert.Equal(3, sends);
        }
    }

    private sealed class SetupEnrollment(Func<AccountEnrollmentIdentity, Task<string>> send) : IAccountEnrollment
    {
        public Task<AccountEnrollmentResult> ResolveAsync(string email, CancellationToken cancellationToken) =>
            Task.FromResult(new AccountEnrollmentResult("resolved", new("https://issuer.test", "setup-account", email)));
        public Task<string> SendSetupEmailAsync(AccountEnrollmentIdentity account, CancellationToken cancellationToken) => send(account);
    }

    private static async Task<WorkspaceInvitation> CreateInvitationAsync(EfAccessGrantStore store, string subject, CancellationToken token)
    {
        WorkspaceInvitationResult result = await store.ChangeInvitationAsync(new WorkspaceInvitationCreate(Guid.NewGuid(), "https://issuer.test", "operator",
            new("https://issuer.test", subject, "Synthetic " + subject)), token);
        Assert.Equal("applied", result.Status);
        return result.Offer!;
    }

    private static WorkspaceInvitationChange AdminChange(string kind, WorkspaceInvitation offer, WorkspaceRole role = WorkspaceRole.Viewer) =>
        kind == "cancel" ? new WorkspaceInvitationCancel(Guid.NewGuid(), "https://issuer.test", "operator", offer.Id, offer.Revision)
            : new WorkspaceInvitationRenew(Guid.NewGuid(), "https://issuer.test", "operator", offer.Id, offer.Revision, role);

    private static async Task WaitForAccessLockAsync(ShutterSpoolDbContext database, int process, CancellationToken token)
    {
        while (!await database.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid={process} AND wait_event='advisory') AS \"Value\"").SingleAsync(token))
            await Task.Delay(TimeSpan.FromMilliseconds(20), token);
    }

    [Theory]
    [InlineData(60_000_000, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public async Task Workspace_invitation_accepts_only_before_database_expiry_boundary(int remainingMicroseconds, bool allowed)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        WorkspaceInvitation offer = await CreateInvitationAsync(store, "expiry-boundary", token);
        await database.Database.ExecuteSqlAsync($"UPDATE workspace_invitations SET \"ExpiresAt\"=clock_timestamp()+{remainingMicroseconds}*interval '1 microsecond' WHERE \"Id\"={offer.Id}", token);
        WorkspaceInvitationResult result = await store.ChangeInvitationAsync(new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "expiry-boundary", offer.Id, offer.Revision, EmailVerified: true), token);
        Assert.Equal(allowed ? "applied" : "expired", result.Status);
        Assert.Equal(allowed, (await store.GetAsync("https://issuer.test", "expiry-boundary", token))?.IsActive == true);
    }

    [Fact]
    public async Task Workspace_invitation_removal_requires_new_offer_identity_and_old_commands_never_restore_membership()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        var pendingCreate = new WorkspaceInvitationCreate(Guid.NewGuid(), "https://issuer.test", "operator",
            new("https://issuer.test", "pending-removal", "Synthetic pending removal"));
        WorkspaceInvitation pending = (await store.ChangeInvitationAsync(pendingCreate, token)).Offer!;
        database.AuthorizationGrants.Add(new() { Issuer = "https://issuer.test", Subject = "pending-removal", Role = WorkspaceRole.Curator, IsActive = true });
        await database.SaveChangesAsync(token);
        WorkspaceMembership member = (await store.GetAsync("https://issuer.test", "pending-removal", token))!;
        AccessChangeResult removal = await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", member.Id, member.Revision, AccessChangeKind.Remove), token);
        Assert.Equal("applied", removal.Status);
        WorkspaceInvitation invalidated = Assert.Single(await store.ListOwnInvitationsAsync("https://issuer.test", "pending-removal", token));
        Assert.Equal("invalidated", invalidated.Status);
        WorkspaceAccessOperationEntity audit = await database.Set<WorkspaceAccessOperationEntity>().SingleAsync(item => item.Id == removal.OperationId, token);
        using JsonDocument after = JsonDocument.Parse(audit.AfterState!);
        Assert.Equal(pending.Id, Assert.Single(after.RootElement.GetProperty("InvalidatedOffers").EnumerateArray()).GetGuid());
        Assert.False((await store.GetAsync("https://issuer.test", "pending-removal", token))!.IsActive);

        var renewRemoved = new WorkspaceInvitationRenew(Guid.NewGuid(), "https://issuer.test", "operator", pending.Id, invalidated.Revision);
        Assert.Equal("invalidated", (await store.ChangeInvitationAsync(renewRemoved, token)).Status);
        Assert.Equal("invalidated", (await store.ChangeInvitationAsync(
            new WorkspaceInvitationCancel(Guid.NewGuid(), "https://issuer.test", "operator", pending.Id, invalidated.Revision), token)).Status);
        var acceptRemoved = new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "pending-removal", pending.Id, invalidated.Revision, true);
        Assert.Equal("invalidated", (await store.ChangeInvitationAsync(acceptRemoved, token)).Status);
        WorkspaceInvitationResult replacement = await store.ChangeInvitationAsync(pendingCreate with { OperationId = Guid.NewGuid() }, token);
        Assert.Equal("applied", replacement.Status);
        Assert.NotEqual(pending.Id, replacement.Offer!.Id);
        Assert.Equal("pending", replacement.Offer.Status);
        Assert.Equal(TimeSpan.FromHours(24), replacement.Offer.ExpiresAt - replacement.Offer.CreatedAt);
        Assert.Equal("conflict", (await store.ChangeInvitationAsync(pendingCreate, token)).Status);
        Assert.Equal("conflict", (await store.ChangeInvitationAsync(renewRemoved, token)).Status);
        Assert.Equal("invalidated", (await store.ChangeInvitationAsync(acceptRemoved, token)).Status);
        Assert.False((await store.GetAsync("https://issuer.test", "pending-removal", token))!.IsActive);
        Assert.Equal(replacement.Offer.Id, Assert.Single(await store.ListOwnInvitationsAsync("https://issuer.test", "pending-removal", token)).Id);

        WorkspaceInvitation first = await CreateInvitationAsync(store, "new-decision", token);
        var accept = new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "new-decision", first.Id, first.Revision, EmailVerified: true);
        WorkspaceInvitationResult accepted = await store.ChangeInvitationAsync(accept, token);
        member = accepted.Membership!;
        await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", member.Id, member.Revision, AccessChangeKind.Remove), token);
        Assert.Equal("accepted", (await store.ChangeInvitationAsync(new WorkspaceInvitationRenew(Guid.NewGuid(), "https://issuer.test", "operator", first.Id, accepted.Offer!.Revision), token)).Status);
        WorkspaceInvitationResult fresh = await store.ChangeInvitationAsync(new WorkspaceInvitationCreate(Guid.NewGuid(), "https://issuer.test", "operator", new(first.RecipientIssuer, first.RecipientSubject, first.RecipientLabel), WorkspaceRole.Curator), token);
        Assert.Equal("applied", fresh.Status);
        Assert.NotEqual(first.Id, fresh.Offer!.Id);
        Assert.Equal(TimeSpan.FromHours(24), fresh.Offer.ExpiresAt - fresh.Offer.CreatedAt);
        Assert.Equal("membershipRemoved", (await store.ChangeInvitationAsync(accept, token)).Status);
        Assert.False((await store.GetAsync("https://issuer.test", "new-decision", token))!.IsActive);
        WorkspaceInvitationResult rejoined = await store.ChangeInvitationAsync(new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "new-decision", fresh.Offer.Id, fresh.Offer.Revision, EmailVerified: true), token);
        Assert.Equal("applied", rejoined.Status);
        Assert.Equal(WorkspaceRole.Curator, rejoined.Membership!.Role);
        Assert.Equal(member.Id, rejoined.Membership.Id);
        Assert.Equal(1, await database.Set<WorkspaceInvitationEntity>().CountAsync(item => item.RecipientIssuer == first.RecipientIssuer && item.RecipientSubject == first.RecipientSubject, token));
    }

    [Fact]
    public async Task Workspace_invitation_existing_member_creation_and_operation_retry_return_current_role_without_new_offer()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        database.AuthorizationGrants.Add(new() { Issuer = "https://issuer.test", Subject = "existing", Role = WorkspaceRole.Curator, IsActive = true });
        await database.SaveChangesAsync(token);
        var create = new WorkspaceInvitationCreate(Guid.NewGuid(), "https://issuer.test", "operator", new("https://issuer.test", "existing", "Synthetic existing member"), WorkspaceRole.Administrator);
        WorkspaceInvitationResult result = await store.ChangeInvitationAsync(create, token);
        Assert.Equal("existingMember", result.Status);
        Assert.Equal(WorkspaceRole.Curator, result.Membership!.Role);
        Assert.Null(result.Offer);
        await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", result.Membership.Id, result.Membership.Revision, AccessChangeKind.Role, WorkspaceRole.Viewer), token);
        Assert.Equal(WorkspaceRole.Viewer, (await store.ChangeInvitationAsync(create, token)).Membership!.Role);
        Assert.Empty(await store.ListInvitationsAsync(token));
    }

    [Fact]
    public async Task Workspace_invitation_requires_exact_verified_recipient_and_never_recreates_removed_membership()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        WorkspaceInvitationResult created = await store.ChangeInvitationAsync(new WorkspaceInvitationCreate(Guid.NewGuid(), "https://issuer.test",
            "operator", new("https://issuer.test", "invited", "Synthetic tester")), token);
        Assert.Equal("applied", created.Status);
        WorkspaceInvitation offer = created.Offer!;
        Assert.Equal(TimeSpan.FromHours(24), offer.ExpiresAt - offer.CreatedAt);
        Assert.Equal(WorkspaceRole.Viewer, offer.Role);
        var accept = new WorkspaceInvitationAccept(Guid.NewGuid(), "https://issuer.test", "invited", offer.Id,
            offer.Revision, EmailVerified: true);
        Assert.Equal("notFound", (await store.ChangeInvitationAsync(accept with { OperationId = Guid.NewGuid(), ActorSubject = "wrong" }, token)).Status);
        Assert.Equal("verificationRequired", (await store.ChangeInvitationAsync(accept with { OperationId = Guid.NewGuid(), EmailVerified = false }, token)).Status);
        WorkspaceInvitationResult joined = await store.ChangeInvitationAsync(accept, token);
        Assert.Equal("applied", joined.Status);
        Assert.Equal(WorkspaceRole.Viewer, joined.Membership!.Role);
        Assert.Equal("applied", (await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator",
            joined.Membership.Id, joined.Membership.Revision, AccessChangeKind.Role, WorkspaceRole.Curator), token)).Status);
        Assert.Equal(WorkspaceRole.Curator, (await store.ChangeInvitationAsync(accept, token)).Membership!.Role);
        WorkspaceMembership current = (await store.GetAsync("https://issuer.test", "invited", token))!;
        await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", current.Id, current.Revision, AccessChangeKind.Remove), token);
        Assert.False((await store.ChangeInvitationAsync(accept, token)).Membership!.IsActive);
        Assert.False((await store.ChangeInvitationAsync(accept with { OperationId = Guid.NewGuid() }, token)).Membership!.IsActive);
        Assert.False((await store.GetAsync("https://issuer.test", "invited", token))!.IsActive);
    }
}
