using ShutterSpool.Domain.Assets;

namespace ShutterSpool.Application.Ingest;

public sealed record IngestRequest(Guid SourceId, string OriginalName, Stream Content, long? MaximumUploadBytes = null, Guid? OperationId = null);

public sealed record IngestResult(Asset Asset);

public enum IngestOperationState
{
    Receiving,
    Promoted,
    AwaitingSource,
    Received,
    Committed,
    Quarantined,
}

public sealed record IngestOperation(
    Guid Id,
    Guid AssetId,
    Guid SourceId,
    string OriginalName,
    string PartialKey,
    IngestOperationState State,
    string? FinalKey = null,
    long? Size = null,
    string? Sha256 = null,
    string? QuarantineReason = null);

public sealed record SpoolWrite(string PartialKey, string FinalKey, long Size, string Sha256);
public sealed record OriginalRemovalRecovery(bool IsAuthorized, IReadOnlyList<string> ProtectedSpoolKeys);

public interface IIngestJournal
{
    Task<IngestResult?> FindCommittedResultAsync(Guid operationId, CancellationToken cancellationToken);
    Task<IngestOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken);
    Task<OriginalRemovalRecovery> GetOriginalRemovalRecoveryAsync(Guid assetId, CancellationToken cancellationToken);
    Task CreateAsync(IngestOperation operation, CancellationToken cancellationToken);
    Task MarkPromotedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken);
    Task MarkReceivedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken);
    Task CommitAsync(Guid operationId, DateTimeOffset ingestedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<IngestOperation>> GetAllAsync(CancellationToken cancellationToken);
    Task QuarantineAsync(Guid operationId, string reason, CancellationToken cancellationToken);
}

public interface ISpool
{
    string GetPartialKey(Guid sourceId, Guid assetId);
    Task CheckAdmissionAsync(long? maximumUploadBytes, CancellationToken cancellationToken);
    Task<SpoolWrite> WriteAsync(Guid sourceId, Guid assetId, Stream content, long? maximumUploadBytes, CancellationToken cancellationToken);
    Task<SpoolWrite?> FindFinalAsync(IngestOperation operation, CancellationToken cancellationToken);
    Task QuarantineAsync(IngestOperation operation, string reason, CancellationToken cancellationToken);
    Task QuarantineUntrackedAsync(IReadOnlySet<string> knownKeys, CancellationToken cancellationToken);
}

// Only application use cases that validated a persisted upload admission may use this continuation path.
internal interface IAdmittedSpool : ISpool
{
    Task CheckAdmittedAdmissionAsync(long requiredFreeBytes, CancellationToken cancellationToken);
    string GetReceiveKey(Guid assetId);
    Task<SpoolWrite> ReceiveAdmittedAsync(string receiveKey, string partialKey, Stream content, long maximumUploadBytes, CancellationToken cancellationToken);
    Task<SpoolWrite?> VerifyReceivedAsync(IngestOperation operation, long expectedSize, CancellationToken cancellationToken);
}

internal interface IQuotaAwareSpool : ISpool
{
    IReadOnlyList<string> GetReservationKeys(Guid sourceId, Guid assetId);
    Task<SpoolWrite> WriteReservedAsync(Guid sourceId, Guid assetId, string partialKey, string finalKey,
        Stream content, long maximumUploadBytes, CancellationToken cancellationToken);
}
