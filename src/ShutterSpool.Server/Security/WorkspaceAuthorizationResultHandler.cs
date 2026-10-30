using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace ShutterSpool.Server.Security;

public sealed class WorkspaceAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler fallback = new();
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden && context.Items["access-denial"] is string code)
        {
            await Results.Problem(statusCode: code == "authorization-unavailable" ? 503 : code == "media-not-found" ? 404 : 403,
                title: code == "membership-revoked" ? "Workspace membership is unavailable" : "Workspace permission denied",
                extensions: new Dictionary<string, object?> { ["errorCode"] = code }).ExecuteAsync(context);
            return;
        }
        await fallback.HandleAsync(next, context, policy, result);
    }
}
