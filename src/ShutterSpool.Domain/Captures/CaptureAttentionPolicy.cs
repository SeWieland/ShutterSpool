using System.Linq.Expressions;

namespace ShutterSpool.Domain.Captures;

public sealed class CaptureAttentionFacts
{
    public Guid CaptureId { get; init; }
    public bool IsReleased { get; init; }
    public bool HasUnroutedPrimaryAsset { get; init; }
    public bool HasBlockedRouting { get; init; }
    public bool HasBlockedDelivery { get; init; }
    public bool HasRetentionUrgency { get; init; }
}

public static class CaptureAttentionPolicy
{
    public static Expression<Func<CaptureAttentionFacts, bool>> RequiresAttention { get; } = facts =>
        (facts.IsReleased && facts.HasUnroutedPrimaryAsset)
        || facts.HasBlockedRouting
        || facts.HasBlockedDelivery
        || facts.HasRetentionUrgency;
}
