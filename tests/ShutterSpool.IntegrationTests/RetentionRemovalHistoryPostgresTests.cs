using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed class RetentionRemovalHistoryPostgresTests
{
    [Fact]
    public async Task Removal_failure_retry_and_completion_append_operation_linked_revisions()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid sourceId = Guid.NewGuid(), captureId = Guid.NewGuid(), assetId = Guid.NewGuid(), operationId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-removal-history" });
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "synthetic-removal-history",
            FirstAcceptedAt = now.AddDays(-10), LastPrimaryIngestedAt = now.AddDays(-10),
            RetentionDisposition = "Purging", RetentionPaused = true,
        });
        database.Assets.Add(new AssetEntity
        {
            Id = assetId, SourceId = sourceId, OriginalName = "synthetic.jpg", SpoolKey = $"synthetic/{assetId:N}",
            Size = 4, Sha256 = new string('a', 64), IngestedAt = now.AddDays(-10),
            RecoveryCorrelation = Guid.NewGuid(), State = "Spooled",
        });
        database.CaptureAssets.Add(new CaptureAssetEntity
        {
            AssetId = assetId, CaptureId = captureId, Role = "JPEG", IsPrimary = true, GroupingReason = "synthetic",
        });
        database.AssetRemovalOperations.Add(new AssetRemovalOperationEntity
        {
            AssetId = assetId, CaptureId = captureId, OperationId = operationId, SpoolKey = $"synthetic/{assetId:N}",
            ExpectedSize = 4, StorageMarker = "synthetic-marker", Scope = "original", Reason = "manual",
            PolicyRevision = 1, State = "Authorized", DueAt = now, AuthorizedAt = now,
        });
        await database.SaveChangesAsync(token);

        var store = new EfRetentionRemovalStore(database,
            new EfCaptureRetentionStore(database, TimeProvider.System), TimeProvider.System);
        await store.FailAssetAsync(operationId, "synthetic_failure", now, 0, token);
        await store.FailAssetAsync(operationId, "synthetic_retry_failure", now.AddMinutes(1), 1, token);
        await store.CompleteAssetAsync(operationId, 3, now.AddMinutes(2), token);

        RetentionHistoryEntity[] history = await database.RetentionHistory.AsNoTracking()
            .Where(item => item.EpisodeId == operationId && item.Kind == "removal")
            .OrderBy(item => item.Revision).ToArrayAsync(token);
        Assert.Equal(new long[] { 1, 2, 3 }, history.Select(item => item.Revision));
        Assert.Equal(new[] { "failed", "failed", "completed" }, history.Select(item => item.State));
        Assert.Equal(new long[] { 0, 1, 3 }, history.Select(item => item.PurgedBytes));
        Assert.Equal(4, (await database.AssetRemovalOperations.SingleAsync(item => item.OperationId == operationId, token)).RemovedBytes);
    }
}
