using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Spool;
using System.Globalization;

namespace ShutterSpool.Application.Retention;

public sealed class PrivacyErasureService(IPrivacyErasureStore store, PreviewAssetUseGate previewUseGate,
    IOriginalAssetLock originalAssetLock, IPreviewAssetCacheInvalidator previewCache,
    IExactOriginalRemoval originalRemoval, TimeProvider clock)
{
    public const int MaximumCaptureCount = 20;
    public const int MaximumAssetCount = 100;

    public async Task<PrivacyErasureResult> EraseAsync(PrivacyErasureRequest request,
        CancellationToken cancellationToken)
    {
        Validate(request);
        PrivacyErasureBeginResult begin = await store.BeginAsync(request, clock.GetUtcNow(), cancellationToken);
        if (begin.Status != PrivacyErasureStatus.Pending)
            return new(request.OperationId, begin.Status, begin.ErrorCode, []);
        if (begin.Work.Count == 0)
            return await store.GetResultAsync(request.OperationId, cancellationToken);

        await RemoveAuthorizedAsync(request.OperationId, begin.Work, cancellationToken);
        return await store.GetResultAsync(request.OperationId, cancellationToken);
    }

    public Task<PrivacyErasurePreview> PreviewAsync(IReadOnlyList<PrivacyErasureSelection> selections,
        CancellationToken cancellationToken)
    {
        ValidateSelections(selections);
        return store.PreviewAsync(selections, cancellationToken);
    }

    public async Task<PrivacyErasureOperationPage> ListAsync(int pageSize, bool pendingOnly, string? cursor,
        CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        (DateTimeOffset? beforeRequestedAt, Guid? beforeOperationId) = ParseCursor(cursor);
        IReadOnlyList<PrivacyErasureOperationSummary> rows = await store.ListAsync(pageSize + 1, pendingOnly,
            beforeRequestedAt, beforeOperationId, cancellationToken);
        bool hasMore = rows.Count > pageSize;
        PrivacyErasureOperationSummary[] operations = [.. rows.Take(pageSize)];
        return new(operations, hasMore ? FormatCursor(operations[^1]) : null);
    }

    private static string FormatCursor(PrivacyErasureOperationSummary operation) =>
        $"{operation.RequestedAt.UtcTicks:x16}.{operation.OperationId:N}";

    private static (DateTimeOffset? RequestedAt, Guid? OperationId) ParseCursor(string? cursor)
    {
        if (cursor is null) return (null, null);
        if (cursor.Length != 49 || cursor[16] != '.'
            || !long.TryParse(cursor.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture,
                out long ticks)
            || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks
            || !Guid.TryParseExact(cursor[17..], "N", out Guid operationId))
            throw new ArgumentException("The erasure operation cursor is invalid.", nameof(cursor));
        return (new DateTimeOffset(ticks, TimeSpan.Zero), operationId);
    }

    public Task<PrivacyErasureResult> GetAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An erasure operation identity is required.");
        return store.GetResultAsync(operationId, cancellationToken);
    }

    public async Task<PrivacyErasureResult> ContinueAsync(Guid operationId, bool confirmed,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || !confirmed)
            throw new ArgumentException("An operation identity and explicit confirmation are required.");

        IReadOnlyList<PrivacyErasureAssetWork> pending = await store.GetPendingAsync(operationId,
            MaximumAssetCount, cancellationToken);
        if (pending.Count == 0) return await store.GetResultAsync(operationId, cancellationToken);

        await RemoveAuthorizedAsync(operationId, pending, cancellationToken);
        return await store.GetResultAsync(operationId, cancellationToken);
    }

    public async Task<PrivacyErasureImportStatus> DrainPendingAsync(Guid snapshotId,
        CancellationToken cancellationToken)
    {
        Guid? cursor = null;
        while (true)
        {
            IReadOnlyList<Guid> operationIds = await store.GetPendingOperationIdsAsync(cursor, 100,
                cancellationToken);
            if (operationIds.Count == 0) return new(snapshotId, 0, 0, true, null);

            foreach (Guid operationId in operationIds)
            {
                PrivacyErasureResult result = await ContinueAsync(operationId, true, cancellationToken);
                if (result.Status != PrivacyErasureStatus.Completed)
                    return new(snapshotId, 0, 0, false,
                        result.ErrorCode is "PRESERVATION_HOLD_ACTIVE" or "DELIVERY_RECONCILIATION_REQUIRED"
                            or "INGEST_RECONCILIATION_REQUIRED" or "CAMERA_RECEIVE_RECONCILIATION_REQUIRED"
                            ? "RESTORE_ERASURE_BLOCKED" : "RESTORE_ERASURE_PENDING");
            }
            cursor = operationIds[^1];
        }
    }

    public async Task<int> ProcessPendingAsync(int maximumOperations, CancellationToken cancellationToken)
    {
        if (maximumOperations is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        IReadOnlyList<Guid> operationIds = await store.GetPendingOperationIdsAsync(null, maximumOperations,
            cancellationToken);
        foreach (Guid operationId in operationIds)
            _ = await ContinueAsync(operationId, true, cancellationToken);
        return operationIds.Count;
    }

    private async Task RemoveAuthorizedAsync(Guid operationId, IReadOnlyList<PrivacyErasureAssetWork> work,
        CancellationToken cancellationToken)
    {
        foreach (PrivacyErasureAssetWork asset in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RemoveAuthorizedAssetAsync(operationId, asset, cancellationToken);
        }
    }

    private async Task RemoveAuthorizedAssetAsync(Guid operationId, PrivacyErasureAssetWork asset,
        CancellationToken cancellationToken)
    {
        using IDisposable previewFence = await previewUseGate.FenceAsync(asset.AssetId, cancellationToken);
        await using IOriginalAssetLockLease storageFence = await originalAssetLock.AcquireExclusiveAsync(
            asset.AssetId, asset.SpoolKey, cancellationToken);
        if (!await store.MarkRemovingAsync(operationId, asset.AssetId, cancellationToken)) return;

        await RemoveDerivativesAsync(operationId, asset.AssetId, cancellationToken);
        await RemoveOriginalAsync(operationId, asset, cancellationToken);
    }

    private async Task RemoveDerivativesAsync(Guid operationId, Guid assetId, CancellationToken cancellationToken)
    {
        try
        {
            await previewCache.RemoveAssetAsync(assetId, cancellationToken);
            await store.CompleteSurfaceAsync(operationId, assetId, "derivatives", clock.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await store.FailSurfaceAsync(operationId, assetId, "derivatives", "derivative_surface_unavailable",
                clock.GetUtcNow(), cancellationToken);
        }
    }

    private async Task RemoveOriginalAsync(Guid operationId, PrivacyErasureAssetWork asset,
        CancellationToken cancellationToken)
    {
        try
        {
            ExactOriginalRemovalResult result = await originalRemoval.RemoveAsync(new(asset.AssetId,
                asset.SpoolKey, asset.ExpectedSize, asset.StorageMarker, asset.KnownTransientKeys), cancellationToken);
            if (result.Status is ExactOriginalRemovalStatus.Removed or ExactOriginalRemovalStatus.AlreadyAbsent)
                await store.CompleteSurfaceAsync(operationId, asset.AssetId, "originals", clock.GetUtcNow(), cancellationToken);
            else
                await store.FailSurfaceAsync(operationId, asset.AssetId, "originals",
                    result.FailureCode ?? "storage_removal_blocked", clock.GetUtcNow(), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await store.FailSurfaceAsync(operationId, asset.AssetId, "originals", "erasure_surface_unavailable",
                clock.GetUtcNow(), cancellationToken);
        }
    }

    private static void Validate(PrivacyErasureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty || !request.Confirmed || request.Selections is null
            || request.Selections.Count is < 1 or > MaximumCaptureCount)
            throw new ArgumentException("A finite Capture and Asset selection with explicit confirmation is required.");

        ValidateSelections(request.Selections);
    }

    private static void ValidateSelections(IReadOnlyList<PrivacyErasureSelection>? selections)
    {
        if (selections is null || selections.Count is < 1 or > MaximumCaptureCount)
            throw new ArgumentException("A finite Capture and Asset selection is required.");

        var captures = new HashSet<Guid>();
        var assets = new HashSet<Guid>();
        foreach (PrivacyErasureSelection selection in selections)
        {
            if (selection.CaptureId == Guid.Empty || selection.ExpectedRevision < 0
                || !captures.Add(selection.CaptureId) || selection.AssetIds is null || selection.AssetIds.Count == 0)
                throw new ArgumentException("Each selected Capture needs one finite, unique Asset membership.");
            foreach (Guid assetId in selection.AssetIds)
                if (assetId == Guid.Empty || !assets.Add(assetId))
                    throw new ArgumentException("Selected Assets must be unique and valid.");
        }
        if (assets.Count > MaximumAssetCount)
            throw new ArgumentException($"An erasure can select at most {MaximumAssetCount} Assets.");
    }
}
