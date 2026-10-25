using Microsoft.AspNetCore.Authorization;
using ShutterSpool.Server.Security;
using System.Security.Claims;

namespace ShutterSpool.Server.Api.Live;

public static class LiveStatusEndpoint
{
    public static async Task StreamAsync(HttpContext context, IAuthorizationService authorization, CancellationToken cancellationToken)
    {
        context.Response.ContentType = "text/event-stream";
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

        do
        {
            if (!HasUnexpiredToken(context.User, DateTimeOffset.UtcNow)) return;
            AuthorizationResult access = await authorization.AuthorizeAsync(context.User, null, SecurityExtensions.ManagementAccessPolicy);
            if (!access.Succeeded) return;

            await context.Response.WriteAsync("event: status\ndata: {}\n\n", cancellationToken);
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    public static bool HasUnexpiredToken(ClaimsPrincipal principal, DateTimeOffset now)
    {
        if (!long.TryParse(principal.FindFirst("exp")?.Value, out long expiration)) return false;
        try { return DateTimeOffset.FromUnixTimeSeconds(expiration) > now; }
        catch (ArgumentOutOfRangeException) { return false; }
    }
}
