using Microsoft.Extensions.Configuration;
using ShutterSpool.Infrastructure.Workspaces;

namespace ShutterSpool.Infrastructure.Composition;

public static class WorkspaceOperator
{
    public sealed class RefusedException(string message) : InvalidOperationException(message);

    public static Task RunAsync(string[] args, IConfiguration configuration, CancellationToken token) =>
        new WorkspaceMaintenance(configuration).RunAsync(args, token);
}
