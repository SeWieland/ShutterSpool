using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Metadata;
using ShutterSpool.Infrastructure.Metadata;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class MetadataPipelinePostgresTests
{
    [Fact]
    public async Task Accepted_original_is_processed_after_ack_and_after_restart()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m102_{Guid.NewGuid():N}";
        string root = Path.Combine(Path.GetTempPath(), databaseName);
        Directory.CreateDirectory(root);
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        try
        {
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = root, ReservedBytes = 0 });
            byte[] bytes = [0xff, 0xd8, 0xff, 0xe0, 0x00, 0x02, 0xff, 0xd9];
            IngestResult accepted;
            await using (var ingestDatabase = new ShutterSpoolDbContext(options))
            {
                await new DatabaseInitializer(ingestDatabase).InitializeAsync(root, TestContext.Current.CancellationToken);
                Guid sourceId = Guid.NewGuid();
                ingestDatabase.Sources.Add(new SourceEntity { Id = sourceId, Name = $"synthetic-{sourceId:N}" });
                ingestDatabase.SourceCredentials.Add(new SourceCredentialEntity
                {
                    SourceId = sourceId,
                    Username = $"camera-{sourceId:N}",
                    VirtualRoot = $"sources/{sourceId:N}",
                    SecretVerifier = new byte[32],
                });
                await ingestDatabase.SaveChangesAsync(TestContext.Current.CancellationToken);
                accepted = await new IngestService(new EfIngestJournal(ingestDatabase), spool, TimeProvider.System)
                    .AcceptAsync(new IngestRequest(sourceId, "misleading.ARW", new MemoryStream(bytes)), TestContext.Current.CancellationToken);
            }

            await using (var restartedDatabase = new ShutterSpoolDbContext(options))
            {
                Assert.Equal("Pending", await StateAsync(restartedDatabase, accepted.Asset.Id));
                var service = new MetadataProcessingService(new EfAssetMetadataStore(restartedDatabase), spool, new MetadataExtractorMediaMetadataParser(), TimeProvider.System);
                Assert.True(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
                Assert.False(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
                Assert.Equal("Partial", await StateAsync(restartedDatabase, accepted.Asset.Id));
            }

            await using Stream original = await spool.OpenReadAsync(accepted.Asset.SpoolKey, TestContext.Current.CancellationToken);
            using var copy = new MemoryStream();
            await original.CopyToAsync(copy, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, copy.ToArray());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> StateAsync(ShutterSpoolDbContext database, Guid assetId)
    {
        await database.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT \"MetadataState\" FROM assets WHERE \"Id\" = @id";
        var id = command.CreateParameter();
        id.ParameterName = "id";
        id.Value = assetId;
        command.Parameters.Add(id);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
