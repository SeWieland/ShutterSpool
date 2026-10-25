namespace ShutterSpool.Application.Retention;

public enum SpoolQuotaFileCategory
{
    Spool,
    Incoming,
    Quarantine,
    DiskPreview,
}

public enum SpoolQuotaReservationCategory
{
    Spool,
    DiskPreview,
}

public sealed record SpoolQuotaFile(string Key, long SizeBytes, SpoolQuotaFileCategory Category);

public sealed record SpoolQuotaInventory(
    DateTimeOffset ObservedAt,
    bool IsKnown,
    IReadOnlyList<SpoolQuotaFile> Files,
    long? DriveTotalBytes,
    long? DriveAvailableBytes,
    long? FilesystemReserveBytes,
    string? UnknownReason = null,
    string? StorageMarker = null,
    bool StorageMarkerNeedsCommit = false);

public sealed record SpoolQuotaSettings(
    int Revision = 0,
    bool IsActive = false,
    long? QuotaBytes = null,
    long? HighWatermarkBytes = null,
    long? RecoveryTargetBytes = null)
{
    public void Validate()
    {
        if (Revision < 0) throw new ArgumentOutOfRangeException(nameof(Revision));
        bool anyCapacity = QuotaBytes is not null || HighWatermarkBytes is not null || RecoveryTargetBytes is not null;
        if (anyCapacity && (QuotaBytes is null || HighWatermarkBytes is null || RecoveryTargetBytes is null))
            throw new ArgumentException("Quota and both watermarks must be configured together.");
        if (IsActive && !anyCapacity) throw new ArgumentException("A quota and both watermarks are required before quota admission is enabled.");
        if (QuotaBytes is long quota && quota <= 0) throw new ArgumentOutOfRangeException(nameof(QuotaBytes));
        if (HighWatermarkBytes is long high && (high <= 0 || QuotaBytes is not long configuredQuota || high > configuredQuota))
            throw new ArgumentOutOfRangeException(nameof(HighWatermarkBytes));
        if (RecoveryTargetBytes is long target && (target < 0 || HighWatermarkBytes is not long configuredHigh || target >= configuredHigh))
            throw new ArgumentOutOfRangeException(nameof(RecoveryTargetBytes));
    }
}

public sealed record SpoolQuotaReservation(
    Guid OperationId,
    Guid? AssetId,
    long ReservedBytes,
    long WrittenBytes,
    IReadOnlyList<string> SpoolKeys,
    long FilesystemReservedBytes = 0,
    SpoolQuotaReservationCategory Category = SpoolQuotaReservationCategory.Spool);

public sealed record SpoolQuotaSnapshot(
    SpoolQuotaSettings Settings,
    DateTimeOffset ObservedAt,
    bool IsKnown,
    long UsedBytes,
    long ReservedBytes,
    long ActiveBytes,
    long TrashBytes,
    long PartialBytes,
    long QuarantineBytes,
    long DiskPreviewBytes,
    long? DriveTotalBytes,
    long? DriveAvailableBytes,
    long? FilesystemReserveBytes,
    long RecoveryDeficitBytes,
    string? AdmissionBlockedReason,
    string? UnknownReason = null)
{
    public bool IsOverQuota => Settings.QuotaBytes is long quota && (UsedBytes > quota || ReservedBytes > quota - UsedBytes);
}

public interface ISpoolAccounting
{
    Task<SpoolQuotaInventory> ObserveAsync(IReadOnlySet<string> reservedSpoolKeys, string? expectedStorageMarker,
        bool allowInitializeStorageMarker, CancellationToken cancellationToken);
    Task<bool> EstablishStorageMarkerAsync(string marker, CancellationToken cancellationToken);
}

public interface ISpoolQuotaStore
{
    Task<SpoolQuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<SpoolQuotaSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<SpoolQuotaSettings?> StageSettingsUpdateAsync(int expectedRevision, SpoolQuotaSettings settings,
        bool authorizeStorageMarkerAdoption, CancellationToken cancellationToken);
    Task<SpoolQuotaSettings?> UpdateSettingsAsync(int expectedRevision, SpoolQuotaSettings settings, CancellationToken cancellationToken);
    Task<bool> TryReserveAsync(SpoolQuotaReservation reservation, CancellationToken cancellationToken);
    Task<bool> UpdateReservationAsync(Guid operationId, Guid? assetId, IReadOnlyList<string> spoolKeys,
        long reservedBytes, long writtenBytes, long filesystemReservedBytes, CancellationToken cancellationToken);
    Task<bool> MarkReservationRetainedAsync(Guid operationId, long actualBytes, CancellationToken cancellationToken);
    Task ReleaseUnadmittedReservationAsync(Guid operationId, CancellationToken cancellationToken);
    Task ReleaseReservationAsync(Guid operationId, CancellationToken cancellationToken);
    Task ReconcileReservationsAsync(CancellationToken cancellationToken);
}

/// <summary>Enlists reservation removal in the caller's existing ingest Asset transaction.</summary>
public interface IQuotaReservationCommitter
{
    Task CompleteReservationAsync(Guid operationId, CancellationToken cancellationToken);
}
