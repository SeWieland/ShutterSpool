using Microsoft.AspNetCore.Mvc;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Routing;
using System.Globalization;
using System.Text.Json;

namespace ShutterSpool.Server.Api.Routing;

public sealed record RoutingConditionDto(string Fact, string Operator, JsonElement Value)
{
    internal RoutingCondition ToDomain() => new(Fact, Operator, Fact switch
    {
        "rating" or "size" when Value.ValueKind == JsonValueKind.Number && Value.TryGetDecimal(out decimal number) => new(Number: number),
        "captureTime" when Value.ValueKind == JsonValueKind.String && DateTime.TryParse(Value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out DateTime time) => new(Time: time),
        _ when Value.ValueKind == JsonValueKind.String => new(Text: Value.GetString()),
        _ => new(),
    });

    internal static RoutingConditionDto From(RoutingCondition condition) => new(condition.Fact, condition.Operator,
        condition.Value.Number is decimal number ? JsonSerializer.SerializeToElement(number)
        : condition.Value.Time is DateTime time ? JsonSerializer.SerializeToElement(time)
        : JsonSerializer.SerializeToElement(condition.Value.Text));
}

public sealed record RoutingRuleDto(string Id, string Name, int Order, bool Required, RoutingConditionDto[] Conditions,
    string FolderTemplate, string FilenameTemplate, bool Negate = false, int ConditionMode = 0)
{
    internal RoutingRule ToDomain() => new(Id, Name, Order, Required, [.. Conditions.Select(condition => condition.ToDomain())],
        FolderTemplate, FilenameTemplate, Negate, (RoutingConditionMode)ConditionMode);

    internal static RoutingRuleDto From(RoutingRule rule) => new(rule.Id, rule.Name, rule.Order, rule.Required,
        [.. rule.Conditions.Select(RoutingConditionDto.From)], rule.FolderTemplate, rule.FilenameTemplate,
        rule.Negate, (int)rule.ConditionMode);
}

public sealed record UpdateDestinationRoutingRequest(long ExpectedRevision, bool Enabled, RoutingRuleDto[] Rules);
public sealed record DestinationRoutingResponse(Guid DestinationId, bool Enabled, RoutingRuleDto[] Rules, long ConfigurationRevision)
{
    internal static DestinationRoutingResponse From(DestinationRouting routing) =>
        new(routing.DestinationId, routing.Enabled, [.. routing.Rules.Select(RoutingRuleDto.From)], routing.ConfigurationRevision);
}
public sealed class DestinationRoutingConflictProblemDetails : Microsoft.AspNetCore.Mvc.ProblemDetails
{
    public string ErrorCode { get; init; } = "CONFIGURATION_CONFLICT";
    public required DestinationRoutingResponse Current { get; init; }
}

