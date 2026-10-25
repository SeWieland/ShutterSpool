using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Preview;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.Infrastructure.Preview;

internal sealed class EfPreviewPrewarmStore(ShutterSpoolDbContext database) : IPreviewPrewarmStore
{
    public async Task<PreviewPrewarmWork?> ClaimAsync(PreviewSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (settings.PrewarmMode == PreviewPrewarmMode.Off) return null;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // One background generation across replicas leaves Imaginary capacity for interactive misses.
        bool coordinator = await database.Database.SqlQueryRaw<bool>(
            "SELECT pg_try_advisory_xact_lock(hashtextextended('shutterspool-preview-prewarm', 0)) AS \"Value\"")
            .SingleAsync(cancellationToken);
        if (!coordinator || await database.PreviewPrewarms.AnyAsync(
                job => job.State == "Processing" && job.LeaseUntil > now, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        PreviewPrewarmEntity? candidate = await database.PreviewPrewarms.FromSqlInterpolated($"""
            SELECT job.* FROM preview_prewarms AS job
            JOIN capture_assets AS membership ON membership."AssetId" = job."AssetId"
            JOIN captures AS capture ON capture."Id" = membership."CaptureId"
            JOIN assets AS asset ON asset."Id" = job."AssetId"
            WHERE membership."IsPrimary" AND membership."Role" = 'JPEG' AND asset."State" = 'Spooled'
              AND capture."RetentionDisposition" = 'Active' AND NOT capture."RetentionPaused"
              AND ({settings.PrewarmMode.ToString()} = 'Full' OR job."Profile" = 'Grid')
              AND (job."State" = 'Pending' AND (job."RetryAt" IS NULL OR job."RetryAt" <= {now})
                OR job."State" = 'Processing' AND job."LeaseUntil" <= {now}
                OR job."State" IN ('Complete', 'Invalid') AND (
                    job."CompletedWidth" IS DISTINCT FROM CASE WHEN job."Profile" = 'Grid' THEN {settings.GridWidth} ELSE {settings.DetailWidth} END
                    OR job."CompletedHeight" IS DISTINCT FROM CASE WHEN job."Profile" = 'Grid' THEN {settings.GridHeight} ELSE {settings.DetailHeight} END))
            ORDER BY CASE WHEN job."Profile" = 'Grid' THEN 0 ELSE 1 END, asset."IngestedAt" DESC
            FOR UPDATE OF job SKIP LOCKED
            LIMIT 1
            """).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (candidate is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        PreviewProfile profile = Enum.Parse<PreviewProfile>(candidate.Profile);
        PreviewSize size = profile == PreviewProfile.Grid
            ? new(settings.GridWidth, settings.GridHeight)
            : new(settings.DetailWidth, settings.DetailHeight);
        bool changedSize = candidate.CompletedWidth != size.Width || candidate.CompletedHeight != size.Height;
        int attempts = changedSize ? 1 : candidate.AttemptCount + 1;
        Guid leaseId = Guid.NewGuid();
        TimeSpan leaseDuration = TimeSpan.FromSeconds(
            settings.GenerationQueueTimeoutSeconds + settings.GenerationTimeoutSeconds + 120);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE preview_prewarms
            SET "State" = 'Processing', "AttemptCount" = {attempts}, "LeaseId" = {leaseId},
                "LeaseUntil" = {now + leaseDuration}, "RetryAt" = NULL
            WHERE "AssetId" = {candidate.AssetId} AND "Profile" = {candidate.Profile}
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(candidate.AssetId, candidate.CaptureId, profile, leaseId, attempts, size);
    }

    public async Task<bool> FinishAsync(PreviewPrewarmWork work, PreviewWarmOutcome outcome, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string state = outcome switch
        {
            PreviewWarmOutcome.Ready => "Complete",
            PreviewWarmOutcome.Retry => "Pending",
            PreviewWarmOutcome.Invalid => "Invalid",
            PreviewWarmOutcome.Obsolete => "Obsolete",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        DateTimeOffset? retryAt = outcome == PreviewWarmOutcome.Retry ? now + Backoff(work.AttemptCount) : null;
        return await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE preview_prewarms
            SET "State" = {state}, "LeaseId" = NULL, "LeaseUntil" = NULL,
                "RetryAt" = {retryAt}, "CompletedWidth" = {work.Size.Width}, "CompletedHeight" = {work.Size.Height}
            WHERE "AssetId" = {work.AssetId} AND "Profile" = {work.Profile.ToString()}
              AND "State" = 'Processing' AND "LeaseId" = {work.LeaseId}
            """, cancellationToken) == 1;
    }

    private static TimeSpan Backoff(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromSeconds(5),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(2),
        4 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(30),
    };
}
