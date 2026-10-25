using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Mvc;
using ShutterSpool.Application.Collections;
using System.Text;

namespace ShutterSpool.Server.Api.Collections;

public sealed record CreateCollectionRequest(string Name);
public sealed record RenameCollectionRequest(long ExpectedRevision, string Name);
public sealed record CollectionResponse(Guid Id, string Name, string Path, long Revision, bool IsBuiltIn,
    int SourceReferences, int CaptureReferences)
{
    internal static CollectionResponse From(CollectionSummary item) => new(item.Id, item.Name, item.Path,
        item.Revision, item.IsBuiltIn, item.SourceReferences, item.CaptureReferences);
}
public sealed record CollectionPageResponse(CollectionResponse[] Items, string? NextCursor);
public sealed class CollectionConflictProblemDetails : ProblemDetails
{
    public string ErrorCode { get; init; } = "COLLECTION_CONFLICT";
    public CollectionResponse? Current { get; init; }
    public bool Referenced { get; init; }
}

public static class CollectionEndpoints
{
    public static RouteGroupBuilder MapCollectionEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/collections", ListAsync).WithSummary("Search and page Collections")
            .Produces<CollectionPageResponse>().ProducesValidationProblem();
        management.MapGet("/collections/{id:guid}", GetAsync).WithSummary("Read a Collection")
            .Produces<CollectionResponse>().ProducesProblem(404);
        management.MapPost("/collections", CreateAsync).WithSummary("Create a Collection")
            .Produces<CollectionResponse>(201).ProducesValidationProblem().ProducesProblem(409);
        management.MapPut("/collections/{id:guid}", RenameAsync).WithSummary("Rename a Collection")
            .Produces<CollectionResponse>().ProducesValidationProblem().ProducesProblem(404)
            .Produces<CollectionConflictProblemDetails>(409, "application/problem+json");
        management.MapDelete("/collections/{id:guid}", DeleteAsync).WithSummary("Delete an unreferenced Collection")
            .ProducesProblem(404).Produces<CollectionConflictProblemDetails>(409, "application/problem+json");
        return management;
    }

    private static async Task<IResult> GetAsync(Guid id, CollectionManagementService collections, CancellationToken token)
    {
        CollectionSummary? item = await collections.GetAsync(id, token);
        return item is null ? Results.Problem(statusCode: 404, title: "Collection not found")
            : Results.Ok(CollectionResponse.From(item));
    }

    private static async Task<IResult> ListAsync(string? q, int? limit, string? cursor,
        CollectionManagementService collections, CancellationToken token)
    {
        (string? path, Guid? id) = (null, null);
        if (cursor is not null && !TryCursor(cursor, q, out path, out id)) return Invalid("cursor", "The Collection cursor is invalid for this search.");
        try
        {
            CollectionPage page = await collections.ListAsync(q, limit ?? 30, path, id, token);
            string? next = page.NextCursor is null ? null : WebEncoders.Base64UrlEncode(
                Encoding.UTF8.GetBytes($"{SearchKey(q)}\n{page.NextCursor}"));
            return Results.Ok(new CollectionPageResponse([.. page.Items.Select(CollectionResponse.From)], next));
        }
        catch (ArgumentException exception) { return Invalid("q", exception.Message); }
    }

    private static async Task<IResult> CreateAsync(CreateCollectionRequest request, CollectionManagementService collections,
        CancellationToken token)
    {
        try
        {
            CollectionSummary created = await collections.CreateAsync(request.Name, token);
            return Results.Created($"/management/collections/{created.Id}", CollectionResponse.From(created));
        }
        catch (ArgumentException exception) { return Invalid("name", exception.Message); }
        catch (InvalidOperationException exception)
        {
            return Results.Json(new CollectionConflictProblemDetails
            { Status = 409, Title = exception.Message, ErrorCode = "COLLECTION_NAME_CONFLICT" },
                statusCode: 409, contentType: "application/problem+json");
        }
    }

    private static async Task<IResult> RenameAsync(Guid id, RenameCollectionRequest request,
        CollectionManagementService collections, CancellationToken token)
    {
        try
        {
            CollectionWrite result = await collections.RenameAsync(id, request.ExpectedRevision, request.Name, token);
            if (result.Current is null) return Results.Problem(statusCode: 404, title: "Collection not found");
            return result.Applied ? Results.Ok(CollectionResponse.From(result.Current))
                : Conflict(result.Current, result.NameConflict ? "COLLECTION_NAME_CONFLICT" : "COLLECTION_REVISION_CONFLICT");
        }
        catch (ArgumentException exception) { return Invalid("name", exception.Message); }
        catch (InvalidOperationException exception)
        {
            return Results.Json(new CollectionConflictProblemDetails
            { Status = 409, Title = exception.Message, ErrorCode = "COLLECTION_NAME_CONFLICT" },
                statusCode: 409, contentType: "application/problem+json");
        }
    }

    private static async Task<IResult> DeleteAsync(Guid id, long expectedRevision,
        CollectionManagementService collections, CancellationToken token)
    {
        CollectionWrite result = await collections.DeleteAsync(id, expectedRevision, token);
        if (result.Applied) return Results.NoContent();
        if (result.Current is null) return Results.Problem(statusCode: 404, title: "Collection not found");
        return Conflict(result.Current, result.Referenced ? "COLLECTION_REFERENCED" : "COLLECTION_REVISION_CONFLICT", result.Referenced);
    }

    private static IResult Invalid(string field, string error) => Results.ValidationProblem(
        new Dictionary<string, string[]> { [field] = [error] });

    private static IResult Conflict(CollectionSummary current, string code, bool referenced = false) => Results.Json(
        new CollectionConflictProblemDetails { Status = 409, Title = "Collection could not be changed.",
            ErrorCode = code, Current = CollectionResponse.From(current), Referenced = referenced },
        statusCode: 409, contentType: "application/problem+json");

    private static string SearchKey(string? query)
    {
        try { return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes((query ?? string.Empty).Normalize(NormalizationForm.FormC).ToUpperInvariant()))); }
        catch (ArgumentException) { return "invalid"; }
    }

    private static bool TryCursor(string cursor, string? query, out string? path, out Guid? id)
    {
        path = null; id = null;
        if (cursor.Length is 0 or > 8_192) return false;
        try
        {
            string value = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor));
            int searchSplit = value.IndexOf('\n');
            if (searchSplit <= 0 || !string.Equals(value[..searchSplit], SearchKey(query), StringComparison.Ordinal)) return false;
            string cursorValue = value[(searchSplit + 1)..];
            int split = cursorValue.LastIndexOf('|');
            if (split is <= 0 or > 1_024
                || !Guid.TryParseExact(cursorValue[(split + 1)..], "N", out Guid parsed)) return false;
            path = cursorValue[..split]; id = parsed; return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
    }
}
