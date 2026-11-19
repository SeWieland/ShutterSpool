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
        if (Revision < 0) throw OutOfRange(nameof(Revision));
        ArgumentNullException.ThrowIfNull(Confirmed);
        ArgumentNullException.ThrowIfNull(NoDelivery);
        ArgumentNullException.ThrowIfNull(Waiting);
        ArgumentNullException.ThrowIfNull(HeldOrUnfinished);

        const string durationError = "Retention protection durations must be positive and no longer than ten years.";
        if (TrashRecoveryWindow <= TimeSpan.Zero) throw OutOfRange(nameof(TrashRecoveryWindow), durationError);
        if (RestoreProtection <= TimeSpan.Zero) throw OutOfRange(nameof(RestoreProtection), durationError);
        if (GalleryMaximum <= TimeSpan.Zero) throw OutOfRange(nameof(GalleryMaximum), durationError);

        TimeSpan maximumDuration = TimeSpan.FromDays(3650);
        if (TrashRecoveryWindow > maximumDuration) throw OutOfRange(nameof(TrashRecoveryWindow), durationError);
        if (RestoreProtection > maximumDuration) throw OutOfRange(nameof(RestoreProtection), durationError);
        if (GalleryMaximum > maximumDuration) throw OutOfRange(nameof(GalleryMaximum), durationError);

        const string historyError = "Accepted history retention periods are fixed at 90 and 30 days.";
        if (CompletedAttemptHistoryDays != 90) throw OutOfRange(nameof(CompletedAttemptHistoryDays), historyError);
        if (CompletedPressureHistoryDays != 30) throw OutOfRange(nameof(CompletedPressureHistoryDays), historyError);
    }

    private static ArgumentOutOfRangeException OutOfRange(string paramName, string? message = null) =>
        message is null ? new(paramName) : new(paramName, message);
}
