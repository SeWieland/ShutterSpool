using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Spool;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class FileSystemSpoolOptions
{
    public required string RootPath { get; init; }
    public long? ReservedBytes { get; init; }
    public double? ReservedPercent { get; init; }
    public int BufferSize { get; init; } = 64 * 1024;
    internal Func<string, FileStream>? ReceiveOutputFactory { get; init; }
}

internal sealed class FileSystemSpool(FileSystemSpoolOptions options) : ISpool, IAdmittedSpool, IQuotaAwareSpool, ICommittedOriginalReader
{
    private const string IncomingDirectory = "incoming";
    private const string SpoolDirectory = "spool";
    private const string QuarantineDirectory = "quarantine";
    private readonly string _root = Path.GetFullPath(options.RootPath);
    private readonly Lock _admissionGate = new();
    private long _inFlightReservation;
    private long _cameraReceiveReservation;

    public string GetPartialKey(Guid sourceId, Guid assetId) => $"{IncomingDirectory}/{sourceId:N}/{assetId:N}.partial";

    IReadOnlyList<string> IQuotaAwareSpool.GetReservationKeys(Guid sourceId, Guid assetId) =>
        [GetPartialKey(sourceId, assetId), GetFinalKey(assetId, DateTime.UtcNow)];

    Task<SpoolWrite> IQuotaAwareSpool.WriteReservedAsync(Guid sourceId, Guid assetId, string partialKey, string finalKey,
        Stream content, long maximumUploadBytes, CancellationToken cancellationToken)
    {
        if (!string.Equals(partialKey, GetPartialKey(sourceId, assetId), StringComparison.Ordinal))
            throw new InvalidOperationException("The reserved partial key does not match the ingest identity.");
        _ = Resolve(finalKey);
        return WriteCoreAsync(sourceId, assetId, content, maximumUploadBytes, finalKey, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken)
    {
        if (!spoolKey.StartsWith($"{SpoolDirectory}/", StringComparison.Ordinal)) throw new InvalidOperationException("The key does not name a committed original.");
        string path = Resolve(spoolKey);
        EnsureNoReparsePoints(path);
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task CheckAdmissionAsync(long? maximumUploadBytes, CancellationToken cancellationToken)
    {
        EnsureAdmission(maximumUploadBytes);
        return Task.CompletedTask;
    }

    Task IAdmittedSpool.CheckAdmittedAdmissionAsync(long requiredFreeBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckAdmittedAdmission(requiredFreeBytes);
        return Task.CompletedTask;
    }

    string IAdmittedSpool.GetReceiveKey(Guid assetId) =>
        $"{SpoolDirectory}/{DateTime.UtcNow.ToString("yyyy/MM", CultureInfo.InvariantCulture)}/{assetId:N}";

    public Task<SpoolWrite> WriteAsync(Guid sourceId, Guid assetId, Stream content, long? maximumUploadBytes, CancellationToken cancellationToken) =>
        WriteCoreAsync(sourceId, assetId, content, maximumUploadBytes, null, cancellationToken);

    async Task<SpoolWrite> IAdmittedSpool.ReceiveAdmittedAsync(string receiveKey, string partialKey, Stream content,
        long maximumUploadBytes, CancellationToken cancellationToken)
    {
        if (maximumUploadBytes <= 0) throw new InvalidOperationException("An admitted receive requires a positive size limit.");
        long reservation = ReserveAdmittedReceive(maximumUploadBytes);
        try
        {
            string path = Resolve(receiveKey);
            EnsureNoReparsePoints(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureNoReparsePoints(path);

            (long size, string sha256) receipt;
            await using (var output = options.ReceiveOutputFactory?.Invoke(path)
                ?? new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, options.BufferSize,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                receipt = await WriteAndFingerprintAsync(content, output, maximumUploadBytes, cancellationToken);
            }

            return new SpoolWrite(partialKey, receiveKey, receipt.size, receipt.sha256);
        }
        finally
        {
            ReleaseCameraReceive(reservation);
        }
    }

    async Task<SpoolWrite?> IAdmittedSpool.VerifyReceivedAsync(IngestOperation operation, long expectedSize,
        CancellationToken cancellationToken)
    {
        if (operation.FinalKey is null || expectedSize < 0 || operation.Size is not null && operation.Size != expectedSize) return null;
        string path = Resolve(operation.FinalKey);
        EnsureNoReparsePoints(path);
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize) return null;
        (long size, string sha256) = await FingerprintAsync(path, cancellationToken);
        if (size != expectedSize) return null;
        if (operation.Sha256 is not null && !CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(operation.Sha256), Convert.FromHexString(sha256))) return null;
        return new SpoolWrite(operation.PartialKey, operation.FinalKey, size, sha256);
    }

    private async Task<SpoolWrite> WriteCoreAsync(Guid sourceId, Guid assetId, Stream content, long? maximumUploadBytes,
        string? reservedFinalKey, CancellationToken cancellationToken)
    {
        long reservation = Reserve(maximumUploadBytes);
        try
        {
            string partialKey = GetPartialKey(sourceId, assetId);
            string finalKey = reservedFinalKey ?? GetFinalKey(assetId, DateTime.UtcNow);
            string partialPath = Resolve(partialKey);
            string finalPath = Resolve(finalKey);
            EnsureNoReparsePoints(partialPath);
            EnsureNoReparsePoints(finalPath);
            Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            EnsureNoReparsePoints(partialPath);
            EnsureNoReparsePoints(finalPath);

            (long size, string sha256) receipt;
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, options.BufferSize, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                receipt = await WriteAndFingerprintAsync(content, output, maximumUploadBytes, cancellationToken);
            }

            CopyAndFlush(partialPath, finalPath);
            return new SpoolWrite(partialKey, finalKey, receipt.size, receipt.sha256);
        }
        finally
        {
            Release(reservation);
        }
    }

