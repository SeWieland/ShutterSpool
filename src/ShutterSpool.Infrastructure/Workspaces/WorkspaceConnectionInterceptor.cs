using Microsoft.EntityFrameworkCore.Diagnostics;
using ShutterSpool.Application.Workspaces;
using System.Data.Common;

namespace ShutterSpool.Infrastructure.Workspaces;

// Verify each logical connection once before EF can query tenant state, including after credential/config rotation.
internal sealed class WorkspaceConnectionInterceptor(WorkspaceDefinition workspace) : DbConnectionInterceptor
{
    private readonly Lock gate = new();
    private readonly HashSet<DbConnection> verified = new(ReferenceEqualityComparer.Instance);

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        lock (gate) if (verified.Contains(connection)) return;
        try
        {
            using DbCommand command = Command(connection);
            using DbDataReader reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetGuid(0) != workspace.Id || reader.GetString(1) != workspace.StorageIdentity || reader.Read())
                throw new InvalidOperationException("The database connection does not match its selected workspace.");
            lock (gate) verified.Add(connection);
        }
        catch { connection.Close(); throw; }
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        lock (gate) if (verified.Contains(connection)) return;
        try
        {
            await using DbCommand command = Command(connection);
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != workspace.Id
                || reader.GetString(1) != workspace.StorageIdentity || await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("The database connection does not match its selected workspace.");
            lock (gate) verified.Add(connection);
        }
        catch { await connection.CloseAsync(); throw; }
    }

    private static DbCommand Command(DbConnection connection)
    {
        DbCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,storage_identity FROM workspace_identity";
        command.CommandTimeout = 3;
        return command;
    }
}
