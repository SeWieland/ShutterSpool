using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;

namespace ShutterSpool.IntegrationTests;

public sealed class SourceCredentialVerifierTests
{
    [Fact]
    public async Task Rejects_legacy_FTPS_for_a_verified_secret_without_source_opt_in()
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var database = new ShutterSpoolDbContext(options);
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        var sourceId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "Camera" });
        database.SourceCredentials.Add(new SourceCredentialEntity
        {
            SourceId = sourceId, Username = "cam_legacy", VirtualRoot = "sources/camera",
            SecretVerifier = protector.CreateVerifier("correct-secret"),
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var verifier = new SourceCredentialVerifier(new EfSourceConfigurationStore(database), protector,
            new SourceAccessAudit(LoggerFactory.Create(_ => { }).CreateLogger<SourceAccessAudit>()));

        SourceCredentialCheck check = await verifier.VerifyAttemptAsync("cam_legacy", "correct-secret",
            TestContext.Current.CancellationToken, SourceConnectionProfile.LegacyFtps);

        Assert.Null(check.Access);
        Assert.Equal(sourceId, check.VerifiedButRejectedSourceId);
    }

    [Fact]
    public async Task Grants_only_the_configured_virtual_root_for_a_matching_source_secret()
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var database = new ShutterSpoolDbContext(options);
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        var sourceId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "Camera" });
        database.SourceCredentials.Add(new SourceCredentialEntity
        {
            SourceId = sourceId,
            Username = "cam_test",
            VirtualRoot = "sources/camera",
            SecretVerifier = protector.CreateVerifier("correct-secret"),
            CredentialGeneration = 4,
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var verifier = new SourceCredentialVerifier(new EfSourceConfigurationStore(database), protector, new SourceAccessAudit(loggerFactory.CreateLogger<SourceAccessAudit>()));

        SourceAccess? access = await verifier.VerifyAsync("cam_test", "correct-secret", TestContext.Current.CancellationToken);

        Assert.NotNull(access);
        Assert.Equal("sources/camera", access.VirtualRoot);
        Assert.Equal(4, access.CredentialGeneration);
        Assert.DoesNotContain(loggerProvider.Entries, eventId => eventId.Id == 2004 && eventId.Name == "SourceAuthenticationSucceeded");
        verifier.RecordSucceeded(sourceId);
        Assert.Contains(loggerProvider.Entries, eventId => eventId.Id == 2004 && eventId.Name == "SourceAuthenticationSucceeded");
        Assert.Null(await verifier.VerifyAsync("cam_test", "wrong-secret", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Suspended_source_credentials_cannot_authenticate()
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var database = new ShutterSpoolDbContext(options);
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        var sourceId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "Suspended camera" });
        database.SourceCredentials.Add(new SourceCredentialEntity
        {
            SourceId = sourceId,
            Username = "cam_suspended",
            VirtualRoot = "sources/camera",
            SecretVerifier = protector.CreateVerifier("correct-secret"),
            IsIngressEnabled = false,
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);

        var verifier = new SourceCredentialVerifier(new EfSourceConfigurationStore(database), protector,
            new SourceAccessAudit(LoggerFactory.Create(_ => { }).CreateLogger<SourceAccessAudit>()));

        SourceCredentialCheck rejected = await verifier.VerifyAttemptAsync("cam_suspended", "correct-secret", TestContext.Current.CancellationToken);
        Assert.Null(rejected.Access);
        Assert.Equal(sourceId, rejected.VerifiedButRejectedSourceId);
        SourceCredentialCheck unproved = await verifier.VerifyAttemptAsync("cam_suspended", "wrong-secret", TestContext.Current.CancellationToken);
        Assert.Null(unproved.Access);
        Assert.Null(unproved.VerifiedButRejectedSourceId);
    }

    [Fact]
    public async Task Authentication_snapshot_orders_observations_by_version_and_deletes_with_its_source()
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var database = new ShutterSpoolDbContext(options);
        Guid sourceId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "Camera" });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfSourceAuthenticationObservationStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.Equal(1, await store.RecordAsync(new(sourceId, SourceAuthenticationOutcome.Succeeded, now), TestContext.Current.CancellationToken));
        Assert.Null(await store.RecordAsync(new(sourceId, SourceAuthenticationOutcome.Rejected, now.AddSeconds(-1)), TestContext.Current.CancellationToken));
        SourceAuthenticationSnapshot saved = Assert.Single(await store.ReadAsync([sourceId], TestContext.Current.CancellationToken));
        Assert.Equal(SourceAuthenticationOutcome.Succeeded, saved.Outcome);
        Assert.Equal(1, saved.Version);
        Assert.Equal(now, saved.ObservedAt);

        database.Sources.Remove(await database.Sources.SingleAsync(TestContext.Current.CancellationToken));
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(await store.ReadAsync([sourceId], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Suspend_reenable_and_regeneration_each_revoke_the_previous_generation()
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var database = new ShutterSpoolDbContext(options);
        var sourceId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "Camera" });
        database.SourceCredentials.Add(new SourceCredentialEntity
        {
            SourceId = sourceId, Username = "cam_test", VirtualRoot = "sources/camera", SecretVerifier = new byte[32],
        });
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new EfSourceConfigurationStore(database);

        SourceIngressState suspended = (await store.SetIngressEnabledAsync(sourceId, false, TestContext.Current.CancellationToken))!;
        SourceIngressState unchanged = (await store.SetIngressEnabledAsync(sourceId, false, TestContext.Current.CancellationToken))!;
        SourceIngressState reenabled = (await store.SetIngressEnabledAsync(sourceId, true, TestContext.Current.CancellationToken))!;
        Assert.True(await store.RegenerateAsync(sourceId, new byte[32], TestContext.Current.CancellationToken));
        SourceCredential credential = (await store.FindCredentialAsync("cam_test", TestContext.Current.CancellationToken))!;

        Assert.Equal(2, suspended.CredentialGeneration);
        Assert.Equal(2, unchanged.CredentialGeneration);
        Assert.Equal(3, reenabled.CredentialGeneration);
        Assert.Equal(4, credential.CredentialGeneration);
        Assert.True(credential.IsIngressEnabled);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<EventId> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(List<EventId> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            entries.Add(eventId);
        }
    }
}
