using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class CaptureRoutingHistoryStore(ShutterSpoolDbContext database) : ICaptureRoutingHistoryStore
{
    public async Task<RoutingHistoryPage<RoutingDecisionHistory>?> GetDecisionsAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token)
    {
        if (!await CaptureExistsAsync(captureId, token)) return null;
        var decisions = database.RoutingDecisions.AsNoTracking().Where(item => item.CaptureId == captureId);
        if (query.AssetId is Guid asset) decisions = decisions.Where(item => item.AssetId == asset);
        if (query.DestinationId is Guid destination) decisions = decisions.Where(item => item.DestinationId == destination);
        if (query.Before is DateTimeOffset before && query.BeforeId is Guid id)
            decisions = decisions.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingDecisionHistory[] items = await (from item in decisions
            join target in database.Destinations.AsNoTracking() on item.DestinationId equals target.Id into destinationRows
            from target in destinationRows.DefaultIfEmpty()
            orderby item.CreatedAt descending, item.Id descending
            select new RoutingDecisionHistory(item.Id, item.AssetId, item.DestinationId, item.Kind.ToString(),
                item.RuleId, item.RuleName, item.Required, item.TargetPath, item.Explanation, item.CreatedAt,
                item.IsCurrent, item.Generation, target == null ? null : target.Name,
                item.CollectionId, item.CollectionName, item.CollectionPath))
            .Take(query.Limit + 1).ToArrayAsync(token);
        return Page(items, query.Limit, item => (item.CreatedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDeliveriesAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token)
    {
        if (!await CaptureExistsAsync(captureId, token)) return null;
        var deliveries = database.Deliveries.AsNoTracking().Where(item => database.CaptureAssets.Any(member => member.CaptureId == captureId && member.AssetId == item.AssetId));
        if (query.AssetId is Guid asset) deliveries = deliveries.Where(item => item.AssetId == asset);
        if (query.DestinationId is Guid destination) deliveries = deliveries.Where(item => item.DestinationId == destination);
        if (query.Before is DateTimeOffset before && query.BeforeId is Guid id)
            deliveries = deliveries.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingDeliveryHistory[] items = await (from item in deliveries
            join decision in database.RoutingDecisions on item.DecisionId equals decision.Id
            join target in database.Destinations.AsNoTracking() on item.DestinationId equals target.Id into destinationRows
            from target in destinationRows.DefaultIfEmpty()
            orderby item.CreatedAt descending, item.Id descending
            select new RoutingDeliveryHistory(item.Id, item.AssetId, item.DecisionId, item.DestinationId, item.TargetPath,
                item.Required, item.State.ToString(), decision.RuleName, decision.Explanation, item.CreatedAt,
                item.RepeatsDeliveryId, item.NextAttemptAt, item.RetryReason, item.ConfirmationEvidence, decision.CaptureId,
                target == null ? null : target.Name, decision.CollectionId, decision.CollectionName, decision.CollectionPath))
            .Take(query.Limit + 1).ToArrayAsync(token);
        return Page(items, query.Limit, item => (item.CreatedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDestinationWorkAsync(Guid destinationId,
        RoutingHistoryQuery query, CancellationToken token)
    {
        if (!await database.Destinations.AnyAsync(item => item.Id == destinationId, token)) return null;
        var deliveries = database.Deliveries.AsNoTracking().Where(item => item.DestinationId == destinationId);
        if (query.State is DeliveryState state) deliveries = deliveries.Where(item => item.State == state);
        if (query.Before is DateTimeOffset before && query.BeforeId is Guid id)
            deliveries = deliveries.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingDeliveryHistory[] items = await (
            from item in deliveries
            join decision in database.RoutingDecisions.AsNoTracking() on item.DecisionId equals decision.Id
            join target in database.Destinations.AsNoTracking() on item.DestinationId equals target.Id into destinationRows
            from target in destinationRows.DefaultIfEmpty()
            orderby item.CreatedAt descending, item.Id descending
            select new RoutingDeliveryHistory(item.Id, item.AssetId, item.DecisionId, item.DestinationId,
                item.TargetPath, item.Required, item.State.ToString(), decision.RuleName, decision.Explanation,
                item.CreatedAt, item.RepeatsDeliveryId, item.NextAttemptAt, item.RetryReason, item.ConfirmationEvidence,
                decision.CaptureId, target == null ? null : target.Name,
                decision.CollectionId, decision.CollectionName, decision.CollectionPath))
            .Take(query.Limit + 1).ToArrayAsync(token);
        return Page(items, query.Limit, item => (item.CreatedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingAttemptHistory>?> GetAttemptsAsync(Guid deliveryId, RoutingHistoryQuery query, CancellationToken token)
    {
        if (!await database.Deliveries.AnyAsync(item => item.Id == deliveryId, token)) return null;
        var attempts = database.DeliveryAttempts.AsNoTracking().Where(item => item.DeliveryId == deliveryId);
        if (query.Before is DateTimeOffset before && query.BeforeId is Guid id)
            attempts = attempts.Where(item => item.StartedAt < before || item.StartedAt == before && item.Id.CompareTo(id) < 0);
        RoutingAttemptHistory[] items = await attempts.OrderByDescending(item => item.StartedAt).ThenByDescending(item => item.Id)
            .Take(query.Limit + 1).Select(item => new RoutingAttemptHistory(item.Id, item.DeliveryId, item.RetryNumber, item.WorkerId,
                item.StartedAt, item.FinishedAt, item.Result, item.ErrorCode, item.ErrorMessage, item.BytesSent,
                item.ConfirmationEvidence, item.TraceId)).ToArrayAsync(token);
        return Page(items, query.Limit, item => (item.StartedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingActionHistory>?> GetActionsAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token)
    {
        if (!await CaptureExistsAsync(captureId, token)) return null;
        var actions = database.RoutingActions.AsNoTracking().Where(item => database.CaptureAssets.Any(member => member.CaptureId == captureId && member.AssetId == item.AssetId));
        if (query.AssetId is Guid asset) actions = actions.Where(item => item.AssetId == asset);
        if (query.DestinationId is Guid destination) actions = actions.Where(item => item.DestinationId == destination);
        if (query.Before is DateTimeOffset before && query.BeforeId is Guid id)
            actions = actions.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingActionHistory[] items = await actions.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(query.Limit + 1).Select(item => new RoutingActionHistory(item.Id, item.Kind, item.AssetId, item.DestinationId,
                item.SourceDeliveryId, item.DeliveryId, item.SourceDecisionId, item.DecisionId, item.ProposedTarget,
                item.Result ?? "preview", item.CreatedAt, item.ConfirmedAt)).ToArrayAsync(token);
        return Page(items, query.Limit, item => (item.CreatedAt, item.Id));
    }

    private Task<bool> CaptureExistsAsync(Guid id, CancellationToken token) => database.Captures.AnyAsync(item => item.Id == id, token);

    private static RoutingHistoryPage<T> Page<T>(T[] items, int limit, Func<T, (DateTimeOffset At, Guid Id)> anchor)
    {
        if (items.Length <= limit) return new(items, null, null);
        (DateTimeOffset at, Guid id) = anchor(items[limit - 1]);
        return new(items[..limit], at, id);
    }
}
