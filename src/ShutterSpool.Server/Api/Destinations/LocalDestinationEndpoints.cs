using Microsoft.AspNetCore.Mvc;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;

namespace ShutterSpool.Server.Api.Destinations;

public sealed record UpdateLocalDestinationRequest(long ExpectedRevision, string RootPath);
public sealed record LocalDestinationResponse(string RootPath, long ConfigurationRevision);
public sealed record LocalDestinationTestResponse(bool Writable, string? ErrorCode, long TestedConfigurationRevision);
public sealed record LocalDirectoryListingResponse(string? Path, string? ParentPath, IReadOnlyList<LocalDirectoryResponse> Directories, bool Truncated);
public sealed record LocalDirectoryResponse(string Name, string Path);
public sealed class LocalDestinationConflictProblemDetails : ProblemDetails
{
    public required string ErrorCode { get; init; }
    public DestinationResponse? Current { get; init; }
}

public static class LocalDestinationEndpoints
{
    public static RouteGroupBuilder MapLocalDestinationEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/destinations/{id:guid}/local", GetAsync).WithSummary("Get local destination root")
            .Produces<LocalDestinationResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        management.MapPut("/destinations/{id:guid}/local", UpdateAsync).WithSummary("Configure local destination root")
            .Produces<LocalDestinationResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces<LocalDestinationConflictProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json");
        management.MapPost("/destinations/{id:guid}/local/test", TestAsync).WithSummary("Test local destination root")
            .Produces<LocalDestinationTestResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        management.MapGet("/destinations/local-folders", BrowseAsync).WithSummary("Browse local destination folders")
            .Produces<LocalDirectoryListingResponse>().ProducesValidationProblem();
        return management;
    }

    private static async Task<IResult> BrowseAsync([FromQuery] string? path, ILocalDestinationConfiguration local,
        CancellationToken cancellationToken)
    {
        LocalDirectoryListingResult result = await local.BrowseAsync(path, cancellationToken);
        if (result.Listing is not { } listing)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["path"] = [result.Error ?? "The directory cannot be read."] },
                detail: result.Error ?? "The directory cannot be read.");
        return Results.Ok(new LocalDirectoryListingResponse(listing.Path, listing.ParentPath,
            listing.Directories.Select(entry => new LocalDirectoryResponse(entry.Name, entry.Path)).ToArray(), listing.Truncated));
    }

    private static async Task<IResult> GetAsync(Guid id, DestinationManagementService destinations, CancellationToken cancellationToken)
    {
        DestinationConfiguration? config = await destinations.GetAsync(id, cancellationToken);
        if (config is not { Kind: "local" }) return Missing();
        string? root = config.LocalRootPath;
        return root is null ? Missing() : Results.Ok(new LocalDestinationResponse(root, config.ConfigurationRevision));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateLocalDestinationRequest request,
        DestinationManagementService destinations, ILocalDestinationConfiguration local, CancellationToken cancellationToken)
    {
        if (await destinations.GetAsync(id, cancellationToken) is null) return Missing();
        if (string.IsNullOrWhiteSpace(request.RootPath))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rootPath"] = ["A local destination root is required."] });
        string? error = await local.ConfigureAsync(id, request.ExpectedRevision, request.RootPath, cancellationToken);
        if (error == "CONFIGURATION_CONFLICT")
        {
            DestinationConfiguration? current = await destinations.GetAsync(id, cancellationToken);
            return current is null ? Missing() : Results.Json(new LocalDestinationConflictProblemDetails
            { Status = 409, Title = "Destination configuration revision conflict", ErrorCode = error, Current = DestinationResponse.From(current) },
                statusCode: 409, contentType: "application/problem+json");
        }
        if (error == "ROOT_LOCKED")
            return Results.Json(new LocalDestinationConflictProblemDetails
            {
                Status = StatusCodes.Status409Conflict, Title = "Destination root is locked", Detail = "The destination root cannot change after delivery work has been recorded.", ErrorCode = error,
            }, statusCode: StatusCodes.Status409Conflict, contentType: "application/problem+json");
        if (error is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["rootPath"] = [error] },
                detail: RootErrorDetail(error));
        DestinationConfiguration updated = (await destinations.GetAsync(id, cancellationToken))!;
        return Results.Ok(new LocalDestinationResponse(updated.LocalRootPath!, updated.ConfigurationRevision));
    }

    private static async Task<IResult> TestAsync(Guid id, DestinationManagementService destinations,
        ILocalDestinationConfiguration local, CancellationToken cancellationToken)
    {
        if (await destinations.GetAsync(id, cancellationToken) is null) return Missing();
        (bool writable, string? errorCode, long revision) = await local.TestWithRevisionAsync(id, cancellationToken);
        return Results.Ok(new LocalDestinationTestResponse(writable, errorCode, revision));
    }

    private static IResult Missing() => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Destination not found");

    private static string RootErrorDetail(string error) => error switch
    {
        "INVALID_ROOT" => "Enter an absolute directory path.",
        "ROOT_UNAVAILABLE" => "The directory must already exist, and the server must have permission to use it.",
        "PERMISSION_DENIED" => "The server does not have permission to use this directory.",
        "UNSAFE_PATH" => "The directory path crosses a redirected or symbolic link.",
        "UNSUPPORTED_PUBLICATION" => "The filesystem does not support safe local delivery.",
        _ => "The local destination root could not be configured.",
    };
}
