using Microsoft.EntityFrameworkCore;

namespace ShutterSpool.Infrastructure.Persistence;

internal static class RetentionQualification
{
    public static bool IsConfirmedIntent(RoutingDecisionKindValue kind, bool hasDeliveries,
        bool allDeliveriesConfirmed, bool sameTargetConfirmed) =>
        kind == RoutingDecisionKindValue.Matched
        && (hasDeliveries ? allDeliveriesConfirmed : sameTargetConfirmed);

    public static async Task RecordNewIntentAsync(ShutterSpoolDbContext database, CaptureEntity capture,
        DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        capture.RetentionFactsDirty = true;
        capture.NoDeliveryIntent = false;
        if (!capture.IsFavorite || capture.FavoriteQualificationLostAt is not null) return;
        var intents = await database.RoutingDecisions.AsNoTracking()
            .Where(decision => decision.CaptureId == capture.Id
                && (decision.Kind == RoutingDecisionKindValue.Matched
                    || decision.RuleId != null && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                        || decision.Kind == RoutingDecisionKindValue.DestinationDisabled)))
            .Select(decision => new
            {
                decision.Id,
                decision.AssetId,
                decision.DestinationId,
                decision.TargetPath,
                decision.Kind,
            }).ToArrayAsync(cancellationToken);
        if (intents.Length == 0) return;

        Guid[] intentIds = [.. intents.Select(intent => intent.Id)];
        Guid[] intentAssetIds = [.. intents.Select(intent => intent.AssetId).Distinct()];
        var deliveredFacts = await database.Deliveries.AsNoTracking()
            .Where(delivery => intentIds.Contains(delivery.DecisionId)
                || delivery.State == ShutterSpool.Domain.Delivery.DeliveryState.Delivered
                    && delivery.ConfirmedAt != null && intentAssetIds.Contains(delivery.AssetId))
            .Select(delivery => new
            {
                delivery.DecisionId,
                delivery.AssetId,
                delivery.DestinationId,
                delivery.TargetPath,
                delivery.State,
                delivery.ConfirmedAt,
            }).ToArrayAsync(cancellationToken);
        var deliveriesByDecision = deliveredFacts.Where(delivery => intentIds.Contains(delivery.DecisionId))
            .GroupBy(delivery => delivery.DecisionId).ToDictionary(group => group.Key, group => group.ToArray());

        foreach (var intent in intents)
        {
            var routeDeliveries = deliveriesByDecision.GetValueOrDefault(intent.Id) ?? [];
            bool sameTargetWasConfirmed = routeDeliveries.Length == 0 && intent.TargetPath is not null
                && deliveredFacts.Any(delivery => delivery.AssetId == intent.AssetId
                    && delivery.DestinationId == intent.DestinationId
                    && delivery.State == ShutterSpool.Domain.Delivery.DeliveryState.Delivered
                    && delivery.ConfirmedAt is not null
                    && string.Equals(delivery.TargetPath, intent.TargetPath, StringComparison.OrdinalIgnoreCase));
            bool allDeliveriesConfirmed = routeDeliveries.Length > 0
                && routeDeliveries.All(delivery => delivery.State == ShutterSpool.Domain.Delivery.DeliveryState.Delivered
                    && delivery.ConfirmedAt is not null);
            if (!IsConfirmedIntent(intent.Kind, routeDeliveries.Length > 0, allDeliveriesConfirmed, sameTargetWasConfirmed)) return;
        }

        if (intents.Length > 0)
        {
            capture.FavoriteQualificationLostAt = createdAt;
            await database.CaptureAssets.Where(member => member.CaptureId == capture.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(member => member.FavoriteQualificationLostAt,
                    createdAt), cancellationToken);
        }
    }
}
