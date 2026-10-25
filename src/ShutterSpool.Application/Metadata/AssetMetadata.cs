namespace ShutterSpool.Application.Metadata;

public enum MetadataOutcome { Complete, Partial, Unsupported, Failed }

public sealed record EmbeddedValue(string Value, string Tag);

public sealed record CaptureTime(DateTime Local, TimeSpan? Offset, int FractionalDigits, string Tag, bool IsUncertain);

public sealed record AssetMetadata(
    MetadataOutcome Outcome,
    string? DetectedMediaType,
    string? Role,
    CaptureTime? CaptureTime,
    EmbeddedValue? CameraMake,
    EmbeddedValue? CameraModel,
    EmbeddedValue? CameraSerial,
    bool ExtensionMismatch,
    string? Reason);

public sealed record MetadataWork(Guid AssetId, string SpoolKey, string OriginalName, long Size, Guid LeaseId);

public interface IMediaMetadataParser
{
    Task<AssetMetadata> ParseAsync(Stream original, string originalName, long size, CancellationToken cancellationToken);
}

public interface IAssetMetadataStore
{
    Task<MetadataWork?> ClaimAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> RenewAsync(MetadataWork work, DateTimeOffset now, CancellationToken cancellationToken);
    Task CompleteAsync(MetadataWork work, AssetMetadata metadata, CancellationToken cancellationToken);
    Task RetryAsync(MetadataWork work, DateTimeOffset now, CancellationToken cancellationToken);
}
