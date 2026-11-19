using System.Net.Mime;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Domain.Security;
using ShutterSpool.Server.Security;

namespace ShutterSpool.Server.Api.Workspaces;

public sealed record WorkspaceInvitationResponse(Guid Id, string RecipientLabel, string Role,
    string Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    internal static WorkspaceInvitationResponse From(WorkspaceInvitation offer) => new(offer.Id,
        offer.RecipientLabel, offer.Role.ToString(), offer.Status, offer.Revision, offer.CreatedAt, offer.ExpiresAt);
}
public sealed record WorkspaceInvitationsResponse(WorkspaceInvitationResponse[] Items);
public sealed record WorkspaceOfferResponse(Guid Id, Guid WorkspaceId, string WorkspaceName, string Role,
    string Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt)
{
    internal static WorkspaceOfferResponse From(WorkspaceInvitation offer, WorkspaceDefinition workspace) => new(offer.Id,
        workspace.Id, workspace.Name, offer.Role.ToString(), offer.Status, offer.Revision, offer.CreatedAt, offer.ExpiresAt);
}
public sealed record WorkspaceOffersResponse(WorkspaceOfferResponse[] Items, bool Incomplete);
public sealed record WorkspaceInvitationCreateRequest(Guid OperationId, string Email, string Role = "Viewer");
public sealed record WorkspaceInvitationRenewRequest(Guid OperationId, long ExpectedRevision, string Role = "Viewer");
public sealed record WorkspaceInvitationChangeRequest(Guid OperationId, long ExpectedRevision);
public sealed record WorkspaceInvitationChangeResponse(Guid OperationId, string Status, WorkspaceInvitationResponse? Offer,
    WorkspaceMemberResponse? Membership, string? ErrorCode, string? SetupEmailStatus);
public sealed record WorkspaceOfferAcceptanceResponse(Guid OperationId, string Status, WorkspaceOfferResponse? Offer,
    WorkspaceMemberResponse? Membership, string? ErrorCode);
public sealed class WorkspaceInvitationProblemDetails : ProblemDetails
{
    public required string ErrorCode { get; init; }
    public WorkspaceInvitationResponse? Offer { get; init; }
    public WorkspaceMemberResponse? Membership { get; init; }
}
public sealed class WorkspaceOfferProblemDetails : ProblemDetails
{
    public required string ErrorCode { get; init; }
    public WorkspaceOfferResponse? Offer { get; init; }
    public WorkspaceMemberResponse? Membership { get; init; }
}

internal static class WorkspaceInvitationEndpoints
{
    public static void MapWorkspaceInvitationEndpoints(this WebApplication app, RouteGroupBuilder management)
    {
        RouteGroupBuilder invitations = management.MapGroup("").RequireAuthorization(WorkspacePolicies.Administer);
        invitations.MapGet("/invitations", async (IWorkspaceInvitationStore store, CancellationToken token) =>
            Results.Ok(new WorkspaceInvitationsResponse([.. (await store.ListInvitationsAsync(token)).Select(WorkspaceInvitationResponse.From)])))
            .Produces<WorkspaceInvitationsResponse>();
        invitations.MapPost("/invitations", CreateAsync)
            .Produces<WorkspaceInvitationChangeResponse>().Produces<WorkspaceInvitationProblemDetails>(409, MediaTypeNames.Application.ProblemJson)
            .Produces<WorkspaceInvitationProblemDetails>(503, MediaTypeNames.Application.ProblemJson)
            .Produces<WorkspaceInvitationProblemDetails>(400, MediaTypeNames.Application.ProblemJson);
        invitations.MapPost("/invitations/{id:guid}/renew", RenewAsync)
            .Produces<WorkspaceInvitationChangeResponse>().Produces<WorkspaceInvitationProblemDetails>(404, MediaTypeNames.Application.ProblemJson)
            .Produces<WorkspaceInvitationProblemDetails>(409, MediaTypeNames.Application.ProblemJson).ProducesValidationProblem();
        invitations.MapPost("/invitations/{id:guid}/cancel", CancelAsync)
            .Produces<WorkspaceInvitationChangeResponse>().Produces<WorkspaceInvitationProblemDetails>(404, MediaTypeNames.Application.ProblemJson)
            .Produces<WorkspaceInvitationProblemDetails>(409, MediaTypeNames.Application.ProblemJson).ProducesValidationProblem();
        app.MapGet("/workspace-offers", DiscoverAsync).RequireAuthorization().Produces<WorkspaceOffersResponse>().ProducesProblem(401).ProducesProblem(503);
        app.MapPost("/workspace-offers/{workspaceId:guid}/{id:guid}/accept", AcceptAsync).RequireAuthorization()
            .Produces<WorkspaceOfferAcceptanceResponse>().Produces<WorkspaceOfferProblemDetails>(403, MediaTypeNames.Application.ProblemJson)
            .Produces<WorkspaceOfferProblemDetails>(404, MediaTypeNames.Application.ProblemJson).Produces<WorkspaceOfferProblemDetails>(409, MediaTypeNames.Application.ProblemJson)
            .ProducesValidationProblem().ProducesProblem(401).ProducesProblem(503);
    }

