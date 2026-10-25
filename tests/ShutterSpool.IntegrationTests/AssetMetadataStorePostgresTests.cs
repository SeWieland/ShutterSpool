using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Metadata;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Persistence;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class AssetMetadataStorePostgresTests
{
    [Fact]
    public async Task Postgres_live_parse_renews_lease_past_its_original_expiry()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m102_{Guid.NewGuid():N}";
        string spoolRoot = Path.Combine(Path.GetTempPath(), databaseName);
        Directory.CreateDirectory(spoolRoot);
        string testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        try
        {
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(testConnection).Options;
            Guid assetId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await new DatabaseInitializer(database).InitializeAsync(spoolRoot, TestContext.Current.CancellationToken);
                Guid sourceId = await AddSourceAsync(database, TestContext.Current.CancellationToken);
                assetId = await CommitAssetAsync(database, sourceId, TestContext.Current.CancellationToken);
            }

            var parser = new WaitingParser();
            await using var workerContext = new ShutterSpoolDbContext(options);
            var clock = new ManualClock(DateTimeOffset.UtcNow);
            TimeSpan leaseDuration = TimeSpan.FromMilliseconds(600);
            var service = new MetadataProcessingService(
                new EfAssetMetadataStore(workerContext, leaseDuration),
                new MemoryOriginalReader(), parser, clock, TimeSpan.FromMilliseconds(100));
            DateTimeOffset originalLeaseUntil = clock.GetUtcNow() + leaseDuration;
            Task<bool> processing = service.ProcessOneAsync(TestContext.Current.CancellationToken);
            await parser.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            try
            {
                clock.Advance(TimeSpan.FromMilliseconds(250));
                DateTimeOffset renewedLeaseUntil = await WaitForLeaseExtensionAsync(
                    options, assetId, originalLeaseUntil, TestContext.Current.CancellationToken);

                DateTimeOffset afterOriginalExpiry = originalLeaseUntil.AddMilliseconds(1);
                Assert.True(renewedLeaseUntil > afterOriginalExpiry);
                clock.Advance(afterOriginalExpiry - clock.GetUtcNow());

                await using var contenderContext = new ShutterSpoolDbContext(options);
                Assert.Null(await new EfAssetMetadataStore(contenderContext).ClaimAsync(clock.GetUtcNow(), TestContext.Current.CancellationToken));
            }
            finally
            {
                parser.Release.TrySetResult();
            }

            Assert.True(await processing);
            await using var verifyContext = new ShutterSpoolDbContext(options);
            await verifyContext.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var verify = verifyContext.Database.GetDbConnection().CreateCommand();
            verify.CommandText = "SELECT \"MetadataState\" FROM assets WHERE \"Id\" = @id";
            var parameter = verify.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = assetId;
            verify.Parameters.Add(parameter);
            Assert.Equal(nameof(MetadataOutcome.Complete), await verify.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            if (Directory.Exists(spoolRoot)) Directory.Delete(spoolRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Postgres_claims_once_reclaims_expired_work_and_rejects_stale_completion()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m102_{Guid.NewGuid():N}";
        string spoolRoot = Path.Combine(Path.GetTempPath(), databaseName);
        Directory.CreateDirectory(spoolRoot);
        string testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        try
        {
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(testConnection).Options;
            Guid retryAssetId = Guid.NewGuid();
            Guid sourceId;
            await using (var database = new ShutterSpoolDbContext(options))
            {
                await new DatabaseInitializer(database).InitializeAsync(spoolRoot, TestContext.Current.CancellationToken);
                sourceId = await AddSourceAsync(database, TestContext.Current.CancellationToken);
                var journal = new EfIngestJournal(database);
                var operation = new IngestOperation(Guid.NewGuid(), Guid.NewGuid(), sourceId, "synthetic.jpg", "partial", IngestOperationState.Receiving);
                await journal.CreateAsync(operation, TestContext.Current.CancellationToken);
                await journal.MarkPromotedAsync(operation.Id, new SpoolWrite("partial", "originals/test.jpg", 3, "abc"), TestContext.Current.CancellationToken);
                await journal.CommitAsync(operation.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            MetadataWork firstClaim;
            await using (var firstContext = new ShutterSpoolDbContext(options))
            await using (var secondContext = new ShutterSpoolDbContext(options))
            {
                var firstStore = new EfAssetMetadataStore(firstContext);
                var secondStore = new EfAssetMetadataStore(secondContext);
                MetadataWork?[] claims = await Task.WhenAll(
                    firstStore.ClaimAsync(now, TestContext.Current.CancellationToken),
                    secondStore.ClaimAsync(now, TestContext.Current.CancellationToken));
                firstClaim = Assert.IsType<MetadataWork>(Assert.Single(claims, claim => claim is not null));
                Assert.Null(await firstStore.ClaimAsync(now, TestContext.Current.CancellationToken));
            }

            await using var expiredContext = new ShutterSpoolDbContext(options);
            var expiredStore = new EfAssetMetadataStore(expiredContext);
            Assert.True(await expiredStore.RenewAsync(firstClaim, now.AddMinutes(4), TestContext.Current.CancellationToken));
            await using var contenderContext = new ShutterSpoolDbContext(options);
            Assert.Null(await new EfAssetMetadataStore(contenderContext).ClaimAsync(now.AddMinutes(6), TestContext.Current.CancellationToken));
            MetadataWork reclaimed = Assert.IsType<MetadataWork>(await expiredStore.ClaimAsync(now.AddMinutes(10), TestContext.Current.CancellationToken));
            Assert.Equal(firstClaim.AssetId, reclaimed.AssetId);
            Assert.NotEqual(firstClaim.LeaseId, reclaimed.LeaseId);

            AssetMetadata metadata = new(
                MetadataOutcome.Complete,
                "image/jpeg",
                "JPEG",
                new CaptureTime(new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Unspecified), TimeSpan.FromHours(2), 3, "ExifSubIFD.DateTimeOriginal", false),
                new EmbeddedValue("Synthetic Camera", "Exif IFD0.Make"), null, null, false, null);
            await expiredStore.CompleteAsync(firstClaim, metadata, TestContext.Current.CancellationToken);

            await expiredContext.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var read = expiredContext.Database.GetDbConnection().CreateCommand();
            read.CommandText = "SELECT \"MetadataState\", \"CaptureTimeLocal\", \"CaptureTimeOffset\", \"CameraMake\" FROM assets WHERE \"Id\" = @id";
            var idParameter = read.CreateParameter();
            idParameter.ParameterName = "id";
            idParameter.Value = reclaimed.AssetId;
            read.Parameters.Add(idParameter);
            await using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("Processing", reader.GetString(0));
            Assert.True(await reader.IsDBNullAsync(1, TestContext.Current.CancellationToken));
            Assert.True(await reader.IsDBNullAsync(2, TestContext.Current.CancellationToken));
            Assert.True(await reader.IsDBNullAsync(3, TestContext.Current.CancellationToken));
            await reader.DisposeAsync();

            await expiredStore.CompleteAsync(reclaimed, metadata, TestContext.Current.CancellationToken);
            await using var verify = expiredContext.Database.GetDbConnection().CreateCommand();
            verify.CommandText = "SELECT \"MetadataState\", \"CaptureTimeLocal\", \"CaptureTimeOffset\", \"CameraMake\" FROM assets WHERE \"Id\" = @id";
            var verifyId = verify.CreateParameter();
            verifyId.ParameterName = "id";
            verifyId.Value = reclaimed.AssetId;
            verify.Parameters.Add(verifyId);
            await using var verified = await verify.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await verified.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(nameof(MetadataOutcome.Complete), verified.GetString(0));
            Assert.Equal(new DateTime(2025, 2, 3, 4, 5, 6), verified.GetDateTime(1));
            Assert.Equal(TimeSpan.FromHours(2), verified.GetFieldValue<TimeSpan>(2));
            Assert.Equal("Synthetic Camera", verified.GetString(3));
            await verified.DisposeAsync();

            await using (var retryContext = new ShutterSpoolDbContext(options))
            {
                var retryJournal = new EfIngestJournal(retryContext);
                var retryOperation = new IngestOperation(Guid.NewGuid(), retryAssetId, sourceId, "retry.jpg", "retry-partial", IngestOperationState.Receiving);
                await retryJournal.CreateAsync(retryOperation, TestContext.Current.CancellationToken);
                await retryJournal.MarkPromotedAsync(retryOperation.Id, new SpoolWrite("retry-partial", "originals/retry.jpg", 3, "def"), TestContext.Current.CancellationToken);
                await retryJournal.CommitAsync(retryOperation.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            }

            MetadataWork retryWork = Assert.IsType<MetadataWork>(await expiredStore.ClaimAsync(now.AddMinutes(10), TestContext.Current.CancellationToken));
            Assert.Equal(retryAssetId, retryWork.AssetId);
            DateTimeOffset retryAt = now.AddMinutes(10);
            await expiredStore.RetryAsync(retryWork, retryAt, TestContext.Current.CancellationToken);
            Assert.Null(await expiredStore.ClaimAsync(retryAt, TestContext.Current.CancellationToken));
            Assert.Null(await expiredStore.ClaimAsync(retryAt.AddSeconds(4), TestContext.Current.CancellationToken));
            MetadataWork retried = Assert.IsType<MetadataWork>(await expiredStore.ClaimAsync(retryAt.AddSeconds(6), TestContext.Current.CancellationToken));
            Assert.Equal(retryAssetId, retried.AssetId);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            if (Directory.Exists(spoolRoot)) Directory.Delete(spoolRoot, recursive: true);
        }
    }

    private static async Task<Guid> AddSourceAsync(ShutterSpoolDbContext database, CancellationToken cancellationToken)
    {
        Guid id = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = id, Name = $"synthetic-{id:N}" });
        database.SourceCredentials.Add(new SourceCredentialEntity
        {
            SourceId = id,
            Username = $"camera-{id:N}",
            VirtualRoot = $"sources/{id:N}",
            SecretVerifier = new byte[32],
        });
        await database.SaveChangesAsync(cancellationToken);
        return id;
    }

    private static async Task<Guid> CommitAssetAsync(ShutterSpoolDbContext database, Guid sourceId, CancellationToken cancellationToken)
    {
        Guid assetId = Guid.NewGuid();
        var journal = new EfIngestJournal(database);
        var operation = new IngestOperation(Guid.NewGuid(), assetId, sourceId, "slow.jpg", "partial", IngestOperationState.Receiving);
        await journal.CreateAsync(operation, cancellationToken);
        await journal.MarkPromotedAsync(operation.Id, new SpoolWrite("partial", "originals/slow.jpg", 3, "abc"), cancellationToken);
        await journal.CommitAsync(operation.Id, DateTimeOffset.UtcNow, cancellationToken);
        return assetId;
    }

    private static async Task<DateTimeOffset> WaitForLeaseExtensionAsync(
        DbContextOptions<ShutterSpoolDbContext> options,
        Guid assetId,
        DateTimeOffset originalLeaseUntil,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var database = new ShutterSpoolDbContext(options);
            DateTimeOffset? leaseUntil = await database.Assets
                .Where(asset => asset.Id == assetId)
                .Select(asset => asset.MetadataLeaseUntil)
                .SingleAsync(deadline.Token);
            if (leaseUntil > originalLeaseUntil) return leaseUntil.Value;

            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
        }
    }

    private sealed class MemoryOriginalReader : ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
    }

    private sealed class WaitingParser : IMediaMetadataParser
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AssetMetadata> ParseAsync(Stream original, string originalName, long size, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new AssetMetadata(MetadataOutcome.Complete, "image/jpeg", "JPEG", null, null, null, null, false, null);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcDateTime.Ticks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }
}
