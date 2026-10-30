using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Api.Workspaces;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class WorkspaceTenancyHttpTests
{
    [Fact]
    public async Task One_identity_has_independent_roles_and_viewer_policies_in_two_selected_workspaces()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition first = fixture.Workspaces[0], second = fixture.Workspaces[1];
        await fixture.SetGrantAsync(second, first.Name, true);
        Guid capture = Guid.NewGuid(), source = Guid.NewGuid();
        await fixture.SeedAsync(first, source, (capture, "first-private"));
        await fixture.SeedAsync(second, source, (capture, "second-private"));
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, second))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            (await database.AuthorizationGrants.SingleAsync(item => item.Subject == first.Name, Token)).Role = WorkspaceRole.Viewer;
            await database.SaveChangesAsync(Token);
        }
        using HttpClient client = fixture.Client(first.Name);
        using HttpResponseMessage firstAccess = await SendAsync(client, HttpMethod.Get, "/management/access", first.Id);
        using HttpResponseMessage secondAccess = await SendAsync(client, HttpMethod.Get, "/management/access", second.Id);
        Assert.Equal("Owner", (await firstAccess.Content.ReadFromJsonAsync<WorkspaceAccessResponse>(Token))!.Role);
        Assert.Equal("Viewer", (await secondAccess.Content.ReadFromJsonAsync<WorkspaceAccessResponse>(Token))!.Role);
        using HttpResponseMessage firstDetail = await SendAsync(client, HttpMethod.Get, $"/management/captures/{capture}", first.Id);
        Assert.Equal(HttpStatusCode.OK, firstDetail.StatusCode);
        using HttpResponseMessage secondHidden = await SendAsync(client, HttpMethod.Get, $"/management/captures/{capture}", second.Id);
        Assert.Equal(HttpStatusCode.NotFound, secondHidden.StatusCode);
        using HttpResponseMessage secondConfig = await SendAsync(client, HttpMethod.Get, "/management/sources", second.Id);
        Assert.Equal(HttpStatusCode.Forbidden, secondConfig.StatusCode);
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, second))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            (await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(Token)).IncludeUnreleased = true;
            await database.SaveChangesAsync(Token);
        }
        using HttpResponseMessage secondVisible = await SendAsync(client, HttpMethod.Get, $"/management/captures/{capture}", second.Id);
        Assert.Equal(HttpStatusCode.OK, secondVisible.StatusCode);
        JsonElement selected = await secondVisible.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(second.Name, selected.GetProperty("source").GetProperty("name").GetString());
        Assert.NotEqual(first.Name, selected.GetProperty("source").GetProperty("name").GetString());
        using HttpResponseMessage firstPolicy = await SendAsync(client, HttpMethod.Get, "/management/viewer-policy", first.Id);
        Assert.False((await firstPolicy.Content.ReadFromJsonAsync<WorkspaceViewerPolicyResponse>(Token))!.IncludeUnreleased);
    }

    [Fact]
    public async Task Viewer_live_hints_are_content_free_and_policy_change_closes_stale_subscription()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition workspace = fixture.Workspaces[0];
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, workspace))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            (await database.AuthorizationGrants.SingleAsync(Token)).Role = WorkspaceRole.Viewer;
            await database.SaveChangesAsync(Token);
        }
        using WebSocket socket = await fixture.ConnectAsync(workspace, workspace.Name);
        Guid hiddenCapture = Guid.NewGuid();
        fixture.Publish(workspace, hiddenCapture);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        byte[] buffer = new byte[4096];
        string hint;
        do
        {
            WebSocketReceiveResult frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            hint = Encoding.UTF8.GetString(buffer, 0, frame.Count);
        } while (!hint.Contains("resyncRequired", StringComparison.Ordinal));
        Assert.DoesNotContain(hiddenCapture.ToString("D"), hint, StringComparison.Ordinal);
        Assert.DoesNotContain("captureChanged", hint, StringComparison.Ordinal);
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, workspace))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            var policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(Token);
            policy.IncludeUnreleased = true; policy.Revision++;
            await database.SaveChangesAsync(Token);
        }
        try
        {
            WebSocketReceiveResult frame;
            do { frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token); }
            while (frame.MessageType != WebSocketMessageType.Close);
        }
        catch (WebSocketException) when (socket.State == WebSocketState.Aborted) { }
    }
}
