using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShutterSpool.Application.Curation;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Server.Security;
using System.Text.Json.Serialization;

namespace ShutterSpool.Server.Api.Captures;

public sealed record UpdateCaptureCurationRequest(int ExpectedRevision, int Rating, string? Label, string[] Tags);
public sealed record CaptureRevisionRequest(int ExpectedRevision);
public sealed record CaptureBulkCurationItemRequest
{
    private string? _label;
    private bool _labelSpecified;

    public required Guid Id { get; init; }
    public required int ExpectedRevision { get; init; }
    public int? Rating { get; init; }
    public string? Label
    {
        get => _label;
        init { _label = value; _labelSpecified = true; }
    }
    [JsonIgnore]
    public bool LabelSpecified => _labelSpecified;
    public string[]? Tags { get; init; }
}
public sealed record CaptureBulkRequest(CaptureBulkCurationItemRequest[] Items);
public sealed record CaptureBulkItemResponse(Guid Id, string Status, CaptureCurationResponse? Current, string? ErrorCode);
public sealed record CaptureBulkResponse(CaptureBulkItemResponse[] Items);
public sealed record CaptureCurationResponse(Guid Id, int Revision, int Rating, string? Label, string[] Tags, string Selection,
    string Policy, int? DelaySeconds, DateTimeOffset? ReleaseDueAt, bool Held, string State,
    DateTimeOffset? ReleasedAt, int? ReleasedRevision, int? ReleasedRating, string? ReleasedLabel, string[] ReleasedTags, string? ReleasedSelection)
{
    internal static CaptureCurationResponse From(CaptureCurationState state) => new(state.Id, state.Revision, state.Rating,
        state.Label, state.Tags, state.Selection.ToString(), state.Policy.ToString(), state.DelaySeconds, state.ReleaseDueAt,
        state.Held, state.State, state.ReleasedAt, state.ReleasedRevision, state.ReleasedRating, state.ReleasedLabel, state.ReleasedTags,
        state.ReleasedSelection?.ToString());
}
public sealed class CaptureConflictProblemDetails : ProblemDetails
{
    public string ErrorCode { get; init; } = "CAPTURE_REVISION_CONFLICT";
    public required CaptureCurationResponse Current { get; init; }
}

