using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed partial class CameraIngressIntegrationTests
{
    [Fact]
    public async Task Successful_login_initializes_a_usable_virtual_root()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("root-login", "camera-secret");

        CameraLogin login = (await fixture.Ingress.AuthenticateAsync("cam_root-login", "camera-secret", Token))!;

        Assert.True(Directory.Exists(SourceRoot(fixture, sourceId)));
        CameraFileInfo root = (await fixture.Ingress.GetFileInfoAsync(login.Id, "/", Token))!;
        Assert.Equal("/", root.Name);
        Assert.True(root.IsDirectory);
        await fixture.Ingress.CreateDirectoryAsync(login.Id, "/DCIM", Token);
        Assert.Equal("DCIM", Assert.Single(await fixture.Ingress.GetDirectoryAsync(login.Id, "/", Token)).Name);
    }

    [Fact]
    public async Task Repeated_concurrent_and_restarted_logins_preserve_directories_and_accepted_media()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("root-reconnect", "camera-secret");
        CameraLogin firstLogin = (await fixture.Ingress.AuthenticateAsync("cam_root-reconnect", "camera-secret", Token))!;
        await fixture.Ingress.CreateDirectoryAsync(firstLogin.Id, "/DCIM/100MEDIA", Token);
        byte[] bytes = [3, 1, 4, 1, 5, 9];
        IngestResult accepted = await CommitSyntheticUploadAsync(fixture, firstLogin, "/DCIM/100MEDIA/DSC0001.JPG", bytes);

        await using var restartedDatabase = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        CameraIngressService restarted = fixture.CreateIngress(restartedDatabase);
        CameraLogin restartedLogin = (await restarted.AuthenticateAsync("cam_root-reconnect", "camera-secret", Token))!;
        ShutterSpoolDbContext[] concurrentDatabases = Enumerable.Range(0, 4)
            .Select(_ => new ShutterSpoolDbContext(fixture.DatabaseOptions)).ToArray();
        try
        {
            CameraLogin?[] concurrentLogins = await Task.WhenAll(concurrentDatabases.Select(database =>
                fixture.CreateIngress(database).AuthenticateAsync("cam_root-reconnect", "camera-secret", Token)));

            Assert.All(concurrentLogins, login => Assert.NotNull(login));
        }
        finally
        {
            foreach (ShutterSpoolDbContext database in concurrentDatabases) await database.DisposeAsync();
        }

        Assert.NotEqual(firstLogin.Id, restartedLogin.Id);
        Assert.True(Directory.Exists(SourceRoot(fixture, sourceId)));
        Assert.Equal("100MEDIA", Assert.Single(await restarted.GetDirectoryAsync(restartedLogin.Id, "/DCIM", Token)).Name);
        CameraFileInfo retained = Assert.Single(await restarted.GetDirectoryAsync(restartedLogin.Id, "/DCIM/100MEDIA", Token));
        Assert.Equal("DSC0001.JPG", retained.Name);
        Assert.Equal(bytes.Length, retained.Size);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.FinalPath(accepted), Token));
        Assert.Equal(sourceId, (await fixture.Database.Assets.AsNoTracking().SingleAsync(Token)).SourceId);
    }

    [Fact]
    public async Task Login_recovers_only_a_missing_empty_root_and_keeps_original_bytes()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("root-recovery", "camera-secret");
        CameraLogin firstLogin = (await fixture.Ingress.AuthenticateAsync("cam_root-recovery", "camera-secret", Token))!;
        byte[] bytes = [2, 7, 1, 8, 2, 8];
        IngestResult accepted = await CommitSyntheticUploadAsync(fixture, firstLogin, "/DSC0002.JPG", bytes);
        string sourceDirectory = Path.Combine(fixture.DirectoriesRoot, sourceId.ToString("N"));
        Directory.Delete(sourceDirectory, recursive: true);

        await using var restartedDatabase = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        CameraIngressService restarted = fixture.CreateIngress(restartedDatabase);
        CameraLogin recoveredLogin = (await restarted.AuthenticateAsync("cam_root-recovery", "camera-secret", Token))!;

        Assert.NotEqual(firstLogin.Id, recoveredLogin.Id);
        Assert.True(Directory.Exists(SourceRoot(fixture, sourceId)));
        Assert.Empty(await fixture.Directories.GetDirectoryAsync(sourceId, "/", Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.FinalPath(accepted), Token));
        Assert.Single(await fixture.Database.Assets.AsNoTracking().ToListAsync(Token));
    }

    [Fact]
    public async Task Invalid_credentials_do_not_create_camera_directory_state()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("root-invalid", "camera-secret");

        Assert.Null(await fixture.Ingress.AuthenticateAsync("cam_root-invalid", "wrong-secret", Token));

        Assert.False(Directory.Exists(fixture.DirectoriesRoot));
        Assert.Empty(await fixture.Database.CameraLogins.AsNoTracking().ToListAsync(Token));
        Assert.DoesNotContain(fixture.AuthenticationRecorder.Observations,
            observation => observation.Outcome == SourceAuthenticationOutcome.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(fixture.DirectoriesRoot, sourceId.ToString("N"))));
    }

    [Fact]
    public async Task File_obstruction_fails_login_without_replacing_the_obstruction_or_publishing_success()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("root-file-obstruction", "camera-secret");
        string sourceDirectory = Path.Combine(fixture.DirectoriesRoot, sourceId.ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        string obstruction = Path.Combine(sourceDirectory, "files");
        byte[] contents = [6, 2, 6, 4];
        await File.WriteAllBytesAsync(obstruction, contents, Token);

        CameraSourceRootInitializationException failure = await Assert.ThrowsAsync<CameraSourceRootInitializationException>(() =>
            fixture.Ingress.AuthenticateAsync("cam_root-file-obstruction", "camera-secret", Token));

        Assert.Equal(sourceId, failure.SourceId);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.DirectoriesRoot, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(contents, await File.ReadAllBytesAsync(obstruction, Token));
        Assert.Empty(await fixture.Database.CameraLogins.AsNoTracking().ToListAsync(Token));
        Assert.DoesNotContain(fixture.AuthenticationRecorder.Observations,
            observation => observation.Outcome == SourceAuthenticationOutcome.Succeeded);
        Assert.Contains(fixture.AuthenticationRecorder.Observations,
            observation => observation.SourceId == sourceId && observation.Outcome == SourceAuthenticationOutcome.Rejected);
    }

    [Fact]
    public async Task Symlink_obstruction_fails_login_without_following_or_replacing_the_link()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("root-link-obstruction", "camera-secret");
        string sourceDirectory = Path.Combine(fixture.DirectoriesRoot, sourceId.ToString("N"));
        Directory.CreateDirectory(sourceDirectory);
        string obstruction = Path.Combine(sourceDirectory, "files");
        string target = Path.Combine(Path.GetTempPath(), $"shutterspool-root-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(target);
        try
        {
            try
            {
                Directory.CreateSymbolicLink(obstruction, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                throw SkipException.ForSkip($"The test host cannot create a directory symlink: {exception.GetType().Name}.");
            }

            CameraSourceRootInitializationException failure = await Assert.ThrowsAsync<CameraSourceRootInitializationException>(() =>
                fixture.Ingress.AuthenticateAsync("cam_root-link-obstruction", "camera-secret", Token));

            Assert.Equal(sourceId, failure.SourceId);
            Assert.Null(failure.InnerException);
            Assert.DoesNotContain(fixture.DirectoriesRoot, failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(target, new DirectoryInfo(obstruction).LinkTarget);
            Assert.Empty(Directory.EnumerateFileSystemEntries(target));
            Assert.Empty(await fixture.Database.CameraLogins.AsNoTracking().ToListAsync(Token));
            Assert.DoesNotContain(fixture.AuthenticationRecorder.Observations,
                observation => observation.Outcome == SourceAuthenticationOutcome.Succeeded);
            Assert.Contains(fixture.AuthenticationRecorder.Observations,
                observation => observation.SourceId == sourceId && observation.Outcome == SourceAuthenticationOutcome.Rejected);
        }
        finally
        {
            if (Directory.Exists(obstruction)) Directory.Delete(obstruction);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task Authority_revoked_during_root_initialization_does_not_publish_login_success()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid sourceId = await fixture.CreateSourceAsync("root-authority-race", "camera-secret");
        await using var ingressDatabase = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        await using var authorityDatabase = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        var directories = new RevokeIngressAfterRootInitialization(fixture.Directories, async () =>
        {
            if (await new EfSourceConfigurationStore(authorityDatabase).SetIngressEnabledAsync(sourceId, false, Token) is null)
                throw new InvalidOperationException("The test could not revoke ingress authority.");
        });
        CameraIngressService ingress = fixture.CreateIngress(ingressDatabase, directoriesOverride: directories);

        Assert.Null(await ingress.AuthenticateAsync("cam_root-authority-race", "camera-secret", Token));

        Assert.True(Directory.Exists(SourceRoot(fixture, sourceId)));
        Assert.Empty(await ingressDatabase.CameraLogins.AsNoTracking().ToListAsync(Token));
        Assert.DoesNotContain(fixture.AuthenticationRecorder.Observations,
            observation => observation.Outcome == SourceAuthenticationOutcome.Succeeded);
    }

    [Fact]
    public async Task Login_rechecks_the_verified_source_when_its_username_is_reassigned_during_root_initialization()
    {
        await using var fixture = await CameraIngressFixture.CreateAsync();
        Guid firstSourceId = await fixture.CreateSourceAsync("auth-owner-race", "owner-a-secret");
        const string username = "cam_auth-owner-race";
        await using var ingressDatabase = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        await using var authorityDatabase = new ShutterSpoolDbContext(fixture.DatabaseOptions);
        var directories = new BlockingDirectories(fixture.Directories);
        CameraIngressService ingress = fixture.CreateIngress(ingressDatabase, directoriesOverride: directories);

        Task<CameraLogin?> firstLogin = ingress.AuthenticateAsync(username, "owner-a-secret", Token);
        await directories.Entered.Task.WaitAsync(Token);
        try
        {
            SourceConfigurationWrite replacement = await new EfSourceConfigurationStore(authorityDatabase).ReplaceCredentialAsync(
                firstSourceId, 0, "cam_auth-owner-race-replaced",
                new SecretProtector(fixture.ProtectionOptions).CreateVerifier("replacement-secret"), Token);
            Assert.True(replacement.Applied);
            Guid secondSourceId = Guid.NewGuid();
            fixture.Database.Sources.Add(new SourceEntity { Id = secondSourceId, Name = "Camera auth-owner-race-b" });
            fixture.Database.SourceCredentials.Add(new SourceCredentialEntity
            {
                SourceId = secondSourceId,
                Username = username,
                VirtualRoot = "sources/auth-owner-race-b",
                SecretVerifier = new SecretProtector(fixture.ProtectionOptions).CreateVerifier("owner-b-secret"),
            });
            await fixture.Database.SaveChangesAsync(Token);

            directories.Release.TrySetResult();
            Assert.Null(await firstLogin);
            Assert.False(Directory.Exists(SourceRoot(fixture, secondSourceId)));
            Assert.Empty(await ingressDatabase.CameraLogins.AsNoTracking().ToListAsync(Token));
            Assert.DoesNotContain(fixture.AuthenticationRecorder.Observations,
                observation => observation.Outcome == SourceAuthenticationOutcome.Succeeded);

            CameraLogin validSecondLogin = (await ingress.AuthenticateAsync(username, "owner-b-secret", Token))!;

            Assert.Equal(secondSourceId, validSecondLogin.SourceId);
            Assert.True(Directory.Exists(SourceRoot(fixture, secondSourceId)));
            Assert.Single(fixture.AuthenticationRecorder.Observations,
                observation => observation.SourceId == secondSourceId && observation.Outcome == SourceAuthenticationOutcome.Succeeded);
        }
        finally
        {
            directories.Release.TrySetResult();
        }
    }

    private static async Task<IngestResult> CommitSyntheticUploadAsync(CameraIngressFixture fixture, CameraLogin login,
        string path, byte[] bytes)
    {
        const string session = "FTP_root-lifecycle";
        long timestamp = CurrentTimestamp();
        await fixture.Ingress.AdmitAsync(login.Id, login.Username, session, path, timestamp, 1_024, 2, 600, Token);
        await fixture.Ingress.ReceiveAsync(login.Id, path, new MemoryStream(bytes), 1_024, Token);
        return await fixture.Ingress.CompleteAsync(login.Id, login.Username, session, path, timestamp + 1_000, bytes.Length, Token);
    }

    private static string SourceRoot(CameraIngressFixture fixture, Guid sourceId) =>
        Path.Combine(fixture.DirectoriesRoot, sourceId.ToString("N"), "files");

    private sealed class RevokeIngressAfterRootInitialization(ICameraIngressDirectories inner, Func<Task> revoke) : ICameraIngressDirectories
    {
        public async Task CreateDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken)
        {
            await inner.CreateDirectoryAsync(sourceId, virtualPath, cancellationToken);
            await revoke();
        }

        public Task<CameraFileInfo?> GetFileInfoAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
            inner.GetFileInfoAsync(sourceId, virtualPath, cancellationToken);

        public Task<IReadOnlyList<CameraFileInfo>> GetDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
            inner.GetDirectoryAsync(sourceId, virtualPath, cancellationToken);
    }

    private sealed class BlockingDirectories(ICameraIngressDirectories inner) : ICameraIngressDirectories
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task CreateDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await inner.CreateDirectoryAsync(sourceId, virtualPath, cancellationToken);
        }

        public Task<CameraFileInfo?> GetFileInfoAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
            inner.GetFileInfoAsync(sourceId, virtualPath, cancellationToken);

        public Task<IReadOnlyList<CameraFileInfo>> GetDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
            inner.GetDirectoryAsync(sourceId, virtualPath, cancellationToken);
    }
}
