namespace ShutterSpool.Domain.Delivery;

public enum DeliveryState { Pending, Running, Delivered, RetryWait, Failed, Disabled, Superseded, Ambiguous }
public enum DeliveryConfirmationEvidenceLevel { AcknowledgedCompleteWrite, ReadBackHash, ProviderReceipt }
public enum DeliveryResultKind { Confirmed, TransientFailure, ConfigurationFailure, Uncertain }
public sealed record DeliveryResult(DeliveryResultKind Kind, DeliveryConfirmationEvidenceLevel? Evidence = null, long? BytesSent = null,
    string? ErrorCode = null, string? ErrorMessage = null);
public enum DeliveryReconciliationKind { Confirmed, NotPublished, Conflict, Uncertain }
public sealed record DeliveryReconciliation(DeliveryReconciliationKind Kind, DeliveryConfirmationEvidenceLevel? Evidence = null,
    string? ErrorCode = null, string? ErrorMessage = null, bool SafeToRepeat = false);

public sealed record DeliveryTransition(DeliveryState State, string AttemptResult, string? AttemptMessage,
    string? RetryReason, DateTimeOffset? NextAttemptAt = null, DeliveryConfirmationEvidenceLevel? Evidence = null);

public static class DeliveryPolicy
{
    public static DeliveryTransition AfterWrite(DeliveryResult result, bool enabled, bool current,
        int retryNumber, DateTimeOffset now, double jitterFactor)
    {
        if (result.Kind == DeliveryResultKind.Confirmed && result.Evidence is not null)
            return new(DeliveryState.Delivered, "Confirmed", null, null, Evidence: result.Evidence);
        if (result.Kind is DeliveryResultKind.Confirmed or DeliveryResultKind.Uncertain)
            return new(DeliveryState.Ambiguous, "Uncertain", "The destination write outcome could not be confirmed.",
                "The destination outcome needs reconciliation before another write.");
        string message = result.Kind == DeliveryResultKind.ConfigurationFailure
            ? "The destination requires correction before work can continue."
            : "The destination reported a temporary failure.";
        string attemptResult = result.Kind.ToString();
        if (!current) return new(DeliveryState.Superseded, attemptResult, message, null);
        if (!enabled) return new(DeliveryState.Disabled, attemptResult, message,
            result.Kind == DeliveryResultKind.TransientFailure ? "Destination is disabled." : null);
        if (result.Kind == DeliveryResultKind.ConfigurationFailure)
            return new(DeliveryState.Failed, attemptResult, message, message);
        return new(DeliveryState.RetryWait, attemptResult, message, SafeErrorCode(result.ErrorCode) ?? "TRANSIENT_FAILURE",
            now + DeliveryRetrySchedule.DelayAfter(retryNumber, jitterFactor));
    }

    public static DeliveryTransition AfterReconciliation(DeliveryReconciliation result, bool enabled, bool current, DateTimeOffset now)
    {
        if (result.Kind == DeliveryReconciliationKind.Confirmed && result.Evidence is not null)
            return new(DeliveryState.Delivered, "ConfirmedAfterRecovery", null, null, Evidence: result.Evidence);
        if (result.Kind == DeliveryReconciliationKind.NotPublished && result.SafeToRepeat)
        {
            DeliveryState state = !current ? DeliveryState.Superseded : !enabled ? DeliveryState.Disabled : DeliveryState.Pending;
            return new(state, "NotPublishedAfterRecovery", "Reconciliation proved the prior write was not published.",
                state == DeliveryState.Pending ? "Reconciliation proved the prior write was not published." : null, now);
        }
        if (result.Kind == DeliveryReconciliationKind.Conflict)
            return new(DeliveryState.Failed, "ConflictAfterRecovery", "The recorded target requires correction before delivery can continue.",
                "The recorded target requires correction before delivery can continue.");
        return new(DeliveryState.Ambiguous, "ReconciliationUncertain", "The destination outcome remains uncertain.",
            "The destination outcome remains uncertain; automatic writes are blocked.");
    }

    public static string? SafeErrorCode(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
        ? value.ToUpperInvariant()
        : null;
}

public static class DeliveryRetrySchedule
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30),
    ];

    public static TimeSpan DelayAfter(int completedAttempts, double jitterFactor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(completedAttempts, 1);
        if (!double.IsFinite(jitterFactor) || jitterFactor is < -0.1 or > 0.1) throw new ArgumentOutOfRangeException(nameof(jitterFactor));
        double seconds = completedAttempts <= Delays.Length
            ? Delays[completedAttempts - 1].TotalSeconds
            : Math.Min(TimeSpan.FromHours(6).TotalSeconds,
                Delays[^1].TotalSeconds * Math.Pow(2, completedAttempts - Delays.Length));
        return TimeSpan.FromSeconds(seconds * (1 + jitterFactor));
    }
}
