using ShutterSpool.Server.Security;
using Microsoft.AspNetCore.WebUtilities;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Preview;
using System.Globalization;
using System.Text;

namespace ShutterSpool.Server.Api.Live;

public sealed record TransferActivitySnapshotItem(Guid TransferId, Guid SourceId, Guid? AssetId, Guid? CaptureId,
    string Phase, DateTimeOffset ObservedAt, long Version, bool Accepted, bool PreviewReady);
public sealed record TransferActivitySnapshotResponse(bool CoverageComplete, string Epoch,
    TransferActivitySnapshotItem[] Items, string? NextCursor);

public static class TransferActivityEndpoints
{
    public static RouteGroupBuilder MapTransferActivityEndpoints(this RouteGroupBuilder management)
    {
        RouteGroupBuilder authorized = management.MapGroup("").RequireAuthorization(WorkspacePolicies.Administer);
        authorized.MapGet("/activity/transfers", ReadAsync)
            .WithSummary("Read bounded Source transfer activity")
            .Produces<TransferActivitySnapshotResponse>().ProducesValidationProblem();
        return management;
    }

    private static async Task<IResult> ReadAsync(string[]? sourceIds, string[]? transferIds, int? limit,
        string? cursor, IServiceProvider services,
        LiveChangeNotifier changes, TimeProvider clock, IPreviewCache previews, PreviewSettings settings,
        CancellationToken cancellationToken)
    {
        int pageSize = limit ?? 30;
        string? idError = ParseActivityIds(sourceIds, transferIds, pageSize, out bool bySource, out Guid[] ids);
        if (idError is not null)
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["sourceIds"] = [idError] });
        if (!TryDecodeCursor(cursor, out long? beforeTimestamp, out Guid? beforeTransferId))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["The activity cursor is invalid."] });

        ICameraIngressStore? transfers = services.GetService<ICameraIngressStore>();
        if (transfers is null) return Results.Ok(new TransferActivitySnapshotResponse(false, changes.Epoch, [], null));
        CameraTransferActivityPage page = await transfers.GetActivityAsync(bySource ? ids : null, bySource ? null : ids,
            pageSize, beforeTimestamp, beforeTransferId, clock.GetUtcNow(), cancellationToken);
        CameraTransferActivity[] items = page.Items;
        DateTimeOffset observedNow = clock.GetUtcNow();
        for (int index = 0; index < items.Length; index++)
            items[index] = await EnrichActivityAsync(items[index], observedNow, changes, previews, settings, cancellationToken);

        string? nextCursor = page.HasMore && page.NextAdmissionTimestamp is long nextTime && page.NextTransferId is Guid nextId
            ? EncodeCursor(nextTime, nextId) : null;
        TransferActivitySnapshotItem[] responseItems = [.. items.Select(activity => new TransferActivitySnapshotItem(
            activity.TransferId, activity.SourceId, activity.AssetId, activity.CaptureId, activity.Phase.ToString(),
            activity.ObservedAt, activity.Version, activity.Accepted, activity.PreviewReady))];
        return Results.Ok(new TransferActivitySnapshotResponse(!page.HasMore && changes.ActivityCoverageComplete,
            changes.Epoch, responseItems, nextCursor));
    }

    private static string? ParseActivityIds(string[]? sourceIds, string[]? transferIds, int pageSize, out bool bySource, out Guid[] ids)
    {
        const string invalid = "Provide either 1 to 100 Source IDs or 1 to 100 transfer IDs, plus a page size from 1 to 100.";
        ids = [];
        bySource = sourceIds is { Length: > 0 };
        string[]? selectedIds = bySource ? sourceIds : transferIds;
        if (bySource && transferIds is { Length: > 0 } || selectedIds is not { Length: > 0 and <= 100 }
            || pageSize is < 1 or > 100) return invalid;

        ids = new Guid[selectedIds.Length];
        for (int index = 0; index < selectedIds.Length; index++)
        {
            if (!Guid.TryParse(selectedIds[index], out ids[index])) return invalid;
        }
        return ids.Distinct().Count() == ids.Length ? null : "Requested IDs must be distinct.";
    }

    private static async Task<CameraTransferActivity> EnrichActivityAsync(CameraTransferActivity activity,
        DateTimeOffset observedNow, LiveChangeNotifier changes, IPreviewCache previews, PreviewSettings settings,
        CancellationToken cancellationToken)
    {
        if (settings.PrewarmMode == PreviewPrewarmMode.Off && activity.Phase == CameraTransferPhase.PreparingPreview)
            activity = activity with { Phase = CameraTransferPhase.AcceptedAwaitingGrouping };
        if (activity.Phase is CameraTransferPhase.Stale or CameraTransferPhase.Receiving
            && changes.GetRecentReceiving(activity.TransferId, observedNow) is { } receiving)
            activity = receiving;
        if (!activity.Accepted || !activity.IsCurrentPreviewAsset || activity.AssetId is not Guid assetId
            || activity.CaptureId is not Guid captureId) return activity;

        var size = new PreviewSize(settings.GridWidth, settings.GridHeight);
        string key = $"{captureId:N}/{assetId:N}/grid-{settings.GridWidth}x{settings.GridHeight}.webp";
        if (await previews.ContainsAsync(key, size, cancellationToken))
            return activity with { Phase = CameraTransferPhase.Ready, ObservedAt = observedNow, PreviewReady = true };
        if (changes.GetRecentPreviewPreparation(captureId, assetId, PreviewProfile.Grid, observedNow) is { } preparation)
            return activity with { Phase = CameraTransferPhase.PreparingPreview, ObservedAt = preparation.ObservedAt,
                Version = preparation.Version, PreviewReady = false };
        return activity.PreviewReady
            ? activity with { Phase = CameraTransferPhase.AcceptedAwaitingGrouping, PreviewReady = false }
            : activity;
    }

    private static string EncodeCursor(long admissionTimestamp, Guid transferId) => WebEncoders.Base64UrlEncode(
        Encoding.UTF8.GetBytes($"{admissionTimestamp}|{transferId:N}"));

    private static bool TryDecodeCursor(string? cursor, out long? admissionTimestamp, out Guid? transferId)
    {
        admissionTimestamp = null;
        transferId = null;
        if (cursor is null) return true;
        if (cursor.Length is 0 or > 256) return false;
        try
        {
            string decoded = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(cursor));
            int separator = decoded.IndexOf('|');
            if (separator <= 0 || !long.TryParse(decoded[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)
                || !Guid.TryParseExact(decoded[(separator + 1)..], "N", out Guid id)) return false;
            admissionTimestamp = timestamp;
            transferId = id;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { return false; }
    }
}
