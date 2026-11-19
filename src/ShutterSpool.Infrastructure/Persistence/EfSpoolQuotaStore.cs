using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Workspaces;
using System.Diagnostics.Metrics;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfSpoolQuotaStore(
    ShutterSpoolDbContext database,
    ISpoolAccounting accounting,
    TimeProvider timeProvider, WorkspaceContext? workspace = null) : ISpoolQuotaStore, IQuotaReservationCommitter
{
    private const int MaximumReconciledRows = 100_000;
    private const string ActiveReservation = "Active";
    private const string CommittedReservation = "Committed";
    private const string SpoolCategory = nameof(SpoolQuotaReservationCategory.Spool);
    private const string PreviewCategory = nameof(SpoolQuotaReservationCategory.DiskPreview);
    private const string InvalidReservation = "invalid_reservation";
    private const string UnknownAccounting = "unknown_accounting";
    private const string FilesystemReserve = "filesystem_reserve";
    private const string QuotaCapacity = "quota_capacity";
    private const string AccountingIncomplete = "Filesystem accounting could not complete.";
    private const string FilesNotReconciled = "Filesystem accounting could not reconcile managed files.";
    private static readonly TimeSpan MaximumObservationAge = TimeSpan.FromSeconds(60);
    private static readonly Meter Meter = new("ShutterSpool.Retention");
    private static readonly Counter<long> RejectedReservations = Meter.CreateCounter<long>("quota.admission.rejected");

    public async Task<SpoolQuotaSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        ToContract(await database.SpoolQuotaSettings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken));

    public async Task<SpoolQuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaSettingsEntity settingsEntity = await database.SpoolQuotaSettings.SingleAsync(item => item.Id == 1, cancellationToken);
        SpoolQuotaSettings settings = ToContract(settingsEntity);
        List<SpoolQuotaReservationEntity> reservations = await GetReservationsAsync(cancellationToken);
        if (reservations.Count > MaximumReconciledRows)
        {
            await PersistUnknownAsync(AccountingIncomplete, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return UnknownSnapshot(settings, AccountingIncomplete, timeProvider.GetUtcNow(), long.MaxValue);
        }

        HashSet<string> reservedKeys = reservations.Where(item => item.State == ActiveReservation)
            .SelectMany(item => item.SpoolKeys).ToHashSet(KeyComparer);
        SpoolQuotaObservationEntity observation = await database.SpoolQuotaObservations.SingleAsync(item => item.Id == 1, cancellationToken);
        bool canEstablishMarker = observation.StorageMarker is null
            && (settingsEntity.StorageMarkerAdoptionAuthorized || !await database.Assets.AsNoTracking().AnyAsync(cancellationToken));
        SpoolQuotaInventory inventory = await accounting.ObserveAsync(reservedKeys, observation.StorageMarker,
            canEstablishMarker, cancellationToken);
        ReconcilePreviewReservations(reservations, inventory);
        SpoolQuotaSnapshot snapshot = await BuildSnapshotAsync(settings, reservations, inventory, cancellationToken);
        snapshot = await EstablishStorageIdentityAsync(settings, observation, inventory, snapshot, cancellationToken);
        ApplyObservation(observation, snapshot, reservations);
        if (snapshot.IsKnown)
            RetireCommittedReservations(reservations);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BuildCachedSnapshot(settings, observation,
            ToTotals(reservations.Where(item => item.State == ActiveReservation).ToArray()), timeProvider.GetUtcNow());
    }

    private static void ReconcilePreviewReservations(IEnumerable<SpoolQuotaReservationEntity> reservations,
        SpoolQuotaInventory inventory)
    {
        foreach (SpoolQuotaReservationEntity reservation in reservations.Where(item =>
                     item.State == ActiveReservation && item.Category == PreviewCategory))
        {
            SpoolQuotaFile? retained = inventory.Files.SingleOrDefault(file =>
                reservation.SpoolKeys.Contains(file.Key, KeyComparer));
            if (retained is null) continue;
            reservation.State = CommittedReservation;
            reservation.ReservedBytes = retained.SizeBytes;
            reservation.WrittenBytes = retained.SizeBytes;
            reservation.FilesystemReservedBytes = retained.SizeBytes;
        }
    }

    private async Task<SpoolQuotaSnapshot> EstablishStorageIdentityAsync(SpoolQuotaSettings settings,
        SpoolQuotaObservationEntity observation, SpoolQuotaInventory inventory, SpoolQuotaSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.IsKnown || observation.StorageMarker is not null) return snapshot;
        if (inventory.StorageMarker is null || inventory.StorageMarkerNeedsCommit
            && !await accounting.EstablishStorageMarkerAsync(inventory.StorageMarker, cancellationToken))
            return UnknownSnapshot(settings, "Filesystem accounting could not establish the storage identity.", timeProvider.GetUtcNow());

        observation.StorageMarker = inventory.StorageMarker;
        await ClearStorageMarkerAdoptionAuthorizationAsync(cancellationToken);
        return snapshot;
    }

    private void RetireCommittedReservations(IEnumerable<SpoolQuotaReservationEntity> reservations)
    {
        foreach (SpoolQuotaReservationEntity reservation in reservations.Where(item => item.State == CommittedReservation))
            database.SpoolQuotaReservations.Remove(reservation);
    }

    public async Task<SpoolQuotaSettings?> StageSettingsUpdateAsync(int expectedRevision, SpoolQuotaSettings settings,
        bool authorizeStorageMarkerAdoption, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (workspace is not null && (!settings.IsActive || settings.QuotaBytes is not long quota
            || quota > workspace.Required.AllocatedBytes))
            throw new ArgumentException("Workspace quota admission must remain enabled within its operator allocation.");
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Quota settings must be staged inside the combined retention settings transaction.");
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaSettingsEntity current = await database.SpoolQuotaSettings.SingleAsync(item => item.Id == 1, cancellationToken);
        if (current.Revision != expectedRevision) return null;
        current.IsActive = settings.IsActive;
        current.QuotaBytes = settings.QuotaBytes;
        current.HighWatermarkBytes = settings.HighWatermarkBytes;
        current.RecoveryTargetBytes = settings.RecoveryTargetBytes;
        if (authorizeStorageMarkerAdoption && settings.IsActive)
            current.StorageMarkerAdoptionAuthorized = true;
        current.Revision = checked(expectedRevision + 1);
        return ToContract(current);
    }

    public async Task<SpoolQuotaSettings?> UpdateSettingsAsync(int expectedRevision, SpoolQuotaSettings settings,
        CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaSettings? updated = await StageSettingsUpdateAsync(expectedRevision, settings,
            authorizeStorageMarkerAdoption: false, cancellationToken: cancellationToken);
        if (updated is null) return null;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> TryReserveAsync(SpoolQuotaReservation reservation, CancellationToken cancellationToken)
    {
        if (!IsValid(reservation))
        {
            Reject(InvalidReservation);
            return false;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? existing = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == reservation.OperationId, cancellationToken);
        if (existing is not null)
            return reservation.Category != SpoolQuotaReservationCategory.DiskPreview
                && existing.State == ActiveReservation && IsSame(existing, reservation);

        ReservationTotals totals = await GetReservationTotalsAsync(cancellationToken);
        if (totals.Count > MaximumReconciledRows)
        {
            Reject(UnknownAccounting);
            return false;
        }
        SpoolQuotaSettings settings = ToContract(await database.SpoolQuotaSettings.SingleAsync(item => item.Id == 1, cancellationToken));
        SpoolQuotaObservationEntity observation = await database.SpoolQuotaObservations.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken);
        SpoolQuotaSnapshot snapshot = BuildCachedSnapshot(settings, observation, totals, timeProvider.GetUtcNow());
        string? rejection = GetReservationRejection(settings, snapshot, totals, reservation);
        if (rejection is not null)
        {
            Reject(rejection);
            return false;
        }

        database.SpoolQuotaReservations.Add(new SpoolQuotaReservationEntity
        {
            OperationId = reservation.OperationId,
            AssetId = reservation.AssetId,
            ReservedBytes = reservation.ReservedBytes,
            WrittenBytes = reservation.WrittenBytes,
            FilesystemReservedBytes = reservation.FilesystemReservedBytes,
            SpoolKeys = [.. reservation.SpoolKeys],
            Category = reservation.Category.ToString(),
            CreatedAt = timeProvider.GetUtcNow(),
        });
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static string? GetReservationRejection(SpoolQuotaSettings settings, SpoolQuotaSnapshot snapshot,
        ReservationTotals totals, SpoolQuotaReservation reservation)
    {
        // A bounded admission made before quota activation still needs a durable reservation.
        if (!snapshot.IsKnown) return settings.IsActive ? UnknownAccounting : null;
        if (!HasFilesystemHeadroom(snapshot.FilesystemReserveBytes, snapshot.DriveAvailableBytes,
                totals.FilesystemReservedBytes, reservation.FilesystemReservedBytes))
            return FilesystemReserve;
        if (settings.IsActive && (settings.QuotaBytes is not long quota
            || snapshot.UsedBytes > quota || snapshot.ReservedBytes > quota - snapshot.UsedBytes
            || reservation.ReservedBytes > quota - snapshot.UsedBytes - snapshot.ReservedBytes))
            return QuotaCapacity;
        return null;
    }

    private static bool HasFilesystemHeadroom(long? reserveBytes, long? availableBytes, long existing, long requested)
    {
        if (reserveBytes is not long reserve || availableBytes is not long available || available <= reserve) return false;
        long headroom = available - reserve;
        return existing < headroom && requested < headroom - existing;
    }

    public async Task<bool> UpdateReservationAsync(Guid operationId, Guid? assetId, IReadOnlyList<string> spoolKeys,
        long reservedBytes, long writtenBytes, long filesystemReservedBytes, CancellationToken cancellationToken)
    {
        if (reservedBytes <= 0 || writtenBytes < 0 || writtenBytes > reservedBytes || filesystemReservedBytes < 0
            || !AreValidKeys(spoolKeys))
        {
            Reject(InvalidReservation);
            return false;
        }
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is null) return false;
        if (reservation.State != ActiveReservation) return false;
        reservation.AssetId = assetId;
        reservation.SpoolKeys = [.. spoolKeys];
        reservation.ReservedBytes = reservedBytes;
        reservation.WrittenBytes = writtenBytes;
        reservation.FilesystemReservedBytes = filesystemReservedBytes;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> MarkReservationRetainedAsync(Guid operationId, long actualBytes, CancellationToken cancellationToken)
    {
        if (actualBytes <= 0)
        {
            Reject(InvalidReservation);
            return false;
        }
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is null || reservation.State != ActiveReservation || reservation.Category != PreviewCategory
            || actualBytes > reservation.ReservedBytes) return false;
        reservation.ReservedBytes = actualBytes;
        reservation.WrittenBytes = actualBytes;
        reservation.FilesystemReservedBytes = actualBytes;
        reservation.State = CommittedReservation;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseReservationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is null) return;
        List<SpoolQuotaReservationEntity> reservations = await GetReservationsAsync(cancellationToken);
        if (reservations.Count > MaximumReconciledRows) return;
        SpoolQuotaSettings settings = ToContract(await database.SpoolQuotaSettings.SingleAsync(item => item.Id == 1, cancellationToken));
        SpoolQuotaObservationEntity currentObservation = await database.SpoolQuotaObservations.AsNoTracking()
            .SingleAsync(item => item.Id == 1, cancellationToken);
        HashSet<string> otherKeys = reservations.Where(item => item.OperationId != operationId && item.State == ActiveReservation)
            .SelectMany(item => item.SpoolKeys).ToHashSet(KeyComparer);
        SpoolQuotaInventory inventory = await accounting.ObserveAsync(otherKeys, currentObservation.StorageMarker,
            allowInitializeStorageMarker: false, cancellationToken: cancellationToken);
        if (!inventory.IsKnown || inventory.Files.Any(file => reservation.SpoolKeys.Contains(file.Key, KeyComparer))) return;
        SpoolQuotaSnapshot snapshot = await BuildSnapshotAsync(settings,
            reservations.Where(item => item.OperationId != operationId).ToArray(), inventory, cancellationToken);
        if (!snapshot.IsKnown) return;
        SpoolQuotaObservationEntity observation = await database.SpoolQuotaObservations.SingleAsync(item => item.Id == 1, cancellationToken);
        SpoolQuotaReservationEntity[] others = [.. reservations.Where(item => item.OperationId != operationId)];
        ApplyObservation(observation, snapshot, others);
        observation.StorageMarker = inventory.IsKnown ? inventory.StorageMarker : observation.StorageMarker;
        foreach (SpoolQuotaReservationEntity committed in others.Where(item => item.State == CommittedReservation))
            database.SpoolQuotaReservations.Remove(committed);
        database.SpoolQuotaReservations.Remove(reservation);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReleaseUnadmittedReservationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is null || reservation.State != ActiveReservation || reservation.Category != SpoolCategory
            || reservation.SpoolKeys.Length != 1 || !reservation.SpoolKeys[0].StartsWith("spool/", StringComparison.Ordinal)) return;

        bool hasIngestOperation = await database.Operations.AnyAsync(item => item.Id == operationId, cancellationToken);
        bool hasCameraAdmission = await database.CameraUploadAdmissions.AnyAsync(item => item.Id == operationId, cancellationToken);
        if (hasIngestOperation || hasCameraAdmission) return;

        database.SpoolQuotaReservations.Remove(reservation);
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReconcileReservationsAsync(CancellationToken cancellationToken)
    {
        _ = await GetSnapshotAsync(cancellationToken);
        List<SpoolQuotaReservationEntity> reservations = await GetReservationsAsync(cancellationToken);
        if (reservations.Count > MaximumReconciledRows) return;
        foreach (Guid operationId in reservations.Select(reservation => reservation.OperationId))
        {
            string? operationState = await database.Operations.AsNoTracking().Where(item => item.Id == operationId)
                .Select(item => item.State).SingleOrDefaultAsync(cancellationToken);
            string? admissionState = await database.CameraUploadAdmissions.AsNoTracking()
                .Where(item => item.Id == operationId).Select(item => item.State.ToString()).SingleOrDefaultAsync(cancellationToken);
            if (admissionState is nameof(CameraTransferState.Quarantined) or nameof(CameraTransferState.Failed)
                || operationState is nameof(IngestOperationState.Quarantined)
                || operationState is null && admissionState is null)
                await ReleaseReservationAsync(operationId, cancellationToken);
        }
    }

    public async Task CompleteReservationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A spool reservation must be completed inside the ingest Asset transaction.");
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is not null) reservation.State = CommittedReservation;
    }

    private async Task<SpoolQuotaSnapshot> BuildSnapshotAsync(SpoolQuotaSettings settings,
        IReadOnlyCollection<SpoolQuotaReservationEntity> reservations, SpoolQuotaInventory inventory,
        CancellationToken cancellationToken)
    {
        if (!inventory.IsKnown || reservations.Count > MaximumReconciledRows)
            return UnknownSnapshot(settings, inventory.UnknownReason ?? AccountingIncomplete, inventory.ObservedAt,
                ReservationRemainder(reservations));

        try
        {
            SnapshotEvidence? evidence = await LoadSnapshotEvidenceAsync(inventory, cancellationToken);
            if (evidence is null)
                return UnknownSnapshot(settings, AccountingIncomplete, inventory.ObservedAt,
                    ReservationRemainder(reservations));

            return CalculateSnapshot(settings, reservations, inventory, evidence);
        }
        catch (Exception error) when (error is OverflowException or InvalidOperationException or ArgumentException)
        {
            return UnknownSnapshot(settings, FilesNotReconciled, inventory.ObservedAt);
        }
    }

    private async Task<SnapshotEvidence?> LoadSnapshotEvidenceAsync(SpoolQuotaInventory inventory,
        CancellationToken cancellationToken)
    {
        SpoolQuotaFile[] physical = [.. inventory.Files];
        Dictionary<string, SpoolQuotaFile> managed = physical
            .Where(file => file.Category is SpoolQuotaFileCategory.Spool or SpoolQuotaFileCategory.Incoming)
            .ToDictionary(file => file.Key, KeyComparer);
        List<AssetQuotaRow> assets = await database.Assets.AsNoTracking()
            .Where(asset => asset.State == "Spooled")
            .OrderBy(asset => asset.Id).Take(MaximumReconciledRows + 1)
            .Select(asset => new AssetQuotaRow(asset.Id, asset.SpoolKey, asset.Size,
                database.CaptureAssets.Where(member => member.AssetId == asset.Id)
                    .Select(member => database.Captures.Where(capture => capture.Id == member.CaptureId)
                        .Select(capture => capture.RetentionDisposition).FirstOrDefault()).FirstOrDefault(),
                database.AssetRemovalOperations.Where(operation => operation.AssetId == asset.Id)
                    .Select(operation => operation.State).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        if (assets.Count > MaximumReconciledRows) return null;

        bool hasUnreservedOperation = await database.Operations.AsNoTracking().AnyAsync(operation =>
            operation.State != nameof(IngestOperationState.Committed) && operation.State != nameof(IngestOperationState.Quarantined)
            && !database.SpoolQuotaReservations.Any(reservation => reservation.OperationId == operation.Id), cancellationToken);
        bool hasUnreservedCameraAdmission = await database.CameraUploadAdmissions.AsNoTracking().AnyAsync(admission =>
            admission.State != CameraTransferState.Completed
            && admission.State != CameraTransferState.Failed
            && admission.State != CameraTransferState.Quarantined
            && !database.SpoolQuotaReservations.Any(reservation => reservation.OperationId == admission.Id), cancellationToken);
        var remainingFiles = new HashSet<string>(managed.Keys, KeyComparer);
        foreach (AssetQuotaRow asset in assets)
        {
            if (asset.RemovalState == "Removed") continue;
            if (managed.TryGetValue(asset.SpoolKey, out SpoolQuotaFile? file) && file.SizeBytes == asset.Size)
                remainingFiles.Remove(asset.SpoolKey);
        }
        List<string> transientKeys = [.. remainingFiles];
        Dictionary<string, string> transientOperations = await FindOperationKeysAsync(transientKeys, cancellationToken);
        return new(physical, managed, assets, transientKeys, transientOperations,
            !hasUnreservedOperation && !hasUnreservedCameraAdmission);
    }

    private static SpoolQuotaSnapshot CalculateSnapshot(SpoolQuotaSettings settings,
        IReadOnlyCollection<SpoolQuotaReservationEntity> reservations, SpoolQuotaInventory inventory,
        SnapshotEvidence evidence)
    {
        var originals = CountOriginalBytes(evidence);
        var transient = CountTransientBytes(evidence);
        long quarantine = Sum(evidence.Physical, SpoolQuotaFileCategory.Quarantine);
        long previews = Sum(evidence.Physical, SpoolQuotaFileCategory.DiskPreview);
        if (CountActiveReservationBytes(reservations) is not { } reserved)
            return UnknownSnapshot(settings, AccountingIncomplete, inventory.ObservedAt, ReservationRemainder(reservations));

        long partial = checked(transient.Bytes + reserved.Spool);
        previews = checked(previews + reserved.Previews);
        long used = checked(originals.Active + originals.Trash + partial + (quarantine + previews));
        long total = checked(used + reserved.Remaining);
        long deficit = settings.RecoveryTargetBytes is long target ? Math.Max(0, total - target) : 0;
        string? blocked = GetBlockedReason(settings, inventory.FilesystemReserveBytes, inventory.DriveAvailableBytes,
            used, reserved.Remaining, SumFilesystemReservations(reservations));
        bool known = evidence.HasOnlyReservedOperations && originals.Known && transient.Known;
        return new SpoolQuotaSnapshot(settings, inventory.ObservedAt, known, used, reserved.Remaining,
            originals.Active, originals.Trash, partial, quarantine, previews, inventory.DriveTotalBytes,
            inventory.DriveAvailableBytes, inventory.FilesystemReserveBytes, deficit, blocked, known ? null : FilesNotReconciled);
    }

    private static (long Active, long Trash, bool Known) CountOriginalBytes(SnapshotEvidence evidence)
    {
        long active = 0;
        long trash = 0;
        bool known = true;
        foreach (AssetQuotaRow asset in evidence.Assets)
        {
            if (asset.RemovalState == "Removed") continue;
            if (!evidence.Managed.TryGetValue(asset.SpoolKey, out SpoolQuotaFile? file) || file.SizeBytes != asset.Size)
            {
                known = false;
                continue;
            }
            if (asset.RetentionDisposition is nameof(RetentionDisposition.Trash) or nameof(RetentionDisposition.Purging))
                trash = checked(trash + file.SizeBytes);
            else if (asset.RetentionDisposition is null or nameof(RetentionDisposition.Active))
                active = checked(active + file.SizeBytes);
            else known = false;
        }
        return (active, trash, known);
    }

    private static (long Bytes, bool Known) CountTransientBytes(SnapshotEvidence evidence)
    {
        long partial = 0;
        bool known = true;
        foreach (string key in evidence.TransientKeys)
        {
            SpoolQuotaFile file = evidence.Managed[key];
            if (!evidence.TransientOperations.TryGetValue(key, out string? state))
            {
                known = false;
                continue;
            }
            if (state is nameof(IngestOperationState.Quarantined) or "Failed") known = false;
            partial = checked(partial + file.SizeBytes);
        }
        return (partial, known);
    }

    private static (long Spool, long Previews, long Remaining)? CountActiveReservationBytes(
        IReadOnlyCollection<SpoolQuotaReservationEntity> reservations)
    {
        long reservedPartial = 0;
        long reservedPreviews = 0;
        long reservedRemaining = 0;
        foreach (SpoolQuotaReservationEntity reservation in reservations)
        {
            if (reservation.ReservedBytes <= 0 || reservation.WrittenBytes < 0 || reservation.WrittenBytes > reservation.ReservedBytes
                || reservation.FilesystemReservedBytes < 0 || reservation.State is not (ActiveReservation or CommittedReservation)
                || reservation.Category is not (SpoolCategory or PreviewCategory) || !AreValidKeys(reservation.SpoolKeys))
                return null;
            if (reservation.State != ActiveReservation) continue;
            if (reservation.Category == PreviewCategory) reservedPreviews = checked(reservedPreviews + reservation.WrittenBytes);
            else reservedPartial = checked(reservedPartial + reservation.WrittenBytes);
            reservedRemaining = checked(reservedRemaining + reservation.ReservedBytes - reservation.WrittenBytes);
        }
        return (reservedPartial, reservedPreviews, reservedRemaining);
    }

    private async Task<Dictionary<string, string>> FindOperationKeysAsync(List<string> keys,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(KeyComparer);
        for (int offset = 0; offset < keys.Count; offset += 500)
        {
            string[] batch = [.. keys.Skip(offset).Take(500)];
            var operations = await database.Operations.AsNoTracking()
                .Where(item => batch.Contains(item.PartialKey) || item.FinalKey != null && batch.Contains(item.FinalKey))
                .Select(item => new { item.PartialKey, item.FinalKey, item.State }).ToListAsync(cancellationToken);
            foreach (var operation in operations)
            {
                if (batch.Contains(operation.PartialKey, KeyComparer)) result.TryAdd(operation.PartialKey, operation.State);
                if (operation.FinalKey is not null && batch.Contains(operation.FinalKey, KeyComparer)) result.TryAdd(operation.FinalKey, operation.State);
            }
        }
        return result;
    }

    private async Task LockSettingsAsync(CancellationToken cancellationToken)
    {
        _ = await database.SpoolQuotaSettings.FromSqlRaw("SELECT * FROM spool_quota_settings WHERE \"Id\" = 1 FOR UPDATE")
            .SingleAsync(cancellationToken);
    }

    private async Task<List<SpoolQuotaReservationEntity>> GetReservationsAsync(CancellationToken cancellationToken) =>
        await database.SpoolQuotaReservations.OrderBy(item => item.CreatedAt).Take(MaximumReconciledRows + 1).ToListAsync(cancellationToken);

    private async Task PersistUnknownAsync(string reason, CancellationToken cancellationToken)
    {
        SpoolQuotaObservationEntity observation = await database.SpoolQuotaObservations.SingleAsync(item => item.Id == 1, cancellationToken);
        observation.ObservedAt = timeProvider.GetUtcNow();
        observation.IsKnown = false;
        observation.UnknownReason = reason;
        observation.DriveTotalBytes = null;
        observation.DriveAvailableBytes = null;
        observation.FilesystemReserveBytes = null;
        await database.SaveChangesAsync(cancellationToken);
    }

    private async Task ClearStorageMarkerAdoptionAuthorizationAsync(CancellationToken cancellationToken)
    {
        SpoolQuotaSettingsEntity settings = await database.SpoolQuotaSettings.SingleAsync(item => item.Id == 1, cancellationToken);
        settings.StorageMarkerAdoptionAuthorized = false;
    }

    private static void ApplyObservation(SpoolQuotaObservationEntity observation, SpoolQuotaSnapshot snapshot,
        IReadOnlyCollection<SpoolQuotaReservationEntity> reservations)
    {
        long activeWritten = reservations.Where(item => item.State == ActiveReservation && item.Category == SpoolCategory)
            .Aggregate(0L, (sum, item) => checked(sum + item.WrittenBytes));
        long previewWritten = reservations.Where(item => item.State == ActiveReservation && item.Category == PreviewCategory)
            .Aggregate(0L, (sum, item) => checked(sum + item.WrittenBytes));
        observation.ObservedAt = snapshot.ObservedAt;
        observation.IsKnown = snapshot.IsKnown;
        observation.ActiveBytes = snapshot.ActiveBytes;
        observation.TrashBytes = snapshot.TrashBytes;
        observation.PartialBytes = snapshot.PartialBytes - activeWritten;
        observation.QuarantineBytes = snapshot.QuarantineBytes;
        observation.DiskPreviewBytes = snapshot.DiskPreviewBytes - previewWritten;
        observation.DriveTotalBytes = snapshot.DriveTotalBytes;
        observation.DriveAvailableBytes = snapshot.DriveAvailableBytes;
        observation.FilesystemReserveBytes = snapshot.FilesystemReserveBytes;
        observation.UnknownReason = snapshot.UnknownReason;
    }

    private async Task<ReservationTotals> GetReservationTotalsAsync(CancellationToken cancellationToken)
    {
        long count = await database.SpoolQuotaReservations.LongCountAsync(cancellationToken);
        if (count > MaximumReconciledRows)
            return new ReservationTotals(count, long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue);
        var value = await database.SpoolQuotaReservations.AsNoTracking().GroupBy(_ => 1)
            .Select(group => new
            {
                WrittenSpool = group.Where(item => item.Category == SpoolCategory).Select(item => (decimal?)item.WrittenBytes).Sum() ?? 0,
                WrittenPreviews = group.Where(item => item.Category == PreviewCategory).Select(item => (decimal?)item.WrittenBytes).Sum() ?? 0,
                Remaining = group.Where(item => item.State == ActiveReservation)
                    .Select(item => (decimal?)(item.ReservedBytes - item.WrittenBytes)).Sum() ?? 0,
                FilesystemReserved = group.Select(item => (decimal?)item.FilesystemReservedBytes).Sum() ?? 0,
            }).SingleOrDefaultAsync(cancellationToken);
        return value is null ? new ReservationTotals(count, 0, 0, 0, 0)
            : new ReservationTotals(count, ClampReservationTotal(value.WrittenSpool),
                ClampReservationTotal(value.WrittenPreviews), ClampReservationTotal(value.Remaining),
                ClampReservationTotal(value.FilesystemReserved));
    }

    private static ReservationTotals ToTotals(SpoolQuotaReservationEntity[] reservations)
    {
        long writtenSpool = 0, writtenPreviews = 0, remaining = 0, filesystem = 0;
        foreach (SpoolQuotaReservationEntity item in reservations)
        {
            if (item.State is not (ActiveReservation or CommittedReservation) || item.Category is not (SpoolCategory or PreviewCategory)
                || item.ReservedBytes < 0 || item.WrittenBytes < 0 || item.WrittenBytes > item.ReservedBytes
                || item.FilesystemReservedBytes < 0)
                throw new InvalidOperationException("The persisted quota reservation is invalid.");
            if (item.Category == SpoolCategory) writtenSpool = checked(writtenSpool + item.WrittenBytes);
            else writtenPreviews = checked(writtenPreviews + item.WrittenBytes);
            if (item.State == ActiveReservation) remaining = checked(remaining + item.ReservedBytes - item.WrittenBytes);
            filesystem = checked(filesystem + item.FilesystemReservedBytes);
        }
        return new ReservationTotals(reservations.Length, writtenSpool, writtenPreviews, remaining, filesystem);
    }

    private static SpoolQuotaSnapshot BuildCachedSnapshot(SpoolQuotaSettings settings, SpoolQuotaObservationEntity observation,
        ReservationTotals reservations, DateTimeOffset now)
    {
        if (!observation.IsKnown || now < observation.ObservedAt || now - observation.ObservedAt > MaximumObservationAge
            || reservations.Count > MaximumReconciledRows)
            return UnknownSnapshot(settings, observation.UnknownReason ?? AccountingIncomplete, observation.ObservedAt,
                reservations.Count > MaximumReconciledRows ? long.MaxValue : reservations.RemainingBytes);

        try
        {
            long partial = checked(observation.PartialBytes + reservations.WrittenSpoolBytes);
            long previews = checked(observation.DiskPreviewBytes + reservations.WrittenPreviewBytes);
            long reserved = reservations.RemainingBytes;
            long used = checked(observation.ActiveBytes + observation.TrashBytes + partial + observation.QuarantineBytes + previews);
            long deficit = settings.RecoveryTargetBytes is long target ? Math.Max(0, checked(used + reserved) - target) : 0;
            string? blocked = GetBlockedReason(settings, observation.FilesystemReserveBytes, observation.DriveAvailableBytes,
                used, reserved, reservations.FilesystemReservedBytes);
            return new SpoolQuotaSnapshot(settings, observation.ObservedAt, true, used, reserved,
                observation.ActiveBytes, observation.TrashBytes, partial, observation.QuarantineBytes, previews,
                observation.DriveTotalBytes, observation.DriveAvailableBytes, observation.FilesystemReserveBytes,
                deficit, blocked);
        }
        catch (OverflowException)
        {
            return UnknownSnapshot(settings, AccountingIncomplete, observation.ObservedAt);
        }
    }

    private static string? GetBlockedReason(SpoolQuotaSettings settings, long? reserveBytes, long? availableBytes,
        long used, long reserved, long filesystemReserved)
    {
        if (!HasFilesystemHeadroom(reserveBytes, availableBytes, filesystemReserved, requested: 0)) return FilesystemReserve;
        if (settings.IsActive && settings.QuotaBytes is long quota && (used >= quota || reserved >= quota - used)) return QuotaCapacity;
        return null;
    }

    private static long SumFilesystemReservations(IEnumerable<SpoolQuotaReservationEntity> reservations) =>
        reservations.Aggregate(0L, (sum, item) => checked(sum + item.FilesystemReservedBytes));

    private static long ReservationRemainder(IEnumerable<SpoolQuotaReservationEntity> reservations)
    {
        try
        {
            return reservations.Where(item => item.State == ActiveReservation)
                .Aggregate(0L, (sum, item) => checked(sum + item.ReservedBytes - item.WrittenBytes));
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }

    private static long ClampReservationTotal(decimal value) => value >= long.MaxValue ? long.MaxValue : (long)value;

    private static long Sum(IEnumerable<SpoolQuotaFile> files, SpoolQuotaFileCategory category) =>
        files.Where(file => file.Category == category).Aggregate(0L, (sum, file) => checked(sum + file.SizeBytes));

    private static bool IsValid(SpoolQuotaReservation reservation) => reservation.OperationId != Guid.Empty
        && (reservation.AssetId is null || reservation.AssetId != Guid.Empty)
        && reservation.ReservedBytes > 0 && reservation.WrittenBytes >= 0
        && reservation.WrittenBytes <= reservation.ReservedBytes && reservation.FilesystemReservedBytes >= 0
        && Enum.IsDefined(reservation.Category)
        && AreValidKeys(reservation.SpoolKeys);

    private static bool AreValidKeys(IEnumerable<string> keys) => keys.Count() <= 2
        && keys.All(key => !string.IsNullOrWhiteSpace(key) && key.Length <= 1_024 && !Path.IsPathRooted(key)
            && !key.Contains('\\') && key.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..")));

    private static bool IsSame(SpoolQuotaReservationEntity entity, SpoolQuotaReservation reservation) =>
        entity.AssetId == reservation.AssetId && entity.ReservedBytes == reservation.ReservedBytes
        && entity.WrittenBytes == reservation.WrittenBytes && entity.FilesystemReservedBytes == reservation.FilesystemReservedBytes
        && entity.Category == reservation.Category.ToString() && entity.SpoolKeys.SequenceEqual(reservation.SpoolKeys, KeyComparer);

    private SpoolQuotaSettings ToContract(SpoolQuotaSettingsEntity entity)
    {
        if (workspace is not null && (!entity.IsActive || entity.QuotaBytes is not long quota
            || quota > workspace.Required.AllocatedBytes))
            throw new InvalidOperationException("Saved workspace quota settings exceed or disable the operator allocation.");
        return new(entity.Revision, entity.IsActive, entity.QuotaBytes, entity.HighWatermarkBytes, entity.RecoveryTargetBytes);
    }

    private static SpoolQuotaSnapshot UnknownSnapshot(SpoolQuotaSettings settings, string reason, DateTimeOffset? observedAt = null,
        long reservedBytes = 0) =>
        new(settings, observedAt ?? DateTimeOffset.MinValue, false, 0, reservedBytes, 0, 0, 0, 0, 0, null, null, null, 0,
            UnknownAccounting, reason);

    private static StringComparer KeyComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static void Reject(string reason) => RejectedReservations.Add(1, new KeyValuePair<string, object?>("reason", reason));

    private sealed record AssetQuotaRow(Guid Id, string SpoolKey, long Size, string? RetentionDisposition, string? RemovalState);
    private sealed record SnapshotEvidence(SpoolQuotaFile[] Physical, Dictionary<string, SpoolQuotaFile> Managed,
        List<AssetQuotaRow> Assets, List<string> TransientKeys, Dictionary<string, string> TransientOperations,
        bool HasOnlyReservedOperations);
    private sealed record ReservationTotals(long Count, long WrittenSpoolBytes, long WrittenPreviewBytes,
        long RemainingBytes, long FilesystemReservedBytes);
}
