using Microsoft.AspNetCore.DataProtection;
using ShutterSpool.Application.Workspaces;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShutterSpool.Server.Security;

// A SignalR connection token is immutable workspace context, including subsequent long-poll/SSE requests.
internal sealed class WorkspaceLiveTokenMiddleware(RequestDelegate next, IDataProtectionProvider protection)
{
    public async Task InvokeAsync(HttpContext http, WorkspaceContext workspace)
    {
        if (!workspace.Enabled || !http.Request.Path.StartsWithSegments("/live/changes")) { await next(http); return; }
        IDataProtector protector = protection.CreateProtector("ShutterSpool.LiveWorkspace.v1", workspace.Required.Id.ToString("N"));
        if (http.Request.Path == "/live/changes/negotiate")
        {
            Stream output = http.Response.Body;
            await using var buffered = new MemoryStream();
            http.Response.Body = buffered;
            try
            {
                await next(http);
                byte[] body = buffered.ToArray();
                if (http.Response.StatusCode == 200 && body.Length > 0)
                {
                    JsonObject document = JsonNode.Parse(body)?.AsObject() ?? throw new InvalidOperationException("Invalid live negotiation response.");
                    string tokenProperty = document.ContainsKey("connectionToken") ? "connectionToken" : "connectionId";
                    if (document[tokenProperty]?.GetValue<string>() is string token)
                    {
                        document[tokenProperty] = protector.Protect(token);
                        body = JsonSerializer.SerializeToUtf8Bytes(document);
                        http.Response.ContentLength = body.Length;
                    }
                }
                http.Response.Body = output;
                await output.WriteAsync(body, http.RequestAborted);
            }
            finally { http.Response.Body = output; }
            return;
        }
        if (!http.Request.Query.TryGetValue("id", out var id)) { await next(http); return; }
        if (id.Count != 1 || id[0] is not { Length: > 0 and < 4096 } protectedToken)
        { http.Response.StatusCode = 403; return; }
        string unwrapped;
        try { unwrapped = protector.Unprotect(protectedToken); }
        catch (CryptographicException) { http.Response.StatusCode = 403; return; }
        QueryString original = http.Request.QueryString;
        http.Request.QueryString = QueryString.Create(http.Request.Query.SelectMany(pair =>
            pair.Key == "id" ? [new KeyValuePair<string, string?>("id", unwrapped)]
                : pair.Value.Select(value => new KeyValuePair<string, string?>(pair.Key, value))));
        try { await next(http); }
        finally { http.Request.QueryString = original; }
    }
}
