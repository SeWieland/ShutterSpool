using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShutterSpool.Application.Routing;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Collections;
using ShutterSpool.Domain.Routing;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Destinations.WebDav;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;

namespace ShutterSpool.IntegrationTests;

public sealed class ConfigurationRevisionPostgresTests
{

    [Fact]
    public async Task Concurrent_source_and_routing_writes_with_one_revision_have_one_winner()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        Guid sourceId = Guid.NewGuid();
        Guid destinationId = Guid.NewGuid();
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            seed.Sources.Add(new SourceEntity { Id = sourceId, Name = "camera", CurationMode = SourceCurationMode.Delay,
                CurationDelaySeconds = 3_600 });
            seed.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "camera-user",
                VirtualRoot = "private/source-root", SecretVerifier = new byte[32] });
            seed.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "destination", Kind = "local" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        Task<SourceConfigurationWrite>[] sourceWrites = [.. Enumerable.Range(0, 2).Select(async index =>
        {
            await using var context = new ShutterSpoolDbContext(options);
            return await new EfSourceConfigurationStore(context).UpdateConfigurationAsync(sourceId, 0,
                $"camera-{index}", SourceCurationMode.Immediate, 3_600, SourceType.CameraFtpUpload,
                SourceConnectionProfile.ModernFtps, CollectionName.BuiltInId, TestContext.Current.CancellationToken);
        })];
        SourceConfigurationWrite[] sourceResults = await Task.WhenAll(sourceWrites);
        Assert.Single(sourceResults, result => result.Applied);
        Assert.Single(sourceResults, result => !result.Applied && result.Current?.ConfigurationRevision == 1);

        Task<DestinationRoutingMutation>[] routingWrites = [.. Enumerable.Range(0, 2).Select(async index =>
        {
            await using var context = new ShutterSpoolDbContext(options);
            return await new DestinationRoutingAdminService(context).SetAsync(
                new DestinationRouting(destinationId, index == 0, []), 0, TestContext.Current.CancellationToken);
        })];
        DestinationRoutingMutation[] routingResults = await Task.WhenAll(routingWrites);
        Assert.Single(routingResults, result => result.Applied);
        Assert.Single(routingResults, result => !result.Applied && result.Current?.ConfigurationRevision == 1);
    }


    [Fact]
    public async Task WebDav_connection_test_reports_the_revision_it_tested_when_settings_change_during_provider_io()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        DbContextOptions<ShutterSpoolDbContext> options = fixture.Options;
        Guid destinationId = Guid.NewGuid();
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions
        { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        await using (var seed = new ShutterSpoolDbContext(options))
        {
            await seed.Database.MigrateAsync(TestContext.Current.CancellationToken);
            seed.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "webdav", Kind = "webdav" });
            seed.WebDavDestinations.Add(new WebDavDestinationEntity { DestinationId = destinationId,
                CollectionUrl = "https://dav.example.test/camera/", Username = "operator" });
            seed.DestinationCredentials.Add(new DestinationCredentialEntity { DestinationId = destinationId,
                EncryptedSecret = protector.Protect("synthetic-app-password") });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        int mutated = 0;
        var handler = new WebDavDestinationAdapterTests.FakeDavHandler
        {
            Intercept = async _ =>
            {
                if (Interlocked.Exchange(ref mutated, 1) == 0)
                {
                    await using var update = new ShutterSpoolDbContext(options);
                    Assert.True((await new EfDestinationConfigurationStore(update).UpdateNameAsync(destinationId, 0,
                        "webdav-updated", TestContext.Current.CancellationToken)).Applied);
                }
                return null;
            },
        };
        await using var database = new ShutterSpoolDbContext(options);
        var configuration = new EfWebDavDestinationConfiguration(database, protector,
            new WebDavDestinationDeliveryAdapter(new WebDavDestinationAdapterTests.FakeHttpClientFactory(handler)));
        (bool writable, string? error, long testedRevision) = await configuration.TestWithRevisionAsync(destinationId,
            TestContext.Current.CancellationToken);

        Assert.True(writable);
        Assert.Null(error);
        Assert.Equal(0, testedRevision);
        Assert.Equal(1, await database.Destinations.Where(destination => destination.Id == destinationId)
            .Select(destination => destination.ConfigurationRevision).SingleAsync(TestContext.Current.CancellationToken));
    }
}
