using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfPrivacyErasureStore(ShutterSpoolDbContext database, TimeProvider clock, PrivacyErasureSuppressionPersistence suppression) : IPrivacyErasureStore
{
    private const int MaximumTransientKeys = 64;

    public async Task<PrivacyErasurePreview> PreviewAsync(IReadOnlyList<PrivacyErasureSelection> selections,
        CancellationToken cancellationToken)
    {
        PreviewSelection? selected = await ReadSelectionAsync(selections, cancellationToken);
        if (selected is null) return new(PrivacyErasureStatus.Conflict, "SELECTION_CHANGED", 0, 0, 0, 0, 0, 0);
        Guid[] assetIds = selected.AssetIds;
        int gallery = await database.GalleryProtectionAssets.CountAsync(member => assetIds.Contains(member.AssetId)
            && database.GalleryProtections.Any(item => item.Id == member.ProtectionId && item.AccessRevokedAt == null), cancellationToken);
        int holds = await database.PreservationHoldAssets.CountAsync(member => assetIds.Contains(member.AssetId)
            && database.PreservationHolds.Any(item => item.Id == member.HoldId && item.ResolvedAt == null), cancellationToken);
        int active = await database.Deliveries.CountAsync(item => assetIds.Contains(item.AssetId)
            && item.State == DeliveryState.Running, cancellationToken);
        int ambiguous = await database.Deliveries.CountAsync(item => assetIds.Contains(item.AssetId)
            && (item.State == DeliveryState.RetryWait || item.State == DeliveryState.Failed
                || item.State == DeliveryState.Pending || item.State == DeliveryState.Ambiguous), cancellationToken);
        return new(PrivacyErasureStatus.Pending, null, selections.Count, assetIds.Length,
            gallery, holds, active, ambiguous);
    }

    public async Task<PrivacyErasureBeginResult> BeginAsync(PrivacyErasureRequest request, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAndCollectionAssignmentsAsync(cancellationToken);
        PrivacyErasureOperationEntity? existing = await database.PrivacyErasureOperations
            .FromSqlInterpolated($"SELECT * FROM privacy_erasures WHERE \"Id\" = {request.OperationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            bool sameScope = await ScopeMatchesAsync(request, cancellationToken);
            if (!sameScope)
                return await FinishBeginAsync(transaction, PrivacyErasureStatus.Conflict, "OPERATION_SCOPE_MISMATCH", [], cancellationToken);
            return await FinishBeginAsync(transaction, ParseStatus(existing.State), existing.ErrorCode,
                await GetWorkCoreAsync(request.OperationId, cancellationToken), cancellationToken);
        }

        PreviewSelection? selected = await ReadSelectionAsync(request.Selections, cancellationToken);
        if (selected is null)
            return await FinishBeginAsync(transaction, PrivacyErasureStatus.Conflict, "SELECTION_CHANGED", [], cancellationToken);
        if (await database.PrivacyErasureAssets.AnyAsync(item => selected.AssetIds.Contains(item.AssetId), cancellationToken))
            return await FinishBeginAsync(transaction, PrivacyErasureStatus.Conflict, "ERASURE_ALREADY_OWNS_ASSET", [], cancellationToken);

        var capturesById = new Dictionary<Guid, CaptureEntity>(selected.CaptureIds.Length);
        foreach (Guid captureId in selected.CaptureIds)
        {
            CaptureEntity? capture = await LockCaptureAsync(captureId, cancellationToken);
            if (capture is null || capture.RetentionDisposition == "Purging")
                return await FinishBeginAsync(transaction, PrivacyErasureStatus.Conflict, "CAPTURE_UNAVAILABLE", [], cancellationToken);
            capturesById.Add(captureId, capture);
            PrivacyErasureSelection selection = request.Selections.Single(item => item.CaptureId == captureId);
            if (capture.Revision != selection.ExpectedRevision)
                return await FinishBeginAsync(transaction, PrivacyErasureStatus.Conflict, "REVISION_CONFLICT", [], cancellationToken);
        }

        AssetEntity[] assets = await database.Assets.Where(asset => selected.AssetIds.Contains(asset.Id))
            .OrderBy(asset => asset.Id).ToArrayAsync(cancellationToken);
        if (assets.Length != selected.AssetIds.Length)
            return await FinishBeginAsync(transaction, PrivacyErasureStatus.Blocked, "ORIGINAL_UNAVAILABLE", [], cancellationToken);

        AssetRemovalOperationEntity[] removalOperations = await database.AssetRemovalOperations.AsNoTracking()
            .Where(item => selected.AssetIds.Contains(item.AssetId)).ToArrayAsync(cancellationToken);
        var removalsByAsset = removalOperations.GroupBy(item => item.AssetId).ToDictionary(group => group.Key, group => group.ToArray());
        bool hasLiveCapture = capturesById.Values.Any(capture => capture.RetentionDisposition != "Purged");
        string? marker = hasLiveCapture
            ? await database.SpoolQuotaObservations.AsNoTracking().Where(item => item.Id == 1)
                .Select(item => item.StorageMarker).SingleOrDefaultAsync(cancellationToken)
            : null;
        if (hasLiveCapture && marker is null)
            return await FinishBeginAsync(transaction, PrivacyErasureStatus.Blocked, "SPOOL_STORAGE_IDENTITY_UNKNOWN", [], cancellationToken);

        (PrivacyErasureStatus Status, string ErrorCode)? removalFailure = ValidateRemovalEvidence(
            request.Selections, assets, capturesById, removalsByAsset);
        if (removalFailure is { } failure)
            return await FinishBeginAsync(transaction, failure.Status, failure.ErrorCode, [], cancellationToken);

        string? blocker = await FindBlockerAsync(selected.AssetIds, cancellationToken);
        WorkBuildResult workResult = await BuildAuthorizedWorkAsync(request.Selections, assets, capturesById,
            removalsByAsset, marker, cancellationToken);
        if (workResult.ErrorCode is not null)
            return await FinishBeginAsync(transaction, PrivacyErasureStatus.Blocked, workResult.ErrorCode, [], cancellationToken);

        StageErasureOwnership(request.OperationId, workResult.Work, blocker, now);

        await PrivacyErasureSuppressionPersistence.RevokeGalleryAccessAsync(database, selected.AssetIds, now, cancellationToken);
        if (blocker is null)
        {
            await RedactWorkflowAsync(selected.AssetIds, selected.CaptureIds, now, cancellationToken);
            await MarkAuthorizedCapturesPurgingAsync(selected.CaptureIds, cancellationToken);
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(PrivacyErasureStatus.Pending, blocker, blocker is null ? workResult.Work : []);
    }

    public async Task<IReadOnlyList<PrivacyErasureAssetWork>> GetPendingAsync(Guid operationId, int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAndCollectionAssignmentsAsync(cancellationToken);
        PrivacyErasureOperationEntity? operation = await database.PrivacyErasureOperations
            .FromSqlInterpolated($"SELECT * FROM privacy_erasures WHERE \"Id\" = {operationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (operation is null || operation.State == "Completed")
        {
            await transaction.CommitAsync(cancellationToken);
            return [];
        }
        Guid[] captureIds = await database.PrivacyErasureAssets.Where(item => item.OperationId == operationId)
            .Select(item => item.CaptureId).Distinct().OrderBy(id => id).ToArrayAsync(cancellationToken);
        foreach (Guid captureId in captureIds)
            _ = await LockCaptureAsync(captureId, cancellationToken);

        Guid[] assetIds = await database.PrivacyErasureAssets.Where(item => item.OperationId == operationId)
            .Select(item => item.AssetId).ToArrayAsync(cancellationToken);
        operation.ErrorCode = await FindBlockerAsync(assetIds, cancellationToken);
        if (operation.ErrorCode is not null)
        {
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return [];
        }

        DateTimeOffset now = clock.GetUtcNow();
        await RedactAuthorizedWorkflowAsync(assetIds, captureIds, now, cancellationToken);
        PrivacyErasureAssetEntity[] pending = await AuthorizePendingWorkAsync(operation, operationId, maximumCount,
            now, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        PrivacyErasureAssetWork[] work = [.. pending.Select(item => new PrivacyErasureAssetWork(item.CaptureId,
            item.AssetId, item.SpoolKey, item.ExpectedSize, item.StorageMarker, item.KnownTransientKeys))];
        await transaction.CommitAsync(cancellationToken);
        return work;
    }

    public async Task<bool> MarkRemovingAsync(Guid operationId, Guid assetId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAndCollectionAssignmentsAsync(cancellationToken);
        PrivacyErasureAssetEntity? item = await database.PrivacyErasureAssets
            .FromSqlInterpolated($"SELECT * FROM privacy_erasure_assets WHERE \"OperationId\" = {operationId} AND \"AssetId\" = {assetId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null || item.State == "Completed")
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        _ = await LockCaptureAsync(item.CaptureId, cancellationToken);
        if (await FindBlockerAsync([assetId], cancellationToken) is string blocker)
        {
            item.State = "Waiting";
            item.ErrorCode = blocker;
            PrivacyErasureOperationEntity? blockedOperation = await database.PrivacyErasureOperations
                .SingleOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken);
            if (blockedOperation is not null) blockedOperation.ErrorCode = blocker;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        item.State = "Removing";
        item.ErrorCode = item.DerivativesErrorCode ?? item.OriginalsErrorCode;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task CompleteSurfaceAsync(Guid operationId, Guid assetId, string surface, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        PrivacyErasureAssetEntity? item = await database.PrivacyErasureAssets.SingleOrDefaultAsync(asset =>
            asset.OperationId == operationId && asset.AssetId == assetId, cancellationToken);
        if (item is null) return;
        if (surface == "derivatives")
        {
            item.DerivativesRemovedAt ??= now;
            item.DerivativesErrorCode = null;
        }
        else if (surface == "originals")
        {
            item.OriginalsRemovedAt ??= now;
            item.OriginalsErrorCode = null;
        }
        else throw new ArgumentOutOfRangeException(nameof(surface));
        item.ErrorCode = item.DerivativesErrorCode ?? item.OriginalsErrorCode;
        if (item.MetadataRedactedAt is not null && item.DerivativesRemovedAt is not null && item.OriginalsRemovedAt is not null)
            await CompleteErasedAssetAsync(item, operationId, assetId, now, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task FailSurfaceAsync(Guid operationId, Guid assetId, string surface, string errorCode,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        PrivacyErasureAssetEntity? item = await database.PrivacyErasureAssets.SingleOrDefaultAsync(asset =>
            asset.OperationId == operationId && asset.AssetId == assetId, cancellationToken);
        if (item is null || item.State == "Completed") return;
        item.State = "Failed";
        string safeError = FixedFailureCode(errorCode);
        if (surface == "derivatives") item.DerivativesErrorCode = safeError;
        else if (surface == "originals") item.OriginalsErrorCode = safeError;
        else throw new ArgumentOutOfRangeException(nameof(surface));
        item.ErrorCode = safeError;
        PrivacyErasureOperationEntity? operation = await database.PrivacyErasureOperations
            .SingleOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken);
        if (operation is not null) operation.ErrorCode = safeError;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<PrivacyErasureResult> GetResultAsync(Guid operationId, CancellationToken cancellationToken)
    {
        PrivacyErasureOperationEntity? operation = await database.PrivacyErasureOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);
        if (operation is null) return new(operationId, PrivacyErasureStatus.NotFound, "ERASURE_NOT_FOUND", []);
        PrivacyErasureAssetEntity[] assets = await database.PrivacyErasureAssets.AsNoTracking()
            .Where(item => item.OperationId == operationId).OrderBy(item => item.AssetId).ToArrayAsync(cancellationToken);
        PrivacyErasureAssetSurface[] surfaces = [.. assets.Select(item => new PrivacyErasureAssetSurface(item.CaptureId, item.AssetId,
            item.MetadataRedactedAt is null ? PrivacyErasureSurfaceStatus.Pending : PrivacyErasureSurfaceStatus.Completed,
            SurfaceStatus(item.DerivativesRemovedAt, item.DerivativesErrorCode),
            SurfaceStatus(item.OriginalsRemovedAt, item.OriginalsErrorCode), item.ErrorCode))];
        return new(operationId, ParseStatus(operation.State), operation.ErrorCode, surfaces);
    }

    public async Task<IReadOnlyList<Guid>> GetPendingOperationIdsAsync(Guid? afterOperationId, int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        IQueryable<PrivacyErasureOperationEntity> query = database.PrivacyErasureOperations.AsNoTracking()
            .Where(item => item.State != "Completed");
        if (afterOperationId is Guid cursor) query = query.Where(item => item.Id.CompareTo(cursor) > 0);
        return await query.OrderBy(item => item.Id).Take(maximumCount).Select(item => item.Id)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PrivacyErasureOperationSummary>> ListAsync(int maximumCount, bool pendingOnly,
        DateTimeOffset? beforeRequestedAt, Guid? beforeOperationId, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 101) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        IQueryable<PrivacyErasureOperationEntity> query = database.PrivacyErasureOperations.AsNoTracking();
        if (pendingOnly) query = query.Where(item => item.State != "Completed");
        if (beforeRequestedAt is DateTimeOffset requestedAt && beforeOperationId is Guid operationId)
            query = query.Where(item => item.RequestedAt < requestedAt
                || item.RequestedAt == requestedAt && item.Id.CompareTo(operationId) < 0);
        var operations = await query.OrderByDescending(item => item.RequestedAt)
            .ThenByDescending(item => item.Id).Take(maximumCount).Select(item => new
            {
                item.Id, item.State, item.RequestedAt, item.ErrorCode,
                AssetCount = database.PrivacyErasureAssets.Count(asset => asset.OperationId == item.Id),
                CompletedCount = database.PrivacyErasureAssets.Count(asset => asset.OperationId == item.Id && asset.State == "Completed"),
            }).ToArrayAsync(cancellationToken);
        return operations.Select(item => new PrivacyErasureOperationSummary(item.Id, ParseStatus(item.State),
            item.RequestedAt, item.AssetCount, item.CompletedCount, item.AssetCount - item.CompletedCount,
            item.ErrorCode)).ToArray();
    }

    public Task<PrivacyErasureSuppressionManifest> CreateSuppressionExportAsync(CancellationToken cancellationToken) =>
        suppression.CreateSuppressionExportAsync(cancellationToken);

    public Task<PrivacyErasureSuppressionManifest?> GetSuppressionExportAsync(Guid snapshotId,
        CancellationToken cancellationToken) => suppression.GetSuppressionExportAsync(snapshotId, cancellationToken);

    public Task<PrivacyErasureSuppressionPage?> GetSuppressionExportPageAsync(Guid snapshotId, int pageIndex,
        CancellationToken cancellationToken) => suppression.GetSuppressionExportPageAsync(snapshotId, pageIndex, cancellationToken);

    public Task<PrivacyErasureImportStatus> BeginSuppressionReconciliationAsync(
        PrivacyErasureSuppressionManifest manifest, Guid expectedSnapshotId, CancellationToken cancellationToken) =>
        suppression.BeginSuppressionReconciliationAsync(manifest, expectedSnapshotId, cancellationToken);

    public Task<PrivacyErasureImportStatus> StageSuppressionPageAsync(PrivacyErasureSuppressionPage page,
        CancellationToken cancellationToken) => suppression.StageSuppressionPageAsync(page, cancellationToken);

    public Task<PrivacyErasureImportStatus> CompleteSuppressionReconciliationAsync(Guid snapshotId,
        CancellationToken cancellationToken) => suppression.CompleteSuppressionReconciliationAsync(snapshotId, cancellationToken);
    private async Task<PreviewSelection?> ReadSelectionAsync(IReadOnlyList<PrivacyErasureSelection> selections,
        CancellationToken cancellationToken)
    {
        if (selections.Count == 0) return null;
        Guid[] ids = [.. selections.SelectMany(item => item.AssetIds)];
        Guid[] captureIds = [.. selections.Select(item => item.CaptureId).OrderBy(id => id)];
        var current = await (from membership in database.CaptureAssets.AsNoTracking()
            join capture in database.Captures.AsNoTracking() on membership.CaptureId equals capture.Id
            join asset in database.Assets.AsNoTracking() on membership.AssetId equals asset.Id
            where ids.Contains(asset.Id)
            select new { AssetId = asset.Id, CaptureId = capture.Id, capture.Revision }).ToArrayAsync(cancellationToken);
        if (current.Length != ids.Length || current.Select(item => item.AssetId).Distinct().Count() != ids.Length)
            return null;
        foreach (PrivacyErasureSelection selection in selections)
        {
            if (current.Any(item => item.CaptureId == selection.CaptureId && item.Revision != selection.ExpectedRevision))
                return null;
            if (selection.AssetIds.Any(id => !current.Any(item => item.AssetId == id && item.CaptureId == selection.CaptureId)))
                return null;
            Guid[] exactMembership = await database.CaptureAssets.AsNoTracking()
                .Where(member => member.CaptureId == selection.CaptureId).OrderBy(member => member.AssetId)
                .Select(member => member.AssetId).ToArrayAsync(cancellationToken);
            if (!exactMembership.SequenceEqual(selection.AssetIds.OrderBy(id => id))) return null;
        }
        return new(captureIds, [.. ids.OrderBy(id => id)]);
    }

    private async Task<bool> ScopeMatchesAsync(PrivacyErasureRequest request, CancellationToken cancellationToken)
    {
        var stored = await database.PrivacyErasureAssets.AsNoTracking()
            .Where(item => item.OperationId == request.OperationId).OrderBy(item => item.AssetId)
            .Select(item => new { item.AssetId, item.CaptureId }).ToArrayAsync(cancellationToken);
        var requested = request.Selections.SelectMany(selection => selection.AssetIds
                .Select(assetId => new { AssetId = assetId, CaptureId = selection.CaptureId }))
            .OrderBy(item => item.AssetId).ToArray();
        return stored.SequenceEqual(requested);
    }

    private async Task<string?> FindBlockerAsync(Guid[] assetIds, CancellationToken cancellationToken)
    {
        if (await database.PreservationHoldAssets.AnyAsync(member => assetIds.Contains(member.AssetId)
            && database.PreservationHolds.Any(hold => hold.Id == member.HoldId && hold.ResolvedAt == null), cancellationToken))
            return "PRESERVATION_HOLD_ACTIVE";
        if (await database.Deliveries.AnyAsync(delivery => assetIds.Contains(delivery.AssetId)
            && delivery.State == DeliveryState.Running, cancellationToken))
            return "DELIVERY_RECONCILIATION_REQUIRED";
        if (await database.CameraUploadAdmissions.AnyAsync(admission => assetIds.Contains(admission.AssetId ?? Guid.Empty)
            && (admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Admitted
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Receiving
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Received
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Handoff), cancellationToken))
            return "CAMERA_RECEIVE_RECONCILIATION_REQUIRED";
        if (await database.Operations.AnyAsync(operation => assetIds.Contains(operation.AssetId)
            && (operation.State == "Writing" || operation.State == "Promoted" || operation.State == "Received"), cancellationToken))
            return "INGEST_RECONCILIATION_REQUIRED";
        return null;
    }

    private async Task RedactAuthorizedWorkflowAsync(Guid[] assetIds, Guid[] captureIds, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await RedactWorkflowAsync(assetIds, captureIds, now, cancellationToken);
        foreach (Guid captureId in captureIds)
        {
            CaptureEntity? capture = await database.Captures.SingleOrDefaultAsync(item => item.Id == captureId, cancellationToken);
            if (capture is null) continue;
            if (capture.RetentionDisposition != "Purged") capture.RetentionDisposition = "Purging";
            capture.RetentionPaused = true;
        }
    }

    private static (PrivacyErasureStatus Status, string ErrorCode)? ValidateRemovalEvidence(
        IReadOnlyList<PrivacyErasureSelection> selections, AssetEntity[] assets,
        IReadOnlyDictionary<Guid, CaptureEntity> capturesById,
        IReadOnlyDictionary<Guid, AssetRemovalOperationEntity[]> removalsByAsset)
    {
        foreach (AssetEntity asset in assets)
        {
            CaptureEntity capture = capturesById[selections.Single(selection => selection.AssetIds.Contains(asset.Id)).CaptureId];
            if (capture.RetentionDisposition == "Purged")
            {
                bool hasCompletedExactRemoval = removalsByAsset.TryGetValue(asset.Id, out AssetRemovalOperationEntity[]? removalSet)
                    && removalSet.Length == 1
                    && removalSet[0].CaptureId == capture.Id
                    && removalSet[0].State == "Removed"
                    && removalSet[0].CompletedAt is not null
                    && removalSet[0].SpoolKey == asset.SpoolKey
                    && removalSet[0].ExpectedSize == asset.Size
                    && Guid.TryParseExact(removalSet[0].StorageMarker, "N", out _)
                    && removalSet[0].KnownTransientKeys.Length <= MaximumTransientKeys;
                if (asset.State != "Removed" || !hasCompletedExactRemoval)
                    return (PrivacyErasureStatus.Blocked, "ORIGINAL_REMOVAL_NOT_AUTHORIZED");
            }
            else if (asset.State != "Spooled" || removalsByAsset.ContainsKey(asset.Id))
            {
                return (PrivacyErasureStatus.Conflict,
                    removalsByAsset.ContainsKey(asset.Id) ? "REMOVAL_ALREADY_OWNED" : "ORIGINAL_UNAVAILABLE");
            }
        }
        return null;
    }

    private async Task<WorkBuildResult> BuildAuthorizedWorkAsync(IReadOnlyList<PrivacyErasureSelection> selections,
        AssetEntity[] assets, IReadOnlyDictionary<Guid, CaptureEntity> capturesById,
        IReadOnlyDictionary<Guid, AssetRemovalOperationEntity[]> removalsByAsset, string? marker,
        CancellationToken cancellationToken)
    {
        var work = new List<PrivacyErasureAssetWork>(assets.Length);
        foreach (AssetEntity asset in assets)
        {
            CaptureEntity capture = capturesById[selections.Single(selection => selection.AssetIds.Contains(asset.Id)).CaptureId];
            AssetRemovalOperationEntity? completedRemoval = capture.RetentionDisposition == "Purged"
                ? removalsByAsset[asset.Id][0]
                : null;
            string[] transientKeys = completedRemoval?.KnownTransientKeys ?? await ReadTransientKeysAsync(asset, cancellationToken);
            if (transientKeys.Length > MaximumTransientKeys) return new("TRANSIENT_COPY_SET_UNBOUNDED", []);
            PrivacyErasureSelection selection = selections.Single(item => item.AssetIds.Contains(asset.Id));
            work.Add(new(selection.CaptureId, asset.Id, completedRemoval?.SpoolKey ?? asset.SpoolKey,
                completedRemoval?.ExpectedSize ?? asset.Size, completedRemoval?.StorageMarker ?? marker!, transientKeys));
        }
        return new(null, work);
    }

    private void StageErasureOwnership(Guid operationId, IReadOnlyList<PrivacyErasureAssetWork> work,
        string? blocker, DateTimeOffset now)
    {
        database.PrivacyErasureOperations.Add(new PrivacyErasureOperationEntity
        {
            Id = operationId, RequestedAt = now, State = "Pending", ErrorCode = blocker,
        });
        foreach (PrivacyErasureAssetWork item in work)
        {
            database.PrivacyErasureAssets.Add(new PrivacyErasureAssetEntity
            {
                AssetId = item.AssetId, CaptureId = item.CaptureId, OperationId = operationId,
                SpoolKey = item.SpoolKey, ExpectedSize = item.ExpectedSize, StorageMarker = item.StorageMarker,
                KnownTransientKeys = [.. item.KnownTransientKeys],
                RequestedAt = now, State = blocker is null ? "Authorized" : "Waiting",
                MetadataRedactedAt = blocker is null ? now : null,
            });
        }
    }

    private async Task MarkAuthorizedCapturesPurgingAsync(Guid[] captureIds, CancellationToken cancellationToken)
    {
        foreach (Guid captureId in captureIds)
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, cancellationToken);
            if (capture.RetentionDisposition != "Purged") capture.RetentionDisposition = "Purging";
            capture.RetentionPaused = true;
        }
    }

    private async Task<PrivacyErasureAssetEntity[]> AuthorizePendingWorkAsync(PrivacyErasureOperationEntity operation,
        Guid operationId, int maximumCount, DateTimeOffset now, CancellationToken cancellationToken)
    {
        PrivacyErasureAssetEntity[] pending = await database.PrivacyErasureAssets
            .Where(item => item.OperationId == operationId && item.State != "Completed")
            .OrderBy(item => item.AssetId).Take(maximumCount).ToArrayAsync(cancellationToken);
        foreach (PrivacyErasureAssetEntity item in pending)
        {
            item.MetadataRedactedAt ??= now;
            item.State = "Authorized";
        }
        operation.State = pending.Length == 0 ? "Completed" : "Pending";
        if (pending.Length == 0) operation.CompletedAt ??= now;
        operation.ErrorCode = null;
        return pending;
    }

    private async Task CompleteErasedAssetAsync(PrivacyErasureAssetEntity item, Guid operationId, Guid assetId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        item.State = "Completed";
        item.CompletedAt ??= now;
        await CompleteCaptureErasureAsync(item.CaptureId, assetId, cancellationToken);
        await MarkAssetRemovedAsync(assetId, cancellationToken);
        await CompleteOperationIfDoneAsync(operationId, assetId, now, cancellationToken);
    }

    private async Task CompleteCaptureErasureAsync(Guid captureId, Guid assetId, CancellationToken cancellationToken)
    {
        CaptureEntity? capture = await database.Captures.SingleOrDefaultAsync(candidate => candidate.Id == captureId, cancellationToken);
        Guid[] captureAssetIds = capture is null ? [] : await database.CaptureAssets.AsNoTracking()
            .Where(member => member.CaptureId == captureId).Select(member => member.AssetId).ToArrayAsync(cancellationToken);
        Guid[] completedAssetIds = captureAssetIds.Length == 0 ? [] : await database.PrivacyErasureAssets
            .AsNoTracking().Where(erasureAsset => erasureAsset.CaptureId == captureId
                && captureAssetIds.Contains(erasureAsset.AssetId) && erasureAsset.State == "Completed")
            .Select(erasureAsset => erasureAsset.AssetId).ToArrayAsync(cancellationToken);
        bool allAssetsErased = capture is not null && captureAssetIds.Length > 0
            && captureAssetIds.All(memberAssetId => memberAssetId == assetId || completedAssetIds.Contains(memberAssetId));
        if (!allAssetsErased || capture!.RetentionDisposition == "Purged") return;
        capture.RetentionDisposition = "Purged";
        capture.RetentionPaused = true;
        capture.Revision++;
    }

    private async Task MarkAssetRemovedAsync(Guid assetId, CancellationToken cancellationToken)
    {
        if (database.Assets.Local.SingleOrDefault(asset => asset.Id == assetId) is { } tracked)
            tracked.State = "Removed";
        else
            await database.Assets.Where(asset => asset.Id == assetId)
                .ExecuteUpdateAsync(update => update.SetProperty(asset => asset.State, "Removed"), cancellationToken);
    }

    private async Task CompleteOperationIfDoneAsync(Guid operationId, Guid assetId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int remaining = await database.PrivacyErasureAssets.CountAsync(asset => asset.OperationId == operationId
            && asset.AssetId != assetId && asset.State != "Completed", cancellationToken);
        PrivacyErasureOperationEntity? operation = await database.PrivacyErasureOperations
            .SingleOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken);
        if (operation is null) return;
        if (remaining == 0)
        {
            operation.State = "Completed";
            operation.CompletedAt ??= now;
            operation.ErrorCode = null;
        }
        else
        {
            operation.ErrorCode = await database.PrivacyErasureAssets.Where(asset => asset.OperationId == operationId)
                .Select(asset => asset.ErrorCode).FirstOrDefaultAsync(error => error != null, cancellationToken);
        }
    }

    private async Task RedactWorkflowAsync(Guid[] assetIds, Guid[] captureIds, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await database.Assets.Where(asset => assetIds.Contains(asset.Id)).ExecuteUpdateAsync(update => update
            .SetProperty(asset => asset.SourceId, Guid.Empty)
            .SetProperty(asset => asset.OriginalName, string.Empty)
            .SetProperty(asset => asset.Sha256, string.Empty)
            .SetProperty(asset => asset.RecoveryCorrelation, Guid.Empty)
            .SetProperty(asset => asset.IngestedAt, DateTimeOffset.UnixEpoch)
            .SetProperty(asset => asset.MetadataState, "Erased")
            .SetProperty(asset => asset.MetadataOutcome, "Erased")
            .SetProperty(asset => asset.DetectedMediaType, (string?)null)
            .SetProperty(asset => asset.MetadataRole, (string?)null)
            .SetProperty(asset => asset.CaptureTimeLocal, (DateTime?)null)
            .SetProperty(asset => asset.CaptureTimeOffset, (TimeSpan?)null)
            .SetProperty(asset => asset.CaptureTimeFractionalDigits, (int?)null)
            .SetProperty(asset => asset.CaptureTimeTag, (string?)null)
            .SetProperty(asset => asset.CameraMake, (string?)null)
            .SetProperty(asset => asset.CameraMakeTag, (string?)null)
            .SetProperty(asset => asset.CameraModel, (string?)null)
            .SetProperty(asset => asset.CameraModelTag, (string?)null)
            .SetProperty(asset => asset.CameraSerial, (string?)null)
            .SetProperty(asset => asset.CameraSerialTag, (string?)null)
            .SetProperty(asset => asset.CaptureTimeUncertain, false)
            .SetProperty(asset => asset.ExtensionMismatch, false)
            .SetProperty(asset => asset.MetadataReason, (string?)null)
            .SetProperty(asset => asset.MetadataLeaseId, (Guid?)null)
            .SetProperty(asset => asset.MetadataLeaseUntil, (DateTimeOffset?)null)
            .SetProperty(asset => asset.MetadataRetryAt, (DateTimeOffset?)null), cancellationToken);

        await database.Operations.Where(operation => assetIds.Contains(operation.AssetId)).ExecuteUpdateAsync(update => update
            .SetProperty(operation => operation.SourceId, Guid.Empty)
            .SetProperty(operation => operation.OriginalName, string.Empty)
            .SetProperty(operation => operation.PartialKey, string.Empty)
            .SetProperty(operation => operation.FinalKey, (string?)null)
            .SetProperty(operation => operation.Sha256, (string?)null)
            .SetProperty(operation => operation.QuarantineReason, (string?)null), cancellationToken);
        await database.CameraUploadAdmissions.Where(admission => admission.AssetId != null
                && assetIds.Contains(admission.AssetId.Value))
            // The durable erasure row already holds the exact ReceiveKey transient evidence; deleting the
            // admission avoids replacing its required LoginId/SourceId foreign keys with fabricated values.
            .ExecuteDeleteAsync(cancellationToken);

        foreach (Guid captureId in captureIds)
        {
            Guid[] remaining = await database.CaptureAssets.AsNoTracking().Where(member => member.CaptureId == captureId
                && !assetIds.Contains(member.AssetId)).Select(member => member.AssetId).ToArrayAsync(cancellationToken);
            if (remaining.Length > 0) continue;
            await database.Captures.Where(capture => capture.Id == captureId).ExecuteUpdateAsync(update => update
                .SetProperty(capture => capture.SourceId, Guid.Empty)
                .SetProperty(capture => capture.CollectionId, ShutterSpool.Domain.Collections.CollectionName.BuiltInId)
                .SetProperty(capture => capture.NormalizedStem, string.Empty)
                .SetProperty(capture => capture.Rating, 0)
                .SetProperty(capture => capture.Label, (string?)null)
                .SetProperty(capture => capture.Tags, Array.Empty<string>())
                .SetProperty(capture => capture.Held, false)
                .SetProperty(capture => capture.IsFavorite, false)
                .SetProperty(capture => capture.NoDeliveryIntent, false)
                .SetProperty(capture => capture.RoutingSnapshotJson, (string?)null)
                .SetProperty(capture => capture.ReleasedAt, (DateTimeOffset?)null)
                .SetProperty(capture => capture.ReleasedRevision, (int?)null)
                .SetProperty(capture => capture.ReleasedRating, (int?)null)
                .SetProperty(capture => capture.ReleasedLabel, (string?)null)
                .SetProperty(capture => capture.ReleasedTags, Array.Empty<string>())
                .SetProperty(capture => capture.ReleasedCollectionId, (Guid?)null)
                .SetProperty(capture => capture.ReleasedCollectionName, (string?)null)
                .SetProperty(capture => capture.ReleasedCollectionPath, (string?)null)
                .SetProperty(capture => capture.ReleaseDueAt, (DateTimeOffset?)null)
                .SetProperty(capture => capture.TrashReason, (string?)null)
                .SetProperty(capture => capture.TrashEnteredAt, (DateTimeOffset?)null)
                .SetProperty(capture => capture.RecoverableUntil, (DateTimeOffset?)null)
                .SetProperty(capture => capture.RestoreProtectedUntil, (DateTimeOffset?)null)
                .SetProperty(capture => capture.RetentionFactsDirty, false)
                .SetProperty(capture => capture.RetentionTier, (int?)null)
                .SetProperty(capture => capture.RetentionPressureEligibleAt, (DateTimeOffset?)null)
                .SetProperty(capture => capture.RetentionTrashEligibleAt, (DateTimeOffset?)null)
                .SetProperty(capture => capture.FirstAcceptedAt, DateTimeOffset.UnixEpoch)
                .SetProperty(capture => capture.LastPrimaryIngestedAt, DateTimeOffset.UnixEpoch), cancellationToken);
        }

        await database.CaptureAssets.Where(member => assetIds.Contains(member.AssetId))
            .ExecuteUpdateAsync(update => update.SetProperty(member => member.GroupingReason, string.Empty), cancellationToken);

        await database.RoutingDecisions.Where(item => assetIds.Contains(item.AssetId)).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.RuleId, (string?)null).SetProperty(item => item.RuleName, (string?)null)
            .SetProperty(item => item.TargetPath, (string?)null).SetProperty(item => item.TargetPathKey, (string?)null)
            .SetProperty(item => item.Explanation, "Privacy erased")
            .SetProperty(item => item.CollectionId, (Guid?)null)
            .SetProperty(item => item.CollectionName, (string?)null)
            .SetProperty(item => item.CollectionPath, (string?)null)
            .SetProperty(item => item.RuleSnapshotJson, (string?)null), cancellationToken);
        await database.Deliveries.Where(item => assetIds.Contains(item.AssetId)).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.TargetPath, string.Empty).SetProperty(item => item.RetryReason, (string?)null)
            .SetProperty(item => item.ConfirmationEvidence, (string?)null), cancellationToken);
        await database.DeliveryAttempts.Where(item => assetIds.Contains(item.AssetId)).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.WorkerId, string.Empty).SetProperty(item => item.ErrorMessage, (string?)null)
            .SetProperty(item => item.ErrorCode, (string?)null).SetProperty(item => item.TraceId, string.Empty)
            .SetProperty(item => item.ConfirmationEvidence, (string?)null), cancellationToken);
        await database.RoutingActions.Where(item => assetIds.Contains(item.AssetId)).ExecuteUpdateAsync(update => update
            .SetProperty(item => item.ProposedTarget, (string?)null).SetProperty(item => item.Fingerprint, string.Empty), cancellationToken);
        await database.PreviewPrewarms.Where(item => assetIds.Contains(item.AssetId))
            .ExecuteDeleteAsync(cancellationToken);
        await database.PreservationHoldAssets.Where(member => assetIds.Contains(member.AssetId))
            .ExecuteDeleteAsync(cancellationToken);
        await database.GalleryProtectionAssets.Where(member => assetIds.Contains(member.AssetId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<string[]> ReadTransientKeysAsync(AssetEntity asset, CancellationToken cancellationToken)
    {
        string[] ingestKeys = await database.Operations.AsNoTracking().Where(operation => operation.AssetId == asset.Id)
            .Select(operation => operation.PartialKey).ToArrayAsync(cancellationToken);
        string[] cameraKeys = await database.CameraUploadAdmissions.AsNoTracking()
            .Where(admission => admission.AssetId == asset.Id && admission.ReceiveKey != null)
            .Select(admission => admission.ReceiveKey!).ToArrayAsync(cancellationToken);
        return [.. ingestKeys.Concat(cameraKeys).Where(key => !string.IsNullOrWhiteSpace(key) && key != asset.SpoolKey).Distinct(StringComparer.Ordinal)];
    }

    private async Task<IReadOnlyList<PrivacyErasureAssetWork>> GetWorkCoreAsync(Guid operationId,
        CancellationToken cancellationToken)
    {
        PrivacyErasureAssetEntity[] pending = await database.PrivacyErasureAssets
            .Where(item => item.OperationId == operationId && (item.State == "Authorized" || item.State == "Failed"))
            .OrderBy(item => item.AssetId).Take(100).ToArrayAsync(cancellationToken);
        return pending.Select(item => new PrivacyErasureAssetWork(item.CaptureId, item.AssetId, item.SpoolKey,
            item.ExpectedSize, item.StorageMarker, item.KnownTransientKeys)).ToArray();
    }

    private Task<CaptureEntity?> LockCaptureAsync(Guid captureId, CancellationToken cancellationToken) =>
        database.Captures.FromSqlInterpolated($"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private Task<int> LockRoutingAndCollectionAssignmentsAsync(CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlRawAsync("""
            SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0));
            SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-collection-assignment', 0));
            """,
            cancellationToken);

    private static Task<PrivacyErasureBeginResult> FinishBeginAsync(IDbContextTransaction transaction,
        PrivacyErasureStatus status, string? errorCode, IReadOnlyList<PrivacyErasureAssetWork> work,
        CancellationToken cancellationToken) => FinishAsync(transaction,
        new PrivacyErasureBeginResult(status, errorCode, work), cancellationToken);

    private static async Task<T> FinishAsync<T>(IDbContextTransaction transaction, T result,
        CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static PrivacyErasureStatus ParseStatus(string value) => value switch
    {
        "Completed" => PrivacyErasureStatus.Completed,
        "Blocked" => PrivacyErasureStatus.Blocked,
        "Conflict" => PrivacyErasureStatus.Conflict,
        "NotFound" => PrivacyErasureStatus.NotFound,
        _ => PrivacyErasureStatus.Pending,
    };

    private static PrivacyErasureSurfaceStatus SurfaceStatus(DateTimeOffset? completedAt, string? errorCode) =>
        completedAt is not null ? PrivacyErasureSurfaceStatus.Completed
        : errorCode is not null ? PrivacyErasureSurfaceStatus.Blocked : PrivacyErasureSurfaceStatus.Pending;

    private static string FixedFailureCode(string code) => code switch
    {
        "storage_root_unavailable" or "storage_marker_unavailable" or "storage_marker_mismatch" => code,
        "storage_key_asset_mismatch" or "original_size_mismatch" or "authorized_key_is_linked" => code,
        _ => "erasure_surface_unavailable",
    };

    private sealed record WorkBuildResult(string? ErrorCode, List<PrivacyErasureAssetWork> Work);
    private sealed record PreviewSelection(Guid[] CaptureIds, Guid[] AssetIds);
}
