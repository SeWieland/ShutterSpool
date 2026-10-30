using System.Collections.Concurrent;
using ShutterSpool.Application.Preview;

namespace ShutterSpool.Infrastructure.Preview;

// Only cache budgets/settings and lightweight use gates persist, never a tenant service provider.
internal sealed class WorkspacePreviewResources
{
    private readonly ConcurrentDictionary<Guid, PreviewSettings> settings = new();
    private readonly ConcurrentDictionary<Guid, MemoryPreviewCache> caches = new();
    private readonly ConcurrentDictionary<Guid, PreviewAssetUseGate> uses = new();
    public PreviewSettings Settings(Guid id, Func<PreviewSettings> create) => settings.GetOrAdd(id, _ => create());
    public MemoryPreviewCache Cache(Guid id, PreviewSettings configured) => caches.GetOrAdd(id, _ => new(configured));
    public PreviewAssetUseGate Uses(Guid id) => uses.GetOrAdd(id, _ => new());
}
