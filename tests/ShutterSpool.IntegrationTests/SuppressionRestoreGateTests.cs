using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Retention;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Restore_gate_rejects_missing_pages_and_an_outdated_expected_snapshot()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid assetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.PrivacyErasureAssets.Add(new PrivacyErasureAssetEntity
            {
                AssetId = assetId, CaptureId = Guid.NewGuid(), OperationId = Guid.NewGuid(),
                SpoolKey = $"spool/2026/10/{assetId:N}", ExpectedSize = 4,
                StorageMarker = Guid.NewGuid().ToString("N"), RequestedAt = now,
                CompletedAt = now, MetadataRedactedAt = now, DerivativesRemovedAt = now,
                OriginalsRemovedAt = now, State = "Completed",
            });
            await database.SaveChangesAsync(token);
        }
        await using AsyncServiceScope scope = fixture.Server.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<PrivacyErasureSuppressionService>();
        var erasure = scope.ServiceProvider.GetRequiredService<PrivacyErasureService>();
        PrivacyErasureProtectedManifest manifest = await service.CreateExportAsync(token);
        string directory = Path.Combine(fixture.Database.SpoolRoot, "synthetic-restore-evidence");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)), token);

        IConfiguration configuration = RestoreConfiguration(directory, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => SuppressionRestoreGate.ReconcileAsync(configuration, service, erasure, token));
        configuration = RestoreConfiguration(directory, manifest.SnapshotId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SuppressionRestoreGate.ReconcileAsync(configuration, service, erasure, token));
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Null((await verify.PrivacyErasureImports.SingleAsync(token)).CompletedAt);
    }

    private static IConfiguration RestoreConfiguration(string directory, Guid snapshotId) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Restore:Enabled"] = "true",
            ["ShutterSpool:Restore:SuppressionSnapshotId"] = snapshotId.ToString(),
            ["ShutterSpool:Restore:EvidenceDirectory"] = directory,
        }).Build();
}
