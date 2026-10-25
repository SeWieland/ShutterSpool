using ShutterSpool.Application.Retention;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Api.Captures;

public sealed record RetentionRevisionRequest(int ExpectedRevision);
public sealed record TrashCaptureRequest(int ExpectedRevision);
public sealed record PurgeTrashCaptureRequest(int ExpectedRevision, bool Confirm);
public sealed record RetentionActionResponse(string Status, string? ErrorCode, CaptureRetentionSummaryResponse? Current);
public sealed record TrashItemResponse(Guid CaptureId, int CaptureRevision, string DisplayName,
    DateTimeOffset EnteredAt, DateTimeOffset RecoverableUntil, string Reason,
    CaptureRetentionSummaryResponse Retention)
{
    internal static TrashItemResponse From(RetentionTrashItem item) => new(item.CaptureId, item.CaptureRevision,
        item.DisplayName, item.EnteredAt, item.RecoverableUntil, item.Reason,
        CaptureRetentionSummaryResponse.From(item.Retention));
}
public sealed record TrashPageResponse(TrashItemResponse[] Items, string? NextCursor);
public sealed record RetentionCapacityResponse(DateTimeOffset ObservedAt, long? QuotaBytes, long? HighWatermarkBytes,
    long? RecoveryTargetBytes, long UsedBytes, long ReservedBytes, long ActiveBytes, long TrashBytes, long TransientBytes,
    long QuarantineBytes, long DiskPreviewBytes, long FavoriteBytes, long ProtectedBytes, string PressureState,
    bool AdmissionBlocked, bool UnknownAccounting, string[] Blockers, long PlannedReclamationBytes, long PurgedBytes,
    DateTimeOffset? NextEligiblePurgeAt)
{
    internal static RetentionCapacityResponse From(RetentionCapacitySnapshot value) => new(value.ObservedAt,
        value.QuotaBytes, value.HighWatermarkBytes, value.RecoveryTargetBytes, value.UsedBytes, value.ReservedBytes,
        value.ActiveBytes, value.TrashBytes, value.TransientBytes, value.QuarantineBytes, value.DiskPreviewBytes,
        value.FavoriteBytes, value.ProtectedBytes, value.PressureState, value.AdmissionBlocked, value.UnknownAccounting,
        value.Blockers, value.PlannedReclamationBytes, value.PurgedBytes, value.NextEligiblePurgeAt);
}
public sealed record RetentionHistoryItemResponse(Guid EventId, Guid EpisodeId, long Revision, DateTimeOffset ObservedAt,
    DateTimeOffset CommittedAt, string Kind, string State, long UsedBytes, long ReservedBytes,
    long PlannedReclamationBytes, long PurgedBytes, string[] Blockers)
{
    internal static RetentionHistoryItemResponse From(RetentionHistoryItem value) => new(value.EventId, value.EpisodeId,
        value.Revision, value.ObservedAt, value.CommittedAt, value.Kind, value.State, value.UsedBytes,
        value.ReservedBytes, value.PlannedReclamationBytes, value.PurgedBytes, value.Blockers);
}
public sealed record RetentionHistoryPageResponse(RetentionHistoryItemResponse[] Items, string? NextCursor, bool HasGap)
{
    internal static RetentionHistoryPageResponse From(RetentionHistoryPage value) => new([.. value.Items.Select(RetentionHistoryItemResponse.From)],
        value.NextCursor, value.HasGap);
}

