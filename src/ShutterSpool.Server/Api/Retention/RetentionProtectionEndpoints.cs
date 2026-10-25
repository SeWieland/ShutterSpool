using ShutterSpool.Application.Retention;
using System.Security.Claims;

namespace ShutterSpool.Server.Api.Retention;

public sealed record CreateGalleryProtectionRequest(Guid[] AssetIds, DateTimeOffset ExpiresAt);
public sealed record ExtendGalleryProtectionRequest(int ExpectedRevision, DateTimeOffset ExpiresAt);
public sealed record RevokeGalleryAccessRequest(int ExpectedRevision);
public sealed record GalleryProtectionResponse(Guid Id, int Revision, Guid[] AssetIds,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AccessRevokedAt)
{
    internal static GalleryProtectionResponse From(GalleryProtection value) =>
        new(value.Id, value.Revision, value.AssetIds, value.CreatedAt, value.ExpiresAt, value.AccessRevokedAt);
}

public sealed record CreatePreservationHoldRequest(Guid[] AssetIds, string Authority, string Reason,
    DateTimeOffset ReviewAt);
public sealed record ResolvePreservationHoldRequest(int ExpectedRevision, string Resolution);
public sealed record PreservationHoldResponse(Guid Id, int Revision, Guid[] AssetIds, string Authority,
    string Reason, DateTimeOffset CreatedAt, DateTimeOffset ReviewAt, DateTimeOffset? ResolvedAt,
    string? ResolvedBy, string? Resolution)
{
    internal static PreservationHoldResponse From(PreservationHold value) => new(value.Id, value.Revision,
        value.AssetIds, value.Authority, value.Reason, value.CreatedAt, value.ReviewAt, value.ResolvedAt,
        value.ResolvedBy, value.Resolution);
}

public sealed record GalleryProtectionActionResponse(string Status, string? ErrorCode,
    GalleryProtectionResponse? Current);
public sealed record PreservationHoldActionResponse(string Status, string? ErrorCode,
    PreservationHoldResponse? Current);

