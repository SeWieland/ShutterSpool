using Microsoft.OpenApi;
using ShutterSpool.Application.Preview;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Server.Security;
using System.Security.Cryptography;

namespace ShutterSpool.Server.Api.Previews;

public static class PreviewEndpoint
{
    private const string Placeholder = """
        <svg xmlns="http://www.w3.org/2000/svg" width="480" height="320" viewBox="0 0 480 320" role="img" aria-label="Preview unavailable">
          <rect width="480" height="320" fill="#202936"/>
          <path d="M110 232l78-88 54 56 49-42 79 74z" fill="#46566b"/>
          <circle cx="324" cy="95" r="28" fill="#46566b"/>
        </svg>
        """;

    public static IEndpointRouteBuilder MapPreviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        Describe(endpoints.MapGet("/media/previews/{id}",
            (string id, PreviewService previews, LiveChangeNotifier changes, HttpContext context) => GetAsync(id, "grid", previews, changes, context)));
        Describe(endpoints.MapGet("/media/previews/{id}/{profile}",
            (string id, string profile, int? maximumDimension, PreviewService previews, LiveChangeNotifier changes, HttpContext context) =>
                GetAsync(id, profile, previews, changes, context, maximumDimension)));
        return endpoints;
    }

    private static void Describe(RouteHandlerBuilder route) => route
        .RequireAuthorization(SecurityExtensions.ManagementAccessPolicy)
        .Produces<byte[]>(StatusCodes.Status200OK, contentType: "image/webp", additionalContentTypes: ["image/svg+xml"])
        .Produces(StatusCodes.Status304NotModified)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem()
        .AddOpenApiOperationTransformer((operation, _, _) =>
        {
            foreach (OpenApiMediaType content in operation.Responses!["200"].Content!.Values)
                content.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
            return Task.CompletedTask;
        });

    public static async Task<IResult> GetAsync(string id, string profile, PreviewService previews,
        LiveChangeNotifier changes, HttpContext context, int? maximumDimension = null)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        PreviewProfile? selectedProfile = profile switch
        {
            "grid" => PreviewProfile.Grid,
            "detail" => PreviewProfile.Detail,
            _ => null,
        };
        if (!Guid.TryParse(id, out Guid captureId) || selectedProfile is null)
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Preview not found");

        if (maximumDimension is < 64 or > 4096)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["maximumDimension"] = ["Provide a dimension from 64 to 4096 pixels."] });

        PreviewResult result = await previews.GetAsync(captureId, selectedProfile.Value, context.RequestAborted, maximumDimension);
        context.Response.Headers["X-Preview-State"] = result.State.ToString().ToLowerInvariant();
        if (result.State == PreviewState.Ready)
        {
            if (result.Published && result.SourceAssetId is Guid sourceAssetId)
                changes.PreviewReady(captureId, sourceAssetId, selectedProfile.Value.ToString().ToLowerInvariant());
            string etag = $"\"{Convert.ToHexString(SHA256.HashData(result.Content!))}\"";
            context.Response.Headers.CacheControl = "private, no-cache";
            context.Response.Headers.ETag = etag;
            if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(candidate => candidate.Tag.Value is "*" || candidate.Tag.Value == etag) == true)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Bytes(result.Content!, "image/webp");
        }
        return result.State switch
        {
            PreviewState.Missing => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Preview not found"),
            _ => Results.Text(Placeholder, "image/svg+xml"),
        };
    }
}
