using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Captures;
using ShutterSpool.Application.Curation;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class CaptureReadStore(ShutterSpoolDbContext database, TimeProvider? clock = null,
    WorkspaceAccessContext? access = null) : ICaptureReadStore
{
    private const int InboxAssetPreviewLimit = 8;

    public async Task<CaptureInboxPage> GetInboxAsync(int limit, DateTimeOffset? before, Guid? beforeId,
        CaptureReadFilter filter, CancellationToken cancellationToken)
    {
        IQueryable<CaptureEntity> query = WorkspaceCaptureVisibility.Apply(database.Captures.AsNoTracking(), access)
            .Where(capture => capture.RetentionDisposition == "Active"
                && !database.PrivacyErasureAssets.Any(erased => database.CaptureAssets.Any(member =>
                    member.CaptureId == capture.Id && member.AssetId == erased.AssetId)));
        if (filter.FavoritesOnly) query = query.Where(capture => capture.IsFavorite);
        if (filter.SourceId is Guid sourceId) query = query.Where(capture => capture.SourceId == sourceId);
        if (filter.SourceIds is { Length: > 0 } sourceIds) query = query.Where(capture => sourceIds.Contains(capture.SourceId));
        if (filter.CollectionIds is { Length: > 0 } collectionIds)
            query = query.Where(capture => collectionIds.Contains(capture.CollectionId));
        if (filter.RatingMaximum is int maximum)
        {
            query = query.Where(capture => capture.Rating <= maximum);
            if (filter.Rating is int minimum) query = query.Where(capture => capture.Rating >= minimum);
        }
        else if (filter.Rating is int rating) query = query.Where(capture => capture.Rating == rating);
        if (filter.Selection == CaptureSelection.Rejected) query = query.Where(capture => capture.Rating == -1);
        if (filter.Selection == CaptureSelection.Neutral) query = query.Where(capture => capture.Rating == 0);
        if (filter.Selection == CaptureSelection.Picked) query = query.Where(capture => capture.Rating >= 1);
        if (filter.Tag is string tag) query = query.Where(capture => capture.Tags.Contains(tag));
        if (filter.Labels is { Length: > 0 } labels)
            query = query.Where(capture => labels.Contains("None") && capture.Label == null
                || capture.Label != null && labels.Contains(capture.Label));
        if (filter.Released.HasValue) query = query.Where(capture => (capture.ReleasedAt != null) == filter.Released.Value);
        if (filter.Held.HasValue) query = query.Where(capture => capture.Held == filter.Held.Value);
        if (filter.Attention.HasValue)
        {
            IQueryable<Guid> attentionCaptures = CapturesRequiringAttention((clock ?? TimeProvider.System).GetUtcNow());
            query = filter.Attention.Value ? query.Where(capture => attentionCaptures.Contains(capture.Id))
                : query.Where(capture => !attentionCaptures.Contains(capture.Id));
        }
        if (filter.Workflows is { Length: > 0 } workflows)
        {
            IQueryable<Guid> attentionCaptures = CapturesRequiringAttention((clock ?? TimeProvider.System).GetUtcNow());
            bool includeWaiting = workflows.Contains(CaptureWorkflowFilter.Waiting);
            bool includeHeld = workflows.Contains(CaptureWorkflowFilter.Held);
            bool includeReleased = workflows.Contains(CaptureWorkflowFilter.Released);
            bool includeAttention = workflows.Contains(CaptureWorkflowFilter.Attention);
            query = query.Where(capture => (includeWaiting && capture.ReleasedAt == null)
                || (includeHeld && capture.ReleasedAt == null && capture.Held)
                || (includeReleased && capture.ReleasedAt != null)
                || (includeAttention && attentionCaptures.Contains(capture.Id)));
        }
        if (filter.StartAtCaptureId is Guid startAtId)
        {
            DateTimeOffset? startAt = await query.Where(capture => capture.Id == startAtId)
                .Select(capture => (DateTimeOffset?)capture.FirstAcceptedAt).FirstOrDefaultAsync(cancellationToken);
            if (startAt is DateTimeOffset acceptedAt)
                query = filter.OldestFirst
                    ? query.Where(capture => capture.FirstAcceptedAt > acceptedAt
                        || capture.FirstAcceptedAt == acceptedAt && capture.Id.CompareTo(startAtId) >= 0)
                    : query.Where(capture => capture.FirstAcceptedAt < acceptedAt
                        || capture.FirstAcceptedAt == acceptedAt && capture.Id.CompareTo(startAtId) <= 0);
        }
        if (before is not null && beforeId is not null)
            query = filter.OldestFirst
                ? query.Where(capture => capture.FirstAcceptedAt > before
                    || (capture.FirstAcceptedAt == before && capture.Id.CompareTo(beforeId.Value) > 0))
                : query.Where(capture => capture.FirstAcceptedAt < before
                    || (capture.FirstAcceptedAt == before && capture.Id.CompareTo(beforeId.Value) < 0));

        IOrderedQueryable<CaptureEntity> ordered = filter.OldestFirst
            ? query.OrderBy(capture => capture.FirstAcceptedAt).ThenBy(capture => capture.Id)
            : query.OrderByDescending(capture => capture.FirstAcceptedAt).ThenByDescending(capture => capture.Id);
        CaptureEntity[] page = await ordered.Take(limit + 1).ToArrayAsync(cancellationToken);
        bool hasMore = page.Length > limit;
        CaptureEntity[] captures = [.. page.Take(limit)];
        CaptureRead[] items = await LoadReadsAsync(captures, compactAssets: true, cancellationToken);
        CaptureEntity? last = hasMore ? captures[^1] : null;
        bool anySourceUsesCuration = access?.IsViewer != true && await database.Sources.AsNoTracking()
            .AnyAsync(source => !source.IsArchived
                && source.CurationMode != ShutterSpool.Domain.Sources.SourceCurationMode.Immediate, cancellationToken);
        bool anyCaptureNeedsCuration = await WorkspaceCaptureVisibility.Apply(database.Captures.AsNoTracking(), access).AnyAsync(capture =>
            capture.RetentionDisposition == "Active" && capture.ReleasedAt == null
            && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId))
            && (capture.Held || capture.Policy != ShutterSpool.Domain.Sources.SourceCurationMode.Immediate), cancellationToken);
        return new(items, last?.FirstAcceptedAt, last?.Id, anySourceUsesCuration, anyCaptureNeedsCuration);
    }

    public async Task<CaptureRead?> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        CaptureEntity? capture = await WorkspaceCaptureVisibility.Apply(database.Captures.AsNoTracking(), access)
            .SingleOrDefaultAsync(item => item.Id == captureId
                && !database.PrivacyErasureAssets.Any(erased => database.CaptureAssets.Any(member =>
                    member.CaptureId == item.Id && member.AssetId == erased.AssetId)), cancellationToken);
        return capture is null ? null : (await LoadReadsAsync([capture], compactAssets: false, cancellationToken))[0];
    }

    public async Task<string[]> GetTagsAsync(string query, CancellationToken cancellationToken)
    {
        bool viewer = access?.IsViewer == true;
        bool releasedOnly = access?.ReleasedOnly == true;
        string[] tags = await database.Database.SqlQuery<string>($"""
            SELECT DISTINCT ON (lower(tag)) tag AS "Value"
            FROM captures CROSS JOIN LATERAL unnest("Tags") AS tags(tag)
            WHERE strpos(lower(tag), lower({query})) > 0
              AND (NOT {viewer} OR "RetentionDisposition" = 'Active')
              AND (NOT {releasedOnly} OR ("ReleasedAt" IS NOT NULL AND "Rating" <> -1))
              AND NOT EXISTS (
                  SELECT 1
                  FROM capture_assets AS member
                  JOIN privacy_erasure_assets AS erased ON erased."AssetId" = member."AssetId"
                  WHERE member."CaptureId" = captures."Id")
            ORDER BY lower(tag), tag
            LIMIT 12
            """).ToArrayAsync(cancellationToken);
        return tags;
    }

    private IQueryable<Guid> CapturesRequiringAttention(DateTimeOffset now) => database.Captures.AsNoTracking()
        .Select(capture => new CaptureAttentionFacts
        {
            CaptureId = capture.Id,
            IsReleased = capture.ReleasedAt != null,
            HasUnroutedPrimaryAsset = database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && member.IsPrimary && !database.RoutedAssets.Any(routed => routed.AssetId == member.AssetId)),
            HasBlockedRouting = database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.RoutingDecisions.Any(decision => decision.AssetId == member.AssetId
                    && decision.IsCurrent && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                        || decision.Kind == RoutingDecisionKindValue.DestinationDisabled))),
            HasBlockedDelivery = database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.Deliveries.Any(delivery => delivery.AssetId == member.AssetId
                    && (delivery.State == DeliveryState.Failed || delivery.State == DeliveryState.Ambiguous
                        || delivery.State == DeliveryState.Disabled))),
            HasRetentionUrgency = capture.RetentionDisposition == "Active" && !capture.RetentionFactsDirty
                && capture.RetentionAttentionAt != null && capture.RetentionAttentionAt <= now,
        })
        .Where(CaptureAttentionPolicy.RequiresAttention)
        .Select(facts => facts.CaptureId);

    private async Task<CaptureRead[]> LoadReadsAsync(CaptureEntity[] captures, bool compactAssets,
        CancellationToken cancellationToken)
    {
        if (captures.Length == 0) return [];
        Guid[] captureIds = [.. captures.Select(capture => capture.Id)];
        Guid[] sourceIds = [.. captures.Select(capture => capture.SourceId).Distinct()];
        Guid[] collectionIds = [.. captures.Select(capture => capture.CollectionId).Distinct()];
        Dictionary<Guid, CaptureCollectionRead> collectionRows = await database.Collections.AsNoTracking()
            .Where(item => collectionIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, item => new CaptureCollectionRead(item.Id, item.Name, item.Path), cancellationToken);
        var sourceRows = await (from source in database.Sources.AsNoTracking()
            join collection in database.Collections.AsNoTracking() on source.CollectionId equals collection.Id
            where sourceIds.Contains(source.Id)
            select new { source.Id, source.Name, source.CurationMode, source.CurationDelaySeconds,
                source.CollectionId, CollectionName = collection.Name,
                Enabled = database.SourceCredentials.Any(credential => credential.SourceId == source.Id && credential.IsIngressEnabled) })
            .ToDictionaryAsync(source => source.Id, cancellationToken);
        var membershipProjection = from member in database.CaptureAssets.AsNoTracking()
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where captureIds.Contains(member.CaptureId)
            select new { member.CaptureId, member.AssetId, member.Role, member.IsPrimary, member.RelatedAssetId,
                asset.OriginalName, asset.IngestedAt, asset.Size, asset.MetadataState, asset.MetadataOutcome, asset.MetadataReason,
                asset.DetectedMediaType, asset.MetadataRole, asset.CaptureTimeLocal, asset.CaptureTimeOffset,
                asset.CaptureTimeFractionalDigits, asset.CaptureTimeTag, asset.CaptureTimeUncertain, asset.CameraMake,
                asset.CameraMakeTag, asset.CameraModel, asset.CameraModelTag, asset.CameraSerial, asset.CameraSerialTag,
                asset.ExtensionMismatch };
        var boundedMemberships = compactAssets
            ? membershipProjection.Where(item => (
                from candidate in database.CaptureAssets.AsNoTracking()
                join candidateAsset in database.Assets.AsNoTracking() on candidate.AssetId equals candidateAsset.Id
                where candidate.CaptureId == item.CaptureId
                orderby candidate.IsPrimary descending, candidateAsset.IngestedAt, candidate.AssetId
                select candidate.AssetId).Take(InboxAssetPreviewLimit).Contains(item.AssetId))
            : membershipProjection;
        var membershipRows = await boundedMemberships.OrderByDescending(item => item.IsPrimary)
            .ThenBy(item => item.IngestedAt).ThenBy(item => item.AssetId)
            .Select(item => new Membership(item.CaptureId, item.AssetId, item.Role, item.IsPrimary,
                item.RelatedAssetId, item.OriginalName, item.IngestedAt, item.Size, item.MetadataState, item.MetadataOutcome,
                item.MetadataReason, item.DetectedMediaType, item.MetadataRole, item.CaptureTimeLocal,
                item.CaptureTimeOffset, item.CaptureTimeFractionalDigits, item.CaptureTimeTag, item.CaptureTimeUncertain,
                item.CameraMake, item.CameraMakeTag, item.CameraModel, item.CameraModelTag, item.CameraSerial,
                item.CameraSerialTag, item.ExtensionMismatch)).ToArrayAsync(cancellationToken);
        var assetCounts = await database.CaptureAssets.AsNoTracking().Where(member => captureIds.Contains(member.CaptureId))
            .GroupBy(member => member.CaptureId).Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        var summaries = await database.Captures.AsNoTracking().Where(capture => captureIds.Contains(capture.Id))
            .Select(capture => new
            {
                capture.Id,
                Waiting = database.CaptureAssets.Count(member => member.CaptureId == capture.Id && member.IsPrimary
                    && !database.RoutedAssets.Any(routed => routed.AssetId == member.AssetId)),
                NoRoute = database.CaptureAssets.Count(member => member.CaptureId == capture.Id && member.IsPrimary
                    && database.RoutedAssets.Any(routed => routed.AssetId == member.AssetId)
                    && !database.RoutingDecisions.Any(decision => decision.AssetId == member.AssetId && decision.IsCurrent
                        && decision.Kind != RoutingDecisionKindValue.NoMatch)),
                InvalidRouting = database.CaptureAssets.Count(member => member.CaptureId == capture.Id
                    && database.RoutingDecisions.Any(decision => decision.AssetId == member.AssetId && decision.IsCurrent
                        && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                            || decision.Kind == RoutingDecisionKindValue.DestinationDisabled))),
                Queued = database.Deliveries.Count(delivery => delivery.State == DeliveryState.Pending
                    && database.CaptureAssets.Any(member => member.CaptureId == capture.Id && member.AssetId == delivery.AssetId)),
                Running = database.Deliveries.Count(delivery => delivery.State == DeliveryState.Running
                    && database.CaptureAssets.Any(member => member.CaptureId == capture.Id && member.AssetId == delivery.AssetId)),
                RetryScheduled = database.Deliveries.Count(delivery => delivery.State == DeliveryState.RetryWait
                    && database.CaptureAssets.Any(member => member.CaptureId == capture.Id && member.AssetId == delivery.AssetId)),
                Confirmed = database.Deliveries.Count(delivery => delivery.State == DeliveryState.Delivered
                    && database.CaptureAssets.Any(member => member.CaptureId == capture.Id && member.AssetId == delivery.AssetId)),
                DeliveryAttention = database.Deliveries.Count(delivery =>
                    (delivery.State == DeliveryState.Failed || delivery.State == DeliveryState.Ambiguous
                        || delivery.State == DeliveryState.Disabled)
                    && database.CaptureAssets.Any(member => member.CaptureId == capture.Id && member.AssetId == delivery.AssetId)),
            }).ToDictionaryAsync(item => item.Id, cancellationToken);

        return [.. captures.Select(capture =>
        {
            Membership[] assets = [.. membershipRows.Where(item => item.CaptureId == capture.Id)];
            string displayName = assets.FirstOrDefault(item => item.IsPrimary)?.OriginalName
                ?? assets.FirstOrDefault()?.OriginalName ?? "Capture";
            var summary = summaries[capture.Id];
            int attention = summary.InvalidRouting + summary.DeliveryAttention;
            var source = sourceRows[capture.SourceId];
            CaptureCollectionRead? currentCollection = collectionRows.GetValueOrDefault(capture.CollectionId);
            CaptureCollectionRead? releasedCollection = capture.ReleasedCollectionId is Guid releasedId
                && capture.ReleasedCollectionName is string releasedName
                && capture.ReleasedCollectionPath is string releasedPath
                    ? new(releasedId, releasedName, releasedPath) : null;
            int available = assetCounts.GetValueOrDefault(capture.Id);
            return new CaptureRead(capture.Id, displayName, capture.FirstAcceptedAt, capture.SourceId,
                new(source.Id, source.Name, source.Enabled, source.CurationMode.ToString(),
                    source.CurationMode == ShutterSpool.Domain.Sources.SourceCurationMode.Delay ? source.CurationDelaySeconds : null,
                    source.CollectionId, source.CollectionName),
                ToCuration(capture), [.. assets.Select(item => new CaptureAssetRead(item.AssetId, item.OriginalName,
                    item.Role, item.IsPrimary, item.RelatedAssetId, item.IngestedAt,
                    new(item.MetadataOutcome ?? "Pending", item.MetadataReason, item.DetectedMediaType, item.MetadataRole,
                        item.CaptureTimeLocal, item.CaptureTimeOffset, item.CaptureTimeFractionalDigits, item.CaptureTimeTag,
                        item.CaptureTimeUncertain, item.CameraMake, item.CameraMakeTag, item.CameraModel, item.CameraModelTag,
                        item.CameraSerial, item.CameraSerialTag, item.ExtensionMismatch, item.MetadataState), item.Size))], available, assets.Length < available,
                new(summary.Waiting, summary.NoRoute, summary.Queued, summary.Running, summary.RetryScheduled,
                    summary.Confirmed, attention), capture.IsFavorite, currentCollection, releasedCollection);
        })];
    }

    private static CaptureCurationState ToCuration(CaptureEntity capture) => new(capture.Id, capture.Revision,
        capture.Rating, capture.Label, [.. capture.Tags], capture.Selection, capture.Policy, capture.DelaySeconds,
        capture.ReleaseDueAt, capture.Held, capture.State.ToString(), capture.ReleasedAt,
        capture.ReleasedRevision, capture.ReleasedRating, capture.ReleasedLabel, [.. capture.ReleasedTags], capture.ReleasedSelection);

    private sealed record Membership(Guid CaptureId, Guid AssetId, string Role, bool IsPrimary, Guid? RelatedAssetId,
        string OriginalName, DateTimeOffset IngestedAt, long Size, string MetadataState, string? MetadataOutcome,
        string? MetadataReason, string? DetectedMediaType, string? MetadataRole, DateTime? CaptureTimeLocal,
        TimeSpan? CaptureTimeOffset, int? CaptureTimeFractionalDigits, string? CaptureTimeTag, bool CaptureTimeUncertain,
        string? CameraMake, string? CameraMakeTag, string? CameraModel, string? CameraModelTag,
        string? CameraSerial, string? CameraSerialTag, bool ExtensionMismatch);
}
