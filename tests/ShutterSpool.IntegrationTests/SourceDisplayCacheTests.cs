using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Server.Hosting;

namespace ShutterSpool.IntegrationTests;

public sealed class SourceDisplayCacheTests
{
    [Fact]
    public async Task Warm_display_read_avoids_repeated_projection_queries()
    {
        var store = new DisplayStore();
        using var services = CreateServices();
        var cache = CreateCache(store, services, new ManualClock());
        Assert.Equal(store.Value, await cache.ReadAsync(store.Value.Id, TestContext.Current.CancellationToken));
        Assert.Equal(store.Value, await cache.ReadAsync(store.Value.Id, TestContext.Current.CancellationToken));
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Invalidation_exposes_committed_revision_without_waiting_for_expiry()
    {
        var store = new DisplayStore();
        using var services = CreateServices();
        var cache = CreateCache(store, services, new ManualClock());
        await cache.ReadAsync(store.Value.Id, TestContext.Current.CancellationToken);
        store.Value = store.Value with { Name = "Renamed", ConfigurationRevision = 2 };
        await cache.InvalidateAsync(store.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(store.Value, await cache.ReadAsync(store.Value.Id, TestContext.Current.CancellationToken));
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public async Task Lost_invalidation_and_slow_fill_cannot_extend_the_thirty_second_bound()
    {
        var store = new DisplayStore();
        var clock = new ManualClock();
        using var services = CreateServices();
        var cache = CreateCache(store, services, clock);
        await cache.ReadAsync(store.Value.Id, TestContext.Current.CancellationToken);
        store.Value = store.Value with { Name = "Committed replacement", ConfigurationRevision = 2 };
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(store.Value, await cache.ReadAsync(store.Value.Id, TestContext.Current.CancellationToken));

        await cache.InvalidateAsync(store.Value.Id, TestContext.Current.CancellationToken);
        store.OnRead = () => clock.Advance(TimeSpan.FromSeconds(30));
        await cache.ReadAsync(store.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(4, store.Reads);
    }

    [Fact]
    public async Task Arbitrary_identities_and_large_values_bypass_caching_at_the_bound()
    {
        var store = new DisplayStore();
        using var services = CreateServices();
        var cache = CreateCache(store, services, new ManualClock());
        for (int index = 0; index < 256; index++)
            await cache.ReadAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);
        Guid beyondCapacity = Guid.NewGuid();
        await cache.ReadAsync(beyondCapacity, TestContext.Current.CancellationToken);
        await cache.ReadAsync(beyondCapacity, TestContext.Current.CancellationToken);
        Assert.Equal(258, store.Reads);

        var oversizedStore = new DisplayStore { Value = store.Value with { Name = new string('x', 4_000) } };
        using var separateServices = CreateServices();
        var oversizedCache = CreateCache(oversizedStore, separateServices, new ManualClock());
        await oversizedCache.ReadAsync(oversizedStore.Value.Id, TestContext.Current.CancellationToken);
        await oversizedCache.ReadAsync(oversizedStore.Value.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, oversizedStore.Reads);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider();
    }

    private static SourceDisplayCache CreateCache(DisplayStore store, ServiceProvider services, TimeProvider clock) =>
        new(store, services.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(), new(), clock);

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan elapsed) => ticks += elapsed.Ticks;
    }

    private sealed class DisplayStore : ISourceDisplayStore
    {
        public SourceDisplay Value { get; set; } = new(Guid.NewGuid(), "Synthetic Source", SourceCurationMode.Manual, 0, 1, false,
            SourceType.CameraFtpUpload, SourceConnectionProfile.ModernFtps);
        public int Reads { get; private set; }
        public Action? OnRead { get; set; }
        public Task<SourceDisplay?> ReadAsync(Guid id, CancellationToken cancellationToken)
        {
            Reads++;
            OnRead?.Invoke();
            return Task.FromResult<SourceDisplay?>(Value);
        }
    }
}
