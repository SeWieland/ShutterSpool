using Microsoft.AspNetCore.Mvc;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;

namespace ShutterSpool.Server.Api.Destinations;

public sealed record UpdateWebDavDestinationRequest(long ExpectedRevision, string? CollectionUrl, string? Username, string? Password = null);
public sealed record WebDavDestinationResponse(string CollectionUrl, string Username, long ConfigurationRevision);
public sealed record WebDavDestinationTestResponse(bool Writable, string? ErrorCode, long TestedConfigurationRevision);
public sealed class WebDavDestinationConflictProblemDetails : ProblemDetails
{
    public required string ErrorCode { get; init; }
    public DestinationResponse? Current { get; init; }
}

public static class WebDavDestinationEndpoints
{
    public static RouteGroupBuilder MapWebDavDestinationEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/destinations/{id:guid}/webdav", GetAsync).WithSummary("Get WebDAV destination settings")
            .Produces<WebDavDestinationResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        management.MapPut("/destinations/{id:guid}/webdav", UpdateAsync).WithSummary("Configure WebDAV destination")
            .Produces<WebDavDestinationResponse>().ProducesValidationProblem().ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<WebDavDestinationConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        management.MapPost("/destinations/{id:guid}/webdav/test", TestAsync).WithSummary("Test WebDAV destination")
            .Produces<WebDavDestinationTestResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        return management;
    }

    private static async Task<IResult> GetAsync(Guid id, DestinationManagementService destinations, CancellationToken token)
    {
        DestinationConfiguration? destination = await destinations.GetAsync(id, token);
        return destination is not { Kind: "webdav" } || destination.WebDavCollectionUrl is not string url
            || destination.WebDavUsername is not string username
            ? Missing() : Results.Ok(new WebDavDestinationResponse(url, username, destination.ConfigurationRevision));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateWebDavDestinationRequest request,
        DestinationManagementService destinations, IWebDavDestinationConfiguration webdav, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(request.CollectionUrl) || string.IsNullOrWhiteSpace(request.Username))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Collection URL and username are required."] });
        if (await destinations.GetAsync(id, token) is not { Kind: "webdav" }) return Missing();
        string? error = await webdav.ConfigureAsync(id, request.ExpectedRevision, request.CollectionUrl, request.Username, request.Password, token);
        if (error == "CONFIGURATION_CONFLICT")
        {
            DestinationConfiguration? current = await destinations.GetAsync(id, token);
            return current is null ? Missing() : Results.Json(new WebDavDestinationConflictProblemDetails
            { Status = 409, Title = "Destination configuration revision conflict", ErrorCode = error, Current = DestinationResponse.From(current) },
                statusCode: 409, contentType: "application/problem+json");
        }
        if (error == "IDENTITY_LOCKED") return Results.Json(new WebDavDestinationConflictProblemDetails
        { Status = StatusCodes.Status409Conflict, Title = "Destination identity is locked", ErrorCode = error }, statusCode: 409, contentType: "application/problem+json");
        if (error is not null) return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error] });
        DestinationConfiguration updated = (await destinations.GetAsync(id, token))!;
        return Results.Ok(new WebDavDestinationResponse(updated.WebDavCollectionUrl!, updated.WebDavUsername!, updated.ConfigurationRevision));
    }

    private static async Task<IResult> TestAsync(Guid id, DestinationManagementService destinations,
        IWebDavDestinationConfiguration webdav, CancellationToken token)
    {
        if (await destinations.GetAsync(id, token) is not { Kind: "webdav" }) return Missing();
        (bool writable, string? error, long revision) = await webdav.TestWithRevisionAsync(id, token);
        return Results.Ok(new WebDavDestinationTestResponse(writable, error, revision));
    }

    private static IResult Missing() => Results.Problem(statusCode: 404, title: "Destination not found");
}
