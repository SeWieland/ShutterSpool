using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Retention;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCaptureRetentionStore(ShutterSpoolDbContext database, TimeProvider clock) :
    ICaptureRetentionMutationStore, ICaptureRetentionReadStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CaptureRetentionSummary?> GetCaptureSummaryAsync(Guid captureId, DateTimeOffset asOf,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<Guid, CaptureRetentionSummary> summaries = await GetCaptureSummariesAsync(
            [captureId], asOf, cancellationToken);
        return summaries.GetValueOrDefault(captureId);
    }

    public async Task<IReadOnlyDictionary<Guid, CaptureRetentionSummary>> GetCaptureSummariesAsync(
        IReadOnlyCollection<Guid> captureIds, DateTimeOffset asOf, CancellationToken cancellationToken)
    {
        if (captureIds.Count > 100) throw new ArgumentOutOfRangeException(nameof(captureIds));
        Guid[] requestedIds = [.. captureIds.Where(id => id != Guid.Empty).Distinct()];
        if (requestedIds.Length == 0) return new Dictionary<Guid, CaptureRetentionSummary>();
        CaptureEntity[] captures = await database.Captures.AsNoTracking()
            .Where(item => requestedIds.Contains(item.Id)
                && !database.PrivacyErasureAssets.Any(erased => database.CaptureAssets.Any(member =>
                    member.CaptureId == item.Id && member.AssetId == erased.AssetId)))
            .ToArrayAsync(cancellationToken);
        return await BuildSummariesAsync(captures, asOf, await ReadSettingsAsync(cancellationToken), cancellationToken);
    }

    public async Task<RetentionTrashPage> GetTrashPageAsync(int limit, string? cursor,
        CancellationToken cancellationToken)
    {
        DateTimeOffset? before = null;
        Guid beforeId = Guid.Empty;
        if (cursor is not null)
        {
            if (cursor.Length > 128) throw new ArgumentException("The trash cursor is invalid.", nameof(cursor));
            try
            {
                string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+').Replace('_', '/')
                    .PadRight((cursor.Length + 3) / 4 * 4, '=')));
                string[] parts = decoded.Split(':', 2);
                if (parts.Length != 2 || !long.TryParse(parts[0], out long ticks)
                    || !Guid.TryParseExact(parts[1], "N", out beforeId)) throw new FormatException();
                before = new DateTimeOffset(ticks, TimeSpan.Zero);
            }
            catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
            { throw new ArgumentException("The trash cursor is invalid.", nameof(cursor)); }
        }
        IQueryable<CaptureEntity> query = database.Captures.AsNoTracking()
            .Where(item => (item.RetentionDisposition == "Trash" || item.RetentionDisposition == "Purging")
                && !database.PrivacyErasureAssets.Any(erased => database.CaptureAssets.Any(member =>
                    member.CaptureId == item.Id && member.AssetId == erased.AssetId)));
        if (before is DateTimeOffset boundary)
            query = query.Where(item => item.TrashEnteredAt > boundary
                || item.TrashEnteredAt == boundary && item.Id.CompareTo(beforeId) > 0);
        CaptureEntity[] captures = await query.OrderBy(item => item.TrashEnteredAt).ThenBy(item => item.Id)
            .Take(limit + 1).ToArrayAsync(cancellationToken);
        bool more = captures.Length > limit;
        CaptureEntity[] page = [.. captures.Take(limit)];
        DateTimeOffset now = clock.GetUtcNow();
        RetentionSettings settings = await ReadSettingsAsync(cancellationToken);
        IReadOnlyDictionary<Guid, CaptureRetentionSummary> summaries = await BuildSummariesAsync(page, now, settings,
            cancellationToken);
        Guid[] pageIds = [.. page.Select(item => item.Id)];
        var names = await (from member in database.CaptureAssets.AsNoTracking()
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where pageIds.Contains(member.CaptureId) && member.IsPrimary
            select new { member.CaptureId, asset.OriginalName }).ToArrayAsync(cancellationToken);
        Dictionary<Guid, string> displayNames = names.GroupBy(item => item.CaptureId)
            .ToDictionary(group => group.Key, group => group.First().OriginalName);
        var items = new List<RetentionTrashItem>(page.Length);
        foreach (CaptureEntity capture in page)
        {
            CaptureRetentionSummary summary = summaries[capture.Id];
            string name = displayNames.GetValueOrDefault(capture.Id, "Capture");
            items.Add(new(capture.Id, capture.Revision, name, capture.TrashEnteredAt ?? capture.FirstAcceptedAt,
                capture.RecoverableUntil ?? now, capture.TrashReason ?? "manual", summary));
        }
        CaptureEntity? last = more ? captures[limit - 1] : null;
        string? next = last?.TrashEnteredAt is DateTimeOffset time
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{time.UtcDateTime.Ticks}:{last.Id:N}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_') : null;
        return new([.. items], next);
    }

    public async Task<RetentionOperationResult> TrashAsync(Guid captureId, int expectedRevision, string reason,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity? capture = await LockCaptureAsync(captureId, cancellationToken);
        if (capture is null) return await FinishAsync(transaction, RetentionOperationStatus.NotFound, null, "CAPTURE_NOT_FOUND", cancellationToken);
        if (capture.Revision != expectedRevision)
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "REVISION_CONFLICT", cancellationToken);
        if (capture.RetentionDisposition != "Active")
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "RETENTION_TRANSITION_IN_PROGRESS", cancellationToken);
        bool activeDelivery = await database.Deliveries.AnyAsync(delivery =>
            database.CaptureAssets.Any(member => member.CaptureId == captureId && member.AssetId == delivery.AssetId)
            && (delivery.State == DeliveryState.Running
                || delivery.State == DeliveryState.Ambiguous), cancellationToken);
        if (activeDelivery)
            return await FinishAsync(transaction, RetentionOperationStatus.Blocked, capture, "DELIVERY_RECONCILIATION_REQUIRED", cancellationToken);

        RetentionSettings settings = await ReadSettingsAsync(cancellationToken);
        capture.RetentionDisposition = "Trash";
        capture.TrashReason = reason;
        capture.TrashEnteredAt = now;
        capture.RecoverableUntil = now + settings.TrashRecoveryWindow;
        capture.RetentionPaused = true;
        capture.Revision = checked(capture.Revision + 1);
        capture.RetentionPolicyRevision = settings.Revision;
        await database.SaveChangesAsync(cancellationToken);
        CaptureRetentionSummary summary = await BuildSummaryAsync(capture, now, cancellationToken, settings);
        await transaction.CommitAsync(cancellationToken);
        return new(RetentionOperationStatus.Committed, summary);
    }

    public async Task<RetentionOperationResult> RestoreAsync(Guid captureId, int expectedRevision, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity? capture = await LockCaptureAsync(captureId, cancellationToken);
        if (capture is null) return await FinishAsync(transaction, RetentionOperationStatus.NotFound, null, "CAPTURE_NOT_FOUND", cancellationToken);
        if (capture.Revision != expectedRevision)
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "REVISION_CONFLICT", cancellationToken);
        if (capture.RetentionDisposition != "Trash")
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "CAPTURE_NOT_RESTORABLE", cancellationToken);
        bool removalStarted = await database.CaptureAssets.AnyAsync(member => member.CaptureId == captureId
            && database.AssetRemovalOperations.Any(operation => operation.AssetId == member.AssetId), cancellationToken);
        if (removalStarted)
            return await FinishAsync(transaction, RetentionOperationStatus.Blocked, capture, "PURGE_ALREADY_STARTED", cancellationToken);

        RetentionSettings settings = await ReadSettingsAsync(cancellationToken);
        capture.RetentionDisposition = "Active";
        capture.RetentionPaused = true;
        capture.RestoreProtectedUntil = now + settings.RestoreProtection;
        capture.RetentionFactsDirty = true;
        capture.Revision = checked(capture.Revision + 1);
        await database.SaveChangesAsync(cancellationToken);
        CaptureRetentionSummary summary = await BuildSummaryAsync(capture, now, cancellationToken, settings);
        await transaction.CommitAsync(cancellationToken);
        return new(RetentionOperationStatus.Committed, summary);
    }

    public async Task<RetentionOperationResult> ResumeAsync(Guid captureId, int expectedRevision,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity? capture = await LockCaptureAsync(captureId, cancellationToken);
        if (capture is null) return await FinishAsync(transaction, RetentionOperationStatus.NotFound, null, "CAPTURE_NOT_FOUND", cancellationToken);
        if (capture.Revision != expectedRevision)
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "REVISION_CONFLICT", cancellationToken);
        if (capture.RetentionDisposition != "Active" || !capture.RetentionPaused)
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "CAPTURE_NOT_PAUSED", cancellationToken);
        capture.RetentionPaused = false;
        capture.Revision = checked(capture.Revision + 1);
        await database.SaveChangesAsync(cancellationToken);
        CaptureRetentionSummary summary = await BuildSummaryAsync(capture, clock.GetUtcNow(), cancellationToken,
            await ReadSettingsAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new(RetentionOperationStatus.Committed, summary);
    }

    public async Task<RetentionOperationResult> RecordNoDeliveryIntentAsync(Guid captureId, int expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity? capture = await LockCaptureAsync(captureId, cancellationToken);
        if (capture is null)
            return await FinishAsync(transaction, RetentionOperationStatus.NotFound, null, "CAPTURE_NOT_FOUND", cancellationToken);
        if (capture.Revision != expectedRevision)
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "REVISION_CONFLICT", cancellationToken);
        if (capture.RetentionDisposition != "Active")
            return await FinishAsync(transaction, RetentionOperationStatus.Conflict, capture, "RETENTION_TRANSITION_IN_PROGRESS", cancellationToken);

        Guid[] assetIds = await database.CaptureAssets.AsNoTracking().Where(member => member.CaptureId == captureId)
            .Select(member => member.AssetId).ToArrayAsync(cancellationToken);
        int routed = await database.RoutedAssets.CountAsync(item => assetIds.Contains(item.AssetId), cancellationToken);
        int noMatch = await database.CaptureAssets.CountAsync(member => member.CaptureId == captureId
            && database.RoutingDecisions.Any(decision => decision.AssetId == member.AssetId
                && decision.IsCurrent && decision.Kind == RoutingDecisionKindValue.NoMatch), cancellationToken);
        bool hasMatchedIntent = await database.RoutingDecisions.AnyAsync(decision => decision.CaptureId == captureId
            && (decision.Kind == RoutingDecisionKindValue.Matched
                || decision.RuleId != null && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                    || decision.Kind == RoutingDecisionKindValue.DestinationDisabled)), cancellationToken);
        bool hasDelivery = await database.Deliveries.AnyAsync(delivery => assetIds.Contains(delivery.AssetId), cancellationToken);
        if (assetIds.Length == 0 || routed != assetIds.Length || noMatch != assetIds.Length
            || hasMatchedIntent || hasDelivery)
            return await FinishAsync(transaction, RetentionOperationStatus.Blocked, capture,
                "NO_DELIVERY_REQUIRES_ROUTED_UNMATCHED_CAPTURE", cancellationToken);
        if (capture.NoDeliveryIntent)
            return await FinishAsync(transaction, RetentionOperationStatus.Committed, capture, "NO_DELIVERY_ALREADY_RECORDED", cancellationToken);

        capture.NoDeliveryIntent = true;
        capture.RetentionFactsDirty = true;
        capture.Revision = checked(capture.Revision + 1);
        await database.SaveChangesAsync(cancellationToken);
        CaptureRetentionSummary summary = await BuildSummaryAsync(capture, now, cancellationToken,
            await ReadSettingsAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new(RetentionOperationStatus.Committed, summary);
    }

    internal async Task<CaptureRetentionSummary> BuildSummaryAsync(CaptureEntity capture, DateTimeOffset now,
        CancellationToken cancellationToken, RetentionSettings settings)
    {
        IReadOnlyDictionary<Guid, CaptureRetentionSummary> summaries = await BuildSummariesAsync([capture], now,
            settings, cancellationToken);
        return summaries[capture.Id];
    }

    private async Task<IReadOnlyDictionary<Guid, CaptureRetentionSummary>> BuildSummariesAsync(
        CaptureEntity[] captures, DateTimeOffset now, RetentionSettings settings, CancellationToken cancellationToken)
    {
        if (captures.Length == 0) return new Dictionary<Guid, CaptureRetentionSummary>();

        Guid[] captureIds = [.. captures.Select(capture => capture.Id)];

        RetentionMemberFact[] members = await (
            from member in database.CaptureAssets.AsNoTracking()
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where captureIds.Contains(member.CaptureId)
            select new RetentionMemberFact(
                member.CaptureId,
                member.AssetId,
                member.RelatedAssetId,
                member.Role,
                member.GroupingReason,
                member.FavoriteQualificationLostAt,
                asset.Size,
                asset.IngestedAt,
                asset.State,
                asset.RetentionPolicyRevision,
                database.AssetRemovalOperations.Where(operation => operation.AssetId == asset.Id).Select(operation => operation.State).FirstOrDefault(),
                (from protectionMember in database.GalleryProtectionAssets
                 join protection in database.GalleryProtections on protectionMember.ProtectionId equals protection.Id
                 where protectionMember.AssetId == asset.Id && protection.ExpiresAt > now
                 select (DateTimeOffset?)protection.ExpiresAt).Max(),
                database.PreservationHoldAssets.Any(
                    holdMember => holdMember.AssetId == asset.Id
                    && database.PreservationHolds.Any(hold => hold.Id == holdMember.HoldId && hold.ResolvedAt == null))))
            .ToArrayAsync(cancellationToken);

        int[] policyRevisions = [.. members.Select(member => member.RetentionPolicyRevision).Distinct()];

        Dictionary<int, RetentionSettings> policies = await database.RetentionPolicyHistory.AsNoTracking()
            .Where(item => policyRevisions.Contains(item.Revision))
            .ToDictionaryAsync(item => item.Revision, item => JsonSerializer.Deserialize<RetentionSettings>(item.PolicyJson, JsonOptions)
                ?? new RetentionSettings(), cancellationToken);

        Guid[] evidenceAssetIds = [.. members.SelectMany(member =>
                member.GroupingReason == "recognized_replay" && member.RelatedAssetId is Guid supplierAssetId
                    ? new[] { member.AssetId, supplierAssetId }
                    : [member.AssetId])
            .Distinct()];

        RetentionDeliveryFact[] deliveryFacts = await database.Deliveries.AsNoTracking()
            .Where(delivery => evidenceAssetIds.Contains(delivery.AssetId))
            .Select(delivery => new RetentionDeliveryFact(delivery.DecisionId, delivery.AssetId,
                delivery.DestinationId, delivery.TargetPath, delivery.State, delivery.ConfirmedAt))
            .ToArrayAsync(cancellationToken);

        var deliveryGroups = deliveryFacts.GroupBy(delivery => delivery.AssetId).ToDictionary(group => group.Key, group => group.ToArray());

        RetentionIntentFact[] intentDecisions = await database.RoutingDecisions.AsNoTracking()
            .Where(decision => evidenceAssetIds.Contains(decision.AssetId)
                && (decision.Kind == RoutingDecisionKindValue.Matched
                    || decision.RuleId != null && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                        || decision.Kind == RoutingDecisionKindValue.DestinationDisabled)))
            .Select(decision => new RetentionIntentFact(decision.Id, decision.AssetId, decision.DestinationId,
                decision.TargetPath, decision.Kind)).ToArrayAsync(cancellationToken);

        var intentDecisionsByAsset = intentDecisions.GroupBy(decision => decision.AssetId)
            .ToDictionary(group => group.Key, group => group.Select(decision => decision.Id).ToArray());

        var deliveriesByDecision = deliveryFacts.GroupBy(delivery => delivery.DecisionId)
            .ToDictionary(group => group.Key, group => group.ToArray());

        Guid[] memberAssetIds = [.. members.Select(member => member.AssetId).Distinct()];

        var membersByCapture = members.ToLookup(member => member.CaptureId);

        HashSet<Guid> routedAssets = memberAssetIds.Length == 0 ? [] : await database.RoutedAssets.AsNoTracking()
            .Where(item => memberAssetIds.Contains(item.AssetId)).Select(item => item.AssetId).ToHashSetAsync(cancellationToken);

        HashSet<Guid> noMatchAssets = memberAssetIds.Length == 0 ? [] : await database.CaptureAssets.AsNoTracking()
            .Where(member => captureIds.Contains(member.CaptureId)
                && database.RoutingDecisions.Any(decision => decision.AssetId == member.AssetId
                    && decision.IsCurrent && decision.Kind == RoutingDecisionKindValue.NoMatch))
            .Select(member => member.AssetId).ToHashSetAsync(cancellationToken);

        HashSet<Guid> matchedCaptures = await database.RoutingDecisions.AsNoTracking()
            .Where(decision => captureIds.Contains(decision.CaptureId)
                && (decision.Kind == RoutingDecisionKindValue.Matched
                    || decision.RuleId != null && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                        || decision.Kind == RoutingDecisionKindValue.DestinationDisabled)))
            .Select(decision => decision.CaptureId).Distinct().ToHashSetAsync(cancellationToken);

        HashSet<Guid> erasedAssets = memberAssetIds.Length == 0 ? [] : await database.PrivacyErasureAssets.AsNoTracking()
            .Where(erased => memberAssetIds.Contains(erased.AssetId)).Select(erased => erased.AssetId).ToHashSetAsync(cancellationToken);

        var evidence = new RetentionSummaryEvidence(
            new(policies, membersByCapture),
            new(deliveryFacts, intentDecisions, deliveryGroups, intentDecisionsByAsset, deliveriesByDecision),
            new(routedAssets, noMatchAssets, matchedCaptures, erasedAssets));

        return CaptureRetentionSummaryBuilder.Build(captures, now, settings, evidence);
    }

    private async Task<CaptureEntity?> LockCaptureAsync(Guid captureId, CancellationToken cancellationToken) =>
        await database.Captures.FromSqlInterpolated($"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private Task<int> LockRoutingAsync(CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);

    private async Task<RetentionSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        string? json = await database.RetentionPolicies.AsNoTracking().Where(policy => policy.Id == 1)
            .Select(policy => policy.PolicyJson).SingleOrDefaultAsync(cancellationToken);
        return json is null ? new RetentionSettings() : JsonSerializer.Deserialize<RetentionSettings>(json, JsonOptions) ?? new RetentionSettings();
    }

    private async Task<RetentionOperationResult> FinishAsync(IDbContextTransaction transaction,
        RetentionOperationStatus status, CaptureEntity? capture, string error, CancellationToken cancellationToken)
    {
        CaptureRetentionSummary? summary = capture is null ? null : await BuildSummaryAsync(capture, clock.GetUtcNow(),
            cancellationToken, await ReadSettingsAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new(status, summary, error);
    }
}