public sealed record RoutingAttemptResponse(Guid Id, Guid DeliveryId, int RetryNumber, string WorkerId, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt, string? Result, string? ErrorCode, string? ErrorMessage, long? BytesSent,
    string? ConfirmationEvidence, string TraceId)
{
    internal static RoutingAttemptResponse From(RoutingAttemptHistory item) => new(item.Id, item.DeliveryId, item.RetryNumber, item.WorkerId,
        item.StartedAt, item.FinishedAt, item.Result, item.ErrorCode, item.ErrorMessage, item.BytesSent, item.ConfirmationEvidence, item.TraceId);
}
public sealed record RoutingDeliveryResponse(Guid Id, Guid AssetId, Guid DecisionId, Guid DestinationId, string TargetPath, bool Required,
    string State, string? RuleName, string Explanation, DateTimeOffset CreatedAt, Guid? RepeatsDeliveryId,
    DateTimeOffset? NextAttemptAt, string? RetryReason, string? ConfirmationEvidence, Guid? CaptureId = null,
    string? DestinationName = null, Guid? CollectionId = null, string? CollectionName = null, string? CollectionPath = null)
{
    internal static RoutingDeliveryResponse From(RoutingDeliveryHistory item) => new(item.Id, item.AssetId, item.DecisionId, item.DestinationId,
        item.TargetPath, item.Required, item.State, item.RuleName, item.Explanation, item.CreatedAt, item.RepeatsDeliveryId,
        item.NextAttemptAt, item.RetryReason, item.ConfirmationEvidence, item.CaptureId, item.DestinationName,
        item.CollectionId, item.CollectionName, item.CollectionPath);
}
public sealed record RoutingDecisionResponse(Guid Id, Guid AssetId, Guid DestinationId, string Kind, string? RuleId,
    string? RuleName, bool Required, string? TargetPath, string Explanation, DateTimeOffset CreatedAt, bool IsCurrent, int Generation,
    string? DestinationName = null, Guid? CollectionId = null, string? CollectionName = null, string? CollectionPath = null)
{
    internal static RoutingDecisionResponse From(RoutingDecisionHistory item) => new(item.Id, item.AssetId, item.DestinationId,
        item.Kind, item.RuleId, item.RuleName, item.Required, item.TargetPath, item.Explanation, item.CreatedAt, item.IsCurrent,
        item.Generation, item.DestinationName, item.CollectionId, item.CollectionName, item.CollectionPath);
}
public sealed record RoutingActionHistoryResponse(Guid Id, string Kind, Guid AssetId, Guid DestinationId, Guid? SourceDeliveryId,
    Guid? DeliveryId, Guid? SourceDecisionId, Guid? DecisionId, string? TargetPath, string Result,
    DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt)
{
    internal static RoutingActionHistoryResponse From(RoutingActionHistory item) => new(item.Id, item.Kind, item.AssetId,
        item.DestinationId, item.SourceDeliveryId, item.DeliveryId, item.SourceDecisionId, item.DecisionId,
        item.TargetPath, item.Result, item.CreatedAt, item.ConfirmedAt);
}
public sealed record RoutingDecisionPageResponse(RoutingDecisionResponse[] Items, string? NextCursor);
public sealed record RoutingDeliveryPageResponse(RoutingDeliveryResponse[] Items, string? NextCursor);
public sealed record RoutingAttemptPageResponse(RoutingAttemptResponse[] Items, string? NextCursor);
public sealed record RoutingActionPageResponse(RoutingActionHistoryResponse[] Items, string? NextCursor);

public sealed record RoutingActionResponse(string Status, Guid AssetId, Guid DestinationId, string? PreviousTarget,
    string? ProposedTarget, bool Required, string Explanation, string? ConfirmationToken)
{
    internal static RoutingActionResponse From(RoutingActionPreview preview) => new(preview.Status switch
    {
        RoutingActionStatus.NotFound => "not_found",
        RoutingActionStatus.ConfirmationRequired => "confirmation_required",
        RoutingActionStatus.Queued => "queued",
        RoutingActionStatus.AlreadyQueued => "already_queued",
        RoutingActionStatus.BlockedState => "blocked_state",
        RoutingActionStatus.BlockedOriginalUnavailable => "blocked_original_unavailable",
        RoutingActionStatus.BlockedNotReleased => "blocked_not_released",
        RoutingActionStatus.BlockedReconciliation => "blocked_reconciliation",
        RoutingActionStatus.Paused => "paused",
        RoutingActionStatus.StalePreview => "stale_preview",
        RoutingActionStatus.Recorded => "recorded",
        _ => throw new ArgumentOutOfRangeException(nameof(preview)),
    }, preview.AssetId,
        preview.DestinationId, preview.PreviousTarget, preview.ProposedTarget, preview.Required,
        preview.Explanation, preview.ConfirmationToken);
}

public sealed class RoutingConflictProblemDetails : ProblemDetails
{
    public string ErrorCode { get; init; } = "ROUTING_CONFLICT";
    public required RoutingActionResponse Current { get; init; }
}

public sealed record ConfirmRoutingActionRequest(string ConfirmationToken);
