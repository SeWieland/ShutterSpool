using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Infrastructure.Persistence;

internal static class WorkspaceCaptureVisibility
{
    internal static IQueryable<Guid> VisibleCaptureIds(IQueryable<CaptureEntity> captures, WorkspaceAccessContext? access) =>
        captures.Select(item => new CaptureVisibilityFacts
        {
            CaptureId = item.Id,
            IsViewer = access != null && access.IsViewer,
            ReleasedOnly = access != null && access.ReleasedOnly,
            IsActive = item.RetentionDisposition == "Active",
            IsReleased = item.ReleasedAt != null,
            Rating = item.Rating,
        }).Where(CaptureVisibilityPolicy.IsVisible).Select(facts => facts.CaptureId);
}

internal sealed class WorkspaceMediaScope(ShutterSpoolDbContext database, WorkspaceAccessContext access) : IWorkspaceMediaScope
{
    public Task<bool> CanReadCaptureAsync(Guid captureId, CancellationToken token)
    {
        IQueryable<Guid> visibleCaptureIds = WorkspaceCaptureVisibility.VisibleCaptureIds(database.Captures.AsNoTracking(), access);
        return database.Captures.AsNoTracking().Where(item => visibleCaptureIds.Contains(item.Id))
            .AnyAsync(item => item.Id == captureId
                && !database.PrivacyErasureAssets.Any(erased => database.CaptureAssets.Any(member =>
                    member.CaptureId == item.Id && member.AssetId == erased.AssetId)), token);
    }

    public async Task<SafeDelivery[]?> GetDeliveriesAsync(Guid captureId, CancellationToken token)
    {
        if (!await CanReadCaptureAsync(captureId, token)) return null;
        var rows = await database.Deliveries.AsNoTracking()
            .Where(delivery => database.CaptureAssets.Any(member => member.CaptureId == captureId && member.AssetId == delivery.AssetId))
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.AssetId, item.State,
                Available = database.Captures.Any(capture => capture.Id == captureId && capture.RetentionDisposition == "Active" && !capture.RetentionPaused)
                    && database.Destinations.Any(destination => destination.Id == item.DestinationId && destination.Enabled)
                    && !database.CaptureAssets.Any(member => member.CaptureId == captureId
                        && (database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId)
                            || database.AssetRemovalOperations.Any(removal => removal.AssetId == member.AssetId)))
                    && database.Assets.Any(asset => asset.Id == (item.SourceAssetId ?? item.AssetId) && asset.State == "Spooled")
            }).Take(1001).ToArrayAsync(token);
        return [.. rows.Select(item => new SafeDelivery(item.Id, item.AssetId, item.State.ToString(),
            !access.IsViewer && item.Available && RoutingActionPolicy.CanRetry(item.State)))];
    }
}
