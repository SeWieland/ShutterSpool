using Microsoft.AspNetCore.WebUtilities;
using ShutterSpool.Application.Captures;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Retention;
using System.Globalization;
using System.Text;

namespace ShutterSpool.Server.Api.Captures;

public sealed record CaptureInboxAssetResponse(Guid Id, string DisplayName, string Variant, bool IsPrimary,
    Guid? RelatedAssetId, string? CameraMake = null, string? CameraModel = null);
public sealed record CaptureSourceResponse(Guid Id, string Name, bool Enabled, string CurationMode, int? DelaySeconds,
    Guid? CollectionId, string? CollectionName)
{
    internal static CaptureSourceResponse From(CaptureSourceRead source, bool includeConfiguration) =>
        new(source.Id, source.Name, source.Enabled, source.CurationMode, source.DelaySeconds,
            includeConfiguration ? source.CollectionId : null, includeConfiguration ? source.CollectionName : null);
}
public sealed record CaptureCollectionResponse(Guid Id, string Name, string Path);
public sealed record CaptureDeliverySummaryResponse(int WaitingForRouting, int NoRoute, int Queued, int Running,
    int RetryScheduled, int Confirmed, int Attention);
public sealed record RetentionTimelineResponse(string Kind, DateTimeOffset? StartedAt, DateTimeOffset? EndsAt,
    DateTimeOffset ObservedAt, string Meaning);
public sealed record RetentionOriginalResponse(Guid AssetId, string Variant, long ByteLength,
    DateTimeOffset? ProtectedUntil, string State);
public sealed record CaptureRetentionSummaryResponse(DateTimeOffset AsOf, int CaptureRevision, int RetentionRevision,
    string Disposition, bool IsFavorite, string? Tier, DateTimeOffset? PressureEligibleAt,
    DateTimeOffset? TrashEligibleAt, DateTimeOffset? RecoverableUntil, DateTimeOffset? ProtectedUntil,
    string[] EligibilityReasons, string[] ProtectionReasons, string Urgency, RetentionTimelineResponse? Timeline,
    RetentionOriginalResponse[] AvailableOriginals, bool IsPaused, string[] PauseReasons,
    bool CanRecordNoDeliveryIntent, string[] NoDeliveryIntentReasons)
{
    internal static CaptureRetentionSummaryResponse From(CaptureRetentionSummary summary) => new(
        summary.AsOf, summary.CaptureRevision, summary.RetentionRevision, Camel(summary.Disposition.ToString()),
        summary.IsFavorite, summary.Tier is RetentionTier tier ? Camel(tier.ToString()) : null, summary.PressureEligibleAt,
        summary.TrashEligibleAt, summary.RecoverableUntil, summary.ProtectedUntil, summary.EligibilityReasons,
        summary.ProtectionReasons, Camel(summary.Urgency.ToString()), summary.Timeline is null ? null : new(
            Camel(summary.Timeline.Kind.ToString()), summary.Timeline.StartedAt, summary.Timeline.EndsAt,
            summary.Timeline.ObservedAt, summary.Timeline.Meaning),
        [.. summary.AvailableOriginals.Select(item => new RetentionOriginalResponse(item.AssetId, item.Variant,
            item.ByteLength, item.ProtectedUntil, Camel(item.State.ToString())))],
        summary.IsPaused, summary.PauseReasons ?? [], summary.CanRecordNoDeliveryIntent,
        summary.NoDeliveryIntentReasons ?? []);

    private static string Camel(string value) => char.ToLowerInvariant(value[0]) + value[1..];
}
public sealed record CaptureInboxItemResponse(Guid Id, string DisplayName, DateTimeOffset AcceptedAt,
    CaptureCurationResponse Curation, CaptureInboxAssetResponse[] Assets, CaptureDeliverySummaryResponse DeliverySummary,
    Guid SourceId, CaptureSourceResponse Source, int AssetsAvailable, bool AssetsTruncated, bool IsFavorite,
    CaptureRetentionSummaryResponse? Retention, CaptureCollectionResponse? CurrentCollection,
    CaptureCollectionResponse? ReleasedCollection)
{
    internal static CaptureInboxItemResponse From(CaptureRead item, CaptureRetentionSummary? retention, bool includeSourceConfiguration) => new(item.Id, item.DisplayName,
        item.AcceptedAt, CaptureCurationResponse.From(item.Curation), [.. item.Assets.Select(asset =>
            new CaptureInboxAssetResponse(asset.Id, asset.OriginalName, asset.Variant, asset.IsPrimary,
                asset.RelatedAssetId, asset.Metadata.CameraMake, asset.Metadata.CameraModel))], new(item.DeliverySummary.WaitingForRouting, item.DeliverySummary.NoRoute,
            item.DeliverySummary.Queued, item.DeliverySummary.Running, item.DeliverySummary.RetryScheduled,
            item.DeliverySummary.Confirmed, item.DeliverySummary.Attention), item.SourceId,
        CaptureSourceResponse.From(item.Source, includeSourceConfiguration),
        item.AssetsAvailable, item.AssetsTruncated, item.IsFavorite,
        retention is null ? null : CaptureRetentionSummaryResponse.From(retention),
        item.CurrentCollection is null ? null : new(item.CurrentCollection.Id, item.CurrentCollection.Name, item.CurrentCollection.Path),
        item.ReleasedCollection is null ? null : new(item.ReleasedCollection.Id, item.ReleasedCollection.Name, item.ReleasedCollection.Path));
}
public sealed record CaptureInboxResponse(CaptureInboxItemResponse[] Items, string? NextCursor,
    bool AnySourceUsesCuration, bool AnyCaptureNeedsCuration);
