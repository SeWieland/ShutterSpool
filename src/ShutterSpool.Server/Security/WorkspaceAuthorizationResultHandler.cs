using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace ShutterSpool.Server.Security;

public sealed class WorkspaceAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.Items["access-denial"] is string code)
        {
            int status = code switch
            {
                "authorization-unavailable" => StatusCodes.Status503ServiceUnavailable,
                "media-not-found" => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status403Forbidden,
            };
            await Results.Problem(statusCode: status,
                title: code == "membership-revoked" ? "Workspace membership is unavailable" : "Workspace permission denied",
                extensions: new Dictionary<string, object?> { ["errorCode"] = code }).ExecuteAsync(context);
            return;
        }
        await fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
