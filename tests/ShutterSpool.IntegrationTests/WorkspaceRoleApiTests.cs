using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShutterSpool.Application.Security;
using ShutterSpool.Server.Security;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Theory]
    [InlineData(WorkspaceRole.Owner, true, true)]
    [InlineData(WorkspaceRole.Administrator, true, true)]
    [InlineData(WorkspaceRole.Curator, true, false)]
    [InlineData(WorkspaceRole.Viewer, false, false)]
    public void Workspace_endpoint_matrix_denies_unmapped_and_limits_destructive_and_operator_actions(
        WorkspaceRole role, bool curate, bool administer)
    {
        Assert.True(WorkspaceEndpointPermissions.Allows(role, "GET", "/management/captures/{id:guid}", true));
        Assert.Equal(curate, WorkspaceEndpointPermissions.Allows(role, "POST", "/management/captures/{id:guid}/trash", true));
        Assert.Equal(curate, WorkspaceEndpointPermissions.Allows(role, "POST", "/management/deliveries/{id:guid}/retry", true));
        Assert.Equal(administer, WorkspaceEndpointPermissions.Allows(role, "POST", "/management/trash/{id:guid}/purge", true));
        Assert.Equal(administer, WorkspaceEndpointPermissions.Allows(role, "GET", "/management/sources/{id:guid}", true));
        Assert.Equal(administer, WorkspaceEndpointPermissions.Allows(role, "POST", "/management/captures/{id:guid}/resume", true));
        Assert.False(WorkspaceEndpointPermissions.Allows(role, "GET", "/management/new-private-resource", true));
        Assert.False(WorkspaceEndpointPermissions.Allows(role, "POST", "/management/retention/erasures/suppressions/exports", true));
    }

    [Fact]
    public async Task Workspace_access_reports_local_owner_and_rejects_last_owner_removal()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage access = await fixture.Client.GetAsync("/management/access", token);
        Assert.Equal(HttpStatusCode.OK, access.StatusCode);
        JsonElement current = await access.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("Owner", current.GetProperty("role").GetString());
        Guid id = current.GetProperty("membershipId").GetGuid();
        using HttpResponseMessage removal = await fixture.Client.PostAsJsonAsync($"/management/members/{id}/remove",
            new { operationId = Guid.NewGuid(), expectedRevision = current.GetProperty("revision").GetInt64() }, token);
        Assert.Equal(HttpStatusCode.Conflict, removal.StatusCode);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.True((await database.AuthorizationGrants.SingleAsync(token)).IsActive);
    }

    [Fact]
    public async Task Viewer_scope_precedes_filters_tags_detail_collections_and_preview_even_with_admin_token_claim()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] captures = await SeedMetadataCapturesAsync(fixture, 4);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity[] rows = await database.Captures.OrderBy(item => item.FirstAcceptedAt).ToArrayAsync(token);
            Guid privateDefault = Guid.NewGuid();
            database.Collections.Add(new() { Id = privateDefault, Name = "Private incoming default", Path = "Private incoming default", Identity = "PRIVATE INCOMING DEFAULT" });
            (await database.Sources.SingleAsync(token)).CollectionId = privateDefault;
            rows[0].ReleasedAt = DateTimeOffset.UtcNow; rows[0].Tags = ["scope-visible"];
            rows[1].Tags = ["scope-unreleased-secret"];
            rows[2].ReleasedAt = DateTimeOffset.UtcNow; rows[2].Rating = -1; rows[2].Tags = ["scope-rejected-secret"];
            rows[3].ReleasedAt = DateTimeOffset.UtcNow; rows[3].RetentionDisposition = "Trashed"; rows[3].Tags = ["scope-trash-secret"];
            (await database.AuthorizationGrants.SingleAsync(token)).Role = WorkspaceRole.Viewer;
            await database.SaveChangesAsync(token);
        }
        JsonElement inbox = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/captures", token);
        Assert.Equal(captures[0], Assert.Single(inbox.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        JsonElement visibleSource = inbox.GetProperty("items")[0].GetProperty("source");
        Assert.Equal(JsonValueKind.Null, visibleSource.GetProperty("collectionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, visibleSource.GetProperty("collectionName").ValueKind);
        using HttpResponseMessage visibleDetailResponse = await fixture.Client.GetAsync($"/management/captures/{captures[0]}", token);
        JsonElement visibleDetail = await visibleDetailResponse.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(JsonValueKind.Null, visibleDetail.GetProperty("source").GetProperty("collectionName").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, visibleDetail.GetProperty("currentCollection").ValueKind);
        JsonElement tags = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/tags?q=scope", token);
        Assert.Equal("scope-visible", Assert.Single(tags.GetProperty("tags").EnumerateArray()).GetString());
        JsonElement collections = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/collections", token);
        Assert.Equal(1, Assert.Single(collections.GetProperty("items").EnumerateArray()).GetProperty("captureReferences").GetInt32());
        foreach (Guid hidden in captures.Skip(1))
        {
            foreach (string path in new[] { $"/management/captures/{hidden}", $"/management/captures/{hidden}/curation", $"/media/previews/{hidden}", $"/management/captures/{hidden}/delivery-summary" })
            {
                using HttpResponseMessage response = await fixture.Client.GetAsync(path, token);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        using HttpResponseMessage forbidden = await fixture.Client.GetAsync("/management/sources", token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        JsonElement denial = await forbidden.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("permission-denied", denial.GetProperty("errorCode").GetString());
        using HttpResponseMessage trash = await fixture.Client.GetAsync("/management/trash", token);
        Assert.Equal(HttpStatusCode.Forbidden, trash.StatusCode);
        JsonElement filtered = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/captures?released=false", token);
        Assert.Empty(filtered.GetProperty("items").EnumerateArray());

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            (await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token)).IncludeUnreleased = true;
            await database.SaveChangesAsync(token);
        }
        JsonElement wider = await fixture.Client.GetFromJsonAsync<JsonElement>("/management/captures", token);
        Assert.Equal(captures.Take(3).Order(), wider.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).Order());
        using HttpResponseMessage stillHiddenTrash = await fixture.Client.GetAsync($"/media/previews/{captures[3]}", token);
        Assert.Equal(HttpStatusCode.NotFound, stillHiddenTrash.StatusCode);
    }

    [Fact]
    public async Task Serialized_owner_changes_preserve_an_owner_and_replays_are_bound_to_actor_and_target()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid first;
        Guid second = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            first = (await database.AuthorizationGrants.SingleAsync(token)).Id;
            database.AuthorizationGrants.Add(new() { Id = second, Issuer = "https://issuer.test", Subject = "second", Role = WorkspaceRole.Owner, IsActive = true });
            await database.SaveChangesAsync(token);
        }
        AccessChange left = new(Guid.NewGuid(), "https://issuer.test", "operator", Guid.Empty, 0, "leave");
        AccessChange right = new(Guid.NewGuid(), "https://issuer.test", "second", Guid.Empty, 0, "leave");
        await using var one = new ShutterSpoolDbContext(fixture.Database.Options);
        await using var two = new ShutterSpoolDbContext(fixture.Database.Options);
        AccessChangeResult[] changes = await Task.WhenAll(new EfAccessGrantStore(one).ChangeAsync(left, token), new EfAccessGrantStore(two).ChangeAsync(right, token));
        Assert.Single(changes, item => item.Status == "applied");
        Assert.Single(changes, item => item.Status == "lastOwner");
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Single(await verify.AuthorizationGrants.Where(item => item.IsActive && item.Role == WorkspaceRole.Owner).ToArrayAsync(token));
        AccessChange applied = changes[0].Status == "applied" ? left : right;
        var store = new EfAccessGrantStore(verify);
        Assert.Equal("applied", (await store.ChangeAsync(applied, token)).Status);
        Assert.Equal("conflict", (await store.ChangeAsync(applied with { ActorSubject = "different" }, token)).Status);
        Assert.Equal(2, await verify.Set<WorkspaceAccessOperationEntity>().CountAsync(token));
        WorkspaceAccessOperationEntity audit = await verify.Set<WorkspaceAccessOperationEntity>().SingleAsync(item => item.Id == applied.OperationId, token);
        using JsonDocument before = JsonDocument.Parse(audit.BeforeState!);
        using JsonDocument after = JsonDocument.Parse(audit.AfterState!);
        Assert.True(before.RootElement.GetProperty("Target").GetProperty("IsActive").GetBoolean());
        Assert.False(after.RootElement.GetProperty("Target").GetProperty("IsActive").GetBoolean());
    }

    [Fact]
    public async Task Administrator_cannot_change_owner_or_race_stale_target_into_removal_and_promotion_requires_confirmation()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid owner;
        Guid admin = Guid.NewGuid();
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        owner = (await database.AuthorizationGrants.SingleAsync(token)).Id;
        database.AuthorizationGrants.Add(new() { Id = admin, Issuer = "https://issuer.test", Subject = "admin", Role = WorkspaceRole.Administrator, IsActive = true });
        await database.SaveChangesAsync(token);
        var store = new EfAccessGrantStore(database);
        Assert.Equal("denied", (await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "admin", owner, 0, "remove"), token)).Status);
        Assert.Equal("denied", (await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "admin", admin, 0, "promote-owner", Confirmed: true), token)).Status);
        Assert.Equal("denied", (await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", admin, 0, "promote-owner"), token)).Status);
        Assert.Equal("applied", (await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", admin, 0, "promote-owner", Confirmed: true), token)).Status);
        Assert.Equal("conflict", (await store.ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", admin, 0, "remove"), token)).Status);
        Assert.True((await store.GetAsync("https://issuer.test", "admin", token))!.IsActive);
    }

    [Fact]
    public async Task Operator_recovery_swaps_identity_atomically_records_pending_notice_and_rejects_stale_verification_or_changed_replay()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        WorkspaceAccessInspection before = await store.InspectAsync(token);
        WorkspaceMembership original = Assert.Single(before.Memberships);
        var recovery = new WorkspaceOwnerRecovery(before.AuthorityRevision, original.Id, original.Revision,
            "https://issuer.test", "verified-replacement", -1, "protected:synthetic-contact-check", "established-contact",
            "synthetic-host-operator", "Replace independently verified unavailable representative");
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecoverOwnerAsync(Guid.NewGuid(), recovery with { VerificationReference = "" }, token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecoverOwnerAsync(Guid.NewGuid(), recovery with { OperatorIdentity = "" }, token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.RecoverOwnerAsync(Guid.NewGuid(), recovery with { Reason = "" }, token));
        Assert.Equal("conflict", (await store.RecoverOwnerAsync(Guid.NewGuid(), recovery with { ExpectedAuthorityRevision = 9 }, token)).Status);
        Guid operation = Guid.NewGuid();
        AccessChangeResult result = await store.RecoverOwnerAsync(operation, recovery, token);
        Assert.Equal("applied", result.Status);
        Assert.False((await store.GetAsync(original.Issuer, original.Subject, token))!.IsActive);
        WorkspaceMembership replacement = (await store.GetAsync(recovery.TargetIssuer, recovery.TargetSubject, token))!;
        Assert.True(replacement.IsActive);
        Assert.Equal(WorkspaceRole.Owner, replacement.Role);
        Assert.Equal("pending", (await database.Set<WorkspaceAccessOperationEntity>().SingleAsync(item => item.Id == operation, token)).NotificationStatus);
        WorkspaceAccessOperationEntity audit = await database.Set<WorkspaceAccessOperationEntity>().SingleAsync(item => item.Id == operation, token);
        Assert.Equal("synthetic-host-operator", audit.ActorSubject);
        using JsonDocument attestation = JsonDocument.Parse(audit.Request);
        Assert.Equal(recovery.Reason, attestation.RootElement.GetProperty("Reason").GetString());
        Assert.Equal(result, await store.RecoverOwnerAsync(operation, recovery, token));
        Assert.Equal("conflict", (await store.RecoverOwnerAsync(operation, recovery with { TargetSubject = "different" }, token)).Status);
        await store.RecordRecoveryNotificationAsync(operation, "failed", token);
        Assert.True((await store.GetAsync(recovery.TargetIssuer, recovery.TargetSubject, token))!.IsActive);
        Assert.Equal("failed", (await database.Set<WorkspaceAccessOperationEntity>().SingleAsync(item => item.Id == operation, token)).NotificationStatus);
        WorkspaceAccessInspection current = await store.InspectAsync(token);
        var same = recovery with { ExpectedAuthorityRevision = current.AuthorityRevision, RepresentativeMembershipId = replacement.Id,
            ExpectedRepresentativeRevision = replacement.Revision, ExpectedTargetRevision = replacement.Revision };
        Assert.Equal("applied", (await store.RecoverOwnerAsync(Guid.NewGuid(), same, token)).Status);
        Assert.True((await store.GetAsync(recovery.TargetIssuer, recovery.TargetSubject, token))!.IsActive);
    }

    [Fact]
    public async Task Access_changes_roll_back_when_durable_audit_is_unavailable()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        WorkspaceMembership actor = (await store.GetAsync("https://issuer.test", "operator", token))!;
        Guid peer = Guid.NewGuid();
        database.AuthorizationGrants.Add(new() { Id = peer, Issuer = actor.Issuer, Subject = "peer", IsActive = true, Role = WorkspaceRole.Curator });
        await database.SaveChangesAsync(token);
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE workspace_access_operations ADD CONSTRAINT reject_synthetic_audit CHECK (false)", token);
        await Assert.ThrowsAsync<DbUpdateException>(() => store.ChangeAsync(new(Guid.NewGuid(), actor.Issuer, actor.Subject,
            peer, 0, "role", WorkspaceRole.Administrator), token));
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        AuthorizationGrant unchanged = await verify.AuthorizationGrants.SingleAsync(item => item.Id == peer, token);
        Assert.Equal(WorkspaceRole.Curator, unchanged.Role);
        Assert.Equal(0, unchanged.Revision);
        Assert.Equal(0, (await verify.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token)).AuthorityRevision);
    }

    [Fact]
    public async Task Admitted_bulk_item_commits_after_revocation_but_later_items_and_new_requests_fail_fresh_authorization()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        Guid[] captures = await SeedMetadataCapturesAsync(fixture, 2);
        Guid actor;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            actor = (await database.AuthorizationGrants.SingleAsync(token)).Id;
            database.AuthorizationGrants.Add(new() { Issuer = "https://issuer.test", Subject = "remaining-owner", IsActive = true, Role = WorkspaceRole.Owner });
            await database.SaveChangesAsync(token);
        }
        await using var gate = new ShutterSpoolDbContext(fixture.Database.Options);
        await using var transaction = await gate.Database.BeginTransactionAsync(token);
        await gate.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", token);
        Task<HttpResponseMessage> pending = fixture.Client.PostAsJsonAsync("/management/captures/batch/curation", new
        {
            items = captures.Select(id => new { id, expectedRevision = 0, rating = 3, label = (string?)null, tags = Array.Empty<string>() }).ToArray(),
        }, token);
        try
        {
            while (!await gate.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype='advisory' AND NOT granted AND database=(SELECT oid FROM pg_database WHERE datname=current_database())) AS \"Value\"").SingleAsync(token))
                await Task.Delay(20, token);
            await using var revoke = new ShutterSpoolDbContext(fixture.Database.Options);
            AccessChangeResult removed = await new EfAccessGrantStore(revoke).ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", actor, 0, "remove"), token);
            Assert.Equal("applied", removed.Status);
            Assert.False(pending.IsCompleted);
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); }
        using HttpResponseMessage response = await pending;
        response.EnsureSuccessStatusCode();
        JsonElement result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("committed", result.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal("forbidden", result.GetProperty("items")[1].GetProperty("status").GetString());
        using HttpResponseMessage later = await fixture.Client.GetAsync($"/management/captures/{captures[0]}", token);
        Assert.Equal(HttpStatusCode.Forbidden, later.StatusCode);
        JsonElement denial = await later.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal("membership-revoked", denial.GetProperty("errorCode").GetString());
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal(3, (await verify.Captures.SingleAsync(item => item.Id == captures[0], token)).Rating);
        Assert.Equal(0, (await verify.Captures.SingleAsync(item => item.Id == captures[1], token)).Rating);
    }

    [Fact]
    public async Task Curator_can_trash_and_restore_available_originals_but_cannot_purge_or_change_policy()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid capture = await SeedRetentionCaptureAsync(fixture, token);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            (await database.AuthorizationGrants.SingleAsync(token)).Role = WorkspaceRole.Curator;
            await database.SaveChangesAsync(token);
        }
        using HttpResponseMessage detail = await fixture.Client.GetAsync($"/management/captures/{capture}", token);
        JsonElement safeDetail = await detail.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(JsonValueKind.Null, safeDetail.GetProperty("source").GetProperty("collectionId").ValueKind);
        using HttpResponseMessage staleMove = await fixture.Client.PutAsJsonAsync($"/management/captures/{capture}/collection",
            new { expectedRevision = 99, collectionId = ShutterSpool.Domain.Collections.CollectionName.BuiltInId }, token);
        Assert.Equal(HttpStatusCode.Conflict, staleMove.StatusCode);
        JsonElement conflict = await staleMove.Content.ReadFromJsonAsync<JsonElement>(token);
        Assert.Equal(JsonValueKind.Null, conflict.GetProperty("current").GetProperty("source").GetProperty("collectionName").ValueKind);
        using HttpResponseMessage trashed = await fixture.Client.PostAsJsonAsync($"/management/captures/{capture}/trash", new { expectedRevision = 0 }, token);
        Assert.Equal(HttpStatusCode.OK, trashed.StatusCode);
        using HttpResponseMessage purged = await fixture.Client.PostAsJsonAsync($"/management/trash/{capture}/purge", new { expectedRevision = 1, confirm = true }, token);
        Assert.Equal(HttpStatusCode.Forbidden, purged.StatusCode);
        using HttpResponseMessage policy = await fixture.Client.PutAsJsonAsync("/management/viewer-policy", new { operationId = Guid.NewGuid(), expectedRevision = 0, includeUnreleased = true }, token);
        Assert.Equal(HttpStatusCode.Forbidden, policy.StatusCode);
        using HttpResponseMessage restored = await fixture.Client.PostAsJsonAsync($"/management/trash/{capture}/restore", new { expectedRevision = 1 }, token);
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal("Active", (await verify.Captures.SingleAsync(item => item.Id == capture, token)).RetentionDisposition);
        Assert.True(await verify.Assets.AnyAsync(item => item.State == "Spooled", token));
        Assert.False((await verify.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token)).IncludeUnreleased);
    }

    [Fact]
    public async Task Status_stream_closes_after_local_revocation_within_the_live_authorization_bound()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        CancellationToken token = deadline.Token;
        Guid actor;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            actor = (await database.AuthorizationGrants.SingleAsync(token)).Id;
            database.AuthorizationGrants.Add(new() { Issuer = "https://issuer.test", Subject = "remaining-owner", IsActive = true, Role = WorkspaceRole.Owner });
            await database.SaveChangesAsync(token);
        }
        using HttpResponseMessage stream = await fixture.Client.GetAsync("/live/status", HttpCompletionOption.ResponseHeadersRead, token);
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(token));
        Assert.Equal("event: status", await reader.ReadLineAsync(token));
        await using (var revoke = new ShutterSpoolDbContext(fixture.Database.Options))
            Assert.Equal("applied", (await new EfAccessGrantStore(revoke).ChangeAsync(new(Guid.NewGuid(), "https://issuer.test", "operator", actor, 0, "remove"), token)).Status);
        string remainder = await reader.ReadToEndAsync(token);
        Assert.DoesNotContain("event: status", remainder, StringComparison.Ordinal);
    }
}
