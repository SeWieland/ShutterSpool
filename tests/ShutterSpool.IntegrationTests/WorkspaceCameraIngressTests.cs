using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Sources;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Composition;

namespace ShutterSpool.IntegrationTests;

public sealed class WorkspaceCameraIngressTests
{
    private const string CallbackKey = "synthetic-private-camera-key-at-least-32-characters";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Private_callbacks_bind_current_source_credentials_and_preserve_accepted_media_after_restart()
    {
        await using var fixture = await WorkspaceTenancyPostgresTests.WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        CreatedSource created;
        Guid login;
        await using (WebApplication server = await StartAsync(fixture))
        {
            await using (var scope = fixture.Scope(server.Services, workspaces[0]))
                created = await scope.ServiceProvider.GetRequiredService<SourceManagementService>().CreateAsync("Synthetic camera", "Delay", 60, Token);
            using HttpClient client = server.GetTestClient();
            using (HttpResponseMessage denied = await client.PostAsJsonAsync("/internal/camera/auth", new { username = created.Username, password = created.Secret }, Token))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            client.DefaultRequestHeaders.Add("X-API-KEY", CallbackKey);
            login = await AuthenticateAsync(client, created.Username, created.Secret);
            string endpoint = $"http://localhost:8081/internal/camera/fs/{login:N}";
            long admitted = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            using (HttpResponseMessage admission = await client.PostAsJsonAsync("/internal/camera/hook", Event("pre-upload", created.Username, endpoint, admitted), Token))
                Assert.Equal(HttpStatusCode.OK, admission.StatusCode);
            client.DefaultRequestHeaders.Add("X-ShutterSpool-Workspace", workspaces[1].Id.ToString("D"));
            using (HttpResponseMessage bytes = await client.PostAsync($"/internal/camera/fs/{login:N}/create/%2Fsynthetic.jpg?flags=577", new ByteArrayContent([4, 5, 6]), Token))
                Assert.Equal(HttpStatusCode.OK, bytes.StatusCode);
            using (HttpResponseMessage completion = await client.PostAsJsonAsync("/internal/camera/hook", Event("upload", created.Username, endpoint, admitted + 1_000_000_000, 3), Token))
                Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
            await AssertCustodyAsync(server, fixture, workspaces);
            await using (var scope = fixture.Scope(server.Services, workspaces[0]))
            {
                var changed = await scope.ServiceProvider.GetRequiredService<SourceManagementService>().RegenerateAsync(created.Id, 0, Token);
                Assert.True(changed.Applied);
                using HttpResponseMessage stale = await client.GetAsync($"/internal/camera/fs/{login:N}/stat/%2Fsynthetic.jpg", Token);
                Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
                using HttpResponseMessage oldPassword = await client.PostAsJsonAsync("/internal/camera/auth", new { username = created.Username, password = created.Secret }, Token);
                Assert.Equal(HttpStatusCode.Forbidden, oldPassword.StatusCode);
                _ = await AuthenticateAsync(client, created.Username, changed.Secret!);
            }
            await server.StopAsync(Token);
        }
        await using WebApplication restarted = await StartAsync(fixture);
        await AssertCustodyAsync(restarted, fixture, workspaces);
        using HttpClient replay = restarted.GetTestClient();
        replay.DefaultRequestHeaders.Add("X-API-KEY", CallbackKey);
        using HttpResponseMessage rejected = await replay.GetAsync($"/internal/camera/fs/{login:N}/stat/%2Fsynthetic.jpg", Token);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        await restarted.StopAsync(Token);
    }

    private static async Task AssertCustodyAsync(WebApplication server, WorkspaceTenancyPostgresTests.WorkspaceFixture fixture, WorkspaceDefinition[] workspaces)
    {
        await using (var own = fixture.Scope(server.Services, workspaces[0]))
        {
            AssetEntity asset = await own.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Assets.SingleAsync(Token);
            Assert.Equal("Spooled", asset.State);
            Assert.Equal("synthetic.jpg", asset.OriginalName);
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(Path.Combine(workspaces[0].StorageRoot, asset.SpoolKey.Replace('/', Path.DirectorySeparatorChar)), Token));
        }
        await using var other = fixture.Scope(server.Services, workspaces[1]);
        Assert.Empty(await other.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Assets.ToArrayAsync(Token));
    }

    private static async Task<Guid> AuthenticateAsync(HttpClient client, string username, string password)
    {
        using HttpResponseMessage response = await client.PostAsJsonAsync("/internal/camera/auth", new { username, password }, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        string endpoint = document.RootElement.GetProperty("filesystem").GetProperty("httpconfig").GetProperty("endpoint").GetString()!;
        return Guid.ParseExact(endpoint.Split('/')[^1], "N");
    }

    private static object Event(string action, string username, string endpoint, long timestamp, long? size = null) => new
    {
        action, username, endpoint, path = "/synthetic.jpg", virtual_path = "/synthetic.jpg", session_id = "synthetic-session",
        timestamp, status = 1, protocol = "FTP", fs_provider = 6, file_size = size,
    };

    private static async Task<WebApplication> StartAsync(WorkspaceTenancyPostgresTests.WorkspaceFixture fixture)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddConfiguration(fixture.Configuration);
        builder.Configuration["Logging:LogLevel:Default"] = "Warning";
        foreach ((string key, string value) in new Dictionary<string, string>
        {
            ["Enabled"] = "true", ["CallbackKey"] = CallbackKey, ["CallbackBaseUrl"] = "http://localhost:8081/internal/camera",
            ["InternalPort"] = "8081", ["Profiles:ModernFtps:Enabled"] = "true", ["Profiles:ModernFtps:ControlPort"] = "2121",
            ["MaximumUploadBytes"] = "1024", ["MaximumConcurrentTransfers"] = "1",
        }) builder.Configuration["ShutterSpool:CameraIngress:" + key] = value;
        builder.WebHost.UseTestServer();
        builder.AddShutterSpoolServer();
        builder.Services.RemoveAll<IHostedService>();
        WebApplication app = builder.Build();
        await app.InitializeShutterSpoolServerAsync();
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/internal/camera")) context.Connection.LocalPort = 8081;
            context.Features.Get<IHttpRequestFeature>()!.RawTarget = context.Request.Path + context.Request.QueryString;
            return next(context);
        });
        app.MapShutterSpoolServer();
        await app.StartAsync(Token);
        return app;
    }
}
