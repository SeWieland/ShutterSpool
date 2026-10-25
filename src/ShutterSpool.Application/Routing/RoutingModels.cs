using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Application.Routing;

public sealed record RoutingDeliveryHistory(Guid Id, Guid AssetId, Guid DecisionId, Guid DestinationId, string TargetPath, bool Required, string State,
    string? RuleName, string Explanation, DateTimeOffset CreatedAt, Guid? RepeatsDeliveryId, DateTimeOffset? NextAttemptAt,
    string? RetryReason, string? ConfirmationEvidence, Guid? CaptureId = null, string? DestinationName = null,
    Guid? CollectionId = null, string? CollectionName = null, string? CollectionPath = null);
public sealed record RoutingAttemptHistory(Guid Id, Guid DeliveryId, int RetryNumber, string WorkerId, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string? Result, string? ErrorCode, string? ErrorMessage, long? BytesSent, string? ConfirmationEvidence, string TraceId);
public sealed record RoutingDecisionHistory(Guid Id, Guid AssetId, Guid DestinationId, string Kind, string? RuleId, string? RuleName,
    bool Required, string? TargetPath, string Explanation, DateTimeOffset CreatedAt, bool IsCurrent, int Generation,
    string? DestinationName = null, Guid? CollectionId = null, string? CollectionName = null, string? CollectionPath = null);
public sealed record RoutingActionHistory(Guid Id, string Kind, Guid AssetId, Guid DestinationId, Guid? SourceDeliveryId, Guid? DeliveryId,
    Guid? SourceDecisionId, Guid? DecisionId, string? TargetPath, string Result, DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt);
public sealed record RoutingHistoryPage<T>(T[] Items, DateTimeOffset? NextAt, Guid? NextId);
public sealed record RoutingHistoryQuery(int Limit = 30, DateTimeOffset? Before = null, Guid? BeforeId = null,
    Guid? AssetId = null, Guid? DestinationId = null, DeliveryState? State = null);
public enum RoutingActionStatus
{
    NotFound,
    ConfirmationRequired,
    Queued,
    AlreadyQueued,
    BlockedState,
    BlockedOriginalUnavailable,
    BlockedNotReleased,
    BlockedReconciliation,
    Paused,
    StalePreview,
    Recorded,
}

public sealed record RoutingActionPreview(RoutingActionStatus Status, Guid AssetId, Guid DestinationId, string? PreviousTarget,
    string? ProposedTarget, bool Required, string Explanation, string? ConfirmationToken = null);
