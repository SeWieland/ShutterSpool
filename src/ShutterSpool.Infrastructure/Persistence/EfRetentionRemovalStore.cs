using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Retention;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfRetentionRemovalStore(ShutterSpoolDbContext database, EfCaptureRetentionStore captures,
    TimeProvider clock) : IRetentionRemovalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RecordCapacityObservationAsync(SpoolQuotaSnapshot snapshot,
        RetentionRemovalPassResult pass, bool automaticRemovalEnabled, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        RetentionPressureStateEntity pressure = await database.RetentionPressureStates
            .FromSqlRaw("SELECT * FROM retention_pressure_state WHERE \"Id\" = 1 FOR UPDATE")
            .SingleAsync(cancellationToken);
        RetentionSettings retentionSettings = await ReadSettingsAsync(cancellationToken);
        DateTimeOffset historyCutoff = now.AddDays(-retentionSettings.CompletedPressureHistoryDays);
        PressureTransition transition = DeterminePressureTransition(pressure, snapshot, pass, automaticRemovalEnabled);
        await ApplyPressureTransitionAsync(pressure, transition, snapshot, pass, now, cancellationToken);

        await database.SaveChangesAsync(cancellationToken);
        Guid? currentEpisode = transition.ActiveEpisode ? pressure.EpisodeId : null;
        await PrunePressureHistoryAsync(pressure, currentEpisode, historyCutoff, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static PressureTransition DeterminePressureTransition(RetentionPressureStateEntity pressure,
        SpoolQuotaSnapshot snapshot, RetentionRemovalPassResult pass, bool automaticRemovalEnabled)
    {
        bool activeEpisode = pressure.EpisodeId is not null && pressure.State is not ("Normal" or "Recovered");
        if (!snapshot.IsKnown) return new(activeEpisode ? "ObservationUnknown" : null, activeEpisode);
        long occupancy = checked(snapshot.UsedBytes + snapshot.ReservedBytes);
        bool pressureObserved = snapshot.Settings.IsActive
            && snapshot.Settings.HighWatermarkBytes is long high && occupancy >= high;
        if (!activeEpisode && pressureObserved) return new("Planning", true, StartsEpisode: true);
        if (activeEpisode && snapshot.Settings.RecoveryTargetBytes is long target
            && occupancy <= target && snapshot.AdmissionBlockedReason is null)
            return new("Recovered", true);
        if (!activeEpisode) return new(pressure.EpisodeId is null ? "Normal" : null, false);
        string state = snapshot.AdmissionBlockedReason is not null ? "AdmissionBlocked"
            : !automaticRemovalEnabled ? "AutomaticRemovalDisabled"
            : pass.Failed > 0 ? "ReclamationFailed"
            : pass.Completed > 0 ? "Reclaiming"
            : pressureObserved ? "ReclamationStalled" : "BelowHighWatermark";
        return new(state, true);
    }

    private async Task ApplyPressureTransitionAsync(RetentionPressureStateEntity pressure, PressureTransition transition,
        SpoolQuotaSnapshot snapshot, RetentionRemovalPassResult pass, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (transition.StartsEpisode)
        {
            pressure.EpisodeId = Guid.NewGuid();
            pressure.Revision = 0;
            pressure.StartedAt = now;
        }
        if (transition.State is null || pressure.State == transition.State) return;
        pressure.State = transition.State;
        pressure.Revision = checked(pressure.Revision + 1);
        pressure.UpdatedAt = now;
        if (pressure.EpisodeId is not Guid episodeId || episodeId == Guid.Empty) return;
        AddHistory(episodeId, "pressure", transition.State, now, 0, pass.RemovedBytes, revision: pressure.Revision,
            usedBytes: snapshot.UsedBytes, reservedBytes: snapshot.ReservedBytes,
            blockers: snapshot.AdmissionBlockedReason is null ? [] : [snapshot.AdmissionBlockedReason]);
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task PrunePressureHistoryAsync(RetentionPressureStateEntity pressure, Guid? currentEpisode,
        DateTimeOffset historyCutoff, CancellationToken cancellationToken)
    {
        IQueryable<RetentionHistoryEntity> expired = database.RetentionHistory
            .Where(item => item.Kind == "pressure" && item.CommittedAt < historyCutoff);
        if (currentEpisode is Guid episode) expired = expired.Where(item => item.EpisodeId != episode);
        await expired.ExecuteDeleteAsync(cancellationToken);
        pressure.HistoryPrunedBefore = pressure.HistoryPrunedBefore is DateTimeOffset previous
            ? (previous > historyCutoff ? previous : historyCutoff)
            : historyCutoff;
    }

    private sealed record PressureTransition(string? State, bool ActiveEpisode, bool StartsEpisode = false);

    public async Task<int> PruneCompletedAttemptHistoryAsync(DateTimeOffset cutoff, int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 2_000) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        Guid[] expired = await database.DeliveryAttempts.AsNoTracking()
            .Where(attempt => attempt.FinishedAt < cutoff
                && database.Deliveries.Any(delivery => delivery.Id == attempt.DeliveryId
                    && (delivery.State == DeliveryState.Delivered || delivery.State == DeliveryState.Failed
                        || delivery.State == DeliveryState.Superseded || delivery.State == DeliveryState.Disabled))
                && !database.PreservationHoldAssets.Any(member => member.AssetId == attempt.AssetId
                    && database.PreservationHolds.Any(hold => hold.Id == member.HoldId && hold.ResolvedAt == null)))
            .OrderBy(attempt => attempt.FinishedAt).ThenBy(attempt => attempt.Id)
            .Take(maximumCount).Select(attempt => attempt.Id).ToArrayAsync(cancellationToken);
        if (expired.Length == 0) return 0;
        return await database.DeliveryAttempts.Where(attempt => expired.Contains(attempt.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<RetentionSelectionPassResult> SelectEligibleActiveAsync(int maximumCount,
        long maximumBytes, bool underPressure, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100 || maximumBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        RetentionSettings policy = await ReadSettingsAsync(cancellationToken);
        int classified = await ClassifyDirtyCapturesAsync(50, now, policy, cancellationToken);

        // Do not select from a partially classified population. Once all due facts are
        // authoritative, PostgreSQL orders the entire eligible scope before the bounded take.
        bool dirtyRemain = await database.Captures.AnyAsync(capture => capture.RetentionDisposition == "Active"
            && capture.RetentionFactsDirty, cancellationToken);
        if (dirtyRemain) return new(classified, 0, 0);

        Guid[] ids = await ReadEligibleCaptureIdsAsync(maximumCount, underPressure, now, cancellationToken);
        (List<SelectionCandidate> candidates, bool factsMismatch) = await ReadSelectionCandidatesAsync(
            ids, underPressure, now, policy, cancellationToken);
        int examined = checked(classified + ids.Length);
        if (factsMismatch) return new(examined, 0, 0);
        return await MoveCandidatesToTrashAsync(candidates, maximumCount, maximumBytes, examined,
            underPressure, now, cancellationToken);
    }

    private async Task<Guid[]> ReadEligibleCaptureIdsAsync(int maximumCount, bool underPressure,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        IQueryable<CaptureEntity> eligible = database.Captures.AsNoTracking()
            .Where(capture => capture.RetentionDisposition == "Active" && !capture.RetentionFactsDirty
                && capture.RetentionTier != null);
        IOrderedQueryable<CaptureEntity> ordered = underPressure
            ? eligible.Where(capture => capture.RetentionPressureEligibleAt <= now)
                .OrderBy(capture => capture.RetentionTier).ThenBy(capture => capture.RetentionPressureEligibleAt)
            : eligible.Where(capture => capture.RetentionTrashEligibleAt <= now)
                .OrderBy(capture => capture.RetentionTier).ThenBy(capture => capture.RetentionTrashEligibleAt);
        return await ordered.ThenBy(capture => capture.Id).Take(Math.Min(100, maximumCount * 8))
            .Select(capture => capture.Id).ToArrayAsync(cancellationToken);
    }

    private async Task<(List<SelectionCandidate> Candidates, bool FactsMismatch)> ReadSelectionCandidatesAsync(
        Guid[] ids, bool underPressure, DateTimeOffset now, RetentionSettings policy, CancellationToken cancellationToken)
    {
        var candidates = new List<SelectionCandidate>(ids.Length);
        bool factsMismatch = false;
        foreach (Guid id in ids)
        {
            CaptureEntity? capture = await database.Captures.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
            if (capture is null) continue;
            CaptureRetentionSummary summary = await captures.BuildSummaryAsync(capture, now,
                cancellationToken, policy);
            DateTimeOffset? eligibleAt = underPressure ? summary.PressureEligibleAt : summary.TrashEligibleAt;
            DateTimeOffset? indexedAt = underPressure ? capture.RetentionPressureEligibleAt : capture.RetentionTrashEligibleAt;
            if (capture.RetentionTier != (summary.Tier is null ? null : (int)summary.Tier.Value)
                || indexedAt != eligibleAt)
            {
                await database.Captures.Where(item => item.Id == id && item.RetentionDisposition == "Active")
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.RetentionFactsDirty, true), cancellationToken);
                factsMismatch = true;
                continue;
            }
            if (!CanSelectSummary(summary, eligibleAt, now)) continue;
            candidates.Add(new(id, capture.Revision, summary.Tier, eligibleAt!.Value,
                summary.AvailableOriginals.Sum(original => original.ByteLength)));
        }
        return (candidates, factsMismatch);
    }

    private async Task<RetentionSelectionPassResult> MoveCandidatesToTrashAsync(List<SelectionCandidate> candidates,
        int maximumCount, long maximumBytes, int examined, bool underPressure, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        int selected = 0;
        long selectedBytes = 0;
        foreach (SelectionCandidate candidate in candidates.OrderBy(candidate => candidate.Tier)
                     .ThenBy(candidate => candidate.EligibleAt).ThenBy(candidate => candidate.CaptureId))
        {
            if (selected >= maximumCount || selected > 0 && selectedBytes >= maximumBytes) break;
            if (!await MoveEligibleCaptureToTrashAsync(candidate, underPressure, now, cancellationToken)) continue;
            selected++;
            selectedBytes = checked(selectedBytes + candidate.Bytes);
        }
        return new(examined, selected, selectedBytes);
    }

    public async Task<int> RefreshRetentionFactsAsync(int maximumCount, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        return await ClassifyDirtyCapturesAsync(maximumCount, now, await ReadSettingsAsync(cancellationToken),
            cancellationToken);
    }

    public Task<bool> IsPressureEpisodeActiveAsync(CancellationToken cancellationToken) =>
        database.RetentionPressureStates.AsNoTracking().AnyAsync(state => state.Id == 1
            && state.EpisodeId != null && state.State != "Normal" && state.State != "Recovered",
            cancellationToken);

    private async Task<int> ClassifyDirtyCapturesAsync(int maximumCount, DateTimeOffset now, RetentionSettings policy,
        CancellationToken cancellationToken)
    {
        Guid[] ids = await database.Captures.AsNoTracking()
            .Where(capture => capture.RetentionDisposition == "Active" && capture.RetentionFactsDirty)
            .OrderBy(capture => capture.FirstAcceptedAt).ThenBy(capture => capture.Id)
            .Take(maximumCount).Select(capture => capture.Id).ToArrayAsync(cancellationToken);
        foreach (Guid id in ids)
        {
            await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await LockRoutingAsync(cancellationToken);
            CaptureEntity? capture = await LockCaptureAsync(id, cancellationToken);
            if (capture is null || capture.RetentionDisposition != "Active" || !capture.RetentionFactsDirty)
            {
                await transaction.CommitAsync(cancellationToken);
                continue;
            }

            CaptureRetentionSummary summary = await captures.BuildSummaryAsync(capture, now,
                cancellationToken, policy);
            capture.RetentionTier = summary.Tier is null ? null : (int)summary.Tier.Value;
            capture.RetentionPressureEligibleAt = summary.PressureEligibleAt;
            capture.RetentionTrashEligibleAt = summary.TrashEligibleAt;
            capture.RetentionAttentionAt = RetentionPolicy.AttentionAt(summary.Tier,
                summary.Timeline?.StartedAt, summary.Timeline?.EndsAt);
            capture.RetentionFactsDirty = false;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        return ids.Length;
    }

    private static bool CanSelectSummary(CaptureRetentionSummary summary, DateTimeOffset? eligibleAt,
        DateTimeOffset now) => eligibleAt is DateTimeOffset due && due <= now
        && summary.AvailableOriginals.Length > 0
        && summary.AvailableOriginals.All(original => original.State == RetentionOriginalState.Available)
        && !summary.ProtectionReasons.Contains("preservation-hold", StringComparer.Ordinal);

    private async Task<bool> MoveEligibleCaptureToTrashAsync(SelectionCandidate candidate, bool underPressure,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity? capture = await LockCaptureAsync(candidate.CaptureId, cancellationToken);
        if (capture is null || capture.Revision != candidate.Revision || capture.RetentionDisposition != "Active")
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        RetentionSettings policy = await ReadSettingsAsync(cancellationToken);
        CaptureRetentionSummary summary = await captures.BuildSummaryAsync(capture, now, cancellationToken, policy);
        DateTimeOffset? eligibleAt = underPressure ? summary.PressureEligibleAt : summary.TrashEligibleAt;
        if (!SelectionFactsMatch(capture, summary, eligibleAt, underPressure))
        {
            capture.RetentionFactsDirty = true;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        if (!CanSelectSummary(summary, eligibleAt, now))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        if (await HasActiveCaptureIoAsync(summary, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        capture.RetentionDisposition = "Trash";
        capture.TrashReason = underPressure ? "pressure" : "age";
        capture.TrashEnteredAt = now;
        capture.RecoverableUntil = now + policy.TrashRecoveryWindow;
        capture.RetentionPaused = true;
        capture.Revision = checked(capture.Revision + 1);
        await database.SaveChangesAsync(cancellationToken);
        AddHistory(Guid.NewGuid(), "selection", capture.TrashReason, now, candidate.Bytes, 0);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static bool SelectionFactsMatch(CaptureEntity capture, CaptureRetentionSummary summary,
        DateTimeOffset? eligibleAt, bool underPressure)
    {
        int? summarizedTier = summary.Tier is null ? null : (int)summary.Tier.Value;
        DateTimeOffset? indexedAt = underPressure ? capture.RetentionPressureEligibleAt : capture.RetentionTrashEligibleAt;
        DateTimeOffset? attentionAt = RetentionPolicy.AttentionAt(summary.Tier,
            summary.Timeline?.StartedAt, summary.Timeline?.EndsAt);
        return !capture.RetentionFactsDirty && capture.RetentionTier == summarizedTier && indexedAt == eligibleAt
            && capture.RetentionAttentionAt == attentionAt;
    }

    private async Task<bool> HasActiveCaptureIoAsync(CaptureRetentionSummary summary, CancellationToken cancellationToken)
    {
        Guid[] assetIds = [.. summary.AvailableOriginals.Select(original => original.AssetId)];
        bool activeDelivery = await database.Deliveries.AnyAsync(delivery => assetIds.Contains(delivery.AssetId)
            && delivery.State == DeliveryState.Running, cancellationToken);
        bool activeReceive = await database.CameraUploadAdmissions.AnyAsync(admission => assetIds.Contains(admission.AssetId ?? Guid.Empty)
            && (admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Admitted
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Receiving
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Received
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Handoff), cancellationToken);
        return activeDelivery || activeReceive;
    }

    public async Task<RetentionRemovalAuthorization> AuthorizeCaptureAsync(Guid captureId, int? expectedRevision,
        RetentionRemovalReason reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity? capture = await LockCaptureAsync(captureId, cancellationToken);
        if (capture is null)
            return await FinishAuthorizationAsync(transaction, RetentionOperationStatus.NotFound, null, "CAPTURE_NOT_FOUND", [], cancellationToken);
        var captureBlocker = FindCaptureRemovalBlocker(capture, expectedRevision, reason, now);
        if (captureBlocker is { } blockedCapture)
            return await FinishAuthorizationAsync(transaction, blockedCapture.Status, capture, blockedCapture.Code, [], cancellationToken);

        Guid[] assetIds = await database.CaptureAssets.AsNoTracking().Where(member => member.CaptureId == captureId)
            .OrderBy(member => member.AssetId).Select(member => member.AssetId).ToArrayAsync(cancellationToken);
        if (assetIds.Length == 0)
            return await FinishAuthorizationAsync(transaction, RetentionOperationStatus.Blocked, capture, "CAPTURE_HAS_NO_ASSETS", [], cancellationToken);

        var assetBlocker = await FindAssetRemovalBlockerAsync(assetIds, now, cancellationToken);
        if (assetBlocker is { } blockedAssets)
            return await FinishAuthorizationAsync(transaction, blockedAssets.Status, capture, blockedAssets.Code, [], cancellationToken);

        string? marker = await database.SpoolQuotaObservations.AsNoTracking().Where(item => item.Id == 1)
            .Select(item => item.StorageMarker).SingleOrDefaultAsync(cancellationToken);
        if (marker is null)
            return await FinishAuthorizationAsync(transaction, RetentionOperationStatus.Blocked, capture,
                "SPOOL_STORAGE_IDENTITY_UNKNOWN", [], cancellationToken);

        AssetEntity[] assets = await database.Assets.Where(asset => assetIds.Contains(asset.Id))
            .OrderBy(asset => asset.Id).ToArrayAsync(cancellationToken);
        if (assets.Length != assetIds.Length || assets.Any(asset => asset.State != "Spooled"))
            return await FinishAuthorizationAsync(transaction, RetentionOperationStatus.Blocked, capture,
                "ORIGINAL_UNAVAILABLE", [], cancellationToken);
        bool existingRemoval = await database.AssetRemovalOperations.AnyAsync(operation => assetIds.Contains(operation.AssetId), cancellationToken);
        if (existingRemoval)
            return await FinishAuthorizationAsync(transaction, RetentionOperationStatus.Conflict, capture,
                "REMOVAL_ALREADY_OWNED", [], cancellationToken);

        IReadOnlyList<AuthorizedAssetRemoval>? pending = await AuthorizeAssetsAsync(capture, assets, marker,
            reason, now, cancellationToken);
        if (pending is null)
            return await FinishAuthorizationAsync(transaction, RetentionOperationStatus.Blocked, capture,
                "TRANSIENT_COPY_SET_UNBOUNDED", [], cancellationToken);

        capture.RetentionDisposition = "Purging";
        capture.RetentionPaused = true;
        capture.Revision = checked(capture.Revision + 1);
        await database.SaveChangesAsync(cancellationToken);
        AddHistory(Guid.NewGuid(), "removal", "authorized", now, assets.Sum(asset => asset.Size), 0);
        await database.SaveChangesAsync(cancellationToken);
        CaptureRetentionSummary summary = await BuildSummaryAsync(capture, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new(RetentionOperationStatus.Pending, summary, "REMOVAL_AUTHORIZED"), pending);
    }

    private async Task<IReadOnlyList<AuthorizedAssetRemoval>?> AuthorizeAssetsAsync(CaptureEntity capture,
        AssetEntity[] assets, string marker, RetentionRemovalReason reason, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = new List<AuthorizedAssetRemoval>(assets.Length);
        foreach (AssetEntity asset in assets)
        {
            string[] transientKeys = await ReadTransientKeysAsync(asset, cancellationToken);
            if (transientKeys.Length > 64)
                return null;

            Guid operationId = Guid.NewGuid();
            string operationReason = reason == RetentionRemovalReason.ManualPermanentDelete ? "manual" : "window-expired";
            int policyRevision = capture.RetentionPolicyRevision;
            database.AssetRemovalOperations.Add(new AssetRemovalOperationEntity
            {
                AssetId = asset.Id,
                CaptureId = capture.Id,
                OperationId = operationId,
                SpoolKey = asset.SpoolKey,
                ExpectedSize = asset.Size,
                StorageMarker = marker,
                KnownTransientKeys = transientKeys,
                Scope = "original",
                Reason = operationReason,
                PolicyRevision = policyRevision,
                State = "Authorized",
                DueAt = now,
                AuthorizedAt = now,
            });
            pending.Add(new(operationId, capture.Id, asset.Id, asset.SpoolKey, asset.Size, marker, transientKeys));
        }

        return pending;
    }

    private static (RetentionOperationStatus Status, string Code)? FindCaptureRemovalBlocker(CaptureEntity capture,
        int? expectedRevision, RetentionRemovalReason reason, DateTimeOffset now)
    {
        if (expectedRevision is int revision && capture.Revision != revision)
            return (RetentionOperationStatus.Conflict, "REVISION_CONFLICT");
        if (capture.RetentionDisposition != "Trash")
            return (RetentionOperationStatus.Conflict, "CAPTURE_NOT_IN_TRASH");
        if (reason is RetentionRemovalReason.RecoveryWindowExpired or RetentionRemovalReason.PressureRecoveryExpired)
        {
            if (capture.RecoverableUntil is null || capture.RecoverableUntil > now)
                return (RetentionOperationStatus.Blocked, "TRASH_RECOVERY_WINDOW_ACTIVE");
            // The immutable Trash promise is the purge clock. Eligibility was checked before an
            // automatic Active -> Trash transition; manual Trash also becomes purgeable when its
            // recovery window ends. Pressure must never shorten that promise.
            if (capture.RestoreProtectedUntil > now)
                return (RetentionOperationStatus.Protected, "RESTORE_PROTECTION_ACTIVE");
        }

        return null;
    }

    private async Task<(RetentionOperationStatus Status, string Code)?> FindAssetRemovalBlockerAsync(Guid[] assetIds,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        bool promised = await database.GalleryProtectionAssets.AnyAsync(member => assetIds.Contains(member.AssetId)
            && database.GalleryProtections.Any(protection => protection.Id == member.ProtectionId && protection.ExpiresAt > now), cancellationToken);
        bool preserved = await database.PreservationHoldAssets.AnyAsync(member => assetIds.Contains(member.AssetId)
            && database.PreservationHolds.Any(hold => hold.Id == member.HoldId && hold.ResolvedAt == null), cancellationToken);
        if (promised || preserved)
            return (RetentionOperationStatus.Protected, preserved ? "PRESERVATION_HOLD_ACTIVE" : "GALLERY_PROMISE_ACTIVE");

        bool activeDelivery = await database.Deliveries.AnyAsync(delivery => assetIds.Contains(delivery.AssetId)
            && delivery.State == DeliveryState.Running, cancellationToken);
        if (activeDelivery)
            return (RetentionOperationStatus.Blocked, "DELIVERY_IO_ACTIVE");
        bool activeCameraReceive = await database.CameraUploadAdmissions.AnyAsync(admission => assetIds.Contains(admission.AssetId ?? Guid.Empty)
            && (admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Admitted
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Receiving
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Received
                || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Handoff), cancellationToken);
        if (activeCameraReceive)
            return (RetentionOperationStatus.Blocked, "CAMERA_RECEIVE_RECONCILIATION_REQUIRED");

        return null;
    }

    public async Task<IReadOnlyList<AuthorizedAssetRemoval>> GetPendingAsync(int maximumCount,
        CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        AssetRemovalOperationEntity[] operations = await database.AssetRemovalOperations.AsNoTracking()
            .Where(operation => ((operation.State == "Authorized" || operation.State == "Removing" || operation.State == "Failed")
                    && operation.DueAt <= clock.GetUtcNow())
                || operation.State == "Removed" && database.Captures.Any(capture => capture.Id == operation.CaptureId
                    && capture.RetentionDisposition == "Purging"))
            .OrderBy(operation => operation.DueAt).ThenBy(operation => operation.OperationId)
            .Take(maximumCount).ToArrayAsync(cancellationToken);
        return operations.Select(ToContract).ToArray();
    }

    public async Task<IReadOnlyList<RetentionRemovalCandidate>> GetDueTrashCandidatesAsync(int maximumCount,
        DateTimeOffset now, bool underPressure, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        var candidates = await database.Captures.AsNoTracking()
            .Where(capture => capture.RetentionDisposition == "Trash" && capture.RecoverableUntil <= now
                && (capture.RestoreProtectedUntil == null || capture.RestoreProtectedUntil <= now))
            .Where(capture => database.CaptureAssets.Any(member => member.CaptureId == capture.Id))
            .Where(capture => !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && !database.Assets.Any(asset => asset.Id == member.AssetId && asset.State == "Spooled")))
            .Where(capture => !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.AssetRemovalOperations.Any(operation => operation.AssetId == member.AssetId)))
            .Where(capture => !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.Deliveries.Any(delivery => delivery.AssetId == member.AssetId
                    && delivery.State == DeliveryState.Running)))
            .Where(capture => !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.GalleryProtectionAssets.Any(protectionMember => protectionMember.AssetId == member.AssetId
                    && database.GalleryProtections.Any(protection => protection.Id == protectionMember.ProtectionId
                        && protection.ExpiresAt > now))))
            .Where(capture => !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.PreservationHoldAssets.Any(holdMember => holdMember.AssetId == member.AssetId
                    && database.PreservationHolds.Any(hold => hold.Id == holdMember.HoldId && hold.ResolvedAt == null))))
            .OrderBy(capture => capture.RecoverableUntil).ThenBy(capture => capture.Id)
            .Take(maximumCount)
            .Select(capture => new RetentionRemovalCandidate(capture.Id, capture.Revision,
                (from member in database.CaptureAssets
                    join asset in database.Assets on member.AssetId equals asset.Id
                    where member.CaptureId == capture.Id
                    select (long?)asset.Size).Sum() ?? 0,
                null, capture.RecoverableUntil))
            .ToArrayAsync(cancellationToken);
        // The frozen recovery window is the purge clock; carry it out of the bounded query.
        return candidates;
    }

    public async Task<long> GetPendingReclamationBytesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await (from capture in database.Captures.AsNoTracking()
            where capture.RetentionDisposition == "Trash"
                && capture.RecoverableUntil != null
                && database.CaptureAssets.Any(member => member.CaptureId == capture.Id)
                && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && !database.Assets.Any(asset => asset.Id == member.AssetId && asset.State == "Spooled"))
                && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && database.AssetRemovalOperations.Any(operation => operation.AssetId == member.AssetId))
                && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && database.Deliveries.Any(delivery => delivery.AssetId == member.AssetId
                        && delivery.State == DeliveryState.Running))
                && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && database.CameraUploadAdmissions.Any(admission => admission.AssetId == member.AssetId
                        && (admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Admitted
                            || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Receiving
                            || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Received
                            || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Handoff)))
                && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && database.GalleryProtectionAssets.Any(protectionMember => protectionMember.AssetId == member.AssetId
                        && database.GalleryProtections.Any(protection => protection.Id == protectionMember.ProtectionId
                            && protection.ExpiresAt > now)))
                && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && database.PreservationHoldAssets.Any(holdMember => holdMember.AssetId == member.AssetId
                        && database.PreservationHolds.Any(hold => hold.Id == holdMember.HoldId && hold.ResolvedAt == null)))
            select (long?)((from member in database.CaptureAssets
                join asset in database.Assets on member.AssetId equals asset.Id
                where member.CaptureId == capture.Id
                select (long?)asset.Size).Sum() ?? 0)).SumAsync(cancellationToken) ?? 0;
    }

    public async Task<bool> MarkRemovingAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        AssetRemovalOperationEntity? operation = await LockOperationAsync(operationId, cancellationToken);
        if (operation is null || operation.State == "Removed")
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        CaptureEntity? capture = await database.Captures.SingleOrDefaultAsync(item => item.Id == operation.CaptureId, cancellationToken);
        if (capture?.RetentionDisposition != "Purging")
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        operation.State = "Removing";
        operation.FailureCode = null;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task CompleteAssetAsync(Guid operationId, long removedBytes, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        AssetRemovalOperationEntity? operation = await LockOperationAsync(operationId, cancellationToken);
        if (operation is null) throw new InvalidOperationException("Removal authorization was not found.");
        if (operation.State == "Removed")
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        operation.State = "Removed";
        operation.CompletedAt = now;
        operation.FailureCode = null;
        operation.RemovedBytes = checked(operation.RemovedBytes + removedBytes);
        AssetEntity? asset = await database.Assets.SingleOrDefaultAsync(item => item.Id == operation.AssetId,
            cancellationToken);
        if (asset is not null) asset.State = "Removed";
        await database.SaveChangesAsync(cancellationToken);
        long historyRevision = await NextRemovalHistoryRevisionAsync(operation.OperationId, cancellationToken);
        AddHistory(operation.OperationId, "removal", "completed", now, 0, removedBytes,
            revision: historyRevision);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FailAssetAsync(Guid operationId, string failureCode, DateTimeOffset now,
        long partiallyRemovedBytes, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        AssetRemovalOperationEntity? operation = await LockOperationAsync(operationId, cancellationToken);
        if (operation is null || operation.State == "Removed")
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        operation.State = "Failed";
        operation.RemovedBytes = checked(operation.RemovedBytes + partiallyRemovedBytes);
        operation.FailureCode = FixedFailureCode(failureCode);
        operation.DueAt = now.AddMinutes(1);
        await database.SaveChangesAsync(cancellationToken);
        long historyRevision = await NextRemovalHistoryRevisionAsync(operation.OperationId, cancellationToken);
        AddHistory(operation.OperationId, "removal", "failed", now, 0, partiallyRemovedBytes,
            [operation.FailureCode], revision: historyRevision);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinalizeCaptureAsync(Guid captureId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        CaptureEntity? capture = await LockCaptureAsync(captureId, cancellationToken);
        if (capture is null || capture.RetentionDisposition != "Purging")
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        bool pending = await database.AssetRemovalOperations.AnyAsync(operation => operation.CaptureId == captureId
            && operation.State != "Removed", cancellationToken);
        bool hasOperations = await database.AssetRemovalOperations.AnyAsync(operation => operation.CaptureId == captureId,
            cancellationToken);
        if (hasOperations && !pending)
        {
            capture.RetentionDisposition = "Purged";
            capture.RetentionPaused = true;
            capture.Revision = checked(capture.Revision + 1);
            await database.SaveChangesAsync(cancellationToken);
            AddHistory(Guid.NewGuid(), "removal", "capture-purged", now, 0, 0);
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CaptureRetentionSummary?> GetSummaryAsync(Guid captureId, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        CaptureEntity? capture = await database.Captures.AsNoTracking().SingleOrDefaultAsync(item => item.Id == captureId, cancellationToken);
        return capture is null ? null : await BuildSummaryAsync(capture, now, cancellationToken);
    }

    private async Task<CaptureRetentionSummary> BuildSummaryAsync(CaptureEntity capture, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string? policyJson = await database.RetentionPolicies.AsNoTracking().Where(item => item.Id == 1)
            .Select(item => item.PolicyJson).SingleOrDefaultAsync(cancellationToken);
        RetentionSettings settings = policyJson is null
            ? new RetentionSettings()
            : JsonSerializer.Deserialize<RetentionSettings>(policyJson, JsonOptions) ?? new RetentionSettings();
        return await captures.BuildSummaryAsync(capture, now, cancellationToken, settings);
    }

    private async Task<RetentionSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        string? policyJson = await database.RetentionPolicies.AsNoTracking().Where(item => item.Id == 1)
            .Select(item => item.PolicyJson).SingleOrDefaultAsync(cancellationToken);
        return policyJson is null ? new RetentionSettings()
            : JsonSerializer.Deserialize<RetentionSettings>(policyJson, JsonOptions) ?? new RetentionSettings();
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

    private void AddHistory(Guid episodeId, string kind, string state, DateTimeOffset now,
        long plannedBytes, long purgedBytes, string[]? blockers = null, long revision = 1,
        long usedBytes = 0, long reservedBytes = 0)
    {
        database.RetentionHistory.Add(new RetentionHistoryEntity
        {
            EventId = Guid.NewGuid(),
            EpisodeId = episodeId,
            Revision = revision,
            ObservedAt = now,
            CommittedAt = now,
            Kind = kind,
            State = state,
            UsedBytes = usedBytes,
            ReservedBytes = reservedBytes,
            PlannedReclamationBytes = plannedBytes,
            PurgedBytes = purgedBytes,
            Blockers = blockers ?? [],
        });
    }

    private async Task<long> NextRemovalHistoryRevisionAsync(Guid operationId, CancellationToken cancellationToken)
    {
        long? current = await database.RetentionHistory.AsNoTracking()
            .Where(item => item.EpisodeId == operationId && item.Kind == "removal")
            .Select(item => (long?)item.Revision).MaxAsync(cancellationToken);
        return checked((current ?? 0) + 1);
    }

    private async Task<RetentionRemovalAuthorization> FinishAuthorizationAsync(IDbContextTransaction transaction,
        RetentionOperationStatus status, CaptureEntity? capture, string error,
        IReadOnlyList<AuthorizedAssetRemoval> work, CancellationToken cancellationToken)
    {
        CaptureRetentionSummary? summary = capture is null ? null : await BuildSummaryAsync(capture, clock.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new(status, summary, error), work);
    }

    private Task<CaptureEntity?> LockCaptureAsync(Guid captureId, CancellationToken cancellationToken) =>
        database.Captures.FromSqlInterpolated($"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private Task<int> LockRoutingAsync(CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))",
            cancellationToken);

    private Task<AssetRemovalOperationEntity?> LockOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        database.AssetRemovalOperations.FromSqlInterpolated($"SELECT * FROM asset_removal_operations WHERE \"OperationId\" = {operationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static AuthorizedAssetRemoval ToContract(AssetRemovalOperationEntity operation) => new(
        operation.OperationId, operation.CaptureId, operation.AssetId, operation.SpoolKey, operation.ExpectedSize,
        operation.StorageMarker, operation.KnownTransientKeys);

    private sealed record SelectionCandidate(Guid CaptureId, int Revision, RetentionTier? Tier,
        DateTimeOffset EligibleAt, long Bytes);

    private static string FixedFailureCode(string code) => code switch
    {
        "storage_root_unavailable" or "storage_marker_unavailable" or "storage_marker_mismatch" => code,
        "storage_key_asset_mismatch" or "original_size_mismatch" or "authorized_key_is_linked" => code,
        _ => "removal_surface_unavailable",
    };
}
