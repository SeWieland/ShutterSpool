using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry;
using OpenTelemetry.Trace;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Security;
using ShutterSpool.Infrastructure.Persistence;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed class LiveChangesHubTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly byte[] SigningKey = Encoding.UTF8.GetBytes("test-only-signing-key-must-be-at-least-32-bytes");
    private const string Issuer = "https://issuer.test";
    private const string Audience = "shutterspool";
    private const string Origin = "http://localhost";
    private const string HubPath = "/live/changes";

    [Fact]
    public async Task WebSocket_connects_with_query_bearer_and_logs_do_not_expose_it()
    {
        var logs = new ConcurrentQueue<string>();
        using WebApplicationFactory<Program> server = await CreateServerAsync(logs);
        string token = CreateToken(TimeSpan.FromMinutes(5));
        WebSocketClient client = CreateWebSocketClient(server);
        using WebSocket socket = await client.ConnectAsync(CreateHubUri(token), TestContext.Current.CancellationToken);

        await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketState.Open, socket.State);
        Assert.DoesNotContain(logs, entry => entry.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Query_bearers_are_absent_from_exported_server_traces_and_logs()
    {
        var logs = new ConcurrentQueue<string>();
        var activities = new CapturedActivityExporter();
        using WebApplicationFactory<Program> server = await CreateServerAsync(logs, activities);
        string token = CreateToken(TimeSpan.FromMinutes(5));
        const string rejectedToken = "synthetic-invalid-query-token";

        HttpContext authConfig = await server.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = "/auth/config";
        }, TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status200OK, authConfig.Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, await SendNegotiateAsync(server, token));
        Assert.Equal(StatusCodes.Status401Unauthorized, await SendNegotiateAsync(server, rejectedToken));

        WebSocketClient webSocketClient = CreateWebSocketClient(server);
        using WebSocket socket = await webSocketClient.ConnectAsync(CreateHubUri(token), TestContext.Current.CancellationToken);
        await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test finished", TestContext.Current.CancellationToken);

        try
        {
            using WebSocket rejected = await CreateWebSocketClient(server).ConnectAsync(
                CreateHubUri(rejectedToken), TestContext.Current.CancellationToken);
            Assert.Fail("An invalid bearer token connected to the live hub.");
        }
        catch (WebSocketException) { }
        catch (InvalidOperationException) { }

        Activity[] exported = [.. activities.Activities];
        Assert.Contains(exported, activity => activity.Source.Name == "Microsoft.AspNetCore"
            && activity.Tags.Any(tag => tag.Key == "http.route" && tag.Value == "/auth/config"));
        Assert.DoesNotContain(exported.SelectMany(activity => activity.Tags).Select(tag => tag.Value?.ToString()), value =>
            value?.Contains(token, StringComparison.Ordinal) == true
            || value?.Contains(rejectedToken, StringComparison.Ordinal) == true);
        Assert.DoesNotContain(logs, entry => entry.Contains(token, StringComparison.Ordinal)
            || entry.Contains(rejectedToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WebSocket_rejects_an_unconfigured_origin()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        string token = CreateToken(TimeSpan.FromMinutes(5));
        WebSocketClient client = CreateWebSocketClient(server, "https://attacker.test");
        using WebSocket socket = await client.ConnectAsync(CreateHubUri(token), TestContext.Current.CancellationToken);

        try { await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken); }
        catch (WebSocketException) { }
        await AssertConnectionClosesAsync(socket, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocket_rejects_a_missing_origin()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        using WebSocket socket = await CreateWebSocketClient(server, origin: null)
            .ConnectAsync(CreateHubUri(CreateToken(TimeSpan.FromMinutes(5))), TestContext.Current.CancellationToken);

        try { await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken); }
        catch (WebSocketException) { }
        await AssertConnectionClosesAsync(socket, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WebSocket_closes_after_the_local_grant_is_revoked()
    {
        WebApplicationFactory<Program> server = await CreateServerAsync();
        using (server)
        {
            WebSocketClient client = CreateWebSocketClient(server);
            using WebSocket socket = await client.ConnectAsync(CreateHubUri(CreateToken(TimeSpan.FromMinutes(5))), TestContext.Current.CancellationToken);
            await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken);

            await SetGrantActiveAsync(server, false);
            await AssertConnectionClosesAsync(socket, TimeSpan.FromSeconds(35));
        }
    }

    [Fact]
    public async Task WebSocket_closes_when_the_authoritative_grant_store_becomes_unavailable()
    {
        var grants = new ControlledGrantStore();
        using WebApplicationFactory<Program> server = await CreateServerAsync(grantStore: grants);
        using WebSocket socket = await CreateWebSocketClient(server)
            .ConnectAsync(CreateHubUri(CreateToken(TimeSpan.FromMinutes(5))), TestContext.Current.CancellationToken);
        await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken);

        grants.Unavailable = true;
        await AssertConnectionClosesAsync(socket, TimeSpan.FromSeconds(35));
    }

    [Fact]
    public async Task WebSocket_closes_when_the_bearer_token_expires()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        WebSocketClient client = CreateWebSocketClient(server);
        using WebSocket socket = await client.ConnectAsync(CreateHubUri(CreateToken(TimeSpan.FromSeconds(3))), TestContext.Current.CancellationToken);
        await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken);

        await AssertConnectionClosesAsync(socket, TimeSpan.FromSeconds(10));

        using WebSocket renewed = await CreateWebSocketClient(server).ConnectAsync(
            CreateHubUri(CreateToken(TimeSpan.FromMinutes(5))), TestContext.Current.CancellationToken);
        await CompleteHandshakeAsync(renewed, TestContext.Current.CancellationToken);
        Guid captureId = Guid.NewGuid();
        server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeNotifier>()
            .CaptureChanged(captureId, "delivery");
        string hint = await ReadTextAsync(renewed, TestContext.Current.CancellationToken);
        Assert.Contains("captureChanged", hint, StringComparison.Ordinal);
        Assert.Contains(captureId.ToString(), hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WebSocket_coalesces_per_capture_and_resynchronizes_after_bounded_overflow()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        using WebSocket socket = await CreateWebSocketClient(server)
            .ConnectAsync(CreateHubUri(CreateToken(TimeSpan.FromMinutes(5))), TestContext.Current.CancellationToken);
        await CompleteHandshakeAsync(socket, TestContext.Current.CancellationToken);
        var changes = server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeNotifier>();

        Guid repeatedCapture = Guid.NewGuid();
        changes.CaptureChanged(repeatedCapture, "curation");
        changes.CaptureChanged(repeatedCapture, "preview");
        changes.CaptureChanged(repeatedCapture, "delivery");
        using (var coalesceTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            coalesceTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            string coalesced = await ReadTextAsync(socket, coalesceTimeout.Token);
            Assert.Contains("captureChanged", coalesced, StringComparison.Ordinal);
            Assert.Contains(repeatedCapture.ToString(), coalesced, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("delivery", coalesced, StringComparison.Ordinal);
            Assert.DoesNotContain("curation", coalesced, StringComparison.Ordinal);
            Assert.DoesNotContain("preview", coalesced, StringComparison.Ordinal);
        }

        using (var quietTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            quietTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadTextAsync(socket, quietTimeout.Token));
        }

        Guid[] burstCaptures = [.. Enumerable.Range(0, 2_000).Select(_ => Guid.NewGuid())];
        foreach (Guid captureId in burstCaptures) changes.CaptureChanged(captureId, "burst");
        var frames = new List<string>();
        using var overflowTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        overflowTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!frames.Any(frame => frame.Contains("resyncRequired", StringComparison.Ordinal)))
            frames.Add(await ReadTextAsync(socket, overflowTimeout.Token));

        using (var quietTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            quietTimeout.CancelAfter(TimeSpan.FromMilliseconds(300));
            while (true)
            {
                try { frames.Add(await ReadTextAsync(socket, quietTimeout.Token)); }
                catch (OperationCanceledException) when (quietTimeout.IsCancellationRequested) { break; }
            }
        }

        int captureHints = frames.Sum(frame => frame.Split("captureChanged", StringSplitOptions.None).Length - 1);
        Guid[] deliveredCaptures = [.. burstCaptures.Where(id => frames.Any(frame =>
            frame.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase)))];
        Assert.InRange(captureHints, 1, 512);
        Assert.Equal(captureHints, deliveredCaptures.Length);
        Assert.Equal(deliveredCaptures.Length, deliveredCaptures.Distinct().Count());
        Assert.Contains(frames, frame => frame.Contains("resyncRequired", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Receiving_observation_registry_stays_bounded_and_marks_incomplete_coverage()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        var changes = server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeNotifier>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid[] transfers = [.. Enumerable.Range(0, 300).Select(_ => Guid.NewGuid())];

        for (int index = 0; index < transfers.Length; index++)
            changes.Publish(new(transfers[index], Guid.NewGuid(), null, null, CameraTransferPhase.Receiving, now.AddTicks(index),
                1, false, false));

        Assert.False(changes.ActivityCoverageComplete);
        int retained = transfers.Count(id => changes.GetRecentReceiving(id, now.AddSeconds(1)) is not null);
        Assert.InRange(retained, 1, 256);
        Assert.Null(changes.GetRecentReceiving(transfers[0], now.AddSeconds(1)));
        Assert.NotNull(changes.GetRecentReceiving(transfers[^1], now.AddSeconds(1)));
    }

    [Fact]
    public async Task Transfer_hint_versions_increase_across_phase_changes_within_the_same_epoch()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        var changes = server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeNotifier>();
        Guid transferId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        changes.Publish(new(transferId, sourceId, null, null, CameraTransferPhase.Receiving, now, 1, false, false));
        long firstVersion = Assert.IsType<CameraTransferActivity>(changes.GetRecentReceiving(transferId, now)).Version;

        changes.Publish(new(transferId, sourceId, Guid.NewGuid(), Guid.NewGuid(), CameraTransferPhase.Ready,
            now.AddSeconds(1), 4, true, true));
        changes.Publish(new(transferId, sourceId, null, null, CameraTransferPhase.Receiving,
            now.AddSeconds(2), 1, false, false));
        long laterVersion = Assert.IsType<CameraTransferActivity>(changes.GetRecentReceiving(transferId, now.AddSeconds(2))).Version;

        Assert.True(laterVersion > firstVersion);
    }

    [Fact]
    public async Task Preview_activity_registry_renews_at_thirty_seconds_expires_and_stays_bounded()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        var changes = server.Services.GetRequiredService<ShutterSpool.Server.Api.Live.LiveChangeNotifier>();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        changes.Preparing(captureId, assetId, PreviewProfile.Grid, startedAt);
        var first = Assert.IsType<ShutterSpool.Server.Api.Live.PreviewPreparationObservation>(
            changes.GetRecentPreviewPreparation(captureId, assetId, PreviewProfile.Grid, startedAt));

        DateTimeOffset renewedAt = startedAt.AddSeconds(30);
        changes.Preparing(captureId, assetId, PreviewProfile.Grid, renewedAt);
        var renewed = Assert.IsType<ShutterSpool.Server.Api.Live.PreviewPreparationObservation>(
            changes.GetRecentPreviewPreparation(captureId, assetId, PreviewProfile.Grid, renewedAt));
        Assert.Equal(renewedAt, renewed.ObservedAt);
        Assert.True(renewed.Version > first.Version);
        Assert.NotNull(changes.GetRecentPreviewPreparation(captureId, assetId, PreviewProfile.Grid, renewedAt.AddSeconds(59)));
        Assert.Null(changes.GetRecentPreviewPreparation(captureId, assetId, PreviewProfile.Grid, renewedAt.AddSeconds(61)));

        Guid[] identities = [.. Enumerable.Range(0, 300).Select(_ => Guid.NewGuid())];
        for (int index = 0; index < identities.Length; index++)
            changes.Preparing(identities[index], identities[index], PreviewProfile.Grid, renewedAt.AddTicks(index));
        Assert.False(changes.ActivityCoverageComplete);
        DateTimeOffset latestAt = renewedAt.AddSeconds(1);
        Assert.Null(changes.GetRecentPreviewPreparation(identities[0], identities[0], PreviewProfile.Grid, latestAt));
        Assert.NotNull(changes.GetRecentPreviewPreparation(identities[^1], identities[^1], PreviewProfile.Grid, latestAt));
    }

    [Fact]
    public async Task LongPolling_fallback_completes_negotiate_and_hub_handshake_with_query_bearer()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        using HttpClient client = server.CreateClient();
        string token = CreateToken(TimeSpan.FromMinutes(5));
        using HttpRequestMessage negotiate = CreateHubRequest(HttpMethod.Post,
            $"{HubPath}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}");
        using HttpResponseMessage negotiation = await client.SendAsync(negotiate, TestContext.Current.CancellationToken);
        negotiation.EnsureSuccessStatusCode();
        using JsonDocument negotiationBody = JsonDocument.Parse(await negotiation.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        JsonElement longPolling = negotiationBody.RootElement.GetProperty("availableTransports").EnumerateArray()
            .Single(transport => transport.GetProperty("transport").GetString() == "LongPolling");
        Assert.Contains("Text", longPolling.GetProperty("transferFormats").EnumerateArray().Select(format => format.GetString()));
        string connectionToken = negotiationBody.RootElement.GetProperty("connectionToken").GetString()!;

        string pollPath = $"{HubPath}?id={Uri.EscapeDataString(connectionToken)}&access_token={Uri.EscapeDataString(token)}";
        using HttpRequestMessage initialPoll = new(HttpMethod.Get, pollPath);
        using HttpResponseMessage initialResponse = await client.SendAsync(initialPoll, TestContext.Current.CancellationToken);
        initialResponse.EnsureSuccessStatusCode();

        using var handshake = new HttpRequestMessage(HttpMethod.Post, pollPath)
        {
            Content = new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e", Encoding.UTF8, "text/plain"),
        };
        using HttpResponseMessage handshakeResponse = await client.SendAsync(handshake, TestContext.Current.CancellationToken);
        handshakeResponse.EnsureSuccessStatusCode();

        using HttpRequestMessage handshakePoll = new(HttpMethod.Get, pollPath);
        using HttpResponseMessage pollResponse = await client.SendAsync(handshakePoll, TestContext.Current.CancellationToken);
        pollResponse.EnsureSuccessStatusCode();
        string response = await pollResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("{}\u001e", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LongPolling_connection_rejects_requests_after_the_grant_is_revoked()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        using HttpClient client = server.CreateClient();
        string pollPath = await StartLongPollingAsync(client, CreateToken(TimeSpan.FromMinutes(5)));
        await SetGrantActiveAsync(server, false);

        using HttpRequestMessage poll = new(HttpMethod.Get, pollPath);
        using HttpResponseMessage response = await client.SendAsync(poll, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LongPolling_connection_rejects_requests_after_the_bearer_expires()
    {
        using WebApplicationFactory<Program> server = await CreateServerAsync();
        using HttpClient client = server.CreateClient();
        string pollPath = await StartLongPollingAsync(client, CreateToken(TimeSpan.FromSeconds(3)));
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        using HttpRequestMessage poll = new(HttpMethod.Get, pollPath);
        using HttpResponseMessage response = await client.SendAsync(poll, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private async Task<WebApplicationFactory<Program>> CreateServerAsync(ConcurrentQueue<string>? logs = null,
        BaseExporter<Activity>? exporter = null, IAccessGrantStore? grantStore = null)
    {
        string databaseName = Guid.NewGuid().ToString("N");
        WebApplicationFactory<Program> server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ShutterSpoolDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ShutterSpoolDbContext>>();
            services.AddDbContextFactory<ShutterSpoolDbContext>(options => options.UseInMemoryDatabase(databaseName));
            if (grantStore is not null)
            {
                services.RemoveAll<IAccessGrantStore>();
                services.AddSingleton(grantStore);
            }
            if (exporter is not null)
                services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(exporter)));
            if (logs is not null) services.AddLogging(logging => logging.AddProvider(new CapturedLogProvider(logs)));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = Issuer,
                    ValidAudience = Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(SigningKey),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        }));

        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().AuthorizationGrants.Add(
            new AuthorizationGrant { Issuer = Issuer, Subject = "operator", IsActive = true });
        await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().SaveChangesAsync(TestContext.Current.CancellationToken);
        return server;
    }

    private static async Task<int> SendNegotiateAsync(WebApplicationFactory<Program> server, string token)
    {
        HttpContext response = await server.Server.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"{HubPath}/negotiate";
            context.Request.QueryString = new QueryString($"?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}");
            context.Request.Headers.Origin = Origin;
        }, TestContext.Current.CancellationToken);
        return response.Response.StatusCode;
    }

    private static async Task SetGrantActiveAsync(WebApplicationFactory<Program> server, bool active)
    {
        await using AsyncServiceScope scope = server.Services.CreateAsyncScope();
        ShutterSpoolDbContext database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
        (await database.AuthorizationGrants.SingleAsync(TestContext.Current.CancellationToken)).IsActive = active;
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static WebSocketClient CreateWebSocketClient(WebApplicationFactory<Program> server, string? origin = Origin)
    {
        WebSocketClient client = server.Server.CreateWebSocketClient();
        if (origin is not null) client.ConfigureRequest = request => request.Headers["Origin"] = origin;
        return client;
    }

    private static async Task<string> StartLongPollingAsync(HttpClient client, string token)
    {
        using HttpRequestMessage negotiate = CreateHubRequest(HttpMethod.Post,
            $"{HubPath}/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}");
        using HttpResponseMessage negotiation = await client.SendAsync(negotiate, TestContext.Current.CancellationToken);
        negotiation.EnsureSuccessStatusCode();
        using JsonDocument body = JsonDocument.Parse(await negotiation.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        string connectionToken = body.RootElement.GetProperty("connectionToken").GetString()!;
        string pollPath = $"{HubPath}?id={Uri.EscapeDataString(connectionToken)}&access_token={Uri.EscapeDataString(token)}";

        using HttpRequestMessage initialPoll = new(HttpMethod.Get, pollPath);
        using HttpResponseMessage initial = await client.SendAsync(initialPoll, TestContext.Current.CancellationToken);
        initial.EnsureSuccessStatusCode();
        using var handshake = new HttpRequestMessage(HttpMethod.Post, pollPath)
        {
            Content = new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e", Encoding.UTF8, "text/plain"),
        };
        using HttpResponseMessage handshakeResponse = await client.SendAsync(handshake, TestContext.Current.CancellationToken);
        handshakeResponse.EnsureSuccessStatusCode();
        using HttpRequestMessage handshakePoll = new(HttpMethod.Get, pollPath);
        using HttpResponseMessage connected = await client.SendAsync(handshakePoll, TestContext.Current.CancellationToken);
        connected.EnsureSuccessStatusCode();
        Assert.Contains("{}\u001e", await connected.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        return pollPath;
    }

    private static Uri CreateHubUri(string token) => new($"ws://localhost{HubPath}?access_token={Uri.EscapeDataString(token)}");

    private static HttpRequestMessage CreateHubRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        AddOrigin(request, Origin);
        return request;
    }

    private static void AddOrigin(HttpRequestMessage request, string origin) => request.Headers.TryAddWithoutValidation("Origin", origin);

    private static async Task CompleteHandshakeAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] handshake = "{\"protocol\":\"json\",\"version\":1}\u001e"u8.ToArray();
        await socket.SendAsync(handshake, WebSocketMessageType.Text, true, cancellationToken);
        string response = await ReadTextAsync(socket, cancellationToken);
        // SignalR may combine the handshake and a following record in one WebSocket message.
        Assert.StartsWith("{}\u001e", response, StringComparison.Ordinal);
    }

    private static async Task AssertConnectionClosesAsync(WebSocket socket, TimeSpan timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(timeout);
        try
        {
            byte[] buffer = new byte[1024];
            while (true)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Assert.True(elapsed.Elapsed <= timeout, $"The live connection closed after {elapsed.Elapsed}, beyond {timeout}.");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            Assert.Fail($"The live connection did not close within {timeout}.");
        }
        catch (WebSocketException) when (socket.State == WebSocketState.Aborted)
        {
            // TestServer aborts may surface as a transport exception instead of a close frame.
        }
    }

    private static async Task<string> ReadTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Connection closed before the SignalR handshake completed.");
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
        }
    }

    private static string CreateToken(TimeSpan lifetime)
    {
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, "shutterspool-admin")],
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(SigningKey), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class CapturedLogProvider(ConcurrentQueue<string> entries) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(entries);
        public void Dispose() { }
    }

    private sealed class CapturedLogger(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(formatter(state, exception));
    }

    private sealed class ControlledGrantStore : IAccessGrantStore
    {
        public bool Unavailable { get; set; }
        public Task<bool> IsActiveAsync(string issuer, string subject, CancellationToken cancellationToken) =>
            Unavailable ? throw new InvalidOperationException("synthetic authority store outage") : Task.FromResult(true);
        public Task BootstrapAsync(string issuer, string subject, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturedActivityExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Activities { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (Activity activity in batch) Activities.Enqueue(activity);
            return ExportResult.Success;
        }
    }
}
