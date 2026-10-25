using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Application.Retention;

public enum RetentionDisposition { Active, Trash, Purging, Purged }
public enum RetentionUrgency { Informational, Actionable, Urgent }
public enum RetentionTimelineKind { NormalRetention, HeldOrBlocked, TrashRecovery }
public enum RetentionOriginalState { Available, PendingRemoval, FailedRemoval, Removed, Missing }

public sealed record RetentionTimeline(RetentionTimelineKind Kind, DateTimeOffset? StartedAt,
    DateTimeOffset? EndsAt, DateTimeOffset ObservedAt, string Meaning);

public sealed record RetentionOriginal(Guid AssetId, string Variant, long ByteLength, DateTimeOffset? ProtectedUntil,
    RetentionOriginalState State);

public sealed record CaptureRetentionSummary(
    DateTimeOffset AsOf,
    int CaptureRevision,
    int RetentionRevision,
    RetentionDisposition Disposition,
    bool IsFavorite,
    RetentionTier? Tier,
    DateTimeOffset? PressureEligibleAt,
    DateTimeOffset? TrashEligibleAt,
    DateTimeOffset? RecoverableUntil,
    DateTimeOffset? ProtectedUntil,
    string[] EligibilityReasons,
    string[] ProtectionReasons,
    RetentionUrgency Urgency,
    RetentionTimeline? Timeline,
    RetentionOriginal[] AvailableOriginals,
    bool IsPaused = false,
    string[]? PauseReasons = null,
    bool CanRecordNoDeliveryIntent = false,
    string[]? NoDeliveryIntentReasons = null);

public sealed record RetentionTrashItem(Guid CaptureId, int CaptureRevision, string DisplayName,
    DateTimeOffset EnteredAt, DateTimeOffset RecoverableUntil, string Reason, CaptureRetentionSummary Retention);

public sealed record RetentionTrashPage(RetentionTrashItem[] Items, string? NextCursor);

public sealed record RetentionPolicySnapshot(RetentionSettings Policy, long QuotaRevision,
    bool AutomaticRemovalEnabled, long? QuotaBytes, long? HighWatermarkBytes, long? RecoveryTargetBytes);

public sealed record RetentionPolicyImpact(long UsedBytes, long ProtectedBytes, int EligibleCaptureCount,
    int PlannedTrashCaptureCount, long PlannedBytes, bool OverQuota, bool AdmissionBlocked,
    bool ImpactAcknowledgementRequired, string[] Warnings);

public sealed record RetentionCapacitySnapshot(
    DateTimeOffset ObservedAt,
    long? QuotaBytes,
    long? HighWatermarkBytes,
    long? RecoveryTargetBytes,
    long UsedBytes,
    long ReservedBytes,
    long ActiveBytes,
    long TrashBytes,
    long TransientBytes,
    long QuarantineBytes,
    long DiskPreviewBytes,
    long FavoriteBytes,
    long ProtectedBytes,
    string PressureState,
    bool AdmissionBlocked,
    bool UnknownAccounting,
    string[] Blockers,
    long PlannedReclamationBytes,
    long PurgedBytes,
    DateTimeOffset? NextEligiblePurgeAt);

public sealed record RetentionHistoryItem(Guid EventId, Guid EpisodeId, long Revision,
    DateTimeOffset ObservedAt, DateTimeOffset CommittedAt, string Kind, string State,
    long UsedBytes, long ReservedBytes, long PlannedReclamationBytes, long PurgedBytes, string[] Blockers);

public sealed record RetentionHistoryPage(RetentionHistoryItem[] Items, string? NextCursor, bool HasGap);

public interface IRetentionCapacityReadStore
{
    Task<RetentionCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken);
    Task<RetentionHistoryPage> GetHistoryAsync(int pageSize, string? cursor, CancellationToken cancellationToken);
}

public enum RetentionOperationStatus { Committed, Conflict, NotFound, Protected, Blocked, Pending, Failed }
public sealed record RetentionOperationResult(RetentionOperationStatus Status, CaptureRetentionSummary? Current,
    string? ErrorCode = null);

public interface ICaptureRetentionReadStore
{
    Task<CaptureRetentionSummary?> GetCaptureSummaryAsync(Guid captureId, DateTimeOffset asOf,
        CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, CaptureRetentionSummary>> GetCaptureSummariesAsync(
        IReadOnlyCollection<Guid> captureIds, DateTimeOffset asOf, CancellationToken cancellationToken);
    Task<RetentionTrashPage> GetTrashPageAsync(int limit, string? cursor, CancellationToken cancellationToken);
}

public interface ICaptureRetentionMutationStore
{
    Task<RetentionOperationResult> TrashAsync(Guid captureId, int expectedRevision, string reason,
        DateTimeOffset now, CancellationToken cancellationToken);
    Task<RetentionOperationResult> RestoreAsync(Guid captureId, int expectedRevision, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<RetentionOperationResult> ResumeAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken);
    Task<RetentionOperationResult> RecordNoDeliveryIntentAsync(Guid captureId, int expectedRevision,
        DateTimeOffset now, CancellationToken cancellationToken);
}
