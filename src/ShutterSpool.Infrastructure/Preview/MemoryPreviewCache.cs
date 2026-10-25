using ShutterSpool.Application.Preview;

namespace ShutterSpool.Infrastructure.Preview;

internal sealed class MemoryPreviewCache : IPreviewCache, IPreviewAssetCacheInvalidator
{
    private const int MaximumEntryBytes = 4 * 1024 * 1024;
    private readonly Lock _lock = new();
    private readonly long _budget;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _leastRecentlyUsed = [];
    private long _retainedBytes;

    public MemoryPreviewCache(PreviewSettings settings) => _budget = settings.MemoryCacheBytes;

    public Task<byte[]?> ReadAsync(string key, PreviewSize size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node)) return Task.FromResult<byte[]?>(null);
            if (!PreviewMediaValidation.IsWebP(node.Value.Content, size)) return Task.FromResult<byte[]?>(null);
            _leastRecentlyUsed.Remove(node);
            _leastRecentlyUsed.AddFirst(node);
            return Task.FromResult<byte[]?>([.. node.Value.Content]);
        }
    }

    public Task<bool> ContainsAsync(string key, PreviewSize size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node)
                || !PreviewMediaValidation.IsWebP(node.Value.Content, size)) return Task.FromResult(false);
            _leastRecentlyUsed.Remove(node);
            _leastRecentlyUsed.AddFirst(node);
            return Task.FromResult(true);
        }
    }

    public Task<bool> WriteAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (content.Length is <= 0 or > MaximumEntryBytes) throw new InvalidDataException("Invalid preview output size.");
        lock (_lock)
        {
            Remove(key);
            if (content.Length > _budget) return Task.FromResult(false);

            var entry = new Entry(key, AssetIdFromKey(key), [.. content]);
            LinkedListNode<Entry> node = _leastRecentlyUsed.AddFirst(entry);
            _entries.Add(key, node);
            _retainedBytes += content.Length;
            while (_retainedBytes > _budget)
            {
                LinkedListNode<Entry> last = _leastRecentlyUsed.Last!;
                Remove(last.Value.Key);
            }
        }
        return Task.FromResult(true);
    }

    public Task RemoveAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            foreach (string key in _entries.Values.Where(node => node.Value.AssetId == assetId).Select(node => node.Value.Key).ToArray())
                Remove(key);
        }
        return Task.CompletedTask;
    }

    private void Remove(string key)
    {
        if (!_entries.Remove(key, out LinkedListNode<Entry>? node)) return;
        _leastRecentlyUsed.Remove(node);
        _retainedBytes -= node.Value.Content.Length;
    }

    private static Guid? AssetIdFromKey(string key)
    {
        string[] segments = key.Split('/');
        return segments.Length > 1 && Guid.TryParseExact(segments[1], "N", out Guid assetId) ? assetId : null;
    }

    private sealed record Entry(string Key, Guid? AssetId, byte[] Content);
}
