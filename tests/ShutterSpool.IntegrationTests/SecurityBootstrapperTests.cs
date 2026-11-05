using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed class AccessGrantTests
{
    [Fact]
    public async Task Only_current_exact_issuer_subject_grants_authorize_access()
    {
        var databaseOptions = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var database = new ShutterSpoolDbContext(databaseOptions);
        var grants = new EfAccessGrantStore(database);
        database.AuthorizationGrants.AddRange(
            new AuthorizationGrant { Issuer = "https://issuer.test", Subject = "first", IsActive = false },
            new AuthorizationGrant { Issuer = "https://issuer.test", Subject = "current", IsActive = true });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.False(await grants.IsActiveAsync("https://issuer.test", "first", TestContext.Current.CancellationToken));
        Assert.True(await grants.IsActiveAsync("https://issuer.test", "current", TestContext.Current.CancellationToken));
        Assert.False(await grants.IsActiveAsync("https://other.test", "current", TestContext.Current.CancellationToken));
    }
}
