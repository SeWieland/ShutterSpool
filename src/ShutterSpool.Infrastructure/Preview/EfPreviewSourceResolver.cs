using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Preview;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.Infrastructure.Preview;

internal sealed class EfPreviewSourceResolver(ShutterSpoolDbContext database) : IPreviewSourceResolver
{
    public async Task<PreviewResolution> ResolveAsync(Guid captureId, CancellationToken cancellationToken)
    {
        if (!await database.Captures.AnyAsync(capture => capture.Id == captureId, cancellationToken))
            return new(false, null);

        var jpeg = await (from membership in database.CaptureAssets
                          join asset in database.Assets on membership.AssetId equals asset.Id
                          where membership.CaptureId == captureId && membership.IsPrimary && membership.Role == "JPEG"
                          select new { asset.Id, asset.SpoolKey, asset.Size, asset.State })
            .SingleOrDefaultAsync(cancellationToken);
        if (jpeg is null) return new(true, null);
        if (jpeg.State != "Spooled") return new(true, null, true);
        return new(true, new PreviewSource(jpeg.Id, jpeg.SpoolKey, jpeg.Size));
    }
}
