using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed class PostgresRetentionControllerLockTests
{
    [Fact]
    public async Task Independent_controllers_cannot_hold_the_same_retention_pass_lease()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        string connectionString = database.Database.GetDbConnection().ConnectionString;
        await using var firstSource = NpgsqlDataSource.Create(connectionString);
        await using var secondSource = NpgsqlDataSource.Create(connectionString);
        var first = new PostgresRetentionControllerLock(firstSource);
        var second = new PostgresRetentionControllerLock(secondSource);

        await using (IAsyncDisposable firstLease = await first.TryAcquireAsync(token)
            ?? throw new InvalidOperationException("The first controller should acquire the pass lease."))
        {
            Assert.Null(await second.TryAcquireAsync(token));
        }

        await using IAsyncDisposable secondLease = await second.TryAcquireAsync(token)
            ?? throw new InvalidOperationException("The second controller should acquire after release.");
    }
}
