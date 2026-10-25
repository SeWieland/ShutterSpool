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
        management.MapGet("/activity/transfers", ReadAsync)
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
        bool bySource = sourceIds is { Length: > 0 } && transferIds is not { Length: > 0 };
        bool byTransfer = transferIds is { Length: > 0 } && sourceIds is not { Length: > 0 };
        string[]? selectedIds = bySource ? sourceIds : byTransfer ? transferIds : null;
        if (selectedIds is null || selectedIds.Length > 100 || pageSize is < 1 or > 100
            || selectedIds.Any(value => !Guid.TryParse(value, out _)))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sourceIds"] = ["Provide either 1 to 100 Source IDs or 1 to 100 transfer IDs, plus a page size from 1 to 100."],
            });

        Guid[] ids = [.. selectedIds.Select(Guid.Parse).Distinct()];
        if (ids.Length != selectedIds.Length)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sourceIds"] = ["Requested IDs must be distinct."],
            });

        if (!TryDecodeCursor(cursor, out long? beforeTimestamp, out Guid? beforeTransferId))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["cursor"] = ["The activity cursor is invalid."] });

        ICameraIngressStore? transfers = services.GetService<ICameraIngressStore>();
        if (transfers is null) return Results.Ok(new TransferActivitySnapshotResponse(false, changes.Epoch, [], null));
        CameraTransferActivityPage page = await transfers.GetActivityAsync(bySource ? ids : null, byTransfer ? ids : null,
            pageSize, beforeTimestamp, beforeTransferId, clock.GetUtcNow(), cancellationToken);
        CameraTransferActivity[] items = page.Items;
        DateTimeOffset observedNow = clock.GetUtcNow();
        for (int index = 0; index < items.Length; index++)
        {
            CameraTransferActivity activity = items[index];
            if (settings.PrewarmMode == PreviewPrewarmMode.Off && activity.Phase == CameraTransferPhase.PreparingPreview)
                items[index] = activity = activity with { Phase = CameraTransferPhase.AcceptedAwaitingGrouping };
            if (activity.Phase is CameraTransferPhase.Stale or CameraTransferPhase.Receiving
                && changes.GetRecentReceiving(activity.TransferId, observedNow) is { } receiving)
                items[index] = activity = receiving;
            if (!activity.Accepted || !activity.IsCurrentPreviewAsset || activity.AssetId is not Guid assetId
                || activity.CaptureId is not Guid captureId) continue;
            string key = $"{captureId:N}/{assetId:N}/grid-{settings.GridWidth}x{settings.GridHeight}.webp";
            bool published = await previews.ContainsAsync(key, new PreviewSize(settings.GridWidth, settings.GridHeight), cancellationToken);
            if (published) items[index] = activity with { Phase = CameraTransferPhase.Ready, ObservedAt = observedNow,
                PreviewReady = true };
            else if (changes.GetRecentPreviewPreparation(captureId, assetId, PreviewProfile.Grid, observedNow) is { } preparation)
                items[index] = activity with { Phase = CameraTransferPhase.PreparingPreview,
                    ObservedAt = preparation.ObservedAt, Version = preparation.Version, PreviewReady = false };
            else if (activity.PreviewReady) items[index] = activity with { Phase = CameraTransferPhase.AcceptedAwaitingGrouping, PreviewReady = false };
        }
        string? nextCursor = page.HasMore && page.NextAdmissionTimestamp is long nextTime && page.NextTransferId is Guid nextId
            ? EncodeCursor(nextTime, nextId) : null;
        TransferActivitySnapshotItem[] responseItems = [.. items.Select(activity => new TransferActivitySnapshotItem(
            activity.TransferId, activity.SourceId, activity.AssetId, activity.CaptureId, activity.Phase.ToString(),
            activity.ObservedAt, activity.Version, activity.Accepted, activity.PreviewReady))];
        return Results.Ok(new TransferActivitySnapshotResponse(!page.HasMore && changes.ActivityCoverageComplete,
            changes.Epoch, responseItems, nextCursor));
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
