using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Domain.Collections;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Api.Captures;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Server.Api.Workspaces;
using ShutterSpool.Server.Security;

namespace ShutterSpool.IntegrationTests;

[CollectionDefinition("Workspace HTTP environment", DisableParallelization = true)]
public sealed class WorkspaceHttpEnvironmentCollection;

[Collection("Workspace HTTP environment")]
public sealed partial class WorkspaceTenancyHttpTests
{
    private const string Issuer = "https://identity.example";
    private static readonly byte[] SigningKey = "synthetic-tenant-http-signing-key-at-least-32-bytes"u8.ToArray();
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_requires_authentication_and_returns_only_current_memberships_without_realm_admin_claims()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        using HttpClient anonymous = fixture.Server.CreateClient();
        using HttpResponseMessage unauthenticated = await anonymous.GetAsync("/workspaces", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using HttpClient outsider = fixture.Client("outsider");
        Assert.Empty((await DiscoveryAsync(outsider)).Items);

        WorkspaceDefinition first = fixture.Workspaces[0], second = fixture.Workspaces[1];
        using HttpClient owner = fixture.Client(first.Name);
        WorkspaceListResponse original = await DiscoveryAsync(owner);
        Assert.Equal(first.Id, Assert.Single(original.Items).Id);
        Assert.False(original.Incomplete);
        Assert.True(original.SelectionRequired);
        await fixture.SetGrantAsync(second, first.Name, true);
        Assert.Equal(fixture.Workspaces.Select(item => item.Id).Order(), (await DiscoveryAsync(owner)).Items.Select(item => item.Id).Order());
        await fixture.SetGrantAsync(first, first.Name, false);
        Assert.Equal(second.Id, Assert.Single((await DiscoveryAsync(owner)).Items).Id);
        using HttpResponseMessage revoked = await SendAsync(owner, HttpMethod.Get, "/management/captures", first.Id);
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
    }

    [Fact]
    public async Task Discovery_marks_unavailable_tenant_membership_as_incomplete_and_control_failure_is_unavailable()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition first = fixture.Workspaces[0], second = fixture.Workspaces[1];
        await fixture.SetGrantAsync(second, first.Name, true);
        using HttpClient client = fixture.Client(first.Name);
        string tenantKey = "ConnectionStrings:" + second.ConnectionReference;
        string originalTenant = fixture.Storage.Configuration[tenantKey]!;
        fixture.Storage.Configuration[tenantKey] = new NpgsqlConnectionStringBuilder(originalTenant) { Password = "synthetic-invalid-runtime-password" }.ConnectionString;
        try
        {
            WorkspaceListResponse unavailable = await DiscoveryAsync(client);
            Assert.Equal(first.Id, Assert.Single(unavailable.Items).Id);
            Assert.True(unavailable.Incomplete);
            using HttpResponseMessage denied = await SendAsync(client, HttpMethod.Get, "/management/captures", second.Id);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        }
        finally { fixture.Storage.Configuration[tenantKey] = originalTenant; }

