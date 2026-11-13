using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Security;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Display_name_updates_are_bounded_exact_active_identity_labels_without_permission_or_audit_changes()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAccessGrantStore(database);
        WorkspaceMembership before = (await store.GetAsync("https://issuer.test", "operator", token))!;
        WorkspaceViewerPolicy policy = await store.GetViewerPolicyAsync(token);
        database.AuthorizationGrants.AddRange(
            new AuthorizationGrant { Issuer = "https://other.test", Subject = "operator", Role = WorkspaceRole.Viewer, IsActive = true },
            new AuthorizationGrant { Issuer = before.Issuer, Subject = "inactive", Role = WorkspaceRole.Curator, IsActive = false });
        await database.SaveChangesAsync(token);
        int audits = await database.Set<WorkspaceAccessOperationEntity>().CountAsync(token);

        string name = new('n', 256);
        await store.UpdateDisplayNameAsync(before.Issuer, before.Subject, name, token);
        Assert.Equal(before with { DisplayName = name }, await store.GetAsync(before.Issuer, before.Subject, token));
        Assert.Equal(name, (await store.ListAsync(token)).Single(item => item.Id == before.Id).DisplayName);
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateDisplayNameAsync(before.Issuer, before.Subject, new('n', 257), token));
        Assert.Equal(name, (await store.GetAsync(before.Issuer, before.Subject, token))!.DisplayName);

        await store.UpdateDisplayNameAsync(before.Issuer, "inactive", "ignored", token);
        await store.UpdateDisplayNameAsync("https://unknown.test", before.Subject, "ignored", token);
        Assert.Null((await store.GetAsync("https://other.test", before.Subject, token))!.DisplayName);
        Assert.Equal(name, (await store.GetAsync(before.Issuer, before.Subject, token))!.DisplayName);
        WorkspaceMembership inactive = (await store.GetAsync(before.Issuer, "inactive", token))!;
        Assert.Null(inactive.DisplayName);
        Assert.False(inactive.IsActive);
        Assert.Equal(WorkspaceRole.Curator, inactive.Role);
        Assert.Equal(0, inactive.Revision);
        await store.UpdateDisplayNameAsync(before.Issuer, before.Subject, null, token);
        Assert.Equal(before, await store.GetAsync(before.Issuer, before.Subject, token));
        Assert.Equal(policy, await store.GetViewerPolicyAsync(token));
        Assert.Equal(audits, await database.Set<WorkspaceAccessOperationEntity>().CountAsync(token));
    }
}
