using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Infrastructure.Persistence;
using System.Globalization;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class CaptureGroupingPostgresTests
{
    [Fact]
    public async Task Metadata_rich_other_variants_join_one_capture_without_collapsing_same_name_bytes()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m103_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        try
        {
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            await MigrateSecurityAsync(connection, TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid source = Guid.NewGuid();
            Guid tiff;
            Guid raw;
            Guid jpeg;
            Guid dng;
            Guid changedTiff;
            Guid unknown;
            DateTime shot = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(token);
                tiff = await AddAssetAsync(database, source, "shoot/IMG_0042.tif", "tiff-a", "OTHER", token, captureTime: shot);
                raw = await AddAssetAsync(database, source, "shoot/IMG_0042.arw", "raw-a", "RAW", token, captureTime: shot);
                jpeg = await AddAssetAsync(database, source, "shoot/IMG_0042.jpg", "jpeg-a", "JPEG", token, captureTime: shot);
                dng = await AddAssetAsync(database, source, "shoot/IMG_0042.dng", "dng-a", "OTHER", token, captureTime: shot);
                changedTiff = await AddAssetAsync(database, source, "shoot/IMG_0042.tif", "tiff-b", "OTHER", token, captureTime: shot);
                unknown = await AddAssetAsync(database, source, "shoot/IMG_0042.xmp", "unknown", null, token, outcome: "Unsupported", omitTime: true);
            }

            Assert.Equal(6, await GroupUntilEmptyAsync(options, token));
            await using var verify = new ShutterSpoolDbContext(options);
            Assert.Equal(1, await DistinctCaptureCountAsync(verify, [tiff, raw, jpeg, dng], token));
            Assert.Equal(2, await DistinctCaptureCountAsync(verify, [tiff, raw, jpeg, dng, changedTiff], token));
            Assert.Equal(3, await DistinctCaptureCountAsync(verify, [tiff, raw, jpeg, dng, changedTiff, unknown], token));
            DateTimeOffset otherIngestedAt = await ReadDateTimeOffsetAsync(verify,
                "SELECT \"IngestedAt\" FROM assets WHERE \"Id\" = @capture", dng, token);
            Assert.Equal(otherIngestedAt, await ReadDateTimeOffsetAsync(verify,
                "SELECT \"LastPrimaryIngestedAt\" FROM captures WHERE \"Id\" = @capture",
                await CaptureIdAsync(verify, tiff, token), token));
            Assert.Equal(2, await ScalarAsync(verify, "SELECT count(*) FROM capture_assets WHERE \"CaptureId\" = @capture AND \"Role\" = 'OTHER' AND \"IsPrimary\"",
                token, ("capture", await CaptureIdAsync(verify, tiff, token))));
            Assert.Equal("variant_name_conflict", await TextAsync(verify,
                "SELECT \"GroupingReason\" FROM capture_assets WHERE \"AssetId\" = @asset", changedTiff, token));
            Assert.Equal("missing_or_uncertain_time", await TextAsync(verify,
                "SELECT \"GroupingReason\" FROM capture_assets WHERE \"AssetId\" = @asset", unknown, token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Grouping_waits_for_release_and_does_not_move_a_released_capture_anchor()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m105_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        try
        {
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            await MigrateSecurityAsync(connection, TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid source = Guid.NewGuid();
            Guid raw;
            Guid jpeg;
            DateTimeOffset initialAnchor;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(token);
                raw = await AddAssetAsync(database, source, "lock/FRAME_1.ARW", "lock-raw", "RAW", token);
                await SetIngestedOneDayAgoAsync(database, raw, token);
            }
            Assert.Equal(1, await GroupUntilEmptyAsync(options, token));
            await using (var database = new ShutterSpoolDbContext(options))
                initialAnchor = await ReadDateTimeOffsetAsync(database,
                    "SELECT c.\"LastPrimaryIngestedAt\" FROM captures c JOIN capture_assets ca ON ca.\"CaptureId\" = c.\"Id\" WHERE ca.\"AssetId\" = @capture", raw, token);

            await using (var database = new ShutterSpoolDbContext(options))
            {
                jpeg = await AddAssetAsync(database, source, "lock/FRAME_1.jpg", "lock-jpeg", "JPEG", token);
            }

            var locker = new NpgsqlConnection(connection);
            await locker.OpenAsync(token);
            await using var transaction = await locker.BeginTransactionAsync(token);
            Guid captureId;
            await using (var select = new NpgsqlCommand("SELECT c.\"Id\" FROM captures c JOIN capture_assets ca ON ca.\"CaptureId\" = c.\"Id\" WHERE ca.\"AssetId\" = @asset FOR UPDATE OF c", locker, transaction))
            {
                select.Parameters.AddWithValue("asset", raw);
                captureId = (Guid)(await select.ExecuteScalarAsync(token))!;
            }

            await using var groupingDb = new ShutterSpoolDbContext(options);
            Task<bool> grouping = new CaptureGroupingService(new EfCaptureGroupingStore(groupingDb)).ProcessOneAsync(token);
            await Task.Delay(250, token);
            Assert.False(grouping.IsCompleted);

            DateTimeOffset releasedAt = DateTimeOffset.UtcNow;
            await using (var release = new NpgsqlCommand("UPDATE captures SET \"ReleasedAt\" = @released, \"State\" = 'READY_FOR_RELEASE', \"ReleasedRevision\" = \"Revision\" WHERE \"Id\" = @capture", locker, transaction))
            {
                release.Parameters.AddWithValue("released", releasedAt);
                release.Parameters.AddWithValue("capture", captureId);
                await release.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            await locker.DisposeAsync();

            Assert.True(await grouping);
            await using var verify = new ShutterSpoolDbContext(options);
            Assert.Equal(initialAnchor, await ReadDateTimeOffsetAsync(verify,
                "SELECT \"LastPrimaryIngestedAt\" FROM captures WHERE \"Id\" = @capture", captureId, token));
            Assert.Equal("paired_complement", await TextAsync(verify,
                "SELECT \"GroupingReason\" FROM capture_assets WHERE \"AssetId\" = @asset", jpeg, token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Concurrent_pairing_and_replay_keep_one_capture_and_one_primary_per_role()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m103_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        try
        {
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            await MigrateSecurityAsync(connection, TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            Guid sourceId = Guid.NewGuid();
            Guid rawId;
            Guid jpegId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
                rawId = await AddAssetAsync(database, sourceId, "shoot/IMG_0042.ARW", "raw-bytes", "RAW", TestContext.Current.CancellationToken, null, null);
                jpegId = await AddAssetAsync(database, sourceId, "shoot/img_0042.jpg", "jpeg-bytes", "JPEG", TestContext.Current.CancellationToken);
            }

            await using (var first = new ShutterSpoolDbContext(options))
            await using (var second = new ShutterSpoolDbContext(options))
            {
                await Task.WhenAll(
                    new CaptureGroupingService(new EfCaptureGroupingStore(first)).ProcessOneAsync(TestContext.Current.CancellationToken),
                    new CaptureGroupingService(new EfCaptureGroupingStore(second)).ProcessOneAsync(TestContext.Current.CancellationToken));
            }

            // A concurrent pass may recheck a just-grouped row from an older snapshot.
            // Poll to idle as the worker does before asserting durable convergence.
            await using (var drain = new ShutterSpoolDbContext(options))
            {
                var grouper = new CaptureGroupingService(new EfCaptureGroupingStore(drain));
                if (await grouper.ProcessOneAsync(TestContext.Current.CancellationToken))
                    Assert.False(await grouper.ProcessOneAsync(TestContext.Current.CancellationToken));
            }

            await using (var verify = new ShutterSpoolDbContext(options))
            {
                Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM captures", TestContext.Current.CancellationToken));
                Assert.Equal(2, await ScalarAsync(verify, "SELECT count(*) FROM capture_assets WHERE \"IsPrimary\"", TestContext.Current.CancellationToken));
                Assert.Equal(1, await ScalarAsync(verify, "SELECT count(DISTINCT \"CaptureId\") FROM capture_assets WHERE \"AssetId\" IN (@raw, @jpeg)", TestContext.Current.CancellationToken, ("raw", rawId), ("jpeg", jpegId)));
            }

            Guid replayId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                replayId = await AddAssetAsync(database, sourceId, "shoot/IMG_0042.ARW", "raw-bytes", "RAW", TestContext.Current.CancellationToken);
                var grouper = new CaptureGroupingService(new EfCaptureGroupingStore(database));
                Assert.True(await grouper.ProcessOneAsync(TestContext.Current.CancellationToken));
                Assert.False(await grouper.ProcessOneAsync(TestContext.Current.CancellationToken));
            }

            await using (var verify = new ShutterSpoolDbContext(options))
            {
                Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM captures", TestContext.Current.CancellationToken));
                Assert.Equal(2, await ScalarAsync(verify, "SELECT count(*) FROM capture_assets WHERE \"IsPrimary\"", TestContext.Current.CancellationToken));
                Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM capture_assets WHERE \"AssetId\" = @asset AND \"RelatedAssetId\" = @raw AND \"GroupingReason\" = 'recognized_replay'", TestContext.Current.CancellationToken, ("asset", replayId), ("raw", rawId)));
            }

            Guid unsupportedReplayId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                unsupportedReplayId = await AddAssetAsync(database, sourceId, "shoot/IMG_0042.ARW", "raw-bytes", null,
                    TestContext.Current.CancellationToken, outcome: "Unsupported");
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(TestContext.Current.CancellationToken));
            }
            await using (var verify = new ShutterSpoolDbContext(options))
            {
                Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM captures", TestContext.Current.CancellationToken));
                Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM capture_assets WHERE \"AssetId\" = @asset AND \"RelatedAssetId\" IN (@raw, @replay) AND \"Role\" = 'OTHER' AND \"GroupingReason\" = 'recognized_replay'",
                    TestContext.Current.CancellationToken, ("asset", unsupportedReplayId), ("raw", rawId), ("replay", replayId)));
            }

            Guid conflictingReplayId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                conflictingReplayId = await AddAssetAsync(database, sourceId, "shoot/IMG_0042.ARW", "raw-bytes", "RAW", TestContext.Current.CancellationToken, "Conflicting Camera", "CONFLICT");
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(TestContext.Current.CancellationToken));
            }

            await using (var verify = new ShutterSpoolDbContext(options))
            {
                Assert.Equal(2, await ScalarAsync(verify, "SELECT count(DISTINCT \"CaptureId\") FROM capture_assets WHERE \"AssetId\" IN (@raw, @jpeg, @conflict)", TestContext.Current.CancellationToken, ("raw", rawId), ("jpeg", jpegId), ("conflict", conflictingReplayId)));
                Assert.Equal(0, await ScalarAsync(verify, "SELECT count(*) FROM capture_assets WHERE \"AssetId\" = @asset AND \"RelatedAssetId\" IS NOT NULL", TestContext.Current.CancellationToken, ("asset", conflictingReplayId)));
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Late_pairing_and_uncertain_or_competing_facts_remain_durable_and_conservative()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m103_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        try
        {
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            await MigrateSecurityAsync(connection, TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid source = Guid.NewGuid();
            Guid lateRaw;
            Guid lateJpeg;
            Guid changedBytes;
            Guid crossSource;
            Guid unsupported;
            Guid failed;
            Guid[] timeConflict;
            Guid[] offsetPair;
            Guid[] mixedZone;
            Guid[] fractionalPair;
            Guid[] fractionalConflict;
            Guid[] uncertainTime;
            Guid[] cameraConflict;
            Guid[] ambiguous;
            Guid ambiguousLate;
            Guid unicodeStem;
            Guid followingAsset;

            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(token);
                lateRaw = await AddAssetAsync(database, source, "late/FRAME_1.ARW", "late-raw", "RAW", token);
                await SetIngestedOneDayAgoAsync(database, lateRaw, token);
            }
            Assert.Equal(1, await GroupUntilEmptyAsync(options, token));

            DateTimeOffset completedDue;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                Guid completedCapture = await CaptureIdAsync(database, lateRaw, token);
                completedDue = await ReadDateTimeOffsetAsync(database, "SELECT \"ReleaseDueAt\" FROM captures WHERE \"Id\" = @capture", completedCapture, token);
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE captures SET \"State\" = 'COMPLETE', \"ReleasedAt\" = {DateTimeOffset.UtcNow} WHERE \"Id\" = {completedCapture}", token);
            }

            await using (var database = new ShutterSpoolDbContext(options))
            {
                lateJpeg = await AddAssetAsync(database, source, "late/frame_1.jpg", "late-jpeg", "JPEG", token);
                changedBytes = await AddAssetAsync(database, source, "late/FRAME_1.ARW", "changed-raw", "RAW", token);
                crossSource = await AddAssetAsync(database, Guid.NewGuid(), "late/FRAME_1.ARW", "late-raw", "RAW", token);
                unsupported = await AddAssetAsync(database, source, "other/unsupported.bin", "unsupported", null, token, outcome: "Unsupported");
                failed = await AddAssetAsync(database, source, "other/failed.jpg", "failed", null, token, outcome: "Failed", omitTime: true);

                DateTime baseTime = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
                timeConflict = [
                    await AddAssetAsync(database, source, "time/TIME_1.ARW", "time-raw", "RAW", token, captureTime: baseTime),
                    await AddAssetAsync(database, source, "time/TIME_1.jpg", "time-jpeg", "JPEG", token, captureTime: baseTime.AddMinutes(1))];
                offsetPair = [
                    await AddAssetAsync(database, source, "offset/OFFSET_1.ARW", "offset-raw", "RAW", token, captureTime: baseTime, captureOffset: TimeSpan.FromHours(2)),
                    await AddAssetAsync(database, source, "offset/OFFSET_1.jpg", "offset-jpeg", "JPEG", token, captureTime: baseTime.AddHours(-1), captureOffset: TimeSpan.FromHours(1))];
                mixedZone = [
                    await AddAssetAsync(database, source, "zone/ZONE_1.ARW", "zone-raw", "RAW", token, captureTime: baseTime, captureOffset: TimeSpan.FromHours(2)),
                    await AddAssetAsync(database, source, "zone/ZONE_1.jpg", "zone-jpeg", "JPEG", token, captureTime: baseTime)];
                fractionalPair = [
                    await AddAssetAsync(database, source, "fraction-pair/FRACTION_PAIR_1.ARW", "fraction-raw", "RAW", token, captureTime: baseTime.AddMilliseconds(123), fractionalDigits: 3),
                    await AddAssetAsync(database, source, "fraction-pair/FRACTION_PAIR_1.jpg", "fraction-jpeg", "JPEG", token, captureTime: baseTime.AddTicks(1_234_560), fractionalDigits: 6)];
                fractionalConflict = [
                    await AddAssetAsync(database, source, "fraction-conflict/FRACTION_CONFLICT_1.ARW", "fraction-raw", "RAW", token, captureTime: baseTime.AddMilliseconds(123), fractionalDigits: 3),
                    await AddAssetAsync(database, source, "fraction-conflict/FRACTION_CONFLICT_1.jpg", "fraction-jpeg", "JPEG", token, captureTime: baseTime.AddMilliseconds(124), fractionalDigits: 3)];
                uncertainTime = [
                    await AddAssetAsync(database, source, "uncertain/UNCERTAIN_1.ARW", "uncertain-raw", "RAW", token, uncertainTime: true),
                    await AddAssetAsync(database, source, "uncertain/UNCERTAIN_1.jpg", "uncertain-jpeg", "JPEG", token)];
                cameraConflict = [
                    await AddAssetAsync(database, source, "camera/CAMERA_1.ARW", "camera-raw", "RAW", token, cameraSerial: "CAM-A"),
                    await AddAssetAsync(database, source, "camera/CAMERA_1.jpg", "camera-jpeg", "JPEG", token, cameraSerial: "CAM-B")];
                ambiguous = [
                    await AddAssetAsync(database, source, "collision/COLLISION_1.ARW", "collision-raw-1", "RAW", token),
                    await AddAssetAsync(database, source, "collision/COLLISION_1.ARW", "collision-raw-2", "RAW", token),
                    await AddAssetAsync(database, source, "collision/COLLISION_1.jpg", "collision-jpeg", "JPEG", token)];
                ambiguousLate = await AddAssetAsync(database, source, "collision/COLLISION_1.ARW", "collision-raw-3", "RAW", token);

                string expandedStem = new('\uFB03', 400);
                unicodeStem = await AddAssetAsync(database, source, $"unicode/{expandedStem}.ARW", "unicode-raw", null, token, outcome: "Unsupported");
                followingAsset = await AddAssetAsync(database, source, "unicode/next.bin", "next-other", null, token, outcome: "Unsupported");
            }

            Assert.True(await GroupUntilEmptyAsync(options, token) >= 12);
            Assert.Equal(0, await GroupUntilEmptyAsync(options, token));

            Guid[] selectivelyAmbiguous;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                selectivelyAmbiguous = [
                    await AddAssetAsync(database, source, "selective/SELECTIVE_1.ARW", "selective-a", "RAW", token, cameraSerial: "A"),
                    await AddAssetAsync(database, source, "selective/SELECTIVE_1.ARW", "selective-c", "RAW", token, cameraSerial: "A"),
                    await AddAssetAsync(database, source, "selective/SELECTIVE_1.ARW", "selective-b", "RAW", token, cameraSerial: "B")];
            }
            Assert.Equal(3, await GroupUntilEmptyAsync(options, token));
            await using (var database = new ShutterSpoolDbContext(options))
                selectivelyAmbiguous = [.. selectivelyAmbiguous,
                    await AddAssetAsync(database, source, "selective/SELECTIVE_1.jpg", "selective-k", "JPEG", token, cameraSerial: "A")];
            Assert.Equal(1, await GroupUntilEmptyAsync(options, token));
            await using (var database = new ShutterSpoolDbContext(options))
                selectivelyAmbiguous = [.. selectivelyAmbiguous,
                    await AddAssetAsync(database, source, "selective/SELECTIVE_1.jpg", "selective-j", "JPEG", token, cameraSerial: null)];
            Assert.Equal(1, await GroupUntilEmptyAsync(options, token));

            await using var verify = new ShutterSpoolDbContext(options);
            Assert.Equal(await CaptureIdAsync(verify, lateRaw, token), await CaptureIdAsync(verify, lateJpeg, token));
            Guid completedId = await CaptureIdAsync(verify, lateRaw, token);
            Assert.Equal("COMPLETE", await TextAsync(verify, "SELECT \"State\" FROM captures WHERE \"Id\" = @asset", completedId, token));
            Assert.Equal(completedDue, await ReadDateTimeOffsetAsync(verify, "SELECT \"ReleaseDueAt\" FROM captures WHERE \"Id\" = @capture", completedId, token));
            Assert.NotEqual(await CaptureIdAsync(verify, lateRaw, token), await CaptureIdAsync(verify, changedBytes, token));
            Assert.NotEqual(await CaptureIdAsync(verify, lateRaw, token), await CaptureIdAsync(verify, crossSource, token));
            Assert.Equal(2, await ScalarAsync(verify, "SELECT count(*) FROM capture_assets WHERE \"AssetId\" IN (@asset1, @asset2) AND \"Role\" = 'OTHER' AND \"IsPrimary\" = true", token,
                ("asset1", unsupported), ("asset2", failed)));
            Assert.Equal(2, await DistinctCaptureCountAsync(verify, timeConflict, token));
            Assert.Equal(1, await DistinctCaptureCountAsync(verify, offsetPair, token));
            Assert.Equal(2, await DistinctCaptureCountAsync(verify, mixedZone, token));
            Assert.Equal("paired_complement", await TextAsync(verify,
                "SELECT \"GroupingReason\" FROM capture_assets WHERE \"AssetId\" = @asset", fractionalPair[1], token));
            Assert.Equal(1, await DistinctCaptureCountAsync(verify, fractionalPair, token));
            Assert.Equal(2, await DistinctCaptureCountAsync(verify, fractionalConflict, token));
            Assert.Equal(2, await DistinctCaptureCountAsync(verify, uncertainTime, token));
            Assert.Equal(2, await DistinctCaptureCountAsync(verify, cameraConflict, token));
            Assert.Equal(4, await DistinctCaptureCountAsync(verify, [.. ambiguous, ambiguousLate], token));
            Assert.Equal(5, await DistinctCaptureCountAsync(verify, selectivelyAmbiguous, token));
            Assert.Equal("ambiguous_candidates", await TextAsync(verify,
                "SELECT \"GroupingReason\" FROM capture_assets WHERE \"AssetId\" = @asset", ambiguous[2], token));
            Assert.Equal(1_200, await ScalarAsync(verify, "SELECT char_length(\"NormalizedStem\") FROM captures JOIN capture_assets ON captures.\"Id\" = capture_assets.\"CaptureId\" WHERE capture_assets.\"AssetId\" = @asset", token,
                ("asset", unicodeStem)));
            Assert.NotEqual(await CaptureIdAsync(verify, unicodeStem, token), await CaptureIdAsync(verify, followingAsset, token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Guid> AddAssetAsync(ShutterSpoolDbContext database, Guid sourceId, string name, string sha, string? role, CancellationToken cancellationToken,
        string? cameraModel = "Synthetic Camera", string? cameraSerial = "SYNTH-1", string outcome = "Complete",
        DateTime? captureTime = null, TimeSpan? captureOffset = null, bool uncertainTime = false, bool omitTime = false, int fractionalDigits = 0)
    {
        await EnsureSourceAsync(database.Database.GetDbConnection().ConnectionString, sourceId, cancellationToken);
        Guid assetId = Guid.NewGuid();
        var journal = new EfIngestJournal(database);
        var operation = new IngestOperation(Guid.NewGuid(), assetId, sourceId, name, $"partial-{assetId:N}", IngestOperationState.Receiving);
        await journal.CreateAsync(operation, cancellationToken);
        await journal.MarkPromotedAsync(operation.Id, new SpoolWrite(operation.PartialKey, $"originals/{assetId:N}", 16, sha), cancellationToken);
        await journal.CommitAsync(operation.Id, DateTimeOffset.UtcNow, cancellationToken);
        DateTime? storedTime = omitTime ? null : captureTime ?? new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);
        var captureTimeParameter = new NpgsqlParameter("captureTime", NpgsqlDbType.Timestamp)
        {
            Value = (object?)storedTime ?? DBNull.Value,
        };
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE assets SET "MetadataState" = {outcome}, "MetadataOutcome" = {outcome}, "MetadataRole" = {role},
                "CaptureTimeLocal" = {captureTimeParameter},
                "CaptureTimeOffset" = {captureOffset}, "CaptureTimeFractionalDigits" = {fractionalDigits}, "CaptureTimeUncertain" = {uncertainTime},
                "CameraModel" = {cameraModel}, "CameraSerial" = {cameraSerial}
            WHERE "Id" = {assetId}
            """, cancellationToken);
        return assetId;
    }

    private static async Task EnsureSourceAsync(string connectionString, Guid sourceId, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var configured = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION"));
        connection.Password = configured.Password;
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection.ConnectionString).Options;
        await using var database = new ShutterSpoolDbContext(options);
        if (!await database.Sources.AnyAsync(source => source.Id == sourceId, cancellationToken))
            database.Sources.Add(new SourceEntity {
                Id = sourceId,
                Name = $"synthetic-{sourceId:N}",
            });
        await database.SaveChangesAsync(cancellationToken);
    }

    private static async Task MigrateSecurityAsync(string connectionString, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connectionString).Options;
        await using var database = new ShutterSpoolDbContext(options);
        await database.Database.MigrateAsync(cancellationToken);
    }

    private static async Task<long> ScalarAsync(ShutterSpoolDbContext database, string sql, CancellationToken cancellationToken, params (string Name, Guid Value)[] parameters)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach ((string name, Guid value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<int> GroupUntilEmptyAsync(DbContextOptions<ShutterSpoolDbContext> options, CancellationToken cancellationToken)
    {
        int count = 0;
        while (count < 100)
        {
            await using var database = new ShutterSpoolDbContext(options);
            if (!await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(cancellationToken)) return count;
            count++;
        }
        throw new InvalidOperationException("Capture grouping did not reach an idle state.");
    }

    private static async Task SetIngestedOneDayAgoAsync(ShutterSpoolDbContext database, Guid assetId, CancellationToken cancellationToken) =>
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE assets SET \"IngestedAt\" = {DateTimeOffset.UtcNow.AddDays(-1)} WHERE \"Id\" = {assetId}", cancellationToken);

    private static async Task<Guid> CaptureIdAsync(ShutterSpoolDbContext database, Guid assetId, CancellationToken cancellationToken)
    {
        object? value = await ScalarObjectAsync(database, "SELECT \"CaptureId\" FROM capture_assets WHERE \"AssetId\" = @asset", cancellationToken, ("asset", assetId));
        return (Guid)value!;
    }

    private static async Task<int> DistinctCaptureCountAsync(ShutterSpoolDbContext database, Guid[] assetIds, CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(DISTINCT \"CaptureId\") FROM capture_assets WHERE \"AssetId\" = ANY(@ids)";
        var ids = command.CreateParameter();
        ids.ParameterName = "ids";
        ids.Value = assetIds;
        command.Parameters.Add(ids);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<string> TextAsync(ShutterSpoolDbContext database, string sql, Guid assetId, CancellationToken cancellationToken) =>
        (string)(await ScalarObjectAsync(database, sql, cancellationToken, ("asset", assetId)))!;

    private static async Task<DateTimeOffset> ReadDateTimeOffsetAsync(ShutterSpoolDbContext database, string sql, Guid captureId, CancellationToken cancellationToken)
    {
        object value = (await ScalarObjectAsync(database, sql, cancellationToken, ("capture", captureId)))!;
        return value is DateTimeOffset offset ? offset : new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc));
    }

    private static async Task<object?> ScalarObjectAsync(ShutterSpoolDbContext database, string sql, CancellationToken cancellationToken, params (string Name, Guid Value)[] parameters)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach ((string name, Guid value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
        return await command.ExecuteScalarAsync(cancellationToken);
    }
}
