using ShutterSpool.Domain.Assets;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Application.Ingress;

public sealed record CameraLogin(Guid Id, Guid SourceId, string Username, string VirtualRoot, long CredentialGeneration,
    SourceConnectionProfile ConnectionProfile);

public enum CameraTransferState
{
    Admitted,
    Receiving,
    Received,
    Handoff,
    Completed,
    Failed,
    Quarantined,
}

public sealed record CameraUploadAdmission(
    Guid Id,
    Guid LoginId,
    Guid SourceId,
    string Username,
    string SessionId,
    string VirtualPath,
    long AdmissionTimestamp,
    CameraTransferState State,
    long? Size = null,
    long? CompletionTimestamp = null,
    Guid? AssetId = null,
    string? FailureReason = null,
    long MaximumUploadBytes = 0,
    string? ReceiveKey = null);

public sealed record CameraTransferStatus(Guid Id, CameraTransferState State, Guid? AssetId, string? FailureReason);

public enum CameraTransferPhase
{
    Receiving,
    AcceptedAwaitingGrouping,
    PreparingPreview,
    Ready,
    FailedUnavailable,
    Stale,
}

public sealed record CameraTransferActivity(Guid TransferId, Guid SourceId, Guid? AssetId, Guid? CaptureId,
    CameraTransferPhase Phase, DateTimeOffset ObservedAt, long Version, bool Accepted, bool PreviewReady, bool IsCurrentPreviewAsset = false);
public sealed record CameraTransferActivityPage(CameraTransferActivity[] Items, bool HasMore,
    long? NextAdmissionTimestamp, Guid? NextTransferId);

public interface ICameraTransferActivityPublisher
{
    void Publish(CameraTransferActivity activity);
}

public interface ICameraIngressStore
{
    Task<CameraLogin?> CreateLoginAsync(Guid sourceId, string username, long credentialGeneration, CancellationToken cancellationToken,
        SourceConnectionProfile connectionProfile = SourceConnectionProfile.ModernFtps);
    Task<CameraLogin?> FindLoginAsync(Guid loginId, CancellationToken cancellationToken);
    Task<CameraUploadAdmission?> AdmitAsync(Guid loginId, string username, string sessionId, string virtualPath, long timestamp,
        Guid admissionId, Guid assetId, long maximumUploadBytes, string receiveKey,
        int maximumConcurrentTransfers, int transferTimeoutSeconds, CancellationToken cancellationToken);
    Task QuarantineStaleAsync(long beforeTimestamp, CancellationToken cancellationToken);
    Task<CameraUploadAdmission?> ClaimReceiveAsync(Guid loginId, string virtualPath, CancellationToken cancellationToken);
    Task MarkReceivedAsync(Guid admissionId, long size, CancellationToken cancellationToken);
    Task<CameraUploadAdmission?> FindCompletionAsync(Guid loginId, string username, string sessionId, string virtualPath, long timestamp, CancellationToken cancellationToken);
    Task<CameraFileInfo?> FindFileAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<CameraFileInfo>> GetFilesAsync(Guid sourceId, string virtualDirectory, CancellationToken cancellationToken);
    Task<bool> TryBeginHandoffAsync(Guid admissionId, long completionTimestamp, CancellationToken cancellationToken);
    Task CompleteAsync(Guid admissionId, Asset asset, long completionTimestamp, CancellationToken cancellationToken);
    Task<IReadOnlyList<CameraUploadAdmission>> GetRecoverableAsync(CancellationToken cancellationToken);
    Task QuarantineAsync(Guid admissionId, string reason, CancellationToken cancellationToken);
    Task ReleaseHandoffAsync(Guid admissionId, CancellationToken cancellationToken);
    Task<bool> FailAsync(Guid loginId, string username, string sessionId, string virtualPath, long completionTimestamp, string reason, CancellationToken cancellationToken);
    Task<IReadOnlyList<CameraTransferStatus>> GetStatusAsync(Guid? sourceId, CancellationToken cancellationToken);
    Task<CameraTransferActivityPage> GetActivityAsync(IReadOnlyCollection<Guid>? sourceIds,
        IReadOnlyCollection<Guid>? transferIds, int limit, long? beforeAdmissionTimestamp, Guid? beforeTransferId,
        DateTimeOffset observedBefore, CancellationToken cancellationToken);
    Task<(long Files, long Size)> GetDirectorySizeAsync(Guid sourceId, CancellationToken cancellationToken);
}

public interface ICameraIngressDirectories
{
    Task CreateDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken);
    Task<CameraFileInfo?> GetFileInfoAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken);
    Task<IReadOnlyList<CameraFileInfo>> GetDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken);
}

public sealed record CameraFileInfo(string Name, long Size, bool IsDirectory, DateTimeOffset LastModified);
