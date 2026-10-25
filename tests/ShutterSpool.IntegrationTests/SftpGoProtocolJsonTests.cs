using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    private const string CameraCallbackKey = "synthetic-camera-callback-key-at-least-32";

    [Fact]
    public async Task Root_initialization_failure_returns_safe_stage_problem_without_authenticating()
    {
        const string username = "cam_root-failure-endpoint";
        const string password = "synthetic-camera-secret";
        await using var fixture = await ConfigurationApiFixture.CreateAsync(enableCameraIngress: true,
            directoriesOverride: new FailingCameraRootDirectories());
        Guid sourceId = Guid.NewGuid();
        var protector = new SecretProtector(Microsoft.Extensions.Options.Options.Create(
            new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "root failure endpoint camera" });
            database.SourceCredentials.Add(new SourceCredentialEntity
            {
                SourceId = sourceId,
                Username = username,
                VirtualRoot = "sources/root-failure-endpoint",
                SecretVerifier = protector.CreateVerifier(password),
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var callbackHandler = fixture.Server.Server.CreateHandler(context => context.Connection.LocalPort = 8081);
        using var callbackClient = new HttpClient(callbackHandler) { BaseAddress = new Uri("http://localhost") };
        callbackClient.DefaultRequestHeaders.Add("X-API-KEY", CameraCallbackKey);
        using HttpResponseMessage response = await callbackClient.PostAsJsonAsync("/internal/camera/auth",
            new { username, password }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("CAMERA_ROOT_UNAVAILABLE", body, StringComparison.Ordinal);
        Assert.Contains("storage is unavailable", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(username, body, StringComparison.Ordinal);
        Assert.DoesNotContain(password, body, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic private path", body, StringComparison.Ordinal);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Empty(await verify.CameraLogins.AsNoTracking().ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private sealed class FailingCameraRootDirectories : ICameraIngressDirectories
    {
        public Task CreateDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
            throw new IOException("synthetic private path could not be provisioned");

        public Task<CameraFileInfo?> GetFileInfoAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<CameraFileInfo>> GetDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task SftpGo_auth_and_hook_accept_vendor_fields_and_omitted_optional_event_fields()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(enableCameraIngress: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid();
        var protector = new SecretProtector(Microsoft.Extensions.Options.Options.Create(
            new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic SFTP source" });
            database.SourceCredentials.Add(new SourceCredentialEntity
            {
                SourceId = sourceId,
                Username = "cam_protocol",
                VirtualRoot = "sources/synthetic-protocol",
                SecretVerifier = protector.CreateVerifier("synthetic-camera-secret"),
            });
            await database.SaveChangesAsync(token);
        }

        using var callbackHandler = fixture.Server.Server.CreateHandler(context => context.Connection.LocalPort = 8081);
        using var callbackClient = new HttpClient(callbackHandler) { BaseAddress = new Uri("http://localhost") };
        callbackClient.DefaultRequestHeaders.Add("X-API-KEY", CameraCallbackKey);

        using HttpResponseMessage auth = await callbackClient.PostAsJsonAsync("/internal/camera/auth", new
        {
            username = "cam_protocol", password = "synthetic-camera-secret", ip = "192.0.2.10", protocol = "FTP",
        }, token);
        Assert.Equal(HttpStatusCode.OK, auth.StatusCode);
        using JsonDocument authBody = JsonDocument.Parse(await auth.Content.ReadAsStringAsync(token));
        string endpoint = authBody.RootElement.GetProperty("filesystem").GetProperty("httpconfig")
            .GetProperty("endpoint").GetString()!;
        Assert.Contains("/internal/camera/fs/", endpoint, StringComparison.Ordinal);

        using HttpResponseMessage legacyAuth = await callbackClient.PostAsJsonAsync("/internal/camera/legacy-auth", new
        {
            username = "cam_protocol", password = "synthetic-camera-secret", protocol = "FTP", legacy = false,
        }, token);
        Assert.Equal(HttpStatusCode.Forbidden, legacyAuth.StatusCode);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            SourceEntity source = await database.Sources.SingleAsync(item => item.Id == sourceId, token);
            source.ConnectionProfile = SourceConnectionProfile.LegacyFtps;
            await database.SaveChangesAsync(token);
        }
        using HttpResponseMessage optedLegacyAuth = await callbackClient.PostAsJsonAsync("/internal/camera/legacy-auth", new
        {
            username = "cam_protocol", password = "synthetic-camera-secret", protocol = "FTP", legacy = false,
        }, token);
        Assert.Equal(HttpStatusCode.OK, optedLegacyAuth.StatusCode);
        using JsonDocument legacyAuthBody = JsonDocument.Parse(await optedLegacyAuth.Content.ReadAsStringAsync(token));
        string legacyEndpoint = legacyAuthBody.RootElement.GetProperty("filesystem").GetProperty("httpconfig")
            .GetProperty("endpoint").GetString()!;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            SourceConnectionProfile[] loginProfiles = await database.CameraLogins.AsNoTracking()
                .Select(login => login.ConnectionProfile).ToArrayAsync(token);
            Assert.Equal(new[] { SourceConnectionProfile.ModernFtps, SourceConnectionProfile.LegacyFtps },
                loginProfiles.Order().ToArray());
        }

        using HttpResponseMessage staleProfileHook = await callbackClient.PostAsJsonAsync("/internal/camera/hook", new
        {
            action = "pre-upload", username = "cam_protocol", path = "/DCIM/PHOTO.JPG",
            virtual_path = "/DCIM/PHOTO.JPG", endpoint, session_id = "FTP_stale-profile",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), status = 1, protocol = "FTP", fs_provider = 6,
        }, token);
        Assert.Equal(HttpStatusCode.Forbidden, staleProfileHook.StatusCode);

        using HttpResponseMessage hook = await callbackClient.PostAsJsonAsync("/internal/camera/hook", new
        {
            action = "pre-upload",
            username = "cam_protocol",
            path = "/DCIM/PHOTO.JPG",
            virtual_path = "/DCIM/PHOTO.JPG",
            endpoint = legacyEndpoint,
            session_id = "FTP_protocol-json-test",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            status = 1,
            protocol = "FTP",
            fs_provider = 6,
            ip = "192.0.2.10",
        }, token);
        Assert.Equal(HttpStatusCode.OK, hook.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal("/DCIM/PHOTO.JPG", await verify.CameraUploadAdmissions.AsNoTracking()
            .Select(item => item.VirtualPath).SingleAsync(token));
    }

    [Fact]
    public async Task Existing_login_cannot_start_an_admission_after_its_profile_is_withdrawn()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(enableCameraIngress: true,
            profileSettings: new ShutterSpool.Server.Hosting.CameraIngressSettings
            {
                Enabled = true,
                CallbackKey = CameraCallbackKey,
                CallbackBaseUrl = "http://localhost:8081/internal/camera",
                InternalPort = 8081,
                Profiles = new ShutterSpool.Server.Hosting.CameraIngressProfiles
                {
                    ModernFtps = new() { Enabled = false, ControlPort = 2121 },
                    LegacyFtps = new() { Enabled = true, ControlPort = 2138 },
                },
            });
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid();
        Guid loginId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "withdrawn profile source" });
            database.CameraLogins.Add(new CameraLoginEntity
            {
                Id = loginId, SourceId = sourceId, Username = "cam_withdrawn", VirtualRoot = "sources/withdrawn",
                CredentialGeneration = 1, ConnectionProfile = SourceConnectionProfile.ModernFtps,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync(token);
        }

        using var callbackHandler = fixture.Server.Server.CreateHandler(context => context.Connection.LocalPort = 8081);
        using var callbackClient = new HttpClient(callbackHandler) { BaseAddress = new Uri("http://localhost") };
        callbackClient.DefaultRequestHeaders.Add("X-API-KEY", CameraCallbackKey);
        string endpoint = $"http://localhost:8081/internal/camera/fs/{loginId:N}";
        using HttpResponseMessage hook = await callbackClient.PostAsJsonAsync("/internal/camera/hook", new
        {
            action = "pre-upload", username = "cam_withdrawn", path = "/DCIM/PHOTO.JPG",
            virtual_path = "/DCIM/PHOTO.JPG", endpoint, session_id = "FTP_withdrawn-profile",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), status = 1, protocol = "FTP", fs_provider = 6,
        }, token);

        Assert.Equal(HttpStatusCode.Forbidden, hook.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Empty(await verify.CameraUploadAdmissions.AsNoTracking().ToArrayAsync(token));
    }
}
