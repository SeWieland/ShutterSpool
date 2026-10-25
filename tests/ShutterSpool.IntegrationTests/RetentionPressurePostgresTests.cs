using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Pressure_selection_waits_for_all_dirty_captures_then_orders_global_tiers()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _ = await InitializeQuotaMarkerAsync(fixture, token);
        Guid[] captures = await SeedMetadataCapturesAsync(fixture, 55);
        await SeedPressureAssetsAsync(fixture, captures.Select((id, index) => index switch
        {
            < 11 => new PressureCaptureSeed(id, now.AddDays(-30), ConfirmedAt: now.AddHours(-2)),
            < 22 => new PressureCaptureSeed(id, now.AddDays(-8), ExplicitNoDelivery: true),
            < 33 => new PressureCaptureSeed(id, now.AddDays(-30), ConfirmedAt: now.AddHours(-2), Favorite: true),
            < 44 => new PressureCaptureSeed(id, now.AddDays(-8)),
            _ => new PressureCaptureSeed(id, now.AddDays(-30), Held: true),
        }).ToArray(), token);

        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfRetentionRemovalStore(database,
            new EfCaptureRetentionStore(database, TimeProvider.System), TimeProvider.System);

        RetentionSelectionPassResult first = await store.SelectEligibleActiveAsync(5, long.MaxValue,
            underPressure: true, now, token);
        Assert.Equal(50, first.Examined);
        Assert.Equal(0, first.Selected);

        RetentionSelectionPassResult next = await store.SelectEligibleActiveAsync(11, 11,
            underPressure: true, now, token);
        Assert.Equal(11, next.Selected);

        Guid[][] tierGroups =
        [
            [.. captures.Take(11)],
            [.. captures.Skip(11).Take(11)],
            [.. captures.Skip(22).Take(11)],
            [.. captures.Skip(33).Take(11)],
            [.. captures.Skip(44).Take(11)],
        ];
        for (int tier = 0; tier < tierGroups.Length; tier++)
        {
            if (tier > 0)
            {
                RetentionSelectionPassResult selectedTier = await store.SelectEligibleActiveAsync(11, 11,
                    underPressure: true, now, token);
                Assert.Equal(11, selectedTier.Selected);
            }

            Guid[] selectedTierIds = tierGroups[tier];
            int selectedCount = await database.Captures.AsNoTracking()
                .CountAsync(capture => selectedTierIds.Contains(capture.Id) && capture.RetentionDisposition == "Trash", token);
            Assert.Equal(11, selectedCount);
            if (tier + 1 < tierGroups.Length)
            {
                Guid[] laterTierIds = tierGroups[tier + 1];
                int laterTierCount = await database.Captures.AsNoTracking()
                    .CountAsync(capture => laterTierIds.Contains(capture.Id)
                        && capture.RetentionDisposition == "Trash", token);
                Assert.Equal(0, laterTierCount);
            }
        }
    }

    [Fact]
    public async Task Mixed_capture_uses_latest_variant_boundary_and_oldest_common_boundary_first()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _ = await InitializeQuotaMarkerAsync(fixture, token);
        Guid[] captures = await SeedMetadataCapturesAsync(fixture, 3);
        await SeedPressureAssetsAsync(fixture,
        [
            new(captures[0], now.AddDays(-40), Held: true),
            new(captures[1], now.AddDays(-20), Held: true),
            new(captures[2], now.AddDays(-40), ConfirmedAt: now.AddDays(-2)),
            new(captures[2], now.AddDays(-10), Unfinished: true),
        ], token);

        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfRetentionRemovalStore(database,
            new EfCaptureRetentionStore(database, TimeProvider.System), TimeProvider.System);

        RetentionSelectionPassResult oldest = await store.SelectEligibleActiveAsync(1, long.MaxValue,
            underPressure: true, now, token);
        Assert.Equal(1, oldest.Selected);
        Assert.Equal("Trash", (await database.Captures.AsNoTracking().SingleAsync(item => item.Id == captures[0], token)).RetentionDisposition);

        RetentionSelectionPassResult next = await store.SelectEligibleActiveAsync(1, long.MaxValue,
            underPressure: true, now, token);
        Assert.Equal(1, next.Selected);
        Assert.Equal("Trash", (await database.Captures.AsNoTracking().SingleAsync(item => item.Id == captures[1], token)).RetentionDisposition);
        Assert.Equal("Active", (await database.Captures.AsNoTracking().SingleAsync(item => item.Id == captures[2], token)).RetentionDisposition);

        RetentionSelectionPassResult mixedDue = await store.SelectEligibleActiveAsync(1, long.MaxValue,
            underPressure: true, now.AddDays(4).AddSeconds(1), token);

        Assert.Equal(1, mixedDue.Selected);
        CaptureEntity mixed = await database.Captures.AsNoTracking().SingleAsync(item => item.Id == captures[2], token);
        Assert.Equal("Trash", mixed.RetentionDisposition);
        Assert.Equal("pressure", mixed.TrashReason);
    }

    [Fact]
    public async Task Pending_recoverable_trash_remains_in_shared_cap_and_blocks_over_capacity_admission()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _ = await InitializeQuotaMarkerAsync(fixture, token);
        Guid[] captures = await SeedMetadataCapturesAsync(fixture, 2);
        await SeedPressureAssetsAsync(fixture,
        [
            new(captures[0], now.AddDays(-40), Held: true, SizeBytes: 40, Disposition: "Trash",
                RecoverableUntil: now.AddHours(71)),
            new(captures[1], now.AddDays(-40), Held: true, SizeBytes: 40),
        ], token);

        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        var accounting = new FileSystemSpoolAccounting(fixture.Database.SpoolRoot,
            Path.Combine(fixture.Database.SpoolRoot, "previews"), 0, null, TimeProvider.System);
        var quota = new EfSpoolQuotaStore(database, accounting, TimeProvider.System);
        SpoolQuotaSnapshot measured = await quota.GetSnapshotAsync(token);
        Assert.True(measured.IsKnown);
        Assert.Equal(40, measured.TrashBytes);
        Assert.Equal(40, measured.ActiveBytes);
        Assert.Equal(80, measured.UsedBytes);

        SpoolQuotaSettings settings = await quota.GetSettingsAsync(token);
        Assert.NotNull(await quota.UpdateSettingsAsync(settings.Revision,
            new(settings.Revision, IsActive: true, QuotaBytes: 100, HighWatermarkBytes: 80,
                RecoveryTargetBytes: 50), token));

        bool admitted = await quota.TryReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 21, 0, [], 1), token);

        Assert.False(admitted);
        SpoolQuotaSnapshot after = await quota.GetSnapshotAsync(token);
        Assert.Equal(80, after.UsedBytes);
        Assert.Equal(40, after.TrashBytes);
        _ = accounting;
    }

    [Fact]
    public async Task Held_and_optional_unfinished_work_reach_only_their_finite_policy_boundaries()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _ = await InitializeQuotaMarkerAsync(fixture, token);
        Guid[] captureIds = await SeedMetadataCapturesAsync(fixture, 2);
        Guid heldId = captureIds[0];
        Guid unfinishedId = captureIds[1];
        await SeedPressureAssetsAsync(fixture,
            [new(heldId, now - TimeSpan.FromDays(14) + TimeSpan.FromHours(1), Held: true)], token);

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            var store = new EfRetentionRemovalStore(database,
                new EfCaptureRetentionStore(database, TimeProvider.System), TimeProvider.System);
            RetentionSelectionPassResult early = await store.SelectEligibleActiveAsync(1, long.MaxValue,
                underPressure: true, now, token);
            Assert.Equal(0, early.Selected);
            RetentionSelectionPassResult heldDue = await store.SelectEligibleActiveAsync(1, long.MaxValue,
                underPressure: true, now.AddHours(1).AddSeconds(1), token);
            Assert.Equal(1, heldDue.Selected);
        }

        DateTimeOffset ordinaryNow = now.AddHours(1);
        await SeedPressureAssetsAsync(fixture,
            [new(unfinishedId, ordinaryNow - TimeSpan.FromDays(180) + TimeSpan.FromHours(1), Unfinished: true)], token);

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            var store = new EfRetentionRemovalStore(database,
                new EfCaptureRetentionStore(database, TimeProvider.System), TimeProvider.System);
            RetentionSelectionPassResult beforeOrdinaryExpiry = await store.SelectEligibleActiveAsync(1, long.MaxValue,
                underPressure: false, ordinaryNow, token);
            Assert.Equal(0, beforeOrdinaryExpiry.Selected);
            RetentionSelectionPassResult ordinaryDue = await store.SelectEligibleActiveAsync(1, long.MaxValue,
                underPressure: false, ordinaryNow.AddHours(1).AddSeconds(1), token);
            Assert.Equal(1, ordinaryDue.Selected);
        }

        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal("Trash", (await verify.Captures.AsNoTracking().SingleAsync(item => item.Id == heldId, token)).RetentionDisposition);
        Assert.Equal("Trash", (await verify.Captures.AsNoTracking().SingleAsync(item => item.Id == unfinishedId, token)).RetentionDisposition);
    }

    private static async Task<FileSystemSpoolAccounting> InitializeQuotaMarkerAsync(ConfigurationApiFixture fixture,
        CancellationToken token)
    {
        Directory.CreateDirectory(fixture.Database.SpoolRoot);
        var accounting = new FileSystemSpoolAccounting(fixture.Database.SpoolRoot,
            Path.Combine(fixture.Database.SpoolRoot, "previews"), 0, null, TimeProvider.System);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        SpoolQuotaSnapshot snapshot = await new EfSpoolQuotaStore(database, accounting, TimeProvider.System)
            .GetSnapshotAsync(token);
        Assert.True(snapshot.IsKnown, snapshot.UnknownReason);
        return accounting;
    }

    private static async Task SeedPressureAssetsAsync(ConfigurationApiFixture fixture,
        IReadOnlyList<PressureCaptureSeed> seeds, CancellationToken token)
    {
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        Guid[] ids = [.. seeds.Select(seed => seed.CaptureId).Distinct()];
        Dictionary<Guid, CaptureEntity> captures = await database.Captures
            .Where(capture => ids.Contains(capture.Id)).ToDictionaryAsync(capture => capture.Id, token);
        var primaryAssigned = new HashSet<Guid>();

        for (int index = 0; index < seeds.Count; index++)
        {
            PressureCaptureSeed seed = seeds[index];
            CaptureEntity capture = captures[seed.CaptureId];
            capture.FirstAcceptedAt = index == 0 || seed.AcceptedAt < capture.FirstAcceptedAt
                ? seed.AcceptedAt : capture.FirstAcceptedAt;
            capture.LastPrimaryIngestedAt = capture.FirstAcceptedAt;
            capture.Held = seed.Held;
            capture.IsFavorite = seed.Favorite;
            capture.NoDeliveryIntent = seed.ExplicitNoDelivery;
            capture.RetentionFactsDirty = true;
            if (seed.Disposition == "Trash")
            {
                capture.RetentionDisposition = "Trash";
                capture.TrashReason = "manual";
                capture.TrashEnteredAt = seed.RecoverableUntil!.Value.AddHours(-72);
                capture.RecoverableUntil = seed.RecoverableUntil;
                capture.RetentionPaused = true;
            }

            Guid assetId = Guid.NewGuid();
            Guid operationId = Guid.NewGuid();
            string spoolKey = $"spool/2026/10/{assetId:N}";
            string partialKey = $"incoming/{capture.SourceId:N}/{assetId:N}.partial";
            string path = Path.Combine(fixture.Database.SpoolRoot,
                spoolKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [.. Enumerable.Repeat((byte)0x5a, seed.SizeBytes)], token);
            string sha256 = new('a', 64);
            database.Assets.Add(new AssetEntity
            {
                Id = assetId, SourceId = capture.SourceId, OriginalName = $"synthetic-{assetId:N}.raw",
                SpoolKey = spoolKey, Size = seed.SizeBytes, Sha256 = sha256,
                IngestedAt = seed.AcceptedAt, RecoveryCorrelation = operationId, State = "Spooled",
            });
            database.Operations.Add(new IngestOperationEntity
            {
                Id = operationId, AssetId = assetId, SourceId = capture.SourceId,
                OriginalName = $"synthetic-{assetId:N}.raw", PartialKey = partialKey, FinalKey = spoolKey,
                State = "Committed", Size = seed.SizeBytes, Sha256 = sha256,
            });
            bool isPrimary = primaryAssigned.Add(capture.Id);
            database.CaptureAssets.Add(new CaptureAssetEntity
            {
                CaptureId = capture.Id, AssetId = assetId, IsPrimary = isPrimary,
                Role = isPrimary ? "RAW" : "JPEG", GroupingReason = "synthetic",
            });

            if (seed.ConfirmedAt is not null || seed.Unfinished)
            {
                Guid decisionId = Guid.NewGuid();
                Guid destinationId = Guid.NewGuid();
                DeliveryState state = seed.ConfirmedAt is null ? DeliveryState.Pending : DeliveryState.Delivered;
                database.RoutingDecisions.Add(new RoutingDecisionEntity
                {
                    Id = decisionId, CaptureId = capture.Id, AssetId = assetId, DestinationId = destinationId,
                    Generation = 1, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
                    Required = !seed.OptionalUnfinished, TargetPath = $"synthetic/{assetId:N}.raw",
                    TargetPathKey = $"SYNTHETIC/{assetId:N}.RAW", Explanation = "Synthetic retention acceptance evidence.",
                    CreatedAt = seed.AcceptedAt,
                });
                database.Deliveries.Add(new DeliveryEntity
                {
                    Id = Guid.NewGuid(), DecisionId = decisionId, AssetId = assetId,
                    DestinationId = destinationId, TargetPath = $"synthetic/{assetId:N}.raw",
                    Required = !seed.OptionalUnfinished, State = state,
                    ConfirmedAt = seed.ConfirmedAt,
                    CreatedAt = seed.AcceptedAt,
                });
            }
        }
        await database.SaveChangesAsync(token);
    }

    private sealed record PressureCaptureSeed(Guid CaptureId, DateTimeOffset AcceptedAt,
        DateTimeOffset? ConfirmedAt = null, bool Favorite = false, bool ExplicitNoDelivery = false,
        bool Held = false, bool Unfinished = false, bool OptionalUnfinished = true, int SizeBytes = 1,
        string Disposition = "Active", DateTimeOffset? RecoverableUntil = null);
}
