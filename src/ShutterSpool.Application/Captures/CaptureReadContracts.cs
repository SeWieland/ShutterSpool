using ShutterSpool.Application.Curation;
using ShutterSpool.Domain.Captures;

namespace ShutterSpool.Application.Captures;

public sealed record CaptureAssetMetadataRead(string MetadataOutcome, string? Reason, string? DetectedMediaType,
    string? Role, DateTime? CaptureTime, TimeSpan? CaptureTimeOffset, int? CaptureTimeFractionalDigits,
    string? CaptureTimeTag, bool CaptureTimeUncertain, string? CameraMake, string? CameraMakeTag,
    string? CameraModel, string? CameraModelTag, string? CameraSerial, string? CameraSerialTag,
    bool ExtensionMismatch, string MetadataState);
public sealed record CaptureAssetRead(Guid Id, string OriginalName, string Variant, bool IsPrimary,
    Guid? RelatedAssetId, DateTimeOffset IngestedAt, CaptureAssetMetadataRead Metadata, long Size = 0);
public sealed record CaptureSourceRead(Guid Id, string Name, bool Enabled, string CurationMode, int? DelaySeconds,
    Guid CollectionId, string CollectionName);
public sealed record CaptureCollectionRead(Guid Id, string Name, string Path);

public sealed record CaptureDeliverySummary(int WaitingForRouting, int NoRoute, int Queued, int Running,
    int RetryScheduled, int Confirmed, int Attention);

public sealed record CaptureRead(Guid Id, string DisplayName, DateTimeOffset AcceptedAt,
    Guid SourceId, CaptureSourceRead Source, CaptureCurationState Curation, CaptureAssetRead[] Assets,
    int AssetsAvailable, bool AssetsTruncated, CaptureDeliverySummary DeliverySummary, bool IsFavorite = false,
    CaptureCollectionRead? CurrentCollection = null, CaptureCollectionRead? ReleasedCollection = null);

public sealed record CaptureInboxPage(CaptureRead[] Items, DateTimeOffset? NextBefore, Guid? NextBeforeId,
    bool AnySourceUsesCuration, bool AnyCaptureNeedsCuration);
public enum CaptureWorkflowFilter { Waiting, Held, Released, Attention }

public sealed record CaptureReadFilter(Guid? SourceId = null, int? Rating = null, CaptureSelection? Selection = null,
    string? Tag = null, string[]? Labels = null, bool? Released = null, bool? Held = null,
    bool? Attention = null, int? RatingMaximum = null, Guid[]? SourceIds = null,
    CaptureWorkflowFilter[]? Workflows = null, bool OldestFirst = false, bool FavoritesOnly = false,
    Guid[]? CollectionIds = null, Guid? StartAtCaptureId = null);

public interface ICaptureReadStore
{
    Task<CaptureInboxPage> GetInboxAsync(int limit, DateTimeOffset? before, Guid? beforeId, CaptureReadFilter filter,
        CancellationToken cancellationToken);
    Task<CaptureRead?> GetAsync(Guid captureId, CancellationToken cancellationToken);
    Task<string[]> GetTagsAsync(string query, CancellationToken cancellationToken);
}

public sealed class CaptureReadService(ICaptureReadStore store)
{
    public Task<CaptureInboxPage> GetInboxAsync(int limit, DateTimeOffset? before, Guid? beforeId,
        CaptureReadFilter filter, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100 || filter.Rating is < -1 or > 5 || filter.RatingMaximum is < -1 or > 5
            || filter.RatingMaximum is int maximum && filter.Rating is int minimum && maximum < minimum
            || filter.Tag is { Length: 0 or > 64 }
            || filter.Labels is { Length: > 6 }
            || filter.SourceIds is { Length: > 30 }
            || filter.CollectionIds is { Length: > 30 }
            || filter.Workflows is { Length: > 4 }
            || filter.Workflows is not null && filter.Workflows.Any(workflow => !Enum.IsDefined(workflow))
            || filter.Labels is not null && filter.Labels.Any(label => label != "None" && !CaptureCurationPolicy.IsCanonicalLabel(label)))
            throw new ArgumentException("The Capture page filter is invalid.");
        return store.GetInboxAsync(limit, before, beforeId, filter, cancellationToken);
    }

    public Task<CaptureRead?> GetAsync(Guid captureId, CancellationToken cancellationToken) =>
        store.GetAsync(captureId, cancellationToken);

    public Task<string[]> GetTagsAsync(string query, CancellationToken cancellationToken)
    {
        if (query.Length > 64) throw new ArgumentException("The tag query is too long.", nameof(query));
        return string.IsNullOrWhiteSpace(query) ? Task.FromResult(Array.Empty<string>()) : store.GetTagsAsync(query, cancellationToken);
    }
}
