namespace ShutterSpool.Application.Delivery;

public sealed class DeliveryWorkerSettings
{
    public int LeaseDurationSeconds { get; set; } = 60;
    public int LeaseRenewalSeconds { get; set; } = 15;
    public int ScanIntervalSeconds { get; set; } = 2;

    public void Validate()
    {
        if (LeaseDurationSeconds is < 15 or > 3_600) throw new InvalidOperationException("Delivery lease duration must be 15–3600 seconds.");
        if (LeaseRenewalSeconds < 1 || LeaseRenewalSeconds >= LeaseDurationSeconds)
            throw new InvalidOperationException("Delivery lease renewal must be positive and shorter than the lease.");
        if (ScanIntervalSeconds is < 1 or > 60) throw new InvalidOperationException("Delivery scan interval must be 1–60 seconds.");
    }
}
