using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Routing;
using ShutterSpool.Application.Spool;
using System.Security.Cryptography;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfVerifiedOriginalSupplier(ShutterSpoolDbContext database, ICommittedOriginalReader originals) : IVerifiedOriginalSupplier
{
    public async Task<Guid?> FindAsync(Guid assetId, Guid? preferredSupplierId, CancellationToken cancellationToken)
    {
        var original = await database.Assets.AsNoTracking().Where(item => item.Id == assetId)
            .Select(item => new SpoolAsset(item.SpoolKey, item.Size, item.Sha256, item.State)).SingleOrDefaultAsync(cancellationToken);
        if (original is null) return null;
        var identity = new OriginalBytes(original.Size, original.Sha256);
        if (preferredSupplierId is Guid preferred && preferred != assetId
            && await SupplierMatchesOriginalAsync(preferred, identity, cancellationToken)) return preferred;
        if (await BytesAvailableAsync(original, cancellationToken)) return assetId;

        Guid[] replayIds = await (from replay in database.CaptureAssets.AsNoTracking()
            join primary in database.CaptureAssets.AsNoTracking() on new { replay.CaptureId, replay.RelatedAssetId, replay.Role }
                equals new { primary.CaptureId, RelatedAssetId = (Guid?)primary.AssetId, primary.Role }
            join replayAsset in database.Assets.AsNoTracking() on replay.AssetId equals replayAsset.Id
            where primary.AssetId == assetId && primary.IsPrimary && !replay.IsPrimary
                && replay.GroupingReason == "recognized_replay" && replayAsset.State == "Spooled"
                && replayAsset.Size == original.Size && replayAsset.Sha256 == original.Sha256
            select replayAsset.Id).ToArrayAsync(cancellationToken);
        foreach (Guid replayId in replayIds)
            if (await SupplierMatchesOriginalAsync(replayId, identity, cancellationToken)) return replayId;
        return null;
    }

    private async Task<bool> SupplierMatchesOriginalAsync(Guid supplierId, OriginalBytes original, CancellationToken cancellationToken)
    {
        var supplier = await database.Assets.AsNoTracking().Where(item => item.Id == supplierId && item.State == "Spooled")
            .Select(item => new SpoolAsset(item.SpoolKey, item.Size, item.Sha256, item.State)).SingleOrDefaultAsync(cancellationToken);
        return supplier is not null && supplier.Size == original.Size && supplier.Sha256 == original.Sha256
            && await BytesAvailableAsync(supplier, cancellationToken);
    }

    private async Task<bool> BytesAvailableAsync(SpoolAsset asset, CancellationToken cancellationToken)
    {
        if (asset.State != "Spooled") return false;
        try
        {
            await using Stream stream = await originals.OpenReadAsync(asset.SpoolKey, cancellationToken);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            long size = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) { hash.AppendData(buffer, 0, read); size += read; }
            return size == asset.Size && CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(asset.Sha256));
        }
        catch (IOException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record OriginalBytes(long Size, string Sha256);
    private sealed record SpoolAsset(string SpoolKey, long Size, string Sha256, string State);

}
