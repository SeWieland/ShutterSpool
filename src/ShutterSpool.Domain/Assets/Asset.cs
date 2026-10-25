namespace ShutterSpool.Domain.Assets;

public enum AssetState
{
    Spooled,
    Quarantined,
}

public sealed record Asset(
    Guid Id,
    Guid SourceId,
    string OriginalName,
    string SpoolKey,
    long Size,
    string Sha256,
    DateTimeOffset IngestedAt,
    Guid RecoveryCorrelation,
    AssetState State = AssetState.Spooled);
