using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Routing;
using ShutterSpool.Application.Spool;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Retention;
using ShutterSpool.Domain.Routing;
using ShutterSpool.Infrastructure.Persistence;
using System.Security.Cryptography;

namespace ShutterSpool.IntegrationTests;

public sealed class RetentionRoutingFencePostgresTests
{
    [Fact]
    public async Task Actual_invalid_target_is_unfinished_and_restarts_previously_qualified_favorite_clock()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset acceptedAt = PostgresNow().AddDays(-250);
        DateTimeOffset priorConfirmedAt = acceptedAt.AddDays(30);
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), historicalDestinationId = Guid.NewGuid();
        Guid ordinaryCaptureId = Guid.NewGuid(), favoriteCaptureId = Guid.NewGuid();
        Guid ordinaryAssetId = Guid.NewGuid(), favoriteAssetId = Guid.NewGuid();
        Guid priorDecisionId = Guid.NewGuid(), priorDeliveryId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-retention-routing" });
        database.Destinations.AddRange(
            new DestinationEntity { Id = destinationId, Name = "synthetic-invalid" },
            new DestinationEntity { Id = historicalDestinationId, Name = "synthetic-historical", IsArchived = true });
        database.Captures.AddRange(
            Capture(ordinaryCaptureId, sourceId, "invalid-only", acceptedAt),
            Capture(favoriteCaptureId, sourceId, "favorite-invalid", acceptedAt, favorite: true));
        database.Assets.AddRange(
            Asset(ordinaryAssetId, sourceId, "ordinary.jpg", "JPEG", acceptedAt),
            Asset(favoriteAssetId, sourceId, "favorite.raw", "RAW", acceptedAt));
        database.CaptureAssets.AddRange(
            Member(ordinaryAssetId, ordinaryCaptureId, "JPEG"),
            Member(favoriteAssetId, favoriteCaptureId, "RAW"));
        database.RoutingDecisions.Add(new RoutingDecisionEntity
        {
            Id = priorDecisionId, CaptureId = favoriteCaptureId, AssetId = favoriteAssetId,
            DestinationId = historicalDestinationId, Generation = 0, IsCurrent = true,
            Kind = RoutingDecisionKindValue.Matched, RuleId = "prior-confirmed", Required = true,
            TargetPath = "archive/favorite.raw", TargetPathKey = "ARCHIVE/FAVORITE.RAW",
            Explanation = "Synthetic prior positive confirmation", CreatedAt = priorConfirmedAt.AddMinutes(-1),
        });
        database.Deliveries.Add(new DeliveryEntity
        {
            Id = priorDeliveryId, DecisionId = priorDecisionId, AssetId = favoriteAssetId,
            DestinationId = historicalDestinationId, TargetPath = "archive/favorite.raw", Required = true,
            State = DeliveryState.Delivered, ConfirmedAt = priorConfirmedAt,
            ConfirmationEvidence = "synthetic-acknowledged-write", CreatedAt = priorConfirmedAt.AddMinutes(-1),
        });
        await database.SaveChangesAsync(token);

        RoutingRule[] invalidRules =
        [
            InvalidRule("raw-path", "RAW"),
            InvalidRule("jpeg-path", "JPEG"),
        ];
        await new DestinationRoutingAdminService(database)
            .SetAsync(new DestinationRouting(destinationId, true, invalidRules), token);
        var curation = new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System));
        Assert.True((await curation.ReleaseNowAsync(ordinaryCaptureId, 0, token)).Succeeded);
        Assert.True((await curation.ReleaseNowAsync(favoriteCaptureId, 0, token)).Succeeded);

        var router = new EfCaptureRoutingProcessor(database);
        Assert.True(await router.ProcessOneAsync(PostgresNow(), token));
        Assert.True(await router.ProcessOneAsync(PostgresNow(), token));

        RoutingDecisionEntity[] actualInvalidTargets = await database.RoutingDecisions.AsNoTracking()
            .Where(item => item.AssetId == ordinaryAssetId || item.AssetId == favoriteAssetId)
            .Where(item => item.Kind == RoutingDecisionKindValue.InvalidTarget && item.RuleId != null)
            .ToArrayAsync(token);
        Assert.Equal(2, actualInvalidTargets.Length);
        Assert.All(actualInvalidTargets, item => Assert.Contains(item.RuleId, new[] { "raw-path", "jpeg-path" }));
        Assert.Equal(0, await database.Deliveries.CountAsync(item => item.AssetId == ordinaryAssetId, token));

        var read = new EfCaptureRetentionStore(database, TimeProvider.System);
        CaptureRetentionSummary ordinary = Assert.IsType<CaptureRetentionSummary>(await read.GetCaptureSummaryAsync(
            ordinaryCaptureId, PostgresNow(), token));
        Assert.Equal(RetentionTier.HeldOrUnfinished, ordinary.Tier);
        Assert.Equal(acceptedAt.AddDays(14), ordinary.PressureEligibleAt);
        Assert.Equal(acceptedAt.AddDays(180), ordinary.TrashEligibleAt);

        DateTimeOffset lostAt = Assert.IsType<DateTimeOffset>(await database.Captures.AsNoTracking()
            .Where(item => item.Id == favoriteCaptureId).Select(item => item.FavoriteQualificationLostAt)
            .SingleAsync(token));
        CaptureRetentionSummary favorite = Assert.IsType<CaptureRetentionSummary>(await read.GetCaptureSummaryAsync(
            favoriteCaptureId, PostgresNow(), token));
        Assert.Equal(RetentionTier.HeldOrUnfinished, favorite.Tier);
        Assert.Equal(lostAt.AddDays(14), favorite.PressureEligibleAt);
        Assert.Equal(lostAt.AddDays(180), favorite.TrashEligibleAt);
        Assert.NotEqual(RetentionTier.ConfirmedFavorite, favorite.Tier);
    }

    [Fact]
    public async Task Recognized_replay_uses_supplier_confirmation_but_ages_from_later_replay_acceptance_for_favorite()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset confirmedAt = PostgresNow().AddDays(-2);
        DateTimeOffset replayAcceptedAt = confirmedAt.AddDays(1);
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid();
        Guid supplierAssetId = Guid.NewGuid(), replayAssetId = Guid.NewGuid(), decisionId = Guid.NewGuid();
        byte[] bytes = [1, 3, 3, 7, 9, 11, 13, 15];
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-replay-retention" });
        database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "synthetic-replay-destination" });
        database.Captures.Add(Capture(captureId, sourceId, "replay", confirmedAt.AddDays(-1), favorite: true));
        database.Assets.AddRange(
            Asset(supplierAssetId, sourceId, "original.raw", "RAW", confirmedAt.AddDays(-1), hash, bytes.Length),
            Asset(replayAssetId, sourceId, "replay.raw", "RAW", replayAcceptedAt, hash, bytes.Length));
        database.CaptureAssets.AddRange(
            Member(supplierAssetId, captureId, "RAW"),
            new CaptureAssetEntity
            {
                AssetId = replayAssetId, CaptureId = captureId, Role = "RAW", IsPrimary = false,
                RelatedAssetId = supplierAssetId, GroupingReason = "recognized_replay",
            });
        database.RoutingDecisions.Add(new RoutingDecisionEntity
        {
            Id = decisionId, CaptureId = captureId, AssetId = supplierAssetId, DestinationId = destinationId,
            Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
            RuleId = "confirmed-original", Required = true, TargetPath = "archive/original.raw",
            TargetPathKey = "ARCHIVE/ORIGINAL.RAW", Explanation = "Synthetic confirmed logical variant",
            CreatedAt = confirmedAt.AddMinutes(-1),
        });
        database.Deliveries.Add(new DeliveryEntity
        {
            Id = Guid.NewGuid(), DecisionId = decisionId, AssetId = supplierAssetId, DestinationId = destinationId,
            TargetPath = "archive/original.raw", Required = true, State = DeliveryState.Delivered,
            ConfirmedAt = confirmedAt, ConfirmationEvidence = "synthetic-acknowledged-write",
            CreatedAt = confirmedAt.AddMinutes(-1),
        });
        await database.SaveChangesAsync(token);

        CaptureRetentionSummary summary = Assert.IsType<CaptureRetentionSummary>(await new EfCaptureRetentionStore(
            database, TimeProvider.System).GetCaptureSummaryAsync(captureId, PostgresNow(), token));

        Assert.Equal(RetentionTier.ConfirmedFavorite, summary.Tier);
        Assert.Equal(replayAcceptedAt, summary.Timeline?.StartedAt);
        Assert.Equal(replayAcceptedAt.AddHours(1), summary.PressureEligibleAt);
        Assert.Null(summary.TrashEligibleAt);
    }

    [Fact]
    public async Task Same_target_record_only_reevaluation_keeps_existing_confirmation_for_favorite()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset acceptedAt = PostgresNow().AddDays(-20);
        DateTimeOffset confirmedAt = acceptedAt.AddDays(1);
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid(), decisionId = Guid.NewGuid();
        RoutingRule[] rules = [new("same-target", "Same target", 0, true, [], "archive", "{suppliedName}")];
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-same-target" });
        database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "synthetic-same-target" });
        await database.SaveChangesAsync(token);
        await new DestinationRoutingAdminService(database)
            .SetAsync(new DestinationRouting(destinationId, true, rules), token);
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "SAME-TARGET",
            FirstAcceptedAt = acceptedAt, LastPrimaryIngestedAt = acceptedAt, IsFavorite = true,
            RetentionPolicyRevision = 1, State = CaptureState.RELEASED, ReleasedAt = acceptedAt.AddHours(1),
            RoutingSnapshotJson = RoutingRuleJson.WriteSnapshot([new DestinationRouting(destinationId, true, rules)]),
        });
        database.Assets.Add(Asset(assetId, sourceId, "same-target.raw", "RAW", acceptedAt));
        database.CaptureAssets.Add(Member(assetId, captureId, "RAW"));
        database.RoutingDecisions.Add(new RoutingDecisionEntity
        {
            Id = decisionId, CaptureId = captureId, AssetId = assetId, DestinationId = destinationId,
            Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
            RuleId = "prior-route", Required = true, TargetPath = "archive/same-target.raw",
            TargetPathKey = "ARCHIVE/SAME-TARGET.RAW", Explanation = "Synthetic prior target", CreatedAt = confirmedAt.AddMinutes(-1),
        });
        database.Deliveries.Add(new DeliveryEntity
        {
            Id = Guid.NewGuid(), DecisionId = decisionId, AssetId = assetId, DestinationId = destinationId,
            TargetPath = "archive/same-target.raw", Required = true, State = DeliveryState.Delivered,
            ConfirmedAt = confirmedAt, ConfirmationEvidence = "synthetic-acknowledged-write", CreatedAt = confirmedAt.AddMinutes(-1),
        });
        await database.SaveChangesAsync(token);

        var routing = new CaptureRoutingWorkService(database, TimeProvider.System);
        RoutingActionPreview preview = await routing.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal("archive/same-target.raw", preview.ProposedTarget);
        Assert.Equal(RoutingActionStatus.Recorded, (await routing.ReevaluateAsync(assetId, destinationId,
            preview.ConfirmationToken, verifiedSupplierId: null, cancellationToken: token)).Status);

        Assert.Equal(1, await database.Deliveries.CountAsync(item => item.AssetId == assetId, token));
        Assert.Null(await database.Captures.AsNoTracking().Where(item => item.Id == captureId)
            .Select(item => item.FavoriteQualificationLostAt).SingleAsync(token));
        CaptureRetentionSummary summary = Assert.IsType<CaptureRetentionSummary>(await new EfCaptureRetentionStore(
            database, TimeProvider.System).GetCaptureSummaryAsync(captureId, PostgresNow(), token));
        Assert.Equal(RetentionTier.ConfirmedFavorite, summary.Tier);
    }

    [Fact]
    public async Task New_resend_does_not_restart_clock_for_favorite_already_incomplete_on_another_intent()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset acceptedAt = PostgresNow().AddDays(-250);
        DateTimeOffset confirmedAt = acceptedAt.AddDays(10);
        Guid sourceId = Guid.NewGuid(), deliveredDestinationId = Guid.NewGuid(), invalidDestinationId = Guid.NewGuid();
        Guid captureId = Guid.NewGuid(), assetId = Guid.NewGuid(), deliveredDecisionId = Guid.NewGuid();
        Guid deliveredId = Guid.NewGuid();
        byte[] bytes = [2, 3, 5, 7, 11, 13, 17, 19];
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-incomplete-favorite" });
        database.Destinations.AddRange(
            new DestinationEntity { Id = deliveredDestinationId, Name = "synthetic-confirmed" },
            new DestinationEntity { Id = invalidDestinationId, Name = "synthetic-unresolved" });
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "INCOMPLETE-FAVORITE",
            FirstAcceptedAt = acceptedAt, LastPrimaryIngestedAt = acceptedAt, IsFavorite = true,
            RetentionPolicyRevision = 1, State = CaptureState.RELEASED, ReleasedAt = acceptedAt.AddHours(1),
        });
        database.Assets.Add(Asset(assetId, sourceId, "incomplete-favorite.raw", "RAW", acceptedAt, hash, bytes.Length));
        database.CaptureAssets.Add(Member(assetId, captureId, "RAW"));
        database.RoutingDecisions.AddRange(
            new RoutingDecisionEntity
            {
                Id = deliveredDecisionId, CaptureId = captureId, AssetId = assetId,
                DestinationId = deliveredDestinationId, Generation = 0, IsCurrent = true,
                Kind = RoutingDecisionKindValue.Matched, RuleId = "confirmed-route", Required = true,
                TargetPath = "archive/incomplete-favorite.raw", TargetPathKey = "ARCHIVE/INCOMPLETE-FAVORITE.RAW",
                Explanation = "Synthetic positive route", CreatedAt = confirmedAt.AddMinutes(-1),
            },
            new RoutingDecisionEntity
            {
                Id = Guid.NewGuid(), CaptureId = captureId, AssetId = assetId,
                DestinationId = invalidDestinationId, Generation = 0, IsCurrent = true,
                Kind = RoutingDecisionKindValue.InvalidTarget, RuleId = "invalid-route", Required = true,
                TargetPath = "../unsafe", Explanation = "Synthetic unresolved technical route", CreatedAt = confirmedAt,
            });
        database.Deliveries.Add(new DeliveryEntity
        {
            Id = deliveredId, DecisionId = deliveredDecisionId, AssetId = assetId,
            DestinationId = deliveredDestinationId, TargetPath = "archive/incomplete-favorite.raw", Required = true,
            State = DeliveryState.Delivered, ConfirmedAt = confirmedAt,
            ConfirmationEvidence = "synthetic-acknowledged-write", CreatedAt = confirmedAt.AddMinutes(-1),
        });
        await database.SaveChangesAsync(token);

        var routing = new CaptureRoutingWorkService(database, TimeProvider.System);
        RoutingActionPreview preview = await routing.PreviewResendAsync(deliveredId, token);
        RoutingActionPreview resent = await routing.ResendAsync(deliveredId, preview.ConfirmationToken, assetId, token);

        Assert.Equal(RoutingActionStatus.Queued, resent.Status);
        Assert.Null(await database.Captures.AsNoTracking().Where(item => item.Id == captureId)
            .Select(item => item.FavoriteQualificationLostAt).SingleAsync(token));
        CaptureRetentionSummary summary = Assert.IsType<CaptureRetentionSummary>(await new EfCaptureRetentionStore(
            database, TimeProvider.System).GetCaptureSummaryAsync(captureId, PostgresNow(), token));
        Assert.Equal(RetentionTier.HeldOrUnfinished, summary.Tier);
        Assert.Equal(acceptedAt.AddDays(14), summary.PressureEligibleAt);
        Assert.Equal(acceptedAt.AddDays(180), summary.TrashEligibleAt);
    }

    [Fact]
    public async Task Delivered_legacy_row_without_confirmation_time_cannot_qualify_or_restart_favorite_clock()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset acceptedAt = PostgresNow().AddDays(-250);
        DateTimeOffset confirmedAt = acceptedAt.AddDays(10);
        Guid sourceId = Guid.NewGuid(), firstDestination = Guid.NewGuid(), legacyDestination = Guid.NewGuid();
        Guid captureId = Guid.NewGuid(), assetId = Guid.NewGuid(), firstDecisionId = Guid.NewGuid(), legacyDecisionId = Guid.NewGuid();
        Guid firstDeliveryId = Guid.NewGuid();
        byte[] bytes = [1, 4, 9, 16, 25, 36, 49, 64];
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-legacy-confirmation" });
        database.Destinations.AddRange(
            new DestinationEntity { Id = firstDestination, Name = "synthetic-confirmed-route" },
            new DestinationEntity { Id = legacyDestination, Name = "synthetic-legacy-route" });
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "LEGACY-CONFIRMATION",
            FirstAcceptedAt = acceptedAt, LastPrimaryIngestedAt = acceptedAt, IsFavorite = true,
            RetentionPolicyRevision = 1, State = CaptureState.RELEASED, ReleasedAt = acceptedAt.AddHours(1),
        });
        database.Assets.Add(Asset(assetId, sourceId, "legacy-confirmation.raw", "RAW", acceptedAt, hash, bytes.Length));
        database.CaptureAssets.Add(Member(assetId, captureId, "RAW"));
        database.RoutingDecisions.AddRange(
            new RoutingDecisionEntity
            {
                Id = firstDecisionId, CaptureId = captureId, AssetId = assetId, DestinationId = firstDestination,
                Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
                RuleId = "confirmed-route", Required = true, TargetPath = "archive/legacy-confirmation.raw",
                TargetPathKey = "ARCHIVE/LEGACY-CONFIRMATION.RAW", Explanation = "Synthetic confirmed route", CreatedAt = confirmedAt,
            },
            new RoutingDecisionEntity
            {
                Id = legacyDecisionId, CaptureId = captureId, AssetId = assetId, DestinationId = legacyDestination,
                Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
                RuleId = "legacy-route", Required = true, TargetPath = "legacy/legacy-confirmation.raw",
                TargetPathKey = "LEGACY/LEGACY-CONFIRMATION.RAW", Explanation = "Legacy delivery without confirmation time", CreatedAt = confirmedAt,
            });
        database.Deliveries.AddRange(
            new DeliveryEntity
            {
                Id = firstDeliveryId, DecisionId = firstDecisionId, AssetId = assetId, DestinationId = firstDestination,
                TargetPath = "archive/legacy-confirmation.raw", Required = true, State = DeliveryState.Delivered,
                ConfirmedAt = confirmedAt, ConfirmationEvidence = "synthetic-acknowledged-write", CreatedAt = confirmedAt,
            },
            new DeliveryEntity
            {
                Id = Guid.NewGuid(), DecisionId = legacyDecisionId, AssetId = assetId, DestinationId = legacyDestination,
                TargetPath = "legacy/legacy-confirmation.raw", Required = true, State = DeliveryState.Delivered,
                ConfirmedAt = null, ConfirmationEvidence = null, CreatedAt = confirmedAt,
            });
        await database.SaveChangesAsync(token);

        var retention = new EfCaptureRetentionStore(database, TimeProvider.System);
        CaptureRetentionSummary before = Assert.IsType<CaptureRetentionSummary>(await retention.GetCaptureSummaryAsync(
            captureId, PostgresNow(), token));
        Assert.Equal(RetentionTier.HeldOrUnfinished, before.Tier);
        Assert.Equal(acceptedAt.AddDays(14), before.PressureEligibleAt);
        Assert.Equal(acceptedAt.AddDays(180), before.TrashEligibleAt);

        var routing = new CaptureRoutingWorkService(database, TimeProvider.System);
        RoutingActionPreview preview = await routing.PreviewResendAsync(firstDeliveryId, token);
        Assert.Equal(RoutingActionStatus.Queued, (await routing.ResendAsync(firstDeliveryId,
            preview.ConfirmationToken, assetId, token)).Status);
        Assert.Null(await database.Captures.AsNoTracking().Where(item => item.Id == captureId)
            .Select(item => item.FavoriteQualificationLostAt).SingleAsync(token));
    }

    [Fact]
    public async Task Reevaluation_to_no_match_does_not_clear_superseded_or_invalid_intent_history()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset acceptedAt = PostgresNow().AddDays(-250);
        DateTimeOffset lostAt = acceptedAt.AddDays(40);
        DateTimeOffset confirmedAt = acceptedAt.AddDays(10);
        Guid sourceId = Guid.NewGuid(), deliveredDestination = Guid.NewGuid(), failedDestination = Guid.NewGuid();
        Guid invalidDestination = Guid.NewGuid(), captureId = Guid.NewGuid(), assetId = Guid.NewGuid();
        Guid deliveredDecision = Guid.NewGuid(), failedDecision = Guid.NewGuid(), invalidDecision = Guid.NewGuid();
        Guid deliveredId = Guid.NewGuid(), failedId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-retained-intents" });
        database.Destinations.AddRange(
            new DestinationEntity { Id = deliveredDestination, Name = "synthetic-delivered" },
            new DestinationEntity { Id = failedDestination, Name = "synthetic-failed" },
            new DestinationEntity { Id = invalidDestination, Name = "synthetic-invalid" });
        await database.SaveChangesAsync(token);
        var routingAdmin = new DestinationRoutingAdminService(database);
        await routingAdmin.SetAsync(new DestinationRouting(failedDestination, true, []), token);
        await routingAdmin.SetAsync(new DestinationRouting(invalidDestination, true, []), token);
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "RETAINED-INTENTS",
            FirstAcceptedAt = acceptedAt, LastPrimaryIngestedAt = acceptedAt, IsFavorite = true,
            FavoriteQualificationLostAt = lostAt, RetentionPolicyRevision = 1,
            State = CaptureState.RELEASED, ReleasedAt = acceptedAt.AddHours(1),
            RoutingSnapshotJson = RoutingRuleJson.WriteSnapshot([
                new DestinationRouting(deliveredDestination, true, []),
                new DestinationRouting(failedDestination, true, []),
                new DestinationRouting(invalidDestination, true, []),
            ]),
        });
        database.Assets.Add(Asset(assetId, sourceId, "retained-intents.raw", "RAW", acceptedAt));
        database.CaptureAssets.Add(new CaptureAssetEntity
        {
            AssetId = assetId, CaptureId = captureId, Role = "RAW", IsPrimary = true,
            GroupingReason = "synthetic-primary", FavoriteQualificationLostAt = lostAt,
        });
        database.RoutingDecisions.AddRange(
            new RoutingDecisionEntity
            {
                Id = deliveredDecision, CaptureId = captureId, AssetId = assetId, DestinationId = deliveredDestination,
                Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
                RuleId = "confirmed-route", Required = true, TargetPath = "archive/retained-intents.raw",
                TargetPathKey = "ARCHIVE/RETAINED-INTENTS.RAW", Explanation = "Synthetic confirmed route", CreatedAt = confirmedAt,
            },
            new RoutingDecisionEntity
            {
                Id = failedDecision, CaptureId = captureId, AssetId = assetId, DestinationId = failedDestination,
                Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
                RuleId = "failed-route", Required = false, TargetPath = "failed/retained-intents.raw",
                TargetPathKey = "FAILED/RETAINED-INTENTS.RAW", Explanation = "Synthetic failed route", CreatedAt = confirmedAt,
            },
            new RoutingDecisionEntity
            {
                Id = invalidDecision, CaptureId = captureId, AssetId = assetId, DestinationId = invalidDestination,
                Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.InvalidTarget,
                RuleId = "invalid-route", Required = true, TargetPath = "../unsafe",
                Explanation = "Synthetic unresolved technical route", CreatedAt = confirmedAt,
            });
        database.Deliveries.AddRange(
            new DeliveryEntity
            {
                Id = deliveredId, DecisionId = deliveredDecision, AssetId = assetId, DestinationId = deliveredDestination,
                TargetPath = "archive/retained-intents.raw", Required = true, State = DeliveryState.Delivered,
                ConfirmedAt = confirmedAt, ConfirmationEvidence = "synthetic-acknowledged-write", CreatedAt = confirmedAt,
            },
            new DeliveryEntity
            {
                Id = failedId, DecisionId = failedDecision, AssetId = assetId, DestinationId = failedDestination,
                TargetPath = "failed/retained-intents.raw", Required = false, State = DeliveryState.Failed,
                RetryReason = "Synthetic unresolved outcome", CreatedAt = confirmedAt,
            });
        await database.SaveChangesAsync(token);

        var routing = new CaptureRoutingWorkService(database, TimeProvider.System);
        foreach (Guid destinationId in new[] { failedDestination, invalidDestination })
        {
            RoutingActionPreview preview = await routing.PreviewReevaluateAsync(assetId, destinationId, token);
            Assert.Equal(RoutingActionStatus.ConfirmationRequired, preview.Status);
            Assert.Equal(RoutingActionStatus.Recorded, (await routing.ReevaluateAsync(assetId, destinationId,
                preview.ConfirmationToken, verifiedSupplierId: null, cancellationToken: token)).Status);
        }

        Assert.Equal(DeliveryState.Superseded, await database.Deliveries.AsNoTracking()
            .Where(item => item.Id == failedId).Select(item => item.State).SingleAsync(token));
        Assert.Equal(2, await database.RoutingDecisions.CountAsync(item => item.CaptureId == captureId
            && item.Kind == RoutingDecisionKindValue.NoMatch && item.IsCurrent, token));
        CaptureRetentionSummary summary = Assert.IsType<CaptureRetentionSummary>(await new EfCaptureRetentionStore(
            database, TimeProvider.System).GetCaptureSummaryAsync(captureId, PostgresNow(), token));
        Assert.Equal(RetentionTier.HeldOrUnfinished, summary.Tier);
        Assert.Equal(lostAt.AddDays(14), summary.PressureEligibleAt);
        Assert.Equal(lostAt.AddDays(180), summary.TrashEligibleAt);
        Assert.False(summary.CanRecordNoDeliveryIntent);
    }

    [Fact]
    public async Task Invalid_reevaluation_restarts_a_previously_confirmed_favorite_clock()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset acceptedAt = PostgresNow().AddDays(-250);
        DateTimeOffset confirmedAt = acceptedAt.AddDays(10);
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid(), decisionId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-invalid-reevaluation" });
        database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "synthetic-invalid-reevaluation" });
        RoutingRule[] rules = [InvalidRule("unsafe-reevaluation", "RAW")];
        await database.SaveChangesAsync(token);
        await new DestinationRoutingAdminService(database)
            .SetAsync(new DestinationRouting(destinationId, true, rules), token);
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "OLD-FAVORITE",
            FirstAcceptedAt = acceptedAt, LastPrimaryIngestedAt = acceptedAt, IsFavorite = true,
            RetentionPolicyRevision = 1, State = CaptureState.RELEASED, ReleasedAt = acceptedAt.AddDays(1),
            RoutingSnapshotJson = RoutingRuleJson.WriteSnapshot([new DestinationRouting(destinationId, true, rules)]),
        });
        database.Assets.Add(Asset(assetId, sourceId, "old-favorite.raw", "RAW", acceptedAt));
        database.CaptureAssets.Add(Member(assetId, captureId, "RAW"));
        database.RoutingDecisions.Add(new RoutingDecisionEntity
        {
            Id = decisionId, CaptureId = captureId, AssetId = assetId, DestinationId = destinationId,
            Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
            RuleId = "previous-safe-rule", Required = true, TargetPath = "archive/old-favorite.raw",
            TargetPathKey = "ARCHIVE/OLD-FAVORITE.RAW", Explanation = "Synthetic previously confirmed route",
            CreatedAt = confirmedAt.AddMinutes(-1),
        });
        database.Deliveries.Add(new DeliveryEntity
        {
            Id = Guid.NewGuid(), DecisionId = decisionId, AssetId = assetId, DestinationId = destinationId,
            TargetPath = "archive/old-favorite.raw", Required = true, State = DeliveryState.Delivered,
            ConfirmedAt = confirmedAt, ConfirmationEvidence = "synthetic-acknowledged-write",
            CreatedAt = confirmedAt.AddMinutes(-1),
        });
        await database.SaveChangesAsync(token);

        var store = new CaptureRoutingWorkService(database, TimeProvider.System);
        RoutingActionPreview preview = await store.PreviewReevaluateAsync(assetId, destinationId, token);
        Assert.Equal(RoutingActionStatus.ConfirmationRequired, preview.Status);
        Assert.Equal(RoutingActionStatus.Recorded, (await store.ReevaluateAsync(assetId, destinationId,
            preview.ConfirmationToken, verifiedSupplierId: null, cancellationToken: token)).Status);

        DateTimeOffset lostAt = Assert.IsType<DateTimeOffset>(await database.Captures.AsNoTracking()
            .Where(item => item.Id == captureId).Select(item => item.FavoriteQualificationLostAt).SingleAsync(token));
        CaptureRetentionSummary summary = Assert.IsType<CaptureRetentionSummary>(await new EfCaptureRetentionStore(
            database, TimeProvider.System).GetCaptureSummaryAsync(captureId, PostgresNow(), token));
        Assert.Equal(RetentionTier.HeldOrUnfinished, summary.Tier);
        Assert.Equal(lostAt.AddDays(14), summary.PressureEligibleAt);
        Assert.Equal(lostAt.AddDays(180), summary.TrashEligibleAt);
        Assert.True(lostAt > acceptedAt.AddDays(180));
    }

    [Fact]
    public async Task Resend_rechecks_durable_removal_committed_after_supplier_verification()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.MigrateAsync(token);

        DateTimeOffset now = PostgresNow();
        byte[] bytes = [7, 5, 3, 1, 2, 4, 6];
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid(), decisionId = Guid.NewGuid(), deliveryId = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-routing-fence" });
        database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "synthetic-routing-fence" });
        database.Captures.Add(new CaptureEntity
        {
            Id = captureId, SourceId = sourceId, NormalizedStem = "FENCED",
            FirstAcceptedAt = now.AddDays(-1), LastPrimaryIngestedAt = now.AddDays(-1),
            State = CaptureState.RELEASED, ReleasedAt = now.AddHours(-12),
            RetentionDisposition = "Active", RetentionPolicyRevision = 1,
        });
        database.Assets.Add(Asset(assetId, sourceId, "fenced.raw", "RAW", now.AddDays(-1), hash, bytes.Length));
        database.CaptureAssets.Add(Member(assetId, captureId, "RAW"));
        database.RoutingDecisions.Add(new RoutingDecisionEntity
        {
            Id = decisionId, CaptureId = captureId, AssetId = assetId, DestinationId = destinationId,
            Generation = 0, IsCurrent = true, Kind = RoutingDecisionKindValue.Matched,
            RuleId = "existing-route", Required = true, TargetPath = "archive/fenced.raw",
            TargetPathKey = "ARCHIVE/FENCED.RAW", Explanation = "Synthetic delivered route", CreatedAt = now.AddHours(-13),
        });
        database.Deliveries.Add(new DeliveryEntity
        {
            Id = deliveryId, DecisionId = decisionId, AssetId = assetId, DestinationId = destinationId,
            TargetPath = "archive/fenced.raw", Required = true, State = DeliveryState.Delivered,
            ConfirmedAt = now.AddHours(-12), ConfirmationEvidence = "synthetic-acknowledged-write",
            CreatedAt = now.AddHours(-13),
        });
        SpoolQuotaObservationEntity marker = await database.SpoolQuotaObservations.SingleAsync(item => item.Id == 1, token);
        marker.StorageMarker = "synthetic-marker";
        await database.SaveChangesAsync(token);

        var innerSupplier = new EfVerifiedOriginalSupplier(database, new MemoryOriginalReader(bytes));
        var supplier = new BarrierOriginalSupplier(innerSupplier);
        var actions = new CaptureRoutingActionService(new CaptureRoutingWorkService(database, TimeProvider.System), supplier);
        RoutingActionPreview preview = await actions.PreviewResendAsync(deliveryId, token);
        Assert.Equal(RoutingActionStatus.ConfirmationRequired, preview.Status);
        Task<RoutingActionPreview> resendTask = actions.ResendAsync(deliveryId, preview.ConfirmationToken, token);
        Assert.Equal(assetId, await supplier.Verified.Task.WaitAsync(token));

        await using (var authority = new ShutterSpoolDbContext(fixture.Options))
        {
            CaptureEntity beforeTrash = await authority.Captures.SingleAsync(item => item.Id == captureId, token);
            var retention = new EfCaptureRetentionStore(authority, TimeProvider.System);
            RetentionOperationResult trashed = await retention.TrashAsync(captureId, beforeTrash.Revision,
                "manual", PostgresNow(), token);
            Assert.Equal(RetentionOperationStatus.Committed, trashed.Status);
            var removals = new EfRetentionRemovalStore(authority, retention, TimeProvider.System);
            RetentionRemovalAuthorization authorized = await removals.AuthorizeCaptureAsync(captureId,
                trashed.Current!.CaptureRevision, RetentionRemovalReason.ManualPermanentDelete,
                PostgresNow(), token);
            Assert.Equal(RetentionOperationStatus.Pending, authorized.Result.Status);
            Assert.Single(authorized.Work);
        }

        supplier.AllowAction.TrySetResult();
        RoutingActionPreview result = await resendTask;

        Assert.Equal(RoutingActionStatus.BlockedState, result.Status);
        Assert.Equal("Purging", await database.Captures.AsNoTracking().Where(item => item.Id == captureId)
            .Select(item => item.RetentionDisposition).SingleAsync(token));
        Assert.Equal(1, await database.Deliveries.CountAsync(item => item.AssetId == assetId, token));
        Assert.Equal(0, await database.RoutingActions.CountAsync(item => item.Kind == "Resend" && item.AssetId == assetId, token));
    }

    private static CaptureEntity Capture(Guid id, Guid sourceId, string stem, DateTimeOffset acceptedAt, bool favorite = false) => new()
    {
        Id = id, SourceId = sourceId, NormalizedStem = stem, FirstAcceptedAt = acceptedAt,
        LastPrimaryIngestedAt = acceptedAt, IsFavorite = favorite, RetentionPolicyRevision = 1,
    };

    private static AssetEntity Asset(Guid id, Guid sourceId, string name, string role, DateTimeOffset acceptedAt,
        string? sha256 = null, int size = 17) => new()
    {
        Id = id, SourceId = sourceId, OriginalName = name, SpoolKey = $"spool/{id:N}", Size = size,
        Sha256 = sha256 ?? new string('a', 64), IngestedAt = acceptedAt, RecoveryCorrelation = Guid.NewGuid(),
        State = "Spooled", MetadataState = "Complete", MetadataRole = role, RetentionPolicyRevision = 1,
    };

    private static CaptureAssetEntity Member(Guid assetId, Guid captureId, string role) => new()
    {
        AssetId = assetId, CaptureId = captureId, Role = role, IsPrimary = true, GroupingReason = "synthetic-primary",
    };

    private static RoutingRule InvalidRule(string id, string variant) => new(id, id, 0, true,
        [new("variant", "equals", new RoutingValue(Text: variant))], "../unsafe", "{suppliedName}");

    private static DateTimeOffset PostgresNow() => new(DateTime.UtcNow.Ticks / TimeSpan.TicksPerMicrosecond * TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    private sealed class MemoryOriginalReader(byte[] bytes) : ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class BarrierOriginalSupplier(IVerifiedOriginalSupplier inner) : IVerifiedOriginalSupplier
    {
        public TaskCompletionSource<Guid?> Verified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowAction { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Guid?> FindAsync(Guid assetId, Guid? preferredSupplierId, CancellationToken cancellationToken)
        {
            Guid? verified = await inner.FindAsync(assetId, preferredSupplierId, cancellationToken);
            Verified.TrySetResult(verified);
            await AllowAction.Task.WaitAsync(cancellationToken);
            return verified;
        }
    }
}
