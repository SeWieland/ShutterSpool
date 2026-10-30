using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Server.Api.Routing;

public static class RoutingEndpoints
{
    public static RouteGroupBuilder MapRoutingEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/destinations/{id:guid}/routing", GetAsync).WithSummary("Get destination routing rules")
            .Produces<DestinationRoutingResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        management.MapPut("/destinations/{id:guid}/routing", UpdateAsync).WithSummary("Replace destination routing rules")
            .Produces<DestinationRoutingResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<DestinationRoutingConflictProblemDetails>(409, "application/problem+json");
        management.MapRoutingHistoryEndpoints();
        return management;
    }

    private static async Task<IResult> GetAsync(Guid id, DestinationManagementService destinations,
        DestinationRoutingService routing, CancellationToken cancellationToken)
    {
        if (await destinations.GetAsync(id, cancellationToken) is null) return Missing("Destination");
        DestinationRouting current = await routing.GetAsync(id, cancellationToken) ?? new(id, true, []);
        return Results.Ok(DestinationRoutingResponse.From(current));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateDestinationRoutingRequest request,
        DestinationManagementService destinations, DestinationRoutingService routing, IDeliveryWorkSignal signal,
        DestinationCapabilities capabilities, CancellationToken cancellationToken)
    {
        if (request.Rules is null || request.Rules.Any(rule => rule is null || rule.Conditions is null || rule.Conditions.Any(condition => condition is null)))
            return Invalid("rules", "Provide a valid rule list and condition lists.");
        if (request.Rules.SelectMany(rule => rule.Conditions).Any(condition => condition.Fact == "label"
            && (condition.Operator is "present" or "absent")
            && condition.Value.ValueKind != System.Text.Json.JsonValueKind.Null))
            return Invalid("rules", "Label present and absent conditions require a null value.");
        RoutingRule[] rules = [.. request.Rules.Select(rule => rule.ToDomain())];
        string? error = RoutingRuleEvaluator.Validate(rules);
        if (error is not null) return Invalid("rules", error);
        DestinationConfiguration? destination = await destinations.GetAsync(id, cancellationToken);
        if (destination is null) return Missing("Destination");
        if (!capabilities.IsEnabled(destination.Kind)) return Invalid("destination", "This destination type is disabled in this deployment.");
        var updated = new DestinationRouting(id, request.Enabled, rules);
        DestinationRoutingMutation result = await routing.SetAsync(updated, request.ExpectedRevision, cancellationToken);
        if (result.Current is null) return Missing("Destination");
        if (!result.Applied) return Results.Json(new DestinationRoutingConflictProblemDetails
        {
            Status = 409, Title = "Destination configuration revision conflict", Current = DestinationRoutingResponse.From(result.Current),
        }, statusCode: 409, contentType: "application/problem+json");
        if (request.Enabled) await signal.NotifyAsync(cancellationToken);
        return Results.Ok(DestinationRoutingResponse.From(result.Current));
    }

    private static IResult Missing(string resource) => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: $"{resource} not found");
    private static IResult Invalid(string field, string error) => Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [error] });
}
