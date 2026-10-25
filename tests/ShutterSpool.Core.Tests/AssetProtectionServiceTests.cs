using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Core.Tests;

public sealed class AssetProtectionServiceTests
{
    [Fact]
    public async Task Gallery_promise_cannot_exceed_the_current_operator_maximum()
    {
        var store = new ProtectionStore();
        var settings = new ProtectionSettingsStore(new RetentionSettings { GalleryMaximum = TimeSpan.FromDays(30) });
        var service = Create(store, new AdmissionLock(true), settings);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.CreateGalleryAsync(
            [Guid.NewGuid()], DateTimeOffset.UtcNow.AddDays(31), TestContext.Current.CancellationToken));

        Assert.Equal(0, store.CreateGalleryCalls);
    }

    [Fact]
    public async Task Gallery_admission_rejects_an_original_that_the_shared_lease_cannot_validate()
    {
        var store = new ProtectionStore();
        var service = Create(store, new AdmissionLock(false), new ProtectionSettingsStore(new()));

        ProtectionOperation<GalleryProtection> result = await service.CreateGalleryAsync(
            [store.Original.AssetId], DateTimeOffset.UtcNow.AddDays(2), TestContext.Current.CancellationToken);

        Assert.Equal(ProtectionOperationStatus.Blocked, result.Status);
        Assert.Equal("ORIGINAL_UNAVAILABLE", result.ErrorCode);
        Assert.Equal(0, store.CreateGalleryCalls);
    }

    [Fact]
    public async Task Gallery_admission_rejects_duplicate_or_unbounded_asset_membership_before_io()
    {
        var store = new ProtectionStore();
        var admission = new AdmissionLock(true);
        var service = Create(store, admission, new ProtectionSettingsStore(new()));
        Guid assetId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateGalleryAsync([assetId, assetId],
            DateTimeOffset.UtcNow.AddDays(2), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateGalleryAsync(
            [.. Enumerable.Range(0, AssetProtectionService.MaximumAssetCount + 1).Select(_ => Guid.NewGuid())],
            DateTimeOffset.UtcNow.AddDays(2), TestContext.Current.CancellationToken));

        Assert.Equal(0, admission.AcquireCalls);
        Assert.Equal(0, store.CandidateCalls);
    }

    private static AssetProtectionService Create(ProtectionStore store, AdmissionLock admission,
        ProtectionSettingsStore settings) => new(store, admission, settings, TimeProvider.System);

    private sealed class ProtectionStore : IAssetProtectionStore
    {
        public ProtectionOriginal Original { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "spool/asset", 4);
        public int CandidateCalls { get; private set; }
        public int CreateGalleryCalls { get; private set; }
        public Task<GalleryProtection?> GetGalleryAsync(Guid id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PreservationHold?> GetPreservationAsync(Guid id, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ProtectionOriginal[]?> GetAvailableOriginalsAsync(Guid[] assetIds, CancellationToken cancellationToken)
        {
            CandidateCalls++;
            return Task.FromResult<ProtectionOriginal[]?>(assetIds.Contains(Original.AssetId) ? [Original] : null);
        }
        public Task<ProtectionOriginal[]?> GetGalleryOriginalsAsync(Guid galleryProtectionId,
            DateTimeOffset now, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ProtectionOperation<GalleryProtection>> CreateGalleryAsync(ProtectionOriginal[] originals,
            DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CreateGalleryCalls++;
            return Task.FromResult(new ProtectionOperation<GalleryProtection>(ProtectionOperationStatus.Committed));
        }
        public Task<ProtectionOperation<GalleryProtection>> ExtendGalleryAsync(Guid id, int expectedRevision,
            ProtectionOriginal[] originals, DateTimeOffset expiresAt, DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ProtectionOperation<GalleryProtection>> RevokeGalleryAccessAsync(Guid id, int expectedRevision,
            DateTimeOffset now, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ProtectionOperation<PreservationHold>> CreatePreservationAsync(ProtectionOriginal[] originals,
            string authority, string reason, DateTimeOffset reviewAt, DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ProtectionOperation<PreservationHold>> ResolvePreservationAsync(Guid id, int expectedRevision,
            string resolvedBy, string resolution, DateTimeOffset now,
            CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class AdmissionLock(bool available) : IProtectionAdmissionLock
    {
        public int AcquireCalls { get; private set; }
        public Task<IProtectionAdmissionLease> AcquireSharedAsync(IReadOnlyList<ProtectionOriginal> originals,
            CancellationToken cancellationToken)
        {
            AcquireCalls++;
            return Task.FromResult<IProtectionAdmissionLease>(new Lease(available));
        }
    }

    private sealed class Lease(bool available) : IProtectionAdmissionLease
    {
        public Task<bool> AreAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(available);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ProtectionSettingsStore(RetentionSettings settings) : IRetentionSettingsStore
    {
        public Task<RetentionSettingsDocument> GetAsync(CancellationToken cancellationToken) => Task.FromResult(
            new RetentionSettingsDocument(settings, new(), new(PreviewCacheMode.Memory, 64 * 1024 * 1024),
                new(PreviewCacheMode.Memory, 64 * 1024 * 1024), false));
        public Task<RetentionSettingsImpact> PreviewAsync(RetentionSettings settings, SpoolQuotaSettings quota,
            PreviewCacheSettings previewCache, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<RetentionSettingsUpdateResult> UpdateAsync(int expectedSettingsRevision, int expectedQuotaRevision,
            RetentionSettings settings, SpoolQuotaSettings quota, PreviewCacheSettings previewCache,
            bool acknowledgeImpact, CancellationToken cancellationToken) => throw new NotImplementedException();
    }
}
