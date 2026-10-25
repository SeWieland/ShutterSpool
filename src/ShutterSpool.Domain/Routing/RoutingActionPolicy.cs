using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Domain.Routing;

public static class RoutingActionPolicy
{
    public static bool CanRetry(DeliveryState state) => state is DeliveryState.RetryWait or DeliveryState.Failed or DeliveryState.Disabled;
    public static bool CanResend(DeliveryState state) => state == DeliveryState.Delivered;
    public static bool PreviewExpired(DateTimeOffset createdAt, DateTimeOffset now) => now - createdAt > TimeSpan.FromMinutes(15);
    public static bool NeedsOriginal(RoutingDecision decision, IEnumerable<(DeliveryState State, string TargetPath)> deliveries) =>
        decision.Kind == RoutingDecisionKind.Matched && !deliveries.Any(delivery => delivery.State == DeliveryState.Delivered
            && string.Equals(delivery.TargetPath, decision.TargetPath, StringComparison.OrdinalIgnoreCase));
}
