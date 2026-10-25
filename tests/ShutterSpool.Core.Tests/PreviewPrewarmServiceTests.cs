using ShutterSpool.Application.Preview;

namespace ShutterSpool.Core.Tests;

public sealed class PreviewPrewarmServiceTests
{
    [Fact]
    public async Task Processes_claimed_work_and_persists_the_preview_outcome()
    {
        var settings = new PreviewSettings();
        var work = new PreviewPrewarmWork(Guid.NewGuid(), Guid.NewGuid(), PreviewProfile.Grid, Guid.NewGuid(), 1,
            new PreviewSize(settings.GridWidth, settings.GridHeight));
        var adapters = new FakePreviewAdapters(work.AssetId);
        var store = new FakeStore(work);
        var service = new PreviewPrewarmService(store, adapters.CreateService(settings), settings, TimeProvider.System);

        Assert.True(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(PreviewWarmOutcome.Ready, store.FinishedOutcome);
        Assert.Equal(work, store.FinishedWork);
        Assert.Equal((work.CaptureId, work.AssetId, work.Profile), service.LastReadyPreview);
        Assert.False(await new PreviewPrewarmService(new FakeStore(null), adapters.CreateService(settings), settings, TimeProvider.System)
            .ProcessOneAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Preview_hints_follow_publication_and_cache_regeneration_only()
    {
        var settings = new PreviewSettings();
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        var adapters = new FakePreviewAdapters(assetId);
        PreviewPrewarmWork Work() => new(assetId, captureId, PreviewProfile.Grid, Guid.NewGuid(), 1,
            new PreviewSize(settings.GridWidth, settings.GridHeight));

        var first = new PreviewPrewarmService(new FakeStore(Work()), adapters.CreateService(settings), settings, TimeProvider.System);
        await first.ProcessOneAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(first.LastReadyPreview);

        var cacheHit = new PreviewPrewarmService(new FakeStore(Work()), adapters.CreateService(settings), settings, TimeProvider.System);
        await cacheHit.ProcessOneAsync(TestContext.Current.CancellationToken);
        Assert.Null(cacheHit.LastReadyPreview);

        adapters.Cache.Clear();
        var regenerated = new PreviewPrewarmService(new FakeStore(Work()), adapters.CreateService(settings), settings, TimeProvider.System);
        await regenerated.ProcessOneAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(regenerated.LastReadyPreview);
    }

    [Fact]
    public async Task Prewarm_finishes_ready_without_a_hint_when_cache_admission_declines_retention()
    {
        var settings = new PreviewSettings();
        var work = new PreviewPrewarmWork(Guid.NewGuid(), Guid.NewGuid(), PreviewProfile.Grid, Guid.NewGuid(), 1,
            new PreviewSize(settings.GridWidth, settings.GridHeight));
        var adapters = new FakePreviewAdapters(work.AssetId) { DeclineWrite = true };
        var store = new FakeStore(work);
        var service = new PreviewPrewarmService(store, adapters.CreateService(settings), settings, TimeProvider.System);

        Assert.True(await service.ProcessOneAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PreviewWarmOutcome.Ready, store.FinishedOutcome);
        Assert.Null(service.LastReadyPreview);
        Assert.Empty(adapters.Cache);
    }

    private sealed class FakeStore(PreviewPrewarmWork? work) : IPreviewPrewarmStore
    {
        public PreviewWarmOutcome? FinishedOutcome { get; private set; }
        public PreviewPrewarmWork? FinishedWork { get; private set; }
        public Task<PreviewPrewarmWork?> ClaimAsync(PreviewSettings settings, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult(work);
        public Task<bool> FinishAsync(PreviewPrewarmWork work, PreviewWarmOutcome outcome, DateTimeOffset now, CancellationToken cancellationToken)
        {
            FinishedWork = work;
            FinishedOutcome = outcome;
            return Task.FromResult(true);
        }
    }

    private sealed class FakePreviewAdapters(Guid assetId) : IPreviewSourceResolver, IPreviewCache, IPreviewGenerator
    {
        public Dictionary<string, byte[]> Cache { get; } = [];
        public bool DeclineWrite { get; set; }
        public PreviewService CreateService(PreviewSettings settings) => new(this, this, this, settings);
        public Task<PreviewResolution> ResolveAsync(Guid captureId, CancellationToken cancellationToken) =>
            Task.FromResult(new PreviewResolution(true, new PreviewSource(assetId, "spool/jpeg", 1)));
        public Task<byte[]?> ReadAsync(string key, PreviewSize size, CancellationToken cancellationToken) => Task.FromResult(Cache.GetValueOrDefault(key));
        public Task<bool> ContainsAsync(string key, PreviewSize size, CancellationToken cancellationToken) => Task.FromResult(Cache.ContainsKey(key));
        public Task<bool> WriteAsync(string key, byte[] content, CancellationToken cancellationToken)
        {
            if (DeclineWrite) return Task.FromResult(false);
            Cache[key] = content;
            return Task.FromResult(true);
        }
        public Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, CancellationToken cancellationToken) => Task.FromResult<byte[]>([1]);
    }
}
