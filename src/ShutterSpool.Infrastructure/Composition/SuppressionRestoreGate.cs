using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Retention;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Composition;

internal static class SuppressionRestoreGate
{
    private const long MaximumEvidenceFileBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static async Task ReconcileAsync(IConfiguration configuration,
        PrivacyErasureSuppressionService service, PrivacyErasureService erasure,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("ShutterSpool:Restore:Enabled")) return;
        if (!Guid.TryParse(configuration["ShutterSpool:Restore:SuppressionSnapshotId"], out Guid expectedId)
            || expectedId == Guid.Empty
            || string.IsNullOrWhiteSpace(configuration["ShutterSpool:Restore:EvidenceDirectory"]))
            throw new InvalidOperationException("Restore requires the current suppression snapshot identity and evidence directory.");
        string directory = Path.GetFullPath(configuration["ShutterSpool:Restore:EvidenceDirectory"]!);
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The restore suppression evidence directory is unavailable.");
        PrivacyErasureProtectedManifest manifest = await ReadAsync<PrivacyErasureProtectedManifest>(
            Path.Combine(directory, "manifest.json"), cancellationToken);
        if (manifest.SnapshotId != expectedId) throw Rejected();
        PrivacyErasureImportStatus status = await service.BeginReconciliationAsync(manifest.Payload, expectedId, cancellationToken);
        if (status.ErrorCode is not null) throw Rejected();
        for (int index = 0; index < status.ExpectedPageCount; index++)
        {
            PrivacyErasureProtectedPage page = await ReadAsync<PrivacyErasureProtectedPage>(
                Path.Combine(directory, $"page-{index:D6}.json"), cancellationToken);
            if (page.SnapshotId != expectedId || page.PageIndex != index) throw Rejected();
            status = await service.ReconcilePageAsync(page.Payload, cancellationToken);
            if (status.ErrorCode is not null) throw Rejected();
        }
        status = await service.CompleteReconciliationAsync(expectedId, cancellationToken);
        if (!status.Ready || status.ErrorCode is not null) throw Rejected();
        status = await erasure.DrainPendingAsync(expectedId, cancellationToken);
        if (!status.Ready || status.ErrorCode is not null) throw Rejected();
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > MaximumEvidenceFileBytes
            || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw Rejected();
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken) ?? throw Rejected();
    }

    private static InvalidOperationException Rejected() =>
        new("Restore suppression evidence is missing, invalid or incomplete; media exposure remains blocked.");
}
