using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Captures;
using ShutterSpool.Application.Curation;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class CaptureReadStore(ShutterSpoolDbContext database, TimeProvider? clock = null,
    WorkspaceAccessContext? access = null) : ICaptureReadStore
{
    private const int InboxAssetPreviewLimit = 8;

    public async Task<CaptureInboxPage> GetInboxAsync(int limit, DateTimeOffset? before, Guid? beforeId,
        CaptureReadFilter filter, CancellationToken cancellationToken)
    {
        IQueryable<CaptureEntity> inbox = VisibleCaptures().Where(capture => capture.RetentionDisposition == "Active");
        IQueryable<CaptureEntity> query = ApplyInboxFilters(inbox, filter);
        query = await ApplyPageBoundaryAsync(query, filter, before, beforeId, cancellationToken);
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
                && source.CurationMode != SourceCurationMode.Immediate, cancellationToken);
        bool anyCaptureNeedsCuration = await inbox.AnyAsync(capture => capture.ReleasedAt == null
            && (capture.Held || capture.Policy != SourceCurationMode.Immediate), cancellationToken);
        return new(items, last?.FirstAcceptedAt, last?.Id, anySourceUsesCuration, anyCaptureNeedsCuration);
    }

    public async Task<CaptureRead?> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        CaptureEntity? capture = await VisibleCaptures().SingleOrDefaultAsync(item => item.Id == captureId, cancellationToken);
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

    private IQueryable<CaptureEntity> VisibleCaptures()
    {
        IQueryable<Guid> visibleCaptureIds = WorkspaceCaptureVisibility.VisibleCaptureIds(database.Captures.AsNoTracking(), access);
        return database.Captures.AsNoTracking().Where(capture => visibleCaptureIds.Contains(capture.Id)
            && !database.PrivacyErasureAssets.Any(erased => database.CaptureAssets.Any(member =>
                member.CaptureId == capture.Id && member.AssetId == erased.AssetId)));
    }

    private IQueryable<CaptureEntity> ApplyInboxFilters(IQueryable<CaptureEntity> query, CaptureReadFilter filter)
    {
        if (filter.FavoritesOnly) query = query.Where(capture => capture.IsFavorite);
        if (filter.SourceId is Guid sourceId) query = query.Where(capture => capture.SourceId == sourceId);
        if (filter.SourceIds is { Length: > 0 } sourceIds) query = query.Where(capture => sourceIds.Contains(capture.SourceId));
        if (filter.CollectionIds is { Length: > 0 } collectionIds)
            query = query.Where(capture => collectionIds.Contains(capture.CollectionId));
        query = ApplyCurationFilters(query, filter);
        return ApplyWorkflowFilters(query, filter);
    }

    private static IQueryable<CaptureEntity> ApplyCurationFilters(IQueryable<CaptureEntity> query, CaptureReadFilter filter)
    {
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
        return query;
    }

    private IQueryable<CaptureEntity> ApplyWorkflowFilters(IQueryable<CaptureEntity> query, CaptureReadFilter filter)
    {
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
        return query;
    }

    private static async Task<IQueryable<CaptureEntity>> ApplyPageBoundaryAsync(IQueryable<CaptureEntity> query,
        CaptureReadFilter filter, DateTimeOffset? before, Guid? beforeId, CancellationToken cancellationToken)
    {
        if (filter.StartAtCaptureId is Guid startAtId)
            query = await ApplyInclusiveStartAsync(query, startAtId, filter.OldestFirst, cancellationToken);
        if (before is not null && beforeId is not null)
            query = filter.OldestFirst
                ? query.Where(capture => capture.FirstAcceptedAt > before
                    || (capture.FirstAcceptedAt == before && capture.Id.CompareTo(beforeId.Value) > 0))
                : query.Where(capture => capture.FirstAcceptedAt < before
                    || (capture.FirstAcceptedAt == before && capture.Id.CompareTo(beforeId.Value) < 0));

        return query;
    }

    private static async Task<IQueryable<CaptureEntity>> ApplyInclusiveStartAsync(IQueryable<CaptureEntity> query,
        Guid startAtId, bool oldestFirst, CancellationToken cancellationToken)
    {
        DateTimeOffset? startAt = await query.Where(capture => capture.Id == startAtId)
            .Select(capture => (DateTimeOffset?)capture.FirstAcceptedAt).FirstOrDefaultAsync(cancellationToken);
        if (startAt is not DateTimeOffset acceptedAt) return query;

        return oldestFirst
            ? query.Where(capture => capture.FirstAcceptedAt > acceptedAt
                || capture.FirstAcceptedAt == acceptedAt && capture.Id.CompareTo(startAtId) >= 0)
            : query.Where(capture => capture.FirstAcceptedAt < acceptedAt
                || capture.FirstAcceptedAt == acceptedAt && capture.Id.CompareTo(startAtId) <= 0);
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
        var assetsByCapture = await LoadAssetsAsync(captureIds, compactAssets, cancellationToken);
        var assetCounts = await database.CaptureAssets.AsNoTracking().Where(member => captureIds.Contains(member.CaptureId))
            .GroupBy(member => member.CaptureId).Select(group => new { Id = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.Id, item => item.Count, cancellationToken);
        var summaries = await LoadDeliverySummariesAsync(captureIds, cancellationToken);

        return [.. captures.Select(capture =>
        {
            CaptureAssetRead[] assets = assetsByCapture[capture.Id].ToArray();
            string displayName = assets.FirstOrDefault()?.OriginalName ?? "Capture";
            var source = sourceRows[capture.SourceId];
            CaptureCollectionRead? currentCollection = collectionRows.GetValueOrDefault(capture.CollectionId);
            CaptureCollectionRead? releasedCollection = capture.ReleasedCollectionId is Guid releasedId
                && capture.ReleasedCollectionName is string releasedName
                && capture.ReleasedCollectionPath is string releasedPath
                    ? new(releasedId, releasedName, releasedPath) : null;
            int available = assetCounts.GetValueOrDefault(capture.Id);
            return new CaptureRead(capture.Id, displayName, capture.FirstAcceptedAt, capture.SourceId,
                new(source.Id, source.Name, source.Enabled, source.CurationMode.ToString(),
                    source.CurationMode == SourceCurationMode.Delay ? source.CurationDelaySeconds : null,
                    source.CollectionId, source.CollectionName),
                ToCuration(capture), assets, available, assets.Length < available, summaries[capture.Id], capture.IsFavorite,
                currentCollection, releasedCollection);
        })];
    }

    private async Task<ILookup<Guid, CaptureAssetRead>> LoadAssetsAsync(Guid[] captureIds, bool compactAssets,
        CancellationToken cancellationToken)
    {
        var membershipProjection = from member in database.CaptureAssets.AsNoTracking()
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where captureIds.Contains(member.CaptureId)
            select new { Member = member, Asset = asset };
        var boundedMemberships = compactAssets
            ? membershipProjection.Where(item => (
                from candidate in database.CaptureAssets.AsNoTracking()
                join candidateAsset in database.Assets.AsNoTracking() on candidate.AssetId equals candidateAsset.Id
                where candidate.CaptureId == item.Member.CaptureId
                orderby candidate.IsPrimary descending, candidateAsset.IngestedAt, candidate.AssetId
                select candidate.AssetId).Take(InboxAssetPreviewLimit).Contains(item.Member.AssetId))
            : membershipProjection;
        var membershipRows = await boundedMemberships.OrderByDescending(item => item.Member.IsPrimary)
            .ThenBy(item => item.Asset.IngestedAt).ThenBy(item => item.Member.AssetId)
            .Select(item => new
            {
                item.Member.CaptureId,
                Asset = new CaptureAssetRead(item.Member.AssetId, item.Asset.OriginalName, item.Member.Role, item.Member.IsPrimary,
                    item.Member.RelatedAssetId, item.Asset.IngestedAt,
                    new CaptureAssetMetadataRead(item.Asset.MetadataOutcome ?? "Pending", item.Asset.MetadataReason,
                        item.Asset.DetectedMediaType, item.Asset.MetadataRole, item.Asset.CaptureTimeLocal, item.Asset.CaptureTimeOffset,
                        item.Asset.CaptureTimeFractionalDigits, item.Asset.CaptureTimeTag, item.Asset.CaptureTimeUncertain,
                        item.Asset.CameraMake, item.Asset.CameraMakeTag, item.Asset.CameraModel, item.Asset.CameraModelTag,
                        item.Asset.CameraSerial, item.Asset.CameraSerialTag, item.Asset.ExtensionMismatch, item.Asset.MetadataState),
                    item.Asset.Size),
            }).ToArrayAsync(cancellationToken);
        return membershipRows.ToLookup(item => item.CaptureId, item => item.Asset);
    }

    private Task<Dictionary<Guid, CaptureDeliverySummary>> LoadDeliverySummariesAsync(Guid[] captureIds,
        CancellationToken cancellationToken)
    {
        var deliveries = from delivery in database.Deliveries
            join member in database.CaptureAssets on delivery.AssetId equals member.AssetId
            select new { member.CaptureId, delivery.State };
        var summaries = from capture in database.Captures.AsNoTracking()
            where captureIds.Contains(capture.Id)
            select new
            {
                capture.Id,
                Waiting = capture.Assets.Count(member => member.IsPrimary
                    && !database.RoutedAssets.Any(routed => routed.AssetId == member.AssetId)),
                NoRoute = capture.Assets.Count(member => member.IsPrimary
                    && database.RoutedAssets.Any(routed => routed.AssetId == member.AssetId)
                    && !database.RoutingDecisions.Any(decision => decision.AssetId == member.AssetId && decision.IsCurrent
                        && decision.Kind != RoutingDecisionKindValue.NoMatch)),
                InvalidRouting = capture.Assets.Count(member =>
                    database.RoutingDecisions.Any(decision => decision.AssetId == member.AssetId && decision.IsCurrent
                        && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                            || decision.Kind == RoutingDecisionKindValue.DestinationDisabled))),
                Queued = deliveries.Count(delivery => delivery.CaptureId == capture.Id && delivery.State == DeliveryState.Pending),
                Running = deliveries.Count(delivery => delivery.CaptureId == capture.Id && delivery.State == DeliveryState.Running),
                RetryScheduled = deliveries.Count(delivery => delivery.CaptureId == capture.Id && delivery.State == DeliveryState.RetryWait),
                Confirmed = deliveries.Count(delivery => delivery.CaptureId == capture.Id && delivery.State == DeliveryState.Delivered),
                DeliveryAttention = deliveries.Count(delivery => delivery.CaptureId == capture.Id
                    && (delivery.State == DeliveryState.Failed || delivery.State == DeliveryState.Ambiguous || delivery.State == DeliveryState.Disabled)),
            };
        return summaries.ToDictionaryAsync(item => item.Id, item => new CaptureDeliverySummary(item.Waiting, item.NoRoute,
            item.Queued, item.Running, item.RetryScheduled, item.Confirmed, item.InvalidRouting + item.DeliveryAttention),
            cancellationToken);
    }

    private static CaptureCurationState ToCuration(CaptureEntity capture) => new(capture.Id, capture.Revision,
        capture.Rating, capture.Label, [.. capture.Tags], capture.Selection, capture.Policy, capture.DelaySeconds,
        capture.ReleaseDueAt, capture.Held, capture.State.ToString(), capture.ReleasedAt,
        capture.ReleasedRevision, capture.ReleasedRating, capture.ReleasedLabel, [.. capture.ReleasedTags], capture.ReleasedSelection);
}
