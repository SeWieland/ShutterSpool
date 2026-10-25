using Microsoft.Extensions.Caching.Hybrid;
using ShutterSpool.Application.Sources;

namespace ShutterSpool.Server.Hosting;

public sealed class SourceDisplayCacheKeys
{
    // shortcut: cache the first 256 identities per process; add bounded eviction when larger installations need it.
    private const int Capacity = 256;
    private readonly HashSet<Guid> identities = [];
    private readonly string prefix = $"source-display:v1:{Guid.NewGuid():N}:";

    public string? Admit(Guid id)
    {
        lock (identities)
        {
            if (!identities.Contains(id) && identities.Count >= Capacity) return null;
            identities.Add(id);
            return Key(id);
        }
    }

    public string Key(Guid id) => $"{prefix}{id:N}";
}

public sealed class SourceDisplayCache(ISourceDisplayStore store, HybridCache cache,
    SourceDisplayCacheKeys keys, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private static readonly HybridCacheEntryOptions Options = new()
    {
        Expiration = Lifetime,
        LocalCacheExpiration = Lifetime,
        Flags = HybridCacheEntryFlags.DisableDistributedCache,
    };

    public async Task<SourceDisplay?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        string? key = keys.Admit(id);
        if (key is null) return await store.ReadAsync(id, cancellationToken);
        bool factoryFailed = false;
        try
        {
            CachedDisplay entry = await cache.GetOrCreateAsync(key, async token =>
            {
                long started = clock.GetTimestamp();
                SourceDisplay? value;
                try { value = await store.ReadAsync(id, token); }
                catch { factoryFailed = true; throw; }
                if (value is not null && value.Name.Length > 256)
                    throw new OversizedDisplay(value);
                return new CachedDisplay(value, started);
            }, Options, cancellationToken: cancellationToken);
            // A slow or racing fill cannot restart the display staleness clock.
            if (clock.GetElapsedTime(entry.Started) < Lifetime) return entry.Value;
            await InvalidateAsync(id, cancellationToken);
        }
        catch (OversizedDisplay oversized) { return oversized.Value; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) when (!factoryFailed) { }
        return await store.ReadAsync(id, cancellationToken);
    }

    public async Task InvalidateAsync(Guid id, CancellationToken cancellationToken)
    {
        try { await cache.RemoveAsync(keys.Key(id), cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { /* Absolute expiry bounds a lost display invalidation. */ }
    }

    private sealed record CachedDisplay(SourceDisplay? Value, long Started);
    private sealed class OversizedDisplay(SourceDisplay value) : Exception
    {
        public SourceDisplay Value { get; } = value;
    }
}
