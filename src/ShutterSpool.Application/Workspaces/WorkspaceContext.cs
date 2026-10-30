namespace ShutterSpool.Application.Workspaces;

public sealed record WorkspaceDefinition(Guid Id, string Name, string StorageIdentity, string StorageRoot,
    long AllocatedBytes, string ConnectionReference);
public sealed record WorkspaceIngressRoute(WorkspaceDefinition Workspace, Guid SourceId);

public static class WorkspaceIngressHandle
{
    public static string Username(string username) => "u:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(username)));
    public static string Login(Guid login) => $"l:{login:N}";
}

// Bound once before resolving workflow services; child scopes must explicitly inherit this identity.
public sealed class WorkspaceContext(bool enabled)
{
    public bool Enabled { get; } = enabled;
    public WorkspaceDefinition? Selected { get; private set; }
    public WorkspaceDefinition Required => Selected ?? throw new InvalidOperationException("A verified workspace context is required.");
    public Guid? Id => Enabled ? Required.Id : null;

    public void Bind(WorkspaceDefinition workspace)
    {
        if (!Enabled || Selected is not null || workspace.Id == Guid.Empty)
            throw new InvalidOperationException("The workspace context cannot be rebound.");
        Selected = workspace;
    }
}

public interface IWorkspaceRegistry
{
    Task<WorkspaceDefinition[]> ListReadyAsync(CancellationToken cancellationToken);
    Task<WorkspaceDefinition?> FindReadyAsync(Guid id, CancellationToken cancellationToken);
    Task<WorkspaceIngressRoute?> FindIngressAsync(string handle, CancellationToken cancellationToken);
    Task RegisterIngressAsync(string handle, Guid sourceId, CancellationToken cancellationToken);
}
