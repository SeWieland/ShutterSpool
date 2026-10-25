using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Spool;

namespace ShutterSpool.Core.Tests;

public sealed class PreviewServiceTests
{
    [Fact]
    public async Task Requested_dimensions_use_separate_derivatives_and_reject_invalid_sizes_before_media_access()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3)) };
        var service = adapters.CreateService(new PreviewSettings());
        Guid captureId = Guid.NewGuid();
        CancellationToken token = TestContext.Current.CancellationToken;
        await service.GetAsync(captureId, PreviewProfile.Grid, token, 640);
        await service.GetAsync(captureId, PreviewProfile.Grid, token, 1024);
        await service.GetAsync(captureId, PreviewProfile.Grid, token, 640);
        Assert.Equal(2, adapters.GenerationCount);
        Assert.Contains(adapters.Cache.Keys, key => key.EndsWith("grid-640x640.webp", StringComparison.Ordinal));
        Assert.Contains(adapters.Cache.Keys, key => key.EndsWith("grid-1024x1024.webp", StringComparison.Ordinal));
        int reads = adapters.ResolutionCount;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetAsync(captureId, PreviewProfile.Grid, token, 63));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetAsync(captureId, PreviewProfile.Grid, token, 4097));
        Assert.Equal(reads, adapters.ResolutionCount);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(4097)]
    public void Profile_dimensions_outside_the_documented_range_are_rejected(int width)
    {
        Assert.Throws<InvalidOperationException>(() => new PreviewSettings { GridWidth = width }.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Preview_cache_requires_a_positive_memory_budget(long bytes)
    {
        Assert.Throws<InvalidOperationException>(() => new PreviewSettings { MemoryCacheBytes = bytes }.Validate());
    }

    [Fact]
    public void Preview_cache_memory_budget_has_a_hard_upper_bound()
    {
        Assert.Throws<InvalidOperationException>(() => new PreviewSettings
        {
            MemoryCacheBytes = PreviewCacheSettings.MaximumMemoryCacheBytes + 1,
        }.Validate());
    }

    [Fact]
    public void Persisted_preview_cache_settings_apply_to_startup_settings()
    {
        var settings = new PreviewSettings();
        new PreviewCacheSettings(PreviewCacheMode.Memory, 128 * 1024 * 1024).ApplyTo(settings);

        Assert.Equal(PreviewCacheMode.Memory, settings.CacheMode);
        Assert.Equal(128 * 1024 * 1024, settings.MemoryCacheBytes);
    }

    [Fact]
    public void Preview_cache_mode_must_be_supported()
    {
        Assert.Throws<InvalidOperationException>(() => new PreviewSettings { CacheMode = (PreviewCacheMode)99 }.Validate());
    }

    [Fact]
    public void Persisted_preview_cache_values_use_argument_validation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewCacheSettings((PreviewCacheMode)99, 128).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PreviewCacheSettings(PreviewCacheMode.Memory, 0).Validate());
    }

    [Fact]
    public async Task A_late_JPEG_replaces_pending_without_reusing_the_old_source()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, null) };
        var service = adapters.CreateService(new PreviewSettings());
        Guid captureId = Guid.NewGuid();

        Assert.Equal(PreviewState.Pending, (await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);

        adapters.Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3));
        PreviewResult firstReady = await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken);
        Assert.Equal(PreviewState.Ready, firstReady.State);
        Assert.True(firstReady.Published);
        Assert.Equal(adapters.Resolution.Source!.AssetId, firstReady.SourceAssetId);
        Assert.Equal(1, adapters.GenerationCount);
        PreviewResult cacheHit = await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken);
        Assert.Equal(PreviewState.Ready, cacheHit.State);
        Assert.False(cacheHit.Published);
        Assert.Equal(1, adapters.GenerationCount);
        adapters.Cache.Clear();
        Assert.True((await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).Published);
        Assert.Equal(2, adapters.GenerationCount);
    }

    [Fact]
    public async Task An_inflight_source_change_is_visible_on_the_next_request()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/old", 3)) };
        adapters.OnGenerate = () => adapters.Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/new", 3));
        var service = adapters.CreateService(new PreviewSettings());

        Guid captureId = Guid.NewGuid();
        Assert.Equal(PreviewState.Ready, (await service.GetAsync(captureId, PreviewProfile.Detail, TestContext.Current.CancellationToken)).State);
        Assert.Single(adapters.Cache);
        Assert.Equal(1, adapters.ResolutionCount);

        Assert.Equal(PreviewState.Ready, (await service.GetAsync(captureId, PreviewProfile.Detail, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, adapters.Cache.Count);
        Assert.Equal(2, adapters.ResolutionCount);
    }

    [Fact]
    public async Task A_deleted_cache_is_regenerated_from_the_current_source()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3)) };
        var service = adapters.CreateService(new PreviewSettings());
        Guid captureId = Guid.NewGuid();

        await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken);
        adapters.Cache.Clear();
        Assert.Equal(PreviewState.Ready, (await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, adapters.GenerationCount);
    }

    [Fact]
    public async Task Preview_generation_publishes_preparing_and_finishes_the_observation()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3)) };
        var activity = new RecordingPreviewActivityPublisher();
        Guid captureId = Guid.NewGuid();
        var service = new PreviewService(adapters, adapters, adapters, new PreviewSettings(), activity);

        Assert.Equal(PreviewState.Ready, (await service.GetAsync(captureId, PreviewProfile.Grid,
            TestContext.Current.CancellationToken)).State);
        Assert.Equal(captureId, Assert.Single(activity.Started).CaptureId);
        Assert.Equal(activity.Started[0], Assert.Single(activity.FinishedItems));

        await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken);
        Assert.Single(activity.Started);
        Assert.Single(activity.FinishedItems);
    }

    [Fact]
    public async Task Changing_a_profile_does_not_serve_its_old_derivative()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3)) };
        Guid captureId = Guid.NewGuid();
        await adapters.CreateService(new PreviewSettings()).GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken);

        PreviewResult resized = await adapters.CreateService(new PreviewSettings { GridWidth = 640 })
            .GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewState.Ready, resized.State);
        Assert.Equal(2, adapters.GenerationCount);
        Assert.Equal(2, adapters.Cache.Count);
    }

    [Fact]
    public async Task Cache_or_generator_failure_is_unavailable_then_a_later_request_recovers()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3)), FailGeneration = true };
        var service = adapters.CreateService(new PreviewSettings());
        Guid captureId = Guid.NewGuid();

        Assert.Equal(PreviewState.Unavailable, (await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);
        adapters.FailGeneration = false;
        adapters.FailWrite = true;
        Assert.Equal(PreviewState.Unavailable, (await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);
        adapters.FailWrite = false;
        Assert.Equal(PreviewState.Ready, (await service.GetAsync(captureId, PreviewProfile.Grid, TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Generated_preview_is_returned_when_cache_admission_declines_retention()
    {
        var adapters = new FakePreviewAdapters
        {
            Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3)),
            DeclineWrite = true,
        };
        PreviewResult result = await adapters.CreateService(new PreviewSettings()).GetAsync(Guid.NewGuid(),
            PreviewProfile.Grid, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewState.Ready, result.State);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Content);
        Assert.False(result.Published);
        Assert.Empty(adapters.Cache);
    }

    [Fact]
    public async Task Fenced_assets_are_not_read_or_prewarmed()
    {
        Guid assetId = Guid.NewGuid();
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(assetId, "spool/jpeg", 3)) };
        var useGate = new PreviewAssetUseGate();
        var service = new PreviewService(adapters, adapters, adapters, new PreviewSettings(), useGate: useGate);
        using IDisposable fence = await useGate.FenceAsync(assetId, TestContext.Current.CancellationToken);

        Assert.Equal(PreviewState.Unavailable, (await service.GetAsync(Guid.NewGuid(), PreviewProfile.Grid,
            TestContext.Current.CancellationToken)).State);
        Assert.Equal(PreviewWarmOutcome.Retry, await service.WarmAsync(Guid.NewGuid(), assetId, PreviewProfile.Grid,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, adapters.GenerationCount);
    }

    [Fact]
    public async Task Shared_asset_lock_covers_current_source_check_cache_read_generation_and_publication()
    {
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(Guid.NewGuid(), "spool/jpeg", 3)) };
        var locks = new FakeOriginalAssetLock();
        var service = new PreviewService(adapters, adapters, adapters, new PreviewSettings(), assetLock: locks);
        Guid captureId = Guid.NewGuid();

        Assert.Equal(PreviewState.Ready, (await service.GetAsync(captureId, PreviewProfile.Grid,
            TestContext.Current.CancellationToken)).State);
        Assert.Equal(2, adapters.ResolutionCount);
        Assert.Same(locks.LastLease, adapters.SharedLeaseUsed);
        Assert.True(locks.LastLease!.Disposed);

        Assert.Equal(PreviewState.Ready, (await service.GetAsync(captureId, PreviewProfile.Grid,
            TestContext.Current.CancellationToken)).State);
        Assert.Equal(4, adapters.ResolutionCount);
        Assert.Equal(2, locks.SharedAcquisitions);
        Assert.Equal(1, adapters.GenerationCount);
    }

    [Fact]
    public async Task Durable_removal_fence_rejects_cached_preview_hits_and_prewarm()
    {
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        var adapters = new FakePreviewAdapters { Resolution = new(true, new PreviewSource(assetId, "spool/jpeg", 3)) };
        adapters.Cache[$"{captureId:N}/{assetId:N}/grid-480x480.webp"] = [1, 2, 3];
        var locks = new FakeOriginalAssetLock { Available = false };
        var service = new PreviewService(adapters, adapters, adapters, new PreviewSettings(), assetLock: locks);

        Assert.Equal(PreviewState.Unavailable, (await service.GetAsync(captureId, PreviewProfile.Grid,
            TestContext.Current.CancellationToken)).State);
        Assert.Equal(PreviewWarmOutcome.Obsolete, await service.WarmAsync(captureId, assetId, PreviewProfile.Grid,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, adapters.GenerationCount);
        Assert.Equal(locks.SharedAcquisitions, adapters.ResolutionCount);
        Assert.True(locks.LastLease!.Disposed);
    }

    private sealed class FakePreviewAdapters : IPreviewSourceResolver, IPreviewCache, IPreviewGenerator
    {
        public PreviewResolution Resolution { get; set; } = new(false, null);
        public Action? OnGenerate { get; set; }
        public bool FailGeneration { get; set; }
        public bool FailWrite { get; set; }
        public bool DeclineWrite { get; set; }
        public Dictionary<string, byte[]> Cache { get; } = [];
        public int GenerationCount { get; private set; }
        public int ResolutionCount { get; private set; }
        public IOriginalAssetLockLease? SharedLeaseUsed { get; private set; }

        public PreviewService CreateService(PreviewSettings settings) => new(this, this, this, settings);

        public Task<PreviewResolution> ResolveAsync(Guid captureId, CancellationToken cancellationToken)
        {
            ResolutionCount++;
            return Task.FromResult(Resolution);
        }
        public Task<byte[]?> ReadAsync(string key, PreviewSize size, CancellationToken cancellationToken) => Task.FromResult(Cache.GetValueOrDefault(key));
        public Task<bool> ContainsAsync(string key, PreviewSize size, CancellationToken cancellationToken) => Task.FromResult(Cache.ContainsKey(key));
        public Task<bool> WriteAsync(string key, byte[] content, CancellationToken cancellationToken)
        {
            if (FailWrite) throw new IOException("Synthetic cache outage.");
            if (DeclineWrite) return Task.FromResult(false);
            Cache[key] = content;
            return Task.FromResult(true);
        }
        public Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, CancellationToken cancellationToken)
        {
            GenerationCount++;
            if (FailGeneration) throw new HttpRequestException("Synthetic Imaginary outage.");
            OnGenerate?.Invoke();
            return Task.FromResult(new byte[] { 1, 2, 3 });
        }
        public Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, IOriginalAssetLockLease sharedLease,
            CancellationToken cancellationToken)
        {
            SharedLeaseUsed = sharedLease;
            return GenerateAsync(source, size, cancellationToken);
        }
    }

    private sealed class FakeOriginalAssetLock : IOriginalAssetLock
    {
        public int SharedAcquisitions { get; private set; }
        public bool Available { get; init; } = true;
        public FakeLockLease? LastLease { get; private set; }
        public Task<IOriginalAssetLockLease> AcquireSharedAsync(Guid assetId, string storageKey, CancellationToken cancellationToken)
        {
            SharedAcquisitions++;
            return Task.FromResult<IOriginalAssetLockLease>(LastLease = new FakeLockLease(assetId, storageKey));
        }
        public Task<IOriginalAssetLockLease> AcquireExclusiveAsync(Guid assetId, string storageKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(IOriginalAssetLockLease sharedLease, CancellationToken cancellationToken) => Task.FromResult(Available);
    }

    private sealed class FakeLockLease(Guid assetId, string storageKey) : IOriginalAssetLockLease
    {
        public Guid AssetId { get; } = assetId;
        public string StorageKey { get; } = storageKey;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class RecordingPreviewActivityPublisher : IPreviewActivityPublisher
    {
        public List<(Guid CaptureId, Guid AssetId, PreviewProfile Profile)> Started { get; } = [];
        public List<(Guid CaptureId, Guid AssetId, PreviewProfile Profile)> FinishedItems { get; } = [];

        public void Preparing(Guid captureId, Guid assetId, PreviewProfile profile, DateTimeOffset observedAt) =>
            Started.Add((captureId, assetId, profile));

        public void Finished(Guid captureId, Guid assetId, PreviewProfile profile) =>
            FinishedItems.Add((captureId, assetId, profile));
    }
}
