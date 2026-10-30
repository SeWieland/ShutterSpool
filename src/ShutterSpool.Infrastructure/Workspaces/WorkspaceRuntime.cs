using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Persistence;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Workspaces;

internal static class WorkspaceRuntime
{
    internal const string Marker = ".shutterspool-workspace.json";
    internal sealed record StorageMarker(Guid WorkspaceId, string StorageIdentity, Guid ProvisionOperation);

    internal static IConfiguration Configure(IConfiguration deployment, WorkspaceContext context)
    {
        if (!context.Enabled) return deployment;
        WorkspaceDefinition workspace = context.Required;
        ValidateRoot(workspace);
        string connection = deployment.GetConnectionString(workspace.ConnectionReference)
            ?? throw new InvalidOperationException("The workspace runtime secret reference is unavailable.");
        var parsed = new NpgsqlConnectionStringBuilder(connection)
        {
            MinPoolSize = 0, MaxPoolSize = 4, Timeout = 3, CommandTimeout = 30,
            ApplicationName = "ShutterSpool workspace", IncludeErrorDetail = false,
        };
        return new ConfigurationBuilder().AddConfiguration(deployment).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:shutterspool"] = parsed.ConnectionString,
            ["ShutterSpool:Spool:RootPath"] = workspace.StorageRoot,
            ["ShutterSpool:Preview:CacheRootPath"] = Path.Combine(workspace.StorageRoot, "previews"),
            ["ShutterSpool:Security:BootstrapIssuer"] = null,
            ["ShutterSpool:Security:BootstrapSubject"] = null,
            ["ShutterSpool:Workspaces:AllocationBytes"] = workspace.AllocatedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
    }

    internal static void ValidateRoot(WorkspaceDefinition workspace)
    {
        string root = Path.GetFullPath(workspace.StorageRoot);
        if (workspace.AllocatedBytes <= 0 || !Path.IsPathFullyQualified(workspace.StorageRoot) || !Directory.Exists(root))
            throw new InvalidOperationException("The assigned workspace storage is unavailable.");
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The workspace storage root contains a symbolic link or reparse point.");
        string path = Path.Combine(root, Marker);
        if (!File.Exists(path) || new FileInfo(path).Length > 4096 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The workspace storage marker is unavailable.");
        StorageMarker marker = JsonSerializer.Deserialize<StorageMarker>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("The workspace storage marker is invalid.");
        if (marker.WorkspaceId != workspace.Id || marker.StorageIdentity != workspace.StorageIdentity)
            throw new InvalidOperationException("The workspace storage identity does not match its registry entry.");
    }

    internal static async Task ValidateDatabaseAsync(ShutterSpoolDbContext database, CancellationToken token, WorkspaceDefinition? workspace = null)
    {
        string[] known = [.. database.Database.GetMigrations()];
        string[] applied = [.. await database.Database.GetAppliedMigrationsAsync(token)];
        if (!known.SequenceEqual(applied, StringComparer.Ordinal))
            throw new InvalidOperationException("The workspace schema is incompatible; run explicit stopped-writer maintenance.");
        bool privileged = await database.Database.SqlQuery<bool>($"""
            SELECT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls
                OR EXISTS (SELECT 1 FROM pg_auth_members WHERE member=pg_roles.oid)) AS "Value"
            FROM pg_roles WHERE rolname=current_user
            """).SingleAsync(token);
        if (privileged) throw new InvalidOperationException("Workspace runtime credentials must be restricted and cannot inherit other roles.");
        bool schemaOwner = await database.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname='public'
                AND pg_get_userbyid(nspowner)=current_user) AS "Value"
            """).SingleAsync(token);
        if (schemaOwner) throw new InvalidOperationException("Workspace runtime credentials cannot own the application schema.");
        bool databaseOwner = await database.Database.SqlQuery<bool>($"SELECT pg_get_userbyid(datdba)=current_user AS \"Value\" FROM pg_database WHERE datname=current_database()").SingleAsync(token);
        if (databaseOwner) throw new InvalidOperationException("Workspace runtime credentials cannot own the application database.");
        if (workspace is not null)
        {
            Guid id = await database.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM workspace_identity").SingleAsync(token);
            string storage = await database.Database.SqlQuery<string>($"SELECT storage_identity AS \"Value\" FROM workspace_identity WHERE id={id}").SingleAsync(token);
            if (id != workspace.Id || storage != workspace.StorageIdentity)
                throw new InvalidOperationException("The tenant database identity does not match its selected storage.");
        }
        bool canCreate = await database.Database.SqlQuery<bool>($"SELECT has_schema_privilege(current_user,'public','CREATE') AS \"Value\"").SingleAsync(token);
        if (canCreate) throw new InvalidOperationException("Workspace runtime credentials cannot create schema objects.");
    }

    internal static async Task RecoverTemporaryAsync(IServiceProvider services, CancellationToken token)
    {
        WorkspaceDefinition workspace = services.GetRequiredService<WorkspaceContext>().Required;
        ShutterSpoolDbContext database = services.GetRequiredService<ShutterSpoolDbContext>();
        var reservations = await database.SpoolQuotaReservations.AsNoTracking()
            .Where(item => item.AssetId == null && item.Category == "Spool")
            .OrderBy(item => item.OperationId)
            .Take(100_001).ToArrayAsync(token);
        if (reservations.Length > 100_000) throw new InvalidOperationException("Temporary media recovery reached its reservation bound.");
        var removed = new List<Guid>();
        foreach (var reservation in reservations)
        {
            string key = $"incoming/media-temporary/{reservation.OperationId:N}.tmp";
            if (reservation.SpoolKeys.Length != 1 || reservation.SpoolKeys[0] != key) continue;
            string directory = Path.Combine(workspace.StorageRoot, "incoming", "media-temporary");
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Temporary media recovery found a linked directory.");
            string path = Path.Combine(directory, $"{reservation.OperationId:N}.tmp");
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Temporary media recovery found a linked file.");
                File.Delete(path);
            }
            if (!File.Exists(path)) removed.Add(reservation.OperationId);
        }
        foreach (Guid operation in removed)
            await services.GetRequiredService<ShutterSpool.Application.Retention.ISpoolQuotaStore>().ReleaseReservationAsync(operation, token);
    }
}

// Preview quota operations create independent contexts, but keep the parent's immutable tenant selection.
internal sealed class WorkspaceBoundScopeFactory(IServiceScopeFactory scopes, WorkspaceDefinition workspace) : IServiceScopeFactory
{
    public IServiceScope CreateScope()
    {
        IServiceScope scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(workspace);
        return scope;
    }
}
