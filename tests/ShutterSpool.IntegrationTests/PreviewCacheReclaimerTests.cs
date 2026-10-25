using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Preview;
using ShutterSpool.Infrastructure.Preview;

namespace ShutterSpool.IntegrationTests;

public sealed class PreviewCacheReclaimerTests
{
    [Fact]
    public async Task Oversized_derivative_blocks_reclamation_before_any_candidate_is_deleted()
    {
        using var fixture = new CacheFixture();
        string directory = fixture.AssetDirectory(Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string valid = Path.Combine(directory, "grid-480x480.webp");
        string oversized = Path.Combine(directory, "detail-2048x2048.webp");
        await File.WriteAllBytesAsync(valid, [1, 2, 3], TestContext.Current.CancellationToken);
        using (var file = new FileStream(oversized, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(4 * 1024 * 1024 + 1);
        }

        PreviewCacheReclaimResult result = await fixture.CreateCache().ReclaimAsync(long.MaxValue, 100,
            TestContext.Current.CancellationToken);

        Assert.Equal("cache_reclaim_oversized_derivative", result.FailureCode);
        Assert.Equal(0, result.RemovedBytes);
        Assert.Equal(0, result.RemovedEntries);
        Assert.True(result.MoreMayRemain);
        Assert.True(File.Exists(valid));
        Assert.True(File.Exists(oversized));
    }

    [Fact]
    public async Task Equal_age_candidates_are_reclaimed_in_ordinal_path_order()
    {
        using var fixture = new CacheFixture();
        string directory = fixture.AssetDirectory(Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string grid = Path.Combine(directory, "grid-480x480.webp");
        string detail = Path.Combine(directory, "detail-2048x2048.webp");
        await File.WriteAllBytesAsync(grid, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(detail, [4, 5, 6], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(grid, DateTime.UnixEpoch);
        File.SetLastWriteTimeUtc(detail, DateTime.UnixEpoch);

        PreviewCacheReclaimResult result = await fixture.CreateCache().ReclaimAsync(long.MaxValue, 1,
            TestContext.Current.CancellationToken);

        Assert.Null(result.FailureCode);
        Assert.Equal(1, result.RemovedEntries);
        Assert.True(result.MoreMayRemain);
        Assert.False(File.Exists(detail));
        Assert.True(File.Exists(grid));
    }

    [Fact]
    public async Task Reclaims_a_whole_disposable_preview_larger_than_the_remaining_deficit()
    {
        using var fixture = new CacheFixture();
        string directory = fixture.AssetDirectory(Guid.NewGuid(), Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string preview = Path.Combine(directory, "grid-480x480.webp");
        await File.WriteAllBytesAsync(preview, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        PreviewCacheReclaimResult result = await fixture.CreateCache().ReclaimAsync(2, 1,
            TestContext.Current.CancellationToken);
        Assert.Equal(4, result.RemovedBytes);
        Assert.Equal(1, result.RemovedEntries);
        Assert.False(File.Exists(preview));
    }

    [Fact]
    public async Task Reclaims_only_recognized_derivatives_within_entry_and_byte_caps()
    {
        using var fixture = new CacheFixture();
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        string assetDirectory = fixture.AssetDirectory(captureId, assetId);
        Directory.CreateDirectory(assetDirectory);
        string oldest = Path.Combine(assetDirectory, "grid-480x480.webp");
        string newer = Path.Combine(assetDirectory, "detail-2048x2048.webp");
        await File.WriteAllBytesAsync(oldest, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(newer, [5, 6, 7, 8], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(oldest, DateTime.UnixEpoch);
        File.SetLastWriteTimeUtc(newer, DateTime.UnixEpoch.AddMinutes(1));
        await File.WriteAllBytesAsync(Path.Combine(assetDirectory, "notes.txt"), [9], TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "unrecognized"));
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "unrecognized", "unrelated.webp"), [10], TestContext.Current.CancellationToken);

        PreviewCacheReclaimResult result = await fixture.CreateCache().ReclaimAsync(4, 1,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.RemovedBytes);
        Assert.Equal(1, result.RemovedEntries);
        Assert.True(result.MoreMayRemain);
        Assert.Null(result.FailureCode);
        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(Path.Combine(assetDirectory, "notes.txt")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "unrecognized", "unrelated.webp")));
    }

    [Fact]
    public async Task Concurrent_cache_instances_do_not_report_the_same_deleted_bytes_twice()
    {
        using var fixture = new CacheFixture();
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        string assetDirectory = fixture.AssetDirectory(captureId, assetId);
        Directory.CreateDirectory(assetDirectory);
        long expectedBytes = 0;
        for (int index = 0; index < 12; index++)
        {
            byte[] bytes = [.. Enumerable.Repeat((byte)index, index + 1)];
            expectedBytes += bytes.Length;
            await File.WriteAllBytesAsync(Path.Combine(assetDirectory, $"grid-{64 + index}x{64 + index}.webp"), bytes,
                TestContext.Current.CancellationToken);
        }
        var first = fixture.CreateCache();
        var second = fixture.CreateCache();
        using var start = new Barrier(3);
        Task<PreviewCacheReclaimResult> Reclaim(IPreviewCacheReclaimer cache) => Task.Run(async () =>
        {
            start.SignalAndWait(TestContext.Current.CancellationToken);
            return await cache.ReclaimAsync(long.MaxValue, 100, TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        Task<PreviewCacheReclaimResult> one = Reclaim(first);
        Task<PreviewCacheReclaimResult> two = Reclaim(second);
        start.SignalAndWait(TestContext.Current.CancellationToken);
        PreviewCacheReclaimResult[] results = await Task.WhenAll(one, two);

        Assert.Equal(expectedBytes, results.Sum(result => result.RemovedBytes));
        Assert.Equal(12, results.Sum(result => result.RemovedEntries));
        Assert.All(results, result => Assert.Null(result.FailureCode));
        Assert.Empty(Directory.EnumerateFiles(assetDirectory));
    }

    [Fact]
    public async Task Linked_cache_asset_directory_is_reported_without_following_or_deleting_external_files()
    {
        using var fixture = new CacheFixture();
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        string outsideDirectory = Path.Combine(fixture.BasePath, "outside");
        Directory.CreateDirectory(outsideDirectory);
        string outsideFile = Path.Combine(outsideDirectory, "grid-480x480.webp");
        await File.WriteAllBytesAsync(outsideFile, [1, 2, 3], TestContext.Current.CancellationToken);
        string captureDirectory = Path.Combine(fixture.Root, captureId.ToString("N"));
        Directory.CreateDirectory(captureDirectory);
        string assetLink = Path.Combine(captureDirectory, assetId.ToString("N"));
        try
        {
            Directory.CreateSymbolicLink(assetLink, outsideDirectory);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("This platform does not allow creating a synthetic directory link.");
        }

        try
        {
            PreviewCacheReclaimResult result = await fixture.CreateCache().ReclaimAsync(1024, 10,
                TestContext.Current.CancellationToken);

            Assert.Equal("cache_reclaim_linked_path", result.FailureCode);
            Assert.Equal(0, result.RemovedBytes);
            Assert.True(File.Exists(outsideFile));
        }
        finally
        {
            Directory.Delete(assetLink);
        }
    }

    private sealed class CacheFixture : IDisposable
    {
        public string BasePath { get; } = Path.Combine(Path.GetTempPath(), $"m205-cache-reclaim-{Guid.NewGuid():N}");
        public string Root => Path.Combine(BasePath, "previews");

        public CacheFixture() => Directory.CreateDirectory(Root);

        public string AssetDirectory(Guid captureId, Guid assetId) => Path.Combine(Root, captureId.ToString("N"), assetId.ToString("N"));

        public FileSystemPreviewCache CreateCache() => new(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ShutterSpool:Preview:CacheRootPath"] = Root }).Build());

        public void Dispose()
        {
            if (Directory.Exists(BasePath)) Directory.Delete(BasePath, recursive: true);
        }
    }
}
