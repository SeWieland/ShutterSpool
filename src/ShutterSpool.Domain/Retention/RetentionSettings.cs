namespace ShutterSpool.Domain.Retention;

public sealed record RetentionSettings
{
    public int Revision { get; init; } = 1;
    public bool AutomaticRemovalEnabled { get; init; }
    public TimeSpan TrashRecoveryWindow { get; init; } = TimeSpan.FromHours(72);
    public TimeSpan RestoreProtection { get; init; } = TimeSpan.FromHours(72);
    public TimeSpan GalleryMaximum { get; init; } = TimeSpan.FromDays(30);
    public RetentionLifetime Confirmed { get; init; } = RetentionLifetime.Confirmed;
    public RetentionLifetime NoDelivery { get; init; } = RetentionLifetime.NoDelivery;
    public RetentionLifetime Waiting { get; init; } = RetentionLifetime.Waiting;
    public RetentionLifetime HeldOrUnfinished { get; init; } = RetentionLifetime.HeldOrUnfinished;
    public int CompletedAttemptHistoryDays { get; init; } = 90;
    public int CompletedPressureHistoryDays { get; init; } = 30;

    public void Validate()
    {
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision));
        ArgumentNullException.ThrowIfNull(Confirmed);
        ArgumentNullException.ThrowIfNull(NoDelivery);
        ArgumentNullException.ThrowIfNull(Waiting);
        ArgumentNullException.ThrowIfNull(HeldOrUnfinished);
        if (TrashRecoveryWindow <= TimeSpan.Zero || RestoreProtection <= TimeSpan.Zero || GalleryMaximum <= TimeSpan.Zero
            || TrashRecoveryWindow > TimeSpan.FromDays(3650) || RestoreProtection > TimeSpan.FromDays(3650)
            || GalleryMaximum > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(TrashRecoveryWindow), "Retention protection durations must be positive and no longer than ten years.");
        if (CompletedAttemptHistoryDays != 90 || CompletedPressureHistoryDays != 30)
            throw new ArgumentOutOfRangeException(nameof(CompletedAttemptHistoryDays), "Accepted history retention periods are fixed at 90 and 30 days.");
    }
}
