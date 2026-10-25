using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Assets;

namespace ShutterSpool.Application.Ingest;

public sealed class IngestService(IIngestJournal journal, ISpool spool, TimeProvider timeProvider, ISpoolQuotaStore? quotaStore = null)
{
    public Task CheckAdmissionAsync(long? maximumUploadBytes, CancellationToken cancellationToken) =>
        spool.CheckAdmissionAsync(maximumUploadBytes, cancellationToken);

    internal Task CheckAdmittedAdmissionAsync(long requiredFreeBytes, CancellationToken cancellationToken) =>
        (spool as IAdmittedSpool ?? throw new InvalidOperationException("The configured spool cannot accept admitted uploads."))
            .CheckAdmittedAdmissionAsync(requiredFreeBytes, cancellationToken);

    internal string GetAdmittedReceiveKey(Guid assetId) =>
        (spool as IAdmittedSpool ?? throw new InvalidOperationException("The configured spool cannot accept admitted uploads."))
            .GetReceiveKey(assetId);

    internal async Task<bool> ReserveCameraAdmissionAsync(Guid operationId, Guid assetId, long maximumUploadBytes,
        string receiveKey, CancellationToken cancellationToken)
    {
        if (quotaStore is null) return true;
        if (maximumUploadBytes <= 0 || operationId == Guid.Empty || assetId == Guid.Empty)
            throw new IOException("The admitted upload cannot be reserved.");
        return await quotaStore.TryReserveAsync(new(operationId, assetId, maximumUploadBytes, 0, [receiveKey],
            maximumUploadBytes), cancellationToken);
    }

    internal Task ReleaseUnadmittedQuotaReservationAsync(Guid operationId, CancellationToken cancellationToken) =>
        quotaStore?.ReleaseUnadmittedReservationAsync(operationId, cancellationToken) ?? Task.CompletedTask;

    public Task<IngestResult?> FindCommittedAsync(Guid operationId, CancellationToken cancellationToken) =>
        FindCommittedAndVerifyAsync(operationId, cancellationToken);

    public Task<IngestResult> AcceptAsync(IngestRequest request, CancellationToken cancellationToken) =>
        AcceptCoreAsync(request, cancellationToken);

    internal async Task<long> ReceiveAdmittedAsync(IngestRequest request, CancellationToken cancellationToken,
        string? admittedReceiveKey = null, Guid? admittedAssetId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OriginalName);
        ArgumentNullException.ThrowIfNull(request.Content);
        if (request.OperationId is not Guid operationId || request.MaximumUploadBytes is not long maximumBytes || maximumBytes <= 0)
            throw new InvalidOperationException("An admitted receive requires its durable operation ID and finite upload limit.");

        IAdmittedSpool admittedSpool = spool as IAdmittedSpool
            ?? throw new InvalidOperationException("The configured spool cannot receive admitted uploads.");
        if (await journal.FindOperationAsync(operationId, cancellationToken) is not null)
            throw new IOException("The admitted ingest operation already exists.");

        Guid assetId = admittedAssetId ?? Guid.NewGuid();
        if (assetId == Guid.Empty) throw new InvalidOperationException("The admitted upload asset identity is invalid.");
        string partialKey = spool.GetPartialKey(request.SourceId, assetId);
        string receiveKey = admittedReceiveKey ?? admittedSpool.GetReceiveKey(assetId);
        var operation = new IngestOperation(operationId, assetId, request.SourceId, request.OriginalName, partialKey,
            IngestOperationState.AwaitingSource, receiveKey);
        await journal.CreateAsync(operation, cancellationToken);

