using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Routing;
using ShutterSpool.Application.Destinations;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class DestinationRoutingAdminService(ShutterSpoolDbContext database, DestinationCapabilities? capabilities = null) : IDestinationRoutingStore
{
    public async Task<DestinationRouting?> GetAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        DestinationEntity? destination = await database.Destinations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == destinationId, cancellationToken);
        if (destination is null) return null;
        DestinationRoutingEntity? config = await database.DestinationRoutings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DestinationId == destinationId, cancellationToken);
        return new DestinationRouting(destinationId, destination.Enabled,
            config is null ? [] : RoutingRuleJson.ReadRules(config.RulesJson), destination.ConfigurationRevision);
    }

    public async Task SetAsync(DestinationRouting routing, CancellationToken cancellationToken)
    {
        DestinationEntity? current = await database.Destinations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == routing.DestinationId, cancellationToken);
        if (current is null) throw new InvalidOperationException("Destination does not exist.");
        _ = await SetAsync(routing, current.ConfigurationRevision, cancellationToken);
    }

    public async Task<DestinationRoutingMutation> SetAsync(DestinationRouting routing, long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
        DestinationEntity destination = await database.Destinations.FromSqlInterpolated($"SELECT * FROM destinations WHERE \"Id\" = {routing.DestinationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken) ?? throw new InvalidOperationException("Destination does not exist.");
        if (routing.Enabled) (capabilities ?? DestinationCapabilities.All).EnsureEnabled(destination.Kind);
        if (destination.IsArchived)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, await GetAsync(routing.DestinationId, cancellationToken));
        }
        if (destination.ConfigurationRevision != expectedRevision)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, await GetAsync(routing.DestinationId, cancellationToken));
        }
        DestinationRoutingEntity? config = await database.DestinationRoutings
            .SingleOrDefaultAsync(item => item.DestinationId == routing.DestinationId, cancellationToken);
        bool wasEnabled = destination.Enabled;
        string rulesJson = RoutingRuleJson.WriteRules(routing.Rules);
        destination.Enabled = routing.Enabled;
        destination.ConfigurationRevision++;
        if (config is null) database.DestinationRoutings.Add(new DestinationRoutingEntity
            { DestinationId = routing.DestinationId, RulesJson = rulesJson });
        else config.RulesJson = rulesJson;
        if (wasEnabled != routing.Enabled)
        {
            DeliveryEntity[] pending = await database.Deliveries.Where(delivery => delivery.DestinationId == routing.DestinationId
                && (routing.Enabled ? delivery.State == DeliveryState.Disabled
                    : delivery.State == DeliveryState.Pending || delivery.State == DeliveryState.RetryWait)).ToArrayAsync(cancellationToken);
            foreach (DeliveryEntity delivery in pending) delivery.State = routing.Enabled ? DeliveryState.Pending : DeliveryState.Disabled;
        }
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, new DestinationRouting(destination.Id, destination.Enabled, routing.Rules,
            destination.ConfigurationRevision));
    }
}
