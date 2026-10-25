using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Routing;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Api.Destinations;
using ShutterSpool.Server.Api.Sources;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Unused_configuration_is_deleted_and_stale_removal_keeps_it()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);

        using HttpResponseMessage stale = await fixture.Client.DeleteAsync($"/management/sources/{source.Id}?expectedRevision=1", token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
            Assert.True(await database.Sources.AnyAsync(item => item.Id == source.Id, token));

        using HttpResponseMessage removed = await fixture.Client.DeleteAsync($"/management/sources/{source.Id}?expectedRevision=0", token);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Equal("deleted", (await removed.Content.ReadFromJsonAsync<JsonDocument>(token))!.RootElement.GetProperty("outcome").GetString());
        using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            Assert.False(await database.Sources.AnyAsync(item => item.Id == source.Id, token));
            Assert.False(await database.SourceCredentials.AnyAsync(item => item.SourceId == source.Id, token));
        }
    }

    [Fact]
    public async Task Source_with_capture_history_is_archived_without_deleting_assets_or_reenabling_ingress()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);
        Guid assetId = Guid.NewGuid(), captureId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Assets.Add(new AssetEntity { Id = assetId, SourceId = source.Id, OriginalName = "retained.raw",
                SpoolKey = Guid.NewGuid().ToString("N"), Size = 1, Sha256 = new string('0', 64),
                IngestedAt = DateTimeOffset.UtcNow, State = "Spooled" });
            database.Captures.Add(new CaptureEntity { Id = captureId, SourceId = source.Id, NormalizedStem = "retained",
                LastPrimaryIngestedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage archived = await fixture.Client.DeleteAsync($"/management/sources/{source.Id}?expectedRevision=0", token);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal("archived", (await archived.Content.ReadFromJsonAsync<JsonDocument>(token))!.RootElement.GetProperty("outcome").GetString());
        using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            SourceEntity current = await database.Sources.SingleAsync(item => item.Id == source.Id, token);
            SourceCredentialEntity credential = await database.SourceCredentials.SingleAsync(item => item.SourceId == source.Id, token);
            Assert.True(current.IsArchived);
            Assert.False(credential.IsIngressEnabled);
            Assert.Equal(2, credential.CredentialGeneration);
            Assert.True(await database.Assets.AnyAsync(item => item.Id == assetId, token));
            Assert.True(await database.Captures.AnyAsync(item => item.Id == captureId, token));
        }

        using HttpResponseMessage listed = await fixture.Client.GetAsync("/management/sources", token);
        Assert.DoesNotContain(source.Id.ToString(), await listed.Content.ReadAsStringAsync(token), StringComparison.OrdinalIgnoreCase);
        using HttpResponseMessage reenable = await fixture.Client.PutAsJsonAsync($"/management/sources/{source.Id}/ingress",
            new SetSourceIngressRequest(1, true), token);
        Assert.Equal(HttpStatusCode.Conflict, reenable.StatusCode);
    }

    [Fact]
    public async Task Source_removal_serializes_with_camera_login_creation()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);
        using var start = new Barrier(2);

        Task<CameraLogin?> loginTask = Task.Run(async () =>
        {
            start.SignalAndWait(token);
            await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
            return await new EfCameraIngressStore(database, TimeProvider.System)
                .CreateLoginAsync(source.Id, source.Username, 1, token, SourceConnectionProfile.ModernFtps);
        }, token);
        Task<HttpResponseMessage> removalTask = Task.Run(async () =>
        {
            start.SignalAndWait(token);
            return await fixture.Client.DeleteAsync($"/management/sources/{source.Id}?expectedRevision=0", token);
        }, token);

        CameraLogin? login = await loginTask;
        using HttpResponseMessage removed = await removalTask;
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        string outcome = (await removed.Content.ReadFromJsonAsync<JsonDocument>(token))!.RootElement.GetProperty("outcome").GetString()!;
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        if (login is null)
        {
            Assert.Equal("deleted", outcome);
            Assert.False(await verify.Sources.AnyAsync(item => item.Id == source.Id, token));
            Assert.False(await verify.CameraLogins.AnyAsync(item => item.SourceId == source.Id, token));
        }
        else
        {
            Assert.Equal("archived", outcome);
            Assert.True(await verify.Sources.AnyAsync(item => item.Id == source.Id && item.IsArchived, token));
            Assert.True(await verify.CameraLogins.AnyAsync(item => item.Id == login.Id, token));
            Assert.False(await verify.SourceCredentials.Where(item => item.SourceId == source.Id)
                .Select(item => item.IsIngressEnabled).SingleAsync(token));
        }
    }

    [Fact]
    public async Task Destination_without_media_history_is_deleted_with_its_configuration()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations",
            new { name = "unused local", kind = "local", secret = "synthetic-secret" }, token);
        DestinationResponse destination = (await created.Content.ReadFromJsonAsync<DestinationResponse>(token))!;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.LocalDestinations.Add(new LocalDestinationEntity { DestinationId = destination.Id, RootPath = "C:\\synthetic" });
            database.DestinationRoutings.Add(new DestinationRoutingEntity { DestinationId = destination.Id, RulesJson = "[]" });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage deleted = await fixture.Client.DeleteAsync($"/management/destinations/{destination.Id}?expectedRevision=0", token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal("deleted", (await deleted.Content.ReadFromJsonAsync<JsonDocument>(token))!.RootElement.GetProperty("outcome").GetString());
        using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            Assert.False(await database.Destinations.AnyAsync(item => item.Id == destination.Id, token));
            Assert.False(await database.DestinationCredentials.AnyAsync(item => item.DestinationId == destination.Id, token));
            Assert.False(await database.LocalDestinations.AnyAsync(item => item.DestinationId == destination.Id, token));
            Assert.False(await database.DestinationRoutings.AnyAsync(item => item.DestinationId == destination.Id, token));
        }
    }

    [Fact]
    public async Task Destination_with_delivery_history_is_archived_and_pending_work_is_paused()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations",
            new { name = "used local", kind = "local" }, token);
        DestinationResponse destination = (await created.Content.ReadFromJsonAsync<DestinationResponse>(token))!;
        Guid assetId = Guid.NewGuid(), captureId = Guid.NewGuid(), decisionId = Guid.NewGuid(), runningDecisionId = Guid.NewGuid();
        Guid deliveryId = Guid.NewGuid(), runningDeliveryId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Assets.Add(new AssetEntity { Id = assetId, SourceId = source.Id, OriginalName = "retained.raw",
                SpoolKey = Guid.NewGuid().ToString("N"), Size = 1, Sha256 = new string('0', 64),
                IngestedAt = DateTimeOffset.UtcNow, State = "Spooled" });
            database.Captures.Add(new CaptureEntity { Id = captureId, SourceId = source.Id, NormalizedStem = "retained",
                LastPrimaryIngestedAt = DateTimeOffset.UtcNow });
            database.RoutingDecisions.Add(new RoutingDecisionEntity { Id = decisionId, CaptureId = captureId, AssetId = assetId,
                DestinationId = destination.Id, Generation = 1, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
                Explanation = "test history", CreatedAt = DateTimeOffset.UtcNow });
            database.RoutingDecisions.Add(new RoutingDecisionEntity { Id = runningDecisionId, CaptureId = captureId, AssetId = assetId,
                DestinationId = destination.Id, Generation = 2, IsCurrent = false, Kind = RoutingDecisionKindValue.Matched,
                Explanation = "in-flight test history", CreatedAt = DateTimeOffset.UtcNow });
            database.Deliveries.Add(new DeliveryEntity { Id = deliveryId, DecisionId = decisionId, AssetId = assetId,
                DestinationId = destination.Id, TargetPath = "retained.raw", State = DeliveryState.Pending, CreatedAt = DateTimeOffset.UtcNow });
            database.Deliveries.Add(new DeliveryEntity { Id = runningDeliveryId, DecisionId = runningDecisionId, AssetId = assetId,
                DestinationId = destination.Id, TargetPath = "in-flight.raw", State = DeliveryState.Running,
                LeaseId = Guid.NewGuid(), LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(1), CreatedAt = DateTimeOffset.UtcNow });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage archived = await fixture.Client.DeleteAsync($"/management/destinations/{destination.Id}?expectedRevision=0", token);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal("archived", (await archived.Content.ReadFromJsonAsync<JsonDocument>(token))!.RootElement.GetProperty("outcome").GetString());
        using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            DestinationEntity current = await database.Destinations.SingleAsync(item => item.Id == destination.Id, token);
            Assert.True(current.IsArchived);
            Assert.False(current.Enabled);
            Assert.True(await database.Assets.AnyAsync(item => item.Id == assetId, token));
            Assert.True(await database.RoutingDecisions.AnyAsync(item => item.Id == decisionId, token));
            Assert.Equal(DeliveryState.Disabled, (await database.Deliveries.SingleAsync(item => item.Id == deliveryId, token)).State);
            Assert.Equal(DeliveryState.Running, (await database.Deliveries.SingleAsync(item => item.Id == runningDeliveryId, token)).State);
        }
    }

    [Fact]
    public async Task Frozen_routing_snapshot_preserves_destination_and_new_releases_skip_it()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        CreatedSourceResponse source = await CreateSourceAsync(fixture.Client, token);
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations",
            new { name = "frozen local", kind = "local" }, token);
        DestinationResponse destination = (await created.Content.ReadFromJsonAsync<DestinationResponse>(token))!;
        Guid firstAssetId = Guid.NewGuid(), firstCaptureId = Guid.NewGuid();
        Guid secondAssetId = Guid.NewGuid(), secondCaptureId = Guid.NewGuid();
        RoutingRule[] rules = [new("all", "All", 0, true, [], "", "{suppliedName}")];
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.DestinationRoutings.Add(new DestinationRoutingEntity
            {
                DestinationId = destination.Id,
                RulesJson = RoutingRuleJson.WriteRules(rules),
            });
            await AddReleasedAssetCaptureAsync(database, source.Id, firstAssetId, firstCaptureId, "frozen-one", token);
            Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System))
                .ReleaseNowAsync(firstCaptureId, 0, token)).Succeeded);
            Assert.Contains(RoutingRuleJson.ReadSnapshot((await database.Captures.AsNoTracking()
                .SingleAsync(item => item.Id == firstCaptureId, token)).RoutingSnapshotJson!)!, item => item.DestinationId == destination.Id);
            Assert.Equal(0, await database.RoutingDecisions.CountAsync(item => item.CaptureId == firstCaptureId, token));
            Assert.Equal(0, await database.Deliveries.CountAsync(item => item.DestinationId == destination.Id, token));
        }

        using HttpResponseMessage archived = await fixture.Client.DeleteAsync($"/management/destinations/{destination.Id}?expectedRevision=0", token);
        Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        Assert.Equal("archived", (await archived.Content.ReadFromJsonAsync<JsonDocument>(token))!.RootElement.GetProperty("outcome").GetString());

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            DestinationEntity retained = await database.Destinations.SingleAsync(item => item.Id == destination.Id, token);
            Assert.True(retained.IsArchived);
            Assert.False(retained.Enabled);
            Assert.True(await database.Assets.AnyAsync(item => item.Id == firstAssetId, token));
            Assert.True(await database.Captures.AnyAsync(item => item.Id == firstCaptureId, token));
            Assert.Equal(0, await database.RoutingDecisions.CountAsync(item => item.CaptureId == firstCaptureId, token));

            await AddReleasedAssetCaptureAsync(database, source.Id, secondAssetId, secondCaptureId, "frozen-two", token);
            Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System))
                .ReleaseNowAsync(secondCaptureId, 0, token)).Succeeded);
            string oldSnapshot = (await database.Captures.AsNoTracking().SingleAsync(item => item.Id == firstCaptureId, token)).RoutingSnapshotJson!;
            string newSnapshot = (await database.Captures.AsNoTracking().SingleAsync(item => item.Id == secondCaptureId, token)).RoutingSnapshotJson!;
            Assert.Contains(RoutingRuleJson.ReadSnapshot(oldSnapshot), item => item.DestinationId == destination.Id);
            Assert.DoesNotContain(RoutingRuleJson.ReadSnapshot(newSnapshot), item => item.DestinationId == destination.Id);

            Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
            Assert.Equal(DeliveryState.Disabled, (await database.Deliveries.SingleAsync(item => item.AssetId == firstAssetId, token)).State);
            Assert.Equal(oldSnapshot, (await database.Captures.AsNoTracking().SingleAsync(item => item.Id == firstCaptureId, token)).RoutingSnapshotJson);
        }
    }

    private static async Task AddReleasedAssetCaptureAsync(ShutterSpoolDbContext database, Guid sourceId,
        Guid assetId, Guid captureId, string stem, CancellationToken token)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        database.Assets.Add(new AssetEntity
        {
            Id = assetId, SourceId = sourceId, OriginalName = $"{stem}.raw", SpoolKey = Guid.NewGuid().ToString("N"),
            Size = 1, Sha256 = new string('0', 64), IngestedAt = now, State = "Spooled",
        });
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = stem, LastPrimaryIngestedAt = now,
        });
        database.CaptureAssets.Add(new CaptureAssetEntity
        {
            AssetId = assetId, CaptureId = captureId, Role = "RAW", IsPrimary = true, GroupingReason = "test",
        });
        await database.SaveChangesAsync(token);
    }
}
