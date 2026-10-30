using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Retention;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfRetentionSettingsStore(ShutterSpoolDbContext database, ISpoolQuotaStore quotaStore,
    IRetentionRemovalStore removalStore,
    TimeProvider clock, PreviewSettings effectivePreviewSettings,
    ShutterSpool.Application.Workspaces.WorkspaceContext? workspace = null) : IRetentionSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RetentionSettingsDocument> GetAsync(CancellationToken cancellationToken)
    {
        RetentionPolicyEntity? entity = await database.RetentionPolicies.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        SpoolQuotaSettings quota = await quotaStore.GetSettingsAsync(cancellationToken);
        RetentionSettings settings = ReadPolicy(entity?.PolicyJson);
        PreviewCacheSettings configured = ReadPreviewCache(entity?.PreviewCacheJson);
        PreviewCacheSettings effective = new(effectivePreviewSettings.CacheMode, effectivePreviewSettings.MemoryCacheBytes);
        return new(settings, quota, configured, effective, configured != effective);
    }

    public async Task<RetentionSettingsImpact> PreviewAsync(RetentionSettings settings, SpoolQuotaSettings quota,
        PreviewCacheSettings previewCache, CancellationToken cancellationToken)
    {
        // Existing Assets retain their acceptance-time policy; this preview is prospective.
        if (workspace?.Enabled == true && (!quota.IsActive || quota.QuotaBytes is not long bytes || bytes > workspace.Required.AllocatedBytes))
            throw new ArgumentException("Workspace quota admission must remain enabled within its operator allocation.");
        DateTimeOffset now = clock.GetUtcNow();
        SpoolQuotaSnapshot snapshot = await quotaStore.GetSnapshotAsync(cancellationToken);
        long protectedBytes = await ReadProtectedBytesAsync(now, cancellationToken);
        string? currentPolicyJson = await database.RetentionPolicies.AsNoTracking().Where(item => item.Id == 1)
            .Select(item => item.PolicyJson).SingleOrDefaultAsync(cancellationToken);
        RetentionSettings currentPolicy = ReadPolicy(currentPolicyJson);
        SpoolQuotaSettings currentQuota = await quotaStore.GetSettingsAsync(cancellationToken);
        bool factsComplete = !await database.Captures.AsNoTracking().AnyAsync(capture =>
            capture.RetentionDisposition == "Active" && capture.RetentionFactsDirty, cancellationToken);
        SelectionPlan plan = await PlanSelectionAsync(settings, quota, snapshot, factsComplete, now, cancellationToken);
        string[] warnings = BuildWarnings(snapshot, protectedBytes, factsComplete, plan.ScanBounded);
        long? proposedQuotaHeadroomBytes = snapshot.IsKnown && quota.IsActive && quota.QuotaBytes is long proposedQuota
            ? checked(proposedQuota - snapshot.UsedBytes - snapshot.ReservedBytes) : null;
        return new(now, plan.Complete, snapshot.IsKnown, snapshot.UsedBytes, snapshot.ReservedBytes,
            proposedQuotaHeadroomBytes, protectedBytes, plan.EligibleCount,
            snapshot.IsKnown ? plan.Count : null, snapshot.IsKnown ? plan.Bytes : null,
            snapshot.IsOverQuota, snapshot.AdmissionBlockedReason is not null,
            !currentPolicy.AutomaticRemovalEnabled && settings.AutomaticRemovalEnabled,
            !currentQuota.IsActive && quota.IsActive, warnings);
    }

    private async Task<long> ReadProtectedBytesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        return await (from member in database.CaptureAssets.AsNoTracking()
            join capture in database.Captures.AsNoTracking() on member.CaptureId equals capture.Id
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where asset.State == "Spooled" && (capture.RestoreProtectedUntil > now
                || capture.RetentionDisposition == "Trash" && capture.RecoverableUntil > now
                || database.GalleryProtectionAssets.Any(protectedAsset => protectedAsset.AssetId == asset.Id
                    && database.GalleryProtections.Any(protection => protection.Id == protectedAsset.ProtectionId
                        && protection.ExpiresAt > now))
                || database.PreservationHoldAssets.Any(heldAsset => heldAsset.AssetId == asset.Id
                    && database.PreservationHolds.Any(hold => hold.Id == heldAsset.HoldId && hold.ResolvedAt == null)))
            select (long?)asset.Size).SumAsync(cancellationToken) ?? 0;
    }

    private async Task<SelectionPlan> PlanSelectionAsync(RetentionSettings settings, SpoolQuotaSettings quota,
        SpoolQuotaSnapshot snapshot, bool factsComplete, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!factsComplete) return new(false, null, null, null);
        int eligibleCount = await EligibleCaptures(now, pressureOnly: false).CountAsync(cancellationToken);
        long occupancy = checked(snapshot.UsedBytes + snapshot.ReservedBytes);
        RetentionPressureStateEntity? pressure = await database.RetentionPressureStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == 1, cancellationToken);
        var empty = new SelectionPlan(snapshot.IsKnown, eligibleCount, 0, 0);
        if (!ShouldPlanPressureSelection(settings, quota, occupancy, snapshot.AdmissionBlockedReason, pressure))
            return empty;

        long pendingTrashBytes = await removalStore.GetPendingReclamationBytesAsync(now, cancellationToken);
        long deficit = Math.Max(0, checked(snapshot.UsedBytes + snapshot.ReservedBytes
            - quota.RecoveryTargetBytes!.Value - pendingTrashBytes));
        if (deficit == 0) return empty;
        List<PlannedCandidate> candidates = await ReadPlannedCandidatesAsync(now, cancellationToken);
        return SelectWithinDeficit(candidates, deficit, eligibleCount, snapshot.IsKnown);
    }

    private static bool ShouldPlanPressureSelection(RetentionSettings settings, SpoolQuotaSettings quota,
        long occupancy, string? admissionBlockedReason, RetentionPressureStateEntity? pressure)
    {
        bool activeEpisode = pressure?.EpisodeId is not null && pressure.State is not ("Normal" or "Recovered");
        return settings.AutomaticRemovalEnabled && quota.IsActive
            && quota.HighWatermarkBytes is long high && quota.RecoveryTargetBytes is long target
            && (occupancy >= high || activeEpisode && (occupancy > target || admissionBlockedReason is not null));
    }

    private async Task<List<PlannedCandidate>> ReadPlannedCandidatesAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return await (from capture in EligibleCaptures(now, pressureOnly: true)
            from member in database.CaptureAssets.AsNoTracking().Where(item => item.CaptureId == capture.Id)
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where asset.State == "Spooled"
                && !database.AssetRemovalOperations.Any(operation => operation.AssetId == asset.Id)
            group asset by new { capture.Id, capture.RetentionTier, capture.RetentionPressureEligibleAt } into groupedAssets
            orderby groupedAssets.Key.RetentionTier, groupedAssets.Key.RetentionPressureEligibleAt, groupedAssets.Key.Id
            select new PlannedCandidate(groupedAssets.Key.Id, groupedAssets.Sum(asset => asset.Size)))
            .Take(10_001).ToListAsync(cancellationToken);
    }

    private static SelectionPlan SelectWithinDeficit(IReadOnlyList<PlannedCandidate> candidates, long deficit,
        int eligibleCount, bool accountingKnown)
    {
        long selectedBytes = 0;
        int selectedCount = 0;
        foreach (PlannedCandidate candidate in candidates.Take(10_000))
        {
            if (selectedBytes >= deficit) break;
            selectedBytes = checked(selectedBytes + candidate.Bytes);
            selectedCount++;
        }
        bool bounded = candidates.Count > 10_000 && selectedBytes < deficit;
        return bounded ? new(false, eligibleCount, 0, 0, ScanBounded: true)
            : new(accountingKnown, eligibleCount, selectedCount, selectedBytes);
    }

    private static string[] BuildWarnings(SpoolQuotaSnapshot snapshot, long protectedBytes,
        bool factsComplete, bool scanBounded)
    {
        var warnings = new List<string>();
        if (!snapshot.IsKnown) warnings.Add("accounting-unknown");
        if (snapshot.IsOverQuota) warnings.Add("already-over-quota");
        if (protectedBytes > 0) warnings.Add("protected-occupancy");
        warnings.Add("existing-assets-retain-policy-revision");
        if (!factsComplete) warnings.Add("eligibility-classification-incomplete");
        if (scanBounded) warnings.Add("planned-trash-scan-bounded");
        return [.. warnings];
    }

    public async Task<RetentionSettingsUpdateResult> UpdateAsync(int expectedSettingsRevision, int expectedQuotaRevision,
        RetentionSettings settings, SpoolQuotaSettings quota, PreviewCacheSettings previewCache, bool acknowledgeImpact,
        CancellationToken cancellationToken)
    {
        RetentionSettingsImpact impact = await PreviewAsync(settings, quota, previewCache, cancellationToken);
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        RetentionPolicyEntity policy = await LockOrCreatePolicyAsync(cancellationToken);
        SpoolQuotaSettings? staged = await quotaStore.StageSettingsUpdateAsync(expectedQuotaRevision, quota,
            acknowledgeImpact && impact.QuotaActivationAcknowledgementRequired, cancellationToken);
        string? error = GetUpdateError(policy, staged, expectedSettingsRevision, settings, impact, acknowledgeImpact);
        if (error is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            RetentionSettingsImpact rejectedImpact = error == "IMPACT_ACKNOWLEDGEMENT_REQUIRED"
                ? impact with { ImpactAcknowledgementRequired = true } : impact;
            return new(false, await GetAsync(cancellationToken), rejectedImpact, error);
        }

        int nextRevision = checked(policy.Revision + 1);
        policy.Revision = nextRevision;
        policy.PolicyJson = JsonSerializer.Serialize(settings with { Revision = nextRevision }, JsonOptions);
        policy.PreviewCacheJson = JsonSerializer.Serialize(previewCache, JsonOptions);
        policy.UpdatedAt = clock.GetUtcNow();
        database.RetentionPolicyHistory.Add(new RetentionPolicyHistoryEntity { Revision = nextRevision,
            PolicyJson = policy.PolicyJson, PreviewCacheJson = policy.PreviewCacheJson, CommittedAt = policy.UpdatedAt });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var updated = new RetentionSettingsDocument(ReadPolicy(policy.PolicyJson), staged!, ReadPreviewCache(policy.PreviewCacheJson),
            new PreviewCacheSettings(effectivePreviewSettings.CacheMode, effectivePreviewSettings.MemoryCacheBytes),
            ReadPreviewCache(policy.PreviewCacheJson) != new PreviewCacheSettings(effectivePreviewSettings.CacheMode, effectivePreviewSettings.MemoryCacheBytes));
        return new(true, updated, impact, null);
    }

    private async Task<RetentionPolicyEntity> LockOrCreatePolicyAsync(CancellationToken cancellationToken)
    {
        RetentionPolicyEntity? policy = await database.RetentionPolicies.FromSqlInterpolated(
            $"SELECT * FROM retention_policy WHERE \"Id\" = 1 FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (policy is null)
        {
            RetentionSettings initial = new();
            policy = new RetentionPolicyEntity { Id = 1, Revision = initial.Revision,
                PolicyJson = JsonSerializer.Serialize(initial, JsonOptions),
                PreviewCacheJson = JsonSerializer.Serialize(new PreviewCacheSettings(PreviewCacheMode.Disk, 64L * 1024 * 1024), JsonOptions),
                UpdatedAt = clock.GetUtcNow() };
            database.RetentionPolicies.Add(policy);
            database.RetentionPolicyHistory.Add(new RetentionPolicyHistoryEntity { Revision = initial.Revision,
                PolicyJson = policy.PolicyJson, PreviewCacheJson = policy.PreviewCacheJson, CommittedAt = policy.UpdatedAt });
        }
        return policy;
    }

    private static string? GetUpdateError(RetentionPolicyEntity policy, SpoolQuotaSettings? staged,
        int expectedRevision, RetentionSettings settings, RetentionSettingsImpact impact, bool acknowledgeImpact)
    {
        if (policy.Revision != expectedRevision || staged is null) return "REVISION_CONFLICT";
        bool enablingRemoval = !ReadPolicy(policy.PolicyJson).AutomaticRemovalEnabled && settings.AutomaticRemovalEnabled;
        if (enablingRemoval && !acknowledgeImpact) return "IMPACT_ACKNOWLEDGEMENT_REQUIRED";
        if (impact.QuotaActivationAcknowledgementRequired && !acknowledgeImpact)
            return "QUOTA_ACTIVATION_ACKNOWLEDGEMENT_REQUIRED";
        if (enablingRemoval && !impact.Complete) return "IMPACT_INCOMPLETE";
        return null;
    }

    private static RetentionSettings ReadPolicy(string? json) => json is null ? new RetentionSettings()
        : JsonSerializer.Deserialize<RetentionSettings>(json, JsonOptions) ?? new RetentionSettings();

    private static PreviewCacheSettings ReadPreviewCache(string? json) => json is null
        ? new PreviewCacheSettings(PreviewCacheMode.Disk, 64L * 1024 * 1024)
        : JsonSerializer.Deserialize<PreviewCacheSettings>(json, JsonOptions)
            ?? new PreviewCacheSettings(PreviewCacheMode.Disk, 64L * 1024 * 1024);

    private IQueryable<CaptureEntity> EligibleCaptures(DateTimeOffset now, bool pressureOnly)
    {
        IQueryable<CaptureEntity> captures = database.Captures.AsNoTracking().Where(capture =>
            capture.RetentionDisposition == "Active" && !capture.RetentionFactsDirty && capture.RetentionTier != null
            && (pressureOnly ? capture.RetentionPressureEligibleAt <= now
                : capture.RetentionPressureEligibleAt <= now || capture.RetentionTrashEligibleAt <= now)
            && database.CaptureAssets.Any(member => member.CaptureId == capture.Id)
            && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && !database.Assets.Any(asset => asset.Id == member.AssetId && asset.State == "Spooled"
                    && !database.AssetRemovalOperations.Any(operation => operation.AssetId == asset.Id)))
            && !database.PreservationHoldAssets.Any(member => database.CaptureAssets.Any(captureAsset =>
                captureAsset.CaptureId == capture.Id && captureAsset.AssetId == member.AssetId)
                && database.PreservationHolds.Any(hold => hold.Id == member.HoldId && hold.ResolvedAt == null))
            && !database.Deliveries.Any(delivery => delivery.State == ShutterSpool.Domain.Delivery.DeliveryState.Running
                && database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && (member.AssetId == delivery.AssetId || member.AssetId == delivery.SourceAssetId)))
            && !database.CameraUploadAdmissions.Any(admission => admission.AssetId != null
                && database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && member.AssetId == admission.AssetId.Value)
                && (admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Admitted
                    || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Receiving
                    || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Received
                    || admission.State == ShutterSpool.Application.Ingress.CameraTransferState.Handoff)));
        return captures;
    }

    private sealed record SelectionPlan(bool Complete, int? EligibleCount, int? Count, long? Bytes, bool ScanBounded = false);

    private sealed record PlannedCandidate(Guid Id, long Bytes);
}
