using Microsoft.EntityFrameworkCore;
using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Infrastructure.Persistence;

internal static class RetentionQualification
{
    public static async Task RecordNewIntentAsync(ShutterSpoolDbContext database, CaptureEntity capture,
        DateTimeOffset createdAt, CancellationToken cancellationToken)
    {
        capture.RetentionFactsDirty = true;
        capture.NoDeliveryIntent = false;
        if (!capture.IsFavorite || capture.FavoriteQualificationLostAt is not null) return;
        if (!await AllPriorIntentsConfirmedAsync(database, capture.Id, cancellationToken)) return;

        capture.FavoriteQualificationLostAt = createdAt;
        await database.CaptureAssets.Where(member => member.CaptureId == capture.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(member => member.FavoriteQualificationLostAt,
                createdAt), cancellationToken);
    }

    private static async Task<bool> AllPriorIntentsConfirmedAsync(ShutterSpoolDbContext database, Guid captureId,
        CancellationToken cancellationToken)
    {
        RetentionIntentFact[] intents = await database.RoutingDecisions.AsNoTracking()
            .Where(decision => decision.CaptureId == captureId
                && (decision.Kind == RoutingDecisionKindValue.Matched
                    || decision.RuleId != null && (decision.Kind == RoutingDecisionKindValue.InvalidTarget
                        || decision.Kind == RoutingDecisionKindValue.DestinationDisabled)))
            .Select(decision => new RetentionIntentFact(decision.Id, decision.AssetId, decision.DestinationId,
                decision.TargetPath, decision.Kind)).ToArrayAsync(cancellationToken);
        if (intents.Length == 0) return false;

        Guid[] intentIds = [.. intents.Select(intent => intent.Id)];
        Guid[] intentAssetIds = [.. intents.Select(intent => intent.AssetId).Distinct()];
        RetentionDeliveryFact[] deliveryFacts = await database.Deliveries.AsNoTracking()
            .Where(delivery => intentIds.Contains(delivery.DecisionId)
                || delivery.State == DeliveryState.Delivered
                    && delivery.ConfirmedAt != null && intentAssetIds.Contains(delivery.AssetId))
            .Select(delivery => new RetentionDeliveryFact(delivery.DecisionId, delivery.AssetId, delivery.DestinationId,
                delivery.TargetPath, delivery.State, delivery.ConfirmedAt)).ToArrayAsync(cancellationToken);
        var deliveriesByDecision = deliveryFacts.Where(delivery => intentIds.Contains(delivery.DecisionId))
            .GroupBy(delivery => delivery.DecisionId).ToDictionary(group => group.Key, group => group.ToArray());
        return intents.All(intent => intent.IsConfirmedBy(deliveriesByDecision.GetValueOrDefault(intent.Id) ?? [], deliveryFacts));
    }
}
