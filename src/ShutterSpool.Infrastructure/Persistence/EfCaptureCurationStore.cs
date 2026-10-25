using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Curation;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCaptureCurationStore(ShutterSpoolDbContext database, TimeProvider clock) : ICaptureCurationStore
{
    public Guid? LastReleasedCaptureId { get; private set; }

    public async Task<CaptureCurationState?> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        CaptureEntity? capture = await database.Captures.AsNoTracking().SingleOrDefaultAsync(capture =>
            capture.Id == captureId && capture.RetentionDisposition == "Active"
            && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                && database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId)), cancellationToken);
        return capture is null ? null : ToState(capture);
    }

    public Task<CaptureMutationResult> UpdateAsync(Guid captureId, int expectedRevision, CaptureCuration curation, CancellationToken cancellationToken) =>
        MutateAsync(captureId, capture => CaptureCurationPolicy.Update(capture, expectedRevision,
            curation.Rating, curation.Label, curation.Tags), cancellationToken);

    public Task<CaptureMutationResult> HoldAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken) =>
        MutateAsync(captureId, capture => CaptureCurationPolicy.Hold(capture, expectedRevision), cancellationToken);

    public Task<CaptureMutationResult> RestoreAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken) =>
        MutateAsync(captureId, capture => CaptureCurationPolicy.Restore(capture, expectedRevision), cancellationToken);

    public Task<CaptureMutationResult> ReleaseNowAsync(Guid captureId, int expectedRevision, CancellationToken cancellationToken) =>
        MutateAsync(captureId, capture => CaptureCurationPolicy.ReleaseNow(capture, expectedRevision, clock.GetUtcNow()),
            cancellationToken, lockRoutingForRelease: true);

    public async Task<bool> ReleaseDueOneAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        LastReleasedCaptureId = null;
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        await LockCollectionAssignmentsAsync(cancellationToken);
        CaptureEntity? capture = await database.Captures.FromSqlInterpolated($"""
            SELECT * FROM captures
            WHERE "RetentionDisposition" = 'Active' AND NOT "RetentionPaused"
              AND NOT EXISTS (
                  SELECT 1 FROM capture_assets member
                  JOIN privacy_erasure_assets erased ON erased."AssetId" = member."AssetId"
                  WHERE member."CaptureId" = captures."Id")
              AND NOT "Held" AND "ReleasedAt" IS NULL AND "ReleaseDueAt" <= {now}
            ORDER BY "ReleaseDueAt", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).FirstOrDefaultAsync(cancellationToken);
        if (capture is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        // Operations needing both locks take the routing key before a Capture row.
        CaptureCurationDecision decision = CaptureCurationPolicy.ReleaseDue(Snapshot(capture), now);
        if (!decision.Accepted)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }
        await FreezeRoutingAsync(capture, cancellationToken);
        Apply(capture, decision.Snapshot);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LastReleasedCaptureId = capture.Id;
        return true;
    }

    private async Task<CaptureMutationResult> MutateAsync(Guid captureId,
        Func<CaptureCurationSnapshot, CaptureCurationDecision> mutation,
        CancellationToken cancellationToken, bool lockRoutingForRelease = false)
    {
        if (lockRoutingForRelease) LastReleasedCaptureId = null;
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        if (lockRoutingForRelease) await LockCollectionAssignmentsAsync(cancellationToken);
        CaptureEntity? capture = await database.Captures.FromSqlInterpolated($"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (capture is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, null);
        }
        bool privateErasureInProgress = await database.CaptureAssets.AnyAsync(member => member.CaptureId == capture.Id
            && database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId), cancellationToken);
        if (capture.RetentionDisposition != "Active" || privateErasureInProgress)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, null);
        }
        if (lockRoutingForRelease && capture.RetentionPaused)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, ToState(capture));
        }

        CaptureCurationDecision decision = mutation(Snapshot(capture));
        if (!decision.Accepted)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, ToState(capture));
        }

        if (decision.Changed)
        {
            if (lockRoutingForRelease) await FreezeRoutingAsync(capture, cancellationToken);
            Apply(capture, decision.Snapshot);
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        if (lockRoutingForRelease && decision.Changed) LastReleasedCaptureId = capture.Id;
        return new(true, ToState(capture));
    }

    private static CaptureCurationSnapshot Snapshot(CaptureEntity capture) => new(
        capture.Revision, capture.Rating, capture.Label, [.. capture.Tags], capture.Policy, capture.DelaySeconds,
        capture.LastPrimaryIngestedAt, capture.ReleaseDueAt, capture.Held, capture.State, capture.ReleasedAt,
        capture.ReleasedRevision, capture.ReleasedRating, capture.ReleasedLabel, [.. capture.ReleasedTags]);

    private static void Apply(CaptureEntity capture, CaptureCurationSnapshot state)
    {
        capture.RetentionFactsDirty = true;
        capture.Revision = state.Revision;
        capture.Rating = state.Rating;
        capture.Label = state.Label;
        capture.Tags = state.Tags;
        capture.ReleaseDueAt = state.ReleaseDueAt;
        capture.Held = state.Held;
        capture.State = state.State;
        capture.ReleasedAt = state.ReleasedAt;
        capture.ReleasedRevision = state.ReleasedRevision;
        capture.ReleasedRating = state.ReleasedRating;
        capture.ReleasedLabel = state.ReleasedLabel;
        capture.ReleasedTags = state.ReleasedTags;
    }

    private async Task FreezeRoutingAsync(CaptureEntity capture, CancellationToken cancellationToken)
    {
        if (capture.RoutingSnapshotJson is not null) return;
        CollectionEntity collection = await database.Collections.AsNoTracking()
            .SingleAsync(item => item.Id == capture.CollectionId, cancellationToken);
        capture.ReleasedCollectionId = collection.Id;
        capture.ReleasedCollectionName = collection.Name;
        capture.ReleasedCollectionPath = collection.Path;
        DestinationRoutingSnapshotRow[] configured = await database.Database.SqlQuery<DestinationRoutingSnapshotRow>($"""
            SELECT destination."Id" AS "DestinationId", destination."Enabled" AS "Enabled",
                   COALESCE(routing."RulesJson"::text, '[]') AS "RulesJson"
            FROM destinations destination
            LEFT JOIN destination_routings routing ON routing."DestinationId" = destination."Id"
            WHERE NOT destination."IsArchived"
            ORDER BY destination."Id"
            """).ToArrayAsync(cancellationToken);
        DestinationRouting[] snapshot = [.. configured.Select(config => new DestinationRouting(config.DestinationId,
            config.Enabled, RoutingRuleJson.ReadRules(config.RulesJson)))];
        capture.RoutingSnapshotJson = RoutingRuleJson.WriteSnapshot(snapshot);
    }

    private Task<int> LockRoutingAsync(CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);

    private Task<int> LockCollectionAssignmentsAsync(CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-collection-assignment', 0))", cancellationToken);

    private sealed class DestinationRoutingSnapshotRow
    {
        public Guid DestinationId { get; set; }
        public bool Enabled { get; set; }
        public string RulesJson { get; set; } = "[]";
    }

    private static CaptureCurationState ToState(CaptureEntity capture) => new(
        capture.Id, capture.Revision, capture.Rating, capture.Label, [.. capture.Tags], capture.Selection,
        capture.Policy, capture.DelaySeconds, capture.ReleaseDueAt, capture.Held,
        capture.State.ToString(), capture.ReleasedAt, capture.ReleasedRevision, capture.ReleasedRating, capture.ReleasedLabel, [.. capture.ReleasedTags],
        capture.ReleasedSelection);
}
