using ShutterSpool.Domain.Collections;

namespace ShutterSpool.Application.Collections;

public sealed record CollectionSummary(Guid Id, string Name, string Path, long Revision, bool IsBuiltIn,
    int SourceReferences, int CaptureReferences);
public sealed record CollectionPage(CollectionSummary[] Items, string? NextCursor);
public sealed record CollectionWrite(bool Applied, CollectionSummary? Current, bool Referenced = false,
    bool NameConflict = false);

public interface ICollectionManagementStore
{
    Task<CollectionPage> ListAsync(string query, int limit, string? afterPath, Guid? afterId, CancellationToken token);
    Task<CollectionSummary?> GetAsync(Guid id, CancellationToken token);
    Task<CollectionSummary> CreateAsync(CollectionName name, CancellationToken token);
    Task<CollectionWrite> RenameAsync(Guid id, long expectedRevision, CollectionName name, CancellationToken token);
    Task<CollectionWrite> DeleteAsync(Guid id, long expectedRevision, CancellationToken token);
}

public sealed class CollectionManagementService(ICollectionManagementStore store)
{
    public Task<CollectionPage> ListAsync(string? query, int limit, string? afterPath, Guid? afterId,
        CancellationToken token)
    {
        if (limit is < 1 or > 100 || query is { Length: > 128 })
            throw new ArgumentException("Provide a search of at most 128 characters and a page size from 1 to 100.");
        return store.ListAsync(query ?? string.Empty, limit, afterPath, afterId, token);
    }

    public Task<CollectionSummary?> GetAsync(Guid id, CancellationToken token) => store.GetAsync(id, token);
    public Task<CollectionSummary> CreateAsync(string name, CancellationToken token) => store.CreateAsync(CollectionName.Parse(name), token);
    public Task<CollectionWrite> RenameAsync(Guid id, long expectedRevision, string name, CancellationToken token) =>
        store.RenameAsync(id, expectedRevision, CollectionName.Parse(name), token);
    public Task<CollectionWrite> DeleteAsync(Guid id, long expectedRevision, CancellationToken token) =>
        store.DeleteAsync(id, expectedRevision, token);
}
