using Microsoft.Extensions.Configuration;
using System.Collections.Concurrent;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class WorkspaceSpoolResources
{
    private readonly ConcurrentDictionary<Guid, (string Root, FileSystemSpool Spool)> spools = new();
    public FileSystemSpool Get(Guid id, FileSystemSpoolOptions options)
    {
        string root = Path.GetFullPath(options.RootPath);
        var resource = spools.GetOrAdd(id, _ => (root, new FileSystemSpool(options)));
        if (!string.Equals(resource.Root, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Changing a workspace spool root requires stopped-writer maintenance and restart.");
        return resource.Spool;
    }
}
