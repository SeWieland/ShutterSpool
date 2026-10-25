using ShutterSpool.Application.Ingress;

namespace ShutterSpool.Infrastructure.Spool;

internal sealed class FileSystemCameraIngressDirectories(string rootPath) : ICameraIngressDirectories
{
    private string Root => Path.GetFullPath(rootPath);

    public Task CreateDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureDirectory(VirtualPath(sourceId, virtualPath));
        return Task.CompletedTask;
    }

    public Task<CameraFileInfo?> GetFileInfoAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = VirtualPath(sourceId, virtualPath);
        EnsureSafePath(path);
        if (!Directory.Exists(path)) return Task.FromResult<CameraFileInfo?>(null);
        var directory = new DirectoryInfo(path);
        string name = string.Equals(path, SourceRoot(sourceId), PathComparison) ? "/" : directory.Name;
        return Task.FromResult<CameraFileInfo?>(new(name, 0, true, directory.LastWriteTimeUtc));
    }

    public Task<IReadOnlyList<CameraFileInfo>> GetDirectoryAsync(Guid sourceId, string virtualPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = VirtualPath(sourceId, virtualPath);
        EnsureSafePath(path);
        if (!Directory.Exists(path)) return Task.FromResult<IReadOnlyList<CameraFileInfo>>([]);
        IReadOnlyList<CameraFileInfo> directories = Directory.EnumerateDirectories(path)
            .Select(entry =>
            {
                EnsureSafePath(entry);
                var directory = new DirectoryInfo(entry);
                return new CameraFileInfo(directory.Name, 0, true, directory.LastWriteTimeUtc);
            }).ToArray();
        return Task.FromResult(directories);
    }

    private string SourceRoot(Guid sourceId) => Path.Combine(Root, sourceId.ToString("N"), "files");

    private string VirtualPath(Guid sourceId, string virtualPath)
    {
        string userRoot = Path.GetFullPath(SourceRoot(sourceId));
        string relative = virtualPath.Trim('/').Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(userRoot, relative));
        if (!full.StartsWith(userRoot + Path.DirectorySeparatorChar, PathComparison) && !string.Equals(full, userRoot, PathComparison))
            throw new IOException("Camera virtual path escaped its login directory root.");
        return full;
    }

    private void EnsureDirectory(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison) && !string.Equals(full, Root, PathComparison))
            throw new IOException("Camera directory path escaped its metadata root.");
        EnsureRootAncestorsSafe();
        Directory.CreateDirectory(Root);
        EnsureSafePath(Root);
        string relative = Path.GetRelativePath(Root, full);
        string current = Root;
        if (relative == ".") return;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) EnsureSafePath(current);
            else Directory.CreateDirectory(current);
        }
    }

    private void EnsureSafePath(string path)
    {
        EnsureRootAncestorsSafe();
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison) && !string.Equals(full, Root, PathComparison))
            throw new IOException("Camera directory path escaped its metadata root.");
        string relative = Path.GetRelativePath(Root, full);
        string current = Root;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Camera directories do not follow reparse points.");
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Camera directories do not follow reparse points.");
        }
    }

    private void EnsureRootAncestorsSafe()
    {
        string? volumeRoot = Path.GetPathRoot(Root);
        if (string.IsNullOrEmpty(volumeRoot)) throw new IOException("Camera directory metadata root is invalid.");
        string current = volumeRoot;
        string relative = Path.GetRelativePath(volumeRoot, Root);
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Camera directories do not follow reparse points.");
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Camera directories do not follow reparse points.");
        }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
