using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Routing;
using ShutterSpool.Server.Api.Routing;

namespace ShutterSpool.Server.Api.Deliveries;

public static class DeliveryEndpoints
{
    public static RouteGroupBuilder MapDeliveryEndpoints(this RouteGroupBuilder management)
    {
        management.MapPost("/deliveries/{id:guid}/retry", RetryAsync).WithSummary("Retry unfinished delivery work")
            .Produces<RoutingActionResponse>().ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<RoutingConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        management.MapPost("/deliveries/{id:guid}/resend/preview", PreviewResendAsync)
            .WithSummary("Preview a repeat of a successful delivery")
            .Produces<RoutingActionResponse>().ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<RoutingConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        management.MapPost("/deliveries/{id:guid}/resend", ResendAsync)
            .WithSummary("Repeat a successful delivery after confirmation")
            .Produces<RoutingActionResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<RoutingConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        management.MapGet("/assets/{assetId:guid}/destinations/{destinationId:guid}/reevaluate-preview", PreviewReevaluateAsync)
            .WithSummary("Preview routing with current Capture curation and destination rules")
            .Produces<RoutingActionResponse>().ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<RoutingConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        management.MapPost("/assets/{assetId:guid}/destinations/{destinationId:guid}/reevaluate", ReevaluateAsync)
            .WithSummary("Record a new routing decision after confirmation")
            .Produces<RoutingActionResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<RoutingConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        return management;
    }

    private static async Task<IResult> RetryAsync(Guid id, CaptureRoutingActionService actions, IDeliveryWorkSignal signal,
        CancellationToken cancellationToken) => await Respond(await actions.RetryAsync(id, cancellationToken), signal, cancellationToken);

    private static async Task<IResult> PreviewResendAsync(Guid id, CaptureRoutingActionService actions,
        CancellationToken cancellationToken) => Respond(await actions.PreviewResendAsync(id, cancellationToken));

    private static async Task<IResult> ResendAsync(Guid id, ConfirmRoutingActionRequest request,
        CaptureRoutingActionService actions, IDeliveryWorkSignal signal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConfirmationToken)) return InvalidConfirmation();
        return await Respond(await actions.ResendAsync(id, request.ConfirmationToken, cancellationToken), signal, cancellationToken, confirming: true);
    }

    private static async Task<IResult> PreviewReevaluateAsync(Guid assetId, Guid destinationId,
        CaptureRoutingActionService actions, CancellationToken cancellationToken) =>
        Respond(await actions.PreviewReevaluateAsync(assetId, destinationId, cancellationToken));

    private static async Task<IResult> ReevaluateAsync(Guid assetId, Guid destinationId, ConfirmRoutingActionRequest request,
        CaptureRoutingActionService actions, IDeliveryWorkSignal signal, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConfirmationToken)) return InvalidConfirmation();
        return await Respond(await actions.ReevaluateAsync(assetId, destinationId, request.ConfirmationToken, cancellationToken),
            signal, cancellationToken, confirming: true);
    }

    private static IResult Respond(RoutingActionPreview result, bool confirming = false)
    {
        RoutingActionResponse current = RoutingActionResponse.From(result);
        if (result.Status == RoutingActionStatus.NotFound)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Routing work not found");
        if (result.Status is RoutingActionStatus.BlockedState or RoutingActionStatus.BlockedOriginalUnavailable
            or RoutingActionStatus.BlockedNotReleased or RoutingActionStatus.BlockedReconciliation
            or RoutingActionStatus.Paused or RoutingActionStatus.StalePreview
            || (confirming && result.Status == RoutingActionStatus.ConfirmationRequired))
            return Results.Json(new RoutingConflictProblemDetails
            {
                Status = StatusCodes.Status409Conflict, Title = "Routing action conflict", Current = current,
            }, statusCode: StatusCodes.Status409Conflict, contentType: "application/problem+json");
        return Results.Ok(current);
    }

    private static async Task<IResult> Respond(RoutingActionPreview result, IDeliveryWorkSignal signal,
        CancellationToken cancellationToken, bool confirming = false)
    {
        if (result.Status == RoutingActionStatus.Queued) await signal.NotifyAsync(cancellationToken);
        return Respond(result, confirming);
    }

    private static IResult InvalidConfirmation() => Results.ValidationProblem(new Dictionary<string, string[]>
        { ["confirmationToken"] = ["A confirmation token is required."] });
}
