using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Workspaces;

internal sealed class WorkspaceWriterLease : IDisposable
{
    private readonly Dictionary<Guid, FileStream> held = [];
    public void Acquire(WorkspaceDefinition workspace)
    {
        if (held.ContainsKey(workspace.Id)) return;
        WorkspaceRuntime.ValidateRoot(workspace);
        string path = Path.Combine(workspace.StorageRoot, ".shutterspool-writers.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The workspace writer lock contains a linked path.");
        held.Add(workspace.Id, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
    }
    public void Dispose()
    {
        foreach (FileStream lease in held.Values) lease.Dispose();
        held.Clear();
    }
}
