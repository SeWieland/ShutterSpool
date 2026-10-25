using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Core.Tests;

public sealed class DeliveryRetryScheduleTests
{
    [Fact]
    public void Delay_uses_the_accepted_schedule_and_caps_exponential_growth()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), DeliveryRetrySchedule.DelayAfter(1, 0));
        Assert.Equal(TimeSpan.FromSeconds(30), DeliveryRetrySchedule.DelayAfter(2, 0));
        Assert.Equal(TimeSpan.FromMinutes(2), DeliveryRetrySchedule.DelayAfter(3, 0));
        Assert.Equal(TimeSpan.FromMinutes(10), DeliveryRetrySchedule.DelayAfter(4, 0));
        Assert.Equal(TimeSpan.FromMinutes(30), DeliveryRetrySchedule.DelayAfter(5, 0));
        Assert.Equal(TimeSpan.FromHours(6), DeliveryRetrySchedule.DelayAfter(20, 0));
    }

    [Fact]
    public void Delay_applies_jitter_after_capping_the_base_delay()
    {
        Assert.Equal(TimeSpan.FromSeconds(9), DeliveryRetrySchedule.DelayAfter(1, -0.1));
        Assert.Equal(TimeSpan.FromSeconds(11), DeliveryRetrySchedule.DelayAfter(1, 0.1));
        Assert.Equal(TimeSpan.FromHours(5.4), DeliveryRetrySchedule.DelayAfter(20, -0.1));
        Assert.Equal(TimeSpan.FromHours(6.6), DeliveryRetrySchedule.DelayAfter(20, 0.1));
    }
}