public static class RetentionProtectionEndpoints
{
    public static RouteGroupBuilder MapRetentionProtectionEndpoints(this RouteGroupBuilder management)
    {
        RouteGroupBuilder gallery = management.MapGroup("/retention/gallery-protections");
        gallery.MapGet("/{id:guid}", GetGalleryAsync).WithSummary("Read a gallery retention promise")
            .Produces<GalleryProtectionResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        gallery.MapPost("/", CreateGalleryAsync).WithSummary("Promise retention for exact available originals")
            .Produces<GalleryProtectionActionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status423Locked);
        gallery.MapPut("/{id:guid}", ExtendGalleryAsync).WithSummary("Extend a gallery retention promise after fresh admission")
            .Produces<GalleryProtectionActionResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict).ProducesProblem(StatusCodes.Status423Locked);
        gallery.MapPost("/{id:guid}/revoke-access", RevokeGalleryAccessAsync)
            .WithSummary("Revoke gallery access without shortening its retention promise")
            .Produces<GalleryProtectionActionResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        RouteGroupBuilder preservation = management.MapGroup("/retention/preservation-holds");
        preservation.MapGet("/{id:guid}", GetPreservationAsync).WithSummary("Read a preservation hold")
            .Produces<PreservationHoldResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        preservation.MapPost("/", CreatePreservationAsync).WithSummary("Protect exact available originals under preservation")
            .Produces<PreservationHoldActionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem().ProducesProblem(StatusCodes.Status423Locked);
        preservation.MapPost("/{id:guid}/resolve", ResolvePreservationAsync)
            .WithSummary("Explicitly resolve a preservation hold")
            .Produces<PreservationHoldActionResponse>().ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);
        return management;
    }

    private static async Task<IResult> GetGalleryAsync(Guid id, AssetProtectionService service,
        CancellationToken cancellationToken)
    {
        GalleryProtection? result = await service.GetGalleryAsync(id, cancellationToken);
        return result is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound,
            title: "Gallery protection not found") : Results.Ok(GalleryProtectionResponse.From(result));
    }

    private static async Task<IResult> CreateGalleryAsync(CreateGalleryProtectionRequest request,
        AssetProtectionService service, CancellationToken cancellationToken)
    {
        try
        {
            ProtectionOperation<GalleryProtection> result = await service.CreateGalleryAsync(request.AssetIds,
                request.ExpiresAt, cancellationToken);
            return GalleryResult(result, created: true);
        }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> ExtendGalleryAsync(Guid id, ExtendGalleryProtectionRequest request,
        AssetProtectionService service, CancellationToken cancellationToken)
    {
        try
        {
            return GalleryResult(await service.ExtendGalleryAsync(id, request.ExpectedRevision,
                request.ExpiresAt, cancellationToken));
        }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> RevokeGalleryAccessAsync(Guid id, RevokeGalleryAccessRequest request,
        AssetProtectionService service, CancellationToken cancellationToken)
    {
        try { return GalleryResult(await service.RevokeGalleryAccessAsync(id, request.ExpectedRevision, cancellationToken)); }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> CreatePreservationAsync(CreatePreservationHoldRequest request,
        AssetProtectionService service, CancellationToken cancellationToken)
    {
        try
        {
            ProtectionOperation<PreservationHold> result = await service.CreatePreservationAsync(request.AssetIds,
                request.Authority, request.Reason, request.ReviewAt, cancellationToken);
            return PreservationResult(result, created: true);
        }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> ResolvePreservationAsync(Guid id, ResolvePreservationHoldRequest request,
        ClaimsPrincipal user, AssetProtectionService service, CancellationToken cancellationToken)
    {
        string? subject = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The operator identity is unavailable");
        try
        {
            return PreservationResult(await service.ResolvePreservationAsync(id, request.ExpectedRevision,
                subject, request.Resolution, cancellationToken));
        }
        catch (ArgumentException exception) { return Invalid("request", exception.Message); }
    }

    private static async Task<IResult> GetPreservationAsync(Guid id, AssetProtectionService service,
        CancellationToken cancellationToken)
    {
        PreservationHold? result = await service.GetPreservationAsync(id, cancellationToken);
        return result is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound,
            title: "Preservation hold not found") : Results.Ok(PreservationHoldResponse.From(result));
    }

    private static IResult GalleryResult(ProtectionOperation<GalleryProtection> result, bool created = false)
    {
        var response = new GalleryProtectionActionResponse(result.Status.ToString(), result.ErrorCode,
            result.Current is null ? null : GalleryProtectionResponse.From(result.Current));
        return result.Status switch
        {
            ProtectionOperationStatus.Committed when created => Results.Created(
                $"/management/retention/gallery-protections/{result.Current!.Id}", response),
            ProtectionOperationStatus.Committed => Results.Ok(response),
            ProtectionOperationStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Gallery protection not found", extensions: new Dictionary<string, object?> { ["result"] = response }),
            ProtectionOperationStatus.Conflict => Results.Conflict(response),
            ProtectionOperationStatus.Blocked => Results.Problem(statusCode: StatusCodes.Status423Locked,
                title: "Original protection is blocked", extensions: new Dictionary<string, object?> { ["result"] = response }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Protection operation failed"),
        };
    }

    private static IResult PreservationResult(ProtectionOperation<PreservationHold> result, bool created = false)
    {
        var response = new PreservationHoldActionResponse(result.Status.ToString(), result.ErrorCode,
            result.Current is null ? null : PreservationHoldResponse.From(result.Current));
        return result.Status switch
        {
            ProtectionOperationStatus.Committed when created => Results.Created(
                $"/management/retention/preservation-holds/{result.Current!.Id}", response),
            ProtectionOperationStatus.Committed => Results.Ok(response),
            ProtectionOperationStatus.NotFound => Results.Problem(statusCode: StatusCodes.Status404NotFound,
                title: "Preservation hold not found", extensions: new Dictionary<string, object?> { ["result"] = response }),
            ProtectionOperationStatus.Conflict => Results.Conflict(response),
            ProtectionOperationStatus.Blocked => Results.Problem(statusCode: StatusCodes.Status423Locked,
                title: "Original protection is blocked", extensions: new Dictionary<string, object?> { ["result"] = response }),
            _ => Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Protection operation failed"),
        };
    }

    private static IResult Invalid(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });
}
