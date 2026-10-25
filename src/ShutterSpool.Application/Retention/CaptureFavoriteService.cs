namespace ShutterSpool.Application.Retention;

public sealed record CaptureFavoriteState(Guid CaptureId, int Revision, bool IsFavorite, string Disposition);
public sealed record CaptureFavoriteMutation(bool Succeeded, CaptureFavoriteState? State, string? ErrorCode = null);

public interface ICaptureFavoriteStore
{
    Task<CaptureFavoriteState?> GetAsync(Guid captureId, CancellationToken cancellationToken);
    Task<CaptureFavoriteMutation> SetAsync(Guid captureId, int expectedRevision, bool isFavorite,
        CancellationToken cancellationToken);
}

public sealed class CaptureFavoriteService(ICaptureFavoriteStore store)
{
    public Task<CaptureFavoriteState?> GetAsync(Guid captureId, CancellationToken cancellationToken) =>
        store.GetAsync(captureId, cancellationToken);

    public Task<CaptureFavoriteMutation> SetAsync(Guid captureId, int expectedRevision, bool isFavorite,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || expectedRevision < 0)
            throw new ArgumentException("A Capture ID and nonnegative revision are required.");
        return store.SetAsync(captureId, expectedRevision, isFavorite, cancellationToken);
    }
}
