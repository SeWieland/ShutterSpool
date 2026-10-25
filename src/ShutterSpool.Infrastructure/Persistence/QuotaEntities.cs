namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class SpoolQuotaSettingsEntity
{
    public int Id { get; init; } = 1;
    public int Revision { get; set; }
    public bool IsActive { get; set; }
    public long? QuotaBytes { get; set; }
    public long? HighWatermarkBytes { get; set; }
    public long? RecoveryTargetBytes { get; set; }
    public bool StorageMarkerAdoptionAuthorized { get; set; }
}

internal sealed class SpoolQuotaReservationEntity
{
    public Guid OperationId { get; init; }
    public Guid? AssetId { get; set; }
    public long ReservedBytes { get; set; }
    public long WrittenBytes { get; set; }
    public long FilesystemReservedBytes { get; set; }
    public string[] SpoolKeys { get; set; } = [];
    public string State { get; set; } = "Active";
    public string Category { get; set; } = "Spool";
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class SpoolQuotaObservationEntity
{
    public int Id { get; init; } = 1;
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.MinValue;
    public bool IsKnown { get; set; }
    public long ActiveBytes { get; set; }
    public long TrashBytes { get; set; }
    public long PartialBytes { get; set; }
    public long QuarantineBytes { get; set; }
    public long DiskPreviewBytes { get; set; }
    public long? DriveTotalBytes { get; set; }
    public long? DriveAvailableBytes { get; set; }
    public long? FilesystemReserveBytes { get; set; }
    public string? StorageMarker { get; set; }
    public string? UnknownReason { get; set; }
}