public sealed record TagSuggestionsResponse(string[] Tags);
public sealed record CaptureDetailAssetResponse(Guid Id, string DisplayName, string Variant, bool IsPrimary,
    Guid? RelatedAssetId, string MetadataState, DateTime? CaptureTime, string? CaptureTimeOffset,
    string? CameraMake, string? CameraModel, bool ExtensionMismatch, string MetadataOutcome, string? MetadataReason,
    string? DetectedMediaType, string? DetectedRole, int? CaptureTimeFractionalDigits, string? CaptureTimeTag,
    bool CaptureTimeUncertain, string? CameraMakeTag, string? CameraModelTag, string? CameraSerial, string? CameraSerialTag,
    long Size = 0);
public sealed record CaptureDetailResponse(Guid Id, string DisplayName, DateTimeOffset AcceptedAt,
    CaptureCurationResponse Curation, CaptureDetailAssetResponse[] Assets, Guid? PreviewAssetId,
    CaptureDeliverySummaryResponse DeliverySummary, Guid SourceId, CaptureSourceResponse Source, int AssetsAvailable,
    bool IsFavorite, CaptureRetentionSummaryResponse? Retention, CaptureCollectionResponse? CurrentCollection,
    CaptureCollectionResponse? ReleasedCollection)
{
    internal static CaptureDetailResponse From(CaptureRead capture, CaptureRetentionSummary? retention, bool includeSourceConfiguration) => new(
        capture.Id, capture.DisplayName, capture.AcceptedAt, CaptureCurationResponse.From(capture.Curation),
        [.. capture.Assets.Select(asset => new CaptureDetailAssetResponse(asset.Id, asset.OriginalName,
            asset.Variant, asset.IsPrimary, asset.RelatedAssetId, asset.Metadata.MetadataState, asset.Metadata.CaptureTime,
            asset.Metadata.CaptureTimeOffset?.ToString("c", CultureInfo.InvariantCulture), asset.Metadata.CameraMake,
            asset.Metadata.CameraModel, asset.Metadata.ExtensionMismatch, asset.Metadata.MetadataOutcome, asset.Metadata.Reason,
            asset.Metadata.DetectedMediaType, asset.Metadata.Role, asset.Metadata.CaptureTimeFractionalDigits,
            asset.Metadata.CaptureTimeTag, asset.Metadata.CaptureTimeUncertain, asset.Metadata.CameraMakeTag,
            asset.Metadata.CameraModelTag, asset.Metadata.CameraSerial, asset.Metadata.CameraSerialTag, asset.Size))],
        capture.Assets.FirstOrDefault(asset => asset.IsPrimary && asset.Variant == "JPEG")?.Id,
        new(capture.DeliverySummary.WaitingForRouting, capture.DeliverySummary.NoRoute,
            capture.DeliverySummary.Queued, capture.DeliverySummary.Running, capture.DeliverySummary.RetryScheduled,
            capture.DeliverySummary.Confirmed, capture.DeliverySummary.Attention),
        capture.SourceId, CaptureSourceResponse.From(capture.Source, includeSourceConfiguration), capture.AssetsAvailable, capture.IsFavorite,
        retention is null ? null : CaptureRetentionSummaryResponse.From(retention),
        capture.CurrentCollection is null ? null : new(capture.CurrentCollection.Id, capture.CurrentCollection.Name, capture.CurrentCollection.Path),
        capture.ReleasedCollection is null ? null : new(capture.ReleasedCollection.Id, capture.ReleasedCollection.Name, capture.ReleasedCollection.Path));
}