        string controlKey = "ConnectionStrings:shutterspool-control";
        string originalControl = fixture.Storage.Configuration[controlKey]!;
        fixture.Storage.Configuration[controlKey] = new NpgsqlConnectionStringBuilder(originalControl) { Password = "synthetic-invalid-control-password" }.ConnectionString;
        try
        {
            using HttpResponseMessage unavailable = await client.GetAsync("/workspaces", Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally { fixture.Storage.Configuration[controlKey] = originalControl; }
    }

    [Fact]
    public async Task Private_http_paths_require_one_authorized_workspace_and_overlapping_ids_read_selected_tenant_only()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition first = fixture.Workspaces[0], second = fixture.Workspaces[1];
        Guid commonSource = Guid.NewGuid(), commonCapture = Guid.NewGuid();
        await fixture.SeedAsync(first, commonSource, (commonCapture, "first-only"));
        await fixture.SeedAsync(second, commonSource, (commonCapture, "second-only"), (Guid.NewGuid(), "second-extra"));
        using HttpClient client = fixture.Client(first.Name);
        foreach (string path in new[] { "/management/captures", $"/media/previews/{commonCapture}", "/live/changes/negotiate?negotiateVersion=1" })
        {
            HttpMethod method = path.StartsWith("/live", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get;
            using HttpResponseMessage missing = await SendAsync(client, method, path, null);
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            using HttpResponseMessage foreign = await SendAsync(client, method, path, second.Id);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
            using HttpResponseMessage unknown = await SendAsync(client, method, path, Guid.NewGuid());
            Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);
        }
        using (var ambiguous = new HttpRequestMessage(HttpMethod.Get, "/management/captures"))
        {
            ambiguous.Headers.TryAddWithoutValidation(WorkspaceSelection.Header, new[] { first.Id.ToString("D"), second.Id.ToString("D") });
            using HttpResponseMessage response = await client.SendAsync(ambiguous, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using HttpResponseMessage firstRead = await SendAsync(client, HttpMethod.Get, $"/management/captures/{commonCapture}", first.Id);
        Assert.Equal(HttpStatusCode.OK, firstRead.StatusCode);
        CaptureDetailResponse firstDetail = (await firstRead.Content.ReadFromJsonAsync<CaptureDetailResponse>(Token))!;
        Assert.Equal(first.Name, firstDetail.Source.Name);
        await fixture.SetGrantAsync(second, first.Name, true);
        using HttpResponseMessage secondRead = await SendAsync(client, HttpMethod.Get, $"/management/captures/{commonCapture}", second.Id);
        Assert.Equal(HttpStatusCode.OK, secondRead.StatusCode);
        Assert.Equal(second.Name, (await secondRead.Content.ReadFromJsonAsync<CaptureDetailResponse>(Token))!.Source.Name);
        using HttpResponseMessage firstList = await SendAsync(client, HttpMethod.Get, "/management/captures?limit=1", first.Id);
        CaptureInboxResponse firstPage = (await firstList.Content.ReadFromJsonAsync<CaptureInboxResponse>(Token))!;
        Assert.Single(firstPage.Items);
        Assert.Null(firstPage.NextCursor);
        using HttpResponseMessage secondList = await SendAsync(client, HttpMethod.Get, "/management/captures?limit=1", second.Id);
        CaptureInboxResponse secondPage = (await secondList.Content.ReadFromJsonAsync<CaptureInboxResponse>(Token))!;
        Assert.Single(secondPage.Items);
        Assert.NotNull(secondPage.NextCursor);
        using HttpResponseMessage next = await SendAsync(client, HttpMethod.Get, "/management/captures?limit=1&cursor=" + Uri.EscapeDataString(secondPage.NextCursor!), second.Id);
        Assert.Single((await next.Content.ReadFromJsonAsync<CaptureInboxResponse>(Token))!.Items);
    }

    [Fact]
    public async Task Mixed_tenant_bulk_and_direct_media_ids_never_modify_or_expose_the_other_database()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition first = fixture.Workspaces[0], second = fixture.Workspaces[1];
        Guid source = Guid.NewGuid(), common = Guid.NewGuid(), foreign = Guid.NewGuid();
        await fixture.SeedAsync(first, source, (common, "owned"));
        await fixture.SeedAsync(second, source, (common, "same-id-foreign"), (foreign, "foreign"));
        using HttpClient client = fixture.Client(first.Name);
        var body = new CaptureBulkRequest([
            new() { Id = common, ExpectedRevision = 0, Rating = 4, Label = null, Tags = ["owned"] },
            new() { Id = foreign, ExpectedRevision = 0, Rating = 5, Label = null, Tags = ["must-not-cross"] },
        ]);
        using HttpResponseMessage bulk = await SendAsync(client, HttpMethod.Post, "/management/captures/batch/curation", first.Id, body);
        Assert.Equal(HttpStatusCode.OK, bulk.StatusCode);
        CaptureBulkResponse result = (await bulk.Content.ReadFromJsonAsync<CaptureBulkResponse>(Token))!;
        Assert.Equal("committed", result.Items.Single(item => item.Id == common).Status);
        CaptureBulkItemResponse rejected = result.Items.Single(item => item.Id == foreign);
        Assert.Equal("not_found", rejected.Status);
        Assert.Null(rejected.Current);
        using (HttpResponseMessage preview = await SendAsync(client, HttpMethod.Get, $"/media/previews/{common}", first.Id))
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        foreach (string path in new[] { $"/management/captures/{foreign}", $"/media/previews/{foreign}" })
        {
            using HttpResponseMessage denied = await SendAsync(client, HttpMethod.Get, path, first.Id);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, first))
            Assert.Equal(4, (await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Captures.SingleAsync(Token)).Rating);
        await using (var scope = fixture.Storage.Scope(fixture.Server.Services, second))
        {
            CaptureEntity[] untouched = await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Captures.ToArrayAsync(Token);
            Assert.Equal(2, untouched.Length);
            Assert.All(untouched, item => { Assert.Equal(0, item.Rating); Assert.Empty(item.Tags); Assert.Equal(0, item.Revision); });
        }
    }

