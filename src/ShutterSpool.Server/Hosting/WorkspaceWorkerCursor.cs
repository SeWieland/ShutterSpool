using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Server.Hosting;

// One cursor per work type; only active scopes hold providers or database connections.
internal sealed class WorkspaceWorkerCursor(IServiceScopeFactory scopes)
{
    private readonly Lock gate = new();
    private readonly HashSet<Guid> active = [];
    private long next;

    public async Task<WorkspaceWorkerScope> NextAsync(CancellationToken token)
    {
        AsyncServiceScope scope = scopes.CreateAsyncScope();
        Guid? selected = null;
        try
        {
            WorkspaceContext context = scope.ServiceProvider.GetRequiredService<WorkspaceContext>();
            if (context.Enabled)
            {
                WorkspaceDefinition[] workspaces = await scope.ServiceProvider.GetRequiredService<IWorkspaceRegistry>().ListReadyAsync(token);
                lock (gate)
                {
                    for (int attempt = 0; attempt < workspaces.Length; attempt++)
                    {
                        WorkspaceDefinition workspace = workspaces[(int)((ulong)next++ % (uint)workspaces.Length)];
                        if (!active.Add(workspace.Id)) continue;
                        selected = workspace.Id;
                        context.Bind(workspace);
                        break;
                    }
                }
                if (selected is null) throw new InvalidOperationException("No workspace worker slot is available.");
            }
            return new(scope, () => { if (selected is Guid id) lock (gate) active.Remove(id); });
        }
        catch
        {
            if (selected is Guid id) lock (gate) active.Remove(id);
            await scope.DisposeAsync();
            throw;
        }
    }
}

internal sealed class WorkspaceWorkerScope(AsyncServiceScope scope, Action release) : IAsyncDisposable
{
    public IServiceProvider ServiceProvider => scope.ServiceProvider;
    public async ValueTask DisposeAsync()
    {
        try { await scope.DisposeAsync(); }
        finally { release(); }
    }
}
