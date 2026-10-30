using Microsoft.Extensions.Configuration;
using Npgsql;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Workspaces;

internal sealed class PostgresWorkspaceRegistry(IConfiguration configuration, WorkspaceContext context) : IWorkspaceRegistry
{
    internal const int MaximumWorkspaces = 32;
    internal static string ControlConnection(IConfiguration configuration) => new NpgsqlConnectionStringBuilder(
        configuration.GetConnectionString("shutterspool-control") ?? throw new InvalidOperationException("The workspace control connection is required."))
        { MinPoolSize = 0, MaxPoolSize = 4, Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false }.ConnectionString;

    public async Task<WorkspaceDefinition[]> ListReadyAsync(CancellationToken cancellationToken)
    {
        if (!context.Enabled) return [];
        await using var connection = new NpgsqlConnection(ControlConnection(configuration));
        await connection.OpenAsync(cancellationToken);
        await using (var version = new NpgsqlCommand("SELECT version FROM workspace_control_version WHERE id=1", connection))
            if (Convert.ToInt32(await version.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException("The control schema is incompatible; run explicit maintenance.");
        await using var command = new NpgsqlCommand("SELECT id, name, storage_identity, storage_root, allocated_bytes, connection_reference FROM workspaces WHERE ready ORDER BY id LIMIT 33", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<WorkspaceDefinition>();
        while (await reader.ReadAsync(cancellationToken)) items.Add(Read(reader));
        if (items.Count > MaximumWorkspaces) throw new InvalidOperationException("The workspace registry exceeds the supported cohort limit.");
        return [.. items];
    }

    public async Task<WorkspaceDefinition?> FindReadyAsync(Guid id, CancellationToken cancellationToken) =>
        (await ListReadyAsync(cancellationToken)).SingleOrDefault(item => item.Id == id);

    public async Task<WorkspaceIngressRoute?> FindIngressAsync(string handle, CancellationToken cancellationToken)
    {
        if (!context.Enabled || handle.Length is < 1 or > 128) return null;
        await using var connection = new NpgsqlConnection(ControlConnection(configuration));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT w.id, w.name, w.storage_identity, w.storage_root, w.allocated_bytes, w.connection_reference, r.source_id FROM ingress_routes r JOIN workspaces w ON w.id=r.workspace_id WHERE r.handle=@handle AND w.ready", connection);
        command.Parameters.AddWithValue("handle", handle);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new(Read(reader), reader.GetGuid(6)) : null;
    }

    public async Task RegisterIngressAsync(string handle, Guid sourceId, CancellationToken cancellationToken)
    {
        if (!context.Enabled) return;
        WorkspaceDefinition workspace = context.Required;
        await using var connection = new NpgsqlConnection(ControlConnection(configuration));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("INSERT INTO ingress_routes(handle,workspace_id,source_id) VALUES (@handle,@workspace,@source) ON CONFLICT(handle) DO UPDATE SET source_id=EXCLUDED.source_id WHERE ingress_routes.workspace_id=EXCLUDED.workspace_id AND ingress_routes.source_id=EXCLUDED.source_id", connection);
        command.Parameters.AddWithValue("handle", handle);
        command.Parameters.AddWithValue("workspace", workspace.Id);
        command.Parameters.AddWithValue("source", sourceId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw handle.StartsWith("u:", StringComparison.Ordinal)
                ? new ShutterSpool.Application.Sources.SourceUsernameConflictException()
                : new InvalidOperationException("The ingress routing handle is unavailable.");
    }

    internal static WorkspaceDefinition Read(NpgsqlDataReader reader) => new(reader.GetGuid(0), reader.GetString(1),
        reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5));
}
