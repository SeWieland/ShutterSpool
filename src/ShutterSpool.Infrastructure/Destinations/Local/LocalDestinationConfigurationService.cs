using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.Infrastructure.Destinations.Local;

// Manages the fixed local root and mount marker; connection tests do not mutate delivery work.
internal sealed class LocalDestinationConfigurationService(ShutterSpoolDbContext database,
    DestinationCapabilities? capabilities = null, WorkspaceContext? workspace = null) : ILocalDestinationConfiguration
{
    public Task<LocalDirectoryListingResult> BrowseAsync(string? path, CancellationToken cancellationToken) =>
        Task.FromResult(!(capabilities ?? DestinationCapabilities.All).IsEnabled("local")
            ? new LocalDirectoryListingResult(null, "Local destinations are unavailable in this deployment.")
            : LocalDirectoryBrowser.Browse(path, LocalDestinationPaths.AssignedRoot(workspace), cancellationToken));

    public async Task<string?> ConfigureAsync(Guid destinationId, long expectedRevision, string root,
        CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled("local")) return "DESTINATION_KIND_DISABLED";
        root = LocalDirectoryBrowser.NormalizePath(root);
        if (string.IsNullOrWhiteSpace(root) || root.Length > 2_048)
            return "INVALID_ROOT";
        string normalized;
        try
        {
            if (!Path.IsPathFullyQualified(root)) return "INVALID_ROOT";
            normalized = Path.GetFullPath(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return "INVALID_ROOT"; }
        if (!LocalDestinationPaths.IsAllowed(normalized, workspace)) return "UNSAFE_PATH";
        await database.Database.OpenConnectionAsync(cancellationToken);
        bool routingLockHeld = false;
        try
        {
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
            routingLockHeld = true;
            var current = await (from saved in database.Destinations.AsNoTracking()
                where saved.Id == destinationId && saved.Kind == "local" && !saved.IsArchived
                join rootConfig in database.LocalDestinations.AsNoTracking() on saved.Id equals rootConfig.DestinationId into roots
                from rootConfig in roots.DefaultIfEmpty()
                select new { saved.ConfigurationRevision, RootPath = rootConfig == null ? null : rootConfig.RootPath })
                .SingleOrDefaultAsync(cancellationToken);
            if (current is null) return "LOCAL_DESTINATION_NOT_FOUND";
            if (expectedRevision != current.ConfigurationRevision) return "CONFIGURATION_CONFLICT";
            bool rootUnchanged = current.RootPath is not null && PathEquals(current.RootPath, normalized);
            // Keep filesystem work outside the PostgreSQL transaction while the shared routing lock fences writers.
            if (rootUnchanged)
            {
                string? error = LocalFileDelivery.TestRoot(normalized, destinationId).ErrorCode;
                if (error is not null) return error;
            }
            else
            {
                if (current.RootPath is not null
                    && await database.Deliveries.AnyAsync(item => item.DestinationId == destinationId, cancellationToken))
                    return "ROOT_LOCKED";
                try { LocalFileDelivery.ProvisionRoot(normalized, destinationId); }
                catch (UnauthorizedAccessException) { return "PERMISSION_DENIED"; }
                catch (InvalidDataException) { return "UNSAFE_PATH"; }
                catch (PlatformNotSupportedException) { return "UNSUPPORTED_PUBLICATION"; }
                catch (IOException) { return "ROOT_UNAVAILABLE"; }
            }

            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            // EF may reopen a broken connection here; fence the final write on the transaction's connection.
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
            DestinationEntity? destination = await database.Destinations.FromSqlInterpolated($"SELECT * FROM destinations WHERE \"Id\" = {destinationId} AND \"Kind\" = 'local' AND NOT \"IsArchived\" FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (destination is null) return "LOCAL_DESTINATION_NOT_FOUND";
            if (destination.ConfigurationRevision != expectedRevision)
                return "CONFIGURATION_CONFLICT";
            if (rootUnchanged)
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            LocalDestinationEntity? config = await database.LocalDestinations
                .SingleOrDefaultAsync(item => item.DestinationId == destinationId, cancellationToken);
            if (config is null)
                database.LocalDestinations.Add(new LocalDestinationEntity { DestinationId = destinationId, RootPath = normalized });
            else config.RootPath = normalized;
            destination.ConfigurationRevision++;
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        finally
        {
            if (routingLockHeld)
                await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(hashtextextended('shutterspool-routing', 0))", CancellationToken.None);
            await database.Database.CloseConnectionAsync();
        }
    }

    public async Task<(bool Writable, string? ErrorCode, long TestedConfigurationRevision)> TestWithRevisionAsync(
        Guid destinationId, CancellationToken token)
    {
        var config = await (from destination in database.Destinations.AsNoTracking()
            where destination.Id == destinationId && destination.Kind == "local"
            join root in database.LocalDestinations.AsNoTracking() on destination.Id equals root.DestinationId into roots
            from root in roots.DefaultIfEmpty()
            select new { destination.ConfigurationRevision, destination.IsArchived, RootPath = root == null ? null : root.RootPath })
            .SingleOrDefaultAsync(token);
        if (config is null) return (false, "LOCAL_DESTINATION_NOT_FOUND", 0);
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled("local")) return (false, "DESTINATION_KIND_DISABLED", config.ConfigurationRevision);
        if (config.IsArchived) return (false, "DESTINATION_ARCHIVED", config.ConfigurationRevision);
        if (config.RootPath is null) return (false, "LOCAL_ROOT_NOT_CONFIGURED", config.ConfigurationRevision);
        if (!LocalDestinationPaths.IsAllowed(config.RootPath, workspace)) return (false, "UNSAFE_PATH", config.ConfigurationRevision);
        (bool writable, string? error) = LocalFileDelivery.TestRoot(config.RootPath, destinationId);
        return (writable, error, config.ConfigurationRevision);
    }

    private static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