    private static string GetFinalKey(Guid assetId, DateTime timestamp) =>
        $"{SpoolDirectory}/{timestamp.ToString("yyyy/MM", CultureInfo.InvariantCulture)}/{assetId:N}";

    public async Task<SpoolWrite?> FindFinalAsync(IngestOperation operation, CancellationToken cancellationToken)
    {
        string? finalKey = FindFinalKey(operation);
        if (finalKey is null) return null;
        string path = Resolve(finalKey);
        EnsureNoReparsePoints(path);
        if (!File.Exists(path))
        {
            string partialPath = Resolve(operation.PartialKey);
            EnsureNoReparsePoints(partialPath);
            if (!File.Exists(partialPath)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureNoReparsePoints(path);
            CopyAndFlush(partialPath, path);
        }
        (long size, string sha256) = await FingerprintAsync(path, cancellationToken);
        if (operation.Size is not null && (size != operation.Size || operation.Sha256 is null || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(operation.Sha256), Convert.FromHexString(sha256))))
        {
            if (operation.Sha256 is null) return null;
            string partialPath = Resolve(operation.PartialKey);
            EnsureNoReparsePoints(partialPath);
            if (!await MatchesAsync(partialPath, operation.Size.Value, operation.Sha256, cancellationToken)) return null;
            MoveToQuarantine(path, operation.Id, "Final spool bytes did not match the recovery journal.");
            EnsureNoReparsePoints(path);
            CopyAndFlush(partialPath, path);
            return new SpoolWrite(operation.PartialKey, finalKey, operation.Size.Value, operation.Sha256);
        }
        if (operation.Size is null)
        {
            string partialPath = Resolve(operation.PartialKey);
            EnsureNoReparsePoints(partialPath);
            if (!File.Exists(partialPath) || new FileInfo(partialPath).Length != size) return null;
            await using var partial = File.OpenRead(partialPath);
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(partial, cancellationToken), Convert.FromHexString(sha256))) return null;
        }
        return new SpoolWrite(operation.PartialKey, finalKey, size, sha256);
    }

    public Task QuarantineAsync(IngestOperation operation, string reason, CancellationToken cancellationToken)
    {
        foreach (string key in new[] { operation.PartialKey, operation.FinalKey }.Where(key => key is not null).Cast<string>().Distinct(StringComparer.Ordinal))
        {
            string path = Resolve(key);
            EnsureNoReparsePoints(path);
            if (!File.Exists(path)) continue;
            MoveToQuarantine(path, operation.Id, reason);
        }
        return Task.CompletedTask;
    }

    public async Task QuarantineUntrackedAsync(IReadOnlySet<string> knownKeys, CancellationToken cancellationToken)
    {
        RestoreMissingQuarantineReasons();
        foreach (string directoryName in new[] { SpoolDirectory, IncomingDirectory })
        {
            string directory = Resolve(directoryName);
            EnsureNoReparsePoints(directory);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                string key = Path.GetRelativePath(_root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (knownKeys.Contains(key)) continue;
                var operation = new IngestOperation(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, "unknown", key, IngestOperationState.Quarantined, directoryName == SpoolDirectory ? key : null);
                await QuarantineAsync(operation, "No matching ingest operation exists.", cancellationToken);
            }
        }
    }

    private void EnsureAdmission(long? maximumUploadBytes)
    {
        if (maximumUploadBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumUploadBytes));
        if (options.ReservedBytes < 0 || options.ReservedPercent is { } percent && (!double.IsFinite(percent) || percent < 0 || percent > 100)) throw new InvalidOperationException("The spool reserve is invalid.");
        if (options.ReservedBytes is null && options.ReservedPercent is null) throw new InvalidOperationException("A spool reserve must be configured before accepting uploads.");
        DriveInfo drive = FileSystemCapacity.GetDriveForPath(_root);
        long reserve = options.ReservedBytes ?? (long)(drive.TotalSize * options.ReservedPercent!.Value / 100);
        if (maximumUploadBytes > long.MaxValue / 2) throw new IOException("The spool reserve would be breached.");
        long required = maximumUploadBytes is null ? 0 : maximumUploadBytes.Value * 2;
        if (drive.AvailableFreeSpace <= reserve || drive.AvailableFreeSpace - required <= reserve) throw new IOException("The spool reserve would be breached.");
    }

    private long Reserve(long? maximumUploadBytes)
    {
        lock (_admissionGate)
        {
            EnsureAdmission(maximumUploadBytes);
            long reservation = maximumUploadBytes is null ? 0 : maximumUploadBytes.Value * 2;
            DriveInfo drive = FileSystemCapacity.GetDriveForPath(_root);
            long reserve = options.ReservedBytes ?? (long)(drive.TotalSize * options.ReservedPercent!.Value / 100);
            if (drive.AvailableFreeSpace - _inFlightReservation - reservation <= reserve)
                throw new IOException("The spool reserve would be breached by concurrent uploads.");
            _inFlightReservation += reservation;
            return reservation;
        }
    }

    private async Task<(long Size, string Sha256)> WriteAndFingerprintAsync(Stream content, FileStream output,
        long? maximumUploadBytes, CancellationToken cancellationToken)
    {
        long size = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(options.BufferSize);
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            if (maximumUploadBytes is not null && size > maximumUploadBytes.Value - read)
                throw new IOException("The upload exceeds its declared maximum size.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private void CheckAdmittedAdmission(long requiredFreeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requiredFreeBytes);
        if (options.ReservedBytes < 0 || options.ReservedPercent is { } percent && (!double.IsFinite(percent) || percent < 0 || percent > 100))
            throw new InvalidOperationException("The spool reserve is invalid.");
        if (options.ReservedBytes is null && options.ReservedPercent is null)
            throw new InvalidOperationException("A spool reserve must be configured before accepting uploads.");
        DriveInfo drive = FileSystemCapacity.GetDriveForPath(_root);
        long reserve = options.ReservedBytes ?? (long)(drive.TotalSize * options.ReservedPercent!.Value / 100);
        lock (_admissionGate)
        {
            long otherInFlight = _inFlightReservation - _cameraReceiveReservation;
            if (drive.AvailableFreeSpace <= reserve || drive.AvailableFreeSpace - otherInFlight - requiredFreeBytes <= reserve)
                throw new IOException("The spool reserve would be breached.");
        }
    }

    private long ReserveAdmittedReceive(long maximumUploadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUploadBytes);
        lock (_admissionGate)
        {
            _inFlightReservation = checked(_inFlightReservation + maximumUploadBytes);
            _cameraReceiveReservation = checked(_cameraReceiveReservation + maximumUploadBytes);
            return maximumUploadBytes;
        }
    }

    private void ReleaseCameraReceive(long reservation)
    {
        lock (_admissionGate)
        {
            _inFlightReservation -= reservation;
            _cameraReceiveReservation -= reservation;
        }
    }

    private void Release(long reservation)
    {
        lock (_admissionGate)
        {
            _inFlightReservation -= reservation;
        }
    }

    private string Resolve(string key)
    {
        string candidate = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string rootPrefix = Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, comparison)) throw new InvalidOperationException("Spool path escapes its configured root.");
        return candidate;
    }

    private string? FindFinalKey(IngestOperation operation)
    {
        if (operation.FinalKey is not null) return operation.FinalKey;
        string directory = Resolve(SpoolDirectory);
        EnsureNoReparsePoints(directory);
        if (!Directory.Exists(directory)) return null;
        string[] matches = [.. Directory.EnumerateFiles(directory, operation.AssetId.ToString("N"), new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Take(2)];
        return matches.Length == 1 ? Path.GetRelativePath(_root, matches[0]).Replace(Path.DirectorySeparatorChar, '/') : null;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        FileSystemInfo target = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path);
        if (target.Exists && target.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Spool path is a reparse point.");
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(path)!); directory is not null && directory.Exists; directory = directory.Parent)
        {
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Spool path traverses a reparse point.");
        }
    }

    private async Task<bool> MatchesAsync(string path, long size, string sha256, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) return false;
        await using var input = File.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, cancellationToken), Convert.FromHexString(sha256));
    }

    private async Task<(long Size, string Sha256)> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, options.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(options.BufferSize);
        long size = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            size += read;
        }
        return (size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void CopyAndFlush(string source, string destination)
    {
        File.Copy(source, destination, overwrite: false);
        using var copy = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.Read);
        copy.Flush(flushToDisk: true);
    }

    private void MoveToQuarantine(string path, Guid operationId, string reason)
    {
        string quarantinePath = Resolve($"{QuarantineDirectory}/{operationId:N}-{Guid.NewGuid():N}-{Path.GetFileName(path)}.media");
        EnsureNoReparsePoints(quarantinePath);
        Directory.CreateDirectory(Path.GetDirectoryName(quarantinePath)!);
        EnsureNoReparsePoints(quarantinePath);
        WriteReason($"{quarantinePath}.reason", reason);
        File.Move(path, quarantinePath, overwrite: false);
    }

    private void RestoreMissingQuarantineReasons()
    {
        string directory = Resolve(QuarantineDirectory);
        EnsureNoReparsePoints(directory);
        if (!Directory.Exists(directory)) return;
        foreach (string path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            if (path.EndsWith(".reason", StringComparison.Ordinal) && File.Exists(path[..^".reason".Length])) continue;
            EnsureNoReparsePoints(path);
            string reasonPath = $"{path}.reason";
            EnsureNoReparsePoints(reasonPath);
            if (!File.Exists(reasonPath)) WriteReason(reasonPath, "Quarantine was interrupted; original reason unavailable.");
        }
    }

    private static void WriteReason(string path, string reason)
    {
        EnsureNoReparsePoints(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4_096, FileOptions.WriteThrough);
        output.Write(Encoding.UTF8.GetBytes(reason));
        output.Flush(flushToDisk: true);
    }
}
