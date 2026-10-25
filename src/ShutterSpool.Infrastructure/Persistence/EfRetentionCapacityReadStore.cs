using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Retention;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfRetentionCapacityReadStore(ShutterSpoolDbContext database, ISpoolQuotaStore quotaStore,
    TimeProvider clock) : IRetentionCapacityReadStore
{
    public async Task<RetentionCapacitySnapshot> GetCapacityAsync(CancellationToken cancellationToken)
    {
        SpoolQuotaSnapshot quota = await quotaStore.GetSnapshotAsync(cancellationToken);
        DateTimeOffset now = clock.GetUtcNow();
        CapacitySubsets subsets = await ReadCapacitySubsetsAsync(now, cancellationToken);
        long plannedBytes = await ReadPlannedBytesAsync(cancellationToken);
        DateTimeOffset? nextPurge = await ReadNextPurgeAsync(cancellationToken);
        string pressure = await ReadPressureAsync(cancellationToken);
        long removedBytes = await ReadRemovedBytesAsync(cancellationToken);
        return BuildCapacitySnapshot(quota, subsets, pressure, plannedBytes, removedBytes, nextPurge);
    }

    private async Task<CapacitySubsets> ReadCapacitySubsetsAsync(DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        long favoriteBytes = await (from member in database.CaptureAssets.AsNoTracking()
            join capture in database.Captures.AsNoTracking() on member.CaptureId equals capture.Id
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where capture.IsFavorite && asset.State == "Spooled"
            select (long?)asset.Size).SumAsync(cancellationToken) ?? 0;
        // EXISTS counts each physical original once even when several promises protect it.
        long protectedBytes = await database.Assets.AsNoTracking().Where(asset => asset.State == "Spooled"
            && (database.CaptureAssets.Any(member => member.AssetId == asset.Id
                && database.Captures.Any(capture => capture.Id == member.CaptureId
                    && (capture.RestoreProtectedUntil > now
                        || capture.RetentionDisposition == "Trash" && capture.RecoverableUntil > now)))
                || database.GalleryProtectionAssets.Any(member => member.AssetId == asset.Id
                    && database.GalleryProtections.Any(promise => promise.Id == member.ProtectionId
                        && promise.ExpiresAt > now))
                || database.PreservationHoldAssets.Any(member => member.AssetId == asset.Id
                    && database.PreservationHolds.Any(hold => hold.Id == member.HoldId && hold.ResolvedAt == null))))
            .SumAsync(asset => (long?)asset.Size, cancellationToken) ?? 0;
        return new(favoriteBytes, protectedBytes);
    }

    private async Task<long> ReadPlannedBytesAsync(CancellationToken cancellationToken) =>
        await (from member in database.CaptureAssets.AsNoTracking()
            join capture in database.Captures.AsNoTracking() on member.CaptureId equals capture.Id
            join asset in database.Assets.AsNoTracking() on member.AssetId equals asset.Id
            where capture.RetentionDisposition == "Trash" && asset.State == "Spooled"
                && !database.AssetRemovalOperations.Any(operation => operation.AssetId == asset.Id && operation.State == "Removed")
            select (long?)asset.Size).SumAsync(cancellationToken) ?? 0;

    private async Task<DateTimeOffset?> ReadNextPurgeAsync(CancellationToken cancellationToken) =>
        await database.Database.SqlQuery<DateTimeOffset?>($"""
            SELECT MIN(GREATEST(c."RecoverableUntil",
                COALESCE(c."RestoreProtectedUntil", c."RecoverableUntil"),
                COALESCE((SELECT MAX(gp."ExpiresAt")
                    FROM gallery_protection_assets gpa
                    JOIN gallery_protections gp ON gp."Id" = gpa."ProtectionId"
                    JOIN capture_assets ca ON ca."AssetId" = gpa."AssetId"
                    WHERE ca."CaptureId" = c."Id"), c."RecoverableUntil"))) AS "Value"
            FROM captures c
            WHERE c."RetentionDisposition" = 'Trash' AND c."RecoverableUntil" IS NOT NULL
                AND NOT EXISTS (
                    SELECT 1 FROM capture_assets ca
                    JOIN preservation_hold_assets pha ON pha."AssetId" = ca."AssetId"
                    JOIN preservation_holds ph ON ph."Id" = pha."HoldId"
                    WHERE ca."CaptureId" = c."Id" AND ph."ResolvedAt" IS NULL)
                AND NOT EXISTS (
                    SELECT 1 FROM capture_assets ca
                    LEFT JOIN assets a ON a."Id" = ca."AssetId"
                    WHERE ca."CaptureId" = c."Id" AND (a."Id" IS NULL OR a."State" <> 'Spooled'))
                AND NOT EXISTS (
                    SELECT 1 FROM capture_assets ca
                    JOIN deliveries d ON d."AssetId" = ca."AssetId"
                    WHERE ca."CaptureId" = c."Id" AND d."State" = 'Running')
            """).SingleOrDefaultAsync(cancellationToken);

    private async Task<string> ReadPressureAsync(CancellationToken cancellationToken) =>
        await database.RetentionPressureStates.AsNoTracking().Where(state => state.Id == 1)
            .Select(state => state.State).SingleOrDefaultAsync(cancellationToken) ?? "Unknown";

    private async Task<long> ReadRemovedBytesAsync(CancellationToken cancellationToken) =>
        await database.AssetRemovalOperations.AsNoTracking()
            .SumAsync(operation => (long?)operation.RemovedBytes, cancellationToken) ?? 0;

    private static RetentionCapacitySnapshot BuildCapacitySnapshot(SpoolQuotaSnapshot quota,
        CapacitySubsets subsets, string pressure, long plannedBytes, long removedBytes, DateTimeOffset? nextPurge)
    {
        var blockers = new List<string>();
        bool admissionBlocked = quota.AdmissionBlockedReason is not null
            && (quota.Settings.IsActive || quota.AdmissionBlockedReason != "unknown_accounting");
        if (!quota.IsKnown) blockers.Add("accounting-unknown");
        if (!quota.Settings.IsActive) blockers.Add("quota-not-active");
        if (admissionBlocked) blockers.Add("admission-blocked");
        if (subsets.ProtectedBytes > 0) blockers.Add("protected-occupancy");
        return new RetentionCapacitySnapshot(quota.ObservedAt, quota.Settings.QuotaBytes,
            quota.Settings.HighWatermarkBytes, quota.Settings.RecoveryTargetBytes, quota.UsedBytes,
            quota.ReservedBytes, quota.ActiveBytes, quota.TrashBytes, quota.PartialBytes,
            quota.QuarantineBytes, quota.DiskPreviewBytes, subsets.FavoriteBytes, subsets.ProtectedBytes, pressure,
            admissionBlocked, !quota.IsKnown, [.. blockers], plannedBytes, removedBytes, nextPurge);
    }

    public async Task<RetentionHistoryPage> GetHistoryAsync(int pageSize, string? cursor,
        CancellationToken cancellationToken)
    {
        HistoryCursor parsedCursor = ParseHistoryCursor(cursor);
        DateTimeOffset? prunedBefore = await database.RetentionPressureStates.AsNoTracking()
            .Where(state => state.Id == 1).Select(state => state.HistoryPrunedBefore)
            .SingleOrDefaultAsync(cancellationToken);
        bool hasGap = HasPrunedHistoryGap(parsedCursor.Before, prunedBefore);
        if (parsedCursor.Before is DateTimeOffset cursorTime && !hasGap)
            hasGap = !await HistoryEventExistsAsync(parsedCursor.EventId, cursorTime, cancellationToken);

        RetentionHistoryEntity[] rows = await ReadHistoryPageAsync(pageSize, parsedCursor, cancellationToken);
        bool hasMore = rows.Length > pageSize;
        if (parsedCursor.Before is not null && prunedBefore is not null && !hasMore) hasGap = true;
        return BuildHistoryPage(rows, pageSize, hasGap, hasMore);
    }

    private async Task<bool> HistoryEventExistsAsync(Guid eventId, DateTimeOffset committedAt,
        CancellationToken cancellationToken) => await database.RetentionHistory.AsNoTracking()
            .AnyAsync(item => item.EventId == eventId && item.CommittedAt == committedAt, cancellationToken);

    private async Task<RetentionHistoryEntity[]> ReadHistoryPageAsync(int pageSize, HistoryCursor cursor,
        CancellationToken cancellationToken)
    {
        IQueryable<RetentionHistoryEntity> query = database.RetentionHistory.AsNoTracking();
        if (cursor.Before is DateTimeOffset boundary)
            query = query.Where(item => item.CommittedAt < boundary
                || item.CommittedAt == boundary && item.EventId.CompareTo(cursor.EventId) < 0);
        return await query.OrderByDescending(item => item.CommittedAt)
            .ThenByDescending(item => item.EventId).Take(pageSize + 1).ToArrayAsync(cancellationToken);
    }

    private static RetentionHistoryPage BuildHistoryPage(RetentionHistoryEntity[] rows, int pageSize,
        bool hasGap, bool hasMore)
    {
        RetentionHistoryEntity[] selected = [.. rows.Take(pageSize)];
        RetentionHistoryEntity? last = hasMore ? selected[^1] : null;
        string? next = last is null ? null : FormatHistoryCursor(last);
        RetentionHistoryItem[] items = [.. selected.Select(item => new RetentionHistoryItem(item.EventId, item.EpisodeId,
            item.Revision, item.ObservedAt, item.CommittedAt, item.Kind, item.State, item.UsedBytes,
            item.ReservedBytes, item.PlannedReclamationBytes, item.PurgedBytes, item.Blockers))];
        return new(items, next, hasGap);
    }

    private static bool HasPrunedHistoryGap(DateTimeOffset? before, DateTimeOffset? prunedBefore) =>
        before is DateTimeOffset requestedBefore && prunedBefore is DateTimeOffset cutoff
            && requestedBefore <= cutoff;

    private static HistoryCursor ParseHistoryCursor(string? cursor)
    {
        if (cursor is null) return new(null, Guid.Empty);
        if (cursor.Length > 128) throw new ArgumentException("The retention history cursor is invalid.", nameof(cursor));
        try
        {
            string decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(cursor.Replace('-', '+')
                .Replace('_', '/').PadRight((cursor.Length + 3) / 4 * 4, '=')));
            string[] parts = decoded.Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out long ticks)
                || !Guid.TryParseExact(parts[1], "N", out Guid eventId)) throw new FormatException();
            return new(new DateTimeOffset(ticks, TimeSpan.Zero), eventId);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new ArgumentException("The retention history cursor is invalid.", nameof(cursor));
        }
    }

    private static string FormatHistoryCursor(RetentionHistoryEntity item) => Convert.ToBase64String(
        System.Text.Encoding.UTF8.GetBytes($"{item.CommittedAt.UtcDateTime.Ticks}:{item.EventId:N}"))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record CapacitySubsets(long FavoriteBytes, long ProtectedBytes);
    private sealed record HistoryCursor(DateTimeOffset? Before, Guid EventId);
}
