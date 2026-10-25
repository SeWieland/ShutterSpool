using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Metadata;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Routing;
using ShutterSpool.Application.Sources;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed class CorePipelineTests
{
    [Fact]
    public async Task Composed_pipeline_survives_restart_and_disposable_service_outages()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var connectionContext = new ShutterSpoolDbContext(fixture.Options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:shutterspool"] = connectionContext.Database.GetConnectionString(),
            ["ShutterSpool:Spool:RootPath"] = Path.Combine(fixture.SpoolRoot, "originals"),
            ["ShutterSpool:Spool:ReservedBytes"] = "0",
            ["ShutterSpool:Security:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["ShutterSpool:Preview:ImaginaryUrl"] = "http://127.0.0.1:1/",
            ["ShutterSpool:Preview:CacheRootPath"] = Path.Combine(fixture.SpoolRoot, "previews"),
            ["ConnectionStrings:valkey"] = "127.0.0.1:1,connectTimeout=100,connectRetry=0,asyncTimeout=100",
        }).Build();
        // Synthetic 1x1 JPEG frame: enough metadata to exercise adapters, no private media.
        byte[] bytes = [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 0, 1, 0, 1, 3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xff, 0xd9];
        IngestResult accepted;
        string targetRoot = Path.Combine(fixture.SpoolRoot, "destination");
        await using (ServiceProvider first = Build(configuration))
        {
            await first.InitializeShutterSpoolAsync(configuration, token);
            Directory.CreateDirectory(targetRoot);
            await using AsyncServiceScope scope = first.CreateAsyncScope();
            var source = await scope.ServiceProvider.GetRequiredService<SourceManagementService>().CreateAsync("Synthetic", "Manual", null, token);
            var destination = await scope.ServiceProvider.GetRequiredService<DestinationManagementService>().CreateAsync("Local", "local", null, token);
            await scope.ServiceProvider.GetRequiredService<ILocalDestinationConfiguration>().ConfigureAsync(destination.Id, targetRoot, token);
            DestinationRoutingMutation routing = await scope.ServiceProvider.GetRequiredService<DestinationRoutingService>().SetAsync(new(destination.Id, true,
                [new("picked", "Picked images", 0, true, [new("selection", "equals", "Picked")], "delivered", "{suppliedName}")]), 1, token);
            Assert.True(routing.Applied);
            accepted = await scope.ServiceProvider.GetRequiredService<IngestService>().AcceptAsync(new(source.Id, "synthetic.jpg", new MemoryStream(bytes)), token);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Captures.ToArrayAsync(token));
        }

        await using ServiceProvider restarted = Build(configuration);
        await restarted.InitializeShutterSpoolAsync(configuration, token);
        Guid captureId;
        await using (AsyncServiceScope scope = restarted.CreateAsyncScope())
        {
            Assert.True(await scope.ServiceProvider.GetRequiredService<MetadataProcessingService>().ProcessOneAsync(token));
            Assert.True(await scope.ServiceProvider.GetRequiredService<CaptureGroupingService>().ProcessOneAsync(token));
            captureId = (await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Captures.SingleAsync(token)).Id;
            var curation = scope.ServiceProvider.GetRequiredService<CaptureCurationService>();
            var current = (await curation.GetAsync(captureId, token))!;
            Assert.True(current.Held);
            var edited = await curation.UpdateAsync(captureId, current.Revision, new(4, null, ["synthetic"]), token);
            Assert.True(edited.Succeeded);
            Assert.True((await curation.ReleaseNowAsync(captureId, edited.State!.Revision, token)).Succeeded);
            CaptureEntity frozen = await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Captures
                .AsNoTracking().SingleAsync(item => item.Id == captureId, token);
            Assert.Equal(4, frozen.ReleasedRating);
            Assert.Equal("Picked", frozen.ReleasedSelection?.ToString());
        }
        await using (AsyncServiceScope scope = restarted.CreateAsyncScope())
        {
            // The generator endpoint and Valkey are deliberately unavailable.
            Assert.Equal(PreviewState.Unavailable, (await scope.ServiceProvider.GetRequiredService<PreviewService>()
                .GetAsync(captureId, PreviewProfile.Grid, token)).State);
            Assert.True(await scope.ServiceProvider.GetRequiredService<CaptureRoutingService>().ProcessOneAsync(token));
            ShutterSpoolDbContext workflow = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            string[] routedKinds = await workflow.RoutingDecisions.AsNoTracking()
                .Select(decision => $"{decision.Kind}/{decision.RuleId}/{decision.Explanation}").ToArrayAsync(token);
            int pendingDeliveries = await workflow.Deliveries.CountAsync(delivery => delivery.State == ShutterSpool.Domain.Delivery.DeliveryState.Pending, token);
            Assert.True(pendingDeliveries > 0, $"Routing did not create pending work (decisions: {string.Join(", ", routedKinds)}).");
            await restarted.GetRequiredService<IDeliveryWorkSignal>().NotifyAsync(token);
            Assert.True(await scope.ServiceProvider.GetRequiredService<DeliveryProcessingService>().ProcessOneAsync(token));
            var history = (await scope.ServiceProvider.GetRequiredService<CaptureRoutingHistoryService>().GetDeliveriesAsync(captureId, new(), token))!;
            Assert.Equal("COMPLETE", (await scope.ServiceProvider.GetRequiredService<CaptureCurationService>().GetAsync(captureId, token))!.State);
            Assert.Equal("Delivered", Assert.Single(history.Items).State);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(targetRoot, "delivered", "synthetic.jpg"), token));
        await using Stream original = await restarted.GetRequiredService<ICommittedOriginalReader>().OpenReadAsync(accepted.Asset.SpoolKey, token);
        using var copy = new MemoryStream();
        await original.CopyToAsync(copy, token);
        Assert.Equal(bytes, copy.ToArray());
    }

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddShutterSpoolInfrastructure(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
