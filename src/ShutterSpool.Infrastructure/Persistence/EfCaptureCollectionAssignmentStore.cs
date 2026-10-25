using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Collections;
using ShutterSpool.Application.Captures;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCaptureCollectionAssignmentStore(ShutterSpoolDbContext database) : ICaptureCollectionAssignmentStore
{
    public Task<bool> ExistsAsync(Guid collectionId, CancellationToken token) => database.Collections.AsNoTracking()
        .AnyAsync(item => item.Id == collectionId, token);

    public async Task<CollectionMoveResult> MoveAsync(Guid captureId, int expectedRevision, Guid collectionId, CancellationToken token)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(token);
        await LockAssignmentsAsync(token);
        if (!await database.Collections.AnyAsync(item => item.Id == collectionId, token))
        {
            await transaction.CommitAsync(token);
            return new(captureId, CollectionMoveStatus.TargetMissing, null, null);
        }
        // Curation and retention also mutate Captures without taking the Collection-assignment lock.
        CaptureEntity? capture = await database.Captures.FromSqlInterpolated(
            $"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE").SingleOrDefaultAsync(token);
        if (capture is null)
        {
            await transaction.CommitAsync(token);
            return new(captureId, CollectionMoveStatus.NotFound, null, null);
        }
        if (capture.RetentionDisposition != "Active"
            || await database.CaptureAssets.AnyAsync(member => member.CaptureId == captureId
                && database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId), token))
        {
            CaptureRead? current = await ReadCurrentAsync(captureId, token);
            await transaction.CommitAsync(token);
            return new(captureId, CollectionMoveStatus.NotMovable, capture.Revision, capture.CollectionId, current);
        }
        if (capture.Revision != expectedRevision)
        {
            CaptureRead? current = await ReadCurrentAsync(captureId, token);
            await transaction.CommitAsync(token);
            return new(captureId, CollectionMoveStatus.Conflict, capture.Revision, capture.CollectionId, current);
        }
        if (capture.CollectionId != collectionId)
        {
            capture.CollectionId = collectionId;
            capture.Revision++;
            capture.RetentionFactsDirty = true;
            await database.SaveChangesAsync(token);
        }
        CaptureRead? committed = await ReadCurrentAsync(captureId, token);
        await transaction.CommitAsync(token);
        return new(captureId, CollectionMoveStatus.Applied, capture.Revision, capture.CollectionId, committed);
    }

    private Task<CaptureRead?> ReadCurrentAsync(Guid captureId, CancellationToken token) =>
        new CaptureReadStore(database).GetAsync(captureId, token);

    private Task<int> LockAssignmentsAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-collection-assignment', 0))", token);
}
