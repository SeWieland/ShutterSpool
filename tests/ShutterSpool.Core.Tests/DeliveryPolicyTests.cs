using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Core.Tests;

public sealed class DeliveryPolicyTests
{
    [Fact]
    public void Unconfirmed_success_blocks_automatic_repeat()
    {
        DeliveryTransition decision = DeliveryPolicy.AfterWrite(new(DeliveryResultKind.Confirmed), true, true,
            1, DateTimeOffset.UtcNow, 0);
        Assert.Equal(DeliveryState.Ambiguous, decision.State);
        Assert.Null(decision.NextAttemptAt);
    }

    [Fact]
    public void Only_safe_reconciliation_can_requeue_the_recorded_target()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(DeliveryState.Ambiguous,
            DeliveryPolicy.AfterReconciliation(new(DeliveryReconciliationKind.NotPublished), true, true, now).State);
        DeliveryTransition safe = DeliveryPolicy.AfterReconciliation(
            new(DeliveryReconciliationKind.NotPublished, SafeToRepeat: true), true, true, now);
        Assert.Equal(DeliveryState.Pending, safe.State);
        Assert.Equal(now, safe.NextAttemptAt);
    }

    [Fact]
    public void Definite_transient_failure_retries_without_attempt_ceiling()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DeliveryTransition decision = DeliveryPolicy.AfterWrite(new(DeliveryResultKind.TransientFailure, ErrorCode: "mount_busy"),
            true, true, 999, now, 0);
        Assert.Equal(DeliveryState.RetryWait, decision.State);
        Assert.Equal(now.AddHours(6), decision.NextAttemptAt);
        Assert.Equal("MOUNT_BUSY", decision.RetryReason);
    }
}
