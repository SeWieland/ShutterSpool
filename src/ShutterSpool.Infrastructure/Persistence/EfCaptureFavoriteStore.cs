using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Retention;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCaptureFavoriteStore(ShutterSpoolDbContext database) : ICaptureFavoriteStore
{
    public async Task<CaptureFavoriteState?> GetAsync(Guid captureId, CancellationToken cancellationToken)
    {
        var capture = await database.Captures.AsNoTracking()
            .Where(item => item.Id == captureId && !database.CaptureAssets.Any(member =>
                member.CaptureId == item.Id && database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId)))
            .Select(item => new { item.Id, item.Revision, item.IsFavorite, item.RetentionDisposition })
            .SingleOrDefaultAsync(cancellationToken);
        return capture is null ? null : new(capture.Id, capture.Revision, capture.IsFavorite, capture.RetentionDisposition);
    }

    public async Task<CaptureFavoriteMutation> SetAsync(Guid captureId, int expectedRevision, bool isFavorite,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
        CaptureEntity? capture = await database.Captures.FromSqlInterpolated(
            $"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (capture is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, null, "CAPTURE_NOT_FOUND");
        }

        if (await database.PrivacyErasureAssets.AnyAsync(erased => database.CaptureAssets.Any(member =>
                member.CaptureId == captureId && member.AssetId == erased.AssetId), cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, null, "CAPTURE_NOT_FOUND");
        }

        if (capture.Revision != expectedRevision)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, State(capture), "REVISION_CONFLICT");
        }
        if (capture.RetentionDisposition != "Active")
        {
            await transaction.CommitAsync(cancellationToken);
            return new(false, State(capture), "RETENTION_TRANSITION_IN_PROGRESS");
        }

        if (capture.IsFavorite != isFavorite)
        {
            capture.IsFavorite = isFavorite;
            capture.RetentionFactsDirty = true;
            capture.Revision = checked(capture.Revision + 1);
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return new(true, State(capture));
    }

    private static CaptureFavoriteState State(CaptureEntity capture) =>
        new(capture.Id, capture.Revision, capture.IsFavorite, capture.RetentionDisposition);
}
