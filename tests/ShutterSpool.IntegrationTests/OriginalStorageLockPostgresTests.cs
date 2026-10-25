using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed class OriginalStorageLockPostgresTests
{
    [Fact]
    public async Task Returned_original_stream_holds_shared_session_lock_until_disposal()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);
        (Guid assetId, string spoolKey, FileSystemSpool spool) = await AddSpooledAssetAsync(database, fixture.SpoolRoot, token);
        string connectionString = LockConnectionString(database);
        var reader = new PostgresCommittedOriginalReader(spool, new PooledDbContextFactory<ShutterSpoolDbContext>(fixture.Options),
            new PostgresOriginalAssetLock(connectionString));
        var removalLocks = new PostgresOriginalAssetLock(connectionString);

        await using Stream original = await reader.OpenReadAsync(spoolKey, token);
        byte[] sample = new byte[1];
        Assert.Equal(1, await original.ReadAsync(sample, token));
        Assert.Equal((byte)1, sample[0]);
        using var queuedRemoval = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<IOriginalAssetLockLease> removalTask = removalLocks.AcquireExclusiveAsync(assetId, spoolKey, queuedRemoval.Token);
        await WaitForBlockedAdvisoryLockAsync(database, token);
        Assert.False(removalTask.IsCompleted);

        await original.DisposeAsync();
        await using IOriginalAssetLockLease removal = await removalTask.WaitAsync(token);
    }

    [Fact]
    public async Task Shared_original_use_blocks_independent_removal_until_stream_and_outer_preview_use_release()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);
        (Guid assetId, string spoolKey, FileSystemSpool spool) = await AddSpooledAssetAsync(database, fixture.SpoolRoot, token);
        string connectionString = LockConnectionString(database);
        var firstLocks = new PostgresOriginalAssetLock(connectionString);
        var secondLocks = new PostgresOriginalAssetLock(connectionString);
        var reader = new PostgresCommittedOriginalReader(spool, new PooledDbContextFactory<ShutterSpoolDbContext>(fixture.Options), firstLocks);

        await using IOriginalAssetLockLease previewUse = await firstLocks.AcquireSharedAsync(assetId, spoolKey, token);
        using var queuedRemoval = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<IOriginalAssetLockLease> removalTask = secondLocks.AcquireExclusiveAsync(assetId, spoolKey, queuedRemoval.Token);
        await WaitForBlockedAdvisoryLockAsync(database, token);
        Assert.False(removalTask.IsCompleted);

        await using Stream source = await reader.OpenReadAsync(assetId, spoolKey, previewUse, token);
        byte[] sample = new byte[1];
        Assert.Equal(1, await source.ReadAsync(sample, token));
        Assert.Equal((byte)1, sample[0]);
        await source.DisposeAsync();
        Assert.False(removalTask.IsCompleted); // The outer lease fences cache publication too.
        await previewUse.DisposeAsync();
        await using IOriginalAssetLockLease removal = await removalTask.WaitAsync(token);
    }

    [Fact]
    public async Task Shared_acquisition_cancels_while_removal_owns_key_and_removal_row_rejects_new_reads()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);
        (Guid assetId, string spoolKey, FileSystemSpool spool) = await AddSpooledAssetAsync(database, fixture.SpoolRoot, token);
        string connectionString = LockConnectionString(database);
        var locks = new PostgresOriginalAssetLock(connectionString);
        var reader = new PostgresCommittedOriginalReader(spool, new PooledDbContextFactory<ShutterSpoolDbContext>(fixture.Options),
            new PostgresOriginalAssetLock(connectionString));

        await using (IOriginalAssetLockLease exclusive = await locks.AcquireExclusiveAsync(assetId, spoolKey, token))
        using (var shortWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.OpenReadAsync(spoolKey, shortWait.Token));
        }

        database.AssetRemovalOperations.Add(new AssetRemovalOperationEntity
        {
            AssetId = assetId,
            CaptureId = await database.CaptureAssets.Where(member => member.AssetId == assetId)
                .Select(member => member.CaptureId).SingleAsync(token),
            OperationId = Guid.NewGuid(),
            SpoolKey = spoolKey,
            StorageMarker = Guid.NewGuid().ToString("N"),
            ExpectedSize = 4,
            Scope = "original",
            Reason = "window-expired",
            PolicyRevision = 1,
            State = "Authorized",
            DueAt = DateTimeOffset.UtcNow,
            AuthorizedAt = DateTimeOffset.UtcNow,
        });
        await database.SaveChangesAsync(token);
        await Assert.ThrowsAsync<FileNotFoundException>(() => reader.OpenReadAsync(spoolKey, token));
    }

    private static async Task<(Guid AssetId, string SpoolKey, FileSystemSpool Spool)> AddSpooledAssetAsync(
        ShutterSpoolDbContext database, string spoolRoot, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(spoolRoot);
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = spoolRoot, ReservedBytes = 0 });
        Guid sourceId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        SpoolWrite write = await spool.WriteAsync(sourceId, assetId, new MemoryStream([1, 2, 3, 4]), null, cancellationToken);
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic" });
        database.Assets.Add(new AssetEntity
        {
            Id = assetId,
            SourceId = sourceId,
            OriginalName = "synthetic.jpg",
            SpoolKey = write.FinalKey,
            Size = write.Size,
            Sha256 = write.Sha256,
            IngestedAt = DateTimeOffset.UtcNow,
            RecoveryCorrelation = Guid.NewGuid(),
            State = "Spooled",
        });
        Guid captureId = Guid.NewGuid();
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "synthetic",
            FirstAcceptedAt = DateTimeOffset.UtcNow, LastPrimaryIngestedAt = DateTimeOffset.UtcNow,
        });
        database.CaptureAssets.Add(new CaptureAssetEntity
        {
            AssetId = assetId, CaptureId = captureId, IsPrimary = true, Role = "JPEG", GroupingReason = "synthetic",
        });
        await database.SaveChangesAsync(cancellationToken);
        return (assetId, write.FinalKey, spool);
    }

    private static string LockConnectionString(ShutterSpoolDbContext database)
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection))
            throw new InvalidOperationException("The PostgreSQL lock tests require their controlled test connection.");
        return new NpgsqlConnectionStringBuilder(adminConnection)
        {
            Database = database.Database.GetDbConnection().Database,
        }.ConnectionString;
    }

    private static async Task WaitForBlockedAdvisoryLockAsync(ShutterSpoolDbContext database, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool waiting = await database.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM pg_locks
                    WHERE locktype = 'advisory' AND NOT granted
                      AND database = (SELECT oid FROM pg_database WHERE datname = current_database())) AS "Value"
                """).SingleAsync(cancellationToken);
            if (waiting) return;
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException("The second PostgreSQL service instance did not queue for the advisory lock.");
    }
}
