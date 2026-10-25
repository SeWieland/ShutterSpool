namespace ShutterSpool.Server.Security;

public sealed class SecurityAuditMiddleware(RequestDelegate next, ILogger<SecurityAuditMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context);

        if (context.Request.Path.StartsWithSegments("/management") && context.Response.StatusCode == StatusCodes.Status401Unauthorized && !context.Items.ContainsKey("security.authentication-failed"))
        {
            logger.ManagementAuthenticationFailed();
        }
        else if (context.Request.Path.StartsWithSegments("/management") && context.Response.StatusCode == StatusCodes.Status403Forbidden)
        {
            logger.ManagementAuthorizationFailed();
        }
    }
}
