using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Retention;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed class RetentionRemovalHistoryPostgresTests
{
    [Fact]
    public async Task Pressure_history_records_the_active_blocker_and_recovers_at_the_target()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);
        var store = new EfRetentionRemovalStore(database,
            new EfCaptureRetentionStore(database, TimeProvider.System), TimeProvider.System);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var snapshot = new SpoolQuotaSnapshot(
            new(IsActive: true, QuotaBytes: 100, HighWatermarkBytes: 80, RecoveryTargetBytes: 50),
            now, IsKnown: true, UsedBytes: 75, ReservedBytes: 5, ActiveBytes: 75, TrashBytes: 0,
            PartialBytes: 0, QuarantineBytes: 0, DiskPreviewBytes: 0, DriveTotalBytes: null,
            DriveAvailableBytes: null, FilesystemReserveBytes: null, RecoveryDeficitBytes: 30,
            AdmissionBlockedReason: null);
        var pass = new RetentionRemovalPassResult(2, 1, 1, 3);
        var atTarget = snapshot with { UsedBytes = 45, ActiveBytes = 45, RecoveryDeficitBytes = 0 };

        await store.RecordCapacityObservationAsync(snapshot, pass, true, now, token);
        await store.RecordCapacityObservationAsync(atTarget with { AdmissionBlockedReason = "quota" },
            pass, false, now.AddMinutes(1), token);
        await store.RecordCapacityObservationAsync(snapshot, pass, false, now.AddMinutes(2), token);
        await store.RecordCapacityObservationAsync(snapshot, pass, true, now.AddMinutes(3), token);
        await store.RecordCapacityObservationAsync(atTarget, pass, true, now.AddMinutes(4), token);

        RetentionHistoryEntity[] history = await database.RetentionHistory.AsNoTracking()
            .Where(item => item.Kind == "pressure").OrderBy(item => item.Revision).ToArrayAsync(token);
        Assert.Equal(new[] { "Planning", "AdmissionBlocked", "AutomaticRemovalDisabled", "ReclamationFailed", "Recovered" },
            history.Select(item => item.State));
        Assert.Equal(new[] { "quota" }, history[1].Blockers);
        Assert.Equal((45L, 5L, 0L, 3L),
            (history[1].UsedBytes, history[1].ReservedBytes, history[1].PlannedReclamationBytes, history[1].PurgedBytes));
        Assert.False(await store.IsPressureEpisodeActiveAsync(token));
    }

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
