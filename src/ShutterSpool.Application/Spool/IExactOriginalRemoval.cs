namespace ShutterSpool.Application.Spool;

public sealed record ExactOriginalRemovalRequest(Guid AssetId, string SpoolKey, long ExpectedSize,
    string StorageMarker, IReadOnlyList<string> KnownTransientKeys);

public enum ExactOriginalRemovalStatus
{
    Removed,
    AlreadyAbsent,
    Blocked,
    Failed,
}

public sealed record ExactOriginalRemovalResult(ExactOriginalRemovalStatus Status, long RemovedBytes,
    string? FailureCode = null);

/// <summary>Removes only exact objects named by a durable Asset-removal authorization.</summary>
public interface IExactOriginalRemoval
{
    Task<ExactOriginalRemovalResult> RemoveAsync(ExactOriginalRemovalRequest request,
        CancellationToken cancellationToken);
}