    private static async Task<IResult> CreateAsync(WorkspaceInvitationCreateRequest request, HttpContext http, WorkspaceInvitationService service)
    {
        if (request.OperationId == Guid.Empty || !TryRole(request.Role, out WorkspaceRole role))
            return ChangeResponse(new(request.OperationId, "invalid-request"));
        return ChangeResponse(await service.CreateAsync(request.OperationId, Issuer(http), Subject(http), request.Email, role, http.RequestAborted));
    }

    private static async Task<IResult> RenewAsync(Guid id, WorkspaceInvitationRenewRequest request, HttpContext http, WorkspaceInvitationService service)
    {
        if (!ValidRequest(request.OperationId, id, request.ExpectedRevision) || !TryRole(request.Role, out WorkspaceRole role)) return InvalidRequest();
        return ChangeResponse(await service.RenewAsync(new(request.OperationId, Issuer(http), Subject(http), id, request.ExpectedRevision, role), http.RequestAborted));
    }

    private static async Task<IResult> CancelAsync(Guid id, WorkspaceInvitationChangeRequest request, HttpContext http, IWorkspaceInvitationStore store)
    {
        if (!ValidRequest(request.OperationId, id, request.ExpectedRevision)) return InvalidRequest();
        return ChangeResponse(await store.ChangeInvitationAsync(new WorkspaceInvitationCancel(request.OperationId, Issuer(http), Subject(http), id, request.ExpectedRevision), http.RequestAborted));
    }

    private static IResult ChangeResponse(WorkspaceInvitationResult result)
    {
        WorkspaceInvitationResponse? offer = result.Offer is null ? null : WorkspaceInvitationResponse.From(result.Offer);
        WorkspaceMemberResponse? member = result.Membership is null ? null : WorkspaceMemberResponse.From(result.Membership);
        if (result.Status is "applied" or "existingMember") return Results.Ok(new WorkspaceInvitationChangeResponse(result.OperationId, result.Status, offer, member, null, result.SetupEmailStatus));
        return Results.Json(new WorkspaceInvitationProblemDetails { Status = StatusCode(result.Status), Title = "Workspace invitation could not be changed",
            ErrorCode = ErrorCode(result.Status), Offer = offer, Membership = member }, statusCode: StatusCode(result.Status), contentType: MediaTypeNames.Application.ProblemJson);
    }

