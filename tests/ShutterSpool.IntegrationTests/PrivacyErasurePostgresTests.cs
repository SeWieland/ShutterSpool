using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Metadata;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Confirmed_erasure_waits_for_hold_removes_local_surfaces_redacts_metadata_and_reapplies_export()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        HeldErasureScenario scenario = await SeedHeldErasureScenarioAsync(fixture, token);
        Guid captureId = scenario.CaptureId;
        Guid assetId = scenario.AssetId;
        string spoolKey = scenario.SpoolKey;
        int revision = scenario.Revision;
        Guid privateCollectionId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Collections.Add(new CollectionEntity { Id = privateCollectionId, Name = "Private event",
                Path = "Synthetic/Private event", Identity = "SYNTHETIC/PRIVATE EVENT" });
            await database.SaveChangesAsync(token);
            await database.Captures.Where(item => item.Id == captureId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.CollectionId, privateCollectionId)
                .SetProperty(item => item.ReleasedCollectionId, privateCollectionId)
                .SetProperty(item => item.ReleasedCollectionName, "Private event")
                .SetProperty(item => item.ReleasedCollectionPath, "Synthetic/Private event"), token);
            await database.RoutingDecisions.Where(item => item.CaptureId == captureId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.CollectionId, privateCollectionId)
                .SetProperty(item => item.CollectionName, "Private event")
                .SetProperty(item => item.CollectionPath, "Synthetic/Private event"), token);
        }

        string derivativePath = Path.Combine(fixture.Database.SpoolRoot, "previews", captureId.ToString("N"),
            assetId.ToString("N"), "grid-256x256.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(derivativePath)!);
        await File.WriteAllBytesAsync(derivativePath, [1, 2, 3], token);

        using var preview = await fixture.Client.PostAsJsonAsync("/management/retention/erasures/preview",
            new { selections = new[] { new { captureId, expectedRevision = revision, assetIds = new[] { assetId } } } }, token);
        preview.EnsureSuccessStatusCode();
        JsonNode previewBody = JsonNode.Parse(await preview.Content.ReadAsStringAsync(token))!;
        Assert.Equal(1, previewBody["preservationHoldCount"]!.GetValue<int>());
        Assert.Equal(1, previewBody["ambiguousDeliveryCount"]!.GetValue<int>());

        Guid erasureId = Guid.NewGuid();
        using var begin = await fixture.Client.PostAsJsonAsync("/management/retention/erasures",
            new { operationId = erasureId, confirmed = true,
                selections = new[] { new { captureId, expectedRevision = revision, assetIds = new[] { assetId } } } }, token);
        Assert.Equal(HttpStatusCode.Accepted, begin.StatusCode);
        JsonNode pending = JsonNode.Parse(await begin.Content.ReadAsStringAsync(token))!;
        Assert.Equal("Pending", pending["status"]!.GetValue<string>());
        Assert.Equal("PRESERVATION_HOLD_ACTIVE", pending["errorCode"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(fixture.Database.SpoolRoot, spoolKey)));
        Assert.True(File.Exists(derivativePath));
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            PrivacyErasureAssetEntity waiting = await database.PrivacyErasureAssets.SingleAsync(item => item.AssetId == assetId, token);
            Assert.Equal("Waiting", waiting.State);
            Assert.Null(waiting.MetadataRedactedAt);
            Assert.Null(waiting.DerivativesRemovedAt);
            Assert.Null(waiting.OriginalsRemovedAt);
            Assert.Equal("Held camera", (await database.Assets.SingleAsync(item => item.Id == assetId, token)).CameraMake);
        }

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            PreservationHoldEntity hold = await database.PreservationHolds.SingleAsync(token);
            hold.ResolvedAt = DateTimeOffset.UtcNow;
            hold.ResolvedBy = "operator";
            hold.Resolution = "Synthetic obligation ended";
            await database.SaveChangesAsync(token);
        }

        using var continued = await fixture.Client.PostAsJsonAsync(
            $"/management/retention/erasures/{erasureId}/continue", new { confirmed = true }, token);
        continued.EnsureSuccessStatusCode();
        JsonNode completed = JsonNode.Parse(await continued.Content.ReadAsStringAsync(token))!;
        Assert.Equal("Completed", completed["status"]!.GetValue<string>());
        JsonNode surface = Assert.Single(completed["assets"]!.AsArray())!;
        Assert.Equal("Completed", surface["metadataRedaction"]!.GetValue<string>());
        Assert.Equal("Completed", surface["derivatives"]!.GetValue<string>());
        Assert.Equal("Completed", surface["originalsAndTransients"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(fixture.Database.SpoolRoot, spoolKey)));
        Assert.False(File.Exists(derivativePath));

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            AssetEntity asset = await database.Assets.SingleAsync(item => item.Id == assetId, token);
            Assert.Equal("Removed", asset.State);
            Assert.Equal(Guid.Empty, asset.SourceId);
            Assert.Equal(string.Empty, asset.OriginalName);
            Assert.Equal(string.Empty, asset.Sha256);
            Assert.Equal("Erased", asset.MetadataState);
            Assert.Null(asset.CameraMake);
            Assert.Equal(Guid.Empty, asset.RecoveryCorrelation);
            Assert.Equal(spoolKey, asset.SpoolKey);
            IngestOperationEntity ingest = await database.Operations.SingleAsync(item => item.Id == scenario.OperationId, token);
            Assert.Equal(Guid.Empty, ingest.SourceId);
            Assert.Equal(string.Empty, ingest.OriginalName);
            Assert.Equal(string.Empty, ingest.PartialKey);
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            Assert.Equal(Guid.Empty, capture.SourceId);
            Assert.Equal(string.Empty, capture.NormalizedStem);
            Assert.Equal("Purged", capture.RetentionDisposition);
            Assert.Equal(ShutterSpool.Domain.Collections.CollectionName.BuiltInId, capture.CollectionId);
            Assert.Null(capture.ReleasedCollectionId);
            Assert.Null(capture.ReleasedCollectionName);
            Assert.Null(capture.ReleasedCollectionPath);
            RoutingDecisionEntity[] decisions = await database.RoutingDecisions.Where(item => item.CaptureId == captureId).ToArrayAsync(token);
            Assert.NotEmpty(decisions);
            Assert.All(decisions, item =>
            {
                Assert.Null(item.CollectionId);
                Assert.Null(item.CollectionName);
                Assert.Null(item.CollectionPath);
            });
            PrivacyErasureAssetEntity erased = await database.PrivacyErasureAssets.SingleAsync(item => item.AssetId == assetId, token);
            Assert.NotNull(erased.MetadataRedactedAt);
            Assert.NotNull(erased.DerivativesRemovedAt);
            Assert.NotNull(erased.OriginalsRemovedAt);
            Assert.Contains(scenario.CameraReceiveKey, erased.KnownTransientKeys);
            Assert.Empty(await database.CameraUploadAdmissions.Where(item => item.AssetId == assetId)
                .ToArrayAsync(token));
            Assert.NotEqual(Guid.Empty, scenario.SourceId);
            DeliveryEntity delivery = await database.Deliveries.SingleAsync(item => item.Id == scenario.DeliveryId, token);
            Assert.Equal(DeliveryState.Ambiguous, delivery.State);
            Assert.Null(delivery.ConfirmationEvidence);
            Assert.Equal(string.Empty, delivery.TargetPath);
            DeliveryAttemptEntity attempt = await database.DeliveryAttempts.SingleAsync(item => item.Id == scenario.AttemptId, token);
            Assert.Null(attempt.ErrorMessage);
            Assert.Null(attempt.ConfirmationEvidence);
        }

        string evidenceDirectory = Path.Combine(fixture.Database.SpoolRoot, "synthetic-restore-evidence");
        Directory.CreateDirectory(evidenceDirectory);
        Guid snapshotId;
        int pageCount;
        await using (AsyncServiceScope scope = fixture.Server.Services.CreateAsyncScope())
        {
            var suppression = scope.ServiceProvider.GetRequiredService<PrivacyErasureSuppressionService>();
            PrivacyErasureProtectedManifest manifest = await suppression.CreateExportAsync(token);
            snapshotId = manifest.SnapshotId;
            pageCount = manifest.PageCount;
            var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, jsonOptions), token);
            for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                PrivacyErasureProtectedPage page = Assert.IsType<PrivacyErasureProtectedPage>(
                    await suppression.GetExportPageAsync(snapshotId, pageIndex, token));
                await File.WriteAllTextAsync(Path.Combine(evidenceDirectory, $"page-{pageIndex:D6}.json"),
                    JsonSerializer.Serialize(page, jsonOptions), token);
            }
        }
        Assert.True(pageCount > 0);

        // Recreate the sensitive rows and same-ID files from a stale pre-erasure restore.
        // The gate must reapply the current external evidence and drain deletion before reads resume.
        await RestoreErasureScenarioAsync(fixture, scenario, erasureId, derivativePath, token);
        string originalPath = Path.Combine(fixture.Database.SpoolRoot, spoolKey.Replace('/', Path.DirectorySeparatorChar));
        string partialPath = Path.Combine(fixture.Database.SpoolRoot, scenario.PartialKey.Replace('/', Path.DirectorySeparatorChar));
        string cameraReceivePath = Path.Combine(fixture.Database.SpoolRoot,
            scenario.CameraReceiveKey.Replace('/', Path.DirectorySeparatorChar));

        await using (var beforeGate = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            AssetEntity staleAsset = await beforeGate.Assets.SingleAsync(item => item.Id == assetId, token);
            Assert.Equal(scenario.SourceId, staleAsset.SourceId);
            Assert.Equal("Restored private camera", staleAsset.CameraMake);
            Assert.Equal("Spooled", staleAsset.State);
            Assert.Equal("restored label", (await beforeGate.Captures.SingleAsync(item => item.Id == captureId, token)).Label);
            Assert.Equal("Restored ambiguous evidence", (await beforeGate.Deliveries.SingleAsync(item => item.Id == scenario.DeliveryId, token)).ConfirmationEvidence);
        }
        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(partialPath));
        Assert.True(File.Exists(cameraReceivePath));
        Assert.True(File.Exists(derivativePath));

        await using (AsyncServiceScope scope = fixture.Server.Services.CreateAsyncScope())
        {
            var suppression = scope.ServiceProvider.GetRequiredService<PrivacyErasureSuppressionService>();
            var erasure = scope.ServiceProvider.GetRequiredService<PrivacyErasureService>();
            IConfiguration configuration = RestoreConfiguration(evidenceDirectory, snapshotId);
            await SuppressionRestoreGate.ReconcileAsync(configuration, suppression, erasure, token);
        }

        using var afterRestoreRead = await fixture.Client.GetAsync($"/management/captures/{captureId}", token);
        Assert.Equal(HttpStatusCode.NotFound, afterRestoreRead.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        AssetEntity finalAsset = await verify.Assets.SingleAsync(item => item.Id == assetId, token);
        Assert.Equal("Removed", finalAsset.State);
        Assert.Equal(Guid.Empty, finalAsset.SourceId);
        Assert.Equal(string.Empty, finalAsset.OriginalName);
        Assert.Equal(string.Empty, finalAsset.Sha256);
        Assert.Equal(Guid.Empty, finalAsset.RecoveryCorrelation);
        Assert.Equal("Erased", finalAsset.MetadataState);
        Assert.Null(finalAsset.CameraMake);
        Assert.Null(finalAsset.MetadataReason);
        CaptureEntity finalCapture = await verify.Captures.SingleAsync(item => item.Id == captureId, token);
        Assert.Equal(Guid.Empty, finalCapture.SourceId);
        Assert.Equal(string.Empty, finalCapture.NormalizedStem);
        Assert.Null(finalCapture.Label);
        Assert.Empty(finalCapture.Tags);
        Assert.Equal("Purged", finalCapture.RetentionDisposition);
        Assert.False(File.Exists(cameraReceivePath));
        IngestOperationEntity finalIngest = await verify.Operations.SingleAsync(item => item.Id == scenario.OperationId, token);
        Assert.Equal(Guid.Empty, finalIngest.SourceId);
        Assert.Equal(string.Empty, finalIngest.OriginalName);
        Assert.Equal(string.Empty, finalIngest.PartialKey);
        Assert.Null(finalIngest.Sha256);
        DeliveryEntity finalDelivery = await verify.Deliveries.SingleAsync(item => item.Id == scenario.DeliveryId, token);
        Assert.Equal(DeliveryState.Ambiguous, finalDelivery.State);
        Assert.Null(finalDelivery.ConfirmationEvidence);
        Assert.Equal(string.Empty, finalDelivery.TargetPath);
        Assert.Null((await verify.DeliveryAttempts.SingleAsync(item => item.Id == scenario.AttemptId, token)).ErrorMessage);
        Assert.False(File.Exists(originalPath));
        Assert.False(File.Exists(partialPath));
        Assert.False(File.Exists(derivativePath));
        PrivacyErasureAssetEntity restored = await verify.PrivacyErasureAssets.SingleAsync(item => item.AssetId == assetId, token);
        Assert.Equal("Completed", restored.State);
        Assert.Equal(spoolKey, restored.SpoolKey);
        Assert.NotNull(restored.MetadataRedactedAt);
        Assert.NotNull(restored.DerivativesRemovedAt);
        Assert.NotNull(restored.OriginalsRemovedAt);
    }

    [Fact]
    public async Task Explicit_erasure_can_redact_metadata_after_authorized_retention_purge()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        Guid assetId;
        int revision;
        string spoolKey;
        long size;
        string storageMarker;
        Guid operationId;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            capture.RetentionDisposition = "Purged";
            capture.RetentionPaused = true;
            revision = capture.Revision;
            AssetEntity asset = await database.Assets.SingleAsync(item => database.CaptureAssets
                .Any(member => member.CaptureId == captureId && member.AssetId == item.Id), token);
            assetId = asset.Id;
            spoolKey = asset.SpoolKey;
            size = asset.Size;
            asset.State = "Removed";
            storageMarker = await database.SpoolQuotaObservations.AsNoTracking()
                .Where(item => item.Id == 1).Select(item => item.StorageMarker!).SingleAsync(token);
            operationId = Guid.NewGuid();
            database.AssetRemovalOperations.Add(new AssetRemovalOperationEntity
            {
                AssetId = assetId, CaptureId = captureId, OperationId = operationId,
                SpoolKey = spoolKey, ExpectedSize = size, StorageMarker = storageMarker,
                KnownTransientKeys = [], Scope = "whole-capture", Reason = "synthetic retention purge",
                PolicyRevision = 1, State = "Removed", DueAt = DateTimeOffset.UtcNow,
                AuthorizedAt = DateTimeOffset.UtcNow, CompletedAt = DateTimeOffset.UtcNow, RemovedBytes = size,
            });
            await database.SaveChangesAsync(token);
        }

        string originalPath = Path.Combine(fixture.Database.SpoolRoot, spoolKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(originalPath));
        File.Delete(originalPath); // Synthetic prior retention purge removed this exact authorized original.

        Guid erasureId = Guid.NewGuid();
        using var begin = await fixture.Client.PostAsJsonAsync("/management/retention/erasures",
            new { operationId = erasureId, confirmed = true,
                selections = new[] { new { captureId, expectedRevision = revision, assetIds = new[] { assetId } } } }, token);
        string beginBody = await begin.Content.ReadAsStringAsync(token);
        Assert.True(begin.StatusCode == HttpStatusCode.OK,
            $"Expected immediate erasure completion, got HTTP {(int)begin.StatusCode}: {beginBody}");
        JsonNode response = JsonNode.Parse(beginBody)!;
        Assert.Equal("Completed", response["status"]!.GetValue<string>());
        JsonNode surface = Assert.Single(response["assets"]!.AsArray())!;
        Assert.Equal("Completed", surface["metadataRedaction"]!.GetValue<string>());
        Assert.Equal("Completed", surface["originalsAndTransients"]!.GetValue<string>());

        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        AssetEntity erasedAsset = await verify.Assets.SingleAsync(item => item.Id == assetId, token);
        Assert.Equal("Removed", erasedAsset.State);
        Assert.Equal(Guid.Empty, erasedAsset.SourceId);
        Assert.Equal(string.Empty, erasedAsset.OriginalName);
        Assert.Equal(string.Empty, erasedAsset.Sha256);
        Assert.Equal("Erased", erasedAsset.MetadataState);
        Assert.Equal("Purged", (await verify.Captures.SingleAsync(item => item.Id == captureId, token)).RetentionDisposition);
        PrivacyErasureAssetEntity persistedSurface = await verify.PrivacyErasureAssets.SingleAsync(item => item.AssetId == assetId, token);
        Assert.Equal("Completed", persistedSurface.State);
        Assert.NotNull(persistedSurface.MetadataRedactedAt);
        Assert.NotNull(persistedSurface.DerivativesRemovedAt);
        Assert.NotNull(persistedSurface.OriginalsRemovedAt);
        Assert.False(File.Exists(originalPath));
    }

    [Fact]
    public async Task Ingest_recovery_keeps_originals_with_authorized_retention_removal()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        Guid assetId;
        string spoolKey;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            AssetEntity asset = await database.Assets.SingleAsync(item => database.CaptureAssets
                .Any(member => member.CaptureId == captureId && member.AssetId == item.Id), token);
            assetId = asset.Id;
            spoolKey = asset.SpoolKey;
            IngestOperationEntity operation = await database.Operations.SingleAsync(item => item.AssetId == assetId, token);
            operation.State = "Promoted";
            operation.FinalKey = spoolKey;
            database.AssetRemovalOperations.Add(new AssetRemovalOperationEntity
            {
                AssetId = assetId, CaptureId = captureId, OperationId = Guid.NewGuid(), SpoolKey = spoolKey,
                ExpectedSize = asset.Size, StorageMarker = "synthetic", Scope = "whole-capture",
                Reason = "authorized retention removal", PolicyRevision = 1, State = "Purging",
                DueAt = DateTimeOffset.UtcNow.AddDays(5), AuthorizedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync(token);
        }

        await using var journalContext = new ShutterSpoolDbContext(fixture.Database.Options);
        var spool = new FileSystemSpool(new FileSystemSpoolOptions
        {
            RootPath = fixture.Database.SpoolRoot,
            ReservedBytes = 0,
        });
        await new ShutterSpool.Application.Ingest.IngestService(new EfIngestJournal(journalContext), spool,
            TimeProvider.System).RecoverAsync(token);

        Assert.True(File.Exists(Path.Combine(fixture.Database.SpoolRoot, spoolKey.Replace('/', Path.DirectorySeparatorChar))));
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal("Promoted", await verify.Operations.Where(item => item.AssetId == assetId)
            .Select(item => item.State).SingleAsync(token));
        Assert.Equal("Spooled", await verify.Assets.Where(item => item.Id == assetId)
            .Select(item => item.State).SingleAsync(token));
    }

    [Fact]
    public async Task Ingest_recovery_preserves_erasure_bytes_after_redaction_crash_until_pending_retry_removes_them()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        Guid assetId;
        Guid sourceId;
        int revision;
        string spoolKey;
        string partialKey;
        long expectedSize;
        Guid erasureId = Guid.NewGuid();
        PrivacyErasureBeginResult begin;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            sourceId = capture.SourceId;
            revision = capture.Revision;
            AssetEntity asset = await database.Assets.SingleAsync(item => database.CaptureAssets
                .Any(member => member.CaptureId == captureId && member.AssetId == item.Id), token);
            assetId = asset.Id;
            spoolKey = asset.SpoolKey;
            asset.CameraMake = "Crash protection camera";
            await database.SaveChangesAsync(token);
            expectedSize = asset.Size;
            partialKey = await database.Operations.Where(item => item.AssetId == assetId)
                .Select(item => item.PartialKey).SingleAsync(token);
            begin = await new EfPrivacyErasureStore(database, TimeProvider.System, new PrivacyErasureSuppressionPersistence(database, TimeProvider.System)).BeginAsync(
                new PrivacyErasureRequest(erasureId,
                    [new PrivacyErasureSelection(captureId, revision, [assetId])], Confirmed: true),
                DateTimeOffset.UtcNow, token);
        }
        Assert.Equal(PrivacyErasureStatus.Pending, begin.Status);
        Assert.Single(begin.Work);
        Assert.NotEqual(Guid.Empty, sourceId);

        string originalPath = Path.Combine(fixture.Database.SpoolRoot, spoolKey.Replace('/', Path.DirectorySeparatorChar));
        string partialPath = Path.Combine(fixture.Database.SpoolRoot, partialKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, [9, 8, 7], token);
        Assert.True(File.Exists(originalPath));

        await using (var journalContext = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            var spool = new FileSystemSpool(new FileSystemSpoolOptions
            {
                RootPath = fixture.Database.SpoolRoot,
                ReservedBytes = 0,
            });
            await new ShutterSpool.Application.Ingest.IngestService(new EfIngestJournal(journalContext), spool,
                TimeProvider.System).RecoverAsync(token);
        }

        Assert.True(File.Exists(originalPath));
        Assert.True(File.Exists(partialPath));
        string quarantineRoot = Path.Combine(fixture.Database.SpoolRoot, "quarantine");
        if (Directory.Exists(quarantineRoot))
            Assert.DoesNotContain(Directory.EnumerateFiles(quarantineRoot, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains(Path.GetFileName(originalPath), StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(Path.GetFileName(partialPath), StringComparison.Ordinal));
        await using (var verifyRecovery = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            IngestOperationEntity operation = await verifyRecovery.Operations.SingleAsync(item => item.AssetId == assetId, token);
            Assert.Equal(string.Empty, operation.PartialKey);
            Assert.Null(operation.FinalKey);
            PrivacyErasureAssetEntity suppression = await verifyRecovery.PrivacyErasureAssets
                .SingleAsync(item => item.AssetId == assetId, token);
            Assert.Equal("Authorized", suppression.State);
            Assert.NotNull(suppression.MetadataRedactedAt);
            Assert.Null(suppression.OriginalsRemovedAt);
            Assert.Equal(spoolKey, suppression.SpoolKey);
            Assert.Contains(partialKey, suppression.KnownTransientKeys);
            Assert.Null((await verifyRecovery.Assets.SingleAsync(item => item.Id == assetId, token)).CameraMake);
        }

        await using (AsyncServiceScope scope = fixture.Server.Services.CreateAsyncScope())
        {
            PrivacyErasureService service = scope.ServiceProvider.GetRequiredService<PrivacyErasureService>();
            Assert.Equal(1, await service.ProcessPendingAsync(10, token));
            Assert.Equal(PrivacyErasureStatus.Completed, (await service.GetAsync(erasureId, token)).Status);
        }
        Assert.False(File.Exists(originalPath));
        Assert.False(File.Exists(partialPath));
        if (Directory.Exists(quarantineRoot))
            Assert.DoesNotContain(Directory.EnumerateFiles(quarantineRoot, "*", SearchOption.AllDirectories),
                path => Path.GetFileName(path).Contains(Path.GetFileName(originalPath), StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains(Path.GetFileName(partialPath), StringComparison.Ordinal));
        await using var verifyFinal = new ShutterSpoolDbContext(fixture.Database.Options);
        PrivacyErasureAssetEntity completed = await verifyFinal.PrivacyErasureAssets.SingleAsync(item => item.AssetId == assetId, token);
        Assert.Equal("Completed", completed.State);
        Assert.NotNull(completed.MetadataRedactedAt);
        Assert.NotNull(completed.DerivativesRemovedAt);
        Assert.NotNull(completed.OriginalsRemovedAt);
        Assert.Equal("Purged", (await verifyFinal.Captures.SingleAsync(item => item.Id == captureId, token)).RetentionDisposition);
        Assert.Equal(Guid.Empty, (await verifyFinal.Captures.SingleAsync(item => item.Id == captureId, token)).SourceId);
        Assert.Equal(expectedSize, (await verifyFinal.Assets.SingleAsync(item => item.Id == assetId, token)).Size);
    }

    [Fact]
    public async Task Metadata_workers_cannot_claim_renew_or_complete_work_after_erasure_is_requested()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        Guid assetId;
        Guid leaseId = Guid.NewGuid();
        string spoolKey;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            AssetEntity asset = await database.Assets.SingleAsync(item => database.CaptureAssets
                .Any(member => member.CaptureId == captureId && member.AssetId == item.Id), token);
            assetId = asset.Id;
            spoolKey = asset.SpoolKey;
            asset.MetadataState = "Processing";
            asset.MetadataLeaseId = leaseId;
            asset.MetadataLeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5);
            asset.MetadataAttemptCount = 1;
            await database.SaveChangesAsync(token);
            database.PrivacyErasureAssets.Add(new PrivacyErasureAssetEntity
            {
                AssetId = assetId, CaptureId = captureId, OperationId = Guid.NewGuid(), SpoolKey = spoolKey,
                ExpectedSize = asset.Size, StorageMarker = "synthetic", RequestedAt = DateTimeOffset.UtcNow,
                State = "Waiting",
            });
            await database.SaveChangesAsync(token);
        }

        await using var workerContext = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAssetMetadataStore(workerContext);
        var work = new MetadataWork(assetId, spoolKey, "sensitive-name.raw", 4, leaseId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.False(await store.RenewAsync(work, now, token));
        await store.CompleteAsync(work, new AssetMetadata(MetadataOutcome.Complete, "image/jpeg", "JPEG",
            new CaptureTime(new DateTime(2025, 1, 2, 3, 4, 5), TimeSpan.Zero, 0, "synthetic", false),
            new EmbeddedValue("Sensitive Camera", "synthetic"), null, null, false, null), token);
        Assert.Null(await store.ClaimAsync(now, token));
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        AssetEntity unchanged = await verify.Assets.SingleAsync(item => item.Id == assetId, token);
        Assert.Equal("Processing", unchanged.MetadataState);
        Assert.Equal(leaseId, unchanged.MetadataLeaseId);
        Assert.Null(unchanged.CameraMake);
        Assert.Null(unchanged.CaptureTimeLocal);
    }

    private static async Task<HeldErasureScenario> SeedHeldErasureScenarioAsync(ConfigurationApiFixture fixture,
        CancellationToken cancellationToken)
    {
        Guid captureId = await SeedRetentionCaptureAsync(fixture, cancellationToken);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, cancellationToken);
        AssetEntity asset = await database.Assets.SingleAsync(item => database.CaptureAssets
            .Any(member => member.CaptureId == captureId && member.AssetId == item.Id), cancellationToken);
        asset.CameraMake = "Held camera";
        Guid operationId = asset.RecoveryCorrelation;
        string partialKey = await database.Operations.Where(item => item.Id == operationId)
            .Select(item => item.PartialKey).SingleAsync(cancellationToken);
        string cameraReceiveKey = $"incoming/{Guid.NewGuid():N}/{asset.Id:N}.partial";

        Guid loginId = Guid.NewGuid();
        database.CameraLogins.Add(new CameraLoginEntity
        {
            Id = loginId, SourceId = capture.SourceId, Username = "synthetic-camera",
            VirtualRoot = "/", CredentialGeneration = 1, CreatedAt = DateTimeOffset.UtcNow,
        });
        database.CameraUploadAdmissions.Add(new CameraUploadAdmissionEntity
        {
            Id = Guid.NewGuid(), LoginId = loginId, SourceId = capture.SourceId,
            Username = "synthetic-camera", SessionId = "synthetic-erasure-session",
            VirtualPath = "/DCIM/ERASED.JPG", AdmissionTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            State = CameraTransferState.Completed, Size = asset.Size, AssetId = asset.Id, ReceiveKey = cameraReceiveKey,
        });

        var hold = new PreservationHoldEntity
        {
            Id = Guid.NewGuid(), Revision = 0, Authority = "Synthetic test authority",
            Reason = "Synthetic hold", CreatedAt = DateTimeOffset.UtcNow,
            ReviewAt = DateTimeOffset.UtcNow.AddDays(1),
        };
        database.PreservationHolds.Add(hold);
        database.PreservationHoldAssets.Add(new PreservationHoldAssetEntity { HoldId = hold.Id, AssetId = asset.Id });
        Guid destinationId = Guid.NewGuid();
        Guid decisionId = Guid.NewGuid();
        Guid deliveryId = Guid.NewGuid();
        Guid attemptId = Guid.NewGuid();
        database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "Synthetic destination" });
        database.RoutingDecisions.Add(new RoutingDecisionEntity
        {
            Id = decisionId, CaptureId = captureId, AssetId = asset.Id, DestinationId = destinationId,
            Generation = 1, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
            Explanation = "Synthetic route", CreatedAt = DateTimeOffset.UtcNow,
        });
        database.Deliveries.Add(new DeliveryEntity
        {
            Id = deliveryId, DecisionId = decisionId, AssetId = asset.Id, DestinationId = destinationId,
            TargetPath = "private/target.raw", Required = true, State = DeliveryState.Ambiguous,
            ConfirmationEvidence = "synthetic ambiguous remote receipt", CreatedAt = DateTimeOffset.UtcNow,
        });
        database.DeliveryAttempts.Add(new DeliveryAttemptEntity
        {
            Id = attemptId, DeliveryId = deliveryId, AssetId = asset.Id, RetryNumber = 1,
            WorkerId = "synthetic-worker", ClaimToken = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow,
            ErrorMessage = "synthetic remote uncertainty", ErrorCode = "uncertain", TraceId = "synthetic-trace",
        });
        await database.SaveChangesAsync(cancellationToken);

        string cameraReceivePath = Path.Combine(fixture.Database.SpoolRoot,
            cameraReceiveKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(cameraReceivePath)!);
        await File.WriteAllBytesAsync(cameraReceivePath, [3, 4], cancellationToken);

        return new(captureId, capture.Revision, capture.SourceId, capture.NormalizedStem, asset.Id,
            asset.SpoolKey, asset.OriginalName, asset.Sha256, asset.IngestedAt, operationId, partialKey,
            cameraReceiveKey, deliveryId, attemptId);
    }

    private static async Task RestoreErasureScenarioAsync(ConfigurationApiFixture fixture,
        HeldErasureScenario scenario, Guid erasureId, string derivativePath, CancellationToken cancellationToken)
    {
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            await database.PrivacyErasureAssets.Where(item => item.AssetId == scenario.AssetId)
                .ExecuteDeleteAsync(cancellationToken);
            await database.PrivacyErasureOperations.Where(item => item.Id == erasureId)
                .ExecuteDeleteAsync(cancellationToken);
            await database.Assets.Where(item => item.Id == scenario.AssetId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.SourceId, scenario.SourceId)
                .SetProperty(item => item.OriginalName, scenario.OriginalName)
                .SetProperty(item => item.Sha256, scenario.OriginalHash)
                .SetProperty(item => item.IngestedAt, scenario.IngestedAt)
                .SetProperty(item => item.RecoveryCorrelation, scenario.OperationId)
                .SetProperty(item => item.State, "Spooled")
                .SetProperty(item => item.MetadataState, "Complete")
                .SetProperty(item => item.MetadataOutcome, "Complete")
                .SetProperty(item => item.CameraMake, "Restored private camera")
                .SetProperty(item => item.CameraMakeTag, "synthetic-restored-tag")
                .SetProperty(item => item.MetadataReason, "Restored private metadata reason"), cancellationToken);
            await database.Operations.Where(item => item.Id == scenario.OperationId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.SourceId, scenario.SourceId)
                .SetProperty(item => item.OriginalName, scenario.OriginalName)
                .SetProperty(item => item.PartialKey, scenario.PartialKey)
                .SetProperty(item => item.FinalKey, scenario.SpoolKey)
                .SetProperty(item => item.Sha256, scenario.OriginalHash)
                .SetProperty(item => item.State, "Committed"), cancellationToken);
            await database.Captures.Where(item => item.Id == scenario.CaptureId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.SourceId, scenario.SourceId)
                .SetProperty(item => item.NormalizedStem, scenario.CaptureStem)
                .SetProperty(item => item.Rating, 3)
                .SetProperty(item => item.Label, "restored label")
                .SetProperty(item => item.Tags, ["Restored private tag"])
                .SetProperty(item => item.RetentionDisposition, "Active")
                .SetProperty(item => item.RetentionPaused, false), cancellationToken);
            await database.CaptureAssets.Where(item => item.CaptureId == scenario.CaptureId
                    && item.AssetId == scenario.AssetId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(item => item.GroupingReason, "Restored private grouping reason"), cancellationToken);
            await database.Deliveries.Where(item => item.Id == scenario.DeliveryId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.TargetPath, "restored/private/target.raw")
                .SetProperty(item => item.ConfirmationEvidence, "Restored ambiguous evidence"), cancellationToken);
            await database.DeliveryAttempts.Where(item => item.Id == scenario.AttemptId).ExecuteUpdateAsync(update => update
                .SetProperty(item => item.ErrorMessage, "Restored private error detail")
                .SetProperty(item => item.ConfirmationEvidence, "Restored private attempt evidence"), cancellationToken);
        }

        string originalPath = Path.Combine(fixture.Database.SpoolRoot,
            scenario.SpoolKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
        await File.WriteAllBytesAsync(originalPath, [1, 2, 3, 4], cancellationToken);
        string partialPath = Path.Combine(fixture.Database.SpoolRoot,
            scenario.PartialKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, [1, 2], cancellationToken);
        string cameraReceivePath = Path.Combine(fixture.Database.SpoolRoot,
            scenario.CameraReceiveKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(cameraReceivePath)!);
        await File.WriteAllBytesAsync(cameraReceivePath, [3, 4], cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(derivativePath)!);
        await File.WriteAllBytesAsync(derivativePath, [7, 8, 9], cancellationToken);
    }

    private sealed record HeldErasureScenario(Guid CaptureId, int Revision, Guid SourceId, string CaptureStem,
        Guid AssetId, string SpoolKey, string OriginalName, string OriginalHash, DateTimeOffset IngestedAt,
        Guid OperationId, string PartialKey, string CameraReceiveKey, Guid DeliveryId, Guid AttemptId);
}
