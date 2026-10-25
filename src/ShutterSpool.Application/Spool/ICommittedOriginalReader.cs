namespace ShutterSpool.Application.Spool;

public interface ICommittedOriginalReader
{
    Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(Guid assetId, string spoolKey, IOriginalAssetLockLease sharedLease,
        CancellationToken cancellationToken) => OpenReadAsync(spoolKey, cancellationToken);
}
