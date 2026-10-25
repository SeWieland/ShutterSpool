using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Application.Retention;

public enum RetentionRemovalReason { ManualPermanentDelete, RecoveryWindowExpired, PressureRecoveryExpired }

public sealed record AuthorizedAssetRemoval(Guid OperationId, Guid CaptureId, Guid AssetId,
    string SpoolKey, long ExpectedSize, string StorageMarker, IReadOnlyList<string> KnownTransientKeys);

public sealed record RetentionRemovalAuthorization(RetentionOperationResult Result,
    IReadOnlyList<AuthorizedAssetRemoval> Work);
public sealed record RetentionRemovalPassResult(int Examined, int Completed, int Failed, long RemovedBytes,
    int Selected = 0, long SelectedBytes = 0);
public sealed record RetentionSelectionPassResult(int Examined, int Selected, long SelectedBytes);
public sealed record RetentionRemovalCandidate(Guid CaptureId, int Revision, long OriginalBytes,
    RetentionTier? Tier = null, DateTimeOffset? EligibilityAt = null);

public interface IRetentionControllerLock
{
    ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken);
}

public interface IRetentionRemovalStore
{
    Task RecordCapacityObservationAsync(SpoolQuotaSnapshot snapshot, RetentionRemovalPassResult pass,
        bool automaticRemovalEnabled, DateTimeOffset now, CancellationToken cancellationToken);
    Task<int> PruneCompletedAttemptHistoryAsync(DateTimeOffset cutoff, int maximumCount,
        CancellationToken cancellationToken);
    Task<int> RefreshRetentionFactsAsync(int maximumCount, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<bool> IsPressureEpisodeActiveAsync(CancellationToken cancellationToken);
    Task<RetentionSelectionPassResult> SelectEligibleActiveAsync(int maximumCount, long maximumBytes,
        bool underPressure, DateTimeOffset now, CancellationToken cancellationToken);
    Task<RetentionRemovalAuthorization> AuthorizeCaptureAsync(Guid captureId, int? expectedRevision,
        RetentionRemovalReason reason, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<AuthorizedAssetRemoval>> GetPendingAsync(int maximumCount,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<RetentionRemovalCandidate>> GetDueTrashCandidatesAsync(int maximumCount,
        DateTimeOffset now, bool underPressure, CancellationToken cancellationToken);
    Task<long> GetPendingReclamationBytesAsync(DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> MarkRemovingAsync(Guid operationId, CancellationToken cancellationToken);
    Task CompleteAssetAsync(Guid operationId, long removedBytes, DateTimeOffset now,
        CancellationToken cancellationToken);
    Task FailAssetAsync(Guid operationId, string failureCode, DateTimeOffset now,
        long partiallyRemovedBytes, CancellationToken cancellationToken);
    Task FinalizeCaptureAsync(Guid captureId, DateTimeOffset now, CancellationToken cancellationToken);
    Task<CaptureRetentionSummary?> GetSummaryAsync(Guid captureId, DateTimeOffset now,
        CancellationToken cancellationToken);
}
