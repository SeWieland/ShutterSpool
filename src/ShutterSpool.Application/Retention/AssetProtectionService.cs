
namespace ShutterSpool.Application.Retention;

public sealed class AssetProtectionService(IAssetProtectionStore store, IProtectionAdmissionLock admissionLock,
    IRetentionSettingsStore retentionSettings, TimeProvider clock)
{
    public const int MaximumAssetCount = 1_000;

    public Task<GalleryProtection?> GetGalleryAsync(Guid id, CancellationToken cancellationToken) =>
        store.GetGalleryAsync(id, cancellationToken);

    public Task<PreservationHold?> GetPreservationAsync(Guid id, CancellationToken cancellationToken) =>
        store.GetPreservationAsync(id, cancellationToken);

    public async Task<ProtectionOperation<GalleryProtection>> CreateGalleryAsync(Guid[] assetIds,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ValidateAssetIds(assetIds);
        DateTimeOffset now = clock.GetUtcNow();
        ValidateGalleryExpiry(expiresAt, now,
            (await retentionSettings.GetAsync(cancellationToken)).Settings.GalleryMaximum);
        ProtectionOriginal[]? members = await store.GetAvailableOriginalsAsync(assetIds, cancellationToken);
        if (members is null) return new(ProtectionOperationStatus.Blocked, ErrorCode: "ORIGINAL_UNAVAILABLE");
        await using IProtectionAdmissionLease lease = await admissionLock.AcquireSharedAsync(members, cancellationToken);
        if (!await lease.AreAvailableAsync(cancellationToken))
            return new(ProtectionOperationStatus.Blocked, ErrorCode: "ORIGINAL_UNAVAILABLE");
        return await store.CreateGalleryAsync(members, expiresAt, now, cancellationToken);
    }

    public async Task<ProtectionOperation<GalleryProtection>> ExtendGalleryAsync(Guid id, int expectedRevision,
        DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ValidateVersionedId(id, expectedRevision);
        DateTimeOffset now = clock.GetUtcNow();
        ValidateGalleryExpiry(expiresAt, now,
            (await retentionSettings.GetAsync(cancellationToken)).Settings.GalleryMaximum);
        ProtectionOriginal[]? members = await store.GetGalleryOriginalsAsync(id, now, cancellationToken);
        if (members is null) return new(ProtectionOperationStatus.NotFound, ErrorCode: "GALLERY_PROTECTION_NOT_FOUND");
        if (members.Length == 0) return new(ProtectionOperationStatus.Blocked, ErrorCode: "ORIGINAL_UNAVAILABLE");
        await using IProtectionAdmissionLease lease = await admissionLock.AcquireSharedAsync(members, cancellationToken);
        if (!await lease.AreAvailableAsync(cancellationToken))
            return new(ProtectionOperationStatus.Blocked, ErrorCode: "ORIGINAL_UNAVAILABLE");
        return await store.ExtendGalleryAsync(id, expectedRevision, members, expiresAt, now, cancellationToken);
    }

    public Task<ProtectionOperation<GalleryProtection>> RevokeGalleryAccessAsync(Guid id, int expectedRevision,
        CancellationToken cancellationToken)
    {
        ValidateVersionedId(id, expectedRevision);
        return store.RevokeGalleryAccessAsync(id, expectedRevision, clock.GetUtcNow(), cancellationToken);
    }

    public async Task<ProtectionOperation<PreservationHold>> CreatePreservationAsync(Guid[] assetIds,
        string authority, string reason, DateTimeOffset reviewAt, CancellationToken cancellationToken)
    {
        ValidateAssetIds(assetIds);
        ValidateText(authority, nameof(authority), 512);
        ValidateText(reason, nameof(reason), 2_048);
        DateTimeOffset now = clock.GetUtcNow();
        if (reviewAt <= now) throw new ArgumentOutOfRangeException(nameof(reviewAt), "Review time must be in the future.");
        ProtectionOriginal[]? members = await store.GetAvailableOriginalsAsync(assetIds, cancellationToken);
        if (members is null) return new(ProtectionOperationStatus.Blocked, ErrorCode: "ORIGINAL_UNAVAILABLE");
        await using IProtectionAdmissionLease lease = await admissionLock.AcquireSharedAsync(members, cancellationToken);
        if (!await lease.AreAvailableAsync(cancellationToken))
            return new(ProtectionOperationStatus.Blocked, ErrorCode: "ORIGINAL_UNAVAILABLE");
        return await store.CreatePreservationAsync(members, authority.Trim(), reason.Trim(), reviewAt, now,
            cancellationToken);
    }

    public Task<ProtectionOperation<PreservationHold>> ResolvePreservationAsync(Guid id, int expectedRevision,
        string resolvedBy, string resolution, CancellationToken cancellationToken)
    {
        ValidateVersionedId(id, expectedRevision);
        ValidateText(resolvedBy, nameof(resolvedBy), 512);
        ValidateText(resolution, nameof(resolution), 2_048);
        return store.ResolvePreservationAsync(id, expectedRevision, resolvedBy.Trim(), resolution.Trim(),
            clock.GetUtcNow(), cancellationToken);
    }

    private static void ValidateGalleryExpiry(DateTimeOffset expiresAt, DateTimeOffset now, TimeSpan maximum)
    {
        if (expiresAt <= now || expiresAt > now + maximum)
            throw new ArgumentOutOfRangeException(nameof(expiresAt),
                $"Expiry must be later than now and no more than {maximum} ahead.");
    }

    private static void ValidateAssetIds(Guid[] assetIds)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        if (assetIds.Length is < 1 or > MaximumAssetCount || assetIds.Any(id => id == Guid.Empty)
            || assetIds.Distinct().Count() != assetIds.Length)
            throw new ArgumentException($"Provide 1 to {MaximumAssetCount} distinct Asset IDs.", nameof(assetIds));
    }

    private static void ValidateVersionedId(Guid id, int expectedRevision)
    {
        if (id == Guid.Empty || expectedRevision < 0)
            throw new ArgumentException("A valid protection ID and revision are required.");
    }

    private static void ValidateText(string value, string name, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Trim().Length > maximumLength) throw new ArgumentOutOfRangeException(name);
    }
}
