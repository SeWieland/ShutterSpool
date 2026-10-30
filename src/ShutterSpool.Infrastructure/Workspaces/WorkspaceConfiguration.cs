using Microsoft.Extensions.Configuration;

namespace ShutterSpool.Infrastructure.Workspaces;

// Cache one immutable connection/root selection per operation without changing framework configuration lifetime.
internal sealed record WorkspaceConfiguration(IConfiguration Value);
