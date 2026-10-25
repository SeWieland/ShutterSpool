namespace ShutterSpool.Application.Retention;

public sealed class PrivacyErasureSuppressionService(IPrivacyErasureStore store, IPrivacyErasureEvidenceCodec evidenceCodec)
{
    // Eight records remain below the 1 MB authenticated payload limit even if every
    // member carries 64 maximum-length spool/transient keys.
    public const int PageSize = 8;
    public const int MaximumProtectedPayloadLength = 1_000_000;

    public async Task<PrivacyErasureProtectedManifest> CreateExportAsync(CancellationToken cancellationToken)
    {
        PrivacyErasureSuppressionManifest manifest = await store.CreateSuppressionExportAsync(cancellationToken);
        string payload = evidenceCodec.ProtectManifest(manifest);
        return new(manifest.SnapshotId, manifest.RecordCount, manifest.PageSize, manifest.PageCount, payload);
    }

    public async Task<PrivacyErasureProtectedManifest?> GetExportManifestAsync(Guid snapshotId,
        CancellationToken cancellationToken)
    {
        PrivacyErasureSuppressionManifest? manifest = await store.GetSuppressionExportAsync(snapshotId,
            cancellationToken);
        return manifest is null ? null : new(manifest.SnapshotId, manifest.RecordCount, manifest.PageSize,
            manifest.PageCount, evidenceCodec.ProtectManifest(manifest));
    }

    public async Task<PrivacyErasureProtectedPage?> GetExportPageAsync(Guid snapshotId, int pageIndex,
        CancellationToken cancellationToken)
    {
        PrivacyErasureSuppressionPage? page = await store.GetSuppressionExportPageAsync(snapshotId, pageIndex,
            cancellationToken);
        if (page is null) return null;
        string payload = evidenceCodec.ProtectPage(page);
        if (payload.Length > MaximumProtectedPayloadLength)
            throw new InvalidOperationException("A suppression export page exceeds its bounded size.");
        return new(snapshotId, pageIndex, payload);
    }

    public async Task<PrivacyErasureImportStatus> BeginReconciliationAsync(string protectedManifest,
        Guid expectedSnapshotId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(protectedManifest)
            || protectedManifest.Length > MaximumProtectedPayloadLength || expectedSnapshotId == Guid.Empty)
            return new(expectedSnapshotId, 0, 0, false, "SUPPRESSION_EXPORT_INVALID");
        if (!evidenceCodec.TryReadManifest(protectedManifest, out PrivacyErasureSuppressionManifest? manifest)
            || manifest is null)
            return new(expectedSnapshotId, 0, 0, false, "SUPPRESSION_EXPORT_INVALID");
        if (manifest.Version != 1 || manifest.SnapshotId != expectedSnapshotId
            || manifest.PageSize != PageSize || manifest.RecordCount < 0
            || manifest.PageCount != (manifest.RecordCount + PageSize - 1) / PageSize)
            return new(expectedSnapshotId, 0, 0, false, "SUPPRESSION_SNAPSHOT_MISMATCH");
        return await store.BeginSuppressionReconciliationAsync(manifest, expectedSnapshotId, cancellationToken);
    }

    public async Task<PrivacyErasureImportStatus> ReconcilePageAsync(string protectedPage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(protectedPage) || protectedPage.Length > MaximumProtectedPayloadLength)
            return new(Guid.Empty, 0, 0, false, "SUPPRESSION_PAGE_INVALID");
        if (!evidenceCodec.TryReadPage(protectedPage, out PrivacyErasureSuppressionPage? page)
            || page is null)
            return new(Guid.Empty, 0, 0, false, "SUPPRESSION_PAGE_INVALID");
        return await store.StageSuppressionPageAsync(page, cancellationToken);
    }

    public Task<PrivacyErasureImportStatus> CompleteReconciliationAsync(Guid snapshotId,
        CancellationToken cancellationToken) => store.CompleteSuppressionReconciliationAsync(snapshotId,
        cancellationToken);

}
