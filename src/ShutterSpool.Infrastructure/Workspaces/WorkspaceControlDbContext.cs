using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Npgsql;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Workspaces;

internal sealed class WorkspaceControlDbContext(DbContextOptions<WorkspaceControlDbContext> options) : DbContext(options)
{
    internal static string RuntimeConnection(IConfiguration configuration) => new NpgsqlConnectionStringBuilder(
        configuration.GetConnectionString("shutterspool-control") ?? throw new WorkspaceOperator.RefusedException("The workspace control connection is required."))
        { MinPoolSize = 0, MaxPoolSize = 4, Timeout = 3, CommandTimeout = 3, IncludeErrorDetail = false }.ConnectionString;

    internal static WorkspaceControlDbContext Create(string connection) =>
        new(new DbContextOptionsBuilder<WorkspaceControlDbContext>().UseNpgsql(connection).Options);

    internal DbSet<WorkspaceRecord> Workspaces => Set<WorkspaceRecord>();
    internal DbSet<IngressRouteRecord> IngressRoutes => Set<IngressRouteRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkspaceRecord>(entity =>
        {
            entity.ToTable("workspaces");
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasIndex(item => item.StorageIdentity).IsUnique();
            entity.HasIndex(item => item.StorageRoot).IsUnique();
            entity.HasIndex(item => item.RuntimeTarget).IsUnique();
            entity.HasIndex(item => item.OperationId).IsUnique();
            entity.Property(item => item.Name).HasMaxLength(256);
        });
        modelBuilder.Entity<IngressRouteRecord>(entity =>
        {
            entity.ToTable("ingress_routes");
            entity.HasKey(item => item.Handle);
            entity.Property(item => item.Handle).HasMaxLength(128);
            entity.HasOne<WorkspaceRecord>().WithMany().HasForeignKey(item => item.WorkspaceId);
        });
    }
}

internal sealed class WorkspaceRecord
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string StorageIdentity { get; set; } = "";
    public string StorageRoot { get; set; } = "";
    public long AllocatedBytes { get; set; }
    public string RuntimeTarget { get; set; } = "";
    public Guid OperationId { get; set; }
    public string Provisioning { get; set; } = "";
    public bool Ready { get; set; }
    internal WorkspaceDefinition Definition => new(Id, Name, StorageIdentity, StorageRoot, AllocatedBytes);
}

internal sealed class IngressRouteRecord
{
    public string Handle { get; set; } = "";
    public Guid WorkspaceId { get; set; }
    public Guid SourceId { get; set; }
}

internal sealed class WorkspaceControlDbContextFactory : IDesignTimeDbContextFactory<WorkspaceControlDbContext>
{
    public WorkspaceControlDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<WorkspaceControlDbContext>()
        .UseNpgsql("Host=localhost;Database=shutterspool-control").Options);
}

// Published once after startup validation; process-lifetime mappings cannot follow a changed control row.
internal sealed class WorkspaceInventory
{
    internal WorkspaceDefinition[]? Ready { get; private set; }
    internal void Freeze(WorkspaceDefinition[] ready) => Ready = ready;
}
