using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Retention;
using System.Security;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class FileSystemSpoolAccounting : ISpoolAccounting
{
    private const int MaximumFiles = 50_000;
    private const string StorageMarkerName = ".shutterspool-root.marker";
    private readonly string spoolRoot;
    private readonly string previewRoot;
    private readonly long? reserveBytes;
    private readonly double? reservePercent;
    private readonly TimeProvider timeProvider;
    private readonly int maximumFiles;

    public FileSystemSpoolAccounting(IConfiguration configuration, TimeProvider timeProvider)
        : this(configuration["ShutterSpool:Spool:RootPath"] ?? throw new InvalidOperationException("The spool root path is not configured."),
            configuration["ShutterSpool:Preview:CacheRootPath"]
                ?? Path.Combine(configuration["ShutterSpool:Spool:RootPath"] ?? ".data/spool", "previews"),
            long.TryParse(configuration["ShutterSpool:Spool:ReservedBytes"], out long bytes) ? bytes : null,
            double.TryParse(configuration["ShutterSpool:Spool:ReservedPercent"], out double percent) ? percent : null,
            timeProvider)
    {
    }

    internal FileSystemSpoolAccounting(string spoolRoot, string previewRoot, long? reserveBytes,
        double? reservePercent, TimeProvider timeProvider, int maximumFiles = MaximumFiles)
    {
        if (reserveBytes < 0 || reservePercent is { } percent && (!double.IsFinite(percent) || percent < 0 || percent > 100))
            throw new InvalidOperationException("The spool reserve is invalid.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFiles);
        this.spoolRoot = Path.GetFullPath(spoolRoot);
        this.previewRoot = Path.GetFullPath(previewRoot);
        if (new[] { Path.Combine(this.spoolRoot, "spool"), Path.Combine(this.spoolRoot, "incoming"),
                Path.Combine(this.spoolRoot, "quarantine") }
            .Any(directory => IsSameOrChild(directory, this.previewRoot)))
            throw new InvalidOperationException("The preview cache root cannot contain a managed spool directory.");
        this.reserveBytes = reserveBytes;
        this.reservePercent = reservePercent;
        this.timeProvider = timeProvider;
        this.maximumFiles = maximumFiles;
    }

    public Task<SpoolQuotaInventory> ObserveAsync(IReadOnlySet<string> reservedSpoolKeys, string? expectedStorageMarker,
        bool allowInitializeStorageMarker, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reservedSpoolKeys);
        try
        {
            var files = new List<SpoolQuotaFile>();
            int visitedEntries = 0;
            if (!Directory.Exists(spoolRoot)) throw new IOException("The configured spool root is unavailable.");
            string previewPath = NormalizePath(previewRoot);
            foreach ((string directory, SpoolQuotaFileCategory category, string prefix) in new[]
            {
                (Path.Combine(spoolRoot, "spool"), SpoolQuotaFileCategory.Spool, "spool"),
                (Path.Combine(spoolRoot, "incoming"), SpoolQuotaFileCategory.Incoming, "incoming"),
                (Path.Combine(spoolRoot, "quarantine"), SpoolQuotaFileCategory.Quarantine, "quarantine"),
            })
            {
                if (expectedStorageMarker is not null && !Directory.Exists(directory))
                    throw new IOException("A managed spool directory is unavailable.");
                Scan(directory, category, prefix, files, reservedSpoolKeys, previewPath, ref visitedEntries, cancellationToken);
            }

            Scan(previewRoot, SpoolQuotaFileCategory.DiskPreview, "previews", files, null, null, ref visitedEntries, cancellationToken);

            (string? storageMarker, bool needsCommit) = ObserveStorageMarker(expectedStorageMarker, allowInitializeStorageMarker);

            DriveInfo drive = GetMostConstrainedDrive();
            long? filesystemReserve = GetReserve(drive);
            return Task.FromResult(new SpoolQuotaInventory(timeProvider.GetUtcNow(), true, files,
                drive.TotalSize, drive.AvailableFreeSpace, filesystemReserve, StorageMarker: storageMarker,
                StorageMarkerNeedsCommit: needsCommit));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or OverflowException)
        {
            return Task.FromResult(new SpoolQuotaInventory(timeProvider.GetUtcNow(), false, [], null, null, null,
                "Filesystem accounting could not complete."));
        }
    }

    private DriveInfo GetMostConstrainedDrive()
    {
        DriveInfo[] drives = [.. new[] { spoolRoot, previewRoot }.Select(FileSystemCapacity.GetDriveForPath)];
        return drives.OrderBy(drive => checked(drive.AvailableFreeSpace - (GetReserve(drive) ?? 0))).First();
    }

    private long? GetReserve(DriveInfo drive) => reserveBytes ?? (reservePercent is double percent
        ? checked((long)(drive.TotalSize * percent / 100))
        : null);

    public Task<bool> EstablishStorageMarkerAsync(string marker, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(marker, "N", out _)) return Task.FromResult(false);
        try
        {
            foreach (string directory in new[] { "spool", "incoming", "quarantine" })
            {
                string managedDirectory = Path.Combine(spoolRoot, directory);
                Directory.CreateDirectory(managedDirectory);
                EnsureNoReparsePoints(managedDirectory);
            }
            string path = Path.Combine(spoolRoot, StorageMarkerName);
            EnsureNoReparsePoints(path);
            if (File.Exists(path)) return Task.FromResult(string.Equals(ReadStorageMarker(path), marker, StringComparison.Ordinal));
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128, FileOptions.WriteThrough);
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(marker);
            output.Write(bytes);
            output.Flush(flushToDisk: true);
            return Task.FromResult(true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult(false);
        }
    }

    private (string? Marker, bool NeedsCommit) ObserveStorageMarker(string? expectedStorageMarker,
        bool allowInitializeStorageMarker)
    {
        string markerPath = Path.Combine(spoolRoot, StorageMarkerName);
        EnsureNoReparsePoints(markerPath);
        if (File.Exists(markerPath) && File.GetAttributes(markerPath).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("The configured spool storage identity is linked.");
        if (expectedStorageMarker is not null)
        {
            string? actual = ReadStorageMarker(markerPath);
            if (!string.Equals(actual, expectedStorageMarker, StringComparison.Ordinal))
                throw new IOException("The configured spool storage identity changed.");
            return (actual, false);
        }

        if (!allowInitializeStorageMarker)
            throw new IOException("The configured spool storage identity is unavailable.");

        if (File.Exists(markerPath))
        {
            string? existing = ReadStorageMarker(markerPath);
            if (existing is not null) return (existing, true);
            throw new IOException("The configured spool storage identity is invalid.");
        }

        return (Guid.NewGuid().ToString("N"), true);
    }

    private static string? ReadStorageMarker(string path)
    {
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128, FileOptions.SequentialScan);
        if (input.Length != 32) return null;
        Span<byte> bytes = stackalloc byte[32];
        input.ReadExactly(bytes);
        string marker = System.Text.Encoding.ASCII.GetString(bytes);
        return Guid.TryParseExact(marker, "N", out _) ? marker : null;
    }

    private void Scan(string directory, SpoolQuotaFileCategory category, string keyPrefix, List<SpoolQuotaFile> files,
        IReadOnlySet<string>? excludedKeys, string? skipDirectory, ref int visitedEntries, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) return;
        EnsureNoReparsePoints(directory);
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out string? current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visitedEntries > maximumFiles) throw new IOException("Filesystem accounting reached its scan bound.");
                if (skipDirectory is not null && string.Equals(NormalizePath(path), skipDirectory, PathComparison)) continue;
                string relative = Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
                string key = $"{keyPrefix}/{relative}";
                if (excludedKeys is not null && excludedKeys.Contains(key)) continue;
                FileAttributes attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Managed spool storage contains a linked path.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(path);
                    continue;
                }

                var before = new FileInfo(path);
                long size = before.Length;
                DateTime lastWrite = before.LastWriteTimeUtc;
                var after = new FileInfo(path);
                if (!after.Exists || after.Length != size || after.LastWriteTimeUtc != lastWrite)
                    throw new IOException("A managed spool file changed during accounting.");
                files.Add(new SpoolQuotaFile(key, size, category));
            }
        }
    }

    private static bool IsSameOrChild(string candidate, string parent)
    {
        candidate = NormalizePath(candidate);
        parent = NormalizePath(parent);
        return string.Equals(candidate, parent, PathComparison)
            || candidate.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, PathComparison);
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null && directory.Exists; directory = directory.Parent)
            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Managed spool storage traverses a linked directory.");
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
