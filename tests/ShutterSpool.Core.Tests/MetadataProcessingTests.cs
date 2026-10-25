using ShutterSpool.Application.Metadata;
using ShutterSpool.Application.Spool;

namespace ShutterSpool.Core.Tests;

public sealed class MetadataProcessingTests
{
    [Fact]
    public async Task Completed_parse_persists_once()
    {
        byte[] original = [0xff, 0xd8, 0xff, 0xd9];
        var store = new MemoryMetadataStore();
        var reader = new MemoryOriginalReader(original);
        var parser = new StubParser(new AssetMetadata(MetadataOutcome.Partial, "image/jpeg", "JPEG", null, null, null, null, false, null));
        var service = new MetadataProcessingService(store, reader, parser, TimeProvider.System);

        Assert.True(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(MetadataOutcome.Partial, store.Result?.Outcome);
        Assert.False(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, parser.Calls);
    }

    [Fact]
    public async Task Parser_error_releases_work_for_retry()
    {
        var store = new MemoryMetadataStore();
        var service = new MetadataProcessingService(store, new MemoryOriginalReader([1]), new StubParser(null), TimeProvider.System);

        Assert.True(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.True(store.Retried);
        Assert.Null(store.Result);
    }

    private sealed class MemoryMetadataStore : IAssetMetadataStore
    {
        private bool _claimed;
        public AssetMetadata? Result { get; private set; }
        public bool Retried { get; private set; }
        public Task<MetadataWork?> ClaimAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            if (_claimed) return Task.FromResult<MetadataWork?>(null);
            _claimed = true;
            return Task.FromResult<MetadataWork?>(new MetadataWork(Guid.NewGuid(), "spool/key", "camera.jpg", 4, Guid.NewGuid()));
        }
        public Task<bool> RenewAsync(MetadataWork work, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CompleteAsync(MetadataWork work, AssetMetadata metadata, CancellationToken cancellationToken)
        {
            Result = metadata;
            return Task.CompletedTask;
        }
        public Task RetryAsync(MetadataWork work, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Retried = true;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryOriginalReader(byte[] bytes) : ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string spoolKey, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private sealed class StubParser(AssetMetadata? result) : IMediaMetadataParser
    {
        public int Calls { get; private set; }
        public Task<AssetMetadata> ParseAsync(Stream original, string originalName, long size, CancellationToken cancellationToken)
        {
            Calls++;
            return result is null ? throw new IOException("Injected parser fault.") : Task.FromResult(result);
        }
    }
}
