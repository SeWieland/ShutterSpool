using ShutterSpool.Application.Ingress;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed class CameraIngressDirectoriesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shutterspool-camera-directories-tests", Guid.NewGuid().ToString("N"));
    private readonly FileSystemCameraIngressDirectories _directories;

    public CameraIngressDirectoriesTests() => _directories = new(_root);

    [Fact]
    public async Task Stores_and_lists_only_empty_virtual_directories_per_source()
    {
        Guid source = Guid.NewGuid();
        Guid otherSource = Guid.NewGuid();
        await _directories.CreateDirectoryAsync(source, "/DCIM/100MEDIA", TestContext.Current.CancellationToken);
        await _directories.CreateDirectoryAsync(source, "/DCIM/100MEDIA", TestContext.Current.CancellationToken);

        CameraFileInfo folder = (await _directories.GetFileInfoAsync(source, "/DCIM", TestContext.Current.CancellationToken))!;
        IReadOnlyList<CameraFileInfo> entries = await _directories.GetDirectoryAsync(source, "/DCIM", TestContext.Current.CancellationToken);
        IReadOnlyList<CameraFileInfo> otherEntries = await _directories.GetDirectoryAsync(otherSource, "/DCIM", TestContext.Current.CancellationToken);

        Assert.True(folder.IsDirectory);
        Assert.Equal("DCIM", folder.Name);
        Assert.Equal("100MEDIA", Assert.Single(entries).Name);
        Assert.Empty(otherEntries);
        Assert.All(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), path => Assert.Fail($"Directory metadata root contains a payload file: {path}"));
    }

    [Fact]
    public async Task Rejects_a_virtual_directory_that_escapes_the_metadata_root()
    {
        await Assert.ThrowsAsync<IOException>(() => _directories.CreateDirectoryAsync(Guid.NewGuid(), "/../../outside", TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(Path.GetFullPath(Path.Combine(_root, "..", "outside"))));
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
