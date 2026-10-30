using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Retention;
using System.Diagnostics.Metrics;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfSpoolQuotaStore(
    ShutterSpoolDbContext database,
    ISpoolAccounting accounting,
    TimeProvider timeProvider, WorkspaceContext? workspace = null) : ISpoolQuotaStore, IQuotaReservationCommitter
{
    private const int MaximumReconciledRows = 100_000;
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
            await PersistUnknownAsync("Filesystem accounting could not complete.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return UnknownSnapshot(settings, "Filesystem accounting could not complete.", timeProvider.GetUtcNow(), long.MaxValue);
        }

        HashSet<string> reservedKeys = reservations.Where(item => item.State == "Active")
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
            ToTotals(reservations.Where(item => item.State == "Active").ToArray()), timeProvider.GetUtcNow());
    }

    private static void ReconcilePreviewReservations(IEnumerable<SpoolQuotaReservationEntity> reservations,
        SpoolQuotaInventory inventory)
    {
        foreach (SpoolQuotaReservationEntity reservation in reservations.Where(item =>
                     item.State == "Active" && item.Category == "DiskPreview"))
        {
            SpoolQuotaFile? retained = inventory.Files.SingleOrDefault(file =>
                reservation.SpoolKeys.Contains(file.Key, KeyComparer));
            if (retained is null) continue;
            reservation.State = "Committed";
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
        foreach (SpoolQuotaReservationEntity reservation in reservations.Where(item => item.State == "Committed"))
            database.SpoolQuotaReservations.Remove(reservation);
    }

    public async Task<SpoolQuotaSettings?> StageSettingsUpdateAsync(int expectedRevision, SpoolQuotaSettings settings,
        bool authorizeStorageMarkerAdoption, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (workspace?.Enabled == true && (!settings.IsActive || settings.QuotaBytes is not long quota
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
            Reject("invalid_reservation");
            return false;
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? existing = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == reservation.OperationId, cancellationToken);
        if (existing is not null)
            return reservation.Category != SpoolQuotaReservationCategory.DiskPreview
                && existing.State == "Active" && IsSame(existing, reservation);

        ReservationTotals totals = await GetReservationTotalsAsync(cancellationToken);
        if (totals.Count > MaximumReconciledRows)
        {
            Reject("unknown_accounting");
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
        // While quota enforcement is inactive, keep a durable bound for every
        // admission that has one. This closes the race with an operator
        // activating quota while the upload is in flight. Existing installs
        // may still have no trusted storage marker, so do not make that
        // unknown inventory block previously accepted bounded uploads.
        bool canUseUnmeasuredInactiveReservation = !settings.IsActive && !snapshot.IsKnown;
        if (!snapshot.IsKnown && !canUseUnmeasuredInactiveReservation)
            return "unknown_accounting";

        if (!canUseUnmeasuredInactiveReservation)
        {
            if (snapshot.FilesystemReserveBytes is not long || snapshot.DriveAvailableBytes is not long)
                return "filesystem_reserve";
            long filesystemReserve = snapshot.FilesystemReserveBytes.Value;
            long available = snapshot.DriveAvailableBytes.Value;
            if (available <= filesystemReserve)
                return "filesystem_reserve";

            long existingFilesystemReserved = totals.FilesystemReservedBytes;
            long filesystemHeadroom = available - filesystemReserve;
            if (existingFilesystemReserved >= filesystemHeadroom
                || reservation.FilesystemReservedBytes >= filesystemHeadroom - existingFilesystemReserved)
                return "filesystem_reserve";
        }
        if (settings.IsActive && (settings.QuotaBytes is not long quota
            || snapshot.UsedBytes > quota || snapshot.ReservedBytes > quota - snapshot.UsedBytes
            || reservation.ReservedBytes > quota - snapshot.UsedBytes - snapshot.ReservedBytes))
            return snapshot.AdmissionBlockedReason == "unknown_accounting" ? "unknown_accounting" : "quota_capacity";
        return null;
    }

    public async Task<bool> UpdateReservationAsync(Guid operationId, Guid? assetId, IReadOnlyList<string> spoolKeys,
        long reservedBytes, long writtenBytes, long filesystemReservedBytes, CancellationToken cancellationToken)
    {
        if (reservedBytes <= 0 || writtenBytes < 0 || writtenBytes > reservedBytes || filesystemReservedBytes < 0
            || !AreValidKeys(spoolKeys))
        {
            Reject("invalid_reservation");
            return false;
        }
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is null) return false;
        if (reservation.State != "Active") return false;
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
            Reject("invalid_reservation");
            return false;
        }
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is null || reservation.State != "Active" || reservation.Category != "DiskPreview"
            || actualBytes > reservation.ReservedBytes) return false;
        reservation.ReservedBytes = actualBytes;
        reservation.WrittenBytes = actualBytes;
        reservation.FilesystemReservedBytes = actualBytes;
        reservation.State = "Committed";
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
        HashSet<string> otherKeys = reservations.Where(item => item.OperationId != operationId && item.State == "Active")
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
        foreach (SpoolQuotaReservationEntity committed in others.Where(item => item.State == "Committed"))
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
        if (reservation is null || reservation.State != "Active" || reservation.Category != "Spool"
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
        foreach (SpoolQuotaReservationEntity reservation in reservations)
        {
            string? operationState = await database.Operations.AsNoTracking().Where(item => item.Id == reservation.OperationId)
                .Select(item => item.State).SingleOrDefaultAsync(cancellationToken);
            string? admissionState = await database.CameraUploadAdmissions.AsNoTracking()
                .Where(item => item.Id == reservation.OperationId).Select(item => item.State.ToString()).SingleOrDefaultAsync(cancellationToken);
            if (admissionState is "Quarantined" or "Failed" || operationState is "Quarantined"
                || operationState is null && admissionState is null)
                await ReleaseReservationAsync(reservation.OperationId, cancellationToken);
        }
    }

    public async Task CompleteReservationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A spool reservation must be completed inside the ingest Asset transaction.");
        await LockSettingsAsync(cancellationToken);
        SpoolQuotaReservationEntity? reservation = await database.SpoolQuotaReservations
            .SingleOrDefaultAsync(item => item.OperationId == operationId, cancellationToken);
        if (reservation is not null) reservation.State = "Committed";
    }

    private async Task<SpoolQuotaSnapshot> BuildSnapshotAsync(SpoolQuotaSettings settings,
        IReadOnlyCollection<SpoolQuotaReservationEntity> reservations, SpoolQuotaInventory inventory,
        CancellationToken cancellationToken)
    {
        if (!inventory.IsKnown || reservations.Count > MaximumReconciledRows)
            return UnknownSnapshot(settings, inventory.UnknownReason ?? "Filesystem accounting could not complete.", inventory.ObservedAt,
                ReservationRemainder(reservations));

        try
        {
            SnapshotEvidence? evidence = await LoadSnapshotEvidenceAsync(inventory, cancellationToken);
            if (evidence is null)
                return UnknownSnapshot(settings, "Filesystem accounting could not complete.", inventory.ObservedAt,
                    ReservationRemainder(reservations));

            return CalculateSnapshot(settings, reservations, inventory, evidence);
        }
        catch (Exception error) when (error is OverflowException or InvalidOperationException or ArgumentException)
        {
            return UnknownSnapshot(settings, "Filesystem accounting could not reconcile managed files.", inventory.ObservedAt);
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
            operation.State != "Committed" && operation.State != "Quarantined"
            && !database.SpoolQuotaReservations.Any(reservation => reservation.OperationId == operation.Id), cancellationToken);
        bool hasUnreservedCameraAdmission = await database.CameraUploadAdmissions.AsNoTracking().AnyAsync(admission =>
            admission.State != ShutterSpool.Application.Ingress.CameraTransferState.Completed
            && admission.State != ShutterSpool.Application.Ingress.CameraTransferState.Failed
            && admission.State != ShutterSpool.Application.Ingress.CameraTransferState.Quarantined
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
        long active = 0;
        long trash = 0;
        bool known = evidence.HasOnlyReservedOperations;
        foreach (AssetQuotaRow asset in evidence.Assets)
        {
            if (asset.RemovalState == "Removed") continue;
            if (!evidence.Managed.TryGetValue(asset.SpoolKey, out SpoolQuotaFile? file) || file.SizeBytes != asset.Size)
            {
                known = false;
                continue;
            }
            string disposition = asset.RetentionDisposition ?? "Active";
            if (disposition is "Trash" or "Purging") trash = checked(trash + file.SizeBytes);
            else if (disposition == "Active") active = checked(active + file.SizeBytes);
            else known = false;
        }

        long partial = 0;
        foreach (string key in evidence.TransientKeys)
        {
            SpoolQuotaFile file = evidence.Managed[key];
            if (!evidence.TransientOperations.TryGetValue(key, out string? state))
            {
                known = false;
                continue;
            }
            if (state is "Quarantined" or "Failed") known = false;
            partial = checked(partial + file.SizeBytes);
        }

        long quarantine = Sum(evidence.Physical, SpoolQuotaFileCategory.Quarantine);
        long previews = Sum(evidence.Physical, SpoolQuotaFileCategory.DiskPreview);
        long reservedPartial = 0;
        long reservedPreviews = 0;
        long reservedRemaining = 0;
        foreach (SpoolQuotaReservationEntity reservation in reservations)
        {
            if (reservation.ReservedBytes <= 0 || reservation.WrittenBytes < 0 || reservation.WrittenBytes > reservation.ReservedBytes
                || reservation.FilesystemReservedBytes < 0 || reservation.State is not ("Active" or "Committed")
                || reservation.Category is not ("Spool" or "DiskPreview") || !AreValidKeys(reservation.SpoolKeys))
                return UnknownSnapshot(settings, "Filesystem accounting could not complete.", inventory.ObservedAt,
                    ReservationRemainder(reservations));
            if (reservation.State == "Active")
            {
                if (reservation.Category == "DiskPreview") reservedPreviews = checked(reservedPreviews + reservation.WrittenBytes);
                else reservedPartial = checked(reservedPartial + reservation.WrittenBytes);
                reservedRemaining = checked(reservedRemaining + reservation.ReservedBytes - reservation.WrittenBytes);
            }
        }
        partial = checked(partial + reservedPartial);
        previews = checked(previews + reservedPreviews);
        long used = checked(checked(checked(active + trash) + partial) + checked(quarantine + previews));
        long total = checked(used + reservedRemaining);
        long deficit = settings.RecoveryTargetBytes is long target ? Math.Max(0, total - target) : 0;
        string? blocked = GetBlockedReason(settings, inventory, used, reservedRemaining, SumFilesystemReservations(reservations));
        return new SpoolQuotaSnapshot(settings, inventory.ObservedAt, known, used, reservedRemaining,
            active, trash, partial, quarantine, previews, inventory.DriveTotalBytes, inventory.DriveAvailableBytes,
            inventory.FilesystemReserveBytes, deficit, blocked,
            known ? null : "Filesystem accounting could not reconcile managed files.");
    }

    private async Task<Dictionary<string, string>> FindOperationKeysAsync(IReadOnlyList<string> keys,
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
        long activeWritten = reservations.Where(item => item.State == "Active" && item.Category == "Spool")
            .Aggregate(0L, (sum, item) => checked(sum + item.WrittenBytes));
        long previewWritten = reservations.Where(item => item.State == "Active" && item.Category == "DiskPreview")
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
                WrittenSpool = group.Where(item => item.Category == "Spool").Select(item => (decimal?)item.WrittenBytes).Sum() ?? 0,
                WrittenPreviews = group.Where(item => item.Category == "DiskPreview").Select(item => (decimal?)item.WrittenBytes).Sum() ?? 0,
                Remaining = group.Where(item => item.State == "Active")
                    .Select(item => (decimal?)(item.ReservedBytes - item.WrittenBytes)).Sum() ?? 0,
                FilesystemReserved = group.Select(item => (decimal?)item.FilesystemReservedBytes).Sum() ?? 0,
            }).SingleOrDefaultAsync(cancellationToken);
        return value is null ? new ReservationTotals(count, 0, 0, 0, 0)
            : new ReservationTotals(count, ClampReservationTotal(value.WrittenSpool),
                ClampReservationTotal(value.WrittenPreviews), ClampReservationTotal(value.Remaining),
                ClampReservationTotal(value.FilesystemReserved));
    }

    private static ReservationTotals ToTotals(IReadOnlyCollection<SpoolQuotaReservationEntity> reservations)
    {
        long writtenSpool = 0, writtenPreviews = 0, remaining = 0, filesystem = 0;
        foreach (SpoolQuotaReservationEntity item in reservations)
        {
            if (item.State is not ("Active" or "Committed") || item.Category is not ("Spool" or "DiskPreview")
                || item.ReservedBytes < 0 || item.WrittenBytes < 0 || item.WrittenBytes > item.ReservedBytes
                || item.FilesystemReservedBytes < 0)
                throw new InvalidOperationException("The persisted quota reservation is invalid.");
            if (item.Category == "Spool") writtenSpool = checked(writtenSpool + item.WrittenBytes);
            else writtenPreviews = checked(writtenPreviews + item.WrittenBytes);
            if (item.State == "Active") remaining = checked(remaining + item.ReservedBytes - item.WrittenBytes);
            filesystem = checked(filesystem + item.FilesystemReservedBytes);
        }
        return new ReservationTotals(reservations.Count, writtenSpool, writtenPreviews, remaining, filesystem);
    }

    private static SpoolQuotaSnapshot BuildCachedSnapshot(SpoolQuotaSettings settings, SpoolQuotaObservationEntity observation,
        ReservationTotals reservations, DateTimeOffset now)
    {
        if (!observation.IsKnown || now < observation.ObservedAt || now - observation.ObservedAt > MaximumObservationAge
            || reservations.Count > MaximumReconciledRows)
            return UnknownSnapshot(settings, observation.UnknownReason ?? "Filesystem accounting could not complete.", observation.ObservedAt,
                reservations.Count > MaximumReconciledRows ? long.MaxValue : reservations.RemainingBytes);

        try
        {
            long partial = checked(observation.PartialBytes + reservations.WrittenSpoolBytes);
            long previews = checked(observation.DiskPreviewBytes + reservations.WrittenPreviewBytes);
            long reserved = reservations.RemainingBytes;
            long filesystemReserved = reservations.FilesystemReservedBytes;
            long used = checked(observation.ActiveBytes + observation.TrashBytes + partial + observation.QuarantineBytes + previews);
            long deficit = settings.RecoveryTargetBytes is long target ? Math.Max(0, checked(used + reserved) - target) : 0;
            string? blocked = observation.FilesystemReserveBytes is not long reserve || observation.DriveAvailableBytes is not long available
                || available <= reserve || filesystemReserved >= available - reserve ? "filesystem_reserve"
                : settings.IsActive && settings.QuotaBytes is long quota && (used >= quota || reserved >= quota - used)
                    ? "quota_capacity" : null;
            return new SpoolQuotaSnapshot(settings, observation.ObservedAt, true, used, reserved,
                observation.ActiveBytes, observation.TrashBytes, partial, observation.QuarantineBytes, previews,
                observation.DriveTotalBytes, observation.DriveAvailableBytes, observation.FilesystemReserveBytes,
                deficit, blocked);
        }
        catch (OverflowException)
        {
            return UnknownSnapshot(settings, "Filesystem accounting could not complete.", observation.ObservedAt);
        }
    }

    private static string? GetBlockedReason(SpoolQuotaSettings settings, SpoolQuotaInventory inventory,
        long used, long reserved, long filesystemReserved)
    {
        if (!inventory.IsKnown) return "unknown_accounting";
        if (inventory.FilesystemReserveBytes is not long reserve || inventory.DriveAvailableBytes is not long available
            || available <= reserve || filesystemReserved >= available - reserve) return "filesystem_reserve";
        if (settings.IsActive && settings.QuotaBytes is long quota && (used >= quota || reserved >= quota - used)) return "quota_capacity";
        return null;
    }

    private static long SumFilesystemReservations(IEnumerable<SpoolQuotaReservationEntity> reservations) =>
        reservations.Aggregate(0L, (sum, item) => checked(sum + item.FilesystemReservedBytes));

    private static long ReservationRemainder(IEnumerable<SpoolQuotaReservationEntity> reservations)
    {
        try
        {
            return reservations.Where(item => item.State == "Active")
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
        if (workspace?.Enabled == true && (!entity.IsActive || entity.QuotaBytes is not long quota
            || quota > workspace.Required.AllocatedBytes))
            throw new InvalidOperationException("Saved workspace quota settings exceed or disable the operator allocation.");
        return new(entity.Revision, entity.IsActive, entity.QuotaBytes, entity.HighWatermarkBytes, entity.RecoveryTargetBytes);
    }

    private static SpoolQuotaSnapshot UnknownSnapshot(SpoolQuotaSettings settings, string reason, DateTimeOffset? observedAt = null,
        long reservedBytes = 0) =>
        new(settings, observedAt ?? DateTimeOffset.MinValue, false, 0, reservedBytes, 0, 0, 0, 0, 0, null, null, null, 0,
            "unknown_accounting", reason);

    private static StringComparer KeyComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static void Reject(string reason) => RejectedReservations.Add(1, new KeyValuePair<string, object?>("reason", reason));

    private sealed record AssetQuotaRow(Guid Id, string SpoolKey, long Size, string? RetentionDisposition, string? RemovalState);
    private sealed record SnapshotEvidence(SpoolQuotaFile[] Physical, Dictionary<string, SpoolQuotaFile> Managed,
        List<AssetQuotaRow> Assets, List<string> TransientKeys, Dictionary<string, string> TransientOperations,
        bool HasOnlyReservedOperations);
    private sealed record ReservationTotals(long Count, long WrittenSpoolBytes, long WrittenPreviewBytes,
        long RemainingBytes, long FilesystemReservedBytes);
}
