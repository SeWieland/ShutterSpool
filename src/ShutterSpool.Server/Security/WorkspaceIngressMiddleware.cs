using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Server.Api.CameraIngress;
using ShutterSpool.Server.Hosting;

namespace ShutterSpool.Server.Security;

internal sealed class WorkspaceIngressMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, WorkspaceContext context, IWorkspaceRegistry registry, CameraIngressSettings settings)
    {
        if (!http.Request.Path.StartsWithSegments("/internal/camera")) { await next(http); return; }
        byte[] supplied = Encoding.UTF8.GetBytes(http.Request.Headers["X-API-KEY"].ToString());
        byte[] expected = Encoding.UTF8.GetBytes(settings.CallbackKey);
        if (!settings.Enabled || http.Connection.LocalPort != settings.InternalPort || supplied.Length != expected.Length
            || !CryptographicOperations.FixedTimeEquals(supplied, expected)) { http.Response.StatusCode = 403; return; }
        try
        {
            string handle;
            if (http.Request.Path.StartsWithSegments("/internal/camera/fs"))
            {
                string[] segments = http.Request.Path.Value!.Split('/');
                if (segments.Length < 6 || !Guid.TryParseExact(segments[4], "N", out Guid loginId)) { http.Response.StatusCode = 403; return; }
                handle = WorkspaceIngressHandle.Login(loginId);
            }
            else
            {
                http.Request.EnableBuffering(32 * 1024, 32 * 1024);
                using JsonDocument body = await JsonDocument.ParseAsync(http.Request.Body,
                    new JsonDocumentOptions { MaxDepth = 16 }, http.RequestAborted);
                http.Request.Body.Position = 0;
                if (http.GetEndpoint()?.Metadata.GetMetadata<CameraHookEndpointMetadata>() is not null)
                {
                    if (!body.RootElement.TryGetProperty("endpoint", out var endpoint)
                        || !CameraIngressEndpoints.TryLoginId(endpoint.GetString(), settings, out Guid loginId))
                    { http.Response.StatusCode = 403; return; }
                    handle = WorkspaceIngressHandle.Login(loginId);
                }
                else
                {
                    if (!body.RootElement.TryGetProperty("username", out var user) || user.GetString() is not { Length: > 0 and <= 64 } name)
                    { http.Response.StatusCode = 403; return; }
                    handle = WorkspaceIngressHandle.Username(name);
                }
            }
            WorkspaceIngressRoute? route = await registry.FindIngressAsync(handle, http.RequestAborted);
            if (route is null) { http.Response.StatusCode = 403; return; }
            context.Bind(route.Workspace);

        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) { http.Response.StatusCode = 403; return; }
        await next(http);
    }
}
