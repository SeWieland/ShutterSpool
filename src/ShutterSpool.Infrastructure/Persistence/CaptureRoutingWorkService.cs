using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Routing;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Routing;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class CaptureRoutingWorkService(ShutterSpoolDbContext database, TimeProvider clock) : ICaptureRoutingActionStore
{

    public async Task<RoutingSupplierRequest?> RetrySupplierRequestAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        DeliveryEntity? delivery = await database.Deliveries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        return delivery is not null && RoutingActionPolicy.CanRetry(delivery.State)
            ? new(delivery.AssetId, delivery.SourceAssetId) : null;
    }

    public async Task<RoutingSupplierRequest?> ResendSupplierRequestAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        DeliveryEntity? delivery = await database.Deliveries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        return delivery is not null && RoutingActionPolicy.CanResend(delivery.State) ? new(delivery.AssetId) : null;
    }

    public async Task<RoutingSupplierRequest?> ReevaluateSupplierRequestAsync(Guid assetId, Guid destinationId, CancellationToken cancellationToken)
    {
        CurrentEvaluation? evaluation = await LoadCurrentEvaluationAsync(assetId, destinationId, cancellationToken, lockCapture: false);
        if (evaluation is not { IsReleased: true } || !evaluation.Destination.Enabled) return null;
        RoutingDecision decision = RoutingRuleEvaluator.Evaluate(evaluation.Config is null ? [] : RoutingRuleJson.ReadRules(evaluation.Config.RulesJson),
            EfCaptureRoutingProcessor.Facts(evaluation.Capture, evaluation.Asset, evaluation.Role, releasedCuration: false, currentCollection: evaluation.Collection));
        database.ChangeTracker.Clear();
        return RoutingActionPolicy.NeedsOriginal(decision, evaluation.Deliveries.Select(item => (item.State, item.TargetPath)))
            ? new(assetId) : null;
    }

    public async Task<RoutingActionPreview> RetryAsync(Guid deliveryId, Guid? verifiedSupplierId, CancellationToken cancellationToken)
    {
        DeliveryEntity? before = await database.Deliveries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (before is null) return Missing(deliveryId);
        if (before.State == DeliveryState.Pending) return Result(RoutingActionStatus.AlreadyQueued, before, "Delivery is already queued.");
        if (!RoutingActionPolicy.CanRetry(before.State))
            return Result(RoutingActionStatus.BlockedState, before, "Retry is only available for unfinished work.");
        if (verifiedSupplierId is null) return Result(RoutingActionStatus.BlockedOriginalUnavailable, before, "Verified original bytes are unavailable.");

        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        DeliveryEntity? delivery = await database.Deliveries.SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (delivery is null || delivery.State == DeliveryState.Superseded) return Missing(deliveryId);
        DestinationEntity? destination = await database.Destinations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == delivery.DestinationId, cancellationToken);
        if (destination is { Enabled: false }) return Result(RoutingActionStatus.Paused, delivery, "Destination is disabled; the recorded work will resume when enabled.");
        if (delivery.State == DeliveryState.Pending) return Result(RoutingActionStatus.AlreadyQueued, delivery, "Delivery is already queued.");
        if (!RoutingActionPolicy.CanRetry(delivery.State))
            return Result(RoutingActionStatus.BlockedState, delivery, "Delivery state changed; refresh before retrying.");
        RoutingDecisionEntity retryDecision = await database.RoutingDecisions.SingleAsync(item => item.Id == delivery.DecisionId, cancellationToken);
        CaptureEntity retryCapture = await database.Captures.SingleAsync(item => item.Id == retryDecision.CaptureId, cancellationToken);
        if (!await IsCaptureWorkflowAvailableAsync(retryCapture, delivery.AssetId, cancellationToken))
            return Result(RoutingActionStatus.BlockedState, delivery, "Capture retention or erasure has stopped new routing work.");
        if (!await SupplierStillAuthoritativeAsync(delivery.AssetId, verifiedSupplierId.Value, cancellationToken))
            return Result(RoutingActionStatus.BlockedOriginalUnavailable, delivery, "Verified original bytes changed before Retry committed.");
        delivery.SourceAssetId = verifiedSupplierId == delivery.AssetId ? null : verifiedSupplierId;
        delivery.State = DeliveryState.Pending;
        delivery.NextAttemptAt = null;
        delivery.RetryReason = "Explicit Retry after operator action.";
        database.RoutingActions.Add(new RoutingActionEntity
        {
            Id = Guid.NewGuid(), Kind = "Retry", AssetId = delivery.AssetId, DestinationId = delivery.DestinationId,
            SourceDeliveryId = delivery.Id, DeliveryId = delivery.Id, DecisionId = delivery.DecisionId, SourceDecisionId = delivery.DecisionId,
            ProposedTarget = delivery.TargetPath, Required = delivery.Required, Fingerprint = Fingerprint(delivery.Id, delivery.TargetPath),
            CreatedAt = clock.GetUtcNow(), ConfirmedAt = clock.GetUtcNow(), Result = StoredResult(RoutingActionStatus.Queued),
        });
        await RefreshCaptureStateAsync(retryCapture, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result(RoutingActionStatus.Queued, delivery, "Retry queued at the recorded target.");
    }

    public async Task<RoutingActionPreview> PreviewResendAsync(Guid deliveryId, CancellationToken cancellationToken)
    {
        DeliveryEntity? delivery = await database.Deliveries.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (delivery is null) return Missing(deliveryId);
        if (!RoutingActionPolicy.CanResend(delivery.State)) return Result(RoutingActionStatus.BlockedState, delivery, "Resend is only available after a successful delivery.");
        RoutingDecisionEntity decision = await database.RoutingDecisions.AsNoTracking().SingleAsync(item => item.Id == delivery.DecisionId, cancellationToken);
        CaptureEntity? capture = await database.Captures.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == decision.CaptureId, cancellationToken);
        if (capture is null || !await IsCaptureWorkflowAvailableAsync(capture, delivery.AssetId, cancellationToken))
            return Result(RoutingActionStatus.BlockedState, delivery, "Capture retention or erasure has stopped new routing work.");
        var action = new RoutingActionEntity
        {
            Id = Guid.NewGuid(), Kind = "ResendPreview", AssetId = delivery.AssetId, DestinationId = delivery.DestinationId,
            SourceDeliveryId = delivery.Id, DecisionId = decision.Id, SourceDecisionId = decision.Id,
            ProposedTarget = delivery.TargetPath, Required = delivery.Required,
            Fingerprint = Fingerprint(delivery.Id, decision.Id, delivery.TargetPath, delivery.State.ToString()), CreatedAt = clock.GetUtcNow(),
            Result = StoredResult(RoutingActionStatus.ConfirmationRequired),
        };
        database.RoutingActions.Add(action);
        await database.SaveChangesAsync(cancellationToken);
        return new(RoutingActionStatus.ConfirmationRequired, delivery.AssetId, delivery.DestinationId, delivery.TargetPath, delivery.TargetPath,
            delivery.Required, "Resend will repeat the successful delivery at its recorded target.", action.Id.ToString("N"));
    }

    public async Task<RoutingActionPreview> ResendAsync(Guid deliveryId, string? confirmationToken, Guid? verifiedSupplierId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(confirmationToken, "N", out Guid previewId))
            return new(RoutingActionStatus.ConfirmationRequired, Guid.Empty, Guid.Empty, null, null, false, "Get a fresh preview before confirming Resend.");
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        RoutingActionEntity? action = await database.RoutingActions.SingleOrDefaultAsync(item => item.Id == previewId, cancellationToken);
        DeliveryEntity? prior = await database.Deliveries.SingleOrDefaultAsync(item => item.Id == deliveryId, cancellationToken);
        if (action is null || action.Kind != "ResendPreview" || action.SourceDeliveryId != deliveryId || action.ConfirmedAt is not null
            || prior is null || prior.State != DeliveryState.Delivered)
            return new(RoutingActionStatus.BlockedState, prior?.AssetId ?? Guid.Empty, prior?.DestinationId ?? Guid.Empty, prior?.TargetPath, null, false,
                "The preview is missing, used, expired or the delivery is no longer successful.");
        if (RoutingActionPolicy.PreviewExpired(action.CreatedAt, clock.GetUtcNow()))
            return new(RoutingActionStatus.StalePreview, prior.AssetId, prior.DestinationId, prior.TargetPath, null, prior.Required, "The preview expired; preview again.");
        RoutingDecisionEntity sourceDecision = await database.RoutingDecisions.SingleAsync(item => item.Id == prior.DecisionId, cancellationToken);
        if (action.Fingerprint != Fingerprint(prior.Id, sourceDecision.Id, prior.TargetPath, prior.State.ToString()))
            return new(RoutingActionStatus.StalePreview, prior.AssetId, prior.DestinationId, prior.TargetPath, null, prior.Required, "The successful delivery changed; preview again.");
        CaptureEntity resendCapture = await database.Captures.SingleAsync(item => item.Id == sourceDecision.CaptureId, cancellationToken);
        if (!await IsCaptureWorkflowAvailableAsync(resendCapture, prior.AssetId, cancellationToken))
            return new(RoutingActionStatus.BlockedState, prior.AssetId, prior.DestinationId, prior.TargetPath, null, prior.Required,
                "Capture retention or erasure has stopped new routing work.");
        bool activeRepeatExists = await database.Deliveries.AnyAsync(item => item.RepeatsDeliveryId == prior.Id
            && (item.State == DeliveryState.Pending || item.State == DeliveryState.Running || item.State == DeliveryState.RetryWait
                || item.State == DeliveryState.Failed || item.State == DeliveryState.Disabled || item.State == DeliveryState.Ambiguous), cancellationToken);
        if (activeRepeatExists) return Result(RoutingActionStatus.AlreadyQueued, prior, "A repeat of this successful delivery is already unfinished.");
        if (verifiedSupplierId is null || !await SupplierStillAuthoritativeAsync(prior.AssetId, verifiedSupplierId.Value, cancellationToken))
            return Result(RoutingActionStatus.BlockedOriginalUnavailable, prior, "Verified original or matching replay bytes changed before Resend committed.");
        DateTimeOffset createdAt = clock.GetUtcNow();
        await RetentionQualification.RecordNewIntentAsync(database, resendCapture, createdAt, cancellationToken);
        int generation = await database.RoutingDecisions.Where(item => item.AssetId == prior.AssetId && item.DestinationId == prior.DestinationId)
            .MaxAsync(item => (int?)item.Generation, cancellationToken) + 1 ?? 0;
        var repeatedDecision = new RoutingDecisionEntity
        {
            Id = Guid.NewGuid(), CaptureId = sourceDecision.CaptureId, AssetId = prior.AssetId, DestinationId = prior.DestinationId,
            Generation = generation, IsCurrent = false, Kind = RoutingDecisionKindValue.Matched, RuleId = sourceDecision.RuleId,
            RuleName = sourceDecision.RuleName, Required = sourceDecision.Required, TargetPath = sourceDecision.TargetPath,
            TargetPathKey = sourceDecision.TargetPathKey, Explanation = "Explicit Resend using the recorded routing decision.",
            RuleSnapshotJson = sourceDecision.RuleSnapshotJson, CreatedAt = clock.GetUtcNow(),
            CollectionId = sourceDecision.CollectionId, CollectionName = sourceDecision.CollectionName,
            CollectionPath = sourceDecision.CollectionPath,
        };
        database.RoutingDecisions.Add(repeatedDecision);
        DestinationEntity? destination = await database.Destinations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == prior.DestinationId, cancellationToken);
        var repeated = new DeliveryEntity
        {
            Id = Guid.NewGuid(), DecisionId = repeatedDecision.Id, AssetId = prior.AssetId, DestinationId = prior.DestinationId,
            TargetPath = prior.TargetPath, Required = prior.Required, State = destination is { Enabled: false } ? DeliveryState.Disabled : DeliveryState.Pending,
            RepeatsDeliveryId = prior.Id, SourceAssetId = verifiedSupplierId == prior.AssetId ? null : verifiedSupplierId, CreatedAt = createdAt,
        };
        database.Deliveries.Add(repeated);
        action.Kind = "Resend";
        action.SourceDecisionId = sourceDecision.Id;
        action.DecisionId = repeatedDecision.Id;
        action.DeliveryId = repeated.Id;
        action.ConfirmedAt = createdAt;
        RoutingActionStatus resultStatus = repeated.State == DeliveryState.Disabled ? RoutingActionStatus.Paused : RoutingActionStatus.Queued;
        action.Result = StoredResult(resultStatus);
        await RefreshCaptureStateAsync(resendCapture, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result(resultStatus, repeated, "Resend queued at the recorded target.");
    }

    public Task<RoutingActionPreview> PreviewReevaluateAsync(Guid assetId, Guid destinationId, CancellationToken cancellationToken) =>
        EvaluateCurrentAsync(assetId, destinationId, cancellationToken);

    public async Task<RoutingActionPreview> ReevaluateAsync(Guid assetId, Guid destinationId, string? confirmationToken, Guid? verifiedSupplierId, CancellationToken cancellationToken)
    {
        CurrentEvaluation? before = await LoadCurrentEvaluationAsync(assetId, destinationId, cancellationToken, lockCapture: false);
        RoutingDecision? beforeDecision = before is { IsReleased: true } && before.Destination.Enabled
            ? RoutingRuleEvaluator.Evaluate(before.Config is null ? [] : RoutingRuleJson.ReadRules(before.Config.RulesJson),
                EfCaptureRoutingProcessor.Facts(before.Capture, before.Asset, before.Role, releasedCuration: false, currentCollection: before.Collection)) : null;
        bool beforeNeedsBytes = beforeDecision is not null && RoutingActionPolicy.NeedsOriginal(beforeDecision,
            before!.Deliveries.Select(item => (item.State, item.TargetPath)));
        database.ChangeTracker.Clear();
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CurrentEvaluation? evaluation = await LoadCurrentEvaluationAsync(assetId, destinationId, cancellationToken, lockCapture: true);
        if (evaluation is null) return new(RoutingActionStatus.NotFound, assetId, destinationId, null, null, false, "Asset or current Capture decision was not found.");
        if (!evaluation.IsReleased) return new(RoutingActionStatus.BlockedNotReleased, assetId, destinationId, evaluation.Current?.TargetPath, null, false,
            "Reevaluate is available after this Capture has been released.");
        if (!evaluation.Destination.Enabled) return new(RoutingActionStatus.Paused, assetId, destinationId, evaluation.Current?.TargetPath, null, false, "Destination is disabled; Reevaluate waits until it is enabled.");
        RoutingDecision decision = RoutingRuleEvaluator.Evaluate(evaluation.Config is null ? [] : RoutingRuleJson.ReadRules(evaluation.Config.RulesJson),
            EfCaptureRoutingProcessor.Facts(evaluation.Capture, evaluation.Asset, evaluation.Role, releasedCuration: false, currentCollection: evaluation.Collection));
        if (decision.Kind == RoutingDecisionKind.Matched && await HasTargetCollisionAsync(assetId, destinationId, decision.TargetPath!, cancellationToken))
            decision = new(RoutingDecisionKind.InvalidTarget, decision.RuleId, decision.Required, decision.TargetPath, "Another Asset already uses the resolved destination path.");
        if (!Guid.TryParseExact(confirmationToken, "N", out Guid previewId))
            return new(RoutingActionStatus.ConfirmationRequired, assetId, destinationId, evaluation.Current?.TargetPath, decision.TargetPath,
                decision.Required, "Get a fresh preview before confirming Reevaluate.");
        RoutingActionEntity? action = await database.RoutingActions.SingleOrDefaultAsync(item => item.Id == previewId, cancellationToken);
        string fingerprint = Fingerprint(evaluation, decision);
        if (action is null || action.Kind != "ReevaluatePreview" || action.AssetId != assetId || action.DestinationId != destinationId
            || action.ConfirmedAt is not null || RoutingActionPolicy.PreviewExpired(action.CreatedAt, clock.GetUtcNow())
            || action.Fingerprint != fingerprint)
            return new(RoutingActionStatus.StalePreview, assetId, destinationId, evaluation.Current?.TargetPath, decision.TargetPath,
                decision.Required, "Curation, Collection, rules or the current decision changed; preview again.");
        bool successfulSameTarget = evaluation.Deliveries.Any(item => item.State == DeliveryState.Delivered
            && string.Equals(item.TargetPath, decision.TargetPath, StringComparison.OrdinalIgnoreCase));
        var preview = new RoutingActionPreview(RoutingActionStatus.ConfirmationRequired, assetId, destinationId, evaluation.Current?.TargetPath,
            decision.TargetPath, decision.Required, decision.Explanation, action.Id.ToString("N"));
        if (evaluation.Deliveries.Any(item => item.State == DeliveryState.Ambiguous))
            return preview with { Status = RoutingActionStatus.BlockedReconciliation, Explanation = "An ambiguous remote outcome must be reconciled first." };
        if (decision.TargetPath is not null && evaluation.Deliveries.Any(item => item.State == DeliveryState.Running
            && string.Equals(item.TargetPath, decision.TargetPath, StringComparison.OrdinalIgnoreCase)))
            return preview with { Status = RoutingActionStatus.BlockedReconciliation, Explanation = "A write to this target is still in progress; confirm its outcome before reevaluating." };
        if (decision.Kind == RoutingDecisionKind.Matched && !successfulSameTarget && !beforeNeedsBytes)
            return preview with { Status = RoutingActionStatus.StalePreview, Explanation = "Routing changed; preview again." };
        if (beforeNeedsBytes && verifiedSupplierId is null)
            return preview with { Status = RoutingActionStatus.BlockedOriginalUnavailable, Explanation = "Verified original or matching replay bytes are unavailable." };
        if (decision.Kind == RoutingDecisionKind.Matched && !successfulSameTarget
            && !await SupplierStillAuthoritativeAsync(assetId, verifiedSupplierId!.Value, cancellationToken))
            return preview with { Status = RoutingActionStatus.BlockedOriginalUnavailable, Explanation = "Verified original bytes changed before Reevaluate committed." };

        foreach (DeliveryEntity delivery in evaluation.Deliveries.Where(item => item.State is DeliveryState.Pending or DeliveryState.RetryWait or DeliveryState.Failed or DeliveryState.Disabled))
            delivery.State = DeliveryState.Superseded;
        if (evaluation.Current is not null) evaluation.Current.IsCurrent = false;
        int generation = await database.RoutingDecisions.Where(item => item.AssetId == assetId && item.DestinationId == destinationId)
            .MaxAsync(item => (int?)item.Generation, cancellationToken) + 1 ?? 0;
        RoutingRule? rule = (evaluation.Config is null ? [] : RoutingRuleJson.ReadRules(evaluation.Config.RulesJson))
            .FirstOrDefault(item => item.Id == decision.RuleId);
        var newDecision = new RoutingDecisionEntity
        {
            Id = Guid.NewGuid(), CaptureId = evaluation.Capture.Id, AssetId = assetId, DestinationId = destinationId,
            Generation = generation, IsCurrent = true, Kind = ToStoredKind(decision.Kind), RuleId = decision.RuleId,
            RuleName = rule?.Name, Required = decision.Required, TargetPath = decision.TargetPath,
            TargetPathKey = decision.Kind == RoutingDecisionKind.Matched ? decision.TargetPath!.ToUpperInvariant() : null,
            Explanation = decision.Explanation, RuleSnapshotJson = rule is null ? null : RoutingRuleJson.WriteRule(rule), CreatedAt = clock.GetUtcNow(),
            CollectionId = evaluation.Collection.Id, CollectionName = evaluation.Collection.Name,
            CollectionPath = evaluation.Collection.Path,
        };
        database.RoutingDecisions.Add(newDecision);
        action.Kind = "Reevaluate";
        action.SourceDecisionId = evaluation.Current?.Id;
        action.DecisionId = newDecision.Id;
        action.ProposedTarget = decision.TargetPath;
        action.Required = decision.Required;
        action.ConfirmedAt = clock.GetUtcNow();
        action.Result = StoredResult(decision.Kind == RoutingDecisionKind.Matched && !successfulSameTarget
            ? RoutingActionStatus.Queued : RoutingActionStatus.Recorded);
        action.DeliveryId = null;
        bool createsIntent = decision.Kind == RoutingDecisionKind.Matched
            || decision.RuleId is not null
                && decision.Kind is (RoutingDecisionKind.InvalidTarget or RoutingDecisionKind.DestinationDisabled);
        if (createsIntent && !(decision.Kind == RoutingDecisionKind.Matched && successfulSameTarget))
            await RetentionQualification.RecordNewIntentAsync(database, evaluation.Capture, clock.GetUtcNow(), cancellationToken);
        if (decision.Kind == RoutingDecisionKind.Matched && !successfulSameTarget)
        {
            DateTimeOffset createdAt = clock.GetUtcNow();
            var delivery = new DeliveryEntity
            {
                Id = Guid.NewGuid(), DecisionId = newDecision.Id, AssetId = assetId, DestinationId = destinationId,
                SourceAssetId = verifiedSupplierId == assetId ? null : verifiedSupplierId, TargetPath = decision.TargetPath!, Required = decision.Required,
                State = DeliveryState.Pending, CreatedAt = createdAt,
            };
            database.Deliveries.Add(delivery);
            action.DeliveryId = delivery.Id;
        }
        await database.SaveChangesAsync(cancellationToken);
        await RefreshCaptureStateAsync(evaluation.Capture, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return preview with { Status = RoutingActionStatus.Recorded };
    }

    private async Task<RoutingActionPreview> EvaluateCurrentAsync(Guid assetId, Guid destinationId, CancellationToken cancellationToken)
    {
        CurrentEvaluation? evaluation = await LoadCurrentEvaluationAsync(assetId, destinationId, cancellationToken, lockCapture: false);
        if (evaluation is null) return new(RoutingActionStatus.NotFound, assetId, destinationId, null, null, false, "Asset or current Capture decision was not found.");
        if (!evaluation.IsReleased) return new(RoutingActionStatus.BlockedNotReleased, assetId, destinationId, evaluation.Current?.TargetPath, null, false,
            "Reevaluate is available after this Capture has been released.");
        if (!evaluation.Destination.Enabled) return new(RoutingActionStatus.Paused, assetId, destinationId, evaluation.Current?.TargetPath, null, false, "Destination is disabled.");
        RoutingRule[] rules = evaluation.Config is null ? [] : RoutingRuleJson.ReadRules(evaluation.Config.RulesJson);
        RoutingDecision decision = RoutingRuleEvaluator.Evaluate(rules,
            EfCaptureRoutingProcessor.Facts(evaluation.Capture, evaluation.Asset, evaluation.Role, releasedCuration: false, currentCollection: evaluation.Collection));
        if (decision.Kind == RoutingDecisionKind.Matched && await HasTargetCollisionAsync(assetId, destinationId, decision.TargetPath!, cancellationToken))
            decision = new(RoutingDecisionKind.InvalidTarget, decision.RuleId, decision.Required, decision.TargetPath, "Another Asset already uses the resolved destination path.");
        string fingerprint = Fingerprint(evaluation, decision);
        var action = new RoutingActionEntity
        {
            Id = Guid.NewGuid(), Kind = "ReevaluatePreview", AssetId = assetId, DestinationId = destinationId,
            SourceDecisionId = evaluation.Current?.Id, ProposedTarget = decision.TargetPath, Required = decision.Required,
            Fingerprint = fingerprint, CreatedAt = clock.GetUtcNow(), Result = StoredResult(RoutingActionStatus.ConfirmationRequired),
        };
        database.RoutingActions.Add(action);
        await database.SaveChangesAsync(cancellationToken);
        return new(RoutingActionStatus.ConfirmationRequired, assetId, destinationId, evaluation.Current?.TargetPath, decision.TargetPath,
            decision.Required, decision.Explanation, action.Id.ToString("N"));
    }

    private async Task<CurrentEvaluation?> LoadCurrentEvaluationAsync(Guid assetId, Guid destinationId, CancellationToken cancellationToken, bool lockCapture)
    {
        await using IDbContextTransaction? readTransaction = lockCapture ? null
            : await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-collection-assignment', 0))", cancellationToken);
        var member = await (from item in database.CaptureAssets.AsNoTracking()
            join asset in database.Assets.AsNoTracking() on item.AssetId equals asset.Id
            join routeCapture in database.Captures on item.CaptureId equals routeCapture.Id
            where item.AssetId == assetId && item.IsPrimary
                && routeCapture.RetentionDisposition == "Active" && !routeCapture.RetentionPaused
                && !database.PrivacyErasureAssets.Any(erased => erased.AssetId == assetId)
                && !database.AssetRemovalOperations.Any(operation => operation.AssetId == assetId)
            select new { item.CaptureId, item.Role, Asset = asset }).SingleOrDefaultAsync(cancellationToken);
        if (member is null) return null;
        CaptureEntity? capture = lockCapture
            ? await database.Captures.FromSqlInterpolated($"SELECT * FROM captures WHERE \"Id\" = {member.CaptureId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
            : await database.Captures.AsNoTracking().SingleOrDefaultAsync(item => item.Id == member.CaptureId, cancellationToken);
        if (capture is null) return null;
        CollectionEntity collection = await database.Collections.AsNoTracking()
            .SingleAsync(item => item.Id == capture.CollectionId, cancellationToken);
        if (capture.RetentionDisposition != "Active" || capture.RetentionPaused
            || await database.CaptureAssets.AnyAsync(item => item.CaptureId == capture.Id
                && database.PrivacyErasureAssets.Any(erased => erased.AssetId == item.AssetId), cancellationToken)
            || await database.CaptureAssets.AnyAsync(item => item.CaptureId == capture.Id
                && database.AssetRemovalOperations.Any(operation => operation.AssetId == item.AssetId), cancellationToken))
            return null;
        var currentMember = await (from item in database.CaptureAssets.AsNoTracking()
            join asset in database.Assets.AsNoTracking() on item.AssetId equals asset.Id
            where item.CaptureId == capture.Id && item.AssetId == assetId && item.IsPrimary
                && asset.State == "Spooled"
                && !database.PrivacyErasureAssets.Any(erased => erased.AssetId == assetId)
                && !database.AssetRemovalOperations.Any(operation => operation.AssetId == assetId)
            select new { item.Role, Asset = asset }).SingleOrDefaultAsync(cancellationToken);
        if (currentMember is null) return null;
        RoutingDecisionEntity? current = await database.RoutingDecisions.SingleOrDefaultAsync(item => item.AssetId == assetId
            && item.DestinationId == destinationId && item.IsCurrent, cancellationToken);
        RoutingDecisionEntity[] allDecisions = await database.RoutingDecisions.Where(item => item.AssetId == assetId && item.DestinationId == destinationId).ToArrayAsync(cancellationToken);
        Guid[] decisionIds = [.. allDecisions.Select(item => item.Id)];
        DeliveryEntity[] deliveries = decisionIds.Length == 0 ? [] : await database.Deliveries.Where(item => decisionIds.Contains(item.DecisionId)).ToArrayAsync(cancellationToken);
        DestinationRoutingEntity? config = await database.DestinationRoutings.AsNoTracking()
            .SingleOrDefaultAsync(item => item.DestinationId == destinationId, cancellationToken);
        DestinationEntity destination = await database.Destinations.AsNoTracking().SingleAsync(item => item.Id == destinationId, cancellationToken);
        if (readTransaction is not null) await readTransaction.CommitAsync(cancellationToken);
        return new(capture, collection, currentMember.Asset, currentMember.Role, current, deliveries, config, destination,
            capture.ReleasedAt is not null && capture.RoutingSnapshotJson is not null);
    }

    private async Task<bool> HasTargetCollisionAsync(Guid assetId, Guid destinationId, string target, CancellationToken cancellationToken)
    {
        if (await database.RoutingDecisions.AsNoTracking().AnyAsync(item => item.AssetId != assetId && item.DestinationId == destinationId
            && item.IsCurrent && item.Kind == RoutingDecisionKindValue.Matched
            && item.TargetPathKey == target.ToUpperInvariant(), cancellationToken)) return true;
        string[] successfulTargets = await database.Deliveries.AsNoTracking().Where(item => item.AssetId != assetId
                && item.DestinationId == destinationId && item.State == DeliveryState.Delivered)
            .Select(item => item.TargetPath).ToArrayAsync(cancellationToken);
        return successfulTargets.Any(path => string.Equals(path, target, StringComparison.OrdinalIgnoreCase));
    }

    private static string Fingerprint(Guid deliveryId, string target) => Fingerprint(deliveryId, Guid.Empty, target, "retry");
    private static string Fingerprint(Guid deliveryId, Guid decisionId, string target, string state) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { deliveryId, decisionId, target, state })));
    private static string Fingerprint(CurrentEvaluation evaluation, RoutingDecision decision) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            evaluation.Capture.Revision,
            collectionId = evaluation.Collection.Id,
            collectionRevision = evaluation.Collection.Revision,
            collectionName = evaluation.Collection.Name,
            collectionPath = evaluation.Collection.Path,
            currentDecisionId = evaluation.Current?.Id,
            rules = evaluation.Config?.RulesJson,
            decision,
        })));

    private async Task<bool> SupplierStillAuthoritativeAsync(Guid assetId, Guid supplierId, CancellationToken cancellationToken)
    {
        var original = await database.Assets.AsNoTracking().Where(item => item.Id == assetId)
            .Select(item => new { item.Size, item.Sha256, item.State }).SingleOrDefaultAsync(cancellationToken);
        var supplier = await database.Assets.AsNoTracking().Where(item => item.Id == supplierId)
            .Select(item => new { item.Size, item.Sha256, item.State }).SingleOrDefaultAsync(cancellationToken);
        if (original is null || supplier is null || original.State != "Spooled" || supplier.State != "Spooled"
            || original.Size != supplier.Size || original.Sha256 != supplier.Sha256) return false;
        Guid[] assetIds = assetId == supplierId ? [assetId] : [assetId, supplierId];
        if (await database.AssetRemovalOperations.AnyAsync(operation => assetIds.Contains(operation.AssetId), cancellationToken)
            || await database.PrivacyErasureAssets.AnyAsync(erased => assetIds.Contains(erased.AssetId), cancellationToken)) return false;
        return supplierId == assetId || await (from replay in database.CaptureAssets.AsNoTracking()
            join primary in database.CaptureAssets.AsNoTracking() on new { replay.CaptureId, replay.RelatedAssetId, replay.Role }
                equals new { primary.CaptureId, RelatedAssetId = (Guid?)primary.AssetId, primary.Role }
            where replay.AssetId == supplierId && primary.AssetId == assetId && primary.IsPrimary
                && !replay.IsPrimary && replay.GroupingReason == "recognized_replay"
            select replay.AssetId).AnyAsync(cancellationToken);
    }

    private async Task<bool> IsCaptureWorkflowAvailableAsync(CaptureEntity capture, Guid assetId,
        CancellationToken cancellationToken)
    {
        if (capture.RetentionDisposition != "Active" || capture.RetentionPaused) return false;
        bool captureFenced = await database.CaptureAssets.AnyAsync(member => member.CaptureId == capture.Id
            && (database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId)
                || database.AssetRemovalOperations.Any(operation => operation.AssetId == member.AssetId)), cancellationToken);
        if (captureFenced) return false;
        return await database.Assets.AnyAsync(asset => asset.Id == assetId && asset.State == "Spooled"
            && !database.PrivacyErasureAssets.Any(erased => erased.AssetId == asset.Id)
            && !database.AssetRemovalOperations.Any(operation => operation.AssetId == asset.Id), cancellationToken);
    }

    private async Task LockRoutingAsync(CancellationToken cancellationToken) =>
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);

    private async Task RefreshCaptureStateAsync(CaptureEntity capture, CancellationToken cancellationToken)
    {
        bool matched = await database.RoutingDecisions.AnyAsync(item => item.CaptureId == capture.Id && item.IsCurrent
            && item.Kind == RoutingDecisionKindValue.Matched, cancellationToken);
        bool requiredPending = await database.Deliveries.AnyAsync(item => item.Required && item.State != DeliveryState.Delivered
            && item.State != DeliveryState.Superseded && database.RoutingDecisions.Any(route => route.Id == item.DecisionId && route.CaptureId == capture.Id), cancellationToken);
        var state = CaptureCompletionPolicy.State(matched, requiredPending);
        if (capture.State != state) { capture.State = state; capture.Revision++; }
    }

    private static RoutingDecisionKindValue ToStoredKind(RoutingDecisionKind kind) => kind switch
    {
        RoutingDecisionKind.Matched => RoutingDecisionKindValue.Matched,
        RoutingDecisionKind.InvalidTarget => RoutingDecisionKindValue.InvalidTarget,
        RoutingDecisionKind.DestinationDisabled => RoutingDecisionKindValue.DestinationDisabled,
        _ => RoutingDecisionKindValue.NoMatch,
    };

    private static RoutingActionPreview Missing(Guid id) => new(RoutingActionStatus.NotFound, Guid.Empty, Guid.Empty, null, null, false, $"Delivery '{id}' was not found.");
    private static RoutingActionPreview Result(RoutingActionStatus status, DeliveryEntity delivery, string explanation) =>
        new(status, delivery.AssetId, delivery.DestinationId, delivery.TargetPath, delivery.TargetPath, delivery.Required, explanation);
    private static string StoredResult(RoutingActionStatus status) => status switch
    {
        RoutingActionStatus.ConfirmationRequired => "confirmation_required",
        RoutingActionStatus.Queued => "queued",
        RoutingActionStatus.Paused => "paused",
        RoutingActionStatus.Recorded => "recorded",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
    private sealed record CurrentEvaluation(CaptureEntity Capture, CollectionEntity Collection, AssetEntity Asset, string Role, RoutingDecisionEntity? Current,
        DeliveryEntity[] Deliveries, DestinationRoutingEntity? Config, DestinationEntity Destination, bool IsReleased);
}
