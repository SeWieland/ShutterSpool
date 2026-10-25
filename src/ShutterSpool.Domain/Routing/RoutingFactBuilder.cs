using ShutterSpool.Domain.Captures;

namespace ShutterSpool.Domain.Routing;

public sealed record RoutingCaptureFacts(Guid SourceId, int? Rating, string? Label, string[] Tags, CaptureSelection? Selection,
    string? CollectionName = null, string? CollectionPath = null);
public sealed record RoutingAssetFacts(string Role, string OriginalName, long Size, string? MediaType,
    string? CameraMake, string? CameraModel, string? CameraSerial, DateTime? CaptureTimeLocal, bool CaptureTimeUncertain);

public static class RoutingFactBuilder
{
    public static RoutingFacts Build(RoutingCaptureFacts capture, RoutingAssetFacts asset)
    {
        string normalized = asset.OriginalName.Replace('\\', '/');
        string suppliedName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var values = new Dictionary<string, RoutingValue>(StringComparer.Ordinal)
        {
            ["source"] = capture.SourceId.ToString("D"),
            ["variant"] = asset.Role,
            ["extension"] = Path.GetExtension(suppliedName).TrimStart('.'),
            ["suppliedName"] = suppliedName,
            ["size"] = asset.Size,
            ["tags"] = capture.Tags,
        };
        if (capture.Rating is int rating) values["rating"] = rating;
        values["label"] = new(Text: capture.Label);
        if (capture.Selection is CaptureSelection selection) values["selection"] = selection.ToString();
        if (capture.CollectionName is not null) values["collectionName"] = capture.CollectionName;
        if (capture.CollectionPath is not null) values["collectionPath"] = capture.CollectionPath;
        if (asset.MediaType is not null) values["mime"] = asset.MediaType;
        if (asset.CameraMake is not null) values["cameraMake"] = asset.CameraMake;
        if (asset.CameraModel is not null) values["cameraModel"] = asset.CameraModel;
        if (asset.CameraSerial is not null) values["cameraSerial"] = asset.CameraSerial;
        if (!asset.CaptureTimeUncertain && asset.CaptureTimeLocal is DateTime local)
            values["captureTime"] = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new(values);
    }
}
