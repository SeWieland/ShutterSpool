namespace ShutterSpool.Domain.Retention;

public static class RetentionIntentPolicy
{
    public static bool IsConfirmedIntent(bool isMatched, bool hasDeliveries,
        bool allDeliveriesConfirmed, bool sameTargetConfirmed) =>
        isMatched
        && (hasDeliveries ? allDeliveriesConfirmed : sameTargetConfirmed);
}
