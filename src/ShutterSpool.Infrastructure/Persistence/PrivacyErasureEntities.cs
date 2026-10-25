namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class PrivacyErasureOperationEntity
{
    public Guid Id { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string State { get; set; }
    public string? ErrorCode { get; set; }
}

internal sealed class PrivacyErasureAssetEntity
{
    public Guid AssetId { get; init; }
    public Guid OperationId { get; init; }
    public Guid CaptureId { get; init; }
    public required string SpoolKey { get; init; }
    public long ExpectedSize { get; init; }
    public string[] KnownTransientKeys { get; set; } = [];
    public required string StorageMarker { get; init; }
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? MetadataRedactedAt { get; set; }
    public DateTimeOffset? DerivativesRemovedAt { get; set; }
    public DateTimeOffset? OriginalsRemovedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public required string State { get; set; }
    public string? ErrorCode { get; set; }
    public string? DerivativesErrorCode { get; set; }
    public string? OriginalsErrorCode { get; set; }
}

internal sealed class PrivacyErasureExportEntity
{
    public Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int RecordCount { get; init; }
    public int PageSize { get; init; }
    public int PageCount { get; init; }
    public string ManifestHash { get; set; } = string.Empty;
}

internal sealed class PrivacyErasureExportAssetEntity
{
    public Guid SnapshotId { get; init; }
    public int PageIndex { get; init; }
    public int Ordinal { get; init; }
    public Guid OperationId { get; init; }
    public Guid CaptureId { get; init; }
    public Guid AssetId { get; init; }
    public required string SpoolKey { get; init; }
    public long ExpectedSize { get; init; }
    public string[] KnownTransientKeys { get; init; } = [];
    public required string StorageMarker { get; init; }
    public DateTimeOffset ErasedAt { get; init; }
    public bool Completed { get; init; }
}

internal sealed class PrivacyErasureImportEntity
{
    public Guid SnapshotId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int RecordCount { get; init; }
    public int PageSize { get; init; }
    public int PageCount { get; init; }
    public required string ManifestHash { get; init; }
    public int ReceivedPageCount { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal sealed class PrivacyErasureImportAssetEntity
{
    public Guid SnapshotId { get; init; }
    public int PageIndex { get; init; }
    public int Ordinal { get; init; }
    public Guid OperationId { get; init; }
    public Guid CaptureId { get; init; }
    public Guid AssetId { get; init; }
    public required string SpoolKey { get; init; }
    public long ExpectedSize { get; init; }
    public string[] KnownTransientKeys { get; init; } = [];
    public required string StorageMarker { get; init; }
    public DateTimeOffset ErasedAt { get; init; }
    public bool Completed { get; init; }
}
