namespace ShutterSpool.Application.Retention;

public sealed record ProtectionOriginal(Guid AssetId, Guid CaptureId, string SpoolKey, long Size);

public sealed record GalleryProtection(Guid Id, int Revision, Guid[] AssetIds, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? AccessRevokedAt);

public sealed record PreservationHold(Guid Id, int Revision, Guid[] AssetIds, string Authority, string Reason,
    DateTimeOffset CreatedAt, DateTimeOffset ReviewAt, DateTimeOffset? ResolvedAt, string? ResolvedBy,
    string? Resolution);

public enum ProtectionOperationStatus { Committed, Conflict, NotFound, Blocked }

public sealed record ProtectionOperation<T>(ProtectionOperationStatus Status, T? Current = default,
    string? ErrorCode = null);

public interface IProtectionAdmissionLock
{
    Task<IProtectionAdmissionLease> AcquireSharedAsync(IReadOnlyList<ProtectionOriginal> originals,
        CancellationToken cancellationToken);
}

public interface IProtectionAdmissionLease : IAsyncDisposable
{
    Task<bool> AreAvailableAsync(CancellationToken cancellationToken);
}

public interface IAssetProtectionStore
{
    Task<GalleryProtection?> GetGalleryAsync(Guid id, CancellationToken cancellationToken);
    Task<PreservationHold?> GetPreservationAsync(Guid id, CancellationToken cancellationToken);
    Task<ProtectionOriginal[]?> GetAvailableOriginalsAsync(Guid[] assetIds, CancellationToken cancellationToken);
    Task<ProtectionOriginal[]?> GetGalleryOriginalsAsync(Guid galleryProtectionId, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<ProtectionOperation<GalleryProtection>> CreateGalleryAsync(ProtectionOriginal[] originals,
        DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken);
    Task<ProtectionOperation<GalleryProtection>> ExtendGalleryAsync(Guid id, int expectedRevision,
        ProtectionOriginal[] originals, DateTimeOffset expiresAt, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<ProtectionOperation<GalleryProtection>> RevokeGalleryAccessAsync(Guid id, int expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<ProtectionOperation<PreservationHold>> CreatePreservationAsync(ProtectionOriginal[] originals,
        string authority, string reason, DateTimeOffset reviewAt, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<ProtectionOperation<PreservationHold>> ResolvePreservationAsync(Guid id, int expectedRevision,
        string resolvedBy, string resolution, DateTimeOffset now, CancellationToken cancellationToken);
}
