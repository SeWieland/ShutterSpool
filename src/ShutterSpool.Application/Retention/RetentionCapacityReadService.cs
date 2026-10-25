namespace ShutterSpool.Application.Retention;

public sealed class RetentionCapacityReadService(IRetentionCapacityReadStore store)
{
    public Task<RetentionCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken) =>
        store.GetCapacityAsync(cancellationToken);

    public Task<RetentionHistoryPage> GetHistoryAsync(int pageSize, string? cursor, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(pageSize));
        return store.GetHistoryAsync(pageSize, cursor, cancellationToken);
    }
}
