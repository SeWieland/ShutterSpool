namespace ShutterSpool.Application.Spool;

public interface IOriginalAssetLock
{
    Task<IOriginalAssetLockLease> AcquireSharedAsync(Guid assetId, string storageKey, CancellationToken cancellationToken);
    Task<IOriginalAssetLockLease> AcquireExclusiveAsync(Guid assetId, string storageKey, CancellationToken cancellationToken);
    Task<bool> IsAvailableAsync(IOriginalAssetLockLease sharedLease, CancellationToken cancellationToken);
}

public interface IOriginalAssetLockLease : IDisposable, IAsyncDisposable
{
    Guid AssetId { get; }
    string StorageKey { get; }
}
