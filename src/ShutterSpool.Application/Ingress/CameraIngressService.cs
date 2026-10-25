using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Application.Ingress;

public sealed class CameraIngressRejectedException(string message) : Exception(message);
public sealed class CameraSourceRootInitializationException(Guid sourceId)
    : Exception("The camera source root could not be initialized.")
{
    public Guid SourceId { get; } = sourceId;
}

public sealed class CameraIngressService(
    SourceCredentialVerifier credentials,
    ICameraIngressStore store,
    ICameraIngressDirectories directories,
    IngestService ingest,
    TimeProvider timeProvider,
    ISourceAuthenticationObservationRecorder authenticationObservations,
    ICameraTransferActivityPublisher? transferActivity = null)
{
    public async Task<CameraLogin?> AuthenticateAsync(string username, string secret, CancellationToken cancellationToken,
        SourceConnectionProfile connectionProfile = SourceConnectionProfile.ModernFtps)
    {
        SourceCredentialCheck check = await credentials.VerifyAttemptAsync(username, secret, cancellationToken, connectionProfile);
        if (check.Access is not SourceAccess access)
        {
            if (check.VerifiedButRejectedSourceId is Guid sourceId)
                authenticationObservations.Record(new(sourceId, SourceAuthenticationOutcome.Rejected, timeProvider.GetUtcNow()));
            else authenticationObservations.RecordUnassigned(timeProvider.GetUtcNow());
            return null;
        }

        try { await directories.CreateDirectoryAsync(access.SourceId, "/", cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            authenticationObservations.Record(new(access.SourceId, SourceAuthenticationOutcome.Rejected, timeProvider.GetUtcNow()));
            throw new CameraSourceRootInitializationException(access.SourceId);
        }
        CameraLogin? login = await store.CreateLoginAsync(access.SourceId, username, access.CredentialGeneration, cancellationToken, connectionProfile);
        if (login is not null)
        {
            credentials.RecordSucceeded(login.SourceId);
            authenticationObservations.Record(new(login.SourceId, SourceAuthenticationOutcome.Succeeded, timeProvider.GetUtcNow()));
        }
        return login;
    }

    public async Task AdmitAsync(Guid loginId, string username, string sessionId, string virtualPath, long timestamp,
        long maximumUploadBytes, int maximumConcurrentTransfers, int transferTimeoutSeconds, CancellationToken cancellationToken)
    {
        ValidateVirtualPath(virtualPath, allowDirectory: false);
        if (maximumUploadBytes <= 0 || maximumConcurrentTransfers <= 0 || transferTimeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumUploadBytes), "Camera upload limits and timeout must be positive.");
        long requiredFreeBytes = checked(maximumUploadBytes * maximumConcurrentTransfers);
        await ingest.CheckAdmittedAdmissionAsync(requiredFreeBytes, cancellationToken);
        Guid admissionId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        string receiveKey = ingest.GetAdmittedReceiveKey(assetId);
        if (!await ingest.ReserveCameraAdmissionAsync(admissionId, assetId, maximumUploadBytes, receiveKey, cancellationToken))
            throw new CameraIngressRejectedException("Upload capacity is unavailable.");

        CameraUploadAdmission? admission = await store.AdmitAsync(loginId, username, sessionId, virtualPath, timestamp,
            admissionId, assetId, maximumUploadBytes, receiveKey,
            maximumConcurrentTransfers, transferTimeoutSeconds, cancellationToken);
        if (admission is null)
        {
            await ingest.ReleaseUnadmittedQuotaReservationAsync(admissionId, cancellationToken);
            throw new CameraIngressRejectedException("Upload admission is unavailable.");
        }

        await ReconcileAdmissionReservationAsync(admission, admissionId, cancellationToken);
    }

    private async Task ReconcileAdmissionReservationAsync(CameraUploadAdmission admission, Guid requestedAdmissionId,
        CancellationToken cancellationToken)
    {
        if (admission.Id == requestedAdmissionId) return;

        await ingest.ReleaseUnadmittedQuotaReservationAsync(requestedAdmissionId, cancellationToken);
        if (admission.State is CameraTransferState.Completed or CameraTransferState.Quarantined) return;

        Guid assetId = admission.AssetId ?? admission.Id;
        string receiveKey = admission.ReceiveKey ?? ingest.GetAdmittedReceiveKey(assetId);
        if (!await ingest.ReserveCameraAdmissionAsync(admission.Id, assetId, admission.MaximumUploadBytes, receiveKey,
                cancellationToken))
            throw new CameraIngressRejectedException("Upload capacity is unavailable.");
    }

    public async Task ReceiveAsync(Guid loginId, string virtualPath, Stream content, long maximumBytes, CancellationToken cancellationToken)
    {
        ValidateVirtualPath(virtualPath, allowDirectory: false);
        CameraUploadAdmission? admission = await store.ClaimReceiveAsync(loginId, virtualPath, cancellationToken);
        if (admission is null) throw new CameraIngressRejectedException("No matching upload admission is available.");

        if (admission.MaximumUploadBytes <= 0 || maximumBytes != admission.MaximumUploadBytes)
            throw new CameraIngressRejectedException("The upload exceeds its durable admission limit.");
        string receiveKey = admission.ReceiveKey ?? ingest.GetAdmittedReceiveKey(admission.AssetId ?? admission.Id);
        Guid assetId = admission.AssetId ?? admission.Id;
        if (!await ingest.ReserveCameraAdmissionAsync(admission.Id, assetId,
                admission.MaximumUploadBytes, receiveKey, cancellationToken))
            throw new CameraIngressRejectedException("Upload capacity is unavailable.");

        PublishReceiving(admission);
        using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(30));
        Task renewals = PublishReceivingRenewalsAsync(admission, heartbeat, cancellationToken);

        try
        {
            long size = await ingest.ReceiveAdmittedAsync(new(admission.SourceId,
                virtualPath[(virtualPath.LastIndexOf('/') + 1)..], content, maximumBytes, admission.Id), cancellationToken,
                receiveKey, assetId);
            await store.MarkReceivedAsync(admission.Id, size, cancellationToken);
        }
        catch
        {
            PublishFailure(admission);
            throw;
        }
        finally
        {
            heartbeat.Dispose();
            await renewals;
        }
    }

    public async Task<IngestResult> CompleteAsync(Guid loginId, string username, string sessionId, string virtualPath,
        long timestamp, long expectedSize, CancellationToken cancellationToken)
    {
        ValidateVirtualPath(virtualPath, allowDirectory: false);
        CameraUploadAdmission? admission = await store.FindCompletionAsync(loginId, username, sessionId, virtualPath, timestamp, cancellationToken);
        if (admission is null || timestamp < admission.AdmissionTimestamp)
            throw new CameraIngressRejectedException("No matching upload admission is available.");
        if (admission.State == CameraTransferState.Completed)
        {
            IngestResult? recorded = await ingest.FindAdmittedResultAsync(admission.Id, admission.SourceId,
                virtualPath[(virtualPath.LastIndexOf('/') + 1)..], expectedSize, cancellationToken);
            if (recorded is null) throw new CameraIngressRejectedException("The recorded upload result is unavailable.");
            return recorded;
        }
        if (admission.State == CameraTransferState.Handoff)
        {
            if (admission.CompletionTimestamp != timestamp)
                throw new CameraIngressRejectedException("The completion event does not match the active handoff.");
            IngestResult? committed = await ingest.FindAdmittedResultAsync(admission.Id, admission.SourceId,
                virtualPath[(virtualPath.LastIndexOf('/') + 1)..], expectedSize, cancellationToken);
            if (committed is null) throw new CameraIngressRejectedException("The upload is already being handed off.");
            await store.CompleteAsync(admission.Id, committed.Asset, timestamp, cancellationToken);
            PublishAccepted(admission, committed.Asset.Id);
            return committed;
        }
        if (admission.State != CameraTransferState.Received || admission.Size != expectedSize)
            throw new CameraIngressRejectedException("The received upload does not match its completion event.");
        if (!await store.TryBeginHandoffAsync(admission.Id, timestamp, cancellationToken))
            throw new CameraIngressRejectedException("The upload is already being handed off.");

        try
        {
            string originalName = virtualPath[(virtualPath.LastIndexOf('/') + 1)..];
            IngestOperation operation = await ingest.FindOperationAsync(admission.Id, cancellationToken)
                ?? throw new CameraIngressRejectedException("The received upload receipt is unavailable.");
            IngestResult result = await ingest.FinalizeAdmittedAsync(operation, admission.SourceId, originalName, expectedSize, cancellationToken);
            if (result.Asset.Size != expectedSize) throw new CameraIngressRejectedException("The received upload does not match its completion event.");
            await store.CompleteAsync(admission.Id, result.Asset, timestamp, cancellationToken);
            PublishAccepted(admission, result.Asset.Id);
            return result;
        }
        catch
        {
            await store.ReleaseHandoffAsync(admission.Id, cancellationToken);
            throw;
        }
    }

    private void PublishAccepted(CameraUploadAdmission admission, Guid assetId) =>
        transferActivity?.Publish(new(admission.Id, admission.SourceId, assetId, null, CameraTransferPhase.AcceptedAwaitingGrouping,
            timeProvider.GetUtcNow(), 2, true, false));

    private void PublishFailure(CameraUploadAdmission admission) =>
        transferActivity?.Publish(new(admission.Id, admission.SourceId, admission.AssetId, null, CameraTransferPhase.FailedUnavailable,
            timeProvider.GetUtcNow(), 2, false, false));

    private void PublishReceiving(CameraUploadAdmission admission) =>
        transferActivity?.Publish(new(admission.Id, admission.SourceId, null, null, CameraTransferPhase.Receiving,
            timeProvider.GetUtcNow(), 1, false, false));

    private async Task PublishReceivingRenewalsAsync(CameraUploadAdmission admission, PeriodicTimer heartbeat,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await heartbeat.WaitForNextTickAsync(cancellationToken))
                PublishReceiving(admission);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task<bool> FailUploadAsync(Guid loginId, string username, string sessionId, string virtualPath, long timestamp,
        CancellationToken cancellationToken)
    {
        bool failed = await store.FailAsync(loginId, username, sessionId, virtualPath, timestamp,
            "The edge reported an incomplete upload.", cancellationToken);
        if (!failed) return false;
        CameraUploadAdmission? admission = await store.FindCompletionAsync(loginId, username, sessionId, virtualPath, timestamp, cancellationToken);
        if (admission is not null)
        {
            PublishFailure(admission);
            await ingest.QuarantineAdmittedAsync(admission.Id, "The edge reported an incomplete upload.", cancellationToken);
        }
        return true;
    }

    public async Task CreateDirectoryAsync(Guid loginId, string virtualPath, CancellationToken cancellationToken)
    {
        ValidateVirtualPath(virtualPath, allowDirectory: true);
        Guid sourceId = await FindSourceIdAsync(loginId, cancellationToken)
            ?? throw new CameraIngressRejectedException("The camera login is unavailable.");
        await directories.CreateDirectoryAsync(sourceId, virtualPath, cancellationToken);
    }

    public async Task<CameraFileInfo?> GetFileInfoAsync(Guid loginId, string virtualPath, CancellationToken cancellationToken)
    {
        ValidateVirtualPath(virtualPath, allowDirectory: true);
        Guid? sourceId = await FindSourceIdAsync(loginId, cancellationToken);
        return sourceId is null ? null : await FindFileOrDirectoryAsync(sourceId.Value, virtualPath, cancellationToken);
    }

    public async Task<IReadOnlyList<CameraFileInfo>> GetDirectoryAsync(Guid loginId, string virtualPath, CancellationToken cancellationToken)
    {
        ValidateVirtualPath(virtualPath, allowDirectory: true);
        Guid? sourceId = await FindSourceIdAsync(loginId, cancellationToken);
        if (sourceId is null) return [];
        CameraFileInfo[] childDirectories = [.. (await directories.GetDirectoryAsync(sourceId.Value, virtualPath, cancellationToken))];
        CameraFileInfo[] files = [.. (await store.GetFilesAsync(sourceId.Value, virtualPath, cancellationToken))];
        return childDirectories.Concat(files.Where(file => childDirectories.All(directory => directory.Name != file.Name))).ToArray();
    }

    public async Task<(long Files, long Size)> GetDirectorySizeAsync(Guid loginId, CancellationToken cancellationToken)
    {
        Guid? sourceId = await FindSourceIdAsync(loginId, cancellationToken);
        return sourceId is null ? (0, 0) : await store.GetDirectorySizeAsync(sourceId.Value, cancellationToken);
    }

    private async Task<Guid?> FindSourceIdAsync(Guid loginId, CancellationToken cancellationToken) =>
        (await store.FindLoginAsync(loginId, cancellationToken))?.SourceId;

    public Task<CameraLogin?> FindLoginAsync(Guid loginId, CancellationToken cancellationToken) =>
        store.FindLoginAsync(loginId, cancellationToken);

    private async Task<CameraFileInfo?> FindFileOrDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken) =>
        await store.FindFileAsync(sourceId, virtualPath, cancellationToken) ?? await directories.GetFileInfoAsync(sourceId, virtualPath, cancellationToken);

    public Task<IReadOnlyList<CameraTransferStatus>> GetStatusAsync(Guid? sourceId, CancellationToken cancellationToken) =>
        store.GetStatusAsync(sourceId, cancellationToken);

    public async Task RecoverAsync(int transferTimeoutSeconds, CancellationToken cancellationToken)
    {
        IReadOnlyList<CameraUploadAdmission> beforeCleanup = await store.GetRecoverableAsync(cancellationToken);
        var completedDuringRecovery = new HashSet<Guid>();
        foreach (CameraUploadAdmission admission in beforeCleanup)
        {
            if (admission.State is not (CameraTransferState.Received or CameraTransferState.Handoff)
                || admission.CompletionTimestamp is not long completedAt) continue;

            string expectedName = admission.VirtualPath[(admission.VirtualPath.LastIndexOf('/') + 1)..];
            IngestResult? committed = await ingest.RecoverAdmittedAsync(admission.Id, admission.SourceId, expectedName,
                admission.Size ?? -1, cancellationToken);
            if (committed is null) continue;

            await store.CompleteAsync(admission.Id, committed.Asset, completedAt, cancellationToken);
            completedDuringRecovery.Add(admission.Id);
        }

        long now = checked(timeProvider.GetUtcNow().ToUnixTimeMilliseconds() * 1_000_000);
        long staleBefore = now - checked(transferTimeoutSeconds * 1_000_000_000L);
        await store.QuarantineStaleAsync(staleBefore, cancellationToken);
        foreach (CameraUploadAdmission admission in beforeCleanup)
        {
            if (admission.State == CameraTransferState.Completed || completedDuringRecovery.Contains(admission.Id)) continue;
            bool interrupted = admission.State is CameraTransferState.Admitted or CameraTransferState.Receiving or CameraTransferState.Handoff or CameraTransferState.Failed
                || admission.State == CameraTransferState.Received && admission.CompletionTimestamp is not null;
            bool expiredWithoutCompletion = admission.State == CameraTransferState.Received
                && admission.CompletionTimestamp is null && admission.AdmissionTimestamp < staleBefore;
            if (interrupted || expiredWithoutCompletion)
            {
                await ingest.QuarantineAdmittedAsync(admission.Id, "The edge handoff was interrupted before completion was proven.", cancellationToken);
                await store.QuarantineAsync(admission.Id, "The edge handoff was interrupted before completion was proven.", cancellationToken);
            }
        }
    }

    private static void ValidateVirtualPath(string path, bool allowDirectory)
    {
        if (path.Length > 1_024 || path.Length == 0 || path[0] != '/' || path.Contains('\\') || path.Contains('\0') || path.Contains("//", StringComparison.Ordinal))
            throw new CameraIngressRejectedException("The virtual path is invalid.");
        if (!allowDirectory && path.EndsWith("/", StringComparison.Ordinal))
            throw new CameraIngressRejectedException("A file path is required.");
        if (path.Split('/').Any(segment => segment is "." or ".." || segment.Contains(':')))
            throw new CameraIngressRejectedException("The virtual path is invalid.");
    }
}
