using ShutterSpool.Application.Collections;
using ShutterSpool.Application.Captures;
using ShutterSpool.Application.Retention;
using Microsoft.AspNetCore.Mvc;

namespace ShutterSpool.Server.Api.Captures;

public sealed record MoveCaptureCollectionRequest(int ExpectedRevision, Guid CollectionId);
public sealed record BulkMoveCaptureItemRequest(Guid CaptureId, int ExpectedRevision);
public sealed record BulkMoveCaptureCollectionRequest(Guid TargetCollectionId, BulkMoveCaptureItemRequest[] Items);
public sealed record CaptureCollectionMoveItemResponse(Guid CaptureId, string Status, CaptureDetailResponse? Current);
public sealed record BulkMoveCaptureCollectionResponse(CaptureCollectionMoveItemResponse[] Items);
public sealed class CaptureCollectionConflictProblemDetails : ProblemDetails
{
    public string ErrorCode { get; init; } = "CAPTURE_COLLECTION_CONFLICT";
    public CaptureDetailResponse? Current { get; init; }
}

public static class CaptureCollectionEndpoints
{
    public static RouteGroupBuilder MapCaptureCollectionEndpoints(this RouteGroupBuilder management)
    {
        RouteGroupBuilder captures = management.MapGroup("/captures");
        captures.MapPut("/{id:guid}/collection", MoveOneAsync).WithSummary("Move an active Capture to a Collection")
            .Produces<CaptureDetailResponse>().ProducesValidationProblem().ProducesProblem(404)
            .Produces<CaptureCollectionConflictProblemDetails>(409, "application/problem+json");
        captures.MapPost("/collection-moves", MoveManyAsync).WithSummary("Move selected Captures to a Collection")
            .Produces<BulkMoveCaptureCollectionResponse>().ProducesValidationProblem();
        return management;
    }

    private static async Task<IResult> MoveOneAsync(Guid id, MoveCaptureCollectionRequest request,
        CaptureCollectionAssignmentService assignments, ICaptureRetentionReadStore retention,
        ShutterSpool.Application.Security.WorkspaceAccessContext access, CancellationToken token)
    {
        try
        {
            CollectionMoveResult result = (await assignments.MoveAsync(request.CollectionId,
                [new(id, request.ExpectedRevision)], token))[0];
            return await ToResultAsync(result, retention, access.IsAdministrator, token);
        }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> MoveManyAsync(BulkMoveCaptureCollectionRequest request,
        CaptureCollectionAssignmentService assignments, ICaptureRetentionReadStore retention,
        Microsoft.AspNetCore.Authorization.IAuthorizationService authorization, HttpContext http,
        ShutterSpool.Application.Security.WorkspaceAccessContext access, CancellationToken token)
    {
        if (request.Items is null || request.Items.Length is < 1 or > 100)
            return Invalid("items", "Provide 1 to 100 Captures.");
        if (request.Items.Any(item => item is null))
            return Invalid("items", "Each item must include a Capture ID and revision.");
        if (request.TargetCollectionId == Guid.Empty || request.Items.Any(item => item.CaptureId == Guid.Empty || item.ExpectedRevision < 0))
            return Invalid("items", "Provide an existing target and Capture IDs with non-negative revisions.");
        if (request.Items.Select(item => item.CaptureId).Distinct().Count() != request.Items.Length)
            return Invalid("items", "Capture IDs must be distinct.");
        CollectionMoveResult[] results;
        try
        {
            var admitted = new List<CollectionMoveResult>();
            bool denied = false;
            foreach (BulkMoveCaptureItemRequest item in request.Items)
            {
                if (denied || !(await authorization.AuthorizeAsync(http.User, http,
                    ShutterSpool.Server.Security.SecurityExtensions.ManagementAccessPolicy)).Succeeded)
                {
                    denied = true;
                    admitted.Add(new(item.CaptureId, CollectionMoveStatus.Forbidden, null, null));
                    continue;
                }
                admitted.Add((await assignments.MoveAsync(request.TargetCollectionId, [new(item.CaptureId, item.ExpectedRevision)], token))[0]);
            }
            results = [.. admitted];
        }
        catch (ArgumentException exception) { return Invalid("items", exception.Message); }
        Guid[] captureIds = [.. results.Where(result => result.Current is not null).Select(result => result.CaptureId).Distinct()];
        IReadOnlyDictionary<Guid, CaptureRetentionSummary> summaries = await retention.GetCaptureSummariesAsync(
            captureIds, DateTimeOffset.UtcNow, token);
        var mapped = new List<CaptureCollectionMoveItemResponse>(results.Length);
        foreach (CollectionMoveResult result in results)
            mapped.Add(ToResponse(result, summaries, access.IsAdministrator));
        return Results.Ok(new BulkMoveCaptureCollectionResponse([.. mapped]));
    }

    private static async Task<IResult> ToResultAsync(CollectionMoveResult result,
        ICaptureRetentionReadStore retention, bool includeSourceConfiguration, CancellationToken token)
    {
        CaptureCollectionMoveItemResponse response = ToResponse(result, result.Current is null
            ? new Dictionary<Guid, CaptureRetentionSummary>()
            : await retention.GetCaptureSummariesAsync([result.CaptureId], DateTimeOffset.UtcNow, token), includeSourceConfiguration);
        return result.Status switch
        {
            CollectionMoveStatus.Applied => Results.Ok(response.Current),
            CollectionMoveStatus.NotFound => Results.Problem(statusCode: 404, title: "Capture not found"),
            CollectionMoveStatus.TargetMissing => Results.Problem(statusCode: 404, title: "Collection not found"),
            _ => Results.Json(new CaptureCollectionConflictProblemDetails
            {
                Status = 409,
                Title = "Capture Collection assignment could not be changed.",
                ErrorCode = result.Status == CollectionMoveStatus.NotMovable
                    ? "CAPTURE_COLLECTION_NOT_MOVABLE" : "CAPTURE_COLLECTION_REVISION_CONFLICT",
                Current = response.Current,
            }, statusCode: 409, contentType: "application/problem+json"),
        };
    }

    private static CaptureCollectionMoveItemResponse ToResponse(CollectionMoveResult result,
        IReadOnlyDictionary<Guid, CaptureRetentionSummary> summaries, bool includeSourceConfiguration)
    {
        CaptureDetailResponse? detail = null;
        if (result.Current is not null)
            detail = CaptureDetailResponse.From(result.Current, summaries.GetValueOrDefault(result.CaptureId), includeSourceConfiguration);
        string status = char.ToLowerInvariant(result.Status.ToString()[0]) + result.Status.ToString()[1..];
        return new(result.CaptureId, status, detail);
    }

    private static IResult Invalid(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });
}
