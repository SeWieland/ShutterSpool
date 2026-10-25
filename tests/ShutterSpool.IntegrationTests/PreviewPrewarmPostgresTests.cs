using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;
using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Application.Preview;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Preview;
using ShutterSpool.Infrastructure.Spool;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class PreviewPrewarmPostgresTests
{
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0, 1, 0, 1,
        3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xff, 0xd9];
    private static readonly byte[] WebP = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
    private static readonly DateTime CaptureTime = new(2025, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

    [Fact]
    public async Task Grouped_JPEG_prewarms_selected_profiles_and_survives_outage_and_restart()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m104warm_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        string root = Path.Combine(Path.GetTempPath(), databaseName);
        try
        {
            Directory.CreateDirectory(root);
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            Guid sourceId = Guid.NewGuid(), rawId = Guid.NewGuid(), jpegId = Guid.NewGuid();
            string spoolKey = $"spool/{jpegId:N}";
            await using var database = new ShutterSpoolDbContext(options);
            await new DatabaseInitializer(database).InitializeAsync(root, TestContext.Current.CancellationToken);
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-preview-source" });
            database.SourceCredentials.Add(new SourceCredentialEntity
            {
                SourceId = sourceId,
                Username = "synthetic-preview-camera",
                VirtualRoot = "sources/preview",
                SecretVerifier = new byte[32],
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, spoolKey))!);
            await File.WriteAllBytesAsync(Path.Combine(root, spoolKey), Jpeg, TestContext.Current.CancellationToken);
            await AddAssetAsync(database, rawId, sourceId, $"spool/{rawId:N}", "synthetic.arw", 4, "RAW");
            Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await CountJobsAsync(database));
            await AddJpegAsync(database, jpegId, sourceId, spoolKey);
            Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, await CountJobsAsync(database));
            Assert.Equal(1, await database.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM captures").SingleAsync(TestContext.Current.CancellationToken));

            var settings = new PreviewSettings { PrewarmMode = PreviewPrewarmMode.Off };
            var store = new EfPreviewPrewarmStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert.Null(await store.ClaimAsync(settings, now, TestContext.Current.CancellationToken));

            settings.PrewarmMode = PreviewPrewarmMode.GridOnly;
            CaptureEntity capture = await database.Captures.SingleAsync(TestContext.Current.CancellationToken);
            capture.RetentionDisposition = "Trash";
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Null(await store.ClaimAsync(settings, now, TestContext.Current.CancellationToken));

            capture.RetentionDisposition = "Active";
            capture.RetentionPaused = true;
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Null(await store.ClaimAsync(settings, now, TestContext.Current.CancellationToken));

            capture.RetentionPaused = false;
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            PreviewPrewarmWork grid = Assert.IsType<PreviewPrewarmWork>(await store.ClaimAsync(settings, now, TestContext.Current.CancellationToken));
            Assert.Equal(PreviewProfile.Grid, grid.Profile);
            var handler = new SyntheticImaginaryHandler();
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ShutterSpool:Preview:CacheRootPath"] = Path.Combine(root, "previews"),
            }).Build();
            using var gate = new PreviewGenerationGate(settings);
            var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = root, ReservedBytes = 0 });
            var previews = new PreviewService(new EfPreviewSourceResolver(database), new FileSystemPreviewCache(configuration),
                new ImaginaryPreviewGenerator(spool, http, settings, gate), settings);
            Assert.Equal(PreviewWarmOutcome.Ready, await previews.WarmAsync(grid.CaptureId, grid.AssetId, grid.Profile, TestContext.Current.CancellationToken));
            Assert.True(await store.FinishAsync(grid, PreviewWarmOutcome.Ready, now, TestContext.Current.CancellationToken));
            Assert.Null(await store.ClaimAsync(settings, now, TestContext.Current.CancellationToken));
            Assert.Equal(1, handler.Requests);

            settings.PrewarmMode = PreviewPrewarmMode.Full;
            PreviewPrewarmWork detail = Assert.IsType<PreviewPrewarmWork>(await store.ClaimAsync(settings, now, TestContext.Current.CancellationToken));
            Assert.Equal(PreviewProfile.Detail, detail.Profile);
            handler.FailNext = true;
            Assert.Equal(PreviewWarmOutcome.Retry, await previews.WarmAsync(detail.CaptureId, detail.AssetId, detail.Profile, TestContext.Current.CancellationToken));
            Assert.True(await store.FinishAsync(detail, PreviewWarmOutcome.Retry, now, TestContext.Current.CancellationToken));
            Assert.Null(await store.ClaimAsync(settings, now, TestContext.Current.CancellationToken));

            // A new store instance sees the durable retry after the outage.
            await using var restarted = new ShutterSpoolDbContext(options);
            var restartedStore = new EfPreviewPrewarmStore(restarted);
            PreviewPrewarmWork retried = Assert.IsType<PreviewPrewarmWork>(await restartedStore.ClaimAsync(settings, now.AddMinutes(1), TestContext.Current.CancellationToken));
            Assert.Equal(detail.AssetId, retried.AssetId);
            handler.MalformedNext = true;
            Assert.Equal(PreviewWarmOutcome.Retry, await previews.WarmAsync(retried.CaptureId, retried.AssetId, retried.Profile, TestContext.Current.CancellationToken));
            Assert.True(await restartedStore.FinishAsync(retried, PreviewWarmOutcome.Retry, now.AddMinutes(1), TestContext.Current.CancellationToken));
            PreviewPrewarmWork recoveredOutput = Assert.IsType<PreviewPrewarmWork>(await restartedStore.ClaimAsync(settings, now.AddMinutes(2), TestContext.Current.CancellationToken));
            Assert.Equal(PreviewWarmOutcome.Ready, await previews.WarmAsync(recoveredOutput.CaptureId, recoveredOutput.AssetId, recoveredOutput.Profile, TestContext.Current.CancellationToken));
            Assert.True(await restartedStore.FinishAsync(recoveredOutput, PreviewWarmOutcome.Ready, now.AddMinutes(2), TestContext.Current.CancellationToken));
            Assert.Null(await restartedStore.ClaimAsync(settings, now.AddMinutes(2), TestContext.Current.CancellationToken));
            Assert.Equal(4, handler.Requests);
            Assert.Equal(PreviewState.Ready, (await previews.GetAsync(recoveredOutput.CaptureId, PreviewProfile.Detail, TestContext.Current.CancellationToken)).State);
            Assert.Equal(4, handler.Requests);

            settings.GridWidth = 640;
            settings.GenerationQueueTimeoutSeconds = 600;
            settings.GenerationTimeoutSeconds = 600;
            PreviewPrewarmWork interrupted = Assert.IsType<PreviewPrewarmWork>(await store.ClaimAsync(settings, now.AddMinutes(3), TestContext.Current.CancellationToken));
            Assert.Equal(PreviewProfile.Grid, interrupted.Profile);
            await using var competingDatabase = new ShutterSpoolDbContext(options);
            var competingStore = new EfPreviewPrewarmStore(competingDatabase);
            Assert.Null(await competingStore.ClaimAsync(settings, now.AddMinutes(9), TestContext.Current.CancellationToken));
            await using var recoveryDatabase = new ShutterSpoolDbContext(options);
            var recoveryStore = new EfPreviewPrewarmStore(recoveryDatabase);
            PreviewPrewarmWork recovered = Assert.IsType<PreviewPrewarmWork>(await recoveryStore.ClaimAsync(settings, now.AddMinutes(26), TestContext.Current.CancellationToken));
            Assert.Equal(interrupted.AssetId, recovered.AssetId);
            Assert.False(await store.FinishAsync(interrupted, PreviewWarmOutcome.Ready, now.AddMinutes(26), TestContext.Current.CancellationToken));
            Assert.Equal(PreviewWarmOutcome.Ready, await previews.WarmAsync(recovered.CaptureId, recovered.AssetId, recovered.Profile, TestContext.Current.CancellationToken));
            Assert.True(await recoveryStore.FinishAsync(recovered, PreviewWarmOutcome.Ready, now.AddMinutes(26), TestContext.Current.CancellationToken));
            Assert.Equal(5, handler.Requests);
            Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Path.Combine(root, spoolKey), TestContext.Current.CancellationToken));

            Guid replayId = Guid.NewGuid();
            await AddJpegAsync(database, replayId, sourceId, $"spool/{replayId:N}");
            Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, await CountJobsAsync(database));

            byte[] invalid = "not-a-jpeg"u8.ToArray();
            Guid invalidId = Guid.NewGuid();
            string invalidKey = $"spool/{invalidId:N}";
            await File.WriteAllBytesAsync(Path.Combine(root, invalidKey), invalid, TestContext.Current.CancellationToken);
            await AddJpegAsync(database, invalidId, sourceId, invalidKey, "invalid.jpg", invalid.Length);
            Assert.True(await new CaptureGroupingService(new EfCaptureGroupingStore(database)).ProcessOneAsync(TestContext.Current.CancellationToken));
            settings.PrewarmMode = PreviewPrewarmMode.GridOnly;
            PreviewPrewarmWork invalidWork = Assert.IsType<PreviewPrewarmWork>(await store.ClaimAsync(settings, now.AddMinutes(27), TestContext.Current.CancellationToken));
            Assert.Equal(invalidId, invalidWork.AssetId);
            Assert.Equal(PreviewWarmOutcome.Invalid, await previews.WarmAsync(invalidWork.CaptureId, invalidWork.AssetId, invalidWork.Profile, TestContext.Current.CancellationToken));
            Assert.True(await store.FinishAsync(invalidWork, PreviewWarmOutcome.Invalid, now.AddMinutes(27), TestContext.Current.CancellationToken));
            Assert.Null(await store.ClaimAsync(settings, now.AddDays(1), TestContext.Current.CancellationToken));
            Assert.Equal(5, handler.Requests);
            Assert.Equal(invalid, await File.ReadAllBytesAsync(Path.Combine(root, invalidKey), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static Task AddJpegAsync(ShutterSpoolDbContext database, Guid id, Guid sourceId, string spoolKey,
        string originalName = "synthetic.jpg", long? size = null) =>
        AddAssetAsync(database, id, sourceId, spoolKey, originalName, size ?? Jpeg.Length, "JPEG");

    private static Task AddAssetAsync(ShutterSpoolDbContext database, Guid id, Guid sourceId, string spoolKey,
        string originalName, long size, string role)
    {
        var captureTime = new NpgsqlParameter("captureTime", NpgsqlDbType.Timestamp) { Value = CaptureTime };
        return database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO assets (\"Id\", \"SourceId\", \"OriginalName\", \"SpoolKey\", \"Size\", \"Sha256\", \"IngestedAt\", \"RecoveryCorrelation\", \"State\", \"MetadataState\", \"MetadataRole\", \"CaptureTimeLocal\") VALUES ({id}, {sourceId}, {originalName}, {spoolKey}, {size}, {new string('0', 64)}, {DateTimeOffset.UtcNow}, {Guid.NewGuid()}, {"Spooled"}, {"Complete"}, {role}, {captureTime})");
    }

    private static Task<int> CountJobsAsync(ShutterSpoolDbContext database) =>
        database.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM preview_prewarms").SingleAsync();

    private sealed class SyntheticImaginaryHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool FailNext { get; set; }
        public bool MalformedNext { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(Jpeg, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            if (FailNext)
            {
                FailNext = false;
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            }
            if (MalformedNext)
            {
                MalformedNext = false;
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent("RIFF\0\0\0\0WEBP"u8.ToArray()) };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(WebP) };
        }
    }
}
