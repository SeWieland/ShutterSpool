using Microsoft.AspNetCore.Authorization;
using ShutterSpool.Application.Security;
using System.Security.Claims;

namespace ShutterSpool.Server.Security;

public sealed class LocalGrantRequirement : IAuthorizationRequirement;

public sealed class LocalGrantAuthorizationHandler(AccessGrantService grants)
    : AuthorizationHandler<LocalGrantRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, LocalGrantRequirement requirement)
    {
        string? issuer = context.User.FindFirstValue("iss");
        string? subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        CancellationToken cancellationToken = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;
        if (await grants.IsAllowedAsync(issuer, subject, cancellationToken)) context.Succeed(requirement);
    }
}