        SpoolWrite receipt = await admittedSpool.ReceiveAdmittedAsync(receiveKey, partialKey, request.Content, maximumBytes, cancellationToken);
        if (!string.Equals(receipt.FinalKey, receiveKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The spool received bytes under a different key from the journaled key.");
        await journal.MarkReceivedAsync(operationId, receipt, cancellationToken);
        await RecordQuotaWriteAsync(operationId, assetId, [receipt.FinalKey, receipt.PartialKey], maximumBytes,
            receipt.Size, maximumBytes, cancellationToken);
        return receipt.Size;
    }

    internal async Task<IngestResult?> FindAdmittedResultAsync(Guid operationId, Guid sourceId, string originalName, long expectedSize,
        CancellationToken cancellationToken)
    {
        IngestOperation? operation = await journal.FindOperationAsync(operationId, cancellationToken);
        return operation is null ? null : await FindAdmittedResultAsync(operation, sourceId, originalName, expectedSize, cancellationToken);
    }

    private async Task<IngestResult?> FindAdmittedResultAsync(IngestOperation operation, Guid sourceId, string originalName,
        long expectedSize, CancellationToken cancellationToken)
    {
        if (operation?.State != IngestOperationState.Committed || operation.SourceId != sourceId
            || !string.Equals(operation.OriginalName, originalName, StringComparison.Ordinal) || operation.Size != expectedSize)
            return null;
        IngestResult? result = await journal.FindCommittedResultAsync(operation.Id, cancellationToken);
        return result is not null && result.Asset.SourceId == sourceId && result.Asset.Size == expectedSize
            && string.Equals(result.Asset.OriginalName, originalName, StringComparison.Ordinal) ? result : null;
    }

    internal Task<IngestOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
        journal.FindOperationAsync(operationId, cancellationToken);

    internal async Task<IngestResult> FinalizeAdmittedAsync(Guid operationId, Guid sourceId, string originalName, long expectedSize,
        CancellationToken cancellationToken)
    {
        IngestOperation operation = await journal.FindOperationAsync(operationId, cancellationToken)
            ?? throw new IOException("The admitted ingest receipt is unavailable.");
        return await FinalizeAdmittedAsync(operation, sourceId, originalName, expectedSize, cancellationToken);
    }

    internal async Task<IngestResult> FinalizeAdmittedAsync(IngestOperation operation, Guid sourceId, string originalName,
        long expectedSize, CancellationToken cancellationToken)
    {
        if (operation.State == IngestOperationState.Committed)
        {
            IngestResult? replay = await FindAdmittedResultAsync(operation, sourceId, originalName, expectedSize, cancellationToken);
            return replay ?? throw new IOException("The committed admitted ingest does not match its completion event.");
        }
        if (operation.State != IngestOperationState.Received || operation.SourceId != sourceId
            || !string.Equals(operation.OriginalName, originalName, StringComparison.Ordinal) || operation.Size != expectedSize
            || operation.FinalKey is null || operation.Sha256 is null)
            throw new IOException("The admitted ingest receipt does not match the successful completion event.");

        await RecordQuotaWriteAsync(operation.Id, operation.AssetId, [operation.FinalKey, operation.PartialKey],
            operation.Size.Value, operation.Size.Value, operation.Size.Value, cancellationToken);
        await journal.CommitAsync(operation.Id, timeProvider.GetUtcNow(), cancellationToken);
        return await journal.FindCommittedResultAsync(operation.Id, cancellationToken)
            ?? throw new IOException("The admitted ingest result was not durably published.");
    }

    internal async Task<IngestResult?> RecoverAdmittedAsync(Guid operationId, Guid sourceId, string originalName, long expectedSize,
        CancellationToken cancellationToken)
    {
        IngestOperation? operation = await journal.FindOperationAsync(operationId, cancellationToken);
        if (operation is null || operation.SourceId != sourceId
            || !string.Equals(operation.OriginalName, originalName, StringComparison.Ordinal)) return null;
        if (operation.State == IngestOperationState.Committed)
        {
            IngestResult? committed = await VerifyCommittedAsync(operation, cancellationToken);
            return committed?.Asset.Size == expectedSize ? committed : null;
        }
        if (operation.State is not (IngestOperationState.AwaitingSource or IngestOperationState.Received)) return null;

        IAdmittedSpool admittedSpool = spool as IAdmittedSpool
            ?? throw new InvalidOperationException("The configured spool cannot recover admitted uploads.");
        SpoolWrite? verified = await admittedSpool.VerifyReceivedAsync(operation, expectedSize, cancellationToken);
        if (verified is null) return null;
        if (operation.State == IngestOperationState.AwaitingSource)
            await journal.MarkReceivedAsync(operationId, verified, cancellationToken);
        else if (operation.Size != verified.Size || !string.Equals(operation.Sha256, verified.Sha256, StringComparison.Ordinal))
            return null;

        await RecordQuotaWriteAsync(operationId, operation.AssetId, [verified.FinalKey, verified.PartialKey],
            verified.Size, verified.Size, verified.Size, cancellationToken);
        await journal.CommitAsync(operationId, timeProvider.GetUtcNow(), cancellationToken);
        return await journal.FindCommittedResultAsync(operationId, cancellationToken);
    }

    internal async Task QuarantineAdmittedAsync(Guid operationId, string reason, CancellationToken cancellationToken)
    {
        IngestOperation? operation = await journal.FindOperationAsync(operationId, cancellationToken);
        if (operation is null || operation.State is IngestOperationState.Committed or IngestOperationState.Quarantined) return;
        await spool.QuarantineAsync(operation, reason, cancellationToken);
        await journal.QuarantineAsync(operationId, reason, cancellationToken);
        if (quotaStore is not null) await quotaStore.ReleaseReservationAsync(operationId, cancellationToken);
    }

    private async Task<IngestResult> AcceptCoreAsync(IngestRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OriginalName);

        IngestOperation? existing = request.OperationId is Guid existingOperationId
            ? await journal.FindOperationAsync(existingOperationId, cancellationToken)
            : null;
        if (existing is not null)
        {
            if (existing.State == IngestOperationState.Committed)
            {
                IngestResult? committed = await VerifyCommittedAsync(existing, cancellationToken);
                if (committed is null) throw new IOException("The committed ingest operation has no recoverable asset result.");
                if (committed.Asset.SourceId != request.SourceId || !string.Equals(committed.Asset.OriginalName, request.OriginalName, StringComparison.Ordinal))
                    throw new InvalidOperationException("The ingest operation belongs to a different source or original name.");
                return committed;
            }
            throw new IOException("The ingest operation is incomplete and requires recovery.");
        }
        ArgumentNullException.ThrowIfNull(request.Content);
        Guid assetId = Guid.NewGuid();
        Guid operationId = request.OperationId ?? Guid.NewGuid();
        string partialKey = spool.GetPartialKey(request.SourceId, assetId);
        QuotaReservation? reservation = await ReserveQuotaAsync(request, operationId, assetId, partialKey,
            cancellationToken);

        await spool.CheckAdmissionAsync(request.MaximumUploadBytes, cancellationToken);
        var operation = new IngestOperation(operationId, assetId, request.SourceId, request.OriginalName, partialKey, IngestOperationState.Receiving);
        await journal.CreateAsync(operation, cancellationToken);
        SpoolWrite write = await WriteUploadAsync(request, assetId, partialKey, reservation, cancellationToken);
        if (!string.Equals(write.PartialKey, operation.PartialKey, StringComparison.Ordinal)) throw new InvalidOperationException("The spool wrote a different partial key from the journaled key.");
        if (reservation is not null)
            await RecordQuotaWriteAsync(operationId, assetId, [write.FinalKey, write.PartialKey],
                checked(reservation.MaximumBytes * 2), checked(write.Size * 2),
                checked(reservation.MaximumBytes * 2), cancellationToken);
        await journal.MarkPromotedAsync(operation.Id, write, cancellationToken);
        DateTimeOffset ingestedAt = timeProvider.GetUtcNow();
        await journal.CommitAsync(operation.Id, ingestedAt, cancellationToken);
        return new IngestResult(new Asset(assetId, request.SourceId, request.OriginalName, write.FinalKey, write.Size, write.Sha256, ingestedAt, operation.Id));
    }

