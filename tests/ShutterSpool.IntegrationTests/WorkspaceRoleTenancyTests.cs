using ShutterSpool.Domain.Security;
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
    public async Task Viewer_live_hints_coalesce_without_content_and_policy_changes_close_stale_subscriptions()
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
        Guid[] hiddenCaptures = [.. Enumerable.Range(0, 8).Select(_ => Guid.NewGuid())];
        foreach (Guid hiddenCapture in hiddenCaptures) fixture.Publish(workspace, hiddenCapture);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        int resyncHints = 0;
        void CheckFrame(string message)
        {
            foreach (Guid hiddenCapture in hiddenCaptures)
                Assert.DoesNotContain(hiddenCapture.ToString("D"), message, StringComparison.Ordinal);
            Assert.DoesNotContain("captureChanged", message, StringComparison.Ordinal);
            foreach (string frame in message.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument document = JsonDocument.Parse(frame);
                JsonElement root = document.RootElement;
                int type = root.GetProperty("type").GetInt32();
                if (type != 1) { Assert.Contains(type, new[] { 6, 7 }); continue; }
                Assert.Equal("resyncRequired", root.GetProperty("target").GetString());
                JsonElement payload = Assert.Single(root.GetProperty("arguments").EnumerateArray());
                Assert.Equal(new[] { "epoch" }, payload.EnumerateObject().Select(property => property.Name));
                Assert.Equal(fixture.Server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeBuffer>().Epoch,
                    payload.GetProperty("epoch").GetString());
                resyncHints++;
            }
        }
        while (resyncHints == 0) CheckFrame(await ReceiveTextAsync(socket, deadline.Token));
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(Token))
        {
            quiet.CancelAfter(TimeSpan.FromMilliseconds(300));
            while (true)
            {
                try { CheckFrame(await ReceiveTextAsync(socket, quiet.Token)); }
                catch (OperationCanceledException) when (quiet.IsCancellationRequested) { break; }
            }
        }
        Assert.Equal(1, resyncHints);
        Assert.Equal(WebSocketState.Open, socket.State);
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, workspace))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            var policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(Token);
            policy.IncludeUnreleased = true; policy.Revision++;
            await database.SaveChangesAsync(Token);
        }
        try
        {
            byte[] buffer = new byte[4096];
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
                    if (frame.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);
                if (frame.MessageType == WebSocketMessageType.Close) break;
                CheckFrame(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
        catch (WebSocketException) when (socket.State == WebSocketState.Aborted) { }
    }
}
