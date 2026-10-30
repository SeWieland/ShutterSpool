using ShutterSpool.Application.Security;
using ShutterSpool.Server.Api.Live;
using System.Security.Claims;

namespace ShutterSpool.Server.Api.Workspaces;

public sealed record WorkspaceCapabilitiesResponse(bool CanCurate, bool CanAdminister, bool CanManageMembers, bool CanManageOwners);
public sealed record WorkspaceViewerPolicyResponse(bool IncludeUnreleased, long Revision);
public sealed record WorkspaceAccessResponse(Guid MembershipId, string Role, long Revision,
    WorkspaceCapabilitiesResponse Capabilities, WorkspaceViewerPolicyResponse ViewerPolicy);
public sealed record WorkspaceMemberResponse(Guid Id, string Issuer, string Subject, string Role, long Revision, bool IsActive)
{
    internal static WorkspaceMemberResponse From(WorkspaceMembership item) => new(item.Id, item.Issuer, item.Subject,
        item.Role.ToString(), item.Revision, item.IsActive);
}
public sealed record WorkspaceMembersResponse(WorkspaceMemberResponse[] Items);
public sealed record SafeDeliveryResponse(Guid Id, Guid AssetId, string State, bool CanRetry);
public sealed record SafeDeliverySummaryResponse(SafeDeliveryResponse[] Items, bool Truncated);
public sealed record WorkspaceMemberChangeRequest(Guid OperationId, long ExpectedRevision);
public sealed record WorkspaceMemberRoleRequest(Guid OperationId, long ExpectedRevision, string Role);
public sealed record WorkspaceOwnerPromotionRequest(Guid OperationId, long ExpectedRevision, bool Confirmed);
public sealed record WorkspaceViewerPolicyRequest(Guid OperationId, long ExpectedRevision, bool IncludeUnreleased);
public sealed record WorkspaceAccessChangeResponse(Guid OperationId, string Status, WorkspaceMemberResponse? Membership,
    WorkspaceViewerPolicyResponse? ViewerPolicy, string? ErrorCode);
public sealed class WorkspaceAccessProblemDetails : Microsoft.AspNetCore.Mvc.ProblemDetails
{
    public required string ErrorCode { get; init; }
    public WorkspaceMemberResponse? Membership { get; init; }
    public WorkspaceViewerPolicyResponse? ViewerPolicy { get; init; }
}

