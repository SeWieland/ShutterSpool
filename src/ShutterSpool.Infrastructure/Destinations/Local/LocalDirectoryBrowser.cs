using ShutterSpool.Application.Delivery;

namespace ShutterSpool.Infrastructure.Destinations.Local;

internal static class LocalDirectoryBrowser
{
    private const int MaximumEntries = 200;

    public static LocalDirectoryListingResult Browse(string? path, CancellationToken cancellationToken, string? assignedRoot = null)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (assignedRoot is not null && string.IsNullOrWhiteSpace(path)) path = assignedRoot;
            if (string.IsNullOrWhiteSpace(path))
            {
                LocalDirectoryEntry[] roots = DriveInfo.GetDrives()
                    .Select(drive => new LocalDirectoryEntry(drive.Name, Path.GetFullPath(drive.RootDirectory.FullName)))
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new(new LocalDirectoryListing(null, null, roots, false), null);
            }

            path = NormalizePath(path);
            if (path.Length > 2_048 || !Path.IsPathFullyQualified(path))
                return new(null, "Enter an absolute directory path.");

            string fullPath = Path.GetFullPath(path);
            if (assignedRoot is not null && !WithinRoot(fullPath, assignedRoot))
                return new(null, "The directory is outside the workspace destination root.");
            if (!Directory.Exists(fullPath)) return new(null, "The directory does not exist or cannot be read.");
            EnsureSafeDirectoryPath(fullPath);
            FileAttributes currentAttributes = File.GetAttributes(fullPath);
            if (!PathEquals(fullPath, Path.GetPathRoot(fullPath))
                && (currentAttributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                return new(null, "The directory does not exist or cannot be read.");

            List<LocalDirectoryEntry> entries = [];
            string[] children = Directory.EnumerateDirectories(fullPath).Take(MaximumEntries + 1).ToArray();
            bool truncated = children.Length > MaximumEntries;
            foreach (string child in children.Take(MaximumEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System))
                    != FileAttributes.Directory) continue;
                entries.Add(new(Path.GetFileName(child), Path.GetFullPath(child)));
            }
            entries.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
            string? parent = Directory.GetParent(fullPath)?.FullName;
            if (assignedRoot is not null && parent is not null && !WithinRoot(parent, assignedRoot)) parent = null;
            return new(new LocalDirectoryListing(fullPath, parent is null ? null : Path.GetFullPath(parent), entries, truncated), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, "Enter a valid absolute directory path.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new(null, "The directory does not exist or cannot be read.");
        }
    }

    private static bool WithinRoot(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathEquals(path, root) || path.StartsWith(root + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    internal static string NormalizePath(string path)
    {
        string normalized = path.Trim();
        if (normalized.Length >= 2 && normalized[0] == '"' && normalized[^1] == '"')
            normalized = normalized[1..^1].Trim();
        return normalized;
    }

    private static void EnsureSafeDirectoryPath(string path)
    {
        for (string? current = path; current is not null; current = Directory.GetParent(current)?.FullName)
        {
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Directory path is redirected.");
        }
    }

    private static bool PathEquals(string left, string? right) => right is not null && string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
