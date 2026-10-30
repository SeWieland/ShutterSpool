using System.Security.Claims;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Server.Api.Workspaces;

public sealed record WorkspaceSummaryResponse(Guid Id, string Name);
public sealed record WorkspaceListResponse(WorkspaceSummaryResponse[] Items, bool Incomplete, bool SelectionRequired);

internal static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/workspaces", DiscoverAsync).RequireAuthorization()
            .WithSummary("Discover memberships for the verified identity")
            .Produces<WorkspaceListResponse>().ProducesProblem(401).ProducesProblem(503);
    }

    private static async Task<IResult> DiscoverAsync(HttpContext http, WorkspaceContext context,
        IWorkspaceRegistry registry, IServiceScopeFactory scopes)
    {
        if (!context.Enabled) return Results.Ok(new WorkspaceListResponse([], false, false));
        string? issuer = http.User.FindFirstValue("iss");
        string? subject = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
            return Results.Ok(new WorkspaceListResponse([], false, true));
        var items = new List<WorkspaceSummaryResponse>();
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
                if (await scope.ServiceProvider.GetRequiredService<IAccessGrantStore>().IsActiveAsync(issuer, subject, timeout.Token))
                    items.Add(new(workspace.Id, workspace.Name));
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception) { incomplete = true; }
        }
        return Results.Ok(new WorkspaceListResponse([.. items], incomplete, true));
    }
}
