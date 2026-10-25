using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class ShutterSpoolDbContextFactory : IDesignTimeDbContextFactory<ShutterSpoolDbContext>
{
    public ShutterSpoolDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ShutterSpoolDbContext>();
        builder.UseNpgsql("Host=localhost;Database=shutterspool;Username=postgres;Password=design-time-only");
        return new ShutterSpoolDbContext(builder.Options);
    }
}
