using MetadataExtractor;
using MetadataExtractor.Formats.Riff;
using MetadataExtractor.Formats.WebP;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace ShutterSpool.Infrastructure.Preview;

internal sealed class FileSystemPreviewCache : IPreviewCache, IPreviewAssetCacheInvalidator, IPreviewCacheReclaimer
{
    private const int MaximumOutputBytes = PreviewCacheReclamation.MaximumOutputBytes;
    private const string OperationLockFileName = ".shutterspool-preview-cache.lock";
    private readonly string _cacheRoot;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public FileSystemPreviewCache(IConfiguration configuration, IServiceScopeFactory? scopeFactory = null)
    {
        _cacheRoot = Path.GetFullPath(configuration["ShutterSpool:Preview:CacheRootPath"]
            ?? Path.Combine(configuration["ShutterSpool:Spool:RootPath"] ?? ".data/spool", "previews"));
        _scopeFactory = scopeFactory;
    }

    public async Task<byte[]?> ReadAsync(string key, PreviewSize size, CancellationToken cancellationToken)
    {
        string path = CachePath(key);
        if (!System.IO.Directory.Exists(_cacheRoot)) return null;
        await using CacheOperationGate operation = await AcquireOperationGateAsync(createRoot: false,
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSafePath(path, allowMissingLeaf: true);
        if (!File.Exists(path)) return null;
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length is <= 0 or > MaximumOutputBytes) return null;
            byte[] bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, cancellationToken);
            if (file.ReadByte() != -1) return null;
            return PreviewMediaValidation.IsWebP(bytes, size) ? bytes : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (EndOfStreamException) { return null; }
    }

    public async Task<bool> ContainsAsync(string key, PreviewSize size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await ReadAsync(key, size, cancellationToken) is not null;
    }

    public async Task<bool> WriteAsync(string key, byte[] content, CancellationToken cancellationToken)
    {
        if (content.Length is <= 0 or > MaximumOutputBytes) throw new InvalidDataException("Invalid preview output size.");
        string path = CachePath(key);
        await using CacheOperationGate operation = await AcquireOperationGateAsync(createRoot: true,
            TimeSpan.FromSeconds(30), cancellationToken);
        EnsureSafePath(path, allowMissingLeaf: true);
        QuotaReservation reservation = await ReserveQuotaAsync(key, cancellationToken);
        if (reservation.Rejected) return false;

        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureSafePath(path, allowMissingLeaf: true);
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            File.Move(temporary, path, overwrite: true);
            if (reservation.Reserved && !await reservation.Quota!.MarkReservationRetainedAsync(
                    reservation.Id, content.Length, cancellationToken))
                throw new IOException("The preview quota reservation could not be retained.");
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            reservation.Scope?.Dispose();
            if (reservation.Reserved && File.Exists(path) is false)
            {
                using IServiceScope releaseScope = _scopeFactory!.CreateScope();
                await releaseScope.ServiceProvider.GetRequiredService<ISpoolQuotaStore>()
                    .ReleaseReservationAsync(reservation.Id, CancellationToken.None);
            }
        }
    }

    private async Task<QuotaReservation> ReserveQuotaAsync(string key, CancellationToken cancellationToken)
    {
        Guid reservationId = ReservationId(key);
        if (_scopeFactory is null) return new(reservationId, null, null, false, false);

        IServiceScope scope = _scopeFactory.CreateScope();
        try
        {
            ISpoolQuotaStore quota = scope.ServiceProvider.GetRequiredService<ISpoolQuotaStore>();
            if (!(await quota.GetSettingsAsync(cancellationToken)).IsActive)
                return new(reservationId, scope, quota, false, false);

            string quotaKey = $"previews/{key}";
            bool reserved = await quota.TryReserveAsync(new SpoolQuotaReservation(reservationId,
                AssetIdFromKey(key), MaximumOutputBytes, 0, [quotaKey], MaximumOutputBytes,
                SpoolQuotaReservationCategory.DiskPreview), cancellationToken);
            if (reserved) return new(reservationId, scope, quota, true, false);

            scope.Dispose();
            return new(reservationId, null, quota, false, true);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    public async Task RemoveAssetAsync(Guid assetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!System.IO.Directory.Exists(_cacheRoot)) return;
        await using CacheOperationGate operation = await AcquireOperationGateAsync(createRoot: false,
            TimeSpan.FromSeconds(30), cancellationToken);
        FileAttributes rootAttributes;
        try { rootAttributes = File.GetAttributes(_cacheRoot); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Preview cache root is a linked directory.");

        foreach (string captureDirectory in System.IO.Directory.EnumerateDirectories(_cacheRoot))
        {
            if (File.GetAttributes(captureDirectory).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Preview cache contains a linked capture directory.");
            if (!Guid.TryParseExact(Path.GetFileName(captureDirectory), "N", out _)) continue;
            string assetDirectory = Path.Combine(captureDirectory, assetId.ToString("N"));
            try { _ = File.GetAttributes(assetDirectory); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            EnsureNoReparsePoints(assetDirectory);
            foreach (string entry in System.IO.Directory.EnumerateFiles(assetDirectory))
            {
                string filename = Path.GetFileName(entry);
                bool derivative = PreviewCacheReclamation.IsDerivativeFilename(filename);
                if (!derivative && !IsTemporaryDerivativeFilename(filename)) continue;
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Preview cache contains a linked derivative.");
                File.Delete(entry);
                if (derivative)
                {
                    string key = $"{Path.GetFileName(captureDirectory)}/{assetId:N}/{filename}";
                    await ReleaseReservationAsync(key);
                }
            }
            if (!System.IO.Directory.EnumerateFileSystemEntries(assetDirectory).Any())
                System.IO.Directory.Delete(assetDirectory);
        }
    }

    public async Task<PreviewCacheReclaimResult> ReclaimAsync(long maximumBytes, int maximumEntries,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        if (!System.IO.Directory.Exists(_cacheRoot)) return new(0, 0, false);

        var timer = Stopwatch.StartNew();
        PreviewCacheReclaimResult result = new(0, 0, false);
        try
        {
            await using CacheOperationGate operation = await AcquireOperationGateAsync(createRoot: false,
                TimeSpan.FromMilliseconds(100), cancellationToken);
            string root = ValidateCacheRoot();
            result = PreviewCacheReclamation.Reclaim(root, maximumBytes, maximumEntries, timer, cancellationToken);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return new(result.RemovedBytes, result.RemovedEntries, true, "cache_reclaim_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            return new(result.RemovedBytes, result.RemovedEntries, true, "cache_reclaim_unavailable");
        }
        catch (SecurityException)
        {
            return new(result.RemovedBytes, result.RemovedEntries, true, "cache_reclaim_unavailable");
        }
    }

    private async Task<CacheOperationGate> AcquireOperationGateAsync(bool createRoot, TimeSpan lockWait,
        CancellationToken cancellationToken)
    {
        if (!await _operationGate.WaitAsync(lockWait, cancellationToken))
            throw new IOException("The preview cache operation gate is busy.");
        try
        {
            if (createRoot)
            {
                ValidateCacheAncestors();
                System.IO.Directory.CreateDirectory(_cacheRoot);
            }
            string root = ValidateCacheRoot();
            string lockPath = Path.Combine(root, OperationLockFileName);
            if (File.Exists(lockPath) && File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("The preview cache operation lock is linked.");

            Stopwatch timer = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                        1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (File.GetAttributes(lockPath).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("The preview cache operation lock is linked.");
                    return new CacheOperationGate(stream, _operationGate);
                }
                catch (IOException) when (timer.Elapsed < lockWait)
                {
                    if (stream is not null) await stream.DisposeAsync();
                    await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
                }
                catch
                {
                    if (stream is not null) await stream.DisposeAsync();
                    throw;
                }
            }
        }
        catch
        {
            _operationGate.Release();
            throw;
        }
    }

    private void ValidateCacheAncestors()
    {
        DirectoryInfo? parent = new(Path.GetDirectoryName(_cacheRoot)!);
        for (; parent is not null; parent = parent.Parent)
        {
            if (!parent.Exists) continue;
            if (parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Preview cache path traverses a linked directory.");
        }
    }

    private string ValidateCacheRoot()
    {
        var root = new DirectoryInfo(_cacheRoot);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Preview cache root is unavailable or linked.");
        for (DirectoryInfo? parent = root; parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Preview cache path traverses a linked directory.");
        return Path.GetFullPath(root.FullName);
    }

    private sealed class CacheOperationGate(FileStream stream, SemaphoreSlim localGate) : IAsyncDisposable
    {
        private FileStream? _stream = stream;
        private readonly SemaphoreSlim _localGate = localGate;

        public async ValueTask DisposeAsync()
        {
            FileStream? owned = Interlocked.Exchange(ref _stream, null);
            if (owned is null) return;
            try { await owned.DisposeAsync(); }
            finally { _localGate.Release(); }
        }
    }

    private sealed record QuotaReservation(Guid Id, IServiceScope? Scope, ISpoolQuotaStore? Quota,
        bool Reserved, bool Rejected);

    private static void EnsureNoReparsePoints(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Preview cache contains a linked asset directory.");
            foreach (string entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("Preview cache contains a linked derivative.");
                if (System.IO.Directory.Exists(entry)) pending.Push(entry);
            }
        }
    }

    private string CachePath(string key)
    {
        string[] segments = key.Split('/');
        if (segments.Length != 3 || !Guid.TryParseExact(segments[0], "N", out _)
            || !Guid.TryParseExact(segments[1], "N", out _)
            || !PreviewCacheReclamation.IsDerivativeFilename(segments[2]))
            throw new ArgumentException("The preview key is invalid.", nameof(key));
        string path = Path.GetFullPath(Path.Combine(_cacheRoot, segments[0], segments[1], segments[2]));
        string root = Path.TrimEndingDirectorySeparator(_cacheRoot);
        string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("The preview key escapes the cache root.", nameof(key));
        return path;
    }

    private static bool IsTemporaryDerivativeFilename(string filename)
    {
        if (!filename.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        string[] segments = filename[..^4].Split('.');
        return segments.Length == 2 && PreviewCacheReclamation.IsDerivativeFilename(segments[0])
            && Guid.TryParseExact(segments[1], "N", out _);
    }

    private static Guid? AssetIdFromKey(string key) => Guid.TryParseExact(key.Split('/')[1], "N", out Guid assetId) ? assetId : null;

    private static Guid ReservationId(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"shutterspool-preview-quota:{key}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task ReleaseReservationAsync(string key)
    {
        if (_scopeFactory is null) return;
        using IServiceScope scope = _scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISpoolQuotaStore>()
            .ReleaseReservationAsync(ReservationId(key), CancellationToken.None);
    }

    private void EnsureSafePath(string path, bool allowMissingLeaf)
    {
        for (DirectoryInfo? directory = new(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Preview cache traverses a linked directory.");
            if (PathEquals(directory.FullName, _cacheRoot)) break;
        }
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Preview cache entry is a linked file.");
        if (!allowMissingLeaf && !File.Exists(path)) throw new FileNotFoundException("Preview derivative is unavailable.", path);
    }

    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

internal static class PreviewMediaValidation
{
    private const int MaximumOutputBytes = 4 * 1024 * 1024;

    public static bool IsWebP(byte[] bytes, PreviewSize size)
    {
        if (bytes.Length < 20 || bytes.Length > MaximumOutputBytes
            || !bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != bytes.Length - 8
            || !bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return false;
        try
        {
            using var input = new MemoryStream(bytes, writable: false);
            WebPDirectory? image = WebPMetadataReader.ReadMetadata(input).OfType<WebPDirectory>().FirstOrDefault();
            if (image is null || image.HasError) return false;
            int width = image.GetInt32(WebPDirectory.TagImageWidth);
            int height = image.GetInt32(WebPDirectory.TagImageHeight);
            return width is > 0 and <= 4096 && height is > 0 and <= 4096 && width <= size.Width && height <= size.Height;
        }
        catch (Exception error) when (error is IOException or RiffProcessingException or ImageProcessingException or ArgumentException)
        {
            return false;
        }
    }
}
