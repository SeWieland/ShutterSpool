using System.Net.Mime;
using ShutterSpool.Domain.Security;
using ShutterSpool.Application.Security;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Server.Security;
using System.Security.Claims;

namespace ShutterSpool.Server.Api.Workspaces;

public sealed record WorkspaceCapabilitiesResponse(bool CanCurate, bool CanAdminister, bool CanManageMembers, bool CanManageOwners);
public sealed record WorkspaceViewerPolicyResponse(bool IncludeUnreleased, long Revision);
public sealed record WorkspaceAccessResponse(Guid MembershipId, string Role, long Revision,
    WorkspaceCapabilitiesResponse Capabilities, WorkspaceViewerPolicyResponse ViewerPolicy);
public sealed record WorkspaceMemberResponse(Guid Id, string Issuer, string Subject, string Role, long Revision, bool IsActive, string? DisplayName)
{
    internal static WorkspaceMemberResponse From(WorkspaceMembership item) => new(item.Id, item.Issuer, item.Subject,
        item.Role.ToString(), item.Revision, item.IsActive, item.DisplayName);
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
        }).RequireAuthorization(WorkspacePolicies.Member).Produces<SafeDeliverySummaryResponse>().ProducesProblem(404);
        management.MapGet("/access", async (WorkspaceAccessContext access, HttpContext http, IAccessGrantStore store, CancellationToken token) =>
        {
            WorkspaceMembership member = access.Membership!;
            WorkspaceViewerPolicy policy = access.ViewerPolicy!;
            string? displayName = http.User.FindFirstValue(ClaimTypes.Name) ?? http.User.FindFirstValue("name");
            displayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
            if (displayName?.Length > 256)
                displayName = displayName[..(char.IsHighSurrogate(displayName[255]) ? 255 : 256)];
            if (member.DisplayName != displayName)
                await store.UpdateDisplayNameAsync(member.Issuer, member.Subject, displayName, token);
            return Results.Ok(new WorkspaceAccessResponse(member.Id, member.Role.ToString(), member.Revision,
                new(member.Role != WorkspaceRole.Viewer, access.IsAdministrator, access.IsAdministrator,
                    member.Role == WorkspaceRole.Owner), new(policy.IncludeUnreleased, policy.Revision)));
        }).RequireAuthorization(WorkspacePolicies.Member).Produces<WorkspaceAccessResponse>();
        management.MapGet("/members", async (IAccessGrantStore store, CancellationToken token) =>
            Results.Ok(new WorkspaceMembersResponse([.. (await store.ListAsync(token)).Select(WorkspaceMemberResponse.From)])))
            .RequireAuthorization(WorkspacePolicies.Administer).Produces<WorkspaceMembersResponse>();
        management.MapGet("/viewer-policy", (WorkspaceAccessContext access) => Results.Ok(
            new WorkspaceViewerPolicyResponse(access.ViewerPolicy!.IncludeUnreleased, access.ViewerPolicy.Revision)))
            .RequireAuthorization(WorkspacePolicies.Member).Produces<WorkspaceViewerPolicyResponse>();
        management.MapPut("/viewer-policy", (WorkspaceViewerPolicyRequest request, HttpContext http,
            IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(
                new(request.OperationId, ActorIssuer(http), ActorSubject(http), Guid.Empty, request.ExpectedRevision,
                    AccessChangeKind.ViewerPolicy, IncludeUnreleased: request.IncludeUnreleased), store, changes, http.RequestAborted))
            .RequireAuthorization(WorkspacePolicies.Administer).Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(403, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(404, MediaTypeNames.Application.ProblemJson).ProducesValidationProblem();
        management.MapPost("/members/{id:guid}/role", (Guid id, WorkspaceMemberRoleRequest request, HttpContext http,
            IAccessGrantStore store, LiveChangeNotifier changes) =>
        {
            if (!Enum.TryParse(request.Role, out WorkspaceRole role) || !Enum.IsDefined(role) || role.ToString() != request.Role)
                return Task.FromResult<IResult>(Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Provide a fixed workspace role."] }));
            return ChangeAsync(new(request.OperationId, ActorIssuer(http), ActorSubject(http), id, request.ExpectedRevision,
                AccessChangeKind.Role, Role: role), store, changes, http.RequestAborted);
        }).RequireAuthorization(WorkspacePolicies.Administer).Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(403, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(404, MediaTypeNames.Application.ProblemJson).ProducesValidationProblem();
        management.MapPost("/members/{id:guid}/promote-owner", (Guid id, WorkspaceOwnerPromotionRequest request,
            HttpContext http, IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(
                new(request.OperationId, ActorIssuer(http), ActorSubject(http), id, request.ExpectedRevision,
                    AccessChangeKind.PromoteOwner, Confirmed: request.Confirmed), store, changes, http.RequestAborted))
            .RequireAuthorization(WorkspacePolicies.ManageOwners).Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(403, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(404, MediaTypeNames.Application.ProblemJson).ProducesValidationProblem();
        management.MapPost("/members/{id:guid}/remove", (Guid id, WorkspaceMemberChangeRequest request,
            HttpContext http, IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(
                new(request.OperationId, ActorIssuer(http), ActorSubject(http), id, request.ExpectedRevision,
                    AccessChangeKind.Remove), store, changes, http.RequestAborted))
            .RequireAuthorization(WorkspacePolicies.Administer).Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(403, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(404, MediaTypeNames.Application.ProblemJson).ProducesValidationProblem();
        management.MapPost("/membership/leave", (WorkspaceMemberChangeRequest request,
            HttpContext http, IAccessGrantStore store, LiveChangeNotifier changes) => ChangeAsync(
                new(request.OperationId, ActorIssuer(http), ActorSubject(http), Guid.Empty, request.ExpectedRevision,
                    AccessChangeKind.Leave), store, changes, http.RequestAborted))
            .RequireAuthorization(WorkspacePolicies.Member).Produces<WorkspaceAccessChangeResponse>().Produces<WorkspaceAccessProblemDetails>(409, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(403, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceAccessProblemDetails>(404, MediaTypeNames.Application.ProblemJson).ProducesValidationProblem();
        return management;
    }

    private static string ActorIssuer(HttpContext http) => http.User.FindFirstValue("iss")!;
    private static string ActorSubject(HttpContext http) =>
        (http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub"))!;

    private static async Task<IResult> ChangeAsync(AccessChange command, IAccessGrantStore store, LiveChangeNotifier changes,
        CancellationToken cancellationToken)
    {
        if (command.OperationId == Guid.Empty || command.ExpectedRevision < 0)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Provide an operation ID and current revision."] });
        AccessChangeResult result = await store.ChangeAsync(command, cancellationToken);
        WorkspaceMemberResponse? member = result.Membership is null ? null : WorkspaceMemberResponse.From(result.Membership);
        WorkspaceViewerPolicyResponse? policy = result.ViewerPolicy is null ? null : new(result.ViewerPolicy.IncludeUnreleased, result.ViewerPolicy.Revision);
        if (result.Status == "applied")
        {
            if (!result.WasReplayed()) changes.RetentionChanged();
            return Results.Ok(new WorkspaceAccessChangeResponse(result.OperationId, result.Status, member, policy, null));
        }

        (int status, string code) = result.Status switch
        {
            "denied" => (403, "permission-denied"),
            "notFound" => (404, "membership-not-found"),
            "lastOwner" => (409, "last-owner"),
            _ => (409, "membership-conflict"),
        };
        return Results.Json(new WorkspaceAccessProblemDetails
        {
            Status = status, Title = "Workspace access could not be changed", ErrorCode = code,
            Membership = member, ViewerPolicy = policy,
        }, statusCode: status, contentType: MediaTypeNames.Application.ProblemJson);
    }
}