public static class CaptureReadEndpoints
{
    public static RouteGroupBuilder MapCaptureReadEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/tags", GetTagsAsync).WithSummary("Suggest existing Capture tags")
            .Produces<TagSuggestionsResponse>().ProducesValidationProblem();
        RouteGroupBuilder captures = management.MapGroup("/captures");
        captures.MapGet("", GetInboxAsync).WithSummary("Browse accepted Captures")
            .Produces<CaptureInboxResponse>().ProducesValidationProblem();
        captures.MapGet("/{id:guid}", GetAsync).WithSummary("Read a Capture and its delivery history")
            .Produces<CaptureDetailResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        return management;
    }

    private static async Task<IResult> GetInboxAsync(int? limit, string? cursor, Guid? startAt, Guid? sourceId, int? rating,
        int? ratingMaximum, string? selection, string? tag, string? label, string[]? labels, bool? released, bool? held,
        bool? attention, Guid[]? sourceIds, Guid[]? collectionIds, string[]? workflows, bool? oldestFirst, bool? favoritesOnly, CaptureReadService reads,
        ICaptureRetentionReadStore retention,
        ShutterSpool.Application.Security.WorkspaceAccessContext access,
        CancellationToken cancellationToken)
    {
        int pageSize = limit ?? 30;
        if (pageSize is < 1 or > 100) return Invalid("limit", "Provide a page size from 1 to 100.");
        if (cursor is not null && startAt.HasValue) return Invalid("startAt", "Use startAt only on the first Capture page.");
        CaptureSelection? selected = null;
        if (selection is not null)
        {
            if (!Enum.TryParse(selection, out CaptureSelection parsed) || !Enum.IsDefined(parsed)
                || !string.Equals(selection, parsed.ToString(), StringComparison.Ordinal))
                return Invalid("selection", "Provide Neutral, Picked or Rejected.");
            selected = parsed;
        }
        bool hasLabels = labels is { Length: > 0 };
        if (label is not null && hasLabels) return Invalid("labels", "Use either label or labels, not both.");
        string[]? selectedLabels = (hasLabels ? labels : label is null ? null : [label])?.Distinct(StringComparer.Ordinal).Order().ToArray();
        if (sourceId.HasValue && sourceIds is { Length: > 0 }) return Invalid("sourceIds", "Use either sourceId or sourceIds, not both.");
        if (sourceIds is { Length: > 30 }) return Invalid("sourceIds", "Provide at most 30 sources.");
        if (collectionIds is { Length: > 30 }) return Invalid("collectionIds", "Provide at most 30 Collections.");
        if (workflows is { Length: > 4 }) return Invalid("workflows", "Provide at most four workflow choices.");
        if (workflows is { Length: > 0 } && (released.HasValue || held.HasValue || attention.HasValue))
            return Invalid("workflows", "Use either workflows or the legacy released, held and attention filters.");
        CaptureWorkflowFilter[] selectedWorkflows = [];
        if (workflows is { Length: > 0 })
        {
            selectedWorkflows = new CaptureWorkflowFilter[workflows.Length];
            for (int index = 0; index < workflows.Length; index++)
            {
                if (!Enum.TryParse(workflows[index], ignoreCase: false, out selectedWorkflows[index])
                    || !Enum.IsDefined(selectedWorkflows[index])
                    || !string.Equals(workflows[index], selectedWorkflows[index].ToString(), StringComparison.Ordinal))
                    return Invalid("workflows", "Provide Waiting, Held, Released or Attention.");
            }
        }
        Guid[]? selectedSources = sourceIds is { Length: > 0 } ? [.. sourceIds.Distinct().Order()] : null;
        Guid[]? selectedCollections = collectionIds is { Length: > 0 } ? [.. collectionIds.Distinct().Order()] : null;
        selectedWorkflows = [.. selectedWorkflows.Distinct().Order()];
        var filter = new CaptureReadFilter(sourceId, rating, selected, tag, selectedLabels, released, held, attention,
            ratingMaximum, selectedSources, selectedWorkflows, oldestFirst ?? false, favoritesOnly ?? false,
            selectedCollections, startAt);
        DateTimeOffset before = default;
        Guid beforeId = default;
        if (cursor is not null && !TryDecodeCursor(cursor, FilterKey(filter), out before, out beforeId))
            return Invalid("cursor", "The Capture page cursor is invalid.");
        DateTimeOffset? boundary = cursor is null ? null : before;
        Guid? boundaryId = cursor is null ? null : beforeId;
        CaptureInboxPage page;
        try { page = await reads.GetInboxAsync(pageSize, boundary, boundaryId, filter, cancellationToken); }
        catch (ArgumentException exception) { return Invalid("filter", exception.Message); }
        string? next = page.NextBefore is DateTimeOffset time && page.NextBeforeId is Guid id
            ? EncodeCursor(time, id, FilterKey(filter)) : null;
        DateTimeOffset asOf = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<Guid, CaptureRetentionSummary> summaries = await retention.GetCaptureSummariesAsync(
            page.Items.Select(capture => capture.Id).ToArray(), asOf, cancellationToken);
        return Results.Ok(new CaptureInboxResponse([.. page.Items.Select(item => CaptureInboxItemResponse.From(item, summaries.GetValueOrDefault(item.Id), access.IsAdministrator))], next,
            page.AnySourceUsesCuration, page.AnyCaptureNeedsCuration));
    }

    private static async Task<IResult> GetTagsAsync(string? q, CaptureReadService reads, CancellationToken token)
    {
        try { return Results.Ok(new TagSuggestionsResponse(await reads.GetTagsAsync(q ?? string.Empty, token))); }
        catch (ArgumentException exception) { return Invalid("q", exception.Message); }
    }

    private static async Task<IResult> GetAsync(Guid id, CaptureReadService reads, ICaptureRetentionReadStore retention,
        ShutterSpool.Application.Security.WorkspaceAccessContext access, CancellationToken cancellationToken)
    {
        CaptureRead? capture = await reads.GetAsync(id, cancellationToken);
        if (capture is null) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Capture not found");
        CaptureRetentionSummary? summary = await retention.GetCaptureSummaryAsync(id, DateTimeOffset.UtcNow, cancellationToken);
        return Results.Ok(CaptureDetailResponse.From(capture, summary, access.IsAdministrator));
    }

    private static IResult Invalid(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });

    private static string FilterKey(CaptureReadFilter filter) =>
        $"GET:/management/captures|first-accepted-{(filter.OldestFirst ? "asc,id-asc" : "desc,id-desc")}|{filter.SourceId:N}|{string.Join(',', filter.SourceIds ?? [])}|{string.Join(',', filter.CollectionIds ?? [])}|{string.Join(',', filter.Workflows ?? [])}|{filter.Rating}|{filter.RatingMaximum}|{filter.Selection}|{filter.Tag}|{string.Join(',', filter.Labels ?? [])}|{filter.Released}|{filter.Held}|{filter.Attention}|{filter.FavoritesOnly}";

    private static string EncodeCursor(DateTimeOffset before, Guid id, string filterKey) =>
        WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes($"{before.UtcDateTime.Ticks}:{id:N}:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(filterKey)))}"));

    private static bool TryDecodeCursor(string value, string filterKey, out DateTimeOffset before, out Guid id)
    {
        before = default;
        id = default;
        if (value.Length is 0 or > 512) return false;
        try
        {
            string decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value));
            string[] parts = decoded.Split(':', 3);
            if (parts.Length != 3
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
                || ticks < DateTimeOffset.MinValue.UtcDateTime.Ticks || ticks > DateTimeOffset.MaxValue.UtcDateTime.Ticks
                || !Guid.TryParseExact(parts[1], "N", out id)
                || !string.Equals(parts[2], Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(filterKey))), StringComparison.Ordinal)) return false;
            before = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException) { return false; }
    }
}