public static class WorkspaceAccessEndpoints
{
    public static RouteGroupBuilder MapWorkspaceAccessEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/captures/{id:guid}/delivery-summary", async (Guid id, IWorkspaceMediaScope media, CancellationToken token) =>
        {
            SafeDelivery[]? items = await media.GetDeliveriesAsync(id, token);
            return items is null ? Results.NotFound() : Results.Ok(new SafeDeliverySummaryResponse(
                [.. items.Take(1000).Select(item => new SafeDeliveryResponse(item.Id, item.AssetId, item.State, item.CanRetry))], items.Length > 1000));
        }).Produces<SafeDeliverySummaryResponse>().ProducesProblem(404);
        management.MapGet("/access", (WorkspaceAccessContext access) =>
        {
            WorkspaceMembership member = access.Membership!;
            WorkspaceViewerPolicy policy = access.ViewerPolicy!;
            return Results.Ok(new WorkspaceAccessResponse(member.Id, member.Role.ToString(), member.Revision,
                new(member.Role != WorkspaceRole.Viewer, access.IsAdministrator, access.IsAdministrator,
                    member.Role == WorkspaceRole.Owner), new(policy.IncludeUnreleased, policy.Revision)));
        }).Produces<WorkspaceAccessResponse>();
        management.MapGet("/members", async (IAccessGrantStore store, CancellationToken token) =>
            Results.Ok(new WorkspaceMembersResponse([.. (await store.ListAsync(token)).Select(WorkspaceMemberResponse.From)])))
            .Produces<WorkspaceMembersResponse>();
        management.MapGet("/viewer-policy", (WorkspaceAccessContext access) => Results.Ok(
            new WorkspaceViewerPolicyResponse(access.ViewerPolicy!.IncludeUnreleased, access.ViewerPolicy.Revision)))
            .Produces<WorkspaceViewerPolicyResponse>();
        management.MapPut("/viewer-policy", (WorkspaceViewerPolicyRequest request, HttpContext http,
            IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(http, store, changes, request.OperationId,
                request.ExpectedRevision, Guid.Empty, "viewer-policy", includeUnreleased: request.IncludeUnreleased))
            .Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(403, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(404, "application/problem+json").ProducesValidationProblem();
        management.MapPost("/members/{id:guid}/role", (Guid id, WorkspaceMemberRoleRequest request, HttpContext http,
            IAccessGrantStore store, LiveChangeNotifier changes) =>
        {
            if (!Enum.TryParse(request.Role, out WorkspaceRole role) || !Enum.IsDefined(role) || role.ToString() != request.Role)
                return Task.FromResult<IResult>(Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Provide a fixed workspace role."] }));
            return ChangeAsync(http, store, changes, request.OperationId, request.ExpectedRevision, id, "role", role);
        }).Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(403, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(404, "application/problem+json").ProducesValidationProblem();
        management.MapPost("/members/{id:guid}/promote-owner", (Guid id, WorkspaceOwnerPromotionRequest request,
            HttpContext http, IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(http, store, changes,
                request.OperationId, request.ExpectedRevision, id, "promote-owner", confirmed: request.Confirmed))
            .Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(403, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(404, "application/problem+json").ProducesValidationProblem();
        management.MapPost("/members/{id:guid}/remove", (Guid id, WorkspaceMemberChangeRequest request,
            HttpContext http, IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(http, store, changes,
                request.OperationId, request.ExpectedRevision, id, "remove"))
            .Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(403, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(404, "application/problem+json").ProducesValidationProblem();
        management.MapPost("/membership/leave", (WorkspaceMemberChangeRequest request,
            HttpContext http, IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(http, store, changes,
                request.OperationId, request.ExpectedRevision, Guid.Empty, "leave"))
            .Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(403, "application/problem+json").Produces<WorkspaceAccessProblemDetails>(404, "application/problem+json").ProducesValidationProblem();
        return management;
    }

    private static async Task<IResult> ChangeAsync(HttpContext http, IAccessGrantStore store, LiveChangeNotifier changes,
        Guid operation, long revision, Guid target, string kind, WorkspaceRole? role = null, bool confirmed = false,
        bool includeUnreleased = false)
    {
        if (operation == Guid.Empty || revision < 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Provide an operation ID and current revision."] });
        AccessChangeResult result = await store.ChangeAsync(new(operation, http.User.FindFirstValue("iss")!,
            (http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub"))!, target,
            revision, kind, role, confirmed, includeUnreleased), http.RequestAborted);
        if (result.Status == "applied") changes.RetentionChanged();
        else return Results.Json(new WorkspaceAccessProblemDetails
        {
            Status = result.Status switch { "denied" => 403, "notFound" => 404, _ => 409 },
            Title = "Workspace access could not be changed",
            ErrorCode = result.Status switch { "denied" => "permission-denied", "notFound" => "membership-not-found", "lastOwner" => "last-owner", _ => "membership-conflict" },
            Membership = result.Membership is null ? null : WorkspaceMemberResponse.From(result.Membership),
            ViewerPolicy = result.ViewerPolicy is null ? null : new(result.ViewerPolicy.IncludeUnreleased, result.ViewerPolicy.Revision),
        }, statusCode: result.Status switch { "denied" => 403, "notFound" => 404, _ => 409 }, contentType: "application/problem+json");
        return Results.Ok(new WorkspaceAccessChangeResponse(result.OperationId, result.Status,
            result.Membership is null ? null : WorkspaceMemberResponse.From(result.Membership),
            result.ViewerPolicy is null ? null : new(result.ViewerPolicy.IncludeUnreleased, result.ViewerPolicy.Revision),
            null));
    }
}
