using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.Infrastructure.Destinations.Local;

// Manages the fixed local root and mount marker; connection tests do not mutate delivery work.
internal sealed class LocalDestinationConfigurationService(ShutterSpoolDbContext database) : ILocalDestinationConfiguration
{
    public Task<LocalDirectoryListingResult> BrowseAsync(string? path, CancellationToken cancellationToken) =>
        Task.FromResult(LocalDirectoryBrowser.Browse(path, cancellationToken));

    public Task<string?> GetRootAsync(Guid destinationId, CancellationToken cancellationToken) =>
        database.LocalDestinations.AsNoTracking().Where(item => item.DestinationId == destinationId)
            .Select(item => item.RootPath).SingleOrDefaultAsync(cancellationToken);

    public async Task<string?> ConfigureAsync(Guid destinationId, string root, CancellationToken cancellationToken)
        => await ConfigureCoreAsync(destinationId, null, root, cancellationToken);

    public Task<string?> ConfigureAsync(Guid destinationId, long expectedRevision, string root, CancellationToken cancellationToken) =>
        ConfigureCoreAsync(destinationId, expectedRevision, root, cancellationToken);

    private async Task<string?> ConfigureCoreAsync(Guid destinationId, long? expectedRevision, string root,
        CancellationToken cancellationToken)
    {
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
        if (!await database.Destinations.AsNoTracking().AnyAsync(item => item.Id == destinationId && item.Kind == "local" && !item.IsArchived, cancellationToken))
            return "LOCAL_DESTINATION_NOT_FOUND";
        await database.Database.OpenConnectionAsync(cancellationToken);
        bool routingLockHeld = false;
        try
        {
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
            routingLockHeld = true;
            string? current = await GetRootAsync(destinationId, cancellationToken);
            long revision = await database.Destinations.AsNoTracking().Where(item => item.Id == destinationId)
                .Select(item => item.ConfigurationRevision).SingleAsync(cancellationToken);
            if (expectedRevision is long expected && expected != revision) return "CONFIGURATION_CONFLICT";
            if (expectedRevision is null && current is not null && PathEquals(current, normalized))
                return LocalFileDelivery.TestRoot(normalized, destinationId).ErrorCode;
            if (current is not null && !PathEquals(current, normalized)
                && await database.Deliveries.AnyAsync(item => item.DestinationId == destinationId, cancellationToken))
                return "ROOT_LOCKED";

            // Keep filesystem work outside the PostgreSQL transaction while the shared routing lock fences writers.
            try { LocalFileDelivery.ProvisionRoot(normalized, destinationId); }
            catch (UnauthorizedAccessException) { return "PERMISSION_DENIED"; }
            catch (InvalidDataException) { return "UNSAFE_PATH"; }
            catch (PlatformNotSupportedException) { return "UNSUPPORTED_PUBLICATION"; }
            catch (IOException) { return "ROOT_UNAVAILABLE"; }

            await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
            DestinationEntity? destination = await database.Destinations.FromSqlInterpolated($"SELECT * FROM destinations WHERE \"Id\" = {destinationId} AND \"Kind\" = 'local' AND NOT \"IsArchived\" FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (destination is null) return "LOCAL_DESTINATION_NOT_FOUND";
            if (expectedRevision is long requestedRevision && destination.ConfigurationRevision != requestedRevision)
                return "CONFIGURATION_CONFLICT";
            LocalDestinationEntity? config = await database.LocalDestinations
                .SingleOrDefaultAsync(item => item.DestinationId == destinationId, cancellationToken);
            if (config?.RootPath is { } settled && PathEquals(settled, normalized))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
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

    public async Task<(bool Writable, string? ErrorCode)> TestAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        string? root = await GetRootAsync(destinationId, cancellationToken);
        return root is null ? (false, "LOCAL_ROOT_NOT_CONFIGURED") : LocalFileDelivery.TestRoot(root, destinationId);
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
        if (config.IsArchived) return (false, "DESTINATION_ARCHIVED", config.ConfigurationRevision);
        if (config.RootPath is null) return (false, "LOCAL_ROOT_NOT_CONFIGURED", config.ConfigurationRevision);
        (bool writable, string? error) = LocalFileDelivery.TestRoot(config.RootPath, destinationId);
        return (writable, error, config.ConfigurationRevision);
    }

    private static bool PathEquals(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
