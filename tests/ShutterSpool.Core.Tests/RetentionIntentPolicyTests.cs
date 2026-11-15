using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Core.Tests;

public sealed class RetentionIntentPolicyTests
{
    [Theory]
    [InlineData(true, true, true, false, true)]
    [InlineData(true, true, false, true, false)]
    [InlineData(true, false, false, true, true)]
    [InlineData(true, false, false, false, false)]
    [InlineData(false, false, false, true, false)]
    public void Intent_requires_matched_route_and_confirmation(bool isMatched, bool hasDeliveries,
        bool allDeliveriesConfirmed, bool sameTargetConfirmed, bool expected)
    {
        Assert.Equal(expected, RetentionIntentPolicy.IsConfirmedIntent(isMatched, hasDeliveries,
            allDeliveriesConfirmed, sameTargetConfirmed));
    }
}
