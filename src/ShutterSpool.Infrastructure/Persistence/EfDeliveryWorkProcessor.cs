using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using System.Diagnostics;
using ShutterSpool.Application.Destinations;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfDeliveryWorkProcessor(IDbContextFactory<ShutterSpoolDbContext> databaseFactory,
    TimeProvider clock, DeliveryWorkerSettings settings, ICredentialProtector protector,
    DestinationCapabilities? capabilities = null) : IDeliveryWorkStore
{
    private readonly string _workerId = $"{Environment.MachineName[..Math.Min(Environment.MachineName.Length, 94)]}:{Guid.NewGuid():N}";

    public async Task<ClaimedDelivery?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using ShutterSpoolDbContext database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        string[] enabledKinds = [.. (capabilities ?? DestinationCapabilities.All).EnabledKinds];
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(database, cancellationToken);
        DeliveryEntity? delivery = await database.Deliveries.FromSqlInterpolated($"""
            SELECT d.* FROM deliveries d
            JOIN destinations config ON config."Id" = d."DestinationId"
            WHERE ((d."State" IN ('Pending', 'RetryWait') AND (d."NextAttemptAt" IS NULL OR d."NextAttemptAt" <= {now})
                     AND config."Enabled"
                     AND EXISTS (SELECT 1 FROM routing_decisions decision WHERE decision."Id" = d."DecisionId"
                         AND decision."Kind" = 'Matched'
                         AND (decision."IsCurrent" OR EXISTS (SELECT 1 FROM routing_actions resend
                             WHERE resend."Kind" = 'Resend' AND resend."DecisionId" = decision."Id"
                                 AND resend."DeliveryId" = d."Id" AND resend."ConfirmedAt" IS NOT NULL
                                 AND EXISTS (SELECT 1 FROM deliveries source
                                     WHERE source."Id" = resend."SourceDeliveryId" AND source."State" = 'Delivered'
                                         AND source."AssetId" = decision."AssetId" AND source."DestinationId" = decision."DestinationId")
                                 AND NOT EXISTS (SELECT 1 FROM routing_decisions newer
                                     WHERE newer."AssetId" = decision."AssetId" AND newer."DestinationId" = decision."DestinationId"
                                         AND newer."IsCurrent" AND newer."Generation" > decision."Generation"))))
                     AND NOT EXISTS (SELECT 1 FROM deliveries active WHERE active."AssetId" = d."AssetId"
                         AND active."DestinationId" = d."DestinationId" AND active."Id" <> d."Id"
                         AND active."State" IN ('Running', 'Ambiguous')))
                OR (d."State" = 'Running' AND d."LeaseUntil" <= {now})
                OR (d."State" = 'Ambiguous' AND EXISTS (SELECT 1 FROM delivery_attempts attempt
                    WHERE attempt."DeliveryId" = d."Id" AND attempt."FinishedAt" IS NOT NULL
                        AND attempt."Result" = 'Uncertain'
                        AND attempt."RetryNumber" = (SELECT max(latest."RetryNumber") FROM delivery_attempts latest WHERE latest."DeliveryId" = d."Id")))
                )
                AND config."Kind" = ANY({enabledKinds})
                AND EXISTS (SELECT 1 FROM capture_assets member JOIN captures capture ON capture."Id" = member."CaptureId"
                    WHERE member."AssetId" = d."AssetId" AND capture."RetentionDisposition" = 'Active'
                        AND NOT capture."RetentionPaused")
                AND NOT EXISTS (SELECT 1 FROM privacy_erasure_assets erased WHERE erased."AssetId" = d."AssetId")
            ORDER BY d."CreatedAt", d."Id"
            FOR UPDATE OF d SKIP LOCKED LIMIT 1
            """).FirstOrDefaultAsync(cancellationToken);
        if (delivery is null) return null;

        Guid claimToken = Guid.NewGuid();
        bool reconcile = delivery.State is DeliveryState.Running or DeliveryState.Ambiguous;
        DeliveryAttemptEntity? attempt;
        if (reconcile)
        {
            attempt = await database.DeliveryAttempts.Where(item => item.DeliveryId == delivery.Id && item.FinishedAt == null)
                .OrderByDescending(item => item.RetryNumber).FirstOrDefaultAsync(cancellationToken)
                ?? await database.DeliveryAttempts.Where(item => item.DeliveryId == delivery.Id)
                    .OrderByDescending(item => item.RetryNumber).FirstOrDefaultAsync(cancellationToken);
            if (attempt is null)
            {
                delivery.State = DeliveryState.Ambiguous;
                delivery.LeaseId = null;
                delivery.LeaseUntil = null;
                delivery.RetryReason = "An expired claim has no persisted attempt to reconcile.";
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
        }
        else
        {
            attempt = new DeliveryAttemptEntity
            {
                Id = Guid.NewGuid(),
                DeliveryId = delivery.Id,
                AssetId = delivery.AssetId,
                RetryNumber = delivery.AttemptCount + 1,
                WorkerId = _workerId,
                ClaimToken = claimToken,
                StartedAt = now,
                TraceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"),
            };
            database.DeliveryAttempts.Add(attempt);
            delivery.AttemptCount++;
        }

        delivery.State = DeliveryState.Running;
        delivery.LeaseId = claimToken;
        delivery.LeaseUntil = now.AddSeconds(settings.LeaseDurationSeconds);
        delivery.NextAttemptAt = null;
        DeliveryAsset asset = await database.Assets.AsNoTracking()
            .Where(item => item.Id == delivery.SourceAssetId.GetValueOrDefault(delivery.AssetId))
            .Select(item => new DeliveryAsset(item.SpoolKey, item.Size, item.Sha256, item.State))
            .SingleAsync(cancellationToken);
        string kind = await database.Destinations.AsNoTracking().Where(item => item.Id == delivery.DestinationId)
            .Select(item => item.Kind).SingleAsync(cancellationToken);
        DestinationDeliveryConfiguration? configuration = kind == "local"
            ? new LocalDestinationDeliveryConfiguration(await database.LocalDestinations.AsNoTracking()
                .Where(item => item.DestinationId == delivery.DestinationId).Select(item => item.RootPath)
                .SingleOrDefaultAsync(cancellationToken))
            : kind == "webdav"
                ? await WebDavConfigurationAsync(database, delivery.DestinationId, protector, cancellationToken)
                : null;
        Guid captureId = await database.RoutingDecisions.AsNoTracking().Where(item => item.Id == delivery.DecisionId)
            .Select(item => item.CaptureId).SingleAsync(cancellationToken);
        var request = new DeliveryRequest(delivery.Id, attempt.Id, delivery.AssetId, delivery.DestinationId,
            delivery.TargetPath, asset.Size, asset.Sha256, attempt.TraceId, kind, configuration, captureId);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(delivery.Id, attempt.Id, claimToken, reconcile, request, asset);
    }

    private static async Task<WebDavDestinationDeliveryConfiguration> WebDavConfigurationAsync(ShutterSpoolDbContext database,
        Guid destinationId, ICredentialProtector protector, CancellationToken cancellationToken)
    {
        var config = await (from item in database.WebDavDestinations.AsNoTracking()
            join credential in database.DestinationCredentials.AsNoTracking() on item.DestinationId equals credential.DestinationId into credentials
            from credential in credentials.DefaultIfEmpty()
            where item.DestinationId == destinationId
            select new { item.CollectionUrl, item.Username, EncryptedSecret = credential == null ? null : credential.EncryptedSecret })
            .SingleOrDefaultAsync(cancellationToken);
        string? encrypted = config?.EncryptedSecret;
        string? password = encrypted is null ? null : protector.Unprotect(encrypted);
        return new(config?.CollectionUrl, config?.Username, password);
    }

    public async Task<bool> RenewAsync(Guid deliveryId, Guid claimToken, CancellationToken cancellationToken)
    {
        await using ShutterSpoolDbContext database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(database, cancellationToken);
        var claim = await database.Deliveries.AsNoTracking().Where(item => item.Id == deliveryId && item.State == DeliveryState.Running
                && item.LeaseId == claimToken && item.LeaseUntil > now)
            .Select(item => new { item.DestinationId, item.DecisionId, item.AssetId }).SingleOrDefaultAsync(cancellationToken);
        if (claim is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        bool enabled = await database.Destinations.AsNoTracking().Where(item => item.Id == claim.DestinationId)
            .Select(item => item.Enabled).SingleAsync(cancellationToken);
        bool current = await IsDecisionActiveAsync(database, deliveryId, claim.DecisionId, cancellationToken);
        bool erased = await database.PrivacyErasureAssets.AnyAsync(item => item.AssetId == claim.AssetId,
            cancellationToken);
        if (!enabled || !current || erased)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        int updated = await database.Deliveries.Where(item => item.Id == deliveryId && item.State == DeliveryState.Running
                && item.LeaseId == claimToken && item.LeaseUntil > now)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseUntil, now.AddSeconds(settings.LeaseDurationSeconds)), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TargetReservedAsync(DeliveryRequest request, CancellationToken cancellationToken)
    {
        await using ShutterSpoolDbContext database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        return await database.RoutingDecisions.AsNoTracking().AnyAsync(item => item.DestinationId == request.DestinationId
            && item.TargetPathKey == request.TargetPath.ToUpperInvariant() && item.AssetId != request.AssetId,
            cancellationToken);
    }

    public async Task FinishAsync(ClaimedDelivery work, DeliveryResult result, CancellationToken cancellationToken)
    {
        await using ShutterSpoolDbContext database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(database, cancellationToken);
        DeliveryEntity? delivery = await database.Deliveries.SingleOrDefaultAsync(item => item.Id == work.DeliveryId
            && item.State == DeliveryState.Running && item.LeaseId == work.ClaimToken && item.LeaseUntil > now, cancellationToken);
        if (delivery is null) return;
        DeliveryAttemptEntity attempt = await database.DeliveryAttempts.SingleAsync(item => item.Id == work.AttemptId, cancellationToken);
        if (await database.PrivacyErasureAssets.AnyAsync(item => item.AssetId == delivery.AssetId, cancellationToken))
        {
            RecordErasedOutcome(delivery, attempt,
                result.Kind == DeliveryResultKind.Confirmed && result.Evidence is not null, now);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        attempt.FinishedAt = now;
        attempt.BytesSent = result.BytesSent;
        attempt.ErrorCode = DeliveryPolicy.SafeErrorCode(result.ErrorCode);
        delivery.LeaseId = null;
        delivery.LeaseUntil = null;
        delivery.RetryReason = null;

        bool enabled = await database.Destinations.AsNoTracking().Where(item => item.Id == delivery.DestinationId)
            .Select(item => item.Enabled).SingleAsync(cancellationToken);
        bool current = await IsDecisionActiveAsync(database, delivery.Id, delivery.DecisionId, cancellationToken);
        DeliveryTransition transition = DeliveryPolicy.AfterWrite(result, enabled, current, attempt.RetryNumber,
            now, Random.Shared.NextDouble() * 0.2 - 0.1);
        attempt.Result = transition.AttemptResult;
        attempt.ErrorMessage = transition.AttemptMessage;
        attempt.ConfirmationEvidence = transition.Evidence?.ToString();
        delivery.State = transition.State;
        delivery.NextAttemptAt = transition.NextAttemptAt;
        delivery.RetryReason = transition.RetryReason;
        delivery.ConfirmationEvidence = transition.Evidence?.ToString();
        if (delivery.State == DeliveryState.Delivered)
        {
            delivery.ConfirmedAt ??= now;
            await UpdateCaptureStateAsync(database, delivery, cancellationToken);
        }
        await MarkRetentionFactsDirtyAsync(database, delivery.DecisionId, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinishReconciliationAsync(ClaimedDelivery work, DeliveryReconciliation result, CancellationToken cancellationToken)
    {
        await using ShutterSpoolDbContext database = await databaseFactory.CreateDbContextAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(database, cancellationToken);
        DeliveryEntity? delivery = await database.Deliveries.SingleOrDefaultAsync(item => item.Id == work.DeliveryId
            && item.State == DeliveryState.Running && item.LeaseId == work.ClaimToken && item.LeaseUntil > now, cancellationToken);
        if (delivery is null) return;
        DeliveryAttemptEntity attempt = await database.DeliveryAttempts.SingleAsync(item => item.Id == work.AttemptId, cancellationToken);
        if (await database.PrivacyErasureAssets.AnyAsync(item => item.AssetId == delivery.AssetId, cancellationToken))
        {
            RecordErasedOutcome(delivery, attempt,
                result.Kind == DeliveryReconciliationKind.Confirmed && result.Evidence is not null, now);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        bool enabled = await database.Destinations.AsNoTracking().Where(item => item.Id == delivery.DestinationId)
            .Select(item => item.Enabled).SingleAsync(cancellationToken);
        bool current = await IsDecisionActiveAsync(database, delivery.Id, delivery.DecisionId, cancellationToken);
        delivery.LeaseId = null;
        delivery.LeaseUntil = null;
        delivery.RetryReason = null;
        DeliveryTransition transition = DeliveryPolicy.AfterReconciliation(result, enabled, current, now);
        attempt.FinishedAt = now;
        attempt.Result = transition.AttemptResult;
        attempt.ErrorCode = DeliveryPolicy.SafeErrorCode(result.ErrorCode) ?? (transition.State switch
        {
            DeliveryState.Failed => "TARGET_CONFLICT",
            DeliveryState.Ambiguous => "RECONCILIATION_UNCERTAIN",
            _ => null,
        });
        attempt.ErrorMessage = transition.AttemptMessage;
        attempt.ConfirmationEvidence = transition.Evidence?.ToString();
        delivery.State = transition.State;
        delivery.NextAttemptAt = transition.NextAttemptAt;
        delivery.RetryReason = transition.RetryReason;
        delivery.ConfirmationEvidence = transition.Evidence?.ToString();
        if (delivery.State == DeliveryState.Delivered)
        {
            delivery.ConfirmedAt ??= now;
            await UpdateCaptureStateAsync(database, delivery, cancellationToken);
        }
        await MarkRetentionFactsDirtyAsync(database, delivery.DecisionId, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void RecordErasedOutcome(DeliveryEntity delivery, DeliveryAttemptEntity attempt,
        bool positivelyConfirmed, DateTimeOffset now)
    {
        attempt.FinishedAt = now;
        attempt.BytesSent = null;
        attempt.Result = positivelyConfirmed ? "Confirmed" : "Redacted";
        attempt.ErrorCode = null;
        attempt.ErrorMessage = null;
        attempt.ConfirmationEvidence = null;
        delivery.LeaseId = null;
        delivery.LeaseUntil = null;
        delivery.NextAttemptAt = null;
        delivery.RetryReason = null;
        delivery.ConfirmationEvidence = null;
        delivery.State = positivelyConfirmed ? DeliveryState.Delivered : DeliveryState.Ambiguous;
        if (positivelyConfirmed) delivery.ConfirmedAt ??= now;
    }

    private static async Task UpdateCaptureStateAsync(ShutterSpoolDbContext database, DeliveryEntity delivery, CancellationToken cancellationToken)
    {
        Guid captureId = await database.RoutingDecisions.Where(item => item.Id == delivery.DecisionId)
            .Select(item => item.CaptureId).SingleAsync(cancellationToken);
        CaptureEntity capture = await database.Captures.FromSqlInterpolated($"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        bool matched = await database.RoutingDecisions.AnyAsync(item => item.CaptureId == captureId && item.IsCurrent
            && item.Kind == RoutingDecisionKindValue.Matched, cancellationToken);
        bool requiredPending = await database.Deliveries.AnyAsync(item => item.Id != delivery.Id && item.Required && item.State != DeliveryState.Delivered
            && item.State != DeliveryState.Superseded
            && database.RoutingDecisions.Any(decision => decision.Id == item.DecisionId && decision.CaptureId == captureId), cancellationToken);
        CaptureState next = CaptureCompletionPolicy.State(matched, requiredPending);
        if (capture.State != next) { capture.State = next; capture.Revision++; }
    }

    private static async Task MarkRetentionFactsDirtyAsync(ShutterSpoolDbContext database, Guid decisionId,
        CancellationToken cancellationToken)
    {
        Guid captureId = await database.RoutingDecisions.AsNoTracking()
            .Where(item => item.Id == decisionId).Select(item => item.CaptureId).SingleAsync(cancellationToken);
        await database.Captures.Where(item => item.Id == captureId && item.RetentionDisposition == "Active")
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.RetentionFactsDirty, true), cancellationToken);
    }

    private static Task<bool> IsDecisionActiveAsync(ShutterSpoolDbContext database, Guid deliveryId, Guid decisionId,
        CancellationToken cancellationToken) => database.RoutingDecisions.AsNoTracking().AnyAsync(decision =>
            decision.Id == decisionId && (decision.IsCurrent || database.RoutingActions.Any(action =>
                action.Kind == "Resend" && action.DecisionId == decisionId && action.DeliveryId == deliveryId && action.ConfirmedAt != null
                && database.Deliveries.Any(source => source.Id == action.SourceDeliveryId && source.State == DeliveryState.Delivered
                    && source.AssetId == decision.AssetId && source.DestinationId == decision.DestinationId)
                && !database.RoutingDecisions.Any(current => current.AssetId == decision.AssetId
                    && current.DestinationId == decision.DestinationId && current.IsCurrent && current.Generation > decision.Generation))), cancellationToken);

    private static Task<int> LockRoutingAsync(ShutterSpoolDbContext database, CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
}
