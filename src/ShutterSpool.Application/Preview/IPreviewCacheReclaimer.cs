namespace ShutterSpool.Application.Preview;

public sealed record PreviewCacheReclaimResult(long RemovedBytes, int RemovedEntries, bool MoreMayRemain,
    string? FailureCode = null);

/// <summary>Reclaims disposable previews up to the byte target, allowing one bounded whole-preview overshoot.</summary>
public interface IPreviewCacheReclaimer
{
    Task<PreviewCacheReclaimResult> ReclaimAsync(long maximumBytes, int maximumEntries,
        CancellationToken cancellationToken);
}
