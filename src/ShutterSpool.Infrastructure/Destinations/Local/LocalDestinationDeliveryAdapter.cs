using ShutterSpool.Application.Delivery;
using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Infrastructure.Destinations.Local;

internal sealed class LocalDestinationDeliveryAdapter : IDestinationDeliveryAdapter
{
    public string Kind => "local";

    public Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken) =>
        request.Configuration is LocalDestinationDeliveryConfiguration { RootPath: { } root }
            ? LocalFileDelivery.WriteAsync(root, request, original, cancellationToken)
            : Task.FromResult(new DeliveryResult(DeliveryResultKind.ConfigurationFailure, ErrorCode: "LOCAL_ROOT_NOT_CONFIGURED"));

    public Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
        request.Configuration is LocalDestinationDeliveryConfiguration { RootPath: { } root }
            ? LocalFileDelivery.ReconcileAsync(root, request, cancellationToken)
            : Task.FromResult(new DeliveryReconciliation(DeliveryReconciliationKind.Uncertain, ErrorCode: "LOCAL_ROOT_NOT_CONFIGURED"));
}
