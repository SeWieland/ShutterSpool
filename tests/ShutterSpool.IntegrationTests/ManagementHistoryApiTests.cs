using Microsoft.EntityFrameworkCore;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Routing_history_exposes_retained_destination_names_and_null_for_missing_config()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        Guid assetId = Guid.NewGuid(), routeOnlyDestinationId = Guid.NewGuid(), workDestinationId = Guid.NewGuid();
        Guid missingDestinationId = Guid.NewGuid(), routeOnlyDecisionId = Guid.NewGuid();
        Guid workDecisionId = Guid.NewGuid(), missingDecisionId = Guid.NewGuid();
        Guid workDeliveryId = Guid.NewGuid(), missingDeliveryId = Guid.NewGuid();
        DateTimeOffset time = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            database.Assets.Add(new AssetEntity
            {
                Id = assetId, SourceId = capture.SourceId, OriginalName = "synthetic.jpg", SpoolKey = "synthetic/routing-name",
                Size = 4, Sha256 = new string('0', 64), State = "Spooled", MetadataRole = "JPEG",
                RecoveryCorrelation = Guid.NewGuid(), IngestedAt = time,
            });
            database.CaptureAssets.Add(new CaptureAssetEntity
            { AssetId = assetId, CaptureId = captureId, Role = "JPEG", IsPrimary = true, GroupingReason = "synthetic" });
            database.Destinations.AddRange(
                new DestinationEntity { Id = routeOnlyDestinationId, Name = "Archived route-only", IsArchived = true },
                new DestinationEntity { Id = workDestinationId, Name = "Archived work target", IsArchived = true });
            database.RoutingDecisions.AddRange(
                new RoutingDecisionEntity
                {
                    Id = routeOnlyDecisionId, CaptureId = captureId, AssetId = assetId, DestinationId = routeOnlyDestinationId,
                    Generation = 1, IsCurrent = true, Kind = RoutingDecisionKindValue.InvalidTarget,
                    Explanation = "Synthetic blocked route", CreatedAt = time,
                },
                new RoutingDecisionEntity
                {
                    Id = workDecisionId, CaptureId = captureId, AssetId = assetId, DestinationId = workDestinationId,
                    Generation = 1, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched, Required = true,
                    TargetPath = "synthetic/work.jpg", Explanation = "Synthetic work", CreatedAt = time,
                },
                new RoutingDecisionEntity
                {
                    Id = missingDecisionId, CaptureId = captureId, AssetId = assetId, DestinationId = missingDestinationId,
                    Generation = 1, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched, Required = true,
                    TargetPath = "synthetic/missing.jpg", Explanation = "Synthetic missing config", CreatedAt = time,
                });
            database.Deliveries.AddRange(
                new DeliveryEntity
                {
                    Id = workDeliveryId, DecisionId = workDecisionId, AssetId = assetId, DestinationId = workDestinationId,
                    TargetPath = "synthetic/work.jpg", Required = true, State = DeliveryState.Pending, CreatedAt = time,
                },
                new DeliveryEntity
                {
                    Id = missingDeliveryId, DecisionId = missingDecisionId, AssetId = assetId, DestinationId = missingDestinationId,
                    TargetPath = "synthetic/missing.jpg", Required = true, State = DeliveryState.Pending, CreatedAt = time,
                });
            await database.SaveChangesAsync(token);
        }

        using var decisions = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/captures/{captureId}/routing", token));
        var decisionNames = decisions.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("destinationId").GetGuid(), item => item.GetProperty("destinationName"));
        Assert.Equal("Archived route-only", decisionNames[routeOnlyDestinationId].GetString());
        Assert.Equal("Archived work target", decisionNames[workDestinationId].GetString());
        Assert.Equal(JsonValueKind.Null, decisionNames[missingDestinationId].ValueKind);

        using var deliveries = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/captures/{captureId}/deliveries", token));
        var deliveryNames = deliveries.RootElement.GetProperty("items").EnumerateArray()
            .ToDictionary(item => item.GetProperty("destinationId").GetGuid(), item => item.GetProperty("destinationName"));
        Assert.Equal("Archived work target", deliveryNames[workDestinationId].GetString());
        Assert.Equal(JsonValueKind.Null, deliveryNames[missingDestinationId].ValueKind);

        using var work = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/destinations/{workDestinationId}/work", token));
        Assert.Equal("Archived work target", work.RootElement.GetProperty("items")[0].GetProperty("destinationName").GetString());
    }

    [Fact]
    public async Task Growing_histories_have_separate_bounded_pages_and_cannot_reuse_another_history_cursor()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        Guid assetId = Guid.NewGuid(), destinationId = Guid.NewGuid(), deliveryId = Guid.NewGuid();
        DateTimeOffset time = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            var capture = await database.Captures.FindAsync([captureId], token);
            database.Assets.Add(new AssetEntity
            {
                Id = assetId, SourceId = capture!.SourceId, OriginalName = "synthetic.jpg", SpoolKey = "synthetic/original",
                Size = 4, Sha256 = new string('0', 64), State = "Spooled", MetadataRole = "JPEG", RecoveryCorrelation = Guid.NewGuid(), IngestedAt = time,
            });
            database.CaptureAssets.Add(new CaptureAssetEntity { AssetId = assetId, CaptureId = captureId, Role = "JPEG", IsPrimary = true, GroupingReason = "synthetic" });
            database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "synthetic-history", Kind = "local" });
            for (int index = 0; index < 105; index++)
            {
                Guid decisionId = Guid.NewGuid(), currentDelivery = index == 0 ? deliveryId : Guid.NewGuid();
                database.RoutingDecisions.Add(new RoutingDecisionEntity
                {
                    Id = decisionId, CaptureId = captureId, AssetId = assetId, DestinationId = destinationId, Generation = index,
                    IsCurrent = index == 104, Kind = RoutingDecisionKindValue.Matched, Required = true,
                    TargetPath = $"synthetic/{index}.jpg", TargetPathKey = $"synthetic/{index}.jpg", Explanation = "Synthetic match", CreatedAt = time,
                });
                database.Deliveries.Add(new DeliveryEntity
                {
                    Id = currentDelivery, DecisionId = decisionId, AssetId = assetId, DestinationId = destinationId,
                    TargetPath = $"synthetic/{index}.jpg", Required = true, State = DeliveryState.Delivered, CreatedAt = time,
                });
                database.RoutingActions.Add(new RoutingActionEntity
                {
                    Id = Guid.NewGuid(), Kind = "Retry", AssetId = assetId, DestinationId = destinationId,
                    Fingerprint = "synthetic", CreatedAt = time, Result = "queued",
                });
            }
            for (int index = 0; index < 105; index++)
                database.DeliveryAttempts.Add(new DeliveryAttemptEntity
                {
                    Id = Guid.NewGuid(), DeliveryId = deliveryId, AssetId = assetId, RetryNumber = index,
                    WorkerId = "synthetic-worker", ClaimToken = Guid.NewGuid(), StartedAt = time, TraceId = "synthetic-trace", Result = "Confirmed",
                });
            await database.SaveChangesAsync(token);
        }
        string[] paths = [$"/management/captures/{captureId}/routing", $"/management/captures/{captureId}/deliveries",
            $"/management/captures/{captureId}/actions", $"/management/deliveries/{deliveryId}/attempts"];
        foreach (string path in paths)
        {
            using var first = JsonDocument.Parse(await fixture.Client.GetStringAsync(path, token));
            Assert.Equal(30, first.RootElement.GetProperty("items").GetArrayLength());
            string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
            using var mismatched = await fixture.Client.GetAsync(paths.First(other => other != path) + "?cursor=" + Uri.EscapeDataString(cursor), token);
            Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
            var seen = first.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToHashSet();
            using var rest = JsonDocument.Parse(await fixture.Client.GetStringAsync(path + "?limit=100&cursor=" + Uri.EscapeDataString(cursor), token));
            Assert.Equal(75, rest.RootElement.GetProperty("items").GetArrayLength());
            foreach (var item in rest.RootElement.GetProperty("items").EnumerateArray()) Assert.True(seen.Add(item.GetProperty("id").GetGuid()));
            Assert.Equal(JsonValueKind.Null, rest.RootElement.GetProperty("nextCursor").ValueKind);
            Assert.Equal(105, seen.Count);
        }
        using var detail = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/captures/{captureId}", token));
        Assert.False(detail.RootElement.TryGetProperty("routing", out _));
    }

    [Fact]
    public async Task Destination_work_filters_before_keyset_paging_binds_cursor_and_tracks_current_state()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] captureIds = await SeedMetadataCapturesAsync(fixture, 3);
        Guid[] assetIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        Guid destinationId = Guid.NewGuid(), otherDestinationId = Guid.NewGuid();
        Guid deliveredId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        Guid firstPendingId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        Guid secondPendingId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        DateTimeOffset createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity[] captures = await database.Captures.Where(item => captureIds.Contains(item.Id)).ToArrayAsync(token);
            database.Assets.AddRange(assetIds.Select((assetId, index) => new AssetEntity
            {
                Id = assetId, SourceId = captures.Single(item => item.Id == captureIds[index]).SourceId,
                OriginalName = $"synthetic-{index}.jpg", SpoolKey = $"synthetic/original/{index}",
                Size = 4, Sha256 = new string('0', 64), State = "Spooled", MetadataRole = "JPEG", RecoveryCorrelation = Guid.NewGuid(), IngestedAt = createdAt,
            }));
            database.CaptureAssets.AddRange(assetIds.Select((assetId, index) => new CaptureAssetEntity
            { AssetId = assetId, CaptureId = captureIds[index], Role = "JPEG", IsPrimary = true, GroupingReason = "synthetic" }));
            database.Destinations.AddRange(
                new DestinationEntity { Id = destinationId, Name = "synthetic-work", Kind = "local" },
                new DestinationEntity { Id = otherDestinationId, Name = "synthetic-other-work", Kind = "local" });
            Guid[] decisionIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
            Guid[] deliveryIds = [deliveredId, firstPendingId, secondPendingId];
            DeliveryState[] states = [DeliveryState.Delivered, DeliveryState.Pending, DeliveryState.Pending];
            for (int index = 0; index < deliveryIds.Length; index++)
            {
                database.RoutingDecisions.Add(new RoutingDecisionEntity
                {
                    Id = decisionIds[index], CaptureId = captureIds[index], AssetId = assetIds[index], DestinationId = destinationId,
                    Generation = index, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched, Required = true,
                    TargetPath = $"synthetic/{index}.jpg", TargetPathKey = $"synthetic/{index}.jpg", Explanation = "Synthetic match", CreatedAt = createdAt,
                });
                database.Deliveries.Add(new DeliveryEntity
                {
                    Id = deliveryIds[index], DecisionId = decisionIds[index], AssetId = assetIds[index], DestinationId = destinationId,
                    TargetPath = $"synthetic/{index}.jpg", Required = true, State = states[index], CreatedAt = createdAt,
                });
            }
            await database.SaveChangesAsync(token);
        }

        string path = $"/management/destinations/{destinationId}/work?state=Pending&limit=1";
        using var first = JsonDocument.Parse(await fixture.Client.GetStringAsync(path, token));
        JsonElement firstItem = first.RootElement.GetProperty("items")[0];
        Assert.Equal(firstPendingId, firstItem.GetProperty("id").GetGuid());
        Assert.Equal(captureIds[1], firstItem.GetProperty("captureId").GetGuid());
        Assert.Equal(assetIds[1], firstItem.GetProperty("assetId").GetGuid());
        Assert.Equal(destinationId, firstItem.GetProperty("destinationId").GetGuid());
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;

        using var badState = await fixture.Client.GetAsync($"/management/destinations/{destinationId}/work?state=unknown", token);
        Assert.Equal(HttpStatusCode.BadRequest, badState.StatusCode);
        using var otherFilter = await fixture.Client.GetAsync($"/management/destinations/{destinationId}/work?state=Delivered&cursor={Uri.EscapeDataString(cursor)}", token);
        Assert.Equal(HttpStatusCode.BadRequest, otherFilter.StatusCode);
        using var otherDestination = await fixture.Client.GetAsync($"/management/destinations/{otherDestinationId}/work?state=Pending&cursor={Uri.EscapeDataString(cursor)}", token);
        Assert.Equal(HttpStatusCode.BadRequest, otherDestination.StatusCode);
        using var missing = await fixture.Client.GetAsync($"/management/destinations/{Guid.NewGuid()}/work", token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            DeliveryEntity changed = await database.Deliveries.SingleAsync(item => item.Id == secondPendingId, token);
            changed.State = DeliveryState.Delivered;
            await database.SaveChangesAsync(token);
        }
        using var next = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/destinations/{destinationId}/work?state=Pending&limit=1&cursor={Uri.EscapeDataString(cursor)}", token));
        Assert.Empty(next.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, next.RootElement.GetProperty("nextCursor").ValueKind);
    }
}
