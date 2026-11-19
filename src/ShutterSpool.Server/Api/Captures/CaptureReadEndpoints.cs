using ShutterSpool.Server.Security;
using Microsoft.AspNetCore.Mvc;
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
    private readonly record struct CaptureInboxQuery
    {
        [FromQuery(Name = "limit")] public int? Limit { get; init; }
        [FromQuery(Name = "cursor")] public string? Cursor { get; init; }
        [FromQuery(Name = "startAt")] public Guid? StartAt { get; init; }
        [FromQuery(Name = "sourceId")] public Guid? SourceId { get; init; }
        [FromQuery(Name = "rating")] public int? Rating { get; init; }
        [FromQuery(Name = "ratingMaximum")] public int? RatingMaximum { get; init; }
        [FromQuery(Name = "selection")] public string? Selection { get; init; }
        [FromQuery(Name = "tag")] public string? Tag { get; init; }
        [FromQuery(Name = "label")] public string? Label { get; init; }
        [FromQuery(Name = "labels")] public string[]? Labels { get; init; }
        [FromQuery(Name = "released")] public bool? Released { get; init; }
        [FromQuery(Name = "held")] public bool? Held { get; init; }
        [FromQuery(Name = "attention")] public bool? Attention { get; init; }
        [FromQuery(Name = "sourceIds")] public Guid[]? SourceIds { get; init; }
        [FromQuery(Name = "collectionIds")] public Guid[]? CollectionIds { get; init; }
        [FromQuery(Name = "workflows")] public string[]? Workflows { get; init; }
        [FromQuery(Name = "oldestFirst")] public bool? OldestFirst { get; init; }
        [FromQuery(Name = "favoritesOnly")] public bool? FavoritesOnly { get; init; }
    }

    public static RouteGroupBuilder MapCaptureReadEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/tags", GetTagsAsync).RequireAuthorization(WorkspacePolicies.Member).WithSummary("Suggest existing Capture tags")
            .Produces<TagSuggestionsResponse>().ProducesValidationProblem();
        RouteGroupBuilder captures = management.MapGroup("/captures");
        captures.MapGet("", GetInboxAsync).RequireAuthorization(WorkspacePolicies.Member).WithSummary("Browse accepted Captures")
            .Produces<CaptureInboxResponse>().ProducesValidationProblem();
        captures.MapGet("/{id:guid}", GetAsync).RequireAuthorization(WorkspacePolicies.Member).WithSummary("Read a Capture and its delivery history")
            .Produces<CaptureDetailResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        return management;
    }

    private static async Task<IResult> GetInboxAsync([AsParameters] CaptureInboxQuery query, CaptureReadService reads,
        ICaptureRetentionReadStore retention,
        ShutterSpool.Application.Security.WorkspaceAccessContext access,
        CancellationToken cancellationToken)
    {
        int pageSize = query.Limit ?? 30;
        if (pageSize is < 1 or > 100) return Invalid("limit", "Provide a page size from 1 to 100.");
        if (query.Cursor is not null && query.StartAt.HasValue) return Invalid("startAt", "Use startAt only on the first Capture page.");
        IResult? error = ParseCurationChoices(query.Selection, query.Label, query.Labels, out CaptureSelection? selected, out string[]? selectedLabels);
        if (error is not null) return error;
        if (query.SourceId.HasValue && query.SourceIds is { Length: > 0 }) return Invalid("sourceIds", "Use either sourceId or sourceIds, not both.");
        if (query.SourceIds is { Length: > 30 }) return Invalid("sourceIds", "Provide at most 30 sources.");
        if (query.CollectionIds is { Length: > 30 }) return Invalid("collectionIds", "Provide at most 30 Collections.");
        error = ParseWorkflows(query.Workflows, query.Released.HasValue || query.Held.HasValue || query.Attention.HasValue, out var selectedWorkflows);
        if (error is not null) return error;
        Guid[]? selectedSources = query.SourceIds is { Length: > 0 } ? [.. query.SourceIds.Distinct().Order()] : null;
        Guid[]? selectedCollections = query.CollectionIds is { Length: > 0 } ? [.. query.CollectionIds.Distinct().Order()] : null;
        var filter = new CaptureReadFilter(query.SourceId, query.Rating, selected, query.Tag, selectedLabels, query.Released,
            query.Held, query.Attention, query.RatingMaximum, selectedSources, selectedWorkflows, query.OldestFirst ?? false,
            query.FavoritesOnly ?? false, selectedCollections, query.StartAt);
        return await ReadPageAsync(pageSize, query.Cursor, filter, reads, retention, access, cancellationToken);
    }

    private static IResult? ParseCurationChoices(string? selection, string? label, string[]? labels,
        out CaptureSelection? selected, out string[]? selectedLabels)
    {
        selected = null;
        selectedLabels = null;
        if (selection is not null)
        {
            if (!TryParseNamedValue(selection, out CaptureSelection parsed))
                return Invalid("selection", "Provide Neutral, Picked or Rejected.");
            selected = parsed;
        }

        if (labels is { Length: > 0 })
        {
            if (label is not null) return Invalid("labels", "Use either label or labels, not both.");
            selectedLabels = [.. labels.Distinct(StringComparer.Ordinal).Order()];
        }
        else if (label is not null) selectedLabels = [label];
        return null;
    }

    private static IResult? ParseWorkflows(string[]? workflows, bool hasLegacyFilters, out CaptureWorkflowFilter[] selected)
    {
        selected = [];
        if (workflows is not { Length: > 0 }) return null;
        if (workflows.Length > 4) return Invalid("workflows", "Provide at most four workflow choices.");
        if (hasLegacyFilters)
            return Invalid("workflows", "Use either workflows or the legacy released, held and attention filters.");

        var parsed = new CaptureWorkflowFilter[workflows.Length];
        for (int index = 0; index < workflows.Length; index++)
        {
            if (!TryParseNamedValue(workflows[index], out parsed[index]))
                return Invalid("workflows", "Provide Waiting, Held, Released or Attention.");
        }
        selected = [.. parsed.Distinct().Order()];
        return null;
    }

    private static bool TryParseNamedValue<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: false, out parsed) && Enum.IsDefined(parsed)
        && string.Equals(value, parsed.ToString(), StringComparison.Ordinal);

    private static async Task<IResult> ReadPageAsync(int pageSize, string? cursor, CaptureReadFilter filter,
        CaptureReadService reads, ICaptureRetentionReadStore retention,
        ShutterSpool.Application.Security.WorkspaceAccessContext access, CancellationToken cancellationToken)
    {
        DateTimeOffset? boundary = null;
        Guid? boundaryId = null;
        if (cursor is not null)
        {
            if (!TryDecodeCursor(cursor, FilterKey(filter), out DateTimeOffset before, out Guid beforeId))
                return Invalid("cursor", "The Capture page cursor is invalid.");
            (boundary, boundaryId) = (before, beforeId);
        }
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
        id = Guid.Empty;
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