    [Fact]
    public async Task Live_connections_receive_only_their_workspace_hints_and_close_after_database_membership_revocation()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition first = fixture.Workspaces[0], second = fixture.Workspaces[1];
        await fixture.SetGrantAsync(second, first.Name, true);
        using WebSocket firstSocket = await fixture.ConnectAsync(first, first.Name);
        using WebSocket secondSocket = await fixture.ConnectAsync(second, first.Name);
        Guid firstHint = Guid.NewGuid(), secondHint = Guid.NewGuid(), lastFirstHint = Guid.NewGuid();
        fixture.Publish(first, firstHint);
        Assert.Equal(firstHint, await ReceiveCaptureHintAsync(firstSocket));
        fixture.Publish(second, secondHint);
        Assert.Equal(secondHint, await ReceiveCaptureHintAsync(secondSocket));
        fixture.Publish(first, lastFirstHint);
        Assert.Equal(lastFirstHint, await ReceiveCaptureHintAsync(firstSocket));
        await fixture.SetGrantAsync(first, first.Name, false);
        using HttpClient client = fixture.Client(first.Name);
        using HttpResponseMessage rejected = await SendAsync(client, HttpMethod.Post, "/live/changes/negotiate?negotiateVersion=1", first.Id);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            byte[] buffer = new byte[4096];
            WebSocketReceiveResult frame;
            do { frame = await firstSocket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token); }
            while (frame.MessageType != WebSocketMessageType.Close);
        }
        catch (WebSocketException) when (firstSocket.State == WebSocketState.Aborted) { }
        Guid survivingHint = Guid.NewGuid();
        fixture.Publish(second, survivingHint);
        Assert.Equal(survivingHint, await ReceiveCaptureHintAsync(secondSocket));
    }

    [Fact]
    public async Task Long_poll_connection_token_cannot_be_replayed_with_another_authorized_workspace()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition first = fixture.Workspaces[0], second = fixture.Workspaces[1];
        await fixture.SetGrantAsync(second, first.Name, true);
        using HttpClient client = fixture.Client(first.Name);
        client.DefaultRequestHeaders.Add("Origin", "http://localhost");
        string path = await StartLongPollingAsync(client, first.Id);
        string replayPath = path.Replace(first.Id.ToString("D"), second.Id.ToString("D"), StringComparison.Ordinal);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Task<HttpResponseMessage> replay = client.GetAsync(replayPath, deadline.Token);
            Guid ownHint = Guid.NewGuid();
            fixture.Publish(first, ownHint);
            using HttpResponseMessage rejected = await replay;
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            using HttpResponseMessage own = await client.GetAsync(path, deadline.Token);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Contains(ownHint.ToString("D"), await own.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
        }
        finally
        {
            using var stop = new HttpRequestMessage(HttpMethod.Delete, path);
            using HttpResponseMessage stopped = await client.SendAsync(stop, Token);
        }
    }

    [Fact]
    public async Task Tenant_owner_cannot_export_or_reconcile_operator_recovery_manifests()
    {
        await using var fixture = await TenantHttpFixture.CreateAsync();
        WorkspaceDefinition workspace = fixture.Workspaces[0];
        using HttpClient client = fixture.Client(workspace.Name);
        string prefix = "/management/retention/erasures/suppressions";
        using HttpResponseMessage manifest = await SendAsync(client, HttpMethod.Get, $"{prefix}/exports/{Guid.NewGuid():D}/manifest", workspace.Id);
        Assert.Equal(HttpStatusCode.Forbidden, manifest.StatusCode);
        using HttpResponseMessage export = await SendAsync(client, HttpMethod.Post, prefix + "/exports", workspace.Id);
        Assert.Equal(HttpStatusCode.Forbidden, export.StatusCode);
        using HttpResponseMessage reconcile = await SendAsync(client, HttpMethod.Post, prefix + "/reconciliation", workspace.Id,
            new { expectedSnapshotId = Guid.NewGuid(), protectedManifest = "synthetic-protected-manifest" });
        Assert.Equal(HttpStatusCode.Forbidden, reconcile.StatusCode);
    }

    private static async Task<string> StartLongPollingAsync(HttpClient client, Guid workspace)
    {
        using HttpResponseMessage negotiated = await client.PostAsync($"/live/changes/negotiate?negotiateVersion=1&workspace={workspace:D}", null, Token);
        Assert.Equal(HttpStatusCode.OK, negotiated.StatusCode);
        using JsonDocument negotiation = JsonDocument.Parse(await negotiated.Content.ReadAsStringAsync(Token));
        string connection = negotiation.RootElement.GetProperty("connectionToken").GetString()!;
        string path = $"/live/changes?workspace={workspace:D}&id={Uri.EscapeDataString(connection)}";
        using HttpResponseMessage initial = await client.GetAsync(path, Token);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var handshake = new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e", Encoding.UTF8, "text/plain");
        using HttpResponseMessage sent = await client.PostAsync(path, handshake, Token);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using HttpResponseMessage connected = await client.GetAsync(path, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
        Assert.Contains("{}\u001e", await connected.Content.ReadAsStringAsync(deadline.Token), StringComparison.Ordinal);
        return path;
    }

    private static async Task<WorkspaceListResponse> DiscoveryAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/workspaces", Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkspaceListResponse>(Token))!;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, Guid? workspace, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (workspace.HasValue) request.Headers.Add(WorkspaceSelection.Header, workspace.Value.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, Token);
    }

    private static string Bearer(string subject) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer,
        "shutterspool", [new Claim(ClaimTypes.NameIdentifier, subject)], expires: DateTime.UtcNow.AddMinutes(5),
        signingCredentials: new(new SymmetricSecurityKey(SigningKey), SecurityAlgorithms.HmacSha256)));

    private static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken token)
    {
        using var message = new MemoryStream();
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            Assert.NotEqual(WebSocketMessageType.Close, frame.MessageType);
            message.Write(buffer, 0, frame.Count);
        } while (!frame.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    private static async Task<Guid> ReceiveCaptureHintAsync(WebSocket socket)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            string payload = await ReceiveTextAsync(socket, deadline.Token);
            foreach (string record in payload.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                using JsonDocument json = JsonDocument.Parse(record);
                if (json.RootElement.GetProperty("type").GetInt32() == 6) continue;
                Assert.Equal("captureChanged", json.RootElement.GetProperty("target").GetString());
                return json.RootElement.GetProperty("arguments")[0].GetProperty("captureId").GetGuid();
            }
        }
    }

    private sealed class TenantHttpFixture(WorkspaceTenancyPostgresTests.WorkspaceFixture storage,
        WebApplicationFactory<Program> server, WorkspaceDefinition[] workspaces) : IAsyncDisposable
    {
        public WorkspaceTenancyPostgresTests.WorkspaceFixture Storage { get; } = storage;
        public WebApplicationFactory<Program> Server { get; } = server;
        public WorkspaceDefinition[] Workspaces { get; } = workspaces;

        public static async Task<TenantHttpFixture> CreateAsync()
        {
            var storage = await WorkspaceTenancyPostgresTests.WorkspaceFixture.CreateAsync();
            await storage.ProvisionAsync();
            storage.Configuration["ShutterSpool:Preview:PrewarmMode"] = "Off";
            storage.Configuration["Logging:LogLevel:Default"] = "Warning";
            var server = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddConfiguration(storage.Configuration));
                builder.ConfigureTestServices(services =>
                {
                    // HTTP assertions own request behavior; worker mutation/recovery has its own real-database suite.
                    services.RemoveAll<IHostedService>();
                    services.AddHostedService(provider => provider.GetRequiredService<LiveChangeBuffer>());
                    services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options => options.TokenValidationParameters = new()
                    {
                        ValidIssuer = Issuer, ValidAudience = "shutterspool", IssuerSigningKey = new SymmetricSecurityKey(SigningKey),
                        ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true, ValidateLifetime = true,
                        ClockSkew = TimeSpan.Zero,
                    });
                });
            });
            string? previousTenancy = Environment.GetEnvironmentVariable("ShutterSpool__Workspaces__Enabled");
            try
            {
                // Program selects tenancy registrations before WebApplicationFactory applies app configuration.
                Environment.SetEnvironmentVariable("ShutterSpool__Workspaces__Enabled", "true");
                _ = server.Services;
                return new(storage, server, await storage.RegistryAsync());
            }
            catch { await server.DisposeAsync(); await storage.DisposeAsync(); throw; }
            finally { Environment.SetEnvironmentVariable("ShutterSpool__Workspaces__Enabled", previousTenancy); }
        }

        public HttpClient Client(string subject)
        {
            HttpClient client = Server.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", Bearer(subject));
            return client;
        }

        public async Task SetGrantAsync(WorkspaceDefinition workspace, string subject, bool active)
        {
            await using var scope = Storage.Scope(Server.Services, workspace);
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            AuthorizationGrant? grant = await database.AuthorizationGrants.SingleOrDefaultAsync(item => item.Issuer == Issuer && item.Subject == subject, Token);
            if (grant is null) database.AuthorizationGrants.Add(new() { Issuer = Issuer, Subject = subject, IsActive = active });
            else grant.IsActive = active;
            await database.SaveChangesAsync(Token);
        }

        public async Task SeedAsync(WorkspaceDefinition workspace, Guid source, params (Guid Id, string Name)[] captures)
        {
            await using var scope = Storage.Scope(Server.Services, workspace);
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            database.Sources.Add(new() { Id = source, Name = workspace.Name, CollectionId = CollectionName.BuiltInId });
            DateTimeOffset time = DateTimeOffset.UtcNow.AddMinutes(-1);
            for (int index = 0; index < captures.Length; index++)
                database.Captures.Add(new() { Id = captures[index].Id, SourceId = source, CollectionId = CollectionName.BuiltInId,
                    NormalizedStem = captures[index].Name, FirstAcceptedAt = time.AddSeconds(index), LastPrimaryIngestedAt = time.AddSeconds(index) });
            await database.SaveChangesAsync(Token);
        }

        public async Task<WebSocket> ConnectAsync(WorkspaceDefinition workspace, string subject)
        {
            WebSocketClient client = Server.Server.CreateWebSocketClient();
            client.ConfigureRequest = request => request.Headers["Origin"] = "http://localhost";
            WebSocket socket = await client.ConnectAsync(new Uri($"ws://localhost/live/changes?workspace={workspace.Id:D}&access_token={Uri.EscapeDataString(Bearer(subject))}"), Token);
            await socket.SendAsync("{\"protocol\":\"json\",\"version\":1}\u001e"u8.ToArray(), WebSocketMessageType.Text, true, Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Assert.StartsWith("{}\u001e", await ReceiveTextAsync(socket, deadline.Token), StringComparison.Ordinal);
            return socket;
        }

        public void Publish(WorkspaceDefinition workspace, Guid capture)
        {
            using var scope = Storage.Scope(Server.Services, workspace);
            scope.ServiceProvider.GetRequiredService<LiveChangeNotifier>().CaptureChanged(capture, "curation");
        }

        public async ValueTask DisposeAsync() { await Server.DisposeAsync(); await Storage.DisposeAsync(); }
    }
}
