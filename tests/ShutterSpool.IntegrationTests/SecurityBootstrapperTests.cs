using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed class SecurityBootstrapperTests
{
    [Fact]
    public async Task Applies_the_configured_bootstrap_grant_and_revokes_the_replaced_one()
    {
        var databaseOptions = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var database = new ShutterSpoolDbContext(databaseOptions);
        var grants = new EfAccessGrantStore(database);
        await grants.BootstrapAsync("https://issuer.test", "first", TestContext.Current.CancellationToken);
        await grants.BootstrapAsync("https://issuer.test", "replacement", TestContext.Current.CancellationToken);

        Assert.False(database.AuthorizationGrants.Single(grant => grant.Subject == "first").IsActive);
        Assert.True(database.AuthorizationGrants.Single(grant => grant.Subject == "replacement").IsActive);
    }
}
