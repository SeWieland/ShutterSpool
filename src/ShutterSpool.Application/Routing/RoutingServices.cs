using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Application.Routing;

public interface ICaptureRoutingHistoryStore
{
    Task<RoutingHistoryPage<RoutingDecisionHistory>?> GetDecisionsAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token);
    Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDeliveriesAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token);
    Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDestinationWorkAsync(Guid destinationId, RoutingHistoryQuery query, CancellationToken token);
    Task<RoutingHistoryPage<RoutingAttemptHistory>?> GetAttemptsAsync(Guid deliveryId, RoutingHistoryQuery query, CancellationToken token);
    Task<RoutingHistoryPage<RoutingActionHistory>?> GetActionsAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token);
}

public sealed class CaptureRoutingHistoryService(ICaptureRoutingHistoryStore store)
{
    public Task<RoutingHistoryPage<RoutingDecisionHistory>?> GetDecisionsAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token) =>
        store.GetDecisionsAsync(captureId, Validate(query), token);
    public Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDeliveriesAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token) =>
        store.GetDeliveriesAsync(captureId, Validate(query), token);
    public Task<RoutingHistoryPage<RoutingDeliveryHistory>?> GetDestinationWorkAsync(Guid destinationId, RoutingHistoryQuery query, CancellationToken token) =>
        store.GetDestinationWorkAsync(destinationId, Validate(query), token);
    public Task<RoutingHistoryPage<RoutingAttemptHistory>?> GetAttemptsAsync(Guid deliveryId, RoutingHistoryQuery query, CancellationToken token) =>
        store.GetAttemptsAsync(deliveryId, Validate(query), token);
    public Task<RoutingHistoryPage<RoutingActionHistory>?> GetActionsAsync(Guid captureId, RoutingHistoryQuery query, CancellationToken token) =>
        store.GetActionsAsync(captureId, Validate(query), token);

    private static RoutingHistoryQuery Validate(RoutingHistoryQuery query) => query.Limit is >= 1 and <= 100
        && query.Before.HasValue == query.BeforeId.HasValue ? query : throw new ArgumentException("Invalid history page.");
}

public interface ICaptureRoutingActionStore
{
    Task<RoutingSupplierRequest?> RetrySupplierRequestAsync(Guid deliveryId, CancellationToken cancellationToken);
    Task<RoutingSupplierRequest?> ResendSupplierRequestAsync(Guid deliveryId, CancellationToken cancellationToken);
    Task<RoutingSupplierRequest?> ReevaluateSupplierRequestAsync(Guid assetId, Guid destinationId, CancellationToken cancellationToken);
    Task<RoutingActionPreview> RetryAsync(Guid deliveryId, Guid? verifiedSupplierId, CancellationToken cancellationToken);
    Task<RoutingActionPreview> PreviewResendAsync(Guid deliveryId, CancellationToken cancellationToken);
    Task<RoutingActionPreview> ResendAsync(Guid deliveryId, string? confirmationToken, Guid? verifiedSupplierId, CancellationToken cancellationToken);
    Task<RoutingActionPreview> PreviewReevaluateAsync(Guid assetId, Guid destinationId, CancellationToken cancellationToken);
    Task<RoutingActionPreview> ReevaluateAsync(Guid assetId, Guid destinationId, string? confirmationToken, Guid? verifiedSupplierId,
        CancellationToken cancellationToken);
}

public sealed record RoutingSupplierRequest(Guid AssetId, Guid? PreferredSupplierId = null);

public interface IVerifiedOriginalSupplier
{
    Task<Guid?> FindAsync(Guid assetId, Guid? preferredSupplierId, CancellationToken cancellationToken);
}

public sealed class CaptureRoutingActionService(ICaptureRoutingActionStore store, IVerifiedOriginalSupplier originals)
{
    public async Task<RoutingActionPreview> RetryAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        RoutingSupplierRequest? request = await store.RetrySupplierRequestAsync(deliveryId, cancellationToken);
        Guid? supplier = request is null ? null : await originals.FindAsync(request.AssetId, request.PreferredSupplierId, cancellationToken);
        return await store.RetryAsync(deliveryId, supplier, cancellationToken);
    }
    public Task<RoutingActionPreview> PreviewResendAsync(Guid deliveryId, CancellationToken cancellationToken) =>
        store.PreviewResendAsync(deliveryId, cancellationToken);
    public async Task<RoutingActionPreview> ResendAsync(Guid deliveryId, string? confirmationToken, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(confirmationToken, "N", out _))
            return await store.ResendAsync(deliveryId, confirmationToken, null, cancellationToken);
        RoutingSupplierRequest? request = await store.ResendSupplierRequestAsync(deliveryId, cancellationToken);
        Guid? supplier = request is null ? null : await originals.FindAsync(request.AssetId, request.PreferredSupplierId, cancellationToken);
        return await store.ResendAsync(deliveryId, confirmationToken, supplier, cancellationToken);
    }
    public Task<RoutingActionPreview> PreviewReevaluateAsync(Guid assetId, Guid destinationId, CancellationToken cancellationToken) =>
        store.PreviewReevaluateAsync(assetId, destinationId, cancellationToken);
    public async Task<RoutingActionPreview> ReevaluateAsync(Guid assetId, Guid destinationId, string? confirmationToken,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(confirmationToken, "N", out _))
            return await store.ReevaluateAsync(assetId, destinationId, confirmationToken, null, cancellationToken);
        RoutingSupplierRequest? request = await store.ReevaluateSupplierRequestAsync(assetId, destinationId, cancellationToken);
        Guid? supplier = request is null ? null : await originals.FindAsync(request.AssetId, request.PreferredSupplierId, cancellationToken);
        return await store.ReevaluateAsync(assetId, destinationId, confirmationToken, supplier, cancellationToken);
    }
}

public interface IDestinationRoutingStore
{
    Task<DestinationRouting?> GetAsync(Guid destinationId, CancellationToken cancellationToken);
    Task<DestinationRoutingMutation> SetAsync(DestinationRouting routing, long expectedRevision, CancellationToken cancellationToken);
}

public sealed record DestinationRoutingMutation(bool Applied, DestinationRouting? Current);

public sealed class DestinationRoutingService(IDestinationRoutingStore store)
{
    public Task<DestinationRouting?> GetAsync(Guid destinationId, CancellationToken cancellationToken) =>
        store.GetAsync(destinationId, cancellationToken);
    public Task<DestinationRoutingMutation> SetAsync(DestinationRouting routing, long expectedRevision, CancellationToken cancellationToken) =>
        store.SetAsync(routing, expectedRevision, cancellationToken);
}