    private async Task<QuotaReservation?> ReserveQuotaAsync(IngestRequest request, Guid operationId, Guid assetId,
        string partialKey, CancellationToken cancellationToken)
    {
        if (quotaStore is null) return null;
        bool quotaActive = (await quotaStore.GetSettingsAsync(cancellationToken)).IsActive;
        if (!quotaActive && request.MaximumUploadBytes is null) return null;

        if (request.MaximumUploadBytes is long maximumBytes && maximumBytes > 0
            && spool is IQuotaAwareSpool quotaAwareSpool && maximumBytes <= long.MaxValue / 2)
        {
            IReadOnlyList<string> keys = quotaAwareSpool.GetReservationKeys(request.SourceId, assetId);
            if (keys.Count != 2 || keys[0] != partialKey)
                throw new IOException("The spool could not provide a safe reservation.");
            if (!await quotaStore.TryReserveAsync(new(operationId, assetId, maximumBytes, 0, keys,
                    checked(maximumBytes * 2)), cancellationToken))
                throw new IOException("Spool capacity is unavailable.");
            return new(keys[1], maximumBytes);
        }

        if (quotaActive)
            throw new IOException("A finite, reservable upload size is required while quota admission is active.");
        return null;
    }

    private Task<SpoolWrite> WriteUploadAsync(IngestRequest request, Guid assetId, string partialKey,
        QuotaReservation? reservation, CancellationToken cancellationToken) => reservation is not null
        ? ((IQuotaAwareSpool)spool).WriteReservedAsync(request.SourceId, assetId, partialKey, reservation.FinalKey,
            request.Content, reservation.MaximumBytes, cancellationToken)
        : spool.WriteAsync(request.SourceId, assetId, request.Content, request.MaximumUploadBytes, cancellationToken);

