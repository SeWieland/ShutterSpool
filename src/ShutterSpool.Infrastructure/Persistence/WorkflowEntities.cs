using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class IngestOperationEntity
{
    public Guid Id { get; init; }
    public Guid AssetId { get; init; }
    public Guid SourceId { get; init; }
    public required string OriginalName { get; init; }
    public required string PartialKey { get; init; }
    public required string State { get; set; }
    public string? FinalKey { get; set; }
    public long? Size { get; set; }
    public string? Sha256 { get; set; }
    public string? QuarantineReason { get; set; }
}

internal sealed class AssetEntity
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public required string OriginalName { get; init; }
    public required string SpoolKey { get; init; }
    public long Size { get; init; }
    public required string Sha256 { get; init; }
    public DateTimeOffset IngestedAt { get; init; }
    public Guid RecoveryCorrelation { get; init; }
    public required string State { get; set; }
    public int RetentionPolicyRevision { get; set; } = 1;
    public string MetadataState { get; set; } = "Pending";
    public int MetadataAttemptCount { get; set; }
    public Guid? MetadataLeaseId { get; set; }
    public DateTimeOffset? MetadataLeaseUntil { get; set; }
    public DateTimeOffset? MetadataRetryAt { get; set; }
    public string? MetadataOutcome { get; set; }
    public string? DetectedMediaType { get; set; }
    public string? MetadataRole { get; set; }
    public DateTime? CaptureTimeLocal { get; set; }
    public TimeSpan? CaptureTimeOffset { get; set; }
    public int? CaptureTimeFractionalDigits { get; set; }
    public string? CaptureTimeTag { get; set; }
    public bool CaptureTimeUncertain { get; set; }
    public string? CameraMake { get; set; }
    public string? CameraMakeTag { get; set; }
    public string? CameraModel { get; set; }
    public string? CameraModelTag { get; set; }
    public string? CameraSerial { get; set; }
    public string? CameraSerialTag { get; set; }
    public bool ExtensionMismatch { get; set; }
    public string? MetadataReason { get; set; }
}

internal sealed class CaptureEntity
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public Guid CollectionId { get; set; }
    public Guid? ReleasedCollectionId { get; set; }
    public string? ReleasedCollectionName { get; set; }
    public string? ReleasedCollectionPath { get; set; }
    public required string NormalizedStem { get; init; }
    public int Revision { get; set; }
    public int Rating { get; set; }
    public string? Label { get; set; }
    public string[] Tags { get; set; } = [];
    public CaptureSelection Selection => CaptureCurationSnapshot.SelectionFor(Rating);
    public SourceCurationMode Policy { get; set; }
    public int? DelaySeconds { get; set; }
    public DateTimeOffset LastPrimaryIngestedAt { get; set; }
    public DateTimeOffset? ReleaseDueAt { get; set; }
    public bool Held { get; set; }
    public bool IsFavorite { get; set; }
    public bool NoDeliveryIntent { get; set; }
    public DateTimeOffset? FavoriteQualificationLostAt { get; set; }
    public bool RetentionPaused { get; set; }
    public string RetentionDisposition { get; set; } = "Active";
    public string? TrashReason { get; set; }
    public DateTimeOffset? TrashEnteredAt { get; set; }
    public DateTimeOffset? RecoverableUntil { get; set; }
    public DateTimeOffset? RestoreProtectedUntil { get; set; }
    public int RetentionPolicyRevision { get; set; }
    public bool RetentionFactsDirty { get; set; } = true;
    public int? RetentionTier { get; set; }
    public DateTimeOffset? RetentionPressureEligibleAt { get; set; }
    public DateTimeOffset? RetentionTrashEligibleAt { get; set; }
    public DateTimeOffset? RetentionAttentionAt { get; set; }
    public CaptureState State { get; set; } = CaptureState.CURATING;
    public DateTimeOffset? ReleasedAt { get; set; }
    public int? ReleasedRevision { get; set; }
    public int? ReleasedRating { get; set; }
    public string? ReleasedLabel { get; set; }
    public string[] ReleasedTags { get; set; } = [];
    public CaptureSelection? ReleasedSelection => ReleasedRating is int rating ? CaptureCurationSnapshot.SelectionFor(rating) : null;
    public DateTimeOffset FirstAcceptedAt { get; set; }
    public string? RoutingSnapshotJson { get; set; }
    public List<CaptureAssetEntity> Assets { get; } = [];
}

