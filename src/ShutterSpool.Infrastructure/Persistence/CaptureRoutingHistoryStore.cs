using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class CaptureRoutingHistoryStore(ShutterSpoolDbContext database) : ICaptureRoutingHistoryStore
{
    public async Task<RoutingHistoryPage<RoutingDecisionHistory>?> GetDecisionsAsync(Guid captureId, RoutingHistoryQuery page, CancellationToken token)
    {
        if (!await CaptureExistsAsync(captureId, token)) return null;
        var query = database.RoutingDecisions.AsNoTracking().Where(item => item.CaptureId == captureId);
        if (page.AssetId is Guid asset) query = query.Where(item => item.AssetId == asset);
        if (page.DestinationId is Guid destination) query = query.Where(item => item.DestinationId == destination);
        if (page.Before is DateTimeOffset before && page.BeforeId is Guid id)
            query = query.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingDecisionHistory[] items = await (from item in query
            join target in database.Destinations.AsNoTracking() on item.DestinationId equals target.Id into destinationRows
            from target in destinationRows.DefaultIfEmpty()
            orderby item.CreatedAt descending, item.Id descending
            select new RoutingDecisionHistory(item.Id, item.AssetId, item.DestinationId, item.Kind.ToString(),
                item.RuleId, item.RuleName, item.Required, item.TargetPath, item.Explanation, item.CreatedAt,
                item.IsCurrent, item.Generation, target == null ? null : target.Name,
                item.CollectionId, item.CollectionName, item.CollectionPath))
            .Take(page.Limit + 1).ToArrayAsync(token);
        return Page(items, page.Limit, item => (item.CreatedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDeliveriesAsync(Guid captureId, RoutingHistoryQuery page, CancellationToken token)
    {
        if (!await CaptureExistsAsync(captureId, token)) return null;
        var query = database.Deliveries.AsNoTracking().Where(item => database.CaptureAssets.Any(member => member.CaptureId == captureId && member.AssetId == item.AssetId));
        if (page.AssetId is Guid asset) query = query.Where(item => item.AssetId == asset);
        if (page.DestinationId is Guid destination) query = query.Where(item => item.DestinationId == destination);
        if (page.Before is DateTimeOffset before && page.BeforeId is Guid id)
            query = query.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingDeliveryHistory[] items = await (from item in query
            join decision in database.RoutingDecisions on item.DecisionId equals decision.Id
            join target in database.Destinations.AsNoTracking() on item.DestinationId equals target.Id into destinationRows
            from target in destinationRows.DefaultIfEmpty()
            orderby item.CreatedAt descending, item.Id descending
            select new RoutingDeliveryHistory(item.Id, item.AssetId, item.DecisionId, item.DestinationId, item.TargetPath,
                item.Required, item.State.ToString(), decision.RuleName, decision.Explanation, item.CreatedAt,
                item.RepeatsDeliveryId, item.NextAttemptAt, item.RetryReason, item.ConfirmationEvidence, decision.CaptureId,
                target == null ? null : target.Name, decision.CollectionId, decision.CollectionName, decision.CollectionPath))
            .Take(page.Limit + 1).ToArrayAsync(token);
        return Page(items, page.Limit, item => (item.CreatedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDestinationWorkAsync(Guid destinationId,
        RoutingHistoryQuery page, CancellationToken token)
    {
        if (!await database.Destinations.AnyAsync(item => item.Id == destinationId, token)) return null;
        var query = database.Deliveries.AsNoTracking().Where(item => item.DestinationId == destinationId);
        if (page.State is DeliveryState state) query = query.Where(item => item.State == state);
        if (page.Before is DateTimeOffset before && page.BeforeId is Guid id)
            query = query.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingDeliveryHistory[] items = await (
            from item in query
            join decision in database.RoutingDecisions.AsNoTracking() on item.DecisionId equals decision.Id
            join target in database.Destinations.AsNoTracking() on item.DestinationId equals target.Id into destinationRows
            from target in destinationRows.DefaultIfEmpty()
            orderby item.CreatedAt descending, item.Id descending
            select new RoutingDeliveryHistory(item.Id, item.AssetId, item.DecisionId, item.DestinationId,
                item.TargetPath, item.Required, item.State.ToString(), decision.RuleName, decision.Explanation,
                item.CreatedAt, item.RepeatsDeliveryId, item.NextAttemptAt, item.RetryReason, item.ConfirmationEvidence,
                decision.CaptureId, target == null ? null : target.Name,
                decision.CollectionId, decision.CollectionName, decision.CollectionPath))
            .Take(page.Limit + 1).ToArrayAsync(token);
        return Page(items, page.Limit, item => (item.CreatedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingAttemptHistory>?> GetAttemptsAsync(Guid deliveryId, RoutingHistoryQuery page, CancellationToken token)
    {
        if (!await database.Deliveries.AnyAsync(item => item.Id == deliveryId, token)) return null;
        var query = database.DeliveryAttempts.AsNoTracking().Where(item => item.DeliveryId == deliveryId);
        if (page.Before is DateTimeOffset before && page.BeforeId is Guid id)
            query = query.Where(item => item.StartedAt < before || item.StartedAt == before && item.Id.CompareTo(id) < 0);
        RoutingAttemptHistory[] items = await query.OrderByDescending(item => item.StartedAt).ThenByDescending(item => item.Id)
            .Take(page.Limit + 1).Select(item => new RoutingAttemptHistory(item.Id, item.DeliveryId, item.RetryNumber, item.WorkerId,
                item.StartedAt, item.FinishedAt, item.Result, item.ErrorCode, item.ErrorMessage, item.BytesSent,
                item.ConfirmationEvidence, item.TraceId)).ToArrayAsync(token);
        return Page(items, page.Limit, item => (item.StartedAt, item.Id));
    }

    public async Task<RoutingHistoryPage<RoutingActionHistory>?> GetActionsAsync(Guid captureId, RoutingHistoryQuery page, CancellationToken token)
    {
        if (!await CaptureExistsAsync(captureId, token)) return null;
        var query = database.RoutingActions.AsNoTracking().Where(item => database.CaptureAssets.Any(member => member.CaptureId == captureId && member.AssetId == item.AssetId));
        if (page.AssetId is Guid asset) query = query.Where(item => item.AssetId == asset);
        if (page.DestinationId is Guid destination) query = query.Where(item => item.DestinationId == destination);
        if (page.Before is DateTimeOffset before && page.BeforeId is Guid id)
            query = query.Where(item => item.CreatedAt < before || item.CreatedAt == before && item.Id.CompareTo(id) < 0);
        RoutingActionHistory[] items = await query.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(page.Limit + 1).Select(item => new RoutingActionHistory(item.Id, item.Kind, item.AssetId, item.DestinationId,
                item.SourceDeliveryId, item.DeliveryId, item.SourceDecisionId, item.DecisionId, item.ProposedTarget,
                item.Result ?? "preview", item.CreatedAt, item.ConfirmedAt)).ToArrayAsync(token);
        return Page(items, page.Limit, item => (item.CreatedAt, item.Id));
    }

    private Task<bool> CaptureExistsAsync(Guid id, CancellationToken token) => database.Captures.AnyAsync(item => item.Id == id, token);

    private static RoutingHistoryPage<T> Page<T>(T[] items, int limit, Func<T, (DateTimeOffset At, Guid Id)> anchor)
    {
        if (items.Length <= limit) return new(items, null, null);
        (DateTimeOffset at, Guid id) = anchor(items[limit - 1]);
        return new(items[..limit], at, id);
    }
}
