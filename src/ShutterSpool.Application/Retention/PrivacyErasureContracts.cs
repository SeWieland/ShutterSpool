namespace ShutterSpool.Application.Retention;

public enum PrivacyErasureStatus { Pending, Completed, Blocked, Conflict, NotFound }
public enum PrivacyErasureSurfaceStatus { Pending, Completed, Blocked }

public sealed record PrivacyErasureSelection(Guid CaptureId, int ExpectedRevision, IReadOnlyList<Guid> AssetIds);
public sealed record PrivacyErasureRequest(Guid OperationId, IReadOnlyList<PrivacyErasureSelection> Selections,
    bool Confirmed);
public sealed record PrivacyErasureAssetWork(Guid CaptureId, Guid AssetId, string SpoolKey, long ExpectedSize,
    string StorageMarker, IReadOnlyList<string> KnownTransientKeys);
public sealed record PrivacyErasureAssetSurface(Guid CaptureId, Guid AssetId, PrivacyErasureSurfaceStatus MetadataRedaction,
    PrivacyErasureSurfaceStatus Derivatives, PrivacyErasureSurfaceStatus OriginalsAndTransients,
    string? ErrorCode);
public sealed record PrivacyErasureResult(Guid OperationId, PrivacyErasureStatus Status, string? ErrorCode,
    IReadOnlyList<PrivacyErasureAssetSurface> Assets);
public sealed record PrivacyErasurePreview(PrivacyErasureStatus Status, string? ErrorCode, int CaptureCount,
    int AssetCount, int GalleryPromiseCount, int PreservationHoldCount, int ActiveDeliveryCount,
    int AmbiguousDeliveryCount);
public sealed record PrivacyErasureOperationSummary(Guid OperationId, PrivacyErasureStatus Status,
    DateTimeOffset RequestedAt, int AssetCount, int CompletedCount, int PendingCount, string? ErrorCode);
public sealed record PrivacyErasureOperationPage(IReadOnlyList<PrivacyErasureOperationSummary> Operations,
    string? NextCursor);
public sealed record PrivacyErasureBeginResult(PrivacyErasureStatus Status, string? ErrorCode,
    IReadOnlyList<PrivacyErasureAssetWork> Work);
public sealed record PrivacyErasureSuppressionRecord(Guid OperationId, Guid CaptureId, Guid AssetId,
    string SpoolKey, long ExpectedSize, IReadOnlyList<string> KnownTransientKeys, string StorageMarker,
    DateTimeOffset ErasedAt, bool Completed);
public sealed record PrivacyErasureSuppressionManifest(int Version, Guid SnapshotId, DateTimeOffset ExportedAt,
    int RecordCount, int PageSize, int PageCount, string ManifestHash);
public sealed record PrivacyErasureSuppressionPage(Guid SnapshotId, int PageIndex,
    IReadOnlyList<PrivacyErasureSuppressionRecord> Records);
public sealed record PrivacyErasureProtectedManifest(Guid SnapshotId, int RecordCount, int PageSize,
    int PageCount, string Payload);
public sealed record PrivacyErasureProtectedPage(Guid SnapshotId, int PageIndex, string Payload);
public sealed record PrivacyErasureImportStatus(Guid SnapshotId, int ExpectedPageCount, int ReceivedPageCount,
    bool Ready, string? ErrorCode);

public interface IPrivacyErasureEvidenceCodec
{
    string ProtectManifest(PrivacyErasureSuppressionManifest manifest);
    bool TryReadManifest(string payload, out PrivacyErasureSuppressionManifest? manifest);
    string ProtectPage(PrivacyErasureSuppressionPage page);
    bool TryReadPage(string payload, out PrivacyErasureSuppressionPage? page);
}

public interface IPrivacyErasureStore
{
    Task<PrivacyErasurePreview> PreviewAsync(IReadOnlyList<PrivacyErasureSelection> selections,
        CancellationToken cancellationToken);
    Task<PrivacyErasureBeginResult> BeginAsync(PrivacyErasureRequest request, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PrivacyErasureAssetWork>> GetPendingAsync(Guid operationId, int maximumCount,
        CancellationToken cancellationToken);
    Task<bool> MarkRemovingAsync(Guid operationId, Guid assetId, CancellationToken cancellationToken);
    Task CompleteSurfaceAsync(Guid operationId, Guid assetId, string surface, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task FailSurfaceAsync(Guid operationId, Guid assetId, string surface, string errorCode,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<PrivacyErasureResult> GetResultAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> GetPendingOperationIdsAsync(Guid? afterOperationId, int maximumCount,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<PrivacyErasureOperationSummary>> ListAsync(int maximumCount, bool pendingOnly,
        DateTimeOffset? beforeRequestedAt, Guid? beforeOperationId, CancellationToken cancellationToken);
    Task<PrivacyErasureSuppressionManifest> CreateSuppressionExportAsync(CancellationToken cancellationToken);
    Task<PrivacyErasureSuppressionManifest?> GetSuppressionExportAsync(Guid snapshotId,
        CancellationToken cancellationToken);
    Task<PrivacyErasureSuppressionPage?> GetSuppressionExportPageAsync(Guid snapshotId, int pageIndex,
        CancellationToken cancellationToken);
    Task<PrivacyErasureImportStatus> BeginSuppressionReconciliationAsync(
        PrivacyErasureSuppressionManifest manifest, Guid expectedSnapshotId, CancellationToken cancellationToken);
    Task<PrivacyErasureImportStatus> StageSuppressionPageAsync(PrivacyErasureSuppressionPage page,
        CancellationToken cancellationToken);
    Task<PrivacyErasureImportStatus> CompleteSuppressionReconciliationAsync(Guid snapshotId,
        CancellationToken cancellationToken);
}
