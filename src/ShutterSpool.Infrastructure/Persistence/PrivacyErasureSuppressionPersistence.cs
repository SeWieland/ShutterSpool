using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Retention;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class PrivacyErasureSuppressionPersistence(ShutterSpoolDbContext database, TimeProvider clock)
{
    private const int MaximumTransientKeys = 64;
    private const int ExportPageSize = PrivacyErasureSuppressionService.PageSize;
    public async Task<PrivacyErasureSuppressionManifest> CreateSuppressionExportAsync(
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await RemovePreviousExportAsync(cancellationToken);
        Guid snapshotId = Guid.NewGuid();
        DateTimeOffset now = clock.GetUtcNow();
        int count = await database.PrivacyErasureAssets.CountAsync(cancellationToken);
        int pages = (count + ExportPageSize - 1) / ExportPageSize;
        var snapshot = new PrivacyErasureExportEntity
        {
            Id = snapshotId, CreatedAt = now, RecordCount = count, PageSize = ExportPageSize,
            PageCount = pages,
        };
        database.PrivacyErasureExports.Add(snapshot);
        await database.SaveChangesAsync(cancellationToken);
        await CopyExportAssetsAsync(snapshotId, cancellationToken);
        snapshot.ManifestHash = await ComputeExportManifestHashAsync(snapshotId, pages, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToManifest(snapshot);
    }

    private async Task RemovePreviousExportAsync(CancellationToken cancellationToken)
    {
        Guid[] prior = await database.PrivacyErasureExports.Select(item => item.Id).ToArrayAsync(cancellationToken);
        await database.PrivacyErasureExportAssets.Where(item => prior.Contains(item.SnapshotId))
            .ExecuteDeleteAsync(cancellationToken);
        await database.PrivacyErasureExports.ExecuteDeleteAsync(cancellationToken);
    }

    private Task<int> CopyExportAssetsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO privacy_erasure_export_assets
                ("SnapshotId", "PageIndex", "Ordinal", "OperationId", "CaptureId", "AssetId",
                 "SpoolKey", "ExpectedSize", "KnownTransientKeys", "StorageMarker", "ErasedAt", "Completed")
            SELECT {snapshotId}, ((row_number() OVER (ORDER BY "AssetId") - 1) / {ExportPageSize})::integer,
                   (row_number() OVER (ORDER BY "AssetId") - 1)::integer, "OperationId", "CaptureId", "AssetId",
                   "SpoolKey", "ExpectedSize", "KnownTransientKeys", "StorageMarker", "RequestedAt", ("State" = 'Completed')
            FROM privacy_erasure_assets
            """, cancellationToken);

    private async Task<string> ComputeExportManifestHashAsync(Guid snapshotId, int pages,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int pageIndex = 0; pageIndex < pages; pageIndex++)
        {
            PrivacyErasureExportAssetEntity[] page = await database.PrivacyErasureExportAssets.AsNoTracking()
                .Where(item => item.SnapshotId == snapshotId && item.PageIndex == pageIndex)
                .OrderBy(item => item.Ordinal).ToArrayAsync(cancellationToken);
            foreach (PrivacyErasureExportAssetEntity item in page)
                AppendRecordHash(hash, ToRecord(item));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public async Task<PrivacyErasureSuppressionManifest?> GetSuppressionExportAsync(Guid snapshotId,
        CancellationToken cancellationToken)
    {
        PrivacyErasureExportEntity? snapshot = await database.PrivacyErasureExports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == snapshotId, cancellationToken);
        return snapshot is null ? null : ToManifest(snapshot);
    }

    public async Task<PrivacyErasureSuppressionPage?> GetSuppressionExportPageAsync(Guid snapshotId, int pageIndex,
        CancellationToken cancellationToken)
    {
        PrivacyErasureExportEntity? snapshot = await database.PrivacyErasureExports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == snapshotId, cancellationToken);
        if (snapshot is null || pageIndex < 0 || pageIndex >= snapshot.PageCount) return null;
        PrivacyErasureExportAssetEntity[] assets = await database.PrivacyErasureExportAssets.AsNoTracking()
            .Where(item => item.SnapshotId == snapshotId && item.PageIndex == pageIndex)
            .OrderBy(item => item.Ordinal).ToArrayAsync(cancellationToken);
        return new(snapshotId, pageIndex, assets.Select(ToRecord).ToArray());
    }

    public async Task<PrivacyErasureImportStatus> BeginSuppressionReconciliationAsync(
        PrivacyErasureSuppressionManifest manifest, Guid expectedSnapshotId, CancellationToken cancellationToken)
    {
        if (manifest.SnapshotId != expectedSnapshotId || manifest.RecordCount < 0 || manifest.PageSize != ExportPageSize
            || manifest.PageCount != (manifest.RecordCount + ExportPageSize - 1) / ExportPageSize
            || manifest.ManifestHash.Length != 64 || !manifest.ManifestHash.All(Uri.IsHexDigit))
            return new(expectedSnapshotId, manifest.PageCount, 0, false, "SUPPRESSION_MANIFEST_INVALID");

        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        PrivacyErasureImportEntity? existing = await database.PrivacyErasureImports
            .FromSqlInterpolated($"SELECT * FROM privacy_erasure_imports WHERE \"SnapshotId\" = {expectedSnapshotId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            bool same = existing.RecordCount == manifest.RecordCount && existing.PageSize == manifest.PageSize
                && existing.PageCount == manifest.PageCount && existing.ManifestHash == manifest.ManifestHash;
            if (same && existing.CompletedAt is not null)
            {
                existing.CompletedAt = null;
                existing.ReceivedPageCount = 0;
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(expectedSnapshotId, existing.PageCount, 0, false, null);
            }
            await transaction.CommitAsync(cancellationToken);
            return new(expectedSnapshotId, existing.PageCount, existing.ReceivedPageCount,
                false, same ? null : "SUPPRESSION_SNAPSHOT_MISMATCH");
        }

        Guid[] old = await database.PrivacyErasureImports.Select(item => item.SnapshotId).ToArrayAsync(cancellationToken);
        await database.PrivacyErasureImportAssets.Where(item => old.Contains(item.SnapshotId))
            .ExecuteDeleteAsync(cancellationToken);
        await database.PrivacyErasureImports.ExecuteDeleteAsync(cancellationToken);
        database.PrivacyErasureImports.Add(new PrivacyErasureImportEntity
        {
            SnapshotId = expectedSnapshotId, CreatedAt = clock.GetUtcNow(), RecordCount = manifest.RecordCount,
            PageSize = manifest.PageSize, PageCount = manifest.PageCount, ManifestHash = manifest.ManifestHash,
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(expectedSnapshotId, manifest.PageCount, 0, false, null);
    }

    public async Task<PrivacyErasureImportStatus> StageSuppressionPageAsync(PrivacyErasureSuppressionPage page,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        PrivacyErasureImportEntity? import = await database.PrivacyErasureImports
            .FromSqlInterpolated($"SELECT * FROM privacy_erasure_imports WHERE \"SnapshotId\" = {page.SnapshotId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (import is null || page.PageIndex < 0 || page.PageIndex >= import.PageCount || import.CompletedAt is not null)
            return await FinishImportAsync(transaction, import, "SUPPRESSION_IMPORT_UNAVAILABLE", cancellationToken);
        int expectedCount = Math.Min(import.PageSize, import.RecordCount - page.PageIndex * import.PageSize);
        if (!IsValidPage(page, expectedCount))
            return await FinishImportAsync(transaction, import, "SUPPRESSION_PAGE_INVALID", cancellationToken);

        PrivacyErasureImportAssetEntity[] previous = await database.PrivacyErasureImportAssets.AsNoTracking()
            .Where(item => item.SnapshotId == page.SnapshotId && item.PageIndex == page.PageIndex)
            .OrderBy(item => item.Ordinal).ToArrayAsync(cancellationToken);
        if (previous.Length > 0)
        {
            bool same = previous.Select(ToRecord).Zip(page.Records).All(pair => SameRecord(pair.First, pair.Second));
            return await FinishImportAsync(transaction, import, same ? null : "SUPPRESSION_PAGE_MISMATCH", cancellationToken);
        }
        var members = page.Records.Select((record, index) => new PrivacyErasureImportAssetEntity
        {
            SnapshotId = page.SnapshotId, PageIndex = page.PageIndex,
            Ordinal = page.PageIndex * import.PageSize + index, OperationId = record.OperationId,
            CaptureId = record.CaptureId, AssetId = record.AssetId, SpoolKey = record.SpoolKey,
            ExpectedSize = record.ExpectedSize, KnownTransientKeys = [.. record.KnownTransientKeys],
            StorageMarker = record.StorageMarker, ErasedAt = record.ErasedAt, Completed = record.Completed,
        }).ToArray();
        Guid[] memberIds = [.. members.Select(member => member.AssetId)];
        Guid[] duplicateIds = await database.PrivacyErasureImportAssets.AsNoTracking()
            .Where(item => item.SnapshotId == page.SnapshotId && memberIds.Contains(item.AssetId))
            .Select(item => item.AssetId).ToArrayAsync(cancellationToken);
        if (duplicateIds.Length > 0)
            return await FinishImportAsync(transaction, import, "SUPPRESSION_ASSET_DUPLICATE", cancellationToken);
        database.PrivacyErasureImportAssets.AddRange(members);
        import.ReceivedPageCount++;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(page.SnapshotId, import.PageCount, import.ReceivedPageCount, false, null);
    }

    private static bool IsValidPage(PrivacyErasureSuppressionPage page, int expectedCount) =>
        page.Records is not null && page.Records.Count == expectedCount
        && page.Records.Select(item => item.AssetId).Distinct().Count() == expectedCount
        && page.Records.All(item => item.AssetId != Guid.Empty && item.CaptureId != Guid.Empty
            && item.OperationId != Guid.Empty && item.ExpectedSize > 0
            && !string.IsNullOrWhiteSpace(item.SpoolKey) && item.SpoolKey.Length <= 1_024
            && !string.IsNullOrWhiteSpace(item.StorageMarker) && item.StorageMarker.Length <= 32
            && item.KnownTransientKeys is not null && item.KnownTransientKeys.Count <= MaximumTransientKeys
            && item.KnownTransientKeys.All(key => !string.IsNullOrWhiteSpace(key) && key.Length <= 1_024
                && key != item.SpoolKey)
            && item.KnownTransientKeys.Distinct(StringComparer.Ordinal).Count() == item.KnownTransientKeys.Count);

    public async Task<PrivacyErasureImportStatus> CompleteSuppressionReconciliationAsync(Guid snapshotId,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        PrivacyErasureImportEntity? import = await database.PrivacyErasureImports
            .FromSqlInterpolated($"SELECT * FROM privacy_erasure_imports WHERE \"SnapshotId\" = {snapshotId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (import is null)
            return await FinishImportAsync(transaction, null, "SUPPRESSION_IMPORT_UNAVAILABLE", cancellationToken);
        if (import.CompletedAt is not null)
            return await FinishImportAsync(transaction, import, null, cancellationToken);
        if (!await IsImportCompleteAsync(import, snapshotId, cancellationToken))
            return await FinishImportAsync(transaction, import, "SUPPRESSION_IMPORT_INCOMPLETE", cancellationToken);
        if (!await ManifestHashMatchesAsync(import, snapshotId, cancellationToken))
            return await FinishImportAsync(transaction, import, "SUPPRESSION_MANIFEST_MISMATCH", cancellationToken);
        if (!await ExistingIdentitiesMatchAsync(import, snapshotId, cancellationToken))
            return await FinishImportAsync(transaction, import, "SUPPRESSION_IDENTITY_MISMATCH", cancellationToken);

        await ApplyImportPagesAsync(import, snapshotId, cancellationToken);
        import.CompletedAt = clock.GetUtcNow();
        await database.PrivacyErasureImportAssets.Where(item => item.SnapshotId == snapshotId)
            .ExecuteDeleteAsync(cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(snapshotId, import.PageCount, import.PageCount, true, null);
    }

    private async Task<bool> IsImportCompleteAsync(PrivacyErasureImportEntity import, Guid snapshotId,
        CancellationToken cancellationToken)
    {
        int actualCount = await database.PrivacyErasureImportAssets.CountAsync(item => item.SnapshotId == snapshotId,
            cancellationToken);
        return import.ReceivedPageCount == import.PageCount && actualCount == import.RecordCount;
    }

    private async Task<bool> ManifestHashMatchesAsync(PrivacyErasureImportEntity import, Guid snapshotId,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (int pageIndex = 0; pageIndex < import.PageCount; pageIndex++)
        {
            PrivacyErasureImportAssetEntity[] page = await ReadImportPageAsync(snapshotId, pageIndex, cancellationToken);
            foreach (PrivacyErasureImportAssetEntity record in page) AppendRecordHash(hash, ToRecord(record));
        }
        return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), import.ManifestHash, StringComparison.Ordinal);
    }

    private async Task<bool> ExistingIdentitiesMatchAsync(PrivacyErasureImportEntity import, Guid snapshotId,
        CancellationToken cancellationToken)
    {
        for (int pageIndex = 0; pageIndex < import.PageCount; pageIndex++)
        {
            PrivacyErasureImportAssetEntity[] page = await ReadImportPageAsync(snapshotId, pageIndex, cancellationToken);
            Guid[] ids = [.. page.Select(item => item.AssetId)];
            PrivacyErasureAssetEntity[] current = await database.PrivacyErasureAssets
                .Where(item => ids.Contains(item.AssetId)).ToArrayAsync(cancellationToken);
            var byId = current.ToDictionary(item => item.AssetId);
            foreach (PrivacyErasureImportAssetEntity record in page)
                if (byId.TryGetValue(record.AssetId, out PrivacyErasureAssetEntity? present)
                    && !SameIdentity(present, record)) return false;
        }
        return true;
    }

    private async Task ApplyImportPagesAsync(PrivacyErasureImportEntity import, Guid snapshotId,
        CancellationToken cancellationToken)
    {
        for (int pageIndex = 0; pageIndex < import.PageCount; pageIndex++)
        {
            PrivacyErasureImportAssetEntity[] page = await ReadImportPageAsync(snapshotId, pageIndex, cancellationToken);
            Guid[] ids = [.. page.Select(item => item.AssetId)];
            PrivacyErasureAssetEntity[] current = await database.PrivacyErasureAssets
                .Where(item => ids.Contains(item.AssetId)).ToArrayAsync(cancellationToken);
            var byId = current.ToDictionary(item => item.AssetId);
            foreach (PrivacyErasureImportAssetEntity record in page)
                await ApplyImportRecordAsync(record, byId, cancellationToken);
            await RevokeGalleryAccessAsync(database, ids, clock.GetUtcNow(), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ApplyImportRecordAsync(PrivacyErasureImportAssetEntity record,
        IReadOnlyDictionary<Guid, PrivacyErasureAssetEntity> byId, CancellationToken cancellationToken)
    {
        PrivacyErasureOperationEntity? operation = await database.PrivacyErasureOperations
            .SingleOrDefaultAsync(item => item.Id == record.OperationId, cancellationToken);
        if (operation is null)
        {
            operation = new PrivacyErasureOperationEntity
            {
                Id = record.OperationId, RequestedAt = record.ErasedAt, State = "Pending",
            };
            database.PrivacyErasureOperations.Add(operation);
        }
        operation.State = "Pending";
        operation.CompletedAt = null;
        operation.ErrorCode = "RESTORE_ERASURE_RECONCILIATION_REQUIRED";
        if (byId.TryGetValue(record.AssetId, out PrivacyErasureAssetEntity? present))
        {
            present.State = "Waiting";
            present.MetadataRedactedAt = null;
            present.DerivativesRemovedAt = null;
            present.OriginalsRemovedAt = null;
            present.CompletedAt = null;
            present.ErrorCode = null;
            present.DerivativesErrorCode = null;
            present.OriginalsErrorCode = null;
            return;
        }
        database.PrivacyErasureAssets.Add(new PrivacyErasureAssetEntity
        {
            AssetId = record.AssetId, OperationId = record.OperationId, CaptureId = record.CaptureId,
            SpoolKey = record.SpoolKey, ExpectedSize = record.ExpectedSize,
            KnownTransientKeys = [.. record.KnownTransientKeys], StorageMarker = record.StorageMarker,
            RequestedAt = record.ErasedAt, State = "Waiting", ErrorCode = null,
        });
    }

    private Task<PrivacyErasureImportAssetEntity[]> ReadImportPageAsync(Guid snapshotId, int pageIndex,
        CancellationToken cancellationToken) => database.PrivacyErasureImportAssets.AsNoTracking()
        .Where(item => item.SnapshotId == snapshotId && item.PageIndex == pageIndex)
        .OrderBy(item => item.Ordinal).ToArrayAsync(cancellationToken);

    private async Task<PrivacyErasureImportStatus> FinishImportAsync(IDbContextTransaction transaction,
        PrivacyErasureImportEntity? import, string? errorCode, CancellationToken cancellationToken)
    {
        var result = new PrivacyErasureImportStatus(import?.SnapshotId ?? Guid.Empty, import?.PageCount ?? 0,
            import?.ReceivedPageCount ?? 0, import?.CompletedAt is not null, errorCode);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static void AppendRecordHash(IncrementalHash hash, PrivacyErasureSuppressionRecord record)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static bool SameRecord(PrivacyErasureSuppressionRecord left, PrivacyErasureSuppressionRecord right) =>
        left.OperationId == right.OperationId && left.CaptureId == right.CaptureId && left.AssetId == right.AssetId
        && left.SpoolKey == right.SpoolKey && left.ExpectedSize == right.ExpectedSize
        && left.StorageMarker == right.StorageMarker && left.ErasedAt == right.ErasedAt
        && left.Completed == right.Completed && left.KnownTransientKeys.SequenceEqual(right.KnownTransientKeys);

    private static PrivacyErasureSuppressionRecord ToRecord(PrivacyErasureExportAssetEntity item) => new(item.OperationId,
        item.CaptureId, item.AssetId, item.SpoolKey, item.ExpectedSize, item.KnownTransientKeys,
        item.StorageMarker, item.ErasedAt, item.Completed);

    private static PrivacyErasureSuppressionRecord ToRecord(PrivacyErasureImportAssetEntity item) => new(item.OperationId,
        item.CaptureId, item.AssetId, item.SpoolKey, item.ExpectedSize, item.KnownTransientKeys,
        item.StorageMarker, item.ErasedAt, item.Completed);

    private static PrivacyErasureSuppressionManifest ToManifest(PrivacyErasureExportEntity item) => new(1,
        item.Id, item.CreatedAt, item.RecordCount, item.PageSize, item.PageCount, item.ManifestHash);

    private static bool SameIdentity(PrivacyErasureAssetEntity current, PrivacyErasureImportAssetEntity imported) =>
        current.OperationId == imported.OperationId && current.CaptureId == imported.CaptureId
        && current.SpoolKey == imported.SpoolKey && current.ExpectedSize == imported.ExpectedSize
        && current.StorageMarker == imported.StorageMarker
        && current.KnownTransientKeys.SequenceEqual(imported.KnownTransientKeys);

    internal static async Task RevokeGalleryAccessAsync(ShutterSpoolDbContext database, Guid[] assetIds,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        Guid[] protectionIds = await database.GalleryProtectionAssets.AsNoTracking()
            .Where(member => assetIds.Contains(member.AssetId)).Select(member => member.ProtectionId).Distinct()
            .ToArrayAsync(cancellationToken);
        await database.GalleryProtections.Where(protection => protectionIds.Contains(protection.Id)
                && protection.AccessRevokedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.AccessRevokedAt, now)
                .SetProperty(item => item.Revision, item => item.Revision + 1), cancellationToken);
    }
}
