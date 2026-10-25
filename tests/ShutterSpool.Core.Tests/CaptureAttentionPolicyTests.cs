using ShutterSpool.Domain.Captures;

namespace ShutterSpool.Core.Tests;

public sealed class CaptureAttentionPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, false)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, true, false, false, true)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, true, true)]
    public void Release_and_unresolved_work_determine_attention(bool released, bool unrouted,
        bool blockedRouting, bool blockedDelivery, bool expected)
    {
        var facts = new CaptureAttentionFacts
        {
            IsReleased = released,
            HasUnroutedPrimaryAsset = unrouted,
            HasBlockedRouting = blockedRouting,
            HasBlockedDelivery = blockedDelivery,
        };

        Assert.Equal(expected, CaptureAttentionPolicy.RequiresAttention.Compile()(facts));
    }

    [Fact]
    public void Unresolved_work_is_informational_but_authoritative_due_retention_is_actionable()
    {
        var noUnresolvedWork = new CaptureAttentionFacts();
        var due = new CaptureAttentionFacts { HasRetentionUrgency = true };

        Assert.False(CaptureAttentionPolicy.RequiresAttention.Compile()(noUnresolvedWork));
        Assert.True(CaptureAttentionPolicy.RequiresAttention.Compile()(due));
    }
}
