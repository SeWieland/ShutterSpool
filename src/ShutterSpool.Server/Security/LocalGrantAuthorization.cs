using Microsoft.AspNetCore.Authorization;
using ShutterSpool.Application.Security;
using System.Security.Claims;

namespace ShutterSpool.Server.Security;

public sealed class LocalGrantRequirement : IAuthorizationRequirement;

public sealed class LocalGrantAuthorizationHandler(IServiceProvider services)
    : AuthorizationHandler<LocalGrantRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, LocalGrantRequirement requirement)
    {
        string? issuer = context.User.FindFirstValue("iss");
        string? subject = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        HttpContext? http = context.Resource as HttpContext ?? services.GetService<IHttpContextAccessor>()?.HttpContext;
        CancellationToken cancellationToken = http?.RequestAborted ?? CancellationToken.None;
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) return;
        var workspace = services.GetRequiredService<ShutterSpool.Application.Workspaces.WorkspaceContext>();
        if (!workspace.Enabled && !context.User.IsInRole(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value.AdministratorRole))
        {
            if (http is not null) http.Items["access-denial"] = "permission-denied";
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            IAccessGrantStore store = services.GetRequiredService<IAccessGrantStore>();
            WorkspaceMembership? member = await store.GetAsync(issuer, subject, timeout.Token);
            if (member?.IsActive != true)
            {
                if (http is not null) http.Items["access-denial"] = "membership-revoked";
                return;
            }
            WorkspaceAccessContext access = services.GetRequiredService<WorkspaceAccessContext>();
            access.Membership = member;
            access.ViewerPolicy = await store.GetViewerPolicyAsync(timeout.Token);
            string? template = (http?.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (member.Role == WorkspaceRole.Viewer && http?.Request.Method == "GET" && template is not null
                && (template.StartsWith("/media/previews/", StringComparison.Ordinal)
                    || template.StartsWith("/management/captures/{id", StringComparison.Ordinal))
                && Guid.TryParse(http.Request.RouteValues["id"]?.ToString(), out Guid captureId)
                && !await services.GetRequiredService<IWorkspaceMediaScope>().CanReadCaptureAsync(captureId, timeout.Token))
            {
                http.Items["access-denial"] = "media-not-found";
                return;
            }
            if (http is not null && template is not null && WorkspaceEndpointPermissions.Allows(member.Role,
                http.Request.Method, template, workspace.Enabled))
                context.Succeed(requirement);
            else if (http is not null) http.Items["access-denial"] = "permission-denied";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (http is not null) http.Items["access-denial"] = "authorization-unavailable";
        }
    }
}
