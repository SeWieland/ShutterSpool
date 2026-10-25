using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Spool;
using System.Globalization;
using System.Security;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class FileSystemExactOriginalRemoval : IExactOriginalRemoval
{
    private const string MarkerFileName = ".shutterspool-root.marker";
    private const int MaximumTransientKeys = 64;
    private readonly string _root;

    public FileSystemExactOriginalRemoval(IConfiguration configuration)
        : this(configuration["ShutterSpool:Spool:RootPath"]
            ?? throw new InvalidOperationException("The spool root path is not configured."))
    {
    }

    internal FileSystemExactOriginalRemoval(string rootPath) => _root = Path.GetFullPath(rootPath);

    public async Task<ExactOriginalRemovalResult> RemoveAsync(ExactOriginalRemovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        long removedBytes = 0;
        bool removedAny = false;

        try
        {
            ValidateRequest(request);
            ValidateStorageMarker(request.StorageMarker);

            string originalPath = ResolveAuthorizedKey(request.SpoolKey, request.AssetId, allowIncomingPartial: false);
            var paths = new List<AuthorizedPath> { new(originalPath, request.ExpectedSize) };
            foreach (string key in request.KnownTransientKeys.Distinct(StringComparer.Ordinal))
            {
                if (string.Equals(key, request.SpoolKey, StringComparison.Ordinal)) continue;
                paths.Add(new(ResolveAuthorizedKey(key, request.AssetId, allowIncomingPartial: true), null));
            }

            // Validate every exact object before deleting any of them. A mismatch leaves the whole set untouched.
            foreach (AuthorizedPath path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateStorageMarker(request.StorageMarker);
                long? actualSize = InspectFile(path.FullPath);
                if (path.ExpectedSize is long expected && actualSize is long found && found != expected)
                    throw new StorageBlockedException("original_size_mismatch");
            }

            foreach (AuthorizedPath path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateStorageMarker(request.StorageMarker);
                long? size = InspectFile(path.FullPath);
                if (size is null) continue;
                if (path.ExpectedSize is long expected && size != expected)
                    throw new StorageBlockedException("original_size_mismatch");
                File.Delete(path.FullPath);
                removedBytes = checked(removedBytes + size.Value);
                removedAny = true;
            }

            return new(removedAny ? ExactOriginalRemovalStatus.Removed : ExactOriginalRemovalStatus.AlreadyAbsent,
                removedBytes);
        }
        catch (StorageBlockedException error)
        {
            return new(ExactOriginalRemovalStatus.Blocked, removedBytes, error.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException
            or ArgumentException or OverflowException)
        {
            return new(ExactOriginalRemovalStatus.Failed, removedBytes, "filesystem_removal_failed");
        }
    }

    private static void ValidateRequest(ExactOriginalRemovalRequest request)
    {
        if (request.AssetId == Guid.Empty || request.ExpectedSize <= 0
            || !Guid.TryParseExact(request.StorageMarker, "N", out _)
            || request.KnownTransientKeys is null || request.KnownTransientKeys.Count > MaximumTransientKeys)
            throw new StorageBlockedException("invalid_removal_authorization");
    }

    private string ResolveAuthorizedKey(string key, Guid assetId, bool allowIncomingPartial)
    {
        if (string.IsNullOrWhiteSpace(key) || Path.IsPathRooted(key) || key.Contains('\\'))
            throw new StorageBlockedException("invalid_storage_key");

        string[] segments = key.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
            throw new StorageBlockedException("invalid_storage_key");

        bool validSpool = segments.Length == 4 && segments[0] == "spool"
            && IsYear(segments[1]) && IsMonth(segments[2])
            && Guid.TryParseExact(segments[3], "N", out Guid spoolAsset) && spoolAsset == assetId;
        bool validLegacySpool = segments.Length == 3 && segments[0] == "spool"
            && IsLegacyYearMonth(segments[1])
            && Guid.TryParseExact(segments[2], "N", out Guid legacySpoolAsset) && legacySpoolAsset == assetId;
        bool validIncomingPartial = allowIncomingPartial && segments.Length == 3 && segments[0] == "incoming"
            && Guid.TryParseExact(segments[1], "N", out _)
            && segments[2] == $"{assetId:N}.partial";
        if (!validSpool && !validLegacySpool && !validIncomingPartial)
            throw new StorageBlockedException("storage_key_asset_mismatch");

        string fullPath = Path.GetFullPath(Path.Combine(_root, Path.Combine(segments)));
        if (!IsChildPath(fullPath, _root)) throw new StorageBlockedException("storage_key_outside_root");
        return fullPath;
    }

    private long? InspectFile(string path)
    {
        ValidateRootAndParents(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new StorageBlockedException("authorized_key_is_linked");
        if (attributes.HasFlag(FileAttributes.Directory)) throw new StorageBlockedException("authorized_key_is_directory");
        return new FileInfo(path).Length;
    }

    private void ValidateStorageMarker(string expectedMarker)
    {
        if (!Directory.Exists(_root)) throw new StorageBlockedException("storage_root_unavailable");
        string markerPath = Path.Combine(_root, MarkerFileName);
        ValidateRootAndParents(markerPath);
        if (!File.Exists(markerPath)) throw new StorageBlockedException("storage_marker_unavailable");
        FileAttributes attributes = File.GetAttributes(markerPath);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory))
            throw new StorageBlockedException("storage_marker_invalid");

        using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64,
            FileOptions.SequentialScan);
        if (marker.Length != 32) throw new StorageBlockedException("storage_marker_invalid");
        Span<byte> bytes = stackalloc byte[32];
        marker.ReadExactly(bytes);
        if (!string.Equals(System.Text.Encoding.ASCII.GetString(bytes), expectedMarker, StringComparison.Ordinal))
            throw new StorageBlockedException("storage_marker_mismatch");
    }

    private void ValidateRootAndParents(string path)
    {
        DirectoryInfo? root = new(_root);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new StorageBlockedException("storage_root_unavailable");

        for (DirectoryInfo? directory = root; directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new StorageBlockedException("storage_path_crosses_link");

        string? parent = Path.GetDirectoryName(path);
        if (parent is null || !IsSameOrChild(parent, _root)) throw new StorageBlockedException("storage_path_outside_root");
        var pending = new Stack<string>();
        for (DirectoryInfo? current = new(parent); current is not null && IsSameOrChild(current.FullName, _root);
             current = current.Parent)
        {
            pending.Push(current.FullName);
            if (PathEquals(current.FullName, _root)) break;
        }
        while (pending.TryPop(out string? directoryPath))
        {
            if (!Directory.Exists(directoryPath)) continue;
            FileAttributes attributes = File.GetAttributes(directoryPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new StorageBlockedException("storage_path_crosses_link");
            if (!attributes.HasFlag(FileAttributes.Directory)) throw new StorageBlockedException("storage_parent_is_file");
        }
    }

    private static bool IsYear(string value) => value.Length == 4
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int year) && year is >= 1 and <= 9999;

    private static bool IsMonth(string value) => value.Length == 2
        && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int month) && month is >= 1 and <= 12;

    private static bool IsLegacyYearMonth(string value) => value.Length == 7
        && value[4] is '.' or '-'
        && IsAsciiDigits(value.AsSpan(0, 4)) && IsAsciiDigits(value.AsSpan(5, 2))
        && int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out int year) && year is >= 1 and <= 9999
        && int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int month) && month is >= 1 and <= 12;

    private static bool IsAsciiDigits(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
            if (character is < '0' or > '9') return false;
        return true;
    }

    private static bool IsChildPath(string candidate, string root) =>
        IsSameOrChild(candidate, root) && !PathEquals(candidate, root);

    private static bool IsSameOrChild(string candidate, string root)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string fullCandidate = Path.GetFullPath(candidate);
        if (PathEquals(fullCandidate, fullRoot)) return true;
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, PathComparison);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record AuthorizedPath(string FullPath, long? ExpectedSize);

    private sealed class StorageBlockedException(string code) : Exception
    {
        public string Code { get; } = code;
    }
}
