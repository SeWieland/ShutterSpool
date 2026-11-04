using ShutterSpool.Infrastructure.Composition;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Workspaces;

internal sealed class PostgresWorkspaceRegistry(IConfiguration configuration, WorkspaceContext context, WorkspaceInventory? inventory = null) : IWorkspaceRegistry
{
    internal const int MaximumWorkspaces = 32;
    internal static string ControlConnection(IConfiguration configuration) => new NpgsqlConnectionStringBuilder(
        configuration.GetConnectionString("shutterspool-control") ?? throw new WorkspaceOperator.RefusedException("The workspace control connection is required."))
        { MinPoolSize = 0, MaxPoolSize = 4, Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false }.ConnectionString;
    internal static WorkspaceControlDbContext Database(string connection) => new(new DbContextOptionsBuilder<WorkspaceControlDbContext>().UseNpgsql(connection).Options);

    public async Task<WorkspaceDefinition[]> ListReadyAsync(CancellationToken cancellationToken)
    {
        if (inventory?.Ready is { } frozen) return frozen;
        await using var database = Database(ControlConnection(configuration));
        string[] applied = [.. await database.Database.GetAppliedMigrationsAsync(cancellationToken)];
        if (!applied.SequenceEqual(database.Database.GetMigrations(), StringComparer.Ordinal))
            throw new WorkspaceOperator.RefusedException("The control schema is incompatible; run explicit maintenance.");
        WorkspaceRecord[] records = await database.Workspaces.AsNoTracking().Where(item => item.Ready)
            .OrderBy(item => item.Name).Take(MaximumWorkspaces + 1).ToArrayAsync(cancellationToken);
        if (records.Length > MaximumWorkspaces) throw new WorkspaceOperator.RefusedException("The workspace registry exceeds the supported cohort limit.");
        return [.. records.Select(item => item.Definition with { StorageRoot = WorkspaceManifest.Read(configuration, item.Name).StorageRoot })];
    }

    public async Task<WorkspaceDefinition?> FindReadyAsync(Guid id, CancellationToken cancellationToken) =>
        (await ListReadyAsync(cancellationToken)).SingleOrDefault(item => item.Id == id);

    public async Task<WorkspaceIngressRoute?> FindIngressAsync(string handle, CancellationToken cancellationToken)
    {
        if (handle.Length is < 1 or > 128) return null;
        await using var database = Database(ControlConnection(configuration));
        IngressRouteRecord? route = await database.IngressRoutes.AsNoTracking().SingleOrDefaultAsync(item => item.Handle == handle, cancellationToken);
        if (route is null) return null;
        WorkspaceDefinition? workspace = await FindReadyAsync(route.WorkspaceId, cancellationToken);
        return workspace is null ? null : new(workspace, route.SourceId);
    }

    public async Task RegisterIngressAsync(string handle, Guid sourceId, CancellationToken cancellationToken)
    {
        WorkspaceDefinition workspace = context.Required;
        await using var database = Database(ControlConnection(configuration));
        var route = new IngressRouteRecord { Handle = handle, WorkspaceId = workspace.Id, SourceId = sourceId };
        database.IngressRoutes.Add(route);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            IngressRouteRecord existing = await database.IngressRoutes.AsNoTracking().SingleAsync(item => item.Handle == handle, cancellationToken);
            if (existing.WorkspaceId == workspace.Id && existing.SourceId == sourceId) return;
            throw handle.StartsWith("u:", StringComparison.Ordinal)
                ? new ShutterSpool.Application.Sources.SourceUsernameConflictException()
                : new WorkspaceOperator.RefusedException("The ingress routing handle is unavailable.");
        }
    }
}