public static class CaptureRetentionEndpoints
{
    public static RouteGroupBuilder MapCaptureRetentionEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/trash", GetTrashAsync).WithSummary("List recoverable Trash")
            .Produces<TrashPageResponse>().ProducesValidationProblem();
        management.MapGet("/retention/capacity", GetCapacityAsync).WithSummary("Read current retention and spool capacity")
            .Produces<RetentionCapacityResponse>();
        management.MapGet("/retention/history", GetHistoryAsync).WithSummary("Read retention pressure history")
            .Produces<RetentionHistoryPageResponse>().ProducesValidationProblem();
        management.MapPost("/captures/{id:guid}/trash", TrashAsync).WithSummary("Move a Capture into recoverable Trash")
            .Produces<RetentionActionResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        management.MapPost("/trash/{id:guid}/restore", RestoreAsync).WithSummary("Restore a Capture from Trash")
            .Produces<RetentionActionResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        management.MapPost("/trash/{id:guid}/purge", PurgeAsync).WithSummary("Permanently delete a Capture from Trash")
            .Produces<RetentionActionResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status202Accepted);
        management.MapPost("/captures/{id:guid}/resume", ResumeAsync).WithSummary("Resume existing unfinished Capture work")
            .Produces<RetentionActionResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        management.MapPost("/captures/{id:guid}/no-delivery-intent", RecordNoDeliveryIntentAsync)
            .WithSummary("Record that an unmatched Capture has no intended delivery")
            .Produces<RetentionActionResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status409Conflict);
        return management;
    }

    private static async Task<RetentionCapacityResponse> GetCapacityAsync(RetentionCapacityReadService service,
        CancellationToken cancellationToken) => RetentionCapacityResponse.From(await service.GetCapacityAsync(cancellationToken));

    private static async Task<IResult> GetHistoryAsync(int? pageSize, string? cursor,
        RetentionCapacityReadService service, CancellationToken cancellationToken)
    {
        try { return Results.Ok(RetentionHistoryPageResponse.From(await service.GetHistoryAsync(pageSize ?? 30, cursor, cancellationToken))); }
        catch (ArgumentOutOfRangeException exception) { return Invalid("pageSize", exception.Message); }
        catch (ArgumentException exception) { return Invalid("cursor", exception.Message); }
    }

    private static async Task<IResult> GetTrashAsync(int? pageSize, string? cursor, ICaptureRetentionReadStore store,
        CancellationToken cancellationToken)
    {
        int limit = pageSize ?? 30;
        if (limit is < 1 or > 100) return Invalid("pageSize", "Provide a page size from 1 to 100.");
        try
        {
            RetentionTrashPage page = await store.GetTrashPageAsync(limit, cursor, cancellationToken);
            return Results.Ok(new TrashPageResponse([.. page.Items.Select(TrashItemResponse.From)], page.NextCursor));
        }
        catch (ArgumentException exception) { return Invalid("cursor", exception.Message); }
    }

    private static Task<IResult> TrashAsync(Guid id, TrashCaptureRequest request,
        CaptureRetentionMutationService service, LiveChangeNotifier changes, CancellationToken cancellationToken) =>
        MutateAsync(id, () => service.TrashAsync(id, request.ExpectedRevision, "manual", cancellationToken), changes);

    private static Task<IResult> RestoreAsync(Guid id, RetentionRevisionRequest request,
        CaptureRetentionMutationService service, LiveChangeNotifier changes, CancellationToken cancellationToken) =>
        MutateAsync(id, () => service.RestoreAsync(id, request.ExpectedRevision, cancellationToken), changes);

    private static Task<IResult> ResumeAsync(Guid id, RetentionRevisionRequest request,
        CaptureRetentionMutationService service, LiveChangeNotifier changes, CancellationToken cancellationToken) =>
        MutateAsync(id, () => service.ResumeAsync(id, request.ExpectedRevision, cancellationToken), changes);

    private static Task<IResult> RecordNoDeliveryIntentAsync(Guid id, RetentionRevisionRequest request,
        CaptureRetentionMutationService service, LiveChangeNotifier changes, CancellationToken cancellationToken) =>
        MutateAsync(id, () => service.RecordNoDeliveryIntentAsync(id, request.ExpectedRevision, cancellationToken), changes);

    private static async Task<IResult> PurgeAsync(Guid id, PurgeTrashCaptureRequest request,
        RetentionRemovalService service, LiveChangeNotifier changes, CancellationToken cancellationToken)
    {
        RetentionOperationResult result;
        try { result = await service.DeletePermanentlyAsync(id, request.ExpectedRevision, request.Confirm, cancellationToken); }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
        var response = new RetentionActionResponse(result.Status.ToString(), result.ErrorCode,
            result.Current is null ? null : CaptureRetentionSummaryResponse.From(result.Current));
        if (result.Status is RetentionOperationStatus.Committed or RetentionOperationStatus.Pending)
            changes.CaptureChanged(id, "retention");
        return result.Status switch
        {
            RetentionOperationStatus.Committed => Results.Ok(response),
            RetentionOperationStatus.Pending => Results.Accepted(value: response),
            RetentionOperationStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Capture not found", extensions: new Dictionary<string, object?> { ["result"] = response }),
            RetentionOperationStatus.Conflict or RetentionOperationStatus.Protected => Results.Conflict(response),
            RetentionOperationStatus.Blocked => Results.Problem(statusCode: StatusCodes.Status423Locked,
                title: "Permanent deletion is blocked", extensions: new Dictionary<string, object?> { ["result"] = response }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Permanent deletion failed"),
        };
    }

    private static async Task<IResult> MutateAsync(Guid captureId, Func<Task<RetentionOperationResult>> operation,
        LiveChangeNotifier changes)
    {
        RetentionOperationResult result;
        try { result = await operation(); }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
        var response = new RetentionActionResponse(result.Status.ToString(), result.ErrorCode,
            result.Current is null ? null : CaptureRetentionSummaryResponse.From(result.Current));
        if (result.Status == RetentionOperationStatus.Committed) changes.CaptureChanged(captureId, "retention");
        return result.Status switch
        {
            RetentionOperationStatus.Committed => Results.Ok(response),
            RetentionOperationStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Capture not found", extensions: new Dictionary<string, object?> { ["result"] = response }),
            RetentionOperationStatus.Conflict or RetentionOperationStatus.Protected => Results.Conflict(response),
            RetentionOperationStatus.Blocked => Results.Problem(statusCode: StatusCodes.Status423Locked,
                title: "Retention operation is blocked", extensions: new Dictionary<string, object?> { ["result"] = response }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Retention operation failed"),
        };
    }

    private static IResult Invalid(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });
}
