using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Assets;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfIngestJournal(ShutterSpoolDbContext database, IQuotaReservationCommitter? quotaCommitter = null) : IIngestJournal
{
    private const int MaximumErasureTransientKeys = 64;

    public async Task<IngestResult?> FindCommittedResultAsync(Guid operationId, CancellationToken cancellationToken)
    {
        AssetEntity? asset = await database.Assets.AsNoTracking().Where(candidate => candidate.State == AssetState.Spooled.ToString()
                && !database.PrivacyErasureAssets.Any(erased => erased.AssetId == candidate.Id))
            .SingleOrDefaultAsync(candidate => candidate.RecoveryCorrelation == operationId, cancellationToken);
        return asset is null ? null : new IngestResult(new Asset(asset.Id, asset.SourceId, asset.OriginalName, asset.SpoolKey,
            asset.Size, asset.Sha256, asset.IngestedAt, asset.RecoveryCorrelation, Enum.Parse<AssetState>(asset.State)));
    }

    public async Task<IngestOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        IngestOperationEntity? operation = await database.Operations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == operationId, cancellationToken);
        return operation is null ? null : new IngestOperation(operation.Id, operation.AssetId, operation.SourceId, operation.OriginalName,
            operation.PartialKey, Enum.Parse<IngestOperationState>(operation.State), operation.FinalKey, operation.Size, operation.Sha256, operation.QuarantineReason);
    }

    public async Task<OriginalRemovalRecovery> GetOriginalRemovalRecoveryAsync(Guid assetId,
        CancellationToken cancellationToken)
    {
        PrivacyErasureAssetEntity? item = await database.PrivacyErasureAssets.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AssetId == assetId, cancellationToken);
        if (item is null)
        {
            bool retentionRemoval = await database.AssetRemovalOperations.AsNoTracking()
                .AnyAsync(candidate => candidate.AssetId == assetId, cancellationToken);
            return new(retentionRemoval, []);
        }
        if (string.IsNullOrWhiteSpace(item.SpoolKey) || item.KnownTransientKeys is null
            || item.KnownTransientKeys.Length > MaximumErasureTransientKeys
            || item.KnownTransientKeys.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException("The persisted privacy-erasure storage identity is invalid.");
        return new(true, [item.SpoolKey, .. item.KnownTransientKeys]);
    }

    public async Task CreateAsync(IngestOperation operation, CancellationToken cancellationToken)
    {
        database.Operations.Add(new IngestOperationEntity
        {
            Id = operation.Id, AssetId = operation.AssetId, SourceId = operation.SourceId, OriginalName = operation.OriginalName,
            PartialKey = operation.PartialKey, State = operation.State.ToString(), FinalKey = operation.FinalKey,
        });
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkPromotedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken)
    {
        IngestOperationEntity operation = await database.Operations.SingleAsync(candidate => candidate.Id == operationId, cancellationToken);
        operation.State = IngestOperationState.Promoted.ToString();
        operation.FinalKey = write.FinalKey;
        operation.Size = write.Size;
        operation.Sha256 = write.Sha256;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkReceivedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken)
    {
        IngestOperationEntity operation = await database.Operations.SingleAsync(candidate => candidate.Id == operationId, cancellationToken);
        if (operation.State is not (nameof(IngestOperationState.AwaitingSource) or nameof(IngestOperationState.Received)))
            throw new InvalidOperationException("The ingest operation is not awaiting a source receipt.");
        if (operation.FinalKey is not null && !string.Equals(operation.FinalKey, write.FinalKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The received bytes do not match the operation's spool key.");
        operation.State = IngestOperationState.Received.ToString();
        operation.FinalKey = write.FinalKey;
        operation.Size = write.Size;
        operation.Sha256 = write.Sha256;
        await database.SaveChangesAsync(cancellationToken);
    }

    public async Task CommitAsync(Guid operationId, DateTimeOffset ingestedAt, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        IngestOperationEntity operation = await database.Operations.FromSqlInterpolated(
                $"SELECT * FROM ingest_operations WHERE \"Id\" = {operationId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        if (operation.State == IngestOperationState.Committed.ToString())
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (await database.PrivacyErasureAssets.AnyAsync(item => item.AssetId == operation.AssetId,
                cancellationToken))
            throw new InvalidOperationException("The asset is suppressed by a completed privacy erasure.");
        if (operation.State is not (nameof(IngestOperationState.Promoted) or nameof(IngestOperationState.Received)))
            throw new InvalidOperationException("A promoted spool record or successful source receipt is required before commit.");
        if (operation.FinalKey is null || operation.Size is null || operation.Sha256 is null) throw new InvalidOperationException("A promoted spool record is required before commit.");

        int retentionRevision = await database.RetentionPolicies.AsNoTracking().Where(policy => policy.Id == 1)
            .Select(policy => (int?)policy.Revision).SingleOrDefaultAsync(cancellationToken) ?? 1;
        database.Assets.Add(new AssetEntity
        {
            Id = operation.AssetId, SourceId = operation.SourceId, OriginalName = operation.OriginalName, SpoolKey = operation.FinalKey,
            Size = operation.Size.Value, Sha256 = operation.Sha256, IngestedAt = ingestedAt, RecoveryCorrelation = operation.Id, State = AssetState.Spooled.ToString(),
            RetentionPolicyRevision = retentionRevision,
        });
        operation.State = IngestOperationState.Committed.ToString();
        if (quotaCommitter is not null)
            await quotaCommitter.CompleteReservationAsync(operationId, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private Task<int> LockRoutingAsync(CancellationToken cancellationToken) =>
        database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))",
            cancellationToken);

    public async Task<IReadOnlyList<IngestOperation>> GetAllAsync(CancellationToken cancellationToken) => await database.Operations
        .Select(operation => new IngestOperation(operation.Id, operation.AssetId, operation.SourceId, operation.OriginalName, operation.PartialKey,
            Enum.Parse<IngestOperationState>(operation.State), operation.FinalKey, operation.Size, operation.Sha256, operation.QuarantineReason))
        .ToListAsync(cancellationToken);

    public async Task QuarantineAsync(Guid operationId, string reason, CancellationToken cancellationToken)
    {
        IngestOperationEntity operation = await database.Operations.SingleAsync(candidate => candidate.Id == operationId, cancellationToken);
        operation.State = IngestOperationState.Quarantined.ToString();
        operation.QuarantineReason = reason;
        AssetEntity? asset = await database.Assets.SingleOrDefaultAsync(candidate => candidate.RecoveryCorrelation == operationId, cancellationToken);
        if (asset is not null) asset.State = AssetState.Quarantined.ToString();
        await database.SaveChangesAsync(cancellationToken);
    }
}
