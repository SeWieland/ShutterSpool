using ShutterSpool.Application.Retention;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Api.Captures;

public sealed record CaptureFavoriteRequest(int ExpectedRevision, bool IsFavorite);
public sealed record CaptureFavoriteResponse(Guid CaptureId, int Revision, bool IsFavorite, string Disposition,
    CaptureRetentionSummaryResponse? Retention);

public static class CaptureFavoriteEndpoints
{
    public static RouteGroupBuilder MapCaptureFavoriteEndpoints(this RouteGroupBuilder management)
    {
        management.MapPut("/captures/{id:guid}/favorite", SetAsync)
            .WithSummary("Set the Capture retention Favorite")
            .Produces<CaptureFavoriteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesValidationProblem();
        return management;
    }

    private static async Task<IResult> SetAsync(Guid id, CaptureFavoriteRequest request, CaptureFavoriteService favorites,
        ICaptureRetentionReadStore retention, LiveChangeNotifier changes, CancellationToken cancellationToken)
    {
        CaptureFavoriteMutation mutation;
        try { mutation = await favorites.SetAsync(id, request.ExpectedRevision, request.IsFavorite, cancellationToken); }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["expectedRevision"] = [exception.Message] });
        }
        if (mutation.State is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Capture not found");
        CaptureRetentionSummary? summary = await retention.GetCaptureSummaryAsync(id, DateTimeOffset.UtcNow, cancellationToken);
        CaptureFavoriteResponse current = new(mutation.State.CaptureId, mutation.State.Revision,
            mutation.State.IsFavorite, mutation.State.Disposition,
            summary is null ? null : CaptureRetentionSummaryResponse.From(summary));
        if (!mutation.Succeeded)
            return Results.Conflict(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = mutation.ErrorCode == "REVISION_CONFLICT" ? "Capture revision conflict" : "Capture retention state changed",
                Extensions = { ["errorCode"] = mutation.ErrorCode, ["current"] = current },
            });
        changes.CaptureChanged(id, "retention");
        return Results.Ok(current);
    }
}
