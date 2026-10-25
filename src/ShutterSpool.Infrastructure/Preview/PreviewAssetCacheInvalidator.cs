using ShutterSpool.Application.Preview;

namespace ShutterSpool.Infrastructure.Preview;

internal sealed class PreviewAssetCacheInvalidator(IPreviewAssetCacheInvalidator activeCache,
    IPreviewAssetCacheInvalidator diskCache) : IPreviewAssetCacheInvalidator
{
    public Task RemoveAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        Task active = RemoveAsync(activeCache, assetId, cancellationToken);
        Task disk = ReferenceEquals(activeCache, diskCache)
            ? Task.CompletedTask
            : RemoveAsync(diskCache, assetId, cancellationToken);
        return Task.WhenAll(active, disk);
    }

    private static Task RemoveAsync(IPreviewAssetCacheInvalidator cache, Guid assetId,
        CancellationToken cancellationToken)
    {
        try { return cache.RemoveAssetAsync(assetId, cancellationToken); }
        catch (Exception error) { return Task.FromException(error); }
    }
}
