using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Infrastructure.Destinations.Local;

internal sealed class LocalDestinationDeliveryAdapter(DestinationCapabilities? capabilities = null,
    WorkspaceContext? workspace = null) : IDestinationDeliveryAdapter
{
    public string Kind => "local";

    public Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled(Kind))
            return Task.FromResult(new DeliveryResult(DeliveryResultKind.ConfigurationFailure, ErrorCode: "DESTINATION_KIND_DISABLED"));
        if (request.Configuration is not LocalDestinationDeliveryConfiguration { RootPath: { } root })
            return Task.FromResult(new DeliveryResult(DeliveryResultKind.ConfigurationFailure, ErrorCode: "LOCAL_ROOT_NOT_CONFIGURED"));
        if (!LocalDestinationPaths.IsAllowed(root, workspace))
            return Task.FromResult(new DeliveryResult(DeliveryResultKind.ConfigurationFailure, ErrorCode: "UNSAFE_PATH"));
        return LocalFileDelivery.WriteAsync(root, request, original, cancellationToken);
    }

    public Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled(Kind))
            return Task.FromResult(new DeliveryReconciliation(DeliveryReconciliationKind.Uncertain, ErrorCode: "DESTINATION_KIND_DISABLED"));
        if (request.Configuration is not LocalDestinationDeliveryConfiguration { RootPath: { } root })
            return Task.FromResult(new DeliveryReconciliation(DeliveryReconciliationKind.Uncertain, ErrorCode: "LOCAL_ROOT_NOT_CONFIGURED"));
        if (!LocalDestinationPaths.IsAllowed(root, workspace))
            return Task.FromResult(new DeliveryReconciliation(DeliveryReconciliationKind.Uncertain, ErrorCode: "UNSAFE_PATH"));
        return LocalFileDelivery.ReconcileAsync(root, request, cancellationToken);
    }
}