public static class CaptureCurationEndpoints
{
    public static RouteGroupBuilder MapCaptureCurationEndpoints(this RouteGroupBuilder management)
    {
        RouteGroupBuilder captures = management.MapGroup("/captures");
        captures.MapGet("/{id:guid}/curation", GetAsync)
            .WithSummary("Read current and released Capture curation")
            .Produces<CaptureCurationResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        captures.MapPut("/{id:guid}/curation", UpdateAsync)
            .WithSummary("Update Capture rating, label and tags")
            .WithDescription("Supply the current Capture revision. A stale revision returns 409 with the current state in the ProblemDetails current extension. Post-release edits update current curation only.")
            .Produces<CaptureCurationResponse>().Produces<CaptureConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        captures.MapPost("/{id:guid}/hold", HoldAsync)
            .WithSummary("Hold an unreleased Capture indefinitely")
            .Produces<CaptureCurationResponse>().Produces<CaptureConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        captures.MapPost("/{id:guid}/restore", RestoreAsync)
            .WithSummary("Restore the Capture's saved Source policy")
            .Produces<CaptureCurationResponse>().Produces<CaptureConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        captures.MapPost("/{id:guid}/release", ReleaseAsync)
            .WithSummary("Release a Capture now")
            .WithDescription("Repeated calls return the existing release decision. A stale revision conflict includes the latest Capture state in the ProblemDetails current extension.")
            .Produces<CaptureCurationResponse>().Produces<CaptureConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound);
        captures.MapPost("/batch/curation", UpdateManyAsync).WithSummary("Update selected Capture curation").Produces<CaptureBulkResponse>().ProducesValidationProblem();
        captures.MapPost("/batch/hold", HoldManyAsync).WithSummary("Hold selected Captures").Produces<CaptureBulkResponse>().ProducesValidationProblem();
        captures.MapPost("/batch/restore", RestoreManyAsync).WithSummary("Restore selected Captures").Produces<CaptureBulkResponse>().ProducesValidationProblem();
        captures.MapPost("/batch/release", ReleaseManyAsync).WithSummary("Release selected Captures").Produces<CaptureBulkResponse>().ProducesValidationProblem();
        return management;
    }

    private static async Task<IResult> GetAsync(Guid id, CaptureCurationService service, CancellationToken cancellationToken)
    {
        CaptureCurationState? state = await service.GetAsync(id, cancellationToken);
        return state is null ? Missing() : Results.Ok(CaptureCurationResponse.From(state));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateCaptureCurationRequest request,
        CaptureCurationService service, LiveChangeNotifier changes, CancellationToken cancellationToken)
    {
        try
        {
            return Mutation(id, await service.UpdateAsync(id, request.ExpectedRevision,
                new(request.Rating, request.Label, request.Tags), cancellationToken), changes, "curation");
        }
        catch (ArgumentException exception)
        {
            return Invalid("request", exception.Message);
        }
    }

    private static async Task<IResult> HoldAsync(Guid id, CaptureRevisionRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, CancellationToken cancellationToken) =>
        request.ExpectedRevision < 0 ? Invalid("expectedRevision", "The Capture revision is invalid.")
            : Mutation(id, await service.HoldAsync(id, request.ExpectedRevision, cancellationToken), changes, "release");

    private static async Task<IResult> RestoreAsync(Guid id, CaptureRevisionRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, CancellationToken cancellationToken) =>
        request.ExpectedRevision < 0 ? Invalid("expectedRevision", "The Capture revision is invalid.")
            : Mutation(id, await service.RestoreAsync(id, request.ExpectedRevision, cancellationToken), changes, "release");

    private static async Task<IResult> ReleaseAsync(Guid id, CaptureRevisionRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, CancellationToken cancellationToken) =>
        request.ExpectedRevision < 0 ? Invalid("expectedRevision", "The Capture revision is invalid.")
            : Mutation(id, await service.ReleaseNowAsync(id, request.ExpectedRevision, cancellationToken), changes, "release");

    private static Task<IResult> UpdateManyAsync(CaptureBulkRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, IAuthorizationService authorization, HttpContext context, CancellationToken token) => BulkAsync(request, service, changes, authorization, context, "curation", token,
            items => service.UpdateManyAsync(items, token));
    private static Task<IResult> HoldManyAsync(CaptureBulkRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, IAuthorizationService authorization, HttpContext context, CancellationToken token) => BulkAsync(request, service, changes, authorization, context, "release", token,
            items => service.HoldManyAsync(items, token));
    private static Task<IResult> RestoreManyAsync(CaptureBulkRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, IAuthorizationService authorization, HttpContext context, CancellationToken token) => BulkAsync(request, service, changes, authorization, context, "release", token,
            items => service.RestoreManyAsync(items, token));
    private static Task<IResult> ReleaseManyAsync(CaptureBulkRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, IAuthorizationService authorization, HttpContext context, CancellationToken token) => BulkAsync(request, service, changes, authorization, context, "release", token,
            items => service.ReleaseManyAsync(items, token));

    private static async Task<IResult> BulkAsync(CaptureBulkRequest request, CaptureCurationService service,
        LiveChangeNotifier changes, IAuthorizationService authorization, HttpContext context, string kind, CancellationToken token,
        Func<CaptureBulkCurationItem[], Task<CaptureBulkResult[]>> apply)
    {
        if (request.Items is null || request.Items.Length is < 1 or > 100 || request.Items.Any(item => item is null)
            || request.Items.Select(item => item.Id).Distinct().Count() != request.Items.Length)
            return Invalid("items", "Provide 1 to 100 distinct Capture IDs.");
        CaptureBulkCurationItem[] items = [.. request.Items.Select(item => new CaptureBulkCurationItem(item.Id,
            item.ExpectedRevision, item.Rating is int rating && item.Tags is not null && item.LabelSpecified
                ? new CaptureCuration(rating, item.Label, item.Tags)
                : null))];
        List<CaptureBulkResult> results = new(items.Length);
        bool accessRevoked = false;
        foreach (CaptureBulkCurationItem item in items)
        {
            if (accessRevoked || !(await authorization.AuthorizeAsync(context.User, context,
                    SecurityExtensions.ManagementAccessPolicy)).Succeeded)
            {
                accessRevoked = true;
                results.Add(new(item.Id, "forbidden", null, "MANAGEMENT_ACCESS_REVOKED"));
                continue;
            }
            results.Add((await apply([item]))[0]);
        }
        foreach (CaptureBulkResult result in results.Where(result => result.Status == "committed"))
            changes.CaptureChanged(result.Id, kind);
        return Results.Ok(new CaptureBulkResponse([.. results.Select(result => new CaptureBulkItemResponse(result.Id,
            result.Status, result.State is null ? null : CaptureCurationResponse.From(result.State), result.ErrorCode))]));
    }

    private static IResult Mutation(Guid captureId, CaptureMutationResult result, LiveChangeNotifier changes, string kind)
    {
        if (result.Succeeded && result.State is not null) changes.CaptureChanged(captureId, kind);
        return result.State is null
        ? Missing()
        : result.Succeeded ? Results.Ok(CaptureCurationResponse.From(result.State))
            : Results.Json(new CaptureConflictProblemDetails
            {
                Status = StatusCodes.Status409Conflict, Title = "Capture revision conflict",
                Current = CaptureCurationResponse.From(result.State),
            }, statusCode: StatusCodes.Status409Conflict, contentType: "application/problem+json");
    }

    private static IResult Missing() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Capture not found");
    private static IResult Invalid(string field, string error) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });
}
