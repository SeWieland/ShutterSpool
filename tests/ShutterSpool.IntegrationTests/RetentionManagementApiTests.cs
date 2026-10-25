using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Routing;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Settings_preview_reports_proposed_quota_headroom_from_one_authoritative_snapshot()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        _ = await SeedRetentionCaptureAsync(fixture, token);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.SpoolQuotaReservations.Add(new SpoolQuotaReservationEntity
            {
                OperationId = Guid.NewGuid(), ReservedBytes = 5, CreatedAt = DateTimeOffset.UtcNow,
            });
            await database.SaveChangesAsync(token);
        }

        async Task<JsonNode> PreviewAsync(long quotaBytes, bool active = true)
        {
            JsonNode saved = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
            var request = new JsonObject
            {
                ["expectedSettingsRevision"] = saved["settings"]!["revision"]!.GetValue<int>(),
                ["expectedQuotaRevision"] = saved["quota"]!["revision"]!.GetValue<int>(),
                ["settings"] = saved["settings"]!.DeepClone(),
                ["quota"] = saved["quota"]!.DeepClone(),
                ["previewCache"] = saved["previewCache"]!.DeepClone(),
            };
            request["quota"]!["isActive"] = active;
            request["quota"]!["quotaBytes"] = quotaBytes;
            request["quota"]!["highWatermarkBytes"] = quotaBytes - 1;
            request["quota"]!["recoveryTargetBytes"] = quotaBytes - 2;
            using var response = await fixture.Client.PostAsJsonAsync("/management/retention/settings/preview", request, token);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
        }

        JsonNode lower = await PreviewAsync(3);
        Assert.True(lower["accountingKnown"]!.GetValue<bool>());
        Assert.Equal(5, lower["reservedBytes"]!.GetValue<long>());
        long occupancy = lower["usedBytes"]!.GetValue<long>() + lower["reservedBytes"]!.GetValue<long>();
        Assert.Equal(3 - occupancy, lower["proposedQuotaHeadroomBytes"]!.GetValue<long>());
        Assert.False(lower["overQuota"]!.GetValue<bool>());
        Assert.False(lower["admissionBlocked"]!.GetValue<bool>());

        JsonNode higher = await PreviewAsync(20);
        Assert.Equal(20 - occupancy, higher["proposedQuotaHeadroomBytes"]!.GetValue<long>());
        JsonNode current = await PreviewAsync(occupancy);
        Assert.Equal(0, current["proposedQuotaHeadroomBytes"]!.GetValue<long>());
        JsonNode inactive = await PreviewAsync(20, active: false);
        Assert.Null(inactive["proposedQuotaHeadroomBytes"]);

        string markerPath = Path.Combine(fixture.Database.SpoolRoot, ".shutterspool-root.marker");
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
            await database.SpoolQuotaObservations.Where(item => item.Id == 1)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.StorageMarker, (string?)null), token);
        File.Delete(markerPath);
        JsonNode unknown = await PreviewAsync(20);
        Assert.False(unknown["accountingKnown"]!.GetValue<bool>());
        Assert.Null(unknown["proposedQuotaHeadroomBytes"]);
    }

    [Fact]
    public async Task Explicit_no_delivery_is_only_recorded_after_routing_without_delivery_obligations()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = await SeedRetentionCaptureAsync(fixture, token);
        int routedRevision;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity initialCapture = await database.Captures.SingleAsync(item => item.Id == id, token);
            Guid destinationId = Guid.NewGuid();
            database.Destinations.Add(new DestinationEntity
                { Id = destinationId, Name = "synthetic-no-route", Kind = "local", Enabled = true });
            await database.SaveChangesAsync(token);
            await new DestinationRoutingAdminService(database).SetAsync(
                new DestinationRouting(destinationId, true, []), token);
            Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System))
                .ReleaseNowAsync(id, initialCapture.Revision, token)).Succeeded);
            Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
            Guid assetId = await database.CaptureAssets.Where(item => item.CaptureId == id)
                .Select(item => item.AssetId).SingleAsync(token);
            Assert.True(await database.RoutedAssets.AnyAsync(item => item.AssetId == assetId, token));
            Assert.True(await database.RoutingDecisions.AnyAsync(item => item.CaptureId == id
                && item.AssetId == assetId && item.IsCurrent && item.Kind == RoutingDecisionKindValue.NoMatch, token));
            Assert.False(await database.Deliveries.AnyAsync(item => item.AssetId == assetId, token));
            routedRevision = await database.Captures.Where(item => item.Id == id)
                .Select(item => item.Revision).SingleAsync(token);
        }

        using var response = await fixture.Client.PostAsJsonAsync($"/management/captures/{id}/no-delivery-intent",
            new { expectedRevision = routedRevision }, token);
        response.EnsureSuccessStatusCode();
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
        Assert.Equal("Committed", result["status"]!.GetValue<string>());
        Assert.Contains("explicit-no-delivery", result["current"]!["eligibilityReasons"]!.AsArray()
            .Select(item => item!.GetValue<string>()));
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity capture = await verify.Captures.SingleAsync(item => item.Id == id, token);
        Assert.True(capture.NoDeliveryIntent);
        Assert.True(capture.RetentionFactsDirty);
        Assert.Equal(routedRevision + 1, capture.Revision);
    }

    [Fact]
    public async Task Ninety_day_attempt_pruning_keeps_compact_delivery_confirmation_and_ambiguous_evidence()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid assetId;
        Guid deliveredAttemptId = Guid.NewGuid();
        Guid ambiguousAttemptId = Guid.NewGuid();
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        assetId = await database.CaptureAssets.Where(item => item.CaptureId == captureId)
            .Select(item => item.AssetId).SingleAsync(token);
        foreach ((DeliveryState state, Guid attemptId, string suffix) in new[]
            { (DeliveryState.Delivered, deliveredAttemptId, "delivered"), (DeliveryState.Ambiguous, ambiguousAttemptId, "ambiguous") })
        {
            Guid decisionId = Guid.NewGuid();
            Guid deliveryId = Guid.NewGuid();
            database.RoutingDecisions.Add(new RoutingDecisionEntity
            {
                Id = decisionId,
                CaptureId = captureId,
                AssetId = assetId,
                DestinationId = Guid.NewGuid(),
                Generation = suffix == "delivered" ? 1 : 2,
                IsCurrent = suffix == "ambiguous",
                Kind = RoutingDecisionKindValue.Matched,
                Required = true,
                TargetPath = $"synthetic/{suffix}",
                TargetPathKey = $"SYNTHETIC/{suffix}",
                Explanation = "Synthetic retained evidence.",
                CreatedAt = now.AddDays(-120),
            });
            database.Deliveries.Add(new DeliveryEntity
            {
                Id = deliveryId,
                DecisionId = decisionId,
                AssetId = assetId,
                DestinationId = Guid.NewGuid(),
                TargetPath = $"synthetic/{suffix}",
                Required = true,
                State = state,
                ConfirmedAt = state == DeliveryState.Delivered ? now.AddDays(-120) : null,
                CreatedAt = now.AddDays(-120),
            });
            database.DeliveryAttempts.Add(new DeliveryAttemptEntity
            {
                Id = attemptId,
                DeliveryId = deliveryId,
                AssetId = assetId,
                RetryNumber = 1,
                WorkerId = "synthetic",
                ClaimToken = Guid.NewGuid(),
                StartedAt = now.AddDays(-120),
                FinishedAt = now.AddDays(-120),
                Result = state.ToString(),
                TraceId = "synthetic-trace",
                ErrorMessage = "Synthetic verbose detail.",
            });
        }
        await database.SaveChangesAsync(token);
        int removed = await new EfRetentionRemovalStore(database,
            new EfCaptureRetentionStore(database, TimeProvider.System), TimeProvider.System)
            .PruneCompletedAttemptHistoryAsync(now.AddDays(-90), 500, token);
        Assert.Equal(1, removed);
        Assert.True(await database.Deliveries.AnyAsync(item => item.AssetId == assetId
            && item.State == DeliveryState.Delivered && item.ConfirmedAt != null, token));
        Assert.True(await database.DeliveryAttempts.AnyAsync(item => item.Id == ambiguousAttemptId, token));
        Assert.False(await database.DeliveryAttempts.AnyAsync(item => item.Id == deliveredAttemptId, token));
    }

    [Fact]
    public async Task Favorite_after_release_preserves_curation_snapshot_and_does_not_add_delivery_work()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = await SeedRetentionCaptureAsync(fixture, token);
        using var edit = await fixture.Client.PutAsJsonAsync($"/management/captures/{id}/curation",
            new { expectedRevision = 0, rating = 4, label = "Blue", tags = new[] { "synthetic" } }, token);
        edit.EnsureSuccessStatusCode();
        var edited = JsonNode.Parse(await edit.Content.ReadAsStringAsync(token))!;
        using var release = await fixture.Client.PostAsJsonAsync($"/management/captures/{id}/release",
            new { expectedRevision = edited["revision"]!.GetValue<int>() }, token);
        release.EnsureSuccessStatusCode();
        var released = JsonNode.Parse(await release.Content.ReadAsStringAsync(token))!;
        using var heart = await fixture.Client.PutAsJsonAsync($"/management/captures/{id}/favorite",
            new { expectedRevision = released["revision"]!.GetValue<int>(), isFavorite = true }, token);
        heart.EnsureSuccessStatusCode();

        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity capture = await database.Captures.AsNoTracking().SingleAsync(item => item.Id == id, token);
        Assert.True(capture.IsFavorite);
        Assert.Equal(4, capture.Rating);
        Assert.Equal(4, capture.ReleasedRating);
        Assert.Equal("Blue", capture.ReleasedLabel);
        Assert.Equal(new[] { "synthetic" }, capture.ReleasedTags);
        Assert.Empty(await database.Deliveries.ToArrayAsync(token));
        string key = await database.CaptureAssets.Where(member => member.CaptureId == id)
            .Join(database.Assets, member => member.AssetId, asset => asset.Id, (member, asset) => asset.SpoolKey)
            .SingleAsync(token);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Combine(fixture.Database.SpoolRoot, key), token));
    }

    [Fact]
    public async Task Trash_rejects_late_heart_but_expired_recovery_time_alone_does_not_own_purge()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = await SeedRetentionCaptureAsync(fixture, token);
        using var trash = await fixture.Client.PostAsJsonAsync($"/management/captures/{id}/trash",
            new { expectedRevision = 0 }, token);
        trash.EnsureSuccessStatusCode();
        using var lateHeart = await fixture.Client.PutAsJsonAsync($"/management/captures/{id}/favorite",
            new { expectedRevision = 1, isFavorite = true }, token);
        Assert.Equal(HttpStatusCode.Conflict, lateHeart.StatusCode);

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            await database.Captures.Where(item => item.Id == id).ExecuteUpdateAsync(setters =>
                setters.SetProperty(item => item.RecoverableUntil, DateTimeOffset.UtcNow.AddSeconds(-1)), token);
        }
        using var restore = await fixture.Client.PostAsJsonAsync($"/management/trash/{id}/restore",
            new { expectedRevision = 1 }, token);
        restore.EnsureSuccessStatusCode();
        var outcome = JsonNode.Parse(await restore.Content.ReadAsStringAsync(token))!;
        DateTimeOffset protectedUntil = outcome["current"]!["protectedUntil"]!.GetValue<DateTimeOffset>();
        Assert.True(protectedUntil > DateTimeOffset.UtcNow.AddHours(71));
        using var repeated = await fixture.Client.PostAsJsonAsync($"/management/trash/{id}/restore",
            new { expectedRevision = 1 }, token);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        await using var final = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity restored = await final.Captures.SingleAsync(item => item.Id == id, token);
        Assert.Equal("Active", restored.RetentionDisposition);
        Assert.Equal(protectedUntil.ToUnixTimeMilliseconds(), restored.RestoreProtectedUntil!.Value.ToUnixTimeMilliseconds());
        Assert.True(restored.RetentionPaused);
        Assert.False(restored.IsFavorite);
    }

    [Fact]
    public async Task Automatic_original_removal_needs_explicit_impact_acknowledgement()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(fixture.Database.SpoolRoot);
        await using (AsyncServiceScope scope = fixture.Server.Services.CreateAsyncScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<ISpoolQuotaStore>().GetSnapshotAsync(token)).IsKnown);
        var saved = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        var request = new JsonObject
        {
            ["expectedSettingsRevision"] = saved["settings"]!["revision"]!.GetValue<int>(),
            ["expectedQuotaRevision"] = saved["quota"]!["revision"]!.GetValue<int>(),
            ["settings"] = saved["settings"]!.DeepClone(),
            ["quota"] = saved["quota"]!.DeepClone(),
            ["previewCache"] = saved["previewCache"]!.DeepClone(),
            ["acknowledgeImpact"] = false,
        };
        request["settings"]!["automaticRemovalEnabled"] = true;
        request["quota"]!["isActive"] = true;
        request["quota"]!["quotaBytes"] = 10 * 1024 * 1024;
        request["quota"]!["highWatermarkBytes"] = 8 * 1024 * 1024;
        request["quota"]!["recoveryTargetBytes"] = 5 * 1024 * 1024;
        using var rejected = await fixture.Client.PutAsJsonAsync("/management/retention/settings", request, token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var unchanged = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        Assert.False(unchanged["settings"]!["automaticRemovalEnabled"]!.GetValue<bool>());

        request["acknowledgeImpact"] = true;
        using var accepted = await fixture.Client.PutAsJsonAsync("/management/retention/settings", request, token);
        accepted.EnsureSuccessStatusCode();
        var current = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        Assert.True(current["settings"]!["automaticRemovalEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Quota_activation_can_acknowledge_legacy_storage_adoption_without_enabling_removal()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        _ = await SeedRetentionCaptureAsync(fixture, token);
        string markerPath = Path.Combine(fixture.Database.SpoolRoot, ".shutterspool-root.marker");
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            await database.SpoolQuotaObservations.Where(item => item.Id == 1)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.StorageMarker, (string?)null), token);
            await database.SpoolQuotaSettings.Where(item => item.Id == 1)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.StorageMarkerAdoptionAuthorized, false), token);
        }
        File.Delete(markerPath);

        var saved = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        Assert.False(saved["settings"]!["automaticRemovalEnabled"]!.GetValue<bool>());
        Assert.False(saved["quota"]!["isActive"]!.GetValue<bool>());
        var request = new JsonObject
        {
            ["expectedSettingsRevision"] = saved["settings"]!["revision"]!.GetValue<int>(),
            ["expectedQuotaRevision"] = saved["quota"]!["revision"]!.GetValue<int>(),
            ["settings"] = saved["settings"]!.DeepClone(),
            ["quota"] = saved["quota"]!.DeepClone(),
            ["previewCache"] = saved["previewCache"]!.DeepClone(),
            ["acknowledgeImpact"] = false,
        };
        request["quota"]!["isActive"] = true;
        request["quota"]!["quotaBytes"] = 10 * 1024 * 1024;
        request["quota"]!["highWatermarkBytes"] = 8 * 1024 * 1024;
        request["quota"]!["recoveryTargetBytes"] = 5 * 1024 * 1024;

        using var preview = await fixture.Client.PostAsJsonAsync("/management/retention/settings/preview", request, token);
        preview.EnsureSuccessStatusCode();
        var impact = JsonNode.Parse(await preview.Content.ReadAsStringAsync(token))!;
        Assert.False(impact["complete"]!.GetValue<bool>());
        Assert.True(impact["quotaActivationAcknowledgementRequired"]!.GetValue<bool>());

        using var rejected = await fixture.Client.PutAsJsonAsync("/management/retention/settings", request, token);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var rejection = JsonNode.Parse(await rejected.Content.ReadAsStringAsync(token))!;
        Assert.Equal("QUOTA_ACTIVATION_ACKNOWLEDGEMENT_REQUIRED", rejection["errorCode"]!.GetValue<string>());

        request["acknowledgeImpact"] = true;
        using var accepted = await fixture.Client.PutAsJsonAsync("/management/retention/settings", request, token);
        accepted.EnsureSuccessStatusCode();
        var result = JsonNode.Parse(await accepted.Content.ReadAsStringAsync(token))!;
        Assert.False(result["impact"]!["complete"]!.GetValue<bool>());

        var current = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        Assert.True(current["quota"]!["isActive"]!.GetValue<bool>());
        Assert.False(current["settings"]!["automaticRemovalEnabled"]!.GetValue<bool>());
        using var capacity = await fixture.Client.GetAsync("/management/retention/capacity", token);
        capacity.EnsureSuccessStatusCode();
        var measured = JsonNode.Parse(await capacity.Content.ReadAsStringAsync(token))!;
        Assert.False(measured["unknownAccounting"]!.GetValue<bool>());
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task Invalid_retention_revision_returns_validation_error_without_mutation()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = await SeedRetentionCaptureAsync(fixture, token);
        using var result = await fixture.Client.PostAsJsonAsync($"/management/captures/{id}/trash",
            new { expectedRevision = -1 }, token);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal("Active", (await database.Captures.SingleAsync(item => item.Id == id, token)).RetentionDisposition);
    }

    [Theory]
    [InlineData("confirmed")]
    [InlineData("previewCache")]
    public async Task Explicit_null_retention_policy_fields_are_rejected_as_invalid_requests(string field)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JsonNode saved = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        var request = new JsonObject
        {
            ["expectedSettingsRevision"] = saved["settings"]!["revision"]!.GetValue<int>(),
            ["expectedQuotaRevision"] = saved["quota"]!["revision"]!.GetValue<int>(),
            ["settings"] = saved["settings"]!.DeepClone(),
            ["quota"] = saved["quota"]!.DeepClone(),
            ["previewCache"] = saved["previewCache"]!.DeepClone(),
        };
        if (field == "confirmed") request["settings"]![field] = null;
        else request[field] = null;
        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/management/retention/settings/preview", request, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonNode after = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        Assert.Equal(saved["settings"]!["revision"]!.GetValue<int>(), after["settings"]!["revision"]!.GetValue<int>());
    }

    [Fact]
    public async Task Invalid_preview_cache_budget_is_a_validation_error_and_keeps_saved_policy()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        var saved = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        var request = new JsonObject
        {
            ["expectedSettingsRevision"] = saved["settings"]!["revision"]!.GetValue<int>(),
            ["expectedQuotaRevision"] = saved["quota"]!["revision"]!.GetValue<int>(),
            ["settings"] = saved["settings"]!.DeepClone(),
            ["quota"] = saved["quota"]!.DeepClone(),
            ["previewCache"] = saved["previewCache"]!.DeepClone(),
        };
        request["previewCache"]!["memoryCacheBytes"] = 0;
        using var response = await fixture.Client.PutAsJsonAsync("/management/retention/settings", request, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var current = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/retention/settings", token))!;
        Assert.Equal(saved["settings"]!["revision"]!.GetValue<int>(), current["settings"]!["revision"]!.GetValue<int>());
    }

    private static async Task<Guid> SeedRetentionCaptureAsync(ConfigurationApiFixture fixture, CancellationToken token)
    {
        Guid captureId = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
        Directory.CreateDirectory(fixture.Database.SpoolRoot);
        var accounting = new FileSystemSpoolAccounting(fixture.Database.SpoolRoot,
            Path.Combine(fixture.Database.SpoolRoot, "previews"), 0, null, TimeProvider.System);
        Assert.True((await new EfSpoolQuotaStore(database, accounting, TimeProvider.System).GetSnapshotAsync(token)).IsKnown);
        Guid assetId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = fixture.Database.SpoolRoot, ReservedBytes = 0 });
        await using var bytes = new MemoryStream([1, 2, 3, 4]);
        var write = await spool.WriteAsync(capture.SourceId, assetId, bytes, null, token);
        database.Assets.Add(new AssetEntity
        {
            Id = assetId, SourceId = capture.SourceId, OriginalName = "synthetic.raw", SpoolKey = write.FinalKey,
            Size = write.Size, Sha256 = write.Sha256, IngestedAt = capture.FirstAcceptedAt,
            RecoveryCorrelation = operationId, State = "Spooled",
        });
        database.Operations.Add(new IngestOperationEntity
        {
            Id = operationId, AssetId = assetId, SourceId = capture.SourceId, OriginalName = "synthetic.raw",
            PartialKey = write.PartialKey, FinalKey = write.FinalKey, Size = write.Size, Sha256 = write.Sha256,
            State = "Committed",
        });
        database.CaptureAssets.Add(new CaptureAssetEntity
        {
            CaptureId = captureId, AssetId = assetId, IsPrimary = true, Role = "RAW", GroupingReason = "synthetic",
        });
        await database.SaveChangesAsync(token);
        return captureId;
    }
}
