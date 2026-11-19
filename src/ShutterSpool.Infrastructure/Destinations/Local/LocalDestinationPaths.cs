using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Destinations.Local;

internal static class LocalDestinationPaths
{
    public static string? AssignedRoot(WorkspaceContext? workspace) => workspace is not null
        ? Path.Combine(workspace.Required.StorageRoot, "destinations") : null;

    public static bool IsAllowed(string root, WorkspaceContext? workspace)
    {
        string? assigned = AssignedRoot(workspace);
        if (assigned is null) return true;
        try
        {
            string full = Path.GetFullPath(root);
            string allowed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assigned));
            StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!full.Equals(allowed, comparison) && !full.StartsWith(allowed + Path.DirectorySeparatorChar, comparison)) return false;
            for (string? path = full; path is not null; path = Path.GetDirectoryName(path))
            {
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                }
                catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // Missing components may be created later. Continue checking existing ancestors for redirection.
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { return false; }
    }
}
