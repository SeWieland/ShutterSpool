using ShutterSpool.Application.Ingest;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed class OriginalReaderTests
{
    [Fact]
    public async Task Original_reader_accepts_only_committed_spool_keys()
    {
        string root = Path.Combine(Path.GetTempPath(), $"m102_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = root, ReservedBytes = 0 });
            SpoolWrite write = await spool.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new MemoryStream([1, 2, 3]), null, TestContext.Current.CancellationToken);
            await using Stream original = await spool.OpenReadAsync(write.FinalKey, TestContext.Current.CancellationToken);
            Assert.Equal([1, 2, 3], await ReadAllBytesAsync(original));
            await Assert.ThrowsAsync<InvalidOperationException>(() => spool.OpenReadAsync(write.PartialKey, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<InvalidOperationException>(() => spool.OpenReadAsync("spool/../../outside", TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

}
