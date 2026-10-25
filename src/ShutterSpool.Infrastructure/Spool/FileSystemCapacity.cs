namespace ShutterSpool.Infrastructure.Spool;

internal static class FileSystemCapacity
{
    public static DriveInfo GetDriveForPath(string path)
    {
        DirectoryInfo directory = new(Path.GetFullPath(path));
        while (!directory.Exists)
            directory = directory.Parent ?? throw new IOException("The configured storage path has no existing parent.");
        return new DriveInfo(directory.FullName);
    }
}
