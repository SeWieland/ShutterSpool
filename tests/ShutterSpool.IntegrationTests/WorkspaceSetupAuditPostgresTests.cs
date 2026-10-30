using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class WorkspaceTenancyPostgresTests
{
    [Fact]
    public async Task Initial_owner_setup_records_operator_principal_and_result_atomically_and_completed_retry_never_regrants()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.ProvisionAsync();
        await using var database = fixture.MigrationDatabase(0);
        AuthorizationGrant owner = await database.AuthorizationGrants.SingleAsync(token);
        WorkspaceAccessOperationEntity audit = await database.Set<WorkspaceAccessOperationEntity>().SingleAsync(token);
        Guid provisionOperation = await database.Database.SqlQuery<Guid>($"SELECT operation_id AS \"Value\" FROM workspace_identity").SingleAsync(token);
        Assert.Equal(provisionOperation, audit.Id);
        Assert.Equal("operator-setup", audit.Kind);
        Assert.Equal("host-operator", audit.ActorIssuer);
        Assert.Equal("synthetic-setup-operator", audit.ActorSubject);
        Assert.Equal(owner.Id, audit.TargetId);
        using JsonDocument request = JsonDocument.Parse(audit.Request);
        Assert.Equal(owner.Issuer, request.RootElement.GetProperty("TargetIssuer").GetString());
        Assert.Equal(owner.Subject, request.RootElement.GetProperty("TargetSubject").GetString());
        Assert.Equal("synthetic-setup-operator", request.RootElement.GetProperty("OperatorIdentity").GetString());
        using JsonDocument before = JsonDocument.Parse(audit.BeforeState!);
        using JsonDocument after = JsonDocument.Parse(audit.AfterState!);
        using JsonDocument result = JsonDocument.Parse(audit.Result);
        Assert.Equal(JsonValueKind.Null, before.RootElement.GetProperty("Target").ValueKind);
        Assert.Equal(owner.Id, after.RootElement.GetProperty("Target").GetProperty("Id").GetGuid());
        Assert.True(after.RootElement.GetProperty("Target").GetProperty("IsActive").GetBoolean());
        Assert.Equal("applied", result.RootElement.GetProperty("Status").GetString());
        Assert.Equal(owner.Id, result.RootElement.GetProperty("Membership").GetProperty("Id").GetGuid());
        Assert.Equal(1, (await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token)).AuthorityRevision);
        Assert.True((await database.SpoolQuotaSettings.SingleAsync(token)).IsActive);
        Assert.True(await database.Database.SqlQuery<bool>($"SELECT bootstrap_complete AS \"Value\" FROM workspace_identity").SingleAsync(token));
        owner.IsActive = false;
        await database.SaveChangesAsync(token);
        fixture.Configuration[$"ShutterSpool:Workspaces:Definitions:{fixture.WorkspaceId(0):D}:SetupOperatorIdentity"] = null;
        await fixture.ProvisionWorkspaceAsync(0);
        database.ChangeTracker.Clear();
        Assert.False((await database.AuthorizationGrants.SingleAsync(token)).IsActive);
        Assert.Equal(audit.Request, (await database.Set<WorkspaceAccessOperationEntity>().SingleAsync(token)).Request);
        Assert.Equal(1, (await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token)).AuthorityRevision);
    }

    [Fact]
    public async Task Initial_owner_setup_requires_bounded_operator_identity_and_audit_failure_rolls_back_before_safe_retry()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await WorkspaceOperator.RunAsync(["--workspace-control-init"], fixture.Configuration, token);
        string key = $"ShutterSpool:Workspaces:Definitions:{fixture.WorkspaceId(0):D}:SetupOperatorIdentity";
        foreach (string? invalid in new[] { null, "", "  ", new string('x', 513) })
        {
            fixture.Configuration[key] = invalid;
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.ProvisionWorkspaceAsync(0));
        }
        await using var database = fixture.MigrationDatabase(0);
        await AssertBootstrapAbsentAsync(database, token);
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE workspace_access_operations ADD CONSTRAINT reject_setup_audit CHECK (false)", token);
        fixture.Configuration[key] = "synthetic-setup-operator";
        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.ProvisionWorkspaceAsync(0));
        await AssertBootstrapAbsentAsync(database, token);
        Assert.Empty(await fixture.RegistryAsync());
        await database.Database.ExecuteSqlRawAsync("ALTER TABLE workspace_access_operations DROP CONSTRAINT reject_setup_audit", token);
        await fixture.ProvisionWorkspaceAsync(0);
        await fixture.ProvisionWorkspaceAsync(0);
        database.ChangeTracker.Clear();
        Assert.Single(await database.AuthorizationGrants.ToArrayAsync(token));
        Assert.Single(await database.Set<WorkspaceAccessOperationEntity>().ToArrayAsync(token));
        Assert.True(await database.Database.SqlQuery<bool>($"SELECT bootstrap_complete AS \"Value\" FROM workspace_identity").SingleAsync(token));
        Assert.Single(await fixture.RegistryAsync());
    }

    [Fact]
    public async Task Completed_legacy_setup_without_operator_evidence_remains_usable_without_fabricated_audit()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.ProvisionAsync();
        await using var database = fixture.MigrationDatabase(0);
        // Represent a completed workspace created before setup audit existed.
        await database.Set<WorkspaceAccessOperationEntity>().ExecuteDeleteAsync(token);
        await database.Set<WorkspaceViewerPolicyEntity>().ExecuteUpdateAsync(update => update.SetProperty(item => item.AuthorityRevision, 0), token);
        fixture.Configuration[$"ShutterSpool:Workspaces:Definitions:{fixture.WorkspaceId(0):D}:SetupOperatorIdentity"] = null;
        await fixture.ProvisionWorkspaceAsync(0);
        fixture.Configuration["ShutterSpool:Workspaces:WritersStopped"] = "true";
        await WorkspaceOperator.RunAsync(["--workspace-upgrade"], fixture.Configuration, token);
        Assert.Empty(await database.Set<WorkspaceAccessOperationEntity>().ToArrayAsync(token));
        Assert.True((await database.AuthorizationGrants.SingleAsync(token)).IsActive);
        Assert.Equal(0, (await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token)).AuthorityRevision);
    }

    private static async Task AssertBootstrapAbsentAsync(ShutterSpoolDbContext database, CancellationToken token)
    {
        Assert.Empty(await database.AuthorizationGrants.AsNoTracking().ToArrayAsync(token));
        Assert.Empty(await database.Set<WorkspaceAccessOperationEntity>().AsNoTracking().ToArrayAsync(token));
        Assert.False((await database.SpoolQuotaSettings.AsNoTracking().SingleAsync(token)).IsActive);
        Assert.Equal(0, (await database.Set<WorkspaceViewerPolicyEntity>().AsNoTracking().SingleAsync(token)).AuthorityRevision);
        Assert.False(await database.Database.SqlQuery<bool>($"SELECT to_regclass('public.workspace_identity') IS NOT NULL AS \"Value\"").SingleAsync(token));
    }
}