    private static async Task<IResult> DiscoverAsync(HttpContext http, IWorkspaceRegistry registry, IServiceScopeFactory scopes)
    {
        var items = new List<WorkspaceOfferResponse>();
        bool incomplete = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(10));
        foreach (WorkspaceDefinition workspace in await registry.ListReadyAsync(http.RequestAborted))
        {
            if (budget.IsCancellationRequested) { incomplete = true; break; }
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(workspace);
                WorkspaceInvitation[] offers = await scope.ServiceProvider.GetRequiredService<IWorkspaceInvitationStore>()
                    .ListOwnInvitationsAsync(Issuer(http), Subject(http), timeout.Token);
                items.AddRange(offers.Select(offer => WorkspaceOfferResponse.From(offer, workspace)));
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception) { incomplete = true; }
        }
        return Results.Ok(new WorkspaceOffersResponse([.. items], incomplete));
    }

    private static async Task<IResult> AcceptAsync(Guid workspaceId, Guid id, WorkspaceInvitationChangeRequest request,
        HttpContext http, IWorkspaceRegistry registry, IServiceScopeFactory scopes)
    {
        if (!ValidRequest(request.OperationId, id, request.ExpectedRevision) || workspaceId == Guid.Empty) return InvalidRequest();
        if (string.IsNullOrWhiteSpace(Issuer(http)) || string.IsNullOrWhiteSpace(Subject(http))) return Results.Forbid();
        WorkspaceDefinition? workspace = await registry.FindReadyAsync(workspaceId, http.RequestAborted);
        if (workspace is null) return Results.Problem(statusCode: 503, title: "Workspace is unavailable");
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(workspace);
        // A signed access-token claim is required; a browser profile or an arbitrary string is not verification.
        Claim[] verified = [.. http.User.FindAll("email_verified")];
        bool emailVerified = verified.Length == 1 && verified[0].ValueType == ClaimValueTypes.Boolean && verified[0].Value == "true";
        WorkspaceInvitationResult result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<IWorkspaceInvitationStore>().ChangeInvitationAsync(
                new WorkspaceInvitationAccept(request.OperationId, Issuer(http), Subject(http), id, request.ExpectedRevision, emailVerified), http.RequestAborted);
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Results.Problem(statusCode: 503, title: "Workspace is unavailable");
        }
        WorkspaceOfferResponse? offer = result.Offer is null ? null : WorkspaceOfferResponse.From(result.Offer, workspace);
        WorkspaceMemberResponse? member = result.Membership is null ? null : WorkspaceMemberResponse.From(result.Membership);
        if (result.Status is "applied" or "existingMember") return Results.Ok(new WorkspaceOfferAcceptanceResponse(request.OperationId, result.Status, offer, member, null));
        return Results.Json(new WorkspaceOfferProblemDetails { Status = StatusCode(result.Status), Title = "Workspace offer could not be accepted",
            ErrorCode = ErrorCode(result.Status), Offer = offer, Membership = member }, statusCode: StatusCode(result.Status), contentType: MediaTypeNames.Application.ProblemJson);
    }

    private static bool TryRole(string text, out WorkspaceRole role) => Enum.TryParse(text, out role)
        && WorkspaceAccessPolicy.IsInvitationRole(role) && role.ToString() == text;
    private static bool ValidRequest(Guid operation, Guid target, long revision) => operation != Guid.Empty && target != Guid.Empty && revision >= 0;
    private static IResult InvalidRequest() => Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["Provide an operation ID, current revision, email or invitation ID and non-Owner role."] });
    private static string Issuer(HttpContext http) => http.User.FindFirstValue("iss") ?? "";
    private static string Subject(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub") ?? "";
    private static int StatusCode(string status) => status switch { "enrollment-unavailable" => 503, "invalid-email" or "invalid-request" => 400, "notFound" => 404, "denied" or "verificationRequired" => 403, _ => 409 };
    private static string ErrorCode(string status) => status switch
    {
        "enrollment-unavailable" or "invalid-email" or "invalid-request" or "account-unavailable" or "enrollment-rejected" => status,
        "denied" => "permission-denied", "verificationRequired" => "email-verification-required", "notFound" => "invitation-not-found",
        "expired" => "invitation-expired", "cancelled" => "invitation-cancelled", "invalidated" => "invitation-invalidated",
        "accepted" => "invitation-accepted", "membershipRemoved" => "membership-revoked", _ => "invitation-conflict",
    };
}
