using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Api.Live;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed class SecurityBoundaryTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/management/status")]
    [InlineData("/management/captures")]
    [InlineData("/management/captures/11111111-1111-1111-1111-111111111111")]
    [InlineData("/media/previews/example")]
    [InlineData("/media/previews/example/detail")]
    [InlineData("/media/originals/example")]
    [InlineData("/live/status")]
    public async Task Private_paths_reject_anonymous_requests(string path)
    {
        using HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Public_auth_configuration_contains_only_browser_oidc_settings()
    {
        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.GetAsync("/auth/config", TestContext.Current.CancellationToken);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "audience", "authority", "clientId" },
            body.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("shutterspool-web", body.RootElement.GetProperty("clientId").GetString());
    }

    [Fact]
    public async Task Query_bearer_is_accepted_only_for_live_hub_negotiate()
    {
        byte[] signingKey = Encoding.UTF8.GetBytes("test-only-signing-key-must-be-at-least-32-bytes");
        var logs = new ConcurrentQueue<string>();
        using WebApplicationFactory<Program> server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ShutterSpoolDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ShutterSpoolDbContext>>();
            services.AddDbContextFactory<ShutterSpoolDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
            services.AddLogging(logging => logging.AddProvider(new CapturedLogProvider(logs)));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "https://issuer.test", ValidAudience = "shutterspool",
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey), ValidateIssuer = true,
                    ValidateAudience = true, ValidateIssuerSigningKey = true, ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        }));
        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
        {
            ShutterSpoolDbContext database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            database.AuthorizationGrants.Add(new AuthorizationGrant { Issuer = "https://issuer.test", Subject = "operator", IsActive = true });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using HttpClient client = server.CreateClient();
        string token = CreateToken(signingKey, "shutterspool-admin");
        using HttpRequestMessage negotiate = new(HttpMethod.Post,
            $"/live/changes/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(token)}");
        negotiate.Headers.TryAddWithoutValidation("Origin", "http://localhost");
        using HttpResponseMessage accepted = await client.SendAsync(negotiate, TestContext.Current.CancellationToken);
        using HttpResponseMessage rejected = await client.GetAsync(
            $"/management/status?access_token={Uri.EscapeDataString(token)}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.DoesNotContain(logs, entry => entry.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Private_paths_reject_authenticated_principals_without_the_administrator_role()
    {
        byte[] signingKey = Encoding.UTF8.GetBytes("test-only-signing-key-must-be-at-least-32-bytes");
        using WebApplicationFactory<Program> server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "https://issuer.test",
                    ValidAudience = "shutterspool",
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        }));
        using HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(signingKey, "viewer"));

        HttpResponseMessage response = await client.GetAsync("/management/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Private_paths_require_an_active_local_grant_even_for_administrators()
    {
        byte[] signingKey = Encoding.UTF8.GetBytes("test-only-signing-key-must-be-at-least-32-bytes");
        using WebApplicationFactory<Program> server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ShutterSpoolDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ShutterSpoolDbContext>>();
            services.AddDbContextFactory<ShutterSpoolDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString("N")));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "https://issuer.test",
                    ValidAudience = "shutterspool",
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        }));
        using HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(signingKey, "shutterspool-admin"));

        HttpResponseMessage response = await client.GetAsync("/management/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("https://wrong-issuer.test", "shutterspool", 5)]
    [InlineData("https://issuer.test", "wrong-audience", 5)]
    [InlineData("https://issuer.test", "shutterspool", -1)]
    public async Task Private_paths_reject_invalid_bearer_tokens(string issuer, string audience, int lifetimeMinutes)
    {
        byte[] signingKey = Encoding.UTF8.GetBytes("test-only-signing-key-must-be-at-least-32-bytes");
        using WebApplicationFactory<Program> server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "https://issuer.test",
                    ValidAudience = "shutterspool",
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        }));
        using HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(signingKey, "shutterspool-admin", issuer, audience, lifetimeMinutes));

        HttpResponseMessage response = await client.GetAsync("/management/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Private_paths_allow_an_administrator_with_an_active_local_grant()
    {
        byte[] signingKey = Encoding.UTF8.GetBytes("test-only-signing-key-must-be-at-least-32-bytes");
        string databaseName = Guid.NewGuid().ToString("N");
        var previews = new CachedPreview();
        using WebApplicationFactory<Program> server = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ShutterSpoolDbContext>>();
            services.RemoveAll<Microsoft.EntityFrameworkCore.Infrastructure.IDbContextOptionsConfiguration<ShutterSpoolDbContext>>();
            services.AddDbContextFactory<ShutterSpoolDbContext>(options => options.UseInMemoryDatabase(databaseName));
            services.RemoveAll<IPreviewSourceResolver>();
            services.RemoveAll<IPreviewCache>();
            services.RemoveAll<IPreviewGenerator>();
            services.AddSingleton<IPreviewSourceResolver>(previews);
            services.AddSingleton<IPreviewCache>(previews);
            services.AddSingleton<IPreviewGenerator>(previews);
            services.RemoveAll<IOriginalAssetLock>();
            services.AddSingleton<IOriginalAssetLock, SyntheticPreviewLock>();
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "https://issuer.test",
                    ValidAudience = "shutterspool",
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.Zero,
                };
            });
        }));
        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
        {
            ShutterSpoolDbContext database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            database.AuthorizationGrants.Add(new AuthorizationGrant { Issuer = "https://issuer.test", Subject = "operator", IsActive = true });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using HttpClient client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", CreateToken(signingKey, "shutterspool-admin"));

        HttpResponseMessage response = await client.GetAsync("/management/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using HttpResponseMessage inbox = await client.GetAsync("/management/captures", TestContext.Current.CancellationToken);
        using JsonDocument inboxBody = JsonDocument.Parse(await inbox.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, inbox.StatusCode);
        Assert.Empty(inboxBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Null(inboxBody.RootElement.GetProperty("nextCursor").GetString());

        string previewPath = $"/media/previews/{Guid.NewGuid():N}/grid";
        HttpResponseMessage cached = await client.GetAsync(previewPath, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        Assert.True(cached.Headers.CacheControl?.Private);
        Assert.True(cached.Headers.CacheControl?.NoCache);
        Assert.NotNull(cached.Headers.ETag);
        Assert.Equal("image/webp", cached.Content.Headers.ContentType?.MediaType);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, previewPath);
        conditional.Headers.IfNoneMatch.Add(cached.Headers.ETag!);
        using HttpResponseMessage notModified = await client.SendAsync(conditional, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(cached.Headers.ETag, notModified.Headers.ETag);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.True(previews.Lookups > 0);

        previews.Pending = true;
        using var noLongerReady = new HttpRequestMessage(HttpMethod.Get, previewPath);
        noLongerReady.Headers.IfNoneMatch.Add(cached.Headers.ETag!);
        using HttpResponseMessage placeholder = await client.SendAsync(noLongerReady, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, placeholder.StatusCode);
        Assert.True(placeholder.Headers.CacheControl?.NoStore);
        Assert.Null(placeholder.Headers.ETag);
        int authorizedLookups = previews.Lookups;

        await using (AsyncServiceScope scope = server.Services.CreateAsyncScope())
        {
            ShutterSpoolDbContext database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            (await database.AuthorizationGrants.SingleAsync(TestContext.Current.CancellationToken)).IsActive = false;
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(previewPath, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/management/captures", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(authorizedLookups, previews.Lookups);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(previewPath, TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(authorizedLookups, previews.Lookups);
    }

    [Fact]
    public void Live_status_rejects_a_principal_after_its_token_expires()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("exp", DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds().ToString())]));

        Assert.False(LiveStatusEndpoint.HasUnexpiredToken(principal, DateTimeOffset.UtcNow));
    }

    private static string CreateToken(byte[] signingKey, string role, string issuer = "https://issuer.test", string audience = "shutterspool", int lifetimeMinutes = 5)
    {
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: [new Claim(ClaimTypes.NameIdentifier, "operator"), new Claim(ClaimTypes.Role, role)],
            expires: DateTime.UtcNow.AddMinutes(lifetimeMinutes),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(signingKey), SecurityAlgorithms.HmacSha256));
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

    private sealed class SyntheticPreviewLock : IOriginalAssetLock
    {
        public Task<IOriginalAssetLockLease> AcquireSharedAsync(Guid assetId, string storageKey,
            CancellationToken cancellationToken) => Task.FromResult<IOriginalAssetLockLease>(new Lease(assetId, storageKey));
        public Task<IOriginalAssetLockLease> AcquireExclusiveAsync(Guid assetId, string storageKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(IOriginalAssetLockLease lease, CancellationToken cancellationToken) => Task.FromResult(true);
        private sealed record Lease(Guid AssetId, string StorageKey) : IOriginalAssetLockLease
        {
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class CachedPreview : IPreviewSourceResolver, IPreviewCache, IPreviewGenerator
    {
        public int Lookups { get; private set; }
        public bool Pending { get; set; }
        public Task<PreviewResolution> ResolveAsync(Guid captureId, CancellationToken cancellationToken)
        {
            Lookups++;
            return Task.FromResult(new PreviewResolution(true, Pending ? null : new PreviewSource(Guid.Empty, "spool/test", 12)));
        }
        public Task<byte[]?> ReadAsync(string key, PreviewSize size, CancellationToken cancellationToken) => Task.FromResult<byte[]?>("RIFF\0\0\0\0WEBP"u8.ToArray());
        public Task<bool> ContainsAsync(string key, PreviewSize size, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<bool> WriteAsync(string key, byte[] content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
