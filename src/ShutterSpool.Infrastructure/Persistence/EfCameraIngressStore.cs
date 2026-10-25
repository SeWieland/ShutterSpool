using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCameraIngressStore(ShutterSpoolDbContext database, TimeProvider timeProvider) : ICameraIngressStore
{
    public async Task<CameraLogin?> CreateLoginAsync(Guid sourceId, string username, long credentialGeneration, CancellationToken cancellationToken,
        SourceConnectionProfile connectionProfile)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        SourceCredentialEntity? credential = await LockCredentialByUsernameAsync(username, cancellationToken);
        if (credential is null || credential.SourceId != sourceId || !credential.IsIngressEnabled
            || credential.CredentialGeneration != credentialGeneration) return null;
        SourceEntity? source = await LockSourceAsync(sourceId, cancellationToken);
        if (source is null || source.IsArchived || source.ConnectionProfile != connectionProfile) return null;

        var login = new CameraLoginEntity
        {
            Id = Guid.NewGuid(), SourceId = credential.SourceId, Username = credential.Username, VirtualRoot = credential.VirtualRoot,
            CredentialGeneration = credential.CredentialGeneration, ConnectionProfile = connectionProfile, CreatedAt = timeProvider.GetUtcNow(),
        };
        database.CameraLogins.Add(login);
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Map(login);
    }

    public async Task<CameraLogin?> FindLoginAsync(Guid loginId, CancellationToken cancellationToken)
    {
        CameraLoginEntity? login = await database.CameraLogins.AsNoTracking().SingleOrDefaultAsync(item => item.Id == loginId, cancellationToken);
        return login is null ? null : Map(login);
    }

    public async Task<CameraUploadAdmission?> AdmitAsync(Guid loginId, string username, string sessionId, string virtualPath, long timestamp,
        Guid admissionId, Guid assetId, long maximumUploadBytes, string receiveKey,
        int maximumConcurrentTransfers, int transferTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (admissionId == Guid.Empty || assetId == Guid.Empty || maximumUploadBytes <= 0
            || string.IsNullOrWhiteSpace(receiveKey) || receiveKey.Length > 1_024 || receiveKey.Contains('\\')
            || Path.IsPathRooted(receiveKey) || receiveKey.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("The upload admission identity or storage key is invalid.");
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        if (transaction is not null)
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-camera-admission', 0))", cancellationToken);
        long staleBeforeTimestamp = timestamp - checked(transferTimeoutSeconds * 1_000_000_000L);
        await QuarantineStaleAsync(staleBeforeTimestamp, cancellationToken);
        CameraLoginEntity? knownLogin = await database.CameraLogins.AsNoTracking().SingleOrDefaultAsync(item => item.Id == loginId, cancellationToken);
        if (knownLogin is null || knownLogin.Username != username) return null;
        SourceCredentialEntity? credential = await LockCredentialAsync(knownLogin.SourceId, cancellationToken);
        if (credential is null || !credential.IsIngressEnabled || credential.CredentialGeneration != knownLogin.CredentialGeneration) return null;
        SourceEntity? source = await LockSourceAsync(knownLogin.SourceId, cancellationToken);
        if (source is null || source.IsArchived || knownLogin.ConnectionProfile != source.ConnectionProfile) return null;
        CameraLoginEntity? login = await LockLoginAsync(loginId, cancellationToken);
        if (login is null || login.Username != username || login.CredentialGeneration != credential.CredentialGeneration
            || login.ConnectionProfile != knownLogin.ConnectionProfile || login.ConnectionProfile != source.ConnectionProfile) return null;

        CameraUploadAdmissionEntity? retry = await database.CameraUploadAdmissions.SingleOrDefaultAsync(item =>
            item.LoginId == loginId && item.SessionId == sessionId && item.VirtualPath == virtualPath && item.AdmissionTimestamp == timestamp, cancellationToken);
        if (retry is not null)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            return Map(retry);
        }
        bool active = await database.CameraUploadAdmissions.AnyAsync(item => item.LoginId == loginId &&
            (item.State == CameraTransferState.Admitted || item.State == CameraTransferState.Receiving
             || item.State == CameraTransferState.Received || item.State == CameraTransferState.Handoff), cancellationToken);
        if (active) return null;
        int activeCount = await database.CameraUploadAdmissions.CountAsync(item => item.State == CameraTransferState.Admitted
            || item.State == CameraTransferState.Receiving || item.State == CameraTransferState.Received
            || item.State == CameraTransferState.Handoff, cancellationToken);
        if (activeCount >= maximumConcurrentTransfers) return null;

        var admission = new CameraUploadAdmissionEntity
        {
            Id = admissionId, AssetId = assetId, MaximumUploadBytes = maximumUploadBytes, ReceiveKey = receiveKey,
            LoginId = loginId, SourceId = login.SourceId, Username = username, SessionId = sessionId,
            VirtualPath = virtualPath, AdmissionTimestamp = timestamp, State = CameraTransferState.Admitted,
        };
        database.CameraUploadAdmissions.Add(admission);
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Map(admission);
    }

    public async Task QuarantineStaleAsync(long beforeTimestamp, CancellationToken cancellationToken)
    {
        IQueryable<CameraUploadAdmissionEntity> stale = database.CameraUploadAdmissions
            .Where(item => item.AdmissionTimestamp < beforeTimestamp
                && (item.State == CameraTransferState.Admitted
                    || item.State == CameraTransferState.Receiving
                    || item.State == CameraTransferState.Received && item.CompletionTimestamp == null));
        if (!database.Database.IsRelational())
        {
            foreach (CameraUploadAdmissionEntity admission in await stale.ToListAsync(cancellationToken))
            {
                admission.State = CameraTransferState.Quarantined;
                admission.FailureReason = "The transfer exceeded its configured deadline and was retained.";
            }
            await database.SaveChangesAsync(cancellationToken);
            return;
        }

        await stale.ExecuteUpdateAsync(updates => updates
            .SetProperty(item => item.State, CameraTransferState.Quarantined)
            .SetProperty(item => item.FailureReason, "The transfer exceeded its configured deadline and was retained."), cancellationToken);
    }

    public async Task<CameraUploadAdmission?> ClaimReceiveAsync(Guid loginId, string virtualPath, CancellationToken cancellationToken)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        await LockLoginAsync(loginId, cancellationToken);
        CameraUploadAdmissionEntity? admission = await database.CameraUploadAdmissions.SingleOrDefaultAsync(item =>
            item.LoginId == loginId && item.VirtualPath == virtualPath && item.State == CameraTransferState.Admitted, cancellationToken);
        if (admission is null) return null;
        admission.State = CameraTransferState.Receiving;
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return Map(admission);
    }

    public async Task MarkReceivedAsync(Guid admissionId, long size, CancellationToken cancellationToken)
    {
        CameraUploadAdmissionEntity admission = await database.CameraUploadAdmissions.SingleAsync(item => item.Id == admissionId, cancellationToken);
        if (admission.State != CameraTransferState.Receiving) throw new InvalidOperationException("The upload is not being received.");
        admission.Size = size;
        admission.State = CameraTransferState.Received;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<CameraUploadAdmission?> FindCompletionAsync(Guid loginId, string username, string sessionId, string virtualPath, long timestamp, CancellationToken cancellationToken)
    {
        CameraUploadAdmissionEntity? recorded = await database.CameraUploadAdmissions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.LoginId == loginId && item.Username == username && item.SessionId == sessionId && item.VirtualPath == virtualPath
            && item.CompletionTimestamp == timestamp && (item.State == CameraTransferState.Completed || item.State == CameraTransferState.Failed), cancellationToken);
        if (recorded is not null) return Map(recorded);

        CameraUploadAdmissionEntity? admission = await database.CameraUploadAdmissions.AsNoTracking().SingleOrDefaultAsync(item =>
            item.LoginId == loginId && item.Username == username && item.SessionId == sessionId && item.VirtualPath == virtualPath
            && item.AdmissionTimestamp <= timestamp && (item.State == CameraTransferState.Received || item.State == CameraTransferState.Handoff)
            && (item.CompletionTimestamp == null || item.CompletionTimestamp == timestamp), cancellationToken);
        return admission is null ? null : Map(admission);
    }

    public async Task<ShutterSpool.Application.Ingress.CameraFileInfo?> FindFileAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken)
    {
        var upload = await database.CameraUploadAdmissions.AsNoTracking()
            .Where(item => item.SourceId == sourceId && item.VirtualPath == virtualPath
                && item.State == CameraTransferState.Completed && item.Size != null)
            .OrderByDescending(item => item.AdmissionTimestamp).FirstOrDefaultAsync(cancellationToken);
        return upload is null ? null : new CameraFileInfo(virtualPath[(virtualPath.LastIndexOf('/') + 1)..], upload.Size!.Value,
            false, DateTimeOffset.FromUnixTimeMilliseconds(upload.AdmissionTimestamp / 1_000_000));
    }

    public async Task<IReadOnlyList<ShutterSpool.Application.Ingress.CameraFileInfo>> GetFilesAsync(Guid sourceId, string virtualDirectory, CancellationToken cancellationToken)
    {
        string prefix = virtualDirectory == "/" ? "/" : virtualDirectory.TrimEnd('/') + "/";
        var uploads = await database.CameraUploadAdmissions.AsNoTracking()
            .Where(item => item.SourceId == sourceId && item.State == CameraTransferState.Completed && item.Size != null && item.VirtualPath.StartsWith(prefix)
                && !item.VirtualPath.Substring(prefix.Length).Contains("/"))
            .GroupBy(item => item.VirtualPath)
            .Select(group => group.OrderByDescending(item => item.AdmissionTimestamp)
                .Select(item => new { item.VirtualPath, item.Size, item.AdmissionTimestamp }).First())
            .ToListAsync(cancellationToken);
        return uploads.Select(item => new CameraFileInfo(item.VirtualPath[(item.VirtualPath.LastIndexOf('/') + 1)..], item.Size!.Value, false,
            DateTimeOffset.FromUnixTimeMilliseconds(item.AdmissionTimestamp / 1_000_000))).ToArray();
    }

    public async Task<(long Files, long Size)> GetDirectorySizeAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        List<long?> sizes = await database.CameraUploadAdmissions.AsNoTracking()
            .Where(item => item.SourceId == sourceId && item.State == CameraTransferState.Completed && item.Size != null)
            .GroupBy(item => item.VirtualPath)
            .Select(group => group.OrderByDescending(item => item.AdmissionTimestamp).Select(item => item.Size).First())
            .ToListAsync(cancellationToken);
        return (sizes.LongCount(size => size is not null), sizes.Sum(size => size ?? 0));
    }

    public async Task<bool> TryBeginHandoffAsync(Guid admissionId, long completionTimestamp, CancellationToken cancellationToken)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        CameraUploadAdmissionEntity? admission = await LockAdmissionAsync(admissionId, cancellationToken);
        if (admission is null || admission.State != CameraTransferState.Received || completionTimestamp < admission.AdmissionTimestamp
            || admission.CompletionTimestamp is not null && admission.CompletionTimestamp != completionTimestamp) return false;
        admission.State = CameraTransferState.Handoff;
        admission.CompletionTimestamp = completionTimestamp;
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task CompleteAsync(Guid admissionId, ShutterSpool.Domain.Assets.Asset asset, long completionTimestamp, CancellationToken cancellationToken)
    {
        CameraUploadAdmissionEntity admission = await database.CameraUploadAdmissions.SingleAsync(item => item.Id == admissionId, cancellationToken);
        bool inHandoff = admission.State == CameraTransferState.Handoff;
        bool releasedHandoff = admission.State == CameraTransferState.Received
            && admission.CompletionTimestamp == completionTimestamp;
        if (!inHandoff && !releasedHandoff) throw new InvalidOperationException("The upload handoff is not active.");
        admission.State = CameraTransferState.Completed;
        admission.CompletionTimestamp = completionTimestamp;
        admission.AssetId = asset.Id;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CameraUploadAdmission>> GetRecoverableAsync(CancellationToken cancellationToken) => await database.CameraUploadAdmissions.AsNoTracking()
        .Where(item => item.State == CameraTransferState.Admitted || item.State == CameraTransferState.Receiving
            || item.State == CameraTransferState.Received || item.State == CameraTransferState.Handoff
            || item.State == CameraTransferState.Completed || item.State == CameraTransferState.Failed)
        .Select(item => new CameraUploadAdmission(item.Id, item.LoginId, item.SourceId, item.Username, item.SessionId, item.VirtualPath,
            item.AdmissionTimestamp, item.State, item.Size, item.CompletionTimestamp, item.AssetId, item.FailureReason))
        .ToListAsync(cancellationToken);

    public async Task QuarantineAsync(Guid admissionId, string reason, CancellationToken cancellationToken)
    {
        CameraUploadAdmissionEntity? admission = await database.CameraUploadAdmissions.SingleOrDefaultAsync(item => item.Id == admissionId, cancellationToken);
        if (admission is null) return;
        admission.State = CameraTransferState.Quarantined;
        admission.FailureReason = reason;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task ReleaseHandoffAsync(Guid admissionId, CancellationToken cancellationToken)
    {
        CameraUploadAdmissionEntity? admission = await database.CameraUploadAdmissions.SingleOrDefaultAsync(item => item.Id == admissionId, cancellationToken);
        if (admission?.State != CameraTransferState.Handoff) return;
        admission.State = CameraTransferState.Received;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> FailAsync(Guid loginId, string username, string sessionId, string virtualPath, long completionTimestamp, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        Guid? candidateId = await database.CameraUploadAdmissions.AsNoTracking().Where(item =>
            item.LoginId == loginId && item.Username == username && item.SessionId == sessionId && item.VirtualPath == virtualPath
            && item.AdmissionTimestamp <= completionTimestamp && (item.State == CameraTransferState.Admitted
                || item.State == CameraTransferState.Receiving || item.State == CameraTransferState.Received)
            && item.CompletionTimestamp == null).Select(item => (Guid?)item.Id).SingleOrDefaultAsync(cancellationToken);
        if (candidateId is not Guid admissionId) return false;
        CameraUploadAdmissionEntity? admission = await LockAdmissionAsync(admissionId, cancellationToken);
        if (admission is null || admission.LoginId != loginId || admission.Username != username || admission.SessionId != sessionId
            || admission.VirtualPath != virtualPath || admission.AdmissionTimestamp > completionTimestamp
            || admission.State is not (CameraTransferState.Admitted or CameraTransferState.Receiving or CameraTransferState.Received)
            || admission.CompletionTimestamp is not null) return false;
        admission.State = CameraTransferState.Failed;
        admission.CompletionTimestamp = completionTimestamp;
        admission.FailureReason = reason;
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<CameraTransferStatus>> GetStatusAsync(Guid? sourceId, CancellationToken cancellationToken) => await database.CameraUploadAdmissions.AsNoTracking()
        .Where(item => sourceId == null || item.SourceId == sourceId)
        .OrderByDescending(item => item.AdmissionTimestamp)
        .Select(item => new CameraTransferStatus(item.Id, item.State, item.AssetId, item.FailureReason))
        .Take(200).ToListAsync(cancellationToken);

    public async Task<CameraTransferActivityPage> GetActivityAsync(IReadOnlyCollection<Guid>? sourceIds,
        IReadOnlyCollection<Guid>? transferIds, int limit, long? beforeAdmissionTimestamp, Guid? beforeTransferId,
        DateTimeOffset observedBefore, CancellationToken cancellationToken)
    {
        IQueryable<CameraUploadAdmissionEntity> query = database.CameraUploadAdmissions.AsNoTracking()
            .Where(item => item.State != CameraTransferState.Admitted);
        if (sourceIds is not null) query = query.Where(item => sourceIds.Contains(item.SourceId));
        if (transferIds is not null) query = query.Where(item => transferIds.Contains(item.Id));
        if (beforeAdmissionTimestamp is long cursorTime && beforeTransferId is Guid cursorId)
            query = query.Where(item => item.AdmissionTimestamp < cursorTime
                || item.AdmissionTimestamp == cursorTime && item.Id.CompareTo(cursorId) < 0);
        CameraUploadAdmissionEntity[] page = await query
            .OrderByDescending(item => item.AdmissionTimestamp).ThenByDescending(item => item.Id)
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        bool hasMore = page.Length > limit;
        CameraUploadAdmissionEntity[] transfers = [.. page.Take(limit)];
        Guid[] selectedTransferIds = [.. transfers.Select(item => item.Id)];
        var committedOperations = await database.Operations.AsNoTracking()
            .Where(item => selectedTransferIds.Contains(item.Id) && item.State == nameof(IngestOperationState.Committed))
            .Select(item => new { item.Id, item.AssetId }).ToDictionaryAsync(item => item.Id, item => item.AssetId, cancellationToken);
        Guid[] assetIds = [.. transfers.Select(item => committedOperations.TryGetValue(item.Id, out Guid committedAsset)
                ? committedAsset : item.AssetId.GetValueOrDefault())
            .Where(item => item != Guid.Empty).Distinct()];
        var captureIds = await database.CaptureAssets.AsNoTracking().Where(item => assetIds.Contains(item.AssetId))
            .Select(item => new { item.AssetId, item.CaptureId, item.IsPrimary }).ToArrayAsync(cancellationToken);
        var previewJobs = await database.PreviewPrewarms.AsNoTracking().Where(item => assetIds.Contains(item.AssetId)
                && item.Profile == "Grid")
            .Select(item => new { item.AssetId, item.CaptureId, item.State, item.RetryAt, item.LeaseUntil }).ToArrayAsync(cancellationToken);
        DateTimeOffset staleBefore = observedBefore.AddSeconds(-60);
        CameraTransferActivity[] items = [.. transfers.Select(transfer =>
        {
            Guid mappedAssetId = committedOperations.TryGetValue(transfer.Id, out Guid committedAsset)
                ? committedAsset : transfer.AssetId.GetValueOrDefault();
            Guid? assetId = mappedAssetId == Guid.Empty ? null : mappedAssetId;
            DateTimeOffset observedAt = DateTimeOffset.FromUnixTimeMilliseconds(transfer.AdmissionTimestamp / 1_000_000);
            Guid? captureId = assetId is Guid linkedAssetId
                ? captureIds.Where(item => item.AssetId == linkedAssetId && item.IsPrimary).Select(item => (Guid?)item.CaptureId).FirstOrDefault()
                    ?? captureIds.Where(item => item.AssetId == linkedAssetId).Select(item => (Guid?)item.CaptureId).FirstOrDefault()
                : null;
            bool isCurrentPrimary = assetId is Guid candidateAsset && captureId is Guid candidateCapture
                && captureIds.Any(item => item.AssetId == candidateAsset && item.CaptureId == candidateCapture && item.IsPrimary);
            bool accepted = assetId is not null && (transfer.State == CameraTransferState.Completed
                || committedOperations.ContainsKey(transfer.Id));
            bool failed = transfer.State is CameraTransferState.Failed or CameraTransferState.Quarantined;
            bool stale = transfer.State is CameraTransferState.Receiving or CameraTransferState.Received or CameraTransferState.Handoff
                && observedAt < staleBefore;
            var job = isCurrentPrimary ? previewJobs.FirstOrDefault(item => item.AssetId == assetId && item.CaptureId == captureId) : null;
            CameraTransferPhase phase = ProjectPhase(transfer.State, accepted, failed, stale, captureId,
                job?.State, job?.RetryAt, job?.LeaseUntil, observedBefore, out bool previewReady);
            if (phase == CameraTransferPhase.PreparingPreview) observedAt = observedBefore;
            return new CameraTransferActivity(transfer.Id, transfer.SourceId, assetId, captureId, phase,
                observedAt, 0, accepted, previewReady, isCurrentPrimary);
        })];
        CameraUploadAdmissionEntity? last = hasMore ? transfers[^1] : null;
        return new(items, hasMore, last?.AdmissionTimestamp, last?.Id);
    }

    private static CameraTransferPhase ProjectPhase(CameraTransferState transferState, bool accepted, bool failed,
        bool stale, Guid? captureId, string? jobState, DateTimeOffset? retryAt, DateTimeOffset? leaseUntil,
        DateTimeOffset observedBefore, out bool previewReady)
    {
        previewReady = false;
        if (accepted && captureId is null) return CameraTransferPhase.AcceptedAwaitingGrouping;
        if (failed) return CameraTransferPhase.FailedUnavailable;
        if (stale) return CameraTransferPhase.Stale;
        if (!accepted)
            return transferState == CameraTransferState.Receiving
                ? CameraTransferPhase.Receiving
                : CameraTransferPhase.Stale;

        if (jobState == "Invalid") return CameraTransferPhase.FailedUnavailable;
        if (jobState == "Pending" && retryAt > observedBefore) return CameraTransferPhase.FailedUnavailable;
        if (jobState == "Processing" && leaseUntil <= observedBefore) return CameraTransferPhase.Stale;
        previewReady = jobState == "Complete";
        return previewReady ? CameraTransferPhase.Ready
            : jobState is "Pending" or "Processing" ? CameraTransferPhase.PreparingPreview
            : CameraTransferPhase.AcceptedAwaitingGrouping;
    }

    private Task<SourceCredentialEntity?> LockCredentialByUsernameAsync(string username, CancellationToken cancellationToken) => database.Database.IsRelational()
        ? database.SourceCredentials.FromSqlInterpolated($"SELECT * FROM source_credentials WHERE \"Username\" = {username} FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
        : database.SourceCredentials.SingleOrDefaultAsync(item => item.Username == username, cancellationToken);

    private Task<SourceCredentialEntity?> LockCredentialAsync(Guid sourceId, CancellationToken cancellationToken) => database.Database.IsRelational()
        ? database.SourceCredentials.FromSqlInterpolated($"SELECT * FROM source_credentials WHERE \"SourceId\" = {sourceId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
        : database.SourceCredentials.SingleOrDefaultAsync(item => item.SourceId == sourceId, cancellationToken);

    private Task<SourceEntity?> LockSourceAsync(Guid sourceId, CancellationToken cancellationToken) => database.Database.IsRelational()
        ? database.Sources.FromSqlInterpolated($"SELECT * FROM sources WHERE \"Id\" = {sourceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
        : database.Sources.SingleOrDefaultAsync(item => item.Id == sourceId, cancellationToken);

    private Task<CameraLoginEntity?> LockLoginAsync(Guid loginId, CancellationToken cancellationToken) => database.Database.IsRelational()
        ? database.CameraLogins.FromSqlInterpolated($"SELECT * FROM camera_logins WHERE \"Id\" = {loginId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
        : database.CameraLogins.SingleOrDefaultAsync(item => item.Id == loginId, cancellationToken);

    private Task<CameraUploadAdmissionEntity?> LockAdmissionAsync(Guid admissionId, CancellationToken cancellationToken) => database.Database.IsRelational()
        ? database.CameraUploadAdmissions.FromSqlInterpolated($"SELECT * FROM camera_upload_admissions WHERE \"Id\" = {admissionId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken)
        : database.CameraUploadAdmissions.SingleOrDefaultAsync(item => item.Id == admissionId, cancellationToken);

    private static CameraLogin Map(CameraLoginEntity login) => new(login.Id, login.SourceId, login.Username, login.VirtualRoot,
        login.CredentialGeneration, login.ConnectionProfile);
    private static CameraUploadAdmission Map(CameraUploadAdmissionEntity admission) => new(admission.Id, admission.LoginId, admission.SourceId,
        admission.Username, admission.SessionId, admission.VirtualPath, admission.AdmissionTimestamp, admission.State,
        admission.Size, admission.CompletionTimestamp, admission.AssetId, admission.FailureReason,
        admission.MaximumUploadBytes, admission.ReceiveKey);
}
