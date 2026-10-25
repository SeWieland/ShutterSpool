using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Domain.Captures;

public enum CaptureSelection { Neutral, Picked, Rejected }
public enum CaptureState { CURATING, READY_FOR_RELEASE, RELEASED, COMPLETE }

public sealed record CaptureCurationSnapshot(
    int Revision,
    int Rating,
    string? Label,
    string[] Tags,
    SourceCurationMode Policy,
    int? DelaySeconds,
    DateTimeOffset LastPrimaryIngestedAt,
    DateTimeOffset? ReleaseDueAt,
    bool Held,
    CaptureState State,
    DateTimeOffset? ReleasedAt,
    int? ReleasedRevision,
    int? ReleasedRating,
    string? ReleasedLabel,
    string[] ReleasedTags)
{
    public CaptureSelection Selection => SelectionFor(Rating);
    public CaptureSelection? ReleasedSelection => ReleasedRating is int rating ? SelectionFor(rating) : null;

    public static CaptureSelection SelectionFor(int rating) => rating switch
    {
        -1 => CaptureSelection.Rejected,
        0 => CaptureSelection.Neutral,
        >= 1 and <= 5 => CaptureSelection.Picked,
        _ => throw new ArgumentOutOfRangeException(nameof(rating)),
    };
}

public sealed record CaptureCurationDecision(bool Accepted, bool Changed, CaptureCurationSnapshot Snapshot);

public static class CaptureCurationPolicy
{
    private static readonly HashSet<string> Labels = new(["Red", "Yellow", "Green", "Blue", "Purple"], StringComparer.Ordinal);

    public static bool IsCanonicalLabel(string? label) => label is not null && Labels.Contains(label);

    public static string[] Validate(int rating, string? label, IEnumerable<string>? tags)
    {
        if (rating is < -1 or > 5 || label is not null && !IsCanonicalLabel(label) || tags is null)
            throw new ArgumentException("The Capture rating, label or tags are invalid.");
        string[] normalized = [.. tags.Select(tag => tag?.Trim() ?? "")];
        if (normalized.Length > 32 || normalized.Any(tag => tag.Length is 0 or > 64)
            || normalized.Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new ArgumentException("Tags must be nonempty, case-unique values of at most 64 characters; at most 32 are allowed.");
        return normalized;
    }

    public static DateTimeOffset? DueAt(SourceCurationMode mode, int? delaySeconds, DateTimeOffset lastPrimaryIngestedAt) => mode switch
    {
        SourceCurationMode.Immediate => lastPrimaryIngestedAt,
        SourceCurationMode.Delay => lastPrimaryIngestedAt.AddSeconds(delaySeconds ?? 3_600),
        _ => null,
    };

    public static bool DefaultHeld(SourceCurationMode mode) => mode == SourceCurationMode.Manual;

    public static bool IsDue(bool held, DateTimeOffset? releasedAt, DateTimeOffset? dueAt, DateTimeOffset now) =>
        !held && releasedAt is null && dueAt <= now;

    public static CaptureCurationDecision Update(CaptureCurationSnapshot capture, int expectedRevision,
        int rating, string? label, string[] tags) =>
        capture.Revision != expectedRevision
            ? Conflict(capture)
            : Changed(capture with { Revision = capture.Revision + 1, Rating = rating, Label = label, Tags = [.. tags] });

    public static CaptureCurationDecision Hold(CaptureCurationSnapshot capture, int expectedRevision)
    {
        if (capture.Revision != expectedRevision || capture.ReleasedAt is not null) return Conflict(capture);
        return capture.Held ? Unchanged(capture) : Changed(capture with { Revision = capture.Revision + 1, Held = true });
    }

    public static CaptureCurationDecision Restore(CaptureCurationSnapshot capture, int expectedRevision)
    {
        if (capture.Revision != expectedRevision || capture.ReleasedAt is not null) return Conflict(capture);
        bool held = DefaultHeld(capture.Policy);
        DateTimeOffset? dueAt = DueAt(capture.Policy, capture.DelaySeconds, capture.LastPrimaryIngestedAt);
        return capture.Held == held && capture.ReleaseDueAt == dueAt
            ? Unchanged(capture)
            : Changed(capture with { Revision = capture.Revision + 1, Held = held, ReleaseDueAt = dueAt });
    }

    public static CaptureCurationDecision ReleaseNow(CaptureCurationSnapshot capture, int expectedRevision, DateTimeOffset now) =>
        capture.ReleasedAt is not null ? Unchanged(capture)
        : capture.Revision != expectedRevision ? Conflict(capture)
        : Release(capture, now);

    public static CaptureCurationDecision ReleaseDue(CaptureCurationSnapshot capture, DateTimeOffset now) =>
        IsDue(capture.Held, capture.ReleasedAt, capture.ReleaseDueAt, now) ? Release(capture, now) : Conflict(capture);

    private static CaptureCurationDecision Release(CaptureCurationSnapshot capture, DateTimeOffset now) => Changed(capture with
    {
        Revision = capture.Revision + 1,
        Held = false,
        State = CaptureState.READY_FOR_RELEASE,
        ReleasedAt = now,
        ReleasedRevision = capture.Revision,
        ReleasedRating = capture.Rating,
        ReleasedLabel = capture.Label,
        ReleasedTags = [.. capture.Tags],
        ReleaseDueAt = capture.ReleaseDueAt ?? now,
    });

    private static CaptureCurationDecision Conflict(CaptureCurationSnapshot capture) => new(false, false, capture);
    private static CaptureCurationDecision Unchanged(CaptureCurationSnapshot capture) => new(true, false, capture);
    private static CaptureCurationDecision Changed(CaptureCurationSnapshot capture) => new(true, true, capture);
}

public static class CaptureCompletionPolicy
{
    public static CaptureState State(bool hasCurrentMatchedRoute, bool hasPendingRequiredDelivery) =>
        hasCurrentMatchedRoute && !hasPendingRequiredDelivery ? CaptureState.COMPLETE : CaptureState.RELEASED;
}
