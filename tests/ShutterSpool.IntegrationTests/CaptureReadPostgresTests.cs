using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Captures;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Persistence;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class CaptureReadPostgresTests
{
    [Fact]
    public async Task Attention_filters_preserve_each_reason_and_its_complement_before_paging()
    {
        AttentionScenario[] scenarios =
        [
            new("unreleased-unrouted"),
            new("released-unrouted", Released: true, Expected: true),
            new("secondary-unrouted", Released: true, Primary: false),
            new("no-destinations", Routed: true),
            new("no-match", Routed: true, Decision: RoutingDecisionKindValue.NoMatch),
            new("retention-eligible", RetentionDue: true, Expected: true),
            new("secondary-no-match", Primary: false, Routed: true, Decision: RoutingDecisionKindValue.NoMatch),
            new("matched", Routed: true, Decision: RoutingDecisionKindValue.Matched),
            new("historical-invalid", Routed: true, Decision: RoutingDecisionKindValue.Matched, HistoricalProblem: true),
            new("invalid-target", Decision: RoutingDecisionKindValue.InvalidTarget, Expected: true),
            new("disabled-destination", Decision: RoutingDecisionKindValue.DestinationDisabled, Expected: true),
            new("secondary-invalid", Primary: false, Decision: RoutingDecisionKindValue.InvalidTarget, Expected: true),
            new("pending", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Pending),
            new("running", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Running),
            new("retry", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.RetryWait),
            new("delivered", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Delivered),
            new("superseded", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Superseded),
            new("failed", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Failed, Expected: true),
            new("ambiguous", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Ambiguous, Expected: true),
            new("disabled-work", Routed: true, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Disabled, Expected: true),
            new("secondary-failed", Primary: false, Decision: RoutingDecisionKindValue.Matched, Delivery: DeliveryState.Failed, Expected: true),
        ];
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await database.Database.MigrateAsync(token);
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-attention" });
        database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "synthetic-attention", Kind = "local" });
        DateTimeOffset anchor = DateTimeOffset.UtcNow.AddHours(-1);
        var expected = new Dictionary<Guid, bool>();
        Guid failedCaptureId = Guid.Empty, failedDeliveryId = Guid.Empty, secondaryInvalidCaptureId = Guid.Empty;
        for (int index = 0; index < scenarios.Length; index++)
        {
            AttentionScenario scenario = scenarios[index];
            Guid captureId = Guid.NewGuid(), assetId = Guid.NewGuid(), decisionId = Guid.NewGuid();
            expected[captureId] = scenario.Expected;
            if (scenario.Name == "secondary-invalid") secondaryInvalidCaptureId = captureId;
            database.Captures.Add(new CaptureEntity
            {
                Id = captureId, SourceId = sourceId, NormalizedStem = scenario.Name,
                FirstAcceptedAt = anchor.AddMinutes(index), LastPrimaryIngestedAt = anchor.AddMinutes(index),
                ReleasedAt = scenario.Released ? anchor : null,
                RetentionFactsDirty = !scenario.RetentionDue,
                RetentionTrashEligibleAt = scenario.RetentionDue ? anchor.AddMinutes(1) : null,
                RetentionAttentionAt = scenario.RetentionDue ? anchor.AddMinutes(-1) : null,
            });
            database.Assets.Add(new AssetEntity
            {
                Id = assetId, SourceId = sourceId, OriginalName = scenario.Name + ".jpg", SpoolKey = "synthetic/" + assetId,
                Size = 4, Sha256 = new string('0', 64), State = "Spooled", IngestedAt = anchor, RecoveryCorrelation = Guid.NewGuid(),
            });
            database.CaptureAssets.Add(new CaptureAssetEntity
            {
                AssetId = assetId, CaptureId = captureId, Role = "JPEG", IsPrimary = scenario.Primary, GroupingReason = "synthetic",
            });
            if (scenario.Routed) database.RoutedAssets.Add(new RoutedAssetEntity { AssetId = assetId, EvaluatedAt = anchor });
            if (scenario.Decision is RoutingDecisionKindValue kind)
                database.RoutingDecisions.Add(new RoutingDecisionEntity
                {
                    Id = decisionId, CaptureId = captureId, AssetId = assetId, DestinationId = destinationId,
                    Generation = 1, IsCurrent = true, Kind = kind, Explanation = "synthetic", CreatedAt = anchor,
                });
            if (scenario.HistoricalProblem)
                database.RoutingDecisions.Add(new RoutingDecisionEntity
                {
                    Id = Guid.NewGuid(), CaptureId = captureId, AssetId = assetId, DestinationId = destinationId,
                    IsCurrent = false, Kind = RoutingDecisionKindValue.InvalidTarget, Explanation = "synthetic old problem", CreatedAt = anchor,
                });
            if (scenario.Delivery is DeliveryState state)
            {
                Guid currentDeliveryId = Guid.NewGuid();
                database.Deliveries.Add(new DeliveryEntity
                {
                    Id = currentDeliveryId, DecisionId = decisionId, AssetId = assetId, DestinationId = destinationId,
                    TargetPath = "synthetic/" + assetId + ".jpg", State = state, CreatedAt = anchor,
                });
                if (scenario.Name == "failed") (failedCaptureId, failedDeliveryId) = (captureId, currentDeliveryId);
            }
        }
        await database.SaveChangesAsync(token);

        var store = new CaptureReadStore(database);
        CaptureInboxPage summaryPage = await store.GetInboxAsync(scenarios.Length, null, null, new(), token);
        CaptureRead secondaryInvalid = Assert.Single(summaryPage.Items, item => item.Id == secondaryInvalidCaptureId);
        Assert.True(secondaryInvalid.DeliverySummary.Attention > 0);

        foreach (bool attention in new[] { true, false })
        {
            var seen = new HashSet<Guid>();
            DateTimeOffset? before = null;
            Guid? beforeId = null;
            do
            {
                CaptureInboxPage page = await store.GetInboxAsync(2, before, beforeId, new(Attention: attention), token);
                Assert.InRange(page.Items.Length, 1, 2);
                foreach (CaptureRead capture in page.Items) Assert.True(seen.Add(capture.Id));
                (before, beforeId) = (page.NextBefore, page.NextBeforeId);
            } while (before is not null);
            Assert.Equal(expected.Where(item => item.Value == attention).Select(item => item.Key).Order(), seen.Order());

            seen.Clear();
            before = null;
            beforeId = null;
            do
            {
                CaptureInboxPage page = await store.GetInboxAsync(2, before, beforeId,
                    new(Attention: attention, OldestFirst: true), token);
                foreach (CaptureRead capture in page.Items) Assert.True(seen.Add(capture.Id));
                (before, beforeId) = (page.NextBefore, page.NextBeforeId);
            } while (before is not null);
            Assert.Equal(expected.Where(item => item.Value == attention).Select(item => item.Key).Order(), seen.Order());
        }

        DeliveryEntity failedDelivery = await database.Deliveries.SingleAsync(item => item.Id == failedDeliveryId, token);
        foreach (DeliveryState state in new[] { DeliveryState.RetryWait, DeliveryState.Delivered })
        {
            failedDelivery.State = state;
            await database.SaveChangesAsync(token);
            CaptureInboxPage attentionPage = await store.GetInboxAsync(scenarios.Length, null, null,
                new(Attention: true), token);
            Assert.Equal(expected.Where(item => item.Value && item.Key != failedCaptureId).Select(item => item.Key).Order(),
                attentionPage.Items.Select(item => item.Id).Order());
            CaptureInboxPage otherPage = await store.GetInboxAsync(scenarios.Length, null, null,
                new(Attention: false), token);
            Assert.Contains(failedCaptureId, otherPage.Items.Select(item => item.Id));
        }
    }

    [Fact]
    public async Task Inbox_is_bounded_newest_first_and_excludes_ungrouped_assets()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection))
            throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");

        string databaseName = $"m110_{Guid.NewGuid():N}";
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        try
        {
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            await using var database = new ShutterSpoolDbContext(options);
            CancellationToken token = TestContext.Current.CancellationToken;
            await database.Database.MigrateAsync(token);
            Guid sourceId = Guid.NewGuid();
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-read-source" });
            DateTimeOffset acceptedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            var captures = Enumerable.Range(0, 3).Select(index => new CaptureEntity
            {
                Id = Guid.Parse($"00000000-0000-0000-0000-{index + 1:D12}"), SourceId = sourceId, NormalizedStem = $"CAPTURE-{index}",
                LastPrimaryIngestedAt = acceptedAt.AddMinutes(index / 2), FirstAcceptedAt = acceptedAt.AddMinutes(index / 2), Rating = index + 1,
                Tags = [$"tag-{index}"], State = CaptureState.CURATING,
            }).ToArray();
            database.Captures.AddRange(captures);
            var assets = captures.Select((capture, index) => new AssetEntity
            {
                Id = Guid.NewGuid(), SourceId = sourceId, OriginalName = "same-name.jpg",
                SpoolKey = $"spool/{Guid.NewGuid():N}", Size = 4, Sha256 = new string('0', 64),
                IngestedAt = acceptedAt.AddMinutes(index), RecoveryCorrelation = Guid.NewGuid(),
                State = "Spooled", MetadataRole = "JPEG",
            }).ToArray();
            database.Assets.AddRange(assets);
            for (int index = 0; index < captures.Length; index++)
                database.CaptureAssets.Add(new CaptureAssetEntity
                {
                    AssetId = assets[index].Id, CaptureId = captures[index].Id, Role = "JPEG",
                    IsPrimary = true, GroupingReason = "synthetic",
                });
            database.Assets.Add(new AssetEntity
            {
                Id = Guid.NewGuid(), SourceId = sourceId, OriginalName = "ungrouped.jpg",
                SpoolKey = "spool/ungrouped", Size = 4, Sha256 = new string('0', 64),
                IngestedAt = acceptedAt.AddMinutes(4), RecoveryCorrelation = Guid.NewGuid(),
                State = "Spooled", MetadataRole = "JPEG",
            });
            await database.SaveChangesAsync(token);

            var reads = new CaptureReadService(new CaptureReadStore(database));
            CaptureInboxPage first = await reads.GetInboxAsync(2, null, null, new(), token);
            Assert.Equal(captures[2].Id, first.Items[0].Id);
            Assert.Equal(captures[1].Id, first.Items[1].Id);
            Assert.Equal("same-name.jpg", first.Items[0].DisplayName);
            Assert.Equal(3, first.Items[0].Curation.Rating);
            Assert.Equal("tag-2", first.Items[0].Curation.Tags[0]);
            Assert.Equal(assets[2].Id, first.Items[0].Assets.Single().Id);
            Assert.NotNull(first.NextBefore);

            CaptureInboxPage second = await reads.GetInboxAsync(2, first.NextBefore, first.NextBeforeId, new(), token);
            Assert.Equal([captures[0].Id], second.Items.Select(item => item.Id));
            Assert.Null(second.NextBefore);

            var oldestFirst = new List<Guid>();
            DateTimeOffset? after = null;
            Guid? afterId = null;
            do
            {
                CaptureInboxPage page = await reads.GetInboxAsync(2, after, afterId, new(OldestFirst: true), token);
                oldestFirst.AddRange(page.Items.Select(item => item.Id));
                (after, afterId) = (page.NextBefore, page.NextBeforeId);
            } while (after is not null);
            Assert.Equal([captures[0].Id, captures[1].Id, captures[2].Id], oldestFirst);
            Assert.Null(await reads.GetAsync(Guid.NewGuid(), token));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
    private sealed record AttentionScenario(string Name, bool Released = false, bool Primary = true, bool Routed = false,
        RoutingDecisionKindValue? Decision = null, bool HistoricalProblem = false, DeliveryState? Delivery = null,
        bool RetentionDue = false, bool Expected = false);
}
