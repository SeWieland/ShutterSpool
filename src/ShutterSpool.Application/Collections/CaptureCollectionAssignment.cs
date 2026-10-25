using ShutterSpool.Application.Captures;

namespace ShutterSpool.Application.Collections;

public enum CollectionMoveStatus { Applied, Conflict, NotFound, NotMovable, TargetMissing }
public sealed record CollectionMoveRequest(Guid CaptureId, int ExpectedRevision);
public sealed record CollectionMoveResult(Guid CaptureId, CollectionMoveStatus Status, int? CurrentRevision,
    Guid? CurrentCollectionId, CaptureRead? Current = null);

public interface ICaptureCollectionAssignmentStore
{
    Task<bool> ExistsAsync(Guid collectionId, CancellationToken token);
    Task<CollectionMoveResult> MoveAsync(Guid captureId, int expectedRevision, Guid collectionId, CancellationToken token);
}

public sealed class CaptureCollectionAssignmentService(ICaptureCollectionAssignmentStore store)
{
    public Task<bool> ExistsAsync(Guid id, CancellationToken token) => store.ExistsAsync(id, token);

    public async Task<CollectionMoveResult[]> MoveAsync(Guid targetId, IReadOnlyList<CollectionMoveRequest> items,
        CancellationToken token)
    {
        if (targetId == Guid.Empty || items.Count is < 1 or > 100
            || items.Any(item => item.CaptureId == Guid.Empty || item.ExpectedRevision < 0)
            || items.Select(item => item.CaptureId).Distinct().Count() != items.Count)
            throw new ArgumentException("Provide an existing target and 1 to 100 distinct Captures with non-negative revisions.");
        if (!await store.ExistsAsync(targetId, token))
            return [.. items.Select(item => new CollectionMoveResult(item.CaptureId, CollectionMoveStatus.TargetMissing, null, null))];
        var results = new List<CollectionMoveResult>(items.Count);
        foreach (CollectionMoveRequest item in items)
            results.Add(await store.MoveAsync(item.CaptureId, item.ExpectedRevision, targetId, token));
        return [.. results];
    }
}
