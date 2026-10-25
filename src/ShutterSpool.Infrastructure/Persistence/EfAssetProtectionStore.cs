using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Retention;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfAssetProtectionStore(ShutterSpoolDbContext database) : IAssetProtectionStore
{
    public async Task<GalleryProtection?> GetGalleryAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await database.GalleryProtections.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken))
            return null;
        return await GalleryStateAsync(id, cancellationToken);
    }

    public async Task<PreservationHold?> GetPreservationAsync(Guid id, CancellationToken cancellationToken)
    {
        if (!await database.PreservationHolds.AsNoTracking().AnyAsync(item => item.Id == id, cancellationToken))
            return null;
        return await PreservationStateAsync(id, cancellationToken);
    }

    public Task<ProtectionOriginal[]?> GetAvailableOriginalsAsync(Guid[] assetIds,
        CancellationToken cancellationToken) => ReadAvailableOriginalsAsync(assetIds, cancellationToken);

    public async Task<ProtectionOriginal[]?> GetGalleryOriginalsAsync(Guid galleryProtectionId,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var protection = await database.GalleryProtections.AsNoTracking()
            .Where(item => item.Id == galleryProtectionId && item.ExpiresAt > now)
            .Select(item => new { item.Id }).SingleOrDefaultAsync(cancellationToken);
        if (protection is null) return null;
        Guid[] members = await database.GalleryProtectionAssets.AsNoTracking()
            .Where(item => item.ProtectionId == galleryProtectionId).Select(item => item.AssetId)
            .OrderBy(id => id).ToArrayAsync(cancellationToken);
        return await ReadAvailableOriginalsAsync(members, cancellationToken) ?? [];
    }

    public Task<ProtectionOperation<GalleryProtection>> CreateGalleryAsync(ProtectionOriginal[] originals,
        DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken) =>
        CreateProtectionAsync<GalleryProtection>(originals, now, async (_, token) =>
        {
            var protection = new GalleryProtectionEntity
            {
                Id = Guid.NewGuid(), CreatedAt = now, ExpiresAt = expiresAt, Revision = 0,
            };
            database.GalleryProtections.Add(protection);
            database.GalleryProtectionAssets.AddRange(originals.Select(item => new GalleryProtectionAssetEntity
            {
                ProtectionId = protection.Id, AssetId = item.AssetId,
            }));
            await database.SaveChangesAsync(token);
            return new(ProtectionOperationStatus.Committed, await GalleryStateAsync(protection.Id, token));
        }, cancellationToken);

    public async Task<ProtectionOperation<GalleryProtection>> ExtendGalleryAsync(Guid id, int expectedRevision,
        ProtectionOriginal[] originals, DateTimeOffset expiresAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return await WithProtectionTransactionAsync<GalleryProtection>(originals, now, async (_, token) =>
        {
            GalleryProtectionEntity? protection = await database.GalleryProtections
                .FromSqlInterpolated($"SELECT * FROM gallery_protections WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(token);
            if (protection is null) return new(ProtectionOperationStatus.NotFound, ErrorCode: "GALLERY_PROTECTION_NOT_FOUND");
            if (protection.Revision != expectedRevision)
                return new(ProtectionOperationStatus.Conflict, await GalleryStateAsync(id, token), "REVISION_CONFLICT");
            if (protection.ExpiresAt <= now || expiresAt <= protection.ExpiresAt)
                return new(ProtectionOperationStatus.Conflict, await GalleryStateAsync(id, token), "GALLERY_PROMISE_NOT_EXTENDABLE");
            if (!await MembershipMatchesAsync(database.GalleryProtectionAssets.Where(item => item.ProtectionId == id)
                    .Select(item => item.AssetId), originals, token))
                return new(ProtectionOperationStatus.Conflict, await GalleryStateAsync(id, token), "MEMBERSHIP_CHANGED");

            protection.ExpiresAt = expiresAt;
            protection.Revision = checked(protection.Revision + 1);
            await database.SaveChangesAsync(token);
            return new(ProtectionOperationStatus.Committed, await GalleryStateAsync(id, token));
        }, cancellationToken);
    }

    public async Task<ProtectionOperation<GalleryProtection>> RevokeGalleryAccessAsync(Guid id,
        int expectedRevision, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity[] affected = await LockProtectionCapturesAsync(
            database.GalleryProtectionAssets.Where(member => member.ProtectionId == id)
                .Select(member => member.AssetId), cancellationToken);
        GalleryProtectionEntity? protection = await database.GalleryProtections
            .FromSqlInterpolated($"SELECT * FROM gallery_protections WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (protection is null) return await FinishAsync(transaction,
            new ProtectionOperation<GalleryProtection>(ProtectionOperationStatus.NotFound,
                ErrorCode: "GALLERY_PROTECTION_NOT_FOUND"), cancellationToken);
        if (protection.Revision != expectedRevision)
            return await FinishAsync(transaction, new ProtectionOperation<GalleryProtection>(ProtectionOperationStatus.Conflict,
                await GalleryStateAsync(id, cancellationToken), "REVISION_CONFLICT"), cancellationToken);
        if (protection.AccessRevokedAt is null)
        {
            protection.AccessRevokedAt = now;
            protection.Revision = checked(protection.Revision + 1);
            MarkRetentionFactsDirty(affected);
            await database.SaveChangesAsync(cancellationToken);
        }
        // Access revocation deliberately leaves ExpiresAt unchanged; its promise remains protected until expiry.
        return await FinishAsync(transaction, new ProtectionOperation<GalleryProtection>(ProtectionOperationStatus.Committed,
            await GalleryStateAsync(id, cancellationToken)), cancellationToken);
    }

    public Task<ProtectionOperation<PreservationHold>> CreatePreservationAsync(ProtectionOriginal[] originals,
        string authority, string reason, DateTimeOffset reviewAt, DateTimeOffset now,
        CancellationToken cancellationToken) => CreateProtectionAsync<PreservationHold>(originals, now, async (_, token) =>
        {
            var hold = new PreservationHoldEntity
            {
                Id = Guid.NewGuid(), Revision = 0, Authority = authority, Reason = reason,
                CreatedAt = now, ReviewAt = reviewAt,
            };
            database.PreservationHolds.Add(hold);
            database.PreservationHoldAssets.AddRange(originals.Select(item => new PreservationHoldAssetEntity
            {
                HoldId = hold.Id, AssetId = item.AssetId,
            }));
            await database.SaveChangesAsync(token);
            return new(ProtectionOperationStatus.Committed, await PreservationStateAsync(hold.Id, token));
        }, cancellationToken);

    public async Task<ProtectionOperation<PreservationHold>> ResolvePreservationAsync(Guid id,
        int expectedRevision, string resolvedBy, string resolution, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        CaptureEntity[] affected = await LockProtectionCapturesAsync(
            database.PreservationHoldAssets.Where(member => member.HoldId == id)
                .Select(member => member.AssetId), cancellationToken);
        PreservationHoldEntity? hold = await database.PreservationHolds
            .FromSqlInterpolated($"SELECT * FROM preservation_holds WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (hold is null) return await FinishAsync(transaction,
            new ProtectionOperation<PreservationHold>(ProtectionOperationStatus.NotFound,
                ErrorCode: "PRESERVATION_HOLD_NOT_FOUND"), cancellationToken);
        if (hold.Revision != expectedRevision)
            return await FinishAsync(transaction, new ProtectionOperation<PreservationHold>(ProtectionOperationStatus.Conflict,
                await PreservationStateAsync(id, cancellationToken), "REVISION_CONFLICT"), cancellationToken);
        if (hold.ResolvedAt is not null)
            return await FinishAsync(transaction, new ProtectionOperation<PreservationHold>(ProtectionOperationStatus.Conflict,
                await PreservationStateAsync(id, cancellationToken), "PRESERVATION_ALREADY_RESOLVED"), cancellationToken);

        hold.ResolvedAt = now;
        hold.ResolvedBy = resolvedBy;
        hold.Resolution = resolution;
        hold.Revision = checked(hold.Revision + 1);
        MarkRetentionFactsDirty(affected);
        await database.SaveChangesAsync(cancellationToken);
        return await FinishAsync(transaction, new ProtectionOperation<PreservationHold>(ProtectionOperationStatus.Committed,
            await PreservationStateAsync(id, cancellationToken)), cancellationToken);
    }

    private async Task<ProtectionOperation<T>> CreateProtectionAsync<T>(ProtectionOriginal[] originals,
        DateTimeOffset now, Func<CaptureEntity[], CancellationToken, Task<ProtectionOperation<T>>> create,
        CancellationToken cancellationToken)
    {
        return await WithProtectionTransactionAsync<T>(originals, now, create, cancellationToken);
    }

    private async Task<ProtectionOperation<T>> WithProtectionTransactionAsync<T>(ProtectionOriginal[] originals,
        DateTimeOffset now, Func<CaptureEntity[], CancellationToken, Task<ProtectionOperation<T>>> operation,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        Guid[] captures = [.. originals.Select(item => item.CaptureId).Distinct().OrderBy(id => id)];
        var lockedCaptures = new List<CaptureEntity>(captures.Length);
        foreach (Guid captureId in captures)
        {
            CaptureEntity? capture = await database.Captures.FromSqlInterpolated(
                $"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (capture is null || capture.RetentionDisposition is not ("Active" or "Trash"))
                return await FinishAsync(transaction, new ProtectionOperation<T>(ProtectionOperationStatus.Blocked,
                    ErrorCode: "ORIGINAL_UNAVAILABLE"), cancellationToken);
            lockedCaptures.Add(capture);
        }
        if (!await RecheckOriginalsAsync(originals, cancellationToken))
            return await FinishAsync(transaction, new ProtectionOperation<T>(ProtectionOperationStatus.Blocked,
                ErrorCode: "ORIGINAL_UNAVAILABLE"), cancellationToken);

        ProtectionOperation<T> result = await operation([.. lockedCaptures], cancellationToken);
        if (result.Status == ProtectionOperationStatus.Committed)
        {
            MarkRetentionFactsDirty(lockedCaptures);
            await database.SaveChangesAsync(cancellationToken);
        }
        return await FinishAsync(transaction, result, cancellationToken);
    }

    private async Task<bool> RecheckOriginalsAsync(ProtectionOriginal[] originals,
        CancellationToken cancellationToken)
    {
        if (originals.Length == 0) return false;
        Guid[] ids = [.. originals.Select(item => item.AssetId).OrderBy(id => id)];
        var current = await (from asset in database.Assets.AsNoTracking()
            join membership in database.CaptureAssets.AsNoTracking() on asset.Id equals membership.AssetId
            join capture in database.Captures.AsNoTracking() on membership.CaptureId equals capture.Id
            where ids.Contains(asset.Id)
            select new { asset.Id, CaptureId = capture.Id, asset.SpoolKey, asset.Size, asset.State,
                capture.RetentionDisposition,
                HasRemoval = database.AssetRemovalOperations.Any(removal => removal.AssetId == asset.Id),
                HasErasure = database.PrivacyErasureAssets.Any(erased => erased.AssetId == asset.Id) })
            .ToArrayAsync(cancellationToken);
        if (current.Length != ids.Length || current.Any(item => item.State != "Spooled"
                || item.RetentionDisposition is not ("Active" or "Trash") || item.HasRemoval || item.HasErasure)) return false;

        var expected = originals.OrderBy(item => item.AssetId).ToArray();
        var actual = current.OrderBy(item => item.Id).ToArray();
        return expected.Zip(actual).All(pair => pair.First.AssetId == pair.Second.Id
            && pair.First.CaptureId == pair.Second.CaptureId && pair.First.SpoolKey == pair.Second.SpoolKey
            && pair.First.Size == pair.Second.Size);
    }

    private async Task<ProtectionOriginal[]?> ReadAvailableOriginalsAsync(Guid[] assetIds,
        CancellationToken cancellationToken)
    {
        if (assetIds.Length == 0 || assetIds.Distinct().Count() != assetIds.Length) return null;
        var originals = await (from asset in database.Assets.AsNoTracking()
            join membership in database.CaptureAssets.AsNoTracking() on asset.Id equals membership.AssetId
            join capture in database.Captures.AsNoTracking() on membership.CaptureId equals capture.Id
            where assetIds.Contains(asset.Id)
            select new { asset.Id, CaptureId = capture.Id, asset.SpoolKey, asset.Size, asset.State,
                capture.RetentionDisposition,
                HasRemoval = database.AssetRemovalOperations.Any(removal => removal.AssetId == asset.Id),
                HasErasure = database.PrivacyErasureAssets.Any(erased => erased.AssetId == asset.Id) })
            .ToArrayAsync(cancellationToken);
        if (originals.Length != assetIds.Length || originals.Any(item => item.State != "Spooled"
                || item.RetentionDisposition is not ("Active" or "Trash") || item.HasRemoval || item.HasErasure)) return null;
        return [.. originals.Select(item => new ProtectionOriginal(item.Id, item.CaptureId, item.SpoolKey, item.Size)).OrderBy(item => item.AssetId)];
    }

    private static async Task<bool> MembershipMatchesAsync(IQueryable<Guid> existingAssetIds,
        ProtectionOriginal[] originals, CancellationToken cancellationToken)
    {
        Guid[] existing = await existingAssetIds.OrderBy(id => id).ToArrayAsync(cancellationToken);
        Guid[] requested = [.. originals.Select(item => item.AssetId).OrderBy(id => id)];
        return existing.SequenceEqual(requested);
    }

    private async Task<GalleryProtection> GalleryStateAsync(Guid id, CancellationToken cancellationToken)
    {
        GalleryProtectionEntity protection = await database.GalleryProtections.AsNoTracking()
            .SingleAsync(item => item.Id == id, cancellationToken);
        Guid[] assetIds = await database.GalleryProtectionAssets.AsNoTracking()
            .Where(member => member.ProtectionId == id).Select(member => member.AssetId)
            .OrderBy(assetId => assetId).ToArrayAsync(cancellationToken);
        return new(protection.Id, protection.Revision, assetIds, protection.CreatedAt,
            protection.ExpiresAt, protection.AccessRevokedAt);
    }

    private async Task<PreservationHold> PreservationStateAsync(Guid id, CancellationToken cancellationToken)
    {
        PreservationHoldEntity hold = await database.PreservationHolds.AsNoTracking()
            .SingleAsync(item => item.Id == id, cancellationToken);
        Guid[] assetIds = await database.PreservationHoldAssets.AsNoTracking()
            .Where(member => member.HoldId == id).Select(member => member.AssetId)
            .OrderBy(assetId => assetId).ToArrayAsync(cancellationToken);
        return new(hold.Id, hold.Revision, assetIds, hold.Authority, hold.Reason,
            hold.CreatedAt, hold.ReviewAt, hold.ResolvedAt, hold.ResolvedBy, hold.Resolution);
    }

    private async Task<CaptureEntity[]> LockProtectionCapturesAsync(IQueryable<Guid> assetIds,
        CancellationToken cancellationToken)
    {
        Guid[] captureIds = await database.CaptureAssets
            .Where(member => assetIds.Contains(member.AssetId))
            .Select(member => member.CaptureId).Distinct().OrderBy(id => id).ToArrayAsync(cancellationToken);
        var captures = new List<CaptureEntity>(captureIds.Length);
        foreach (Guid captureId in captureIds)
        {
            CaptureEntity? capture = await database.Captures.FromSqlInterpolated(
                $"SELECT * FROM captures WHERE \"Id\" = {captureId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (capture is not null) captures.Add(capture);
        }
        return [.. captures];
    }

    private static void MarkRetentionFactsDirty(IEnumerable<CaptureEntity> captures)
    {
        foreach (CaptureEntity capture in captures)
            if (capture.RetentionDisposition == "Active") capture.RetentionFactsDirty = true;
    }

    private Task<int> LockRoutingAsync(CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);

    private static async Task<ProtectionOperation<T>> FinishAsync<T>(IDbContextTransaction transaction,
        ProtectionOperation<T> result, CancellationToken cancellationToken)
    {
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}
