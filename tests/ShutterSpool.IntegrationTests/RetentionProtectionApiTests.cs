using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Erasure_tombstone_fences_protection_reads_and_transactional_admission()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        ProtectionOriginal original;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            original = await (from asset in database.Assets
                join member in database.CaptureAssets on asset.Id equals member.AssetId
                where member.CaptureId == captureId
                select new ProtectionOriginal(asset.Id, captureId, asset.SpoolKey, asset.Size))
                .SingleAsync(token);
            database.PrivacyErasureAssets.Add(new PrivacyErasureAssetEntity
            {
                AssetId = original.AssetId, CaptureId = captureId, OperationId = Guid.NewGuid(),
                SpoolKey = original.SpoolKey, ExpectedSize = original.Size, StorageMarker = "synthetic",
                RequestedAt = DateTimeOffset.UtcNow, State = "Waiting",
            });
            await database.SaveChangesAsync(token);
        }

        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        var store = new EfAssetProtectionStore(verify);
        Assert.Null(await store.GetAvailableOriginalsAsync([original.AssetId], token));
        ProtectionOperation<GalleryProtection> result = await store.CreateGalleryAsync([original],
            DateTimeOffset.UtcNow.AddDays(2), DateTimeOffset.UtcNow, token);
        Assert.Equal(ProtectionOperationStatus.Blocked, result.Status);
        Assert.Empty(await verify.GalleryProtections.ToArrayAsync(token));
        Assert.Empty(await verify.GalleryProtectionAssets.ToArrayAsync(token));

        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = fixture.Database.SpoolRoot, ReservedBytes = 0 });
        var admission = new PostgresProtectionAdmissionLock(verify.Database.GetConnectionString()!, spool);
        await using IProtectionAdmissionLease lease = await admission.AcquireSharedAsync([original], token);
        Assert.False(await lease.AreAvailableAsync(token));
    }

    [Fact]
    public async Task Protection_and_hold_mutations_invalidate_active_capture_retention_facts()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        Guid assetId = await database.CaptureAssets.Where(member => member.CaptureId == captureId)
            .Select(member => member.AssetId).SingleAsync(token);

        await SetProtectionIndexCleanAsync(database, captureId, token);
        using var createGallery = await fixture.Client.PostAsJsonAsync("/management/retention/gallery-protections/",
            new { assetIds = new[] { assetId }, expiresAt = DateTimeOffset.UtcNow.AddDays(2) }, token);
        createGallery.EnsureSuccessStatusCode();
        Assert.True(await IsRetentionFactsDirtyAsync(database, captureId, token));
        Guid protectionId = JsonNode.Parse(await createGallery.Content.ReadAsStringAsync(token))!["current"]!["id"]!.GetValue<Guid>();

        await SetProtectionIndexCleanAsync(database, captureId, token);
        using var revokeGallery = await fixture.Client.PostAsJsonAsync(
            $"/management/retention/gallery-protections/{protectionId}/revoke-access",
            new { expectedRevision = 0 }, token);
        revokeGallery.EnsureSuccessStatusCode();
        Assert.True(await IsRetentionFactsDirtyAsync(database, captureId, token));

        await SetProtectionIndexCleanAsync(database, captureId, token);
        using var createHold = await fixture.Client.PostAsJsonAsync("/management/retention/preservation-holds/",
            new { assetIds = new[] { assetId }, authority = "Synthetic authority", reason = "Preserve source",
                reviewAt = DateTimeOffset.UtcNow.AddDays(1) }, token);
        createHold.EnsureSuccessStatusCode();
        Assert.True(await IsRetentionFactsDirtyAsync(database, captureId, token));
        Guid holdId = JsonNode.Parse(await createHold.Content.ReadAsStringAsync(token))!["current"]!["id"]!.GetValue<Guid>();

        await SetProtectionIndexCleanAsync(database, captureId, token);
        using var resolveHold = await fixture.Client.PostAsJsonAsync(
            $"/management/retention/preservation-holds/{holdId}/resolve",
            new { expectedRevision = 0, resolution = "Obligation ended" }, token);
        resolveHold.EnsureSuccessStatusCode();
        Assert.True(await IsRetentionFactsDirtyAsync(database, captureId, token));
    }

    [Fact]
    public async Task Protection_file_probe_reuses_shared_lease_when_exclusive_removal_is_queued()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        ProtectionOriginal original;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
            original = await (from asset in database.Assets
                join member in database.CaptureAssets on asset.Id equals member.AssetId
                where member.CaptureId == captureId
                select new ProtectionOriginal(asset.Id, captureId, asset.SpoolKey, asset.Size))
                .SingleAsync(token);
        await using var connectionDatabase = new ShutterSpoolDbContext(fixture.Database.Options);
        string connectionString = connectionDatabase.Database.GetConnectionString()!;
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = fixture.Database.SpoolRoot, ReservedBytes = 0 });
        var admission = new PostgresProtectionAdmissionLock(connectionString, spool);
        var removalLocks = new PostgresOriginalAssetLock(connectionString);
        await using IProtectionAdmissionLease shared = await admission.AcquireSharedAsync([original], token);
        Task<IOriginalAssetLockLease>? queuedRemoval = null;
        try
        {
            queuedRemoval = removalLocks.AcquireExclusiveAsync(original.AssetId, original.SpoolKey, token);
            await WaitForQueuedAdvisoryLockAsync(connectionString, original, token);
            Assert.False(queuedRemoval.IsCompleted);
            Assert.True(await shared.AreAvailableAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token));
        }
        finally
        {
            await shared.DisposeAsync();
            if (queuedRemoval is not null)
            {
                IOriginalAssetLockLease exclusive = await queuedRemoval.WaitAsync(TimeSpan.FromSeconds(5), token);
                await exclusive.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Gallery_protection_keeps_exact_originals_through_access_revocation_and_blocks_purge()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        Guid assetId = await database.CaptureAssets.Where(member => member.CaptureId == captureId)
            .Select(member => member.AssetId).SingleAsync(token);
        string key = await database.Assets.Where(asset => asset.Id == assetId).Select(asset => asset.SpoolKey).SingleAsync(token);

        using var trash = await fixture.Client.PostAsJsonAsync($"/management/captures/{captureId}/trash",
            new { expectedRevision = 0 }, token);
        trash.EnsureSuccessStatusCode();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddDays(10);
        using var create = await fixture.Client.PostAsJsonAsync("/management/retention/gallery-protections/",
            new { assetIds = new[] { assetId }, expiresAt }, token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonNode.Parse(await create.Content.ReadAsStringAsync(token))!;
        Guid protectionId = created["current"]!["id"]!.GetValue<Guid>();
        Assert.Equal(new[] { assetId }, created["current"]!["assetIds"]!.AsArray()
            .Select(item => item!.GetValue<Guid>()).ToArray());
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(),
            created["current"]!["expiresAt"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());

        using var revoke = await fixture.Client.PostAsJsonAsync(
            $"/management/retention/gallery-protections/{protectionId}/revoke-access",
            new { expectedRevision = 0 }, token);
        revoke.EnsureSuccessStatusCode();
        var revoked = JsonNode.Parse(await revoke.Content.ReadAsStringAsync(token))!["current"]!;
        Assert.NotNull(revoked["accessRevokedAt"]);
        Assert.Equal(expiresAt.ToUnixTimeMilliseconds(),
            revoked["expiresAt"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());

        using var rejectedPurge = await fixture.Client.PostAsJsonAsync($"/management/trash/{captureId}/purge",
            new { expectedRevision = 1, confirm = true }, token);
        Assert.True(rejectedPurge.StatusCode is HttpStatusCode.Conflict or (HttpStatusCode)423);
        Assert.Equal("GALLERY_PROMISE_ACTIVE", JsonNode.Parse(await rejectedPurge.Content.ReadAsStringAsync(token))!["errorCode"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(fixture.Database.SpoolRoot, key)));

        await database.GalleryProtections.Where(item => item.Id == protectionId).ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), token);
        using var explicitPurge = await fixture.Client.PostAsJsonAsync($"/management/trash/{captureId}/purge",
            new { expectedRevision = 1, confirm = true }, token);
        string purgeBody = await explicitPurge.Content.ReadAsStringAsync(token);
        var purgeResult = JsonNode.Parse(purgeBody)!;
        bool originalExists = File.Exists(Path.Combine(fixture.Database.SpoolRoot, key));
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity currentCapture = await verify.Captures.AsNoTracking()
            .SingleAsync(item => item.Id == captureId, token);
        AssetEntity currentAsset = await verify.Assets.AsNoTracking()
            .SingleAsync(item => item.Id == assetId, token);
        AssetRemovalOperationEntity[] removal = await verify.AssetRemovalOperations.AsNoTracking()
            .Where(item => item.CaptureId == captureId).OrderBy(item => item.OperationId).ToArrayAsync(token);
        string progress = string.Join(", ", removal.Select(item =>
            $"{item.State}:{item.FailureCode ?? "none"}"));
        string diagnostic = $"HTTP {(int)explicitPurge.StatusCode} {explicitPurge.StatusCode}; response={purgeBody}; "
            + $"capture={currentCapture.RetentionDisposition}; asset={currentAsset.State}; "
            + $"removals=[{progress}]; originalExists={originalExists}";

        Assert.True(explicitPurge.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, diagnostic);
        Assert.True(purgeResult["status"]?.GetValue<string>() == "Committed", diagnostic);
        Assert.True(currentCapture.RetentionDisposition == "Purged", diagnostic);
        Assert.True(currentAsset.State == "Removed", diagnostic);
        Assert.NotEmpty(removal);
        Assert.All(removal, operation => Assert.Equal("Removed", operation.State));
        Assert.False(originalExists, diagnostic);
    }

    [Fact]
    public async Task Preservation_review_does_not_release_hold_but_explicit_resolution_allows_purge()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        Guid assetId = await database.CaptureAssets.Where(member => member.CaptureId == captureId)
            .Select(member => member.AssetId).SingleAsync(token);
        string key = await database.Assets.Where(asset => asset.Id == assetId).Select(asset => asset.SpoolKey).SingleAsync(token);

        using var create = await fixture.Client.PostAsJsonAsync("/management/retention/preservation-holds/",
            new { assetIds = new[] { assetId }, authority = "Synthetic order 42", reason = "Retain source bytes",
                reviewAt = DateTimeOffset.UtcNow.AddDays(1) }, token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = JsonNode.Parse(await create.Content.ReadAsStringAsync(token))!;
        Guid holdId = created["current"]!["id"]!.GetValue<Guid>();
        Assert.Equal("Synthetic order 42", created["current"]!["authority"]!.GetValue<string>());
        Assert.Equal(new[] { assetId }, created["current"]!["assetIds"]!.AsArray()
            .Select(item => item!.GetValue<Guid>()).ToArray());

        using var trash = await fixture.Client.PostAsJsonAsync($"/management/captures/{captureId}/trash",
            new { expectedRevision = 0 }, token);
        trash.EnsureSuccessStatusCode();
        await database.PreservationHolds.Where(item => item.Id == holdId).ExecuteUpdateAsync(update =>
            update.SetProperty(item => item.ReviewAt, DateTimeOffset.UtcNow.AddSeconds(-1)), token);
        using var blockedPurge = await fixture.Client.PostAsJsonAsync($"/management/trash/{captureId}/purge",
            new { expectedRevision = 1, confirm = true }, token);
        Assert.True(blockedPurge.StatusCode is HttpStatusCode.Conflict or (HttpStatusCode)423);
        Assert.Equal("PRESERVATION_HOLD_ACTIVE", JsonNode.Parse(await blockedPurge.Content.ReadAsStringAsync(token))!["errorCode"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(fixture.Database.SpoolRoot, key)));

        using var resolve = await fixture.Client.PostAsJsonAsync($"/management/retention/preservation-holds/{holdId}/resolve",
            new { expectedRevision = 0, resolution = "Obligation ended" }, token);
        resolve.EnsureSuccessStatusCode();
        var resolved = JsonNode.Parse(await resolve.Content.ReadAsStringAsync(token))!["current"]!;
        Assert.NotNull(resolved["resolvedAt"]);
        Assert.Equal("operator", resolved["resolvedBy"]!.GetValue<string>());
        Assert.Equal("Obligation ended", resolved["resolution"]!.GetValue<string>());

        using var purge = await fixture.Client.PostAsJsonAsync($"/management/trash/{captureId}/purge",
            new { expectedRevision = 1, confirm = true }, token);
        string purgeBody = await purge.Content.ReadAsStringAsync(token);
        var purgeResult = JsonNode.Parse(purgeBody)!;
        bool originalExists = File.Exists(Path.Combine(fixture.Database.SpoolRoot, key));
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity currentCapture = await verify.Captures.AsNoTracking()
            .SingleAsync(item => item.Id == captureId, token);
        AssetEntity currentAsset = await verify.Assets.AsNoTracking()
            .SingleAsync(item => item.Id == assetId, token);
        AssetRemovalOperationEntity[] removal = await verify.AssetRemovalOperations.AsNoTracking()
            .Where(item => item.CaptureId == captureId).OrderBy(item => item.OperationId).ToArrayAsync(token);
        string progress = string.Join(", ", removal.Select(item =>
            $"{item.State}:{item.FailureCode ?? "none"}"));
        string diagnostic = $"HTTP {(int)purge.StatusCode} {purge.StatusCode}; response={purgeBody}; "
            + $"capture={currentCapture.RetentionDisposition}; asset={currentAsset.State}; "
            + $"removals=[{progress}]; originalExists={originalExists}";

        Assert.True(purge.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, diagnostic);
        Assert.True(purgeResult["status"]?.GetValue<string>() == "Committed", diagnostic);
        Assert.True(currentCapture.RetentionDisposition == "Purged", diagnostic);
        Assert.True(currentAsset.State == "Removed", diagnostic);
        Assert.NotEmpty(removal);
        Assert.All(removal, operation => Assert.Equal("Removed", operation.State));
        Assert.False(originalExists, diagnostic);
    }

    [Fact]
    public async Task Gallery_promise_rejects_missing_members_and_duration_beyond_operator_maximum()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        Guid assetId;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
            assetId = await database.CaptureAssets.Where(member => member.CaptureId == captureId)
                .Select(member => member.AssetId).SingleAsync(token);

        using var missing = await fixture.Client.PostAsJsonAsync("/management/retention/gallery-protections/",
            new { assetIds = new[] { assetId, Guid.NewGuid() }, expiresAt = DateTimeOffset.UtcNow.AddDays(2) }, token);
        Assert.Equal((HttpStatusCode)423, missing.StatusCode);
        using var tooLong = await fixture.Client.PostAsJsonAsync("/management/retention/gallery-protections/",
            new { assetIds = new[] { assetId }, expiresAt = DateTimeOffset.UtcNow.AddDays(31) }, token);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Empty(await verify.GalleryProtections.ToArrayAsync(token));
        Assert.Empty(await verify.PreservationHolds.ToArrayAsync(token));
    }

    [Fact]
    public async Task Gallery_extension_rechecks_exact_original_bytes_before_accepting_new_deadline()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        Guid assetId;
        string key;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            assetId = await database.CaptureAssets.Where(member => member.CaptureId == captureId)
                .Select(member => member.AssetId).SingleAsync(token);
            key = await database.Assets.Where(asset => asset.Id == assetId).Select(asset => asset.SpoolKey).SingleAsync(token);
        }
        DateTimeOffset firstExpiry = DateTimeOffset.UtcNow.AddDays(2);
        using var create = await fixture.Client.PostAsJsonAsync("/management/retention/gallery-protections/",
            new { assetIds = new[] { assetId }, expiresAt = firstExpiry }, token);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Guid protectionId = JsonNode.Parse(await create.Content.ReadAsStringAsync(token))!["current"]!["id"]!.GetValue<Guid>();

        string path = Path.Combine(fixture.Database.SpoolRoot, key);
        byte[] originalBytes = await File.ReadAllBytesAsync(path, token);
        File.Delete(path);
        using var missingExtension = await fixture.Client.PutAsJsonAsync(
            $"/management/retention/gallery-protections/{protectionId}",
            new { expectedRevision = 0, expiresAt = DateTimeOffset.UtcNow.AddDays(5) }, token);
        Assert.Equal((HttpStatusCode)423, missingExtension.StatusCode);
        await File.WriteAllBytesAsync(path, originalBytes, token);

        DateTimeOffset extendedExpiry = DateTimeOffset.UtcNow.AddDays(5);
        using var extend = await fixture.Client.PutAsJsonAsync(
            $"/management/retention/gallery-protections/{protectionId}",
            new { expectedRevision = 0, expiresAt = extendedExpiry }, token);
        extend.EnsureSuccessStatusCode();
        var extended = JsonNode.Parse(await extend.Content.ReadAsStringAsync(token))!["current"]!;
        Assert.Equal(1, extended["revision"]!.GetValue<int>());
        Assert.Equal(new[] { assetId }, extended["assetIds"]!.AsArray()
            .Select(item => item!.GetValue<Guid>()).ToArray());
        Assert.Equal(extendedExpiry.ToUnixTimeMilliseconds(),
            extended["expiresAt"]!.GetValue<DateTimeOffset>().ToUnixTimeMilliseconds());
    }

    private static async Task WaitForQueuedAdvisoryLockAsync(string connectionString, ProtectionOriginal original,
        CancellationToken cancellationToken)
    {
        string lockKey = $"shutterspool-original:{original.AssetId:N}:{original.SpoolKey}";
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH key AS (SELECT hashtextextended(@key, 0) AS value)
                SELECT EXISTS (
                    SELECT 1 FROM pg_locks, key
                    WHERE locktype = 'advisory' AND NOT granted
                      AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
                      AND classid = ((key.value >> 32) & 4294967295)::oid
                      AND objid = (key.value & 4294967295)::oid AND objsubid = 1)
                """;
            command.Parameters.AddWithValue("key", lockKey);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            await Task.Delay(20, cancellationToken);
        }
        throw new TimeoutException("The exclusive original lease did not reach PostgreSQL's wait queue.");
    }

    private static Task SetProtectionIndexCleanAsync(ShutterSpoolDbContext database, Guid captureId,
        CancellationToken token) => database.Captures.Where(item => item.Id == captureId)
        .ExecuteUpdateAsync(update => update.SetProperty(item => item.RetentionFactsDirty, false), token);

    private static async Task<bool> IsRetentionFactsDirtyAsync(ShutterSpoolDbContext database, Guid captureId,
        CancellationToken token) => await database.Captures.AsNoTracking()
        .Where(item => item.Id == captureId).Select(item => item.RetentionFactsDirty).SingleAsync(token);
}
