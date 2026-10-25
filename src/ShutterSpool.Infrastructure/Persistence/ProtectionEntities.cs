namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class GalleryProtectionEntity
{
    public Guid Id { get; init; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AccessRevokedAt { get; set; }
}

internal sealed class GalleryProtectionAssetEntity
{
    public Guid ProtectionId { get; init; }
    public Guid AssetId { get; init; }
}

internal sealed class PreservationHoldEntity
{
    public Guid Id { get; init; }
    public int Revision { get; set; }
    public required string Authority { get; init; }
    public required string Reason { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset ReviewAt { get; init; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? Resolution { get; set; }
}

internal sealed class PreservationHoldAssetEntity
{
    public Guid HoldId { get; init; }
    public Guid AssetId { get; init; }
}
