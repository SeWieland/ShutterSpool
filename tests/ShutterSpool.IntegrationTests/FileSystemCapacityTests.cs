using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed class FileSystemCapacityTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "shutterspool-capacity-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Capacity_lookup_uses_the_nearest_existing_parent_of_a_not_yet_created_storage_root()
    {
        Directory.CreateDirectory(_parent);
        string configuredRoot = Path.Combine(_parent, "not-created", "spool");
        DriveInfo actual = FileSystemCapacity.GetDriveForPath(configuredRoot);
        DriveInfo expected = new(_parent);

        Assert.Equal(expected.TotalSize, actual.TotalSize);
    }

    [Fact]
    public void Capacity_lookup_uses_a_distinct_linux_mount_when_available()
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/dev/shm")) Assert.Skip("A distinct Linux /dev/shm mount is unavailable.");

        DriveInfo sharedMemory = new("/dev/shm");
        DriveInfo rootFilesystem = new("/");
        if (sharedMemory.TotalSize == rootFilesystem.TotalSize) Assert.Skip("/dev/shm is not a distinct-size mount in this environment.");

        string root = Path.Combine("/dev/shm", "shutterspool-capacity-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            DriveInfo actual = FileSystemCapacity.GetDriveForPath(Path.Combine(root, "not-created", "spool"));
            Assert.Equal(sharedMemory.TotalSize, actual.TotalSize);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_parent)) Directory.Delete(_parent, recursive: true);
    }
}
