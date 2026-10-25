using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCaptureRoutingProcessor(ShutterSpoolDbContext database) : ICaptureRoutingStore
{
    public Guid? LastChangedCaptureId { get; private set; }

    public async Task<bool> ProcessOneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        LastChangedCaptureId = null;
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(database, cancellationToken);
        CaptureEntity? capture = await ClaimNextCaptureAsync(database, cancellationToken);
        if (capture is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        CaptureAssetEntity[] members = await LoadUnroutedVariantsAsync(database, capture.Id, cancellationToken);
        Dictionary<Guid, AssetEntity> assets = await LoadAssetsAsync(database, members, cancellationToken);
        DestinationRouting[] snapshot = RoutingRuleJson.ReadSnapshot(capture.RoutingSnapshotJson!);
        List<EvaluatedRoute> decisions = EvaluateRoutes(capture, members, assets, snapshot);

        await RejectCollisionsAsync(database, decisions, cancellationToken);
        await StageDecisionsAndWorkAsync(database, capture, members, decisions, now, cancellationToken);
        await UpdateCaptureStateAsync(database, capture, decisions, cancellationToken);

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LastChangedCaptureId = capture.Id;
        return true;
    }

    private static Task<int> LockRoutingAsync(ShutterSpoolDbContext database, CancellationToken cancellationToken)
    {
        // ponytail: one routing lock fences destination path claims across Captures; split by destination if throughput grows.
        return database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
    }

    private static Task<CaptureEntity?> ClaimNextCaptureAsync(ShutterSpoolDbContext database, CancellationToken cancellationToken) =>
        database.Captures.FromSqlInterpolated($"""
            SELECT * FROM captures capture
            WHERE capture."ReleasedAt" IS NOT NULL AND capture."RoutingSnapshotJson" IS NOT NULL
              AND capture."RetentionDisposition" = 'Active' AND NOT capture."RetentionPaused"
              AND NOT EXISTS (
                  SELECT 1 FROM capture_assets member
                  JOIN privacy_erasure_assets erased ON erased."AssetId" = member."AssetId"
                  WHERE member."CaptureId" = capture."Id")
              AND (capture."State" = 'READY_FOR_RELEASE' OR EXISTS (
                  SELECT 1 FROM capture_assets member
                  WHERE member."CaptureId" = capture."Id" AND member."IsPrimary"
                    AND NOT EXISTS (SELECT 1 FROM routed_assets routed WHERE routed."AssetId" = member."AssetId")))
            ORDER BY capture."ReleasedAt", capture."Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).FirstOrDefaultAsync(cancellationToken);

    // A recognized replay is a non-primary member; it needs an explicit Resend, not automatic routing.
    private static Task<CaptureAssetEntity[]> LoadUnroutedVariantsAsync(ShutterSpoolDbContext database, Guid captureId, CancellationToken cancellationToken) =>
        database.CaptureAssets.AsNoTracking()
            .Where(member => member.CaptureId == captureId && member.IsPrimary)
            .Where(member => !database.RoutedAssets.Any(routed => routed.AssetId == member.AssetId))
            .OrderBy(member => member.AssetId).ToArrayAsync(cancellationToken);

    private static async Task<Dictionary<Guid, AssetEntity>> LoadAssetsAsync(ShutterSpoolDbContext database, CaptureAssetEntity[] members, CancellationToken cancellationToken)
    {
        Guid[] ids = [.. members.Select(member => member.AssetId)];
        if (ids.Length == 0) return [];
        return await database.Assets.AsNoTracking().Where(asset => ids.Contains(asset.Id))
            .ToDictionaryAsync(asset => asset.Id, cancellationToken);
    }

    private static List<EvaluatedRoute> EvaluateRoutes(CaptureEntity capture, CaptureAssetEntity[] members,
        IReadOnlyDictionary<Guid, AssetEntity> assets, DestinationRouting[] snapshot)
    {
        List<EvaluatedRoute> decisions = [];
        foreach (CaptureAssetEntity member in members)
        {
            RoutingFacts facts = Facts(capture, assets[member.AssetId], member.Role);
            foreach (DestinationRouting destination in snapshot)
            {
                if (!destination.Enabled)
                {
                    decisions.Add(new(member.AssetId, destination,
                        new(RoutingDecisionKind.DestinationDisabled, null, false, null, "Destination was disabled at Capture release."), null));
                    continue;
                }
                RoutingDecision decision = RoutingRuleEvaluator.Evaluate(destination.Rules, facts);
                decisions.Add(new(member.AssetId, destination, decision,
                    destination.Rules.FirstOrDefault(rule => rule.Id == decision.RuleId)));
            }
        }
        return decisions;
    }

    private static async Task RejectCollisionsAsync(ShutterSpoolDbContext database, List<EvaluatedRoute> decisions, CancellationToken cancellationToken)
    {
        var candidates = decisions.Select((item, index) => (item, index))
            .Where(pair => pair.item.Decision.Kind == RoutingDecisionKind.Matched)
            .Select(pair => (pair.index, Claim: new RoutingPathClaim(pair.item.AssetId,
                pair.item.Destination.DestinationId, pair.item.Decision.TargetPath!))).ToArray();
        if (candidates.Length == 0) return;
        Guid[] destinationIds = [.. candidates.Select(pair => pair.Claim.DestinationId).Distinct()];
        string[] targetKeys = [.. candidates.Select(pair => pair.Claim.TargetPath.ToUpperInvariant()).Distinct()];
        var taken = await database.RoutingDecisions.AsNoTracking()
            .Where(decision => decision.IsCurrent && destinationIds.Contains(decision.DestinationId)
                && decision.Kind == RoutingDecisionKindValue.Matched && targetKeys.Contains(decision.TargetPathKey!))
            .Select(decision => new { decision.AssetId, decision.DestinationId, decision.TargetPath }).ToListAsync(cancellationToken);
        var successfulTargets = await database.Deliveries.AsNoTracking()
            .Where(delivery => delivery.State == DeliveryState.Delivered && destinationIds.Contains(delivery.DestinationId))
            .Where(delivery => database.RoutingDecisions.Any(decision => decision.Id == delivery.DecisionId
                && targetKeys.Contains(decision.TargetPathKey!)))
            .Select(delivery => new { delivery.AssetId, delivery.DestinationId, TargetPath = (string?)delivery.TargetPath })
            .ToArrayAsync(cancellationToken);
        taken.AddRange(successfulTargets);
        var conflicts = RoutingCollisionPolicy.FindConflicts(candidates.Select(pair => pair.Claim).ToArray(),
            taken.Select(item => new RoutingPathClaim(item.AssetId, item.DestinationId, item.TargetPath!)).ToArray());
        foreach ((int position, string explanation) in conflicts)
        {
            int decisionIndex = candidates[position].index;
            decisions[decisionIndex] = decisions[decisionIndex] with
            { Decision = new RoutingDecision(RoutingDecisionKind.InvalidTarget, decisions[decisionIndex].Decision.RuleId,
                decisions[decisionIndex].Decision.Required, decisions[decisionIndex].Decision.TargetPath, explanation) };
        }
    }

    private static async Task StageDecisionsAndWorkAsync(ShutterSpoolDbContext database, CaptureEntity capture, CaptureAssetEntity[] members,
        IReadOnlyList<EvaluatedRoute> decisions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Dictionary<Guid, bool> enabledNow = await database.Destinations.AsNoTracking()
            .ToDictionaryAsync(item => item.Id, item => item.Enabled, cancellationToken);
        if (decisions.Any(item => item.Decision.Kind == RoutingDecisionKind.Matched
            || item.Decision.RuleId is not null
                && item.Decision.Kind is (RoutingDecisionKind.InvalidTarget or RoutingDecisionKind.DestinationDisabled)))
            await RetentionQualification.RecordNewIntentAsync(database, capture, now, cancellationToken);
        foreach (var evaluated in decisions)
        {
            RoutingRule? rule = evaluated.Rule;
            var decision = new RoutingDecisionEntity
            {
                Id = Guid.NewGuid(), CaptureId = capture.Id, AssetId = evaluated.AssetId,
                DestinationId = evaluated.Destination.DestinationId, Generation = 0, IsCurrent = true,
                Kind = evaluated.Decision.Kind switch
                {
                    RoutingDecisionKind.Matched => RoutingDecisionKindValue.Matched,
                    RoutingDecisionKind.InvalidTarget => RoutingDecisionKindValue.InvalidTarget,
                    RoutingDecisionKind.DestinationDisabled => RoutingDecisionKindValue.DestinationDisabled,
                    _ => RoutingDecisionKindValue.NoMatch,
                },
                RuleId = evaluated.Decision.RuleId, RuleName = rule?.Name, Required = evaluated.Decision.Required,
                TargetPath = evaluated.Decision.TargetPath, Explanation = evaluated.Decision.Explanation,
                TargetPathKey = evaluated.Decision.Kind == RoutingDecisionKind.Matched ? evaluated.Decision.TargetPath!.ToUpperInvariant() : null,
                RuleSnapshotJson = rule is null ? null : RoutingRuleJson.WriteRule(rule), CreatedAt = now,
                CollectionId = capture.ReleasedCollectionId, CollectionName = capture.ReleasedCollectionName,
                CollectionPath = capture.ReleasedCollectionPath,
            };
            database.RoutingDecisions.Add(decision);
            if (decision.Kind == RoutingDecisionKindValue.Matched)
                database.Deliveries.Add(new DeliveryEntity
                {
                    Id = Guid.NewGuid(), DecisionId = decision.Id, AssetId = decision.AssetId,
                    DestinationId = decision.DestinationId, TargetPath = decision.TargetPath!, Required = decision.Required,
                    State = enabledNow.GetValueOrDefault(decision.DestinationId, true) ? DeliveryState.Pending : DeliveryState.Disabled, CreatedAt = now,
                });
        }
        foreach (CaptureAssetEntity member in members)
            database.RoutedAssets.Add(new RoutedAssetEntity { AssetId = member.AssetId, EvaluatedAt = now });
    }

    private static async Task UpdateCaptureStateAsync(ShutterSpoolDbContext database, CaptureEntity capture,
        IReadOnlyList<EvaluatedRoute> decisions, CancellationToken cancellationToken)
    {
        bool matchedAny = decisions.Any(item => item.Decision.Kind == RoutingDecisionKind.Matched)
            || await database.RoutingDecisions.AnyAsync(item => item.CaptureId == capture.Id && item.IsCurrent
                && item.Kind == RoutingDecisionKindValue.Matched, cancellationToken);
        bool requiredPending = decisions.Any(item => item.Decision.Kind == RoutingDecisionKind.Matched && item.Decision.Required)
            || await database.Deliveries.AnyAsync(item => item.Required && item.State != DeliveryState.Delivered && item.State != DeliveryState.Superseded
                && database.RoutingDecisions.Any(route => route.Id == item.DecisionId && route.CaptureId == capture.Id), cancellationToken);
        CaptureState nextState = CaptureCompletionPolicy.State(matchedAny, requiredPending);
        if (capture.State != nextState)
        {
            capture.State = nextState;
            capture.Revision++;
        }
    }

    private sealed record EvaluatedRoute(Guid AssetId, DestinationRouting Destination, RoutingDecision Decision, RoutingRule? Rule);

    internal static RoutingFacts Facts(CaptureEntity capture, AssetEntity asset, string role, bool releasedCuration = true,
        CollectionEntity? currentCollection = null) =>
        RoutingFactBuilder.Build(new RoutingCaptureFacts(capture.SourceId,
                releasedCuration ? capture.ReleasedRating : capture.Rating,
                releasedCuration ? capture.ReleasedLabel : capture.Label,
                releasedCuration ? capture.ReleasedTags : capture.Tags,
                releasedCuration ? capture.ReleasedSelection : capture.Selection,
                releasedCuration ? capture.ReleasedCollectionName : currentCollection?.Name,
                releasedCuration ? capture.ReleasedCollectionPath : currentCollection?.Path),
            new RoutingAssetFacts(role, asset.OriginalName, asset.Size, asset.DetectedMediaType,
                asset.CameraMake, asset.CameraModel, asset.CameraSerial, asset.CaptureTimeLocal, asset.CaptureTimeUncertain));
}
