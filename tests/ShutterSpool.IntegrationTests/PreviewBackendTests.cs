using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Preview;

namespace ShutterSpool.IntegrationTests;

[Collection(MediaTempFileCollection.Name)]
public sealed class PreviewBackendTests
{
    [Fact]
    public async Task Invalid_cached_WebP_is_not_served()
    {
        string root = Path.Combine(Path.GetTempPath(), $"m104-cache-{Guid.NewGuid():N}");
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ShutterSpool:Preview:CacheRootPath"] = root,
            }).Build();
            using var client = new HttpClient(new CountingHandler()) { BaseAddress = new Uri("http://imaginary.test/") };
            var cache = new FileSystemPreviewCache(config);
            string key = $"{Guid.NewGuid():N}/{Guid.NewGuid():N}/grid-480x480.webp";
            byte[] headerOnly = "RIFF\0\0\0\0WEBP"u8.ToArray();

            string path = Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, headerOnly, TestContext.Current.CancellationToken);
            Assert.False(await cache.ContainsAsync(key, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
            Assert.Null(await cache.ReadAsync(key, new PreviewSize(480, 480), TestContext.Current.CancellationToken));

            byte[] valid = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
            await File.WriteAllBytesAsync(path, valid, TestContext.Current.CancellationToken);
            Assert.True(await cache.ContainsAsync(key, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
            Assert.False(await cache.ContainsAsync(key, new PreviewSize(1, 1), TestContext.Current.CancellationToken));
            Assert.Null(await cache.ReadAsync(key, new PreviewSize(1, 1), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Memory_cache_has_bounded_LRU_entries_and_defensive_reads()
    {
        byte[] webp = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        Guid firstAsset = Guid.NewGuid();
        Guid secondAsset = Guid.NewGuid();
        var cache = new MemoryPreviewCache(new PreviewSettings { MemoryCacheBytes = webp.Length });
        string first = $"{Guid.NewGuid():N}/{firstAsset:N}/grid-480x480.webp";
        string second = $"{Guid.NewGuid():N}/{secondAsset:N}/grid-480x480.webp";

        await cache.WriteAsync(first, webp, TestContext.Current.CancellationToken);
        byte[]? firstRead = await cache.ReadAsync(first, new PreviewSize(480, 480), TestContext.Current.CancellationToken);
        Assert.Equal(webp, firstRead);
        firstRead![0] = 0;
        Assert.Equal(webp, await cache.ReadAsync(first, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
        await cache.WriteAsync(second, webp, TestContext.Current.CancellationToken);

        Assert.Null(await cache.ReadAsync(first, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
        Assert.NotNull(await cache.ReadAsync(second, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Memory_asset_invalidation_removes_exact_asset_entries()
    {
        byte[] webp = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        Guid assetId = Guid.NewGuid();
        Guid otherAssetId = Guid.NewGuid();
        var cache = new MemoryPreviewCache(new PreviewSettings { MemoryCacheBytes = webp.Length * 2L });
        string key = $"{Guid.NewGuid():N}/{assetId:N}/grid-480x480.webp";
        string otherKey = $"{Guid.NewGuid():N}/{otherAssetId:N}/grid-480x480.webp";
        await cache.WriteAsync(key, webp, TestContext.Current.CancellationToken);
        await cache.WriteAsync(otherKey, webp, TestContext.Current.CancellationToken);

        await cache.RemoveAssetAsync(assetId, TestContext.Current.CancellationToken);

        Assert.Null(await cache.ReadAsync(key, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
        Assert.NotNull(await cache.ReadAsync(otherKey, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Memory_cache_does_not_retain_an_entry_larger_than_its_budget()
    {
        byte[] webp = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        var cache = new MemoryPreviewCache(new PreviewSettings { MemoryCacheBytes = webp.Length - 1 });
        string key = $"{Guid.NewGuid():N}/{Guid.NewGuid():N}/grid-480x480.webp";

        await cache.WriteAsync(key, webp, TestContext.Current.CancellationToken);

        Assert.False(await cache.ContainsAsync(key, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Asset_invalidation_removes_only_that_assets_disk_derivatives()
    {
        string root = Path.Combine(Path.GetTempPath(), $"m205-invalidation-{Guid.NewGuid():N}");
        Guid assetId = Guid.NewGuid();
        Guid otherAssetId = Guid.NewGuid();
        string capture = Guid.NewGuid().ToString("N");
        string target = Path.Combine(root, capture, assetId.ToString("N"));
        string other = Path.Combine(root, capture, otherAssetId.ToString("N"));
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(other);
        await File.WriteAllBytesAsync(Path.Combine(target, "grid-480x480.webp"), [1], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(target, "notes.txt"), "preserve", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(other, "grid-480x480.webp"), [2], TestContext.Current.CancellationToken);

        await new FileSystemPreviewCache(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Preview:CacheRootPath"] = root,
        }).Build()).RemoveAssetAsync(assetId, TestContext.Current.CancellationToken);

        Assert.True(Directory.Exists(target));
        Assert.False(File.Exists(Path.Combine(target, "grid-480x480.webp")));
        Assert.Equal("preserve", await File.ReadAllTextAsync(Path.Combine(target, "notes.txt"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(other, "grid-480x480.webp")));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task Memory_mode_asset_invalidation_also_removes_lingering_disk_derivatives()
    {
        string root = Path.Combine(Path.GetTempPath(), $"m205-memory-invalidation-{Guid.NewGuid():N}");
        byte[] webp = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        Guid assetId = Guid.NewGuid();
        string key = $"{Guid.NewGuid():N}/{assetId:N}/grid-480x480.webp";
        var memory = new MemoryPreviewCache(new PreviewSettings { MemoryCacheBytes = webp.Length * 2L });
        var disk = new FileSystemPreviewCache(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Preview:CacheRootPath"] = root,
        }).Build());
        try
        {
            await memory.WriteAsync(key, webp, TestContext.Current.CancellationToken);
            await disk.WriteAsync(key, webp, TestContext.Current.CancellationToken);
            var invalidator = new PreviewAssetCacheInvalidator(memory, disk);

            await invalidator.RemoveAssetAsync(assetId, TestContext.Current.CancellationToken);

            Assert.Null(await memory.ReadAsync(key, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
            Assert.Null(await disk.ReadAsync(key, new PreviewSize(480, 480), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task An_oversized_decoded_JPEG_is_rejected_before_Imaginary_receives_media()
    {
        byte[] jpeg = JpegHeader(20_000, 20_000);
        var handler = new CountingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
        var settings = new PreviewSettings();
        using var gate = new PreviewGenerationGate(settings);
        var backend = new ImaginaryPreviewGenerator(new MemoryOriginal(jpeg), client, settings, gate);

        await Assert.ThrowsAsync<PreviewPermanentException>(() => backend.GenerateAsync(
            new PreviewSource(Guid.NewGuid(), "spool/test", jpeg.Length), new PreviewSize(480, 480), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Malformed_source_and_oversized_output_fail_without_publishing_a_derivative()
    {
        byte[] jpeg = JpegHeader(1, 1);
        var source = new PreviewSource(Guid.NewGuid(), "spool/test", jpeg.Length);
        var size = new PreviewSize(480, 480);
        var settings = new PreviewSettings();
        using var gate = new PreviewGenerationGate(settings);

        var malformed = new CountingHandler(System.Net.HttpStatusCode.BadRequest);
        using var badClient = new HttpClient(malformed) { BaseAddress = new Uri("http://imaginary.test/") };
        var badBackend = new ImaginaryPreviewGenerator(new MemoryOriginal(jpeg), badClient, settings, gate);
        await Assert.ThrowsAsync<PreviewPermanentException>(() => badBackend.GenerateAsync(source, size, TestContext.Current.CancellationToken));
        Assert.Equal(1, malformed.Requests);

        var oversized = new CountingHandler(System.Net.HttpStatusCode.OK, new byte[4 * 1024 * 1024 + 1]);
        using var largeClient = new HttpClient(oversized) { BaseAddress = new Uri("http://imaginary.test/") };
        var largeBackend = new ImaginaryPreviewGenerator(new MemoryOriginal(jpeg), largeClient, settings, gate);
        await Assert.ThrowsAsync<PreviewPermanentException>(() => largeBackend.GenerateAsync(source, size, TestContext.Current.CancellationToken));
        Assert.Equal(1, oversized.Requests);

        var invalidWebP = new CountingHandler(System.Net.HttpStatusCode.OK, "RIFF\0\0\0\0WEBP"u8.ToArray());
        using var invalidClient = new HttpClient(invalidWebP) { BaseAddress = new Uri("http://imaginary.test/") };
        var invalidBackend = new ImaginaryPreviewGenerator(new MemoryOriginal(jpeg), invalidClient, settings, gate);
        await Assert.ThrowsAsync<InvalidDataException>(() => invalidBackend.GenerateAsync(source, size, TestContext.Current.CancellationToken));
        Assert.Equal(1, invalidWebP.Requests);
    }

    [Fact]
    public async Task Configured_generation_limit_bounds_parallel_uploads()
    {
        byte[] jpeg = JpegHeader(1, 1);
        var settings = new PreviewSettings { MaxConcurrentGenerations = 1, GenerationQueueTimeoutSeconds = 1 };
        using var gate = new PreviewGenerationGate(settings);
        var handler = new BlockingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
        var backend = new ImaginaryPreviewGenerator(new MemoryOriginal(jpeg), client, settings, gate);
        var source = new PreviewSource(Guid.NewGuid(), "spool/test", jpeg.Length);
        var size = new PreviewSize(480, 480);

        Task<byte[]> first = backend.GenerateAsync(source, size, TestContext.Current.CancellationToken);
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<TimeoutException>(() => backend.GenerateAsync(source, size, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);

        handler.Release.SetResult(true);
        Assert.NotEmpty(await first);
    }

    [Fact]
    public async Task Nonseekable_preview_original_is_spooled_with_a_bounded_buffer()
    {
        byte[] jpeg = JpegHeader(1, 1);
        var handler = new CountingHandler(body: Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
        var settings = new PreviewSettings();
        using var gate = new PreviewGenerationGate(settings);
        var generator = new ImaginaryPreviewGenerator(new NonSeekableOriginal(jpeg), client, settings, gate);

        byte[] output = await generator.GenerateAsync(new PreviewSource(Guid.NewGuid(), "spool/test", jpeg.Length),
            new PreviewSize(480, 480), TestContext.Current.CancellationToken);

        Assert.NotEmpty(output);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Truncated_JPEG_header_is_rejected_before_upload()
    {
        byte[] jpeg = JpegHeader(1, 1)[..11];
        var handler = new CountingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
        var settings = new PreviewSettings();
        using var gate = new PreviewGenerationGate(settings);
        var generator = new ImaginaryPreviewGenerator(new MemoryOriginal(jpeg), client, settings, gate);

        await Assert.ThrowsAsync<PreviewPermanentException>(() => generator.GenerateAsync(
            new PreviewSource(Guid.NewGuid(), "spool/test", jpeg.Length), new PreviewSize(480, 480), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JPEG_metadata_segments_are_skipped_and_the_full_original_is_uploaded(bool nonseekable)
    {
        byte[] header = JpegHeader(1, 1);
        byte[] jpeg = [.. header[..2], 0xff, 0xe1, 0, 6, 1, 2, 3, 4, .. header[2..]];
        byte[] webp = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        var handler = new CountingHandler(body: webp);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
        var settings = new PreviewSettings();
        using var gate = new PreviewGenerationGate(settings);
        ICommittedOriginalReader original = nonseekable ? new NonSeekableOriginal(jpeg) : new MemoryOriginal(jpeg);
        var generator = new ImaginaryPreviewGenerator(original, client, settings, gate);

        byte[] output = await generator.GenerateAsync(new PreviewSource(Guid.NewGuid(), "spool/test", jpeg.Length),
            new PreviewSize(480, 480), TestContext.Current.CancellationToken);

        Assert.Equal(webp, output);
        Assert.Equal(jpeg, handler.UploadedContent);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task JPEG_headers_exceeding_the_limit_are_rejected_before_upload()
    {
        byte[] segment = [0xff, 0xe1, 0xff, 0xff, .. new byte[65_533]];
        byte[] jpeg = [0xff, 0xd8, .. Enumerable.Repeat(segment, 17).SelectMany(bytes => bytes), .. JpegHeader(1, 1)[2..]];
        var handler = new CountingHandler();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://imaginary.test/") };
        var settings = new PreviewSettings();
        using var gate = new PreviewGenerationGate(settings);
        var generator = new ImaginaryPreviewGenerator(new MemoryOriginal(jpeg), client, settings, gate);

        await Assert.ThrowsAsync<PreviewPermanentException>(() => generator.GenerateAsync(
            new PreviewSource(Guid.NewGuid(), "spool/test", jpeg.Length), new PreviewSize(480, 480), TestContext.Current.CancellationToken));
        Assert.Equal(0, handler.Requests);
    }

    private static byte[] JpegHeader(ushort width, ushort height) =>
        [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0, 0xff, 0xd9];

    private sealed class MemoryOriginal(byte[] bytes) : ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    private sealed class NonSeekableOriginal(byte[] bytes) : ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new NonSeekableStream(bytes));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }

    private sealed class CountingHandler(System.Net.HttpStatusCode status = System.Net.HttpStatusCode.OK, byte[]? body = null) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public byte[]? UploadedContent { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            UploadedContent = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA")),
            };
        }
    }
}
