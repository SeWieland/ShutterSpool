using Microsoft.AspNetCore.WebUtilities;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Server.Api.Destinations;

public sealed record CreateDestinationRequest(string Name, string Kind, string? Secret = null);
public sealed record UpdateDestinationRequest(long ExpectedRevision, string Name);
public sealed record DestinationRemovalResponse(string Outcome);
public sealed record DestinationResponse(Guid Id, string Name, string Kind, bool Enabled, long ConfigurationRevision,
    string? LocalRootPath, string? WebDavCollectionUrl, string? WebDavUsername, bool SecretConfigured, bool Archived)
{
    internal static DestinationResponse From(DestinationConfiguration destination) =>
        new(destination.Id, destination.Name, destination.Kind, destination.Enabled, destination.ConfigurationRevision,
            destination.LocalRootPath, destination.WebDavCollectionUrl, destination.WebDavUsername, destination.SecretConfigured,
            destination.IsArchived);
}
public sealed class DestinationConfigurationConflictProblemDetails : Microsoft.AspNetCore.Mvc.ProblemDetails
{
    public string ErrorCode { get; init; } = "CONFIGURATION_CONFLICT";
    public required DestinationResponse Current { get; init; }
}
public sealed record DestinationConfigurationPageResponse(DestinationResponse[] Items, string? NextCursor);
/// <summary>Destination kinds enabled by the deployment operator.</summary>
public sealed record DestinationCapabilitiesResponse(IReadOnlyList<string> EnabledKinds);

public static class DestinationEndpoints
{
    public static RouteGroupBuilder MapDestinationEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/destinations/capabilities", (DestinationCapabilities capabilities) =>
            Results.Ok(new DestinationCapabilitiesResponse(capabilities.EnabledKinds)))
            .WithSummary("Read destination deployment capabilities").Produces<DestinationCapabilitiesResponse>();
        management.MapGet("/destinations", ListAsync).WithSummary("List configured Destinations").Produces<DestinationConfigurationPageResponse>().ProducesValidationProblem();
        management.MapGet("/destinations/{id:guid}", GetAsync).WithSummary("Read Destination configuration").Produces<DestinationResponse>().ProducesProblem(404);
        management.MapPut("/destinations/{id:guid}", UpdateAsync).WithSummary("Update Destination name")
            .Produces<DestinationResponse>().ProducesValidationProblem().Produces<DestinationConfigurationConflictProblemDetails>(409, "application/problem+json");
        management.MapDelete("/destinations/{id:guid}", RemoveAsync).WithSummary("Remove or archive a Destination")
            .Produces<DestinationRemovalResponse>().ProducesProblem(404)
            .Produces<DestinationConfigurationConflictProblemDetails>(409, "application/problem+json");
        management.MapPost("/destinations", CreateAsync)
            .WithSummary("Create a Destination")
            .WithDescription("Supported kinds are local and webdav. Configure provider settings separately; credentials are write-only.")
            .Produces<DestinationResponse>(StatusCodes.Status201Created).ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        return management;
    }

    private static async Task<IResult> ListAsync(int? limit, string? cursor, DestinationManagementService destinations, CancellationToken token)
    {
        int pageSize = limit ?? 30;
        if (pageSize is < 1 or > 100) return Invalid("Provide a page size from 1 to 100.");
        Guid? after = null;
        if (cursor is not null)
        {
            if (cursor.Length is 0 or > 128) return Invalid("The Destination cursor is invalid.");
            try
            {
                if (!Guid.TryParseExact(System.Text.Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor)), "N", out Guid id))
                    return Invalid("The Destination cursor is invalid.");
                after = id;
            }
            catch (FormatException) { return Invalid("The Destination cursor is invalid."); }
        }
        DestinationConfiguration[] values = await destinations.ListAsync(pageSize, after, token);
        bool more = values.Length > pageSize;
        DestinationConfiguration[] page = [.. values.Take(pageSize)];
        string? next = more ? WebEncoders.Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(page[^1].Id.ToString("N"))) : null;
        return Results.Ok(new DestinationConfigurationPageResponse([.. page.Select(DestinationResponse.From)], next));
    }

    private static async Task<IResult> GetAsync(Guid id, DestinationManagementService destinations, CancellationToken token)
    {
        DestinationConfiguration? destination = await destinations.GetAsync(id, token);
        return destination is null ? Missing() : Results.Ok(DestinationResponse.From(destination));
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateDestinationRequest request,
        DestinationManagementService destinations, CancellationToken token)
    {
        try
        {
            DestinationConfigurationWrite result = await destinations.UpdateNameAsync(id, request.ExpectedRevision, request.Name, token);
            if (result.Current is null) return Missing();
            return result.Applied ? Results.Ok(DestinationResponse.From(result.Current)) : Conflict(result.Current);
        }
        catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["name"] = [exception.Message] }); }
        catch (ConfigurationConflictException) { return Results.Problem(statusCode: 409, title: "Destination name is already in use", extensions: new Dictionary<string, object?> { ["errorCode"] = "DESTINATION_NAME_CONFLICT" }); }
    }

    private static async Task<IResult> RemoveAsync(Guid id, long expectedRevision,
        DestinationManagementService destinations, CancellationToken token)
    {
        DestinationRemovalWrite result = await destinations.RemoveAsync(id, expectedRevision, token);
        if (!result.Applied) return result.Current is null ? Missing() : Conflict(result.Current);
        return Results.Ok(new DestinationRemovalResponse(result.Removed ? "deleted" : "archived"));
    }

    private static async Task<IResult> CreateAsync(CreateDestinationRequest request, DestinationManagementService destinations, CancellationToken cancellationToken)
    {
        try
        {
            DestinationConfiguration created = await destinations.CreateAsync(request.Name, request.Kind, request.Secret, cancellationToken);
            return Results.Created($"/management/destinations/{created.Id}", DestinationResponse.From(created));
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [exception.Message] });
        }
        catch (ConfigurationConflictException) { return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Destination name is already in use"); }
    }

    private static IResult Conflict(DestinationConfiguration current) => Results.Json(new DestinationConfigurationConflictProblemDetails
    {
        Status = 409, Title = "Destination configuration revision conflict", Current = DestinationResponse.From(current),
    }, statusCode: 409, contentType: "application/problem+json");

    private static IResult Missing() => Results.Problem(statusCode: 404, title: "Destination not found");
    private static IResult Invalid(string error) => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = [error] });
}
