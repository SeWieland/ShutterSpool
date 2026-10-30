using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Server.Security;

public static class WorkspaceSelection
{
    public const string Header = "X-ShutterSpool-Workspace";

    public static bool IsPrivatePath(PathString path) => path.StartsWithSegments("/management")
        || path.StartsWithSegments("/media") || path.StartsWithSegments("/live");

    public static bool TryRead(HttpRequest request, out Guid id)
    {
        id = Guid.Empty;
        var header = request.Headers[Header];
        var query = request.Query["workspace"];
        bool live = request.Path == "/live/changes" || request.Path == "/live/changes/negotiate" || request.Path == "/live/status";
        if (header.Count > 1 || query.Count > 1 || query.Count > 0 && !live) return false;
        if (header.Count == 0 && query.Count == 0) return false;
        if (!Guid.TryParseExact(header.Count == 1 ? header[0] : query[0], "D", out id) || id == Guid.Empty) return false;
        return header.Count == 0 || query.Count == 0 || Guid.TryParseExact(query[0], "D", out Guid queried) && queried == id;
    }
}

internal sealed class WorkspaceSelectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, WorkspaceContext workspace, IWorkspaceRegistry registry)
    {
        if (!workspace.Enabled || !WorkspaceSelection.IsPrivatePath(http.Request.Path))
        {
            await next(http);
            return;
        }
        if (http.User.Identity?.IsAuthenticated != true)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (http.Request.Path.StartsWithSegments("/management/retention/erasures/suppressions"))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!WorkspaceSelection.TryRead(http.Request, out Guid id))
        {
            await Results.Problem(statusCode: 400, title: "Select one workspace",
                extensions: new Dictionary<string, object?> { ["errorCode"] = "WORKSPACE_REQUIRED" }).ExecuteAsync(http);
            return;
        }
        WorkspaceDefinition? selected = await registry.FindReadyAsync(id, http.RequestAborted);
        if (selected is null)
        {
            await Results.Problem(statusCode: 403, title: "Workspace membership is unavailable",
                extensions: new Dictionary<string, object?> { ["errorCode"] = "membership-revoked" }).ExecuteAsync(http);
            return;
        }
        workspace.Bind(selected);
        http.Items[typeof(WorkspaceDefinition)] = selected;
        await next(http);
    }
}
