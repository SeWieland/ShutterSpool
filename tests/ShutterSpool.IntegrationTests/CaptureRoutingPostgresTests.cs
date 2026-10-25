using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Routing;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;
using System.Security.Cryptography;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class CaptureRoutingPostgresTests
{
    [Fact]
    public async Task Collection_history_freezes_for_late_assets_and_resend_but_reevaluate_uses_current_context()
    {
        string? connection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION for PostgreSQL checks.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RoutingTestDatabase fixture = await RoutingTestDatabase.CreateAsync(connection, token);
        await using var database = new ShutterSpoolDbContext(fixture.IngestOptions);
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), unmatchedId = Guid.NewGuid();
        Guid captureId = Guid.NewGuid(), rawId = Guid.NewGuid(), jpegId = Guid.NewGuid(), collectionId = Guid.NewGuid();
        database.AddRange(new SourceEntity { Id = sourceId, Name = "Synthetic" },
            new DestinationEntity { Id = destinationId, Name = "Synthetic target" },
            new DestinationEntity { Id = unmatchedId, Name = "Unmatched target" },
            new CollectionEntity { Id = collectionId, Name = "Berlin", Path = "Events/Berlin", Identity = "EVENTS/BERLIN" });
        await database.SaveChangesAsync(token);
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("event", "Event", 0, true, [], "{collectionPath}", "{suppliedName}")]), token);
        await new DestinationRoutingAdminService(database).SetAsync(new(unmatchedId, true, []), token);
        await AddReleasedAssetAsync(database, sourceId, captureId, rawId, "frame.raw", token, collectionId);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE collections SET "Name" = 'Renamed', "Path" = 'Events/Renamed', "Identity" = 'EVENTS/RENAMED', "Revision" = "Revision" + 1 WHERE "Id" = {collectionId}
            """, token);
        database.ChangeTracker.Clear();
        Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
        Guid deliveryId = await database.Deliveries.Where(item => item.AssetId == rawId).Select(item => item.Id).SingleAsync(token);
        Assert.Equal("Events/Berlin/frame.raw", (await database.Deliveries.SingleAsync(item => item.Id == deliveryId, token)).TargetPath);

        // A move after release changes organization, while a complementary Asset uses the release snapshot.
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE captures SET "CollectionId" = {ShutterSpool.Domain.Collections.CollectionName.BuiltInId}, "Revision" = "Revision" + 1 WHERE "Id" = {captureId}
            """, token);
        await InsertAssetAsync(database, jpegId, sourceId, "frame.jpg", "JPEG", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO capture_assets ("AssetId", "CaptureId", "Role", "IsPrimary", "GroupingReason") VALUES ({jpegId}, {captureId}, 'JPEG', true, 'paired_complement')
            """, token);
        database.ChangeTracker.Clear();
        Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
        Assert.Equal("Events/Berlin/frame.jpg", (await database.Deliveries.SingleAsync(item => item.AssetId == jpegId, token)).TargetPath);
        Assert.All(await database.RoutingDecisions.Where(item => item.CaptureId == captureId).ToArrayAsync(token), item =>
        {
            Assert.Equal(collectionId, item.CollectionId);
            Assert.Equal("Berlin", item.CollectionName);
            Assert.Equal("Events/Berlin", item.CollectionPath);
        });

        var actions = new CaptureRoutingWorkService(database, TimeProvider.System);
        RoutingActionPreview movePreview = await actions.PreviewReevaluateAsync(rawId, destinationId, token);
        Assert.Equal("ShutterSpool/frame.raw", movePreview.ProposedTarget);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE captures SET "CollectionId" = {collectionId}, "Revision" = "Revision" + 1 WHERE "Id" = {captureId}
            """, token);
        database.ChangeTracker.Clear();
        Assert.Equal(RoutingActionStatus.StalePreview,
            (await actions.ReevaluateAsync(rawId, destinationId, movePreview.ConfirmationToken, rawId, token)).Status);
        RoutingActionPreview renamePreview = await actions.PreviewReevaluateAsync(rawId, destinationId, token);
        Assert.Equal("Events/Renamed/frame.raw", renamePreview.ProposedTarget);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE collections SET "Name" = 'Final', "Path" = 'Events/Final', "Identity" = 'EVENTS/FINAL', "Revision" = "Revision" + 1 WHERE "Id" = {collectionId}
            """, token);
        database.ChangeTracker.Clear();
        Assert.Equal(RoutingActionStatus.StalePreview,
            (await actions.ReevaluateAsync(rawId, destinationId, renamePreview.ConfirmationToken, rawId, token)).Status);
        RoutingActionPreview currentPreview = await actions.PreviewReevaluateAsync(rawId, destinationId, token);
        Assert.Equal(RoutingActionStatus.Recorded,
            (await actions.ReevaluateAsync(rawId, destinationId, currentPreview.ConfirmationToken, rawId, token)).Status);
        RoutingDecisionEntity current = await database.RoutingDecisions.SingleAsync(item => item.AssetId == rawId
            && item.DestinationId == destinationId && item.IsCurrent, token);
        Assert.Equal("Events/Final", current.CollectionPath);
        Assert.Equal("Events/Final/frame.raw", current.TargetPath);

        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("fixed", "Fixed target", 0, true, [], "fixed", "{suppliedName}")]), token);
        RoutingActionPreview fixedPreview = await actions.PreviewReevaluateAsync(rawId, destinationId, token);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE collections SET "Name" = 'Changed', "Path" = 'Events/Changed', "Identity" = 'EVENTS/CHANGED', "Revision" = "Revision" + 1 WHERE "Id" = {collectionId}
            """, token);
        database.ChangeTracker.Clear();
        RoutingActionPreview changedContext = await actions.ReevaluateAsync(rawId, destinationId, fixedPreview.ConfirmationToken, rawId, token);
        Assert.Equal(fixedPreview.ProposedTarget, changedContext.ProposedTarget);
        Assert.Equal(RoutingActionStatus.StalePreview, changedContext.Status);

        // A recorded successful delivery may be repeated after organization changes without following them.
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET \"State\" = 'Delivered' WHERE \"Id\" = {deliveryId}", token);
        database.ChangeTracker.Clear();
        RoutingActionPreview resendPreview = await actions.PreviewResendAsync(deliveryId, token);
        Assert.Equal(RoutingActionStatus.Queued,
            (await actions.ResendAsync(deliveryId, resendPreview.ConfirmationToken, rawId, token)).Status);
        DeliveryEntity repeated = await database.Deliveries.SingleAsync(item => item.RepeatsDeliveryId == deliveryId, token);
        Assert.Equal("Events/Berlin/frame.raw", repeated.TargetPath);
        Assert.Equal("Events/Berlin", (await database.RoutingDecisions.SingleAsync(item => item.Id == repeated.DecisionId, token)).CollectionPath);
        RoutingHistoryPage<RoutingDeliveryHistory>? history = await new CaptureRoutingHistoryStore(database).GetDeliveriesAsync(captureId, new(), token);
        Assert.Contains(history!.Items, item => item.Id == repeated.Id && item.CollectionPath == "Events/Berlin");
    }

    private static CaptureRoutingActionService CreateActions(ShutterSpoolDbContext database, ShutterSpool.Application.Spool.ICommittedOriginalReader originals) =>
        new(new CaptureRoutingWorkService(database, TimeProvider.System), new EfVerifiedOriginalSupplier(database, originals));

    [Fact]
    public async Task Legacy_release_routes_late_assets_without_claiming_a_historical_collection()
    {
        string? connection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION for PostgreSQL checks.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using RoutingTestDatabase fixture = await RoutingTestDatabase.CreateAsync(connection, token);
        await using var database = new ShutterSpoolDbContext(fixture.IngestOptions);
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid(), rawId = Guid.NewGuid(), jpegId = Guid.NewGuid();
        database.AddRange(new SourceEntity { Id = sourceId, Name = "Synthetic" }, new DestinationEntity { Id = destinationId, Name = "Legacy target" });
        await database.SaveChangesAsync(token);
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("legacy", "Legacy", 0, true, [], "original", "{suppliedName}")]), token);
        await AddReleasedAssetAsync(database, sourceId, captureId, rawId, "legacy.raw", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE captures SET "ReleasedCollectionId" = NULL, "ReleasedCollectionName" = NULL, "ReleasedCollectionPath" = NULL WHERE "Id" = {captureId}
            """, token);
        database.ChangeTracker.Clear();
        Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("new", "New rules", 0, true, [], "{collectionPath}", "{suppliedName}")]), token);
        await InsertAssetAsync(database, jpegId, sourceId, "legacy.jpg", "JPEG", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO capture_assets ("AssetId", "CaptureId", "Role", "IsPrimary", "GroupingReason") VALUES ({jpegId}, {captureId}, 'JPEG', true, 'paired_complement')
            """, token);
        database.ChangeTracker.Clear();
        Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
        Assert.Equal("original/legacy.jpg", (await database.Deliveries.SingleAsync(item => item.AssetId == jpegId, token)).TargetPath);
        Assert.All(await database.RoutingDecisions.Where(item => item.CaptureId == captureId).ToArrayAsync(token), item =>
        {
            Assert.Null(item.CollectionId);
            Assert.Null(item.CollectionName);
            Assert.Null(item.CollectionPath);
        });
        Assert.Equal("ShutterSpool/legacy.jpg",
            (await new CaptureRoutingWorkService(database, TimeProvider.System).PreviewReevaluateAsync(jpegId, destinationId, token)).ProposedTarget);
    }

    [Fact]
    public async Task Release_freezes_rules_and_atomically_persists_first_match_decisions_and_work()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
        string databaseName = $"m106_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        try
        {
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var securityOptions = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), noMatchDestinationId = Guid.NewGuid();
            Guid captureId = Guid.NewGuid(), rawId = Guid.NewGuid(), jpegId = Guid.NewGuid(), replayId = Guid.NewGuid(), otherId = Guid.NewGuid(), zeroCaptureId = Guid.NewGuid(), zeroAssetId = Guid.NewGuid();
            await using (var security = new ShutterSpoolDbContext(securityOptions))
            {
                await security.Database.MigrateAsync(token);
                security.AddRange(
                    new SourceEntity { Id = sourceId, Name = "synthetic", },
                    new DestinationEntity { Id = destinationId, Name = "primary", },
                    new DestinationEntity { Id = noMatchDestinationId, Name = "empty", });
                await security.SaveChangesAsync(token);
            }
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(token);
                await InsertAssetAsync(database, rawId, sourceId, "frame.CR3", "RAW", token);
                DateTimeOffset anchor = DateTimeOffset.UtcNow;
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO captures ("Id", "SourceId", "NormalizedStem", "LastPrimaryIngestedAt", "ReleasedRating", "ReleasedTags", "ReleasedLabel")
                    VALUES ({captureId}, {sourceId}, 'FRAME', {anchor}, 4, ARRAY['web']::text[], NULL)
                    """, token);
                await database.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO capture_assets ("AssetId", "CaptureId", "Role", "IsPrimary", "GroupingReason")
                    VALUES ({rawId}, {captureId}, 'RAW', true, 'test')
                    """, token);
            }

            RoutingRule[] originalRules =
            [
                new("raw", "RAW original", 0, false,
                    [new("variant", "equals", Json("RAW")), new("cameraModel", "equals", Json("unknown"))],
                    "raw", "{suppliedName}", ConditionMode: RoutingConditionMode.Or),
                new("fallback", "JPEG fallback", 1, true, [new("variant", "equals", Json("JPEG"))], "all", "{suppliedName}"),
            ];
            await using (var database = new ShutterSpoolDbContext(options))
            {
                var adminService = new DestinationRoutingAdminService(database);
                await adminService.SetAsync(new DestinationRouting(destinationId, true, originalRules), token);
                await adminService.SetAsync(new DestinationRouting(noMatchDestinationId, true, []), token);
                Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System)).ReleaseNowAsync(captureId, 0, token)).Succeeded);
            }
            await using (var database = new ShutterSpoolDbContext(options))
                await new DestinationRoutingAdminService(database).SetAsync(new DestinationRouting(destinationId, true,
                    [new("changed", "Changed after release", 0, true, [new("variant", "equals", Json("RAW"))], "changed", "different.jpg")]), token);
            await using (var database = new ShutterSpoolDbContext(options))
            {
                Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
                Assert.Equal("COMPLETE", await TextAsync(database, "SELECT \"State\" FROM captures WHERE \"Id\" = @capture", captureId, token));
                await InsertAssetAsync(database, jpegId, sourceId, "frame.jpg", "JPEG", token);
                await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({jpegId}, {captureId}, 'JPEG', true, 'paired_complement')", token);
                Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
                Assert.Equal("RELEASED", await TextAsync(database, "SELECT \"State\" FROM captures WHERE \"Id\" = @capture", captureId, token));
                await InsertAssetAsync(database, replayId, sourceId, "frame.CR3", "RAW", token);
                await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"RelatedAssetId\", \"GroupingReason\") VALUES ({replayId}, {captureId}, 'RAW', false, {rawId}, 'recognized_replay')", token);
                Assert.False(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
                byte[] replayBytes = [1, 3, 3, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31, 33];
                string replayHash = Convert.ToHexString(SHA256.HashData(replayBytes)).ToLowerInvariant();
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE assets SET \"Size\" = {replayBytes.Length}, \"Sha256\" = {replayHash} WHERE \"Id\" IN ({rawId}, {replayId})", token);
                string spoolRoot = Path.Combine(Path.GetTempPath(), $"shutterspool-m106-{Guid.NewGuid():N}");
                Directory.CreateDirectory(Path.Combine(spoolRoot, "spool"));
                await File.WriteAllBytesAsync(Path.Combine(spoolRoot, "spool", replayId.ToString("N")), replayBytes, token);
                var actions = CreateActions(database, new FileSystemSpool(new FileSystemSpoolOptions { RootPath = spoolRoot, ReservedBytes = 0 }));
                Guid rawDeliveryId = await GuidScalarAsync(database, "SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset", rawId, token);
                Assert.Equal("Pending", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"Id\" = @capture", rawDeliveryId, token));
                await CompleteConfirmedAsync(options, rawDeliveryId, token);
                database.ChangeTracker.Clear();
                RoutingActionPreview resendPreview = await actions.PreviewResendAsync(rawDeliveryId, token);
                Assert.Equal(RoutingActionStatus.ConfirmationRequired, resendPreview.Status);
                Assert.False(string.IsNullOrWhiteSpace(resendPreview.ConfirmationToken));
                RoutingActionPreview resend = await actions.ResendAsync(rawDeliveryId, resendPreview.ConfirmationToken, token);
                Assert.Equal(RoutingActionStatus.Queued, resend.Status);
                Assert.Equal(replayId, await NullableGuidScalarAsync(database, "SELECT \"SourceAssetId\" FROM deliveries WHERE \"RepeatsDeliveryId\" = @delivery", rawDeliveryId, token));
                RoutingActionPreview secondPreview = await actions.PreviewResendAsync(rawDeliveryId, token);
                Assert.Equal(RoutingActionStatus.AlreadyQueued, (await actions.ResendAsync(rawDeliveryId, secondPreview.ConfirmationToken, token)).Status);
                RoutingActionPreview expiredPreview = await actions.PreviewResendAsync(rawDeliveryId, token);
                Guid previewId = Guid.ParseExact(expiredPreview.ConfirmationToken!, "N");
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE routing_actions SET \"CreatedAt\" = {DateTimeOffset.UtcNow.AddMinutes(-16)} WHERE \"Id\" = {previewId}", token);
                database.ChangeTracker.Clear();
                Assert.Equal(RoutingActionStatus.StalePreview, (await actions.ResendAsync(rawDeliveryId, expiredPreview.ConfirmationToken, token)).Status);
                Assert.Equal(2, await ScalarAsync(database, "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset", rawId, token, ("asset", rawId)));
                Directory.Delete(spoolRoot, recursive: true);
                Guid jpegDeliveryId = await GuidScalarAsync(database, "SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset", jpegId, token);
                await CompleteAfterReconciliationAsync(options, jpegDeliveryId, token);
                database.ChangeTracker.Clear();
                Assert.Equal("COMPLETE", await TextAsync(database, "SELECT \"State\" FROM captures WHERE \"Id\" = @capture", captureId, token));

                await InsertAssetAsync(database, otherId, sourceId, "other.bin", "OTHER", token);
                await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({otherId}, {captureId}, 'OTHER', true, 'late_other')", token);
                Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
                Assert.Equal("COMPLETE", await TextAsync(database, "SELECT \"State\" FROM captures WHERE \"Id\" = @capture", captureId, token));

                await InsertAssetAsync(database, zeroAssetId, sourceId, "unmatched.bin", "OTHER", token);
                DateTimeOffset zeroAnchor = DateTimeOffset.UtcNow;
                await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO captures (\"Id\", \"SourceId\", \"NormalizedStem\", \"LastPrimaryIngestedAt\") VALUES ({zeroCaptureId}, {sourceId}, 'UNMATCHED', {zeroAnchor})", token);
                await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({zeroAssetId}, {zeroCaptureId}, 'OTHER', true, 'test')", token);
                Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System)).ReleaseNowAsync(zeroCaptureId, 0, token)).Succeeded);
                Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
                Assert.Equal("RELEASED", await TextAsync(database, "SELECT \"State\" FROM captures WHERE \"Id\" = @capture", zeroCaptureId, token));
                Assert.Equal(0, await ScalarAsync(database, "SELECT count(*) FROM deliveries d JOIN routing_decisions r ON r.\"Id\" = d.\"DecisionId\" WHERE r.\"CaptureId\" = @capture", zeroCaptureId, token));
            }

            await using var verify = new ShutterSpoolDbContext(options);
            string captureState = await TextAsync(verify, "SELECT \"State\" FROM captures WHERE \"Id\" = @capture", captureId, token);
            Assert.Equal(nameof(CaptureState.COMPLETE), captureState);
            string snapshotJson = await TextAsync(verify, "SELECT \"RoutingSnapshotJson\"::text FROM captures WHERE \"Id\" = @capture", captureId, token);
            DestinationRouting[] snapshot = RoutingRuleJson.ReadSnapshot(snapshotJson);
            Assert.Equal("raw", snapshot.Single(item => item.DestinationId == destinationId).Rules[0].Id);
            Assert.Equal(RoutingConditionMode.Or, snapshot.Single(item => item.DestinationId == destinationId).Rules[0].ConditionMode);
            Assert.Equal(7, await ScalarAsync(verify, "SELECT count(*) FROM routing_decisions WHERE \"CaptureId\" = @capture", captureId, token));
            Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM routing_decisions WHERE \"CaptureId\" = @capture AND \"AssetId\" = @asset AND \"DestinationId\" = @destination AND \"Kind\" = 'Matched' AND \"RuleId\" = 'raw' AND \"IsCurrent\" AND NOT \"Required\" AND \"TargetPath\" = 'raw/frame.CR3'", captureId, token, ("asset", rawId), ("destination", destinationId)));
            Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM routing_decisions WHERE \"CaptureId\" = @capture AND \"AssetId\" = @asset AND \"DestinationId\" = @destination AND \"Kind\" = 'Matched' AND \"RuleId\" = 'fallback' AND \"Required\" AND \"TargetPath\" = 'all/frame.jpg'", captureId, token, ("asset", jpegId), ("destination", destinationId)));
            Assert.Equal(3, await ScalarAsync(verify, "SELECT count(*) FROM deliveries d JOIN routing_decisions r ON r.\"Id\" = d.\"DecisionId\" WHERE r.\"CaptureId\" = @capture", captureId, token));
            Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM routing_actions WHERE \"AssetId\" = @asset AND \"Kind\" = 'Resend' AND \"Result\" = 'queued' AND \"DeliveryId\" IS NOT NULL", rawId, token, ("asset", rawId)));
            Assert.Equal(3, await ScalarAsync(verify, "SELECT count(*) FROM routed_assets routed JOIN capture_assets ca ON ca.\"AssetId\" = routed.\"AssetId\" WHERE ca.\"CaptureId\" = @capture", captureId, token));
            Assert.Equal(3, await ScalarAsync(verify, "SELECT count(*) FROM routing_decisions WHERE \"CaptureId\" = @capture AND \"DestinationId\" = @destination AND \"Kind\" = 'NoMatch'", captureId, token, ("destination", noMatchDestinationId)));
            Assert.Equal(0, await ScalarAsync(verify, "SELECT count(*) FROM routed_assets WHERE \"AssetId\" = @capture", replayId, token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Destination_pause_and_cross_capture_case_insensitive_collision_are_durable()
    {
        string? connection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
        await using RoutingTestDatabase testDatabase = await RoutingTestDatabase.CreateAsync(connection, TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), activeDestinationId = Guid.NewGuid(), disabledDestinationId = Guid.NewGuid();
        Guid firstCaptureId = Guid.NewGuid(), secondCaptureId = Guid.NewGuid(), firstAssetId = Guid.NewGuid(), secondAssetId = Guid.NewGuid();
        await using (var security = new ShutterSpoolDbContext(testDatabase.SecurityOptions))
        {
            security.AddRange(
                new SourceEntity { Id = sourceId, Name = "synthetic", },
                new DestinationEntity { Id = activeDestinationId, Name = "active", },
                new DestinationEntity { Id = disabledDestinationId, Name = "disabled", });
            await security.SaveChangesAsync(token);
        }
        RoutingRule[] rules = [new("raw", "RAW", 0, true, [new("variant", "equals", Json("RAW"))], "", "{suppliedName}")];
        await using var database = new ShutterSpoolDbContext(testDatabase.IngestOptions);
        var routing = new DestinationRoutingAdminService(database);
        await routing.SetAsync(new(activeDestinationId, true, rules), token);
        await routing.SetAsync(new(disabledDestinationId, false, rules), token);
        await AddReleasedAssetAsync(database, sourceId, firstCaptureId, firstAssetId, "Frame.CR3", token);
        await AddReleasedAssetAsync(database, sourceId, secondCaptureId, secondAssetId, "frame.cr3", token);

        var processor = new EfCaptureRoutingProcessor(database);
        Assert.True(await processor.ProcessOneAsync(DateTimeOffset.UtcNow, token));
        Assert.True(await processor.ProcessOneAsync(DateTimeOffset.UtcNow, token));
        Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM routing_decisions WHERE \"CaptureId\" = @capture AND \"DestinationId\" = @destination AND \"Kind\" = 'Matched'", firstCaptureId, token, ("destination", activeDestinationId)));
        Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM routing_decisions WHERE \"CaptureId\" = @capture AND \"DestinationId\" = @destination AND \"Kind\" = 'InvalidTarget'", secondCaptureId, token, ("destination", activeDestinationId)));
        Assert.Equal(2, await ScalarAsync(database, "SELECT count(*) FROM routing_decisions WHERE \"DestinationId\" = @destination AND \"Kind\" = 'DestinationDisabled'", Guid.Empty, token, ("destination", disabledDestinationId)));

        await routing.SetAsync(new(activeDestinationId, false, rules), token);
        Assert.Equal("Disabled", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @capture", firstAssetId, token));
        Guid pausedDeliveryId = await GuidScalarAsync(database, "SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset", firstAssetId, token);
        Assert.Null(await CreateDeliveryStore(testDatabase.IngestOptions).ClaimAsync(token));
        Assert.Equal("Disabled", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"Id\" = @capture", pausedDeliveryId, token));
        await routing.SetAsync(new(activeDestinationId, true, rules), token);
        Assert.Equal("Pending", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @capture", firstAssetId, token));
    }

    [Fact]
    public async Task Retry_and_reevaluate_preserve_outcomes_and_require_fresh_confirmations()
    {
        string? connection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
        await using RoutingTestDatabase testDatabase = await RoutingTestDatabase.CreateAsync(connection, TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid(), assetId = Guid.NewGuid();
        await using (var security = new ShutterSpoolDbContext(testDatabase.SecurityOptions))
        {
            security.AddRange(
                new SourceEntity { Id = sourceId, Name = "synthetic", },
                new DestinationEntity { Id = destinationId, Name = "destination", });
            await security.SaveChangesAsync(token);
        }

        byte[] original = [1, 3, 3, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31, 33];
        string originalHash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        await using var database = new ShutterSpoolDbContext(testDatabase.IngestOptions);
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("initial", "Initial", 0, true, [], "first", "file.raw")]), token);
        await AddReleasedAssetAsync(database, sourceId, captureId, assetId, "file.raw", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE assets SET \"Size\" = {original.Length}, \"Sha256\" = {originalHash} WHERE \"Id\" = {assetId}", token);
        var processor = new EfCaptureRoutingProcessor(database);
        Assert.True(await processor.ProcessOneAsync(DateTimeOffset.UtcNow, token));
        Guid initialDeliveryId = await GuidScalarAsync(database, "SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset", assetId, token);
        var work = CreateActions(database, new MemoryOriginalReader(original));
        ClaimedDelivery initialClaim = await CompleteConfirmedAsync(testDatabase.IngestOptions, initialDeliveryId, token);
        database.ChangeTracker.Clear();

        RoutingActionPreview stale = await work.PreviewReevaluateAsync(assetId, destinationId, token);
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("second", "Second", 0, true, [], "second", "file.raw")]), token);
        Assert.Equal(RoutingActionStatus.StalePreview, (await work.ReevaluateAsync(assetId, destinationId, stale.ConfirmationToken, token)).Status);
        RoutingActionPreview secondPreview = await work.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal("second/file.raw", secondPreview.ProposedTarget);
        var curation = new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System));
        CaptureCurationState curationBeforeEdit = (await curation.GetAsync(captureId, token))!;
        Assert.True((await curation.UpdateAsync(captureId, curationBeforeEdit.Revision,
            new CaptureCuration(4, "Green", ["updated-after-preview"]), token)).Succeeded);
        Assert.Equal(RoutingActionStatus.StalePreview, (await work.ReevaluateAsync(assetId, destinationId, secondPreview.ConfirmationToken, token)).Status);
        secondPreview = await work.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal(RoutingActionStatus.Recorded, (await work.ReevaluateAsync(assetId, destinationId, secondPreview.ConfirmationToken, token)).Status);
        Assert.Equal("Delivered", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"Id\" = @capture", initialDeliveryId, token));

        Guid secondDeliveryId = await GuidScalarAsync(database, "SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset AND \"Id\" <> @prior ORDER BY \"CreatedAt\" DESC LIMIT 1", assetId, token, ("prior", initialDeliveryId));
        await CompleteConfirmedAsync(testDatabase.IngestOptions, secondDeliveryId, token);
        database.ChangeTracker.Clear();
        var withoutBytes = CreateActions(database, new MissingOriginalReader());
        RoutingActionPreview resendPreview = await withoutBytes.PreviewResendAsync(secondDeliveryId, token);
        Assert.Equal(RoutingActionStatus.BlockedOriginalUnavailable, (await withoutBytes.ResendAsync(secondDeliveryId, resendPreview.ConfirmationToken, token)).Status);
        Assert.Equal(2, await ScalarAsync(database, "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset", assetId, token, ("asset", assetId)));
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("third", "Third", 0, true, [], "third", "file.raw")]), token);
        RoutingActionPreview successfulPreview = await work.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal("third/file.raw", successfulPreview.ProposedTarget);
        Assert.Equal(RoutingActionStatus.Recorded, (await work.ReevaluateAsync(assetId, destinationId, successfulPreview.ConfirmationToken, token)).Status);
        Assert.Equal("Delivered", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"Id\" = @capture", secondDeliveryId, token));
        Guid supersededDeliveryId = await GuidScalarAsync(database, "SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset ORDER BY \"CreatedAt\" DESC LIMIT 1", assetId, token);
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("fourth", "Fourth", 0, true, [], "fourth", "file.raw")]), token);
        RoutingActionPreview fourthPreview = await work.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal(RoutingActionStatus.Recorded, (await work.ReevaluateAsync(assetId, destinationId, fourthPreview.ConfirmationToken, token)).Status);
        Assert.Equal("Superseded", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"Id\" = @capture", supersededDeliveryId, token));
        await CreateDeliveryStore(testDatabase.IngestOptions).FinishAsync(initialClaim with { DeliveryId = supersededDeliveryId },
            new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite), token);
        Assert.Equal("Superseded", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"Id\" = @capture", supersededDeliveryId, token));
        Guid retryDeliveryId = await GuidScalarAsync(database, "SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset ORDER BY \"CreatedAt\" DESC LIMIT 1", assetId, token);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET \"State\" = 'Failed' WHERE \"Id\" = {retryDeliveryId}", token);
        database.ChangeTracker.Clear();
        Assert.Equal(RoutingActionStatus.Queued, (await work.RetryAsync(retryDeliveryId, token)).Status);
        Assert.Equal("Pending", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"Id\" = @capture", retryDeliveryId, token));
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET \"State\" = 'Ambiguous' WHERE \"Id\" = {retryDeliveryId}", token);
        database.ChangeTracker.Clear();
        RoutingActionPreview ambiguousPreview = await work.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal(RoutingActionStatus.BlockedReconciliation, (await work.ReevaluateAsync(assetId, destinationId, ambiguousPreview.ConfirmationToken, token)).Status);
        Assert.Equal(4, await ScalarAsync(database, "SELECT count(*) FROM routing_decisions WHERE \"AssetId\" = @asset", assetId, token, ("asset", assetId)));
        Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM routing_actions WHERE \"AssetId\" = @asset AND \"Kind\" = 'Retry' AND \"Result\" = 'queued'", assetId, token, ("asset", assetId)));

        Guid laterCaptureId = Guid.NewGuid(), laterAssetId = Guid.NewGuid();
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("old-path", "Old successful path", 0, true, [], "FIRST", "file.raw")]), token);
        await AddReleasedAssetAsync(database, sourceId, laterCaptureId, laterAssetId, "other.raw", token);
        Assert.True(await processor.ProcessOneAsync(DateTimeOffset.UtcNow, token));
        Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM routing_decisions WHERE \"AssetId\" = @asset AND \"Kind\" = 'InvalidTarget' AND \"TargetPath\" = 'FIRST/file.raw'", laterAssetId, token, ("asset", laterAssetId)));
    }

    [Fact]
    public async Task Concurrent_rule_edit_and_release_freeze_one_complete_rule_revision()
    {
        string? connection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
        await using RoutingTestDatabase testDatabase = await RoutingTestDatabase.CreateAsync(connection, TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid(), assetId = Guid.NewGuid();
        await using (var security = new ShutterSpoolDbContext(testDatabase.SecurityOptions))
        {
            security.AddRange(
                new SourceEntity { Id = sourceId, Name = "synthetic", },
                new DestinationEntity { Id = destinationId, Name = "destination", });
            await security.SaveChangesAsync(token);
        }

        RoutingRule[] before = [new("before", "Before", 0, true, [], "before", "{suppliedName}")];
        RoutingRule[] after = [new("after", "After", 0, true, [], "after", "{suppliedName}"), new("later", "Later", 1, false, [], "later", "{suppliedName}")];
        await using (var database = new ShutterSpoolDbContext(testDatabase.IngestOptions))
        {
            await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true, before), token);
            await InsertAssetAsync(database, assetId, sourceId, "frame.CR3", "RAW", token);
            DateTimeOffset anchor = DateTimeOffset.UtcNow;
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO captures (\"Id\", \"SourceId\", \"NormalizedStem\", \"LastPrimaryIngestedAt\") VALUES ({captureId}, {sourceId}, 'FRAME', {anchor})", token);
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({assetId}, {captureId}, 'RAW', true, 'test')", token);
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task edit = Task.Run(async () =>
        {
            await using var database = new ShutterSpoolDbContext(testDatabase.IngestOptions);
            await gate.Task.WaitAsync(token);
            await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true, after), token);
        }, token);
        Task release = Task.Run(async () =>
        {
            await using var database = new ShutterSpoolDbContext(testDatabase.IngestOptions);
            await gate.Task.WaitAsync(token);
            Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System)).ReleaseNowAsync(captureId, 0, token)).Succeeded);
        }, token);
        gate.SetResult();
        await Task.WhenAll(edit, release);
        await using var verify = new ShutterSpoolDbContext(testDatabase.IngestOptions);
        string snapshotJson = await TextAsync(verify, "SELECT \"RoutingSnapshotJson\"::text FROM captures WHERE \"Id\" = @capture", captureId, token);
        RoutingRule[] frozen = RoutingRuleJson.ReadSnapshot(snapshotJson).Single().Rules;
        string[] frozenIds = [.. frozen.Select(rule => rule.Id)];
        Assert.True(frozenIds.SequenceEqual(before.Select(rule => rule.Id)) || frozenIds.SequenceEqual(after.Select(rule => rule.Id)));
    }

    [Fact]
    public async Task Reevaluate_is_blocked_until_the_capture_has_released()
    {
        string? connection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
        await using RoutingTestDatabase testDatabase = await RoutingTestDatabase.CreateAsync(connection, TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid(), assetId = Guid.NewGuid();
        await using (var security = new ShutterSpoolDbContext(testDatabase.SecurityOptions))
        {
            security.AddRange(
                new SourceEntity { Id = sourceId, Name = "synthetic", },
                new DestinationEntity { Id = destinationId, Name = "destination", });
            await security.SaveChangesAsync(token);
        }
        await using var database = new ShutterSpoolDbContext(testDatabase.IngestOptions);
        await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
            [new("all", "All", 0, true, [], "folder", "{suppliedName}")]), token);
        await InsertAssetAsync(database, assetId, sourceId, "frame.raw", "RAW", token);
        DateTimeOffset anchor = DateTimeOffset.UtcNow;
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO captures (\"Id\", \"SourceId\", \"NormalizedStem\", \"LastPrimaryIngestedAt\") VALUES ({captureId}, {sourceId}, 'FRAME', {anchor})", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({assetId}, {captureId}, 'RAW', true, 'test')", token);
        var work = CreateActions(database, new MemoryOriginalReader([1, 2, 3]));
        RoutingActionPreview preview = await work.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal(RoutingActionStatus.BlockedNotReleased, preview.Status);
        Assert.Equal(RoutingActionStatus.BlockedNotReleased, (await work.ReevaluateAsync(assetId, destinationId, preview.ConfirmationToken, token)).Status);
        Assert.Equal(0, await ScalarAsync(database, "SELECT count(*) FROM routing_decisions WHERE \"CaptureId\" = @capture", captureId, token));
        Assert.Equal(0, await ScalarAsync(database, "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset", assetId, token, ("asset", assetId)));
        Assert.Equal(0, await ScalarAsync(database, "SELECT count(*) FROM routing_actions WHERE \"AssetId\" = @asset", assetId, token, ("asset", assetId)));
    }

    private static Task<int> InsertAssetAsync(ShutterSpoolDbContext database, Guid id, Guid sourceId, string name, string role, CancellationToken token) => database.Database.ExecuteSqlInterpolatedAsync($"""
        INSERT INTO assets ("Id", "SourceId", "OriginalName", "SpoolKey", "Size", "Sha256", "IngestedAt", "RecoveryCorrelation", "State", "MetadataState", "MetadataRole", "DetectedMediaType")
        VALUES ({id}, {sourceId}, {name}, {"spool/" + id.ToString("N")}, 17, {new string('a', 64)}, {DateTimeOffset.UtcNow}, {Guid.NewGuid()}, 'Spooled', 'Complete', {role}, 'image/jpeg')
        """, token);

    private static async Task AddReleasedAssetAsync(ShutterSpoolDbContext database, Guid sourceId, Guid captureId, Guid assetId, string name, CancellationToken token,
        Guid? collectionId = null)
    {
        await InsertAssetAsync(database, assetId, sourceId, name, "RAW", token);
        DateTimeOffset anchor = DateTimeOffset.UtcNow;
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO captures (\"Id\", \"SourceId\", \"NormalizedStem\", \"LastPrimaryIngestedAt\") VALUES ({captureId}, {sourceId}, {name.ToUpperInvariant()}, {anchor})", token);
        if (collectionId is Guid collection)
            await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE captures SET \"CollectionId\" = {collection} WHERE \"Id\" = {captureId}", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({assetId}, {captureId}, 'RAW', true, 'test')", token);
        Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System)).ReleaseNowAsync(captureId, 0, token)).Succeeded);
    }

    private static async Task<int> ScalarAsync(ShutterSpoolDbContext database, string sql, Guid captureId, CancellationToken token, params (string Name, Guid Value)[] parameters)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var capture = command.CreateParameter(); capture.ParameterName = "capture"; capture.Value = captureId; command.Parameters.Add(capture);
        foreach ((string name, Guid value) in parameters) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(token);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> TextAsync(ShutterSpoolDbContext database, string sql, Guid captureId, CancellationToken token)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter(); parameter.ParameterName = "capture"; parameter.Value = captureId; command.Parameters.Add(parameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(token);
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<Guid> GuidScalarAsync(ShutterSpoolDbContext database, string sql, Guid assetId, CancellationToken token,
        params (string Name, Guid Value)[] parameters)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter(); parameter.ParameterName = "asset"; parameter.Value = assetId; command.Parameters.Add(parameter);
        foreach ((string name, Guid value) in parameters) { var extra = command.CreateParameter(); extra.ParameterName = name; extra.Value = value; command.Parameters.Add(extra); }
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(token);
        return (Guid)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<Guid?> NullableGuidScalarAsync(ShutterSpoolDbContext database, string sql, Guid deliveryId, CancellationToken token)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter(); parameter.ParameterName = "delivery"; parameter.Value = deliveryId; command.Parameters.Add(parameter);
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(token);
        object? value = await command.ExecuteScalarAsync(token);
        return value is Guid id ? id : null;
    }

    private static RoutingValue Json(string value) => new RoutingValue(Text: value);

    private static EfDeliveryWorkProcessor CreateDeliveryStore(DbContextOptions<ShutterSpoolDbContext> options) =>
        new(new TestDbFactory(options), TimeProvider.System, new DeliveryWorkerSettings(),
            new ShutterSpool.Infrastructure.Security.SecretProtector(Microsoft.Extensions.Options.Options.Create(
                new ShutterSpool.Infrastructure.Security.CredentialProtectionOptions
                { EncryptionKey = Convert.ToBase64String(new byte[32]) })));

    private static async Task<ClaimedDelivery> CompleteConfirmedAsync(DbContextOptions<ShutterSpoolDbContext> options,
        Guid deliveryId, CancellationToken token)
    {
        EfDeliveryWorkProcessor store = CreateDeliveryStore(options);
        ClaimedDelivery claim = Assert.IsType<ClaimedDelivery>(await store.ClaimAsync(token));
        Assert.Equal(deliveryId, claim.DeliveryId);
        Assert.False(claim.Reconcile);
        await store.FinishAsync(claim,
            new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite), token);
        return claim;
    }

    private static async Task CompleteAfterReconciliationAsync(DbContextOptions<ShutterSpoolDbContext> options,
        Guid deliveryId, CancellationToken token)
    {
        EfDeliveryWorkProcessor store = CreateDeliveryStore(options);
        ClaimedDelivery claim = Assert.IsType<ClaimedDelivery>(await store.ClaimAsync(token));
        Assert.Equal(deliveryId, claim.DeliveryId);
        await store.FinishAsync(claim, new(DeliveryResultKind.Uncertain, ErrorCode: "SYNTHETIC_UNCERTAIN"), token);
        ClaimedDelivery reconciliation = Assert.IsType<ClaimedDelivery>(await store.ClaimAsync(token));
        Assert.Equal(deliveryId, reconciliation.DeliveryId);
        Assert.True(reconciliation.Reconcile);
        await store.FinishReconciliationAsync(reconciliation,
            new(DeliveryReconciliationKind.Confirmed, DeliveryConfirmationEvidenceLevel.ProviderReceipt), token);
    }

    private sealed class TestDbFactory(DbContextOptions<ShutterSpoolDbContext> options) : IDbContextFactory<ShutterSpoolDbContext>
    {
        public ShutterSpoolDbContext CreateDbContext() => new(options);
    }

    private sealed class RoutingTestDatabase : IAsyncDisposable
    {
        private readonly NpgsqlConnection admin;
        private readonly string name;
        private RoutingTestDatabase(NpgsqlConnection admin, string name, DbContextOptions<ShutterSpoolDbContext> ingestOptions,
            DbContextOptions<ShutterSpoolDbContext> securityOptions)
        {
            this.admin = admin;
            this.name = name;
            IngestOptions = ingestOptions;
            SecurityOptions = securityOptions;
        }

        public DbContextOptions<ShutterSpoolDbContext> IngestOptions { get; }
        public DbContextOptions<ShutterSpoolDbContext> SecurityOptions { get; }

        public static async Task<RoutingTestDatabase> CreateAsync(string adminConnection, CancellationToken token)
        {
            string name = $"m106_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync(token);
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync(token);
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = name }.ConnectionString;
            var ingest = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var security = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            await using (var securityDb = new ShutterSpoolDbContext(security)) await securityDb.Database.MigrateAsync(token);
            await using (var ingestDb = new ShutterSpoolDbContext(ingest)) await ingestDb.Database.MigrateAsync(token);
            return new(admin, name, ingest, security);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
            await admin.DisposeAsync();
        }
    }

    private sealed class MemoryOriginalReader(byte[] bytes) : ShutterSpool.Application.Spool.ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class MissingOriginalReader : ShutterSpool.Application.Spool.ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken) =>
            throw new IOException("Synthetic original unavailable.");
    }
}
