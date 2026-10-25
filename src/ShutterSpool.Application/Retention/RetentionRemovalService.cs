using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Spool;

namespace ShutterSpool.Application.Retention;

public sealed class RetentionRemovalService(IRetentionRemovalStore store, ISpoolQuotaStore quota,
    IRetentionSettingsStore settingsStore,
    PreviewAssetUseGate previewUseGate, IOriginalAssetLock originalAssetLock,
    IPreviewAssetCacheInvalidator previewCache, IExactOriginalRemoval originalRemoval, TimeProvider clock,
    IRetentionControllerLock controllerLock, IPreviewCacheReclaimer? cacheReclaimer = null)
{
    public async Task<RetentionOperationResult> DeletePermanentlyAsync(Guid captureId, int expectedRevision,
        bool confirmed, CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || expectedRevision < 0 || !confirmed)
            throw new ArgumentException("A valid Capture revision and explicit confirmation are required.");

        SpoolQuotaSnapshot inventory = await quota.GetSnapshotAsync(cancellationToken);
        if (!inventory.IsKnown)
            return new(RetentionOperationStatus.Blocked, await store.GetSummaryAsync(captureId,
                clock.GetUtcNow(), cancellationToken), "SPOOL_ACCOUNTING_UNKNOWN");

        RetentionRemovalAuthorization authorization = await store.AuthorizeCaptureAsync(captureId,
            expectedRevision, RetentionRemovalReason.ManualPermanentDelete, clock.GetUtcNow(), cancellationToken);
        if (authorization.Result.Status is not (RetentionOperationStatus.Pending or RetentionOperationStatus.Committed))
            return authorization.Result;

        foreach (AuthorizedAssetRemoval removal in authorization.Work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessOneAsync(removal, cancellationToken);
        }

        await store.FinalizeCaptureAsync(captureId, clock.GetUtcNow(), cancellationToken);
        CaptureRetentionSummary? current = await store.GetSummaryAsync(captureId, clock.GetUtcNow(), cancellationToken);
        return current?.Disposition == RetentionDisposition.Purged
            ? new(RetentionOperationStatus.Committed, current)
            : new(RetentionOperationStatus.Pending, current, "REMOVAL_PENDING");
    }

    public async Task<RetentionRemovalPassResult> ProcessPendingAsync(int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        IReadOnlyList<AuthorizedAssetRemoval> pending = await store.GetPendingAsync(maximumCount, cancellationToken);
        int completed = 0;
        int failed = 0;
        long removedBytes = 0;
        foreach (AuthorizedAssetRemoval removal in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemovalAttempt result = await ProcessOneAsync(removal, cancellationToken);
            if (result.Completed) completed++;
            if (result.Failed) failed++;
            removedBytes = checked(removedBytes + result.RemovedBytes);
            await store.FinalizeCaptureAsync(removal.CaptureId, clock.GetUtcNow(), cancellationToken);
        }
        return new(pending.Count, completed, failed, removedBytes);
    }

    public async Task<int> PruneCompletedAttemptHistoryAsync(CancellationToken cancellationToken)
    {
        RetentionSettingsDocument policy = await settingsStore.GetAsync(cancellationToken);
        return await store.PruneCompletedAttemptHistoryAsync(
            clock.GetUtcNow().AddDays(-policy.Settings.CompletedAttemptHistoryDays), 500, cancellationToken);
    }

    public async Task<int> RefreshRetentionFactsAsync(int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        return await store.RefreshRetentionFactsAsync(maximumCount, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<RetentionRemovalPassResult> ProcessAutomaticTrashAsync(int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        await using IAsyncDisposable? lease = await controllerLock.TryAcquireAsync(cancellationToken);
        if (lease is null) return new(0, 0, 0, 0);
        return await ProcessAutomaticTrashUnderLeaseAsync(maximumCount, cancellationToken);
    }

    private async Task<RetentionRemovalPassResult> ProcessAutomaticTrashUnderLeaseAsync(int maximumCount,
        CancellationToken cancellationToken)
    {
        RetentionSettingsDocument policy = await settingsStore.GetAsync(cancellationToken);
        SpoolQuotaSnapshot snapshot = await quota.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Settings.IsActive || !snapshot.IsKnown || !policy.Settings.AutomaticRemovalEnabled
            || snapshot.Settings.HighWatermarkBytes is not long high
            || snapshot.Settings.RecoveryTargetBytes is not long target)
            return await RecordAndReturnAsync(snapshot, EmptyPass, policy, cancellationToken);

        RetentionRemovalPassResult prior = await ProcessPendingAsync(maximumCount, cancellationToken);
        snapshot = await quota.GetSnapshotAsync(cancellationToken);
        if (!snapshot.IsKnown)
            return await RecordAndReturnAsync(snapshot, prior, policy, cancellationToken);

        bool episodeActive = await PersistPressureStartAsync(snapshot, high, target, policy, cancellationToken);
        bool pressure = IsUnderPressure(snapshot, high, target, episodeActive);

        if (pressure && cacheReclaimer is not null)
        {
            CacheReclaimAttempt reclaim = await ReclaimCacheAsync(snapshot, target, cancellationToken);
            snapshot = reclaim.Snapshot;
            if (!snapshot.IsKnown)
                return await RecordAndReturnAsync(snapshot, prior, policy, cancellationToken);
            if (reclaim.FailureCode is not null)
            {
                var failedPass = prior with { Failed = prior.Failed + 1 };
                return await RecordAndReturnAsync(snapshot, failedPass, policy, cancellationToken);
            }

            pressure = IsUnderPressure(snapshot, high, target, episodeActive);
        }
        long deficit = pressure
            ? Math.Max(0, checked(snapshot.UsedBytes + snapshot.ReservedBytes - target))
            : long.MaxValue;
        IReadOnlyList<RetentionRemovalCandidate> candidates = await store.GetDueTrashCandidatesAsync(maximumCount,
            clock.GetUtcNow(), pressure, cancellationToken);
        RetentionRemovalPassResult result = await ProcessDueTrashAsync(candidates, prior, pressure, deficit,
            cancellationToken);
        SpoolQuotaSnapshot observed = await quota.GetSnapshotAsync(cancellationToken);
        result = await SelectActiveCapturesAsync(observed, result, maximumCount, cancellationToken);
        await RecordObservationAsync(observed, result, policy, cancellationToken);
        return result;
    }

    private static readonly RetentionRemovalPassResult EmptyPass = new(0, 0, 0, 0);

    private async Task<RetentionRemovalPassResult> RecordAndReturnAsync(SpoolQuotaSnapshot snapshot,
        RetentionRemovalPassResult pass, RetentionSettingsDocument policy, CancellationToken cancellationToken)
    {
        await RecordObservationAsync(snapshot, pass, policy, cancellationToken);
        return pass;
    }

    private Task RecordObservationAsync(SpoolQuotaSnapshot snapshot, RetentionRemovalPassResult pass,
        RetentionSettingsDocument policy, CancellationToken cancellationToken) =>
        store.RecordCapacityObservationAsync(snapshot, pass, policy.Settings.AutomaticRemovalEnabled,
            clock.GetUtcNow(), cancellationToken);

    private async Task<bool> PersistPressureStartAsync(SpoolQuotaSnapshot snapshot, long high, long target,
        RetentionSettingsDocument policy, CancellationToken cancellationToken)
    {
        bool episodeActive = await store.IsPressureEpisodeActiveAsync(cancellationToken);
        if (IsUnderPressure(snapshot, high, target, episodeActive) && !episodeActive)
        {
            // Persist the threshold crossing before cache reclaim can lower the observed occupancy.
            await RecordObservationAsync(snapshot, EmptyPass, policy, cancellationToken);
            episodeActive = await store.IsPressureEpisodeActiveAsync(cancellationToken);
        }
        return episodeActive;
    }

    private async Task<RetentionRemovalPassResult> SelectActiveCapturesAsync(SpoolQuotaSnapshot observed,
        RetentionRemovalPassResult pass, int maximumCount, CancellationToken cancellationToken)
    {
        if (!observed.IsKnown || observed.Settings.HighWatermarkBytes is not long high
            || observed.Settings.RecoveryTargetBytes is not long target) return pass;

        bool episodeActive = await store.IsPressureEpisodeActiveAsync(cancellationToken);
        bool pressure = IsUnderPressure(observed, high, target, episodeActive);
        long maximumBytes = long.MaxValue;
        if (pressure)
        {
            long pendingBytes = await store.GetPendingReclamationBytesAsync(clock.GetUtcNow(), cancellationToken);
            maximumBytes = Math.Max(0, checked(observed.UsedBytes + observed.ReservedBytes - target - pendingBytes));
            if (maximumBytes == 0) return pass;
        }

        RetentionSelectionPassResult selection = await store.SelectEligibleActiveAsync(maximumCount, maximumBytes,
            underPressure: pressure, clock.GetUtcNow(), cancellationToken);
        return pass with
        {
            Examined = pass.Examined + selection.Examined,
            Selected = selection.Selected,
            SelectedBytes = selection.SelectedBytes,
        };
    }

    private async Task<CacheReclaimAttempt> ReclaimCacheAsync(SpoolQuotaSnapshot snapshot, long target,
        CancellationToken cancellationToken)
    {
        long targetDeficit = Math.Max(0, checked(snapshot.UsedBytes + snapshot.ReservedBytes - target));
        PreviewCacheReclaimResult reclaim;
        try
        {
            reclaim = await cacheReclaimer!.ReclaimAsync(targetDeficit, 10_000, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            reclaim = new(0, 0, true, "cache_reclamation_unavailable");
        }

        // A bounded reclaim may remove entries before reporting a failure, so use fresh physical accounting.
        SpoolQuotaSnapshot refreshed = await quota.GetSnapshotAsync(cancellationToken);
        return new(refreshed, reclaim.FailureCode);
    }

    private async Task<RetentionRemovalPassResult> ProcessDueTrashAsync(
        IReadOnlyList<RetentionRemovalCandidate> candidates, RetentionRemovalPassResult pass, bool pressure,
        long deficit, CancellationToken cancellationToken)
    {
        long plannedBytes = 0;
        foreach (RetentionRemovalCandidate candidate in candidates.OrderBy(candidate => candidate.Tier)
            .ThenBy(candidate => candidate.EligibilityAt).ThenBy(candidate => candidate.CaptureId))
        {
            if (pressure && plannedBytes >= deficit) break;
            cancellationToken.ThrowIfCancellationRequested();
            pass = pass with { Examined = pass.Examined + 1 };
            RetentionRemovalAuthorization authorization = await store.AuthorizeCaptureAsync(candidate.CaptureId,
                candidate.Revision, pressure ? RetentionRemovalReason.PressureRecoveryExpired
                    : RetentionRemovalReason.RecoveryWindowExpired, clock.GetUtcNow(), cancellationToken);
            if (authorization.Result.Status != RetentionOperationStatus.Pending || authorization.Work.Count == 0)
                continue;
            plannedBytes = checked(plannedBytes + candidate.OriginalBytes);
            foreach (AuthorizedAssetRemoval removal in authorization.Work)
            {
                RemovalAttempt attempt = await ProcessOneAsync(removal, cancellationToken);
                pass = pass with
                {
                    Completed = pass.Completed + (attempt.Completed ? 1 : 0),
                    Failed = pass.Failed + (attempt.Failed ? 1 : 0),
                    RemovedBytes = checked(pass.RemovedBytes + attempt.RemovedBytes),
                };
            }
            await store.FinalizeCaptureAsync(candidate.CaptureId, clock.GetUtcNow(), cancellationToken);
        }
        return pass;
    }

    private static bool IsUnderPressure(SpoolQuotaSnapshot snapshot, long high, long target, bool episodeActive)
    {
        long occupancy = checked(snapshot.UsedBytes + snapshot.ReservedBytes);
        return occupancy >= high || episodeActive && (occupancy > target || snapshot.AdmissionBlockedReason is not null);
    }

    private readonly record struct CacheReclaimAttempt(SpoolQuotaSnapshot Snapshot, string? FailureCode);

    private async Task<RemovalAttempt> ProcessOneAsync(AuthorizedAssetRemoval removal, CancellationToken cancellationToken)
    {
        using IDisposable localFence = await previewUseGate.FenceAsync(removal.AssetId, cancellationToken);
        await using IOriginalAssetLockLease storageFence = await originalAssetLock.AcquireExclusiveAsync(
            removal.AssetId, removal.SpoolKey, cancellationToken);
        if (!await store.MarkRemovingAsync(removal.OperationId, cancellationToken)) return default;

        try
        {
            await previewCache.RemoveAssetAsync(removal.AssetId, cancellationToken);
            ExactOriginalRemovalResult result = await originalRemoval.RemoveAsync(new(removal.AssetId,
                removal.SpoolKey, removal.ExpectedSize, removal.StorageMarker, removal.KnownTransientKeys),
                cancellationToken);
            if (result.Status is ExactOriginalRemovalStatus.Removed or ExactOriginalRemovalStatus.AlreadyAbsent)
            {
                await store.CompleteAssetAsync(removal.OperationId, result.RemovedBytes, clock.GetUtcNow(),
                    cancellationToken);
                return new(true, false, result.RemovedBytes);
            }

            await store.FailAssetAsync(removal.OperationId, result.FailureCode ?? "storage_removal_blocked",
                clock.GetUtcNow(), result.RemovedBytes, cancellationToken);
            return new(false, true, result.RemovedBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await store.FailAssetAsync(removal.OperationId, "removal_surface_unavailable", clock.GetUtcNow(), 0,
                cancellationToken);
            return new(false, true, 0);
        }
    }

    private readonly record struct RemovalAttempt(bool Completed, bool Failed, long RemovedBytes);
}