internal sealed class AssetRemovalOperationEntity
{
    public Guid AssetId { get; init; }
    public Guid CaptureId { get; init; }
    public Guid OperationId { get; init; }
    public required string SpoolKey { get; init; }
    public long ExpectedSize { get; init; }
    public required string StorageMarker { get; init; }
    public string[] KnownTransientKeys { get; init; } = [];
    public required string Scope { get; init; }
    public required string Reason { get; init; }
    public int PolicyRevision { get; init; }
    public required string State { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset AuthorizedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureCode { get; set; }
    public long RemovedBytes { get; set; }
}

internal sealed class RetentionPolicyEntity
{
    public int Id { get; init; } = 1;
    public int Revision { get; set; } = 1;
    public required string PolicyJson { get; set; }
    public string PreviewCacheJson { get; set; } = "{\"cacheMode\":0,\"memoryCacheBytes\":67108864}";
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class RetentionPolicyHistoryEntity
{
    public int Revision { get; init; }
    public required string PolicyJson { get; init; }
    public string PreviewCacheJson { get; init; } = "{\"cacheMode\":0,\"memoryCacheBytes\":67108864}";
    public DateTimeOffset CommittedAt { get; init; }
}

internal sealed class RetentionHistoryEntity
{
    public Guid EventId { get; init; }
    public Guid EpisodeId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public DateTimeOffset CommittedAt { get; init; }
    public required string Kind { get; init; }
    public required string State { get; init; }
    public long UsedBytes { get; init; }
    public long ReservedBytes { get; init; }
    public long PlannedReclamationBytes { get; init; }
    public long PurgedBytes { get; init; }
    public string[] Blockers { get; init; } = [];
}

internal sealed class RetentionPressureStateEntity
{
    public int Id { get; init; } = 1;
    public Guid? EpisodeId { get; set; }
    public long Revision { get; set; }
    public required string State { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? HistoryPrunedBefore { get; set; }
}

internal sealed class CaptureAssetEntity
{
    public Guid AssetId { get; init; }
    public Guid CaptureId { get; init; }
    public required string Role { get; init; }
    public bool IsPrimary { get; init; }
    public Guid? RelatedAssetId { get; init; }
    public required string GroupingReason { get; init; }
    public DateTimeOffset? FavoriteQualificationLostAt { get; set; }
}

internal sealed class PreviewPrewarmEntity
{
    public Guid AssetId { get; init; }
    public Guid CaptureId { get; init; }
    public required string Profile { get; init; }
    public string State { get; set; } = "Pending";
    public int AttemptCount { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
    public int? CompletedWidth { get; set; }
    public int? CompletedHeight { get; set; }
}

internal sealed class DestinationRoutingEntity
{
    public Guid DestinationId { get; init; }
    public required string RulesJson { get; set; }
}

internal sealed class RoutedAssetEntity
{
    public Guid AssetId { get; init; }
    public DateTimeOffset EvaluatedAt { get; init; }
}

internal enum RoutingDecisionKindValue { Matched, NoMatch, InvalidTarget, DestinationDisabled }

internal sealed class RoutingDecisionEntity
{
    public Guid Id { get; init; }
    public Guid CaptureId { get; init; }
    public Guid AssetId { get; init; }
    public Guid DestinationId { get; init; }
    public Guid? CollectionId { get; set; }
    public string? CollectionName { get; set; }
    public string? CollectionPath { get; set; }
    public int Generation { get; init; }
    public bool IsCurrent { get; set; }
    public RoutingDecisionKindValue Kind { get; init; }
    public string? RuleId { get; init; }
    public string? RuleName { get; init; }
    public bool Required { get; init; }
    public string? TargetPath { get; init; }
    public string? TargetPathKey { get; init; }
    public required string Explanation { get; init; }
    public string? RuleSnapshotJson { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}



internal sealed class DeliveryEntity
{
    public Guid Id { get; init; }
    public Guid DecisionId { get; init; }
    public Guid AssetId { get; init; }
    public Guid? SourceAssetId { get; set; }
    public Guid DestinationId { get; init; }
    public required string TargetPath { get; init; }
    public bool Required { get; init; }
    public DeliveryState State { get; set; } = DeliveryState.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? RetryReason { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? ConfirmationEvidence { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public Guid? RepeatsDeliveryId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class DeliveryAttemptEntity
{
    public Guid Id { get; init; }
    public Guid DeliveryId { get; init; }
    public Guid AssetId { get; init; }
    public int RetryNumber { get; init; }
    public required string WorkerId { get; init; }
    public Guid ClaimToken { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Result { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long? BytesSent { get; set; }
    public string? ConfirmationEvidence { get; set; }
    public required string TraceId { get; init; }
}

internal sealed class RoutingActionEntity
{
    public Guid Id { get; init; }
    public required string Kind { get; set; }
    public Guid AssetId { get; init; }
    public Guid DestinationId { get; init; }
    public Guid? SourceDeliveryId { get; init; }
    public Guid? DeliveryId { get; set; }
    public Guid? DecisionId { get; set; }
    public Guid? SourceDecisionId { get; set; }
    public string? ProposedTarget { get; set; }
    public bool Required { get; set; }
    public required string Fingerprint { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? Result { get; set; }
}
