using Microsoft.AspNetCore.Authorization;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Security;
using System.Security.Claims;

namespace ShutterSpool.Server.Security;

public sealed record LocalGrantRequirement(string Permission) : IAuthorizationRequirement;

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
            bool allowed = Enum.IsDefined(member.Role) && (requirement.Permission switch
            {
                WorkspacePolicies.Member => true,
                WorkspacePolicies.Curate => member.Role != WorkspaceRole.Viewer,
                WorkspacePolicies.Administer => WorkspaceAccessPolicy.CanAdminister(member.Role),
                WorkspacePolicies.ManageOwners => member.Role == WorkspaceRole.Owner,
                _ => false,
            });
            if (allowed)
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