    private sealed record QuotaReservation(string FinalKey, long MaximumBytes);

    private async Task<IngestResult?> FindCommittedAndVerifyAsync(Guid operationId, CancellationToken cancellationToken)
    {
        IngestOperation? operation = await journal.FindOperationAsync(operationId, cancellationToken);
        return operation is null ? null : await VerifyCommittedAsync(operation, cancellationToken);
    }

    private async Task<IngestResult?> VerifyCommittedAsync(IngestOperation operation, CancellationToken cancellationToken)
    {
        if (operation.State != IngestOperationState.Committed) return null;
        Guid operationId = operation.Id;
        IngestResult? result = await journal.FindCommittedResultAsync(operationId, cancellationToken);
        if (result is null) return null;
        SpoolWrite? bytes = await spool.FindFinalAsync(operation, cancellationToken);
        if (bytes is null || bytes.Size != result.Asset.Size || !string.Equals(bytes.Sha256, result.Asset.Sha256, StringComparison.Ordinal))
            throw new IOException("The committed ingest bytes could not be verified.");
        return result;
    }

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IngestOperation> operations = await journal.GetAllAsync(cancellationToken);
        var erasureReservedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (IngestOperation operation in operations)
        {
            OriginalRemovalRecovery removalRecovery = await journal.GetOriginalRemovalRecoveryAsync(operation.AssetId,
                cancellationToken);
            if (removalRecovery.IsAuthorized)
            {
                erasureReservedKeys.UnionWith(removalRecovery.ProtectedSpoolKeys);
                continue;
            }
            if (operation.State == IngestOperationState.Quarantined) continue;
            if (operation.State is IngestOperationState.AwaitingSource or IngestOperationState.Received) continue;
            SpoolWrite? final = await spool.FindFinalAsync(operation, cancellationToken);
            if (final is not null && operation.State != IngestOperationState.Committed)
            {
                await RecordQuotaWriteAsync(operation.Id, operation.AssetId, [final.FinalKey, final.PartialKey],
                    checked((operation.Size ?? final.Size) * 2), checked(final.Size * 2),
                    checked((operation.Size ?? final.Size) * 2), cancellationToken);
                await journal.MarkPromotedAsync(operation.Id, final, cancellationToken);
                await journal.CommitAsync(operation.Id, timeProvider.GetUtcNow(), cancellationToken);
                continue;
            }
            if (final is not null) continue;

            await spool.QuarantineAsync(operation, "Incomplete ingest could not be proven complete.", cancellationToken);
            await journal.QuarantineAsync(operation.Id, "Incomplete ingest could not be proven complete.", cancellationToken);
        }
        operations = await journal.GetAllAsync(cancellationToken);
        erasureReservedKeys.UnionWith(operations.SelectMany(operation => new[] { operation.PartialKey, operation.FinalKey })
            .Where(key => key is not null).Cast<string>());
        await spool.QuarantineUntrackedAsync(erasureReservedKeys, cancellationToken);
    }

    private async Task RecordQuotaWriteAsync(Guid operationId, Guid assetId, IReadOnlyList<string> keys,
        long reservedBytes, long writtenBytes, long filesystemReservedBytes, CancellationToken cancellationToken)
    {
        if (quotaStore is null || !(await quotaStore.GetSettingsAsync(cancellationToken)).IsActive) return;
        if (!await quotaStore.UpdateReservationAsync(operationId, assetId, keys, reservedBytes, writtenBytes,
            filesystemReservedBytes, cancellationToken))
            throw new IOException("The admitted spool reservation could not be reconciled.");
    }
}
