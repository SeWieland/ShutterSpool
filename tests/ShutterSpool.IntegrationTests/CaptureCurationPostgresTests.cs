using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class CaptureCurationPostgresTests
{
    [Fact]
    public async Task Grouping_without_an_authoritative_source_keeps_the_asset_available_for_retry()
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
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var securityOptions = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid sourceId = Guid.NewGuid();
            await using (var security = new ShutterSpoolDbContext(securityOptions)) await security.Database.MigrateAsync(token);
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(token);
                Guid assetId = await AddAssetAsync(database, sourceId, token);
                await Assert.ThrowsAsync<InvalidOperationException>(() => new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                Assert.Equal(0, await ScalarAsync(database, "SELECT count(*) FROM capture_assets WHERE \"AssetId\" = @id", assetId, token));
                Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM assets WHERE \"Id\" = @id AND \"State\" = 'Spooled'", assetId, token));
            }

            await using (var security = new ShutterSpoolDbContext(securityOptions))
            {
                security.Sources.Add(new SourceEntity {
                    Id = sourceId,
                    Name = "recovered-source",
                });
                await security.SaveChangesAsync(token);
            }
            await using (var database = new ShutterSpoolDbContext(options))
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Source_policy_is_snapshotted_and_delay_anchor_moves_only_for_a_complement()
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
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var securityOptions = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid sourceId = Guid.NewGuid();
            Guid manualSourceId = Guid.NewGuid();
            DateTimeOffset firstIngest = DateTimeOffset.UtcNow.AddDays(-2);
            DateTimeOffset complementIngest = DateTimeOffset.UtcNow.AddMinutes(-10);
            Guid rawId;
            Guid jpegId;
            Guid replayId;
            Guid captureId;
            Guid immediateCaptureId;
            Guid manualCaptureId;
            await using (var security = new ShutterSpoolDbContext(securityOptions))
            {
                await security.Database.MigrateAsync(token);
                security.AddRange(
                    new SourceEntity { Id = sourceId, Name = "delay-source", CurationMode = SourceCurationMode.Delay, CurationDelaySeconds = 3_600 },
                    new SourceEntity { Id = manualSourceId, Name = "manual-source", CurationMode = SourceCurationMode.Manual });
                await security.SaveChangesAsync(token);
            }
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(token);
                rawId = await AddAssetAsync(database, sourceId, token, "shoot/SHOT.CR3", "raw", "RAW", firstIngest);
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                captureId = await CaptureIdAsync(database, rawId, token);
            }

            await using (var security = new ShutterSpoolDbContext(securityOptions))
            {
                SourceEntity source = await security.Sources.SingleAsync(source => source.Id == sourceId, token);
                source.CurationMode = SourceCurationMode.Immediate;
                await security.SaveChangesAsync(token);
            }

            await using (var database = new ShutterSpoolDbContext(options))
            {
                jpegId = await AddAssetAsync(database, sourceId, token, "shoot/shot.jpg", "jpeg", "JPEG", complementIngest);
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                replayId = await AddAssetAsync(database, sourceId, token, "shoot/SHOT.CR3", "raw", "RAW", complementIngest.AddMinutes(5));
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                immediateCaptureId = await AddAssetAsync(database, sourceId, token, "other/IMMEDIATE.CR3", "immediate", "RAW", complementIngest);
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                manualCaptureId = await AddAssetAsync(database, manualSourceId, token, "other/MANUAL.CR3", "manual", "RAW", complementIngest);
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
            }

            await using var verify = new ShutterSpoolDbContext(options);
            var service = new CaptureCurationService(new EfCaptureCurationStore(verify, TimeProvider.System));
            CaptureCurationState delayed = await service.GetAsync(captureId, token) ?? throw new InvalidOperationException();
            Assert.Equal(SourceCurationMode.Delay, delayed.Policy);
            Assert.Equal(3_600, delayed.DelaySeconds);
            Assert.InRange((delayed.ReleaseDueAt!.Value - complementIngest.AddSeconds(3_600)).Duration(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
            Assert.Equal(captureId, await CaptureIdAsync(verify, jpegId, token));
            Assert.Equal(captureId, await CaptureIdAsync(verify, replayId, token));

            CaptureCurationState immediate = await service.GetAsync(await CaptureIdAsync(verify, immediateCaptureId, token), token) ?? throw new InvalidOperationException();
            Assert.Equal(SourceCurationMode.Immediate, immediate.Policy);
            Assert.InRange((immediate.ReleaseDueAt!.Value - complementIngest).Duration(), TimeSpan.Zero, TimeSpan.FromMicroseconds(1));
            Assert.False(immediate.Held);

            Guid manualId = await CaptureIdAsync(verify, manualCaptureId, token);
            CaptureCurationState manual = await service.GetAsync(manualId, token) ?? throw new InvalidOperationException();
            Assert.Equal(SourceCurationMode.Manual, manual.Policy);
            Assert.Null(manual.ReleaseDueAt);
            Assert.True(manual.Held);
            Assert.True((await service.RestoreAsync(manualId, 0, token)).State!.Held);
            Assert.True((await service.ReleaseNowAsync(manualId, 0, token)).Succeeded);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Overdue_capture_serializes_hold_restore_and_duplicate_due_workers()
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
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var securityOptions = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid sourceId = Guid.NewGuid();
            Guid assetId;
            Guid captureId;
            await using (var security = new ShutterSpoolDbContext(securityOptions))
            {
                await security.Database.MigrateAsync(token);
                security.Sources.Add(new SourceEntity {
                    Id = sourceId,
                    Name = "synthetic-source",
                });
                await security.SaveChangesAsync(token);
            }
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await database.Database.MigrateAsync(token);
                assetId = await AddAssetAsync(database, sourceId, token);
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE assets SET \"IngestedAt\" = {DateTimeOffset.UtcNow.AddHours(-2)} WHERE \"Id\" = {assetId}", token);
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                captureId = await CaptureIdAsync(database, assetId, token);
            }

            await using (var database = new ShutterSpoolDbContext(options))
            {
                var service = new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System));
                CaptureMutationResult held = await service.HoldAsync(captureId, 0, token);
                Assert.True(held.Succeeded);
                CaptureCurationState heldState = held.State ?? throw new InvalidOperationException("Hold must return current Capture state.");
                Assert.True(heldState.Held);
                CaptureMutationResult staleRestore = await service.RestoreAsync(captureId, 0, token);
                Assert.False(staleRestore.Succeeded);
                Assert.True(staleRestore.State!.Held);
                Assert.False(await service.ReleaseDueOneAsync(DateTimeOffset.UtcNow, token));
                CaptureMutationResult restored = await service.RestoreAsync(captureId, heldState.Revision, token);
                Assert.True(restored.Succeeded);
                Assert.False(restored.State!.Held);
                Assert.True(restored.State.ReleaseDueAt < DateTimeOffset.UtcNow);
            }

            Task<bool> first;
            Task<bool> second;
            await using (var firstDatabase = new ShutterSpoolDbContext(options))
            await using (var secondDatabase = new ShutterSpoolDbContext(options))
            {
                first = new CaptureCurationService(new EfCaptureCurationStore(firstDatabase, TimeProvider.System)).ReleaseDueOneAsync(DateTimeOffset.UtcNow, token);
                second = new CaptureCurationService(new EfCaptureCurationStore(secondDatabase, TimeProvider.System)).ReleaseDueOneAsync(DateTimeOffset.UtcNow, token);
                bool[] results = await Task.WhenAll(first, second);
                Assert.Equal(1, results.Count(result => result));
            }

            await using var verify = new ShutterSpoolDbContext(options);
            CaptureCurationState state = await new CaptureCurationService(new EfCaptureCurationStore(verify, TimeProvider.System)).GetAsync(captureId, token)
                ?? throw new InvalidOperationException("Grouped Capture disappeared.");
            Assert.Equal("READY_FOR_RELEASE", state.State);
            Assert.NotNull(state.ReleasedAt);
            Assert.Equal(2, state.ReleasedRevision);
            Assert.False(state.Held);
            Assert.Equal(0, await ScalarAsync(verify, "SELECT count(*) FROM captures WHERE \"Id\" = @id AND \"ReleasedAt\" IS NULL", captureId, token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Release_freezes_revision_and_stale_edits_return_conflict()
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
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var securityOptions = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var token = TestContext.Current.CancellationToken;
            Guid sourceId = Guid.NewGuid();
            Guid assetId;
            Guid captureId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await using (var security = new ShutterSpoolDbContext(securityOptions))
                {
                    await security.Database.MigrateAsync(token);
                    security.Sources.Add(new SourceEntity {
                        Id = sourceId,
                        Name = "synthetic-source",
                    });
                    await security.SaveChangesAsync(token);
                }
                await database.Database.MigrateAsync(token);
                assetId = await AddAssetAsync(database, sourceId, token);
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                captureId = await CaptureIdAsync(database, assetId, token);
            }

            await using (var database = new ShutterSpoolDbContext(options))
            {
                var service = new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System));
                CaptureMutationResult edit = await service.UpdateAsync(captureId, 0, new CaptureCuration(4, "Red", ["Portrait"]), token);
                Assert.True(edit.Succeeded);
                CaptureCurationState edited = edit.State ?? throw new InvalidOperationException("A successful edit must return the current state.");
                CaptureMutationResult release = await service.ReleaseNowAsync(captureId, edited.Revision, token);
                Assert.True(release.Succeeded);
                CaptureMutationResult laterEdit = await service.UpdateAsync(captureId, release.State!.Revision,
                    new CaptureCuration(2, "Blue", ["Later"]), token);
                Assert.True(laterEdit.Succeeded);
                Assert.Equal(3, laterEdit.State?.Revision);
                CaptureMutationResult repeatedRelease = await service.ReleaseNowAsync(captureId, 0, token);
                Assert.True(repeatedRelease.Succeeded);
                CaptureMutationResult stale = await service.UpdateAsync(captureId, 0, new CaptureCuration(-1, null, ["Changed"]), token);
                Assert.False(stale.Succeeded);
                Assert.Equal(3, stale.State?.Revision);
            }

            Guid raceAssetId;
            Guid raceCaptureId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                raceAssetId = await AddAssetAsync(database, sourceId, token, "race/OTHER.CR3", "race-bytes");
                Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(token));
                raceCaptureId = await CaptureIdAsync(database, raceAssetId, token);
            }
            await using (var editDatabase = new ShutterSpoolDbContext(options))
            await using (var releaseDatabase = new ShutterSpoolDbContext(options))
            {
                Task<CaptureMutationResult> edit = new CaptureCurationService(new EfCaptureCurationStore(editDatabase, TimeProvider.System))
                    .UpdateAsync(raceCaptureId, 0, new CaptureCuration(5, "Purple", ["Concurrent"]), token);
                Task<CaptureMutationResult> release = new CaptureCurationService(new EfCaptureCurationStore(releaseDatabase, TimeProvider.System))
                    .ReleaseNowAsync(raceCaptureId, 0, token);
                CaptureMutationResult[] results = await Task.WhenAll(edit, release);
                Assert.Single(results, result => result.Succeeded);
                CaptureMutationResult conflict = Assert.Single(results, result => !result.Succeeded);
                Assert.Equal(1, conflict.State?.Revision);
            }

            await using var verify = new ShutterSpoolDbContext(options);
            Assert.Equal(4, await ScalarAsync(verify, "SELECT \"ReleasedRating\" FROM captures WHERE \"Id\" = @id", captureId, token));
            Assert.Equal("Red", await TextAsync(verify, "SELECT \"ReleasedLabel\" FROM captures WHERE \"Id\" = @id", captureId, token));
            Assert.Equal(1, await ScalarAsync(verify, "SELECT \"ReleasedRevision\" FROM captures WHERE \"Id\" = @id", captureId, token));
            Assert.Equal(2, await ScalarAsync(verify, "SELECT \"Rating\" FROM captures WHERE \"Id\" = @id", captureId, token));
            Assert.Equal(3, await ScalarAsync(verify, "SELECT \"Revision\" FROM captures WHERE \"Id\" = @id", captureId, token));
            Assert.Equal("Portrait", await TextAsync(verify, "SELECT \"ReleasedTags\"[1] FROM captures WHERE \"Id\" = @id", captureId, token));
            Assert.Equal("Later", await TextAsync(verify, "SELECT \"Tags\"[1] FROM captures WHERE \"Id\" = @id", captureId, token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Guid> AddAssetAsync(ShutterSpoolDbContext database, Guid sourceId, CancellationToken cancellationToken,
        string name = "shot.CR3", string sha = "synthetic", string role = "RAW", DateTimeOffset? ingestedAt = null)
    {
        Guid assetId = Guid.NewGuid();
        var journal = new EfIngestJournal(database);
        var operation = new IngestOperation(Guid.NewGuid(), assetId, sourceId, name, $"partial-{assetId:N}", IngestOperationState.Receiving);
        await journal.CreateAsync(operation, cancellationToken);
        await journal.MarkPromotedAsync(operation.Id, new SpoolWrite(operation.PartialKey, $"originals/{assetId:N}", 16, sha), cancellationToken);
        await journal.CommitAsync(operation.Id, ingestedAt ?? DateTimeOffset.UtcNow, cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE assets SET \"MetadataState\" = 'Complete', \"MetadataRole\" = {role}, \"CaptureTimeLocal\" = TIMESTAMP '2025-01-01 00:00:00' WHERE \"Id\" = {assetId}", cancellationToken);
        return assetId;
    }

    private static async Task<Guid> CaptureIdAsync(ShutterSpoolDbContext database, Guid assetId, CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"CaptureId\" FROM capture_assets WHERE \"AssetId\" = @asset";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "asset";
        parameter.Value = assetId;
        command.Parameters.Add(parameter);
        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long> ScalarAsync(ShutterSpoolDbContext database, string sql, Guid captureId, CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = captureId;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string> TextAsync(ShutterSpoolDbContext database, string sql, Guid captureId, CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "id";
        parameter.Value = captureId;
        command.Parameters.Add(parameter);
        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
