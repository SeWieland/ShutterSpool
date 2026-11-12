using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Composition;

namespace ShutterSpool.Infrastructure.Workspaces;

internal static class WorkspaceInventoryValidator
{
    internal static async Task ValidateAsync(IConfiguration configuration, WorkspaceDefinition[] workspaces, CancellationToken token)
    {
        long usable = configuration.GetValue<long>("ShutterSpool:Workspaces:UsableCapacityBytes");
        if (usable <= 0 || workspaces.Aggregate(0L, (sum, item) => checked(sum + item.AllocatedBytes)) > usable)
            throw new WorkspaceOperator.RefusedException("Workspace allocations exceed the operator's usable storage budget.");
        await using (var control = new Npgsql.NpgsqlConnection(WorkspaceControlDbContext.RuntimeConnection(configuration)))
        {
            await control.OpenAsync(token);
            await using var privileges = new Npgsql.NpgsqlCommand("""
                SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls
                    OR EXISTS(SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid)
                    OR has_schema_privilege(current_user,'public','CREATE')
                    OR has_table_privilege(current_user,'workspaces','INSERT,UPDATE,DELETE')
                    OR EXISTS(SELECT 1 FROM pg_database WHERE datname=current_database() AND pg_get_userbyid(datdba)=current_user)
                FROM pg_roles WHERE rolname=current_user
                """, control);
            if ((bool)(await privileges.ExecuteScalarAsync(token))!)
                throw new WorkspaceOperator.RefusedException("Control runtime credentials must be restricted to registry reads and opaque ingress routing writes.");
            foreach (WorkspaceDefinition workspace in workspaces)
            {
                string name = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString($"Workspaces:{workspace.Name}:Runtime")).Database!;
                await using var denied = new Npgsql.NpgsqlCommand("SELECT has_database_privilege(current_user,@database,'CONNECT')", control);
                denied.Parameters.AddWithValue("database", name);
                if ((bool)(await denied.ExecuteScalarAsync(token))!) throw new WorkspaceOperator.RefusedException("Control runtime credentials can connect to a tenant database.");
            }
        }
        for (int index = 0; index < workspaces.Length; index++)
        {
            WorkspaceDefinition workspace = workspaces[index];
            WorkspaceManifest manifest = WorkspaceManifest.Read(configuration, workspace.Name);
            if (manifest.AllocatedBytes != workspace.AllocatedBytes)
                throw new WorkspaceOperator.RefusedException("The canonical workspace manifest differs from its provisioned allocation or storage root.");
            WorkspaceRuntime.ValidateRoot(workspace);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.StorageRoot));
            foreach (WorkspaceDefinition other in workspaces.Where(item => item.Id != workspace.Id))
            {
                string otherRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other.StorageRoot));
                if (root.Equals(otherRoot, StringComparison.OrdinalIgnoreCase) || root.StartsWith(otherRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new WorkspaceOperator.RefusedException("Workspace storage allocations cannot overlap.");
            }
            var own = new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString($"Workspaces:{workspace.Name}:Runtime"));
            var controlTarget = new Npgsql.NpgsqlConnectionStringBuilder(WorkspaceControlDbContext.RuntimeConnection(configuration));
            if (!string.Equals(own.Host, controlTarget.Host, StringComparison.OrdinalIgnoreCase) || own.Port != controlTarget.Port)
                throw new WorkspaceOperator.RefusedException("The supported workspace topology uses one explicitly selected PostgreSQL server.");
            await using var connection = new Npgsql.NpgsqlConnection(own.ConnectionString);
            await connection.OpenAsync(token);
            foreach (string database in workspaces.Where(item => item.Id != workspace.Id)
                .Select(item => new Npgsql.NpgsqlConnectionStringBuilder(configuration.GetConnectionString($"Workspaces:{item.Name}:Runtime")).Database!)
                .Append(new Npgsql.NpgsqlConnectionStringBuilder(WorkspaceControlDbContext.RuntimeConnection(configuration)).Database!))
            {
                await using var command = new Npgsql.NpgsqlCommand("SELECT has_database_privilege(current_user,@database,'CONNECT')", connection);
                command.Parameters.AddWithValue("database", database);
                if ((bool)(await command.ExecuteScalarAsync(token))!)
                    throw new WorkspaceOperator.RefusedException("Workspace runtime credentials can connect to another workspace or the control database.");
            }
        }
    }
}
