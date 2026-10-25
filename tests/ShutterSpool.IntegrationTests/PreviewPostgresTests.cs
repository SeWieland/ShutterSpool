using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ShutterSpool.Application.Preview;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Preview;
using ShutterSpool.Infrastructure.Spool;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class PreviewPostgresTests
{
    [Fact]
    public async Task Raw_only_then_JPEG_replay_and_cache_loss_keep_the_current_original_and_capture()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
        string databaseName = $"m104_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        string root = Path.Combine(Path.GetTempPath(), $"m104-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            Guid sourceId = Guid.NewGuid(), captureId = Guid.NewGuid(), rawId = Guid.NewGuid(), jpegId = Guid.NewGuid();
            string spoolKey = $"spool/{jpegId:N}";
            byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0, 1, 0, 1,
                3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xff, 0xd9];
            var handler = new ImaginaryHandler(jpeg);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ShutterSpool:Preview:CacheRootPath"] = Path.Combine(root, "previews"),
            }).Build();
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
            string originalPath = Path.Combine(root, spoolKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            await File.WriteAllBytesAsync(originalPath, jpeg, TestContext.Current.CancellationToken);
            await AddAssetAsync(database, rawId, sourceId, "spool/raw", 3);
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO captures (\"Id\", \"SourceId\", \"NormalizedStem\") VALUES ({captureId}, {sourceId}, {"shot"})", TestContext.Current.CancellationToken);
            await AddMembershipAsync(database, rawId, captureId, "RAW", true);

            var settings = new PreviewSettings();
            using var gate = new PreviewGenerationGate(settings);
            var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = root, ReservedBytes = 0 });
            var previews = new PreviewService(new EfPreviewSourceResolver(database), new FileSystemPreviewCache(configuration),
                new ImaginaryPreviewGenerator(spool, http, settings, gate), settings);
            Assert.Equal(PreviewState.Pending, (await previews.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);

            await AddAssetAsync(database, jpegId, sourceId, spoolKey, jpeg.Length);
            await AddMembershipAsync(database, jpegId, captureId, "JPEG", true);
            PreviewResult first = await previews.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken);
            Assert.Equal(PreviewState.Ready, first.State);
            Assert.Equal(ImaginaryHandler.WebP, first.Content);
            Assert.Equal(1, handler.Requests);

            Guid replayId = Guid.NewGuid();
            await AddAssetAsync(database, replayId, sourceId, "spool/replay", jpeg.Length);
            await AddMembershipAsync(database, replayId, captureId, "JPEG", false);
            Assert.Equal(PreviewState.Ready, (await previews.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);
            Assert.Equal(1, handler.Requests);

            Directory.Delete(Path.Combine(root, "previews"), recursive: true);
            Assert.Equal(PreviewState.Ready, (await previews.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);
            Assert.Equal(2, handler.Requests);
            Assert.Equal(jpeg, await File.ReadAllBytesAsync(originalPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static Task AddAssetAsync(ShutterSpoolDbContext database, Guid id, Guid sourceId, string spoolKey, long size) =>
        database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO assets (\"Id\", \"SourceId\", \"OriginalName\", \"SpoolKey\", \"Size\", \"Sha256\", \"IngestedAt\", \"RecoveryCorrelation\", \"State\", \"MetadataState\") VALUES ({id}, {sourceId}, {"synthetic.jpg"}, {spoolKey}, {size}, {new string('0', 64)}, {DateTimeOffset.UtcNow}, {Guid.NewGuid()}, {"Spooled"}, {"Complete"})");

    private static Task AddMembershipAsync(ShutterSpoolDbContext database, Guid assetId, Guid captureId, string role, bool primary) =>
        database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({assetId}, {captureId}, {role}, {primary}, {"synthetic"})");

    private sealed class ImaginaryHandler(byte[] expectedOriginal) : HttpMessageHandler
    {
        public static readonly byte[] WebP = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Contains("/fit?", request.RequestUri!.PathAndQuery);
            Assert.Equal(expectedOriginal, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(WebP) };
        }
    }
}
