using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShutterSpool.Application.Sources;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Server.Hosting;

namespace ShutterSpool.IntegrationTests;

public sealed class SourceAuthenticationPostgresTests
{
    [Fact]
    public async Task Recorder_coalesces_per_source_and_persists_the_newest_observation()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Guid sourceId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Options))
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-recorder-source" });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new ShutterSpoolDbContext(fixture.Options));
        services.AddScoped<ISourceAuthenticationObservationStore>(provider =>
            new EfSourceAuthenticationObservationStore(provider.GetRequiredService<ShutterSpoolDbContext>()));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var changes = new LiveChangeNotifier(null!, NullLogger<LiveChangeNotifier>.Instance);
        var worker = new SourceAuthenticationObservationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            changes, NullLogger<SourceAuthenticationObservationWorker>.Instance);
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset observedAt = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        await worker.StartAsync(token);
        worker.Record(new(sourceId, SourceAuthenticationOutcome.Succeeded, observedAt));
        worker.Record(new(sourceId, SourceAuthenticationOutcome.Rejected, observedAt.AddSeconds(1)));

        SourceAuthenticationSnapshot? saved = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var database = new ShutterSpoolDbContext(fixture.Options);
            saved = (await new EfSourceAuthenticationObservationStore(database).ReadAsync([sourceId], token)).SingleOrDefault();
            if (saved is not null) break;
            await Task.Delay(50, token);
        }
        await worker.StopAsync(token);

        Assert.NotNull(saved);
        Assert.Equal(SourceAuthenticationOutcome.Rejected, saved.Outcome);
        Assert.Equal(observedAt.AddSeconds(1), saved.ObservedAt);
        Assert.Equal(1, saved.Version);
        Assert.True(worker.CoverageComplete);
    }

    [Fact]
    public async Task Recorder_keeps_latest_same_time_arrival_and_ignores_an_older_late_arrival()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Guid sourceId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Options))
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-recorder-order-source" });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var services = new ServiceCollection();
        services.AddScoped(_ => new ShutterSpoolDbContext(fixture.Options));
        services.AddScoped<ISourceAuthenticationObservationStore>(provider =>
            new EfSourceAuthenticationObservationStore(provider.GetRequiredService<ShutterSpoolDbContext>()));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var changes = new LiveChangeNotifier(null!, NullLogger<LiveChangeNotifier>.Instance);
        var worker = new SourceAuthenticationObservationWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            changes, NullLogger<SourceAuthenticationObservationWorker>.Instance);
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset observedAt = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        await worker.StartAsync(token);
        worker.Record(new(sourceId, SourceAuthenticationOutcome.Succeeded, observedAt));
        worker.Record(new(sourceId, SourceAuthenticationOutcome.Rejected, observedAt));
        worker.Record(new(sourceId, SourceAuthenticationOutcome.Succeeded, observedAt.AddSeconds(-1)));

        SourceAuthenticationSnapshot? saved = null;
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var database = new ShutterSpoolDbContext(fixture.Options);
            saved = (await new EfSourceAuthenticationObservationStore(database).ReadAsync([sourceId], token)).SingleOrDefault();
            if (saved is not null) break;
            await Task.Delay(50, token);
        }
        await worker.StopAsync(token);

        Assert.NotNull(saved);
        Assert.Equal(SourceAuthenticationOutcome.Rejected, saved.Outcome);
        Assert.Equal(observedAt, saved.ObservedAt);
        Assert.Equal(1, saved.Version);
    }

    [Fact]
    public void Recorder_marks_coverage_incomplete_when_distinct_source_backlog_is_full()
    {
        var changes = new LiveChangeNotifier(null!, NullLogger<LiveChangeNotifier>.Instance);
        var worker = new SourceAuthenticationObservationWorker(null!, changes,
            NullLogger<SourceAuthenticationObservationWorker>.Instance);
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;

        for (int index = 0; index < 257; index++)
            worker.Record(new(Guid.NewGuid(), SourceAuthenticationOutcome.Succeeded, observedAt));

        Assert.False(worker.CoverageComplete);
    }

    [Fact]
    public async Task Snapshot_upsert_uses_monotonic_versions_across_clock_reversal_and_follows_source_deletion()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Guid sourceId = Guid.NewGuid();
        DateTimeOffset observedAt = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        await using (var database = new ShutterSpoolDbContext(fixture.Options))
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-auth-source" });
            database.SourceCredentials.Add(new SourceCredentialEntity
            {
                SourceId = sourceId, Username = "synthetic-auth-camera", VirtualRoot = "sources/synthetic-auth",
                SecretVerifier = new byte[32],
            });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        long firstVersion;
        await using (var first = new ShutterSpoolDbContext(fixture.Options))
            firstVersion = (await new EfSourceAuthenticationObservationStore(first).RecordAsync(
                new(sourceId, SourceAuthenticationOutcome.Succeeded, observedAt), TestContext.Current.CancellationToken))!.Value;
        long secondVersion;
        await using (var second = new ShutterSpoolDbContext(fixture.Options))
            secondVersion = (await new EfSourceAuthenticationObservationStore(second).RecordAsync(
                new(sourceId, SourceAuthenticationOutcome.Rejected, observedAt), TestContext.Current.CancellationToken))!.Value;

        Assert.Equal(1, firstVersion);
        Assert.Equal(firstVersion + 1, secondVersion);
        await using (var read = new ShutterSpoolDbContext(fixture.Options))
        {
            var store = new EfSourceAuthenticationObservationStore(read);
            long? reversedClockVersion = await store.RecordAsync(new(sourceId, SourceAuthenticationOutcome.Succeeded,
                observedAt.AddSeconds(-1)), TestContext.Current.CancellationToken);
            SourceAuthenticationSnapshot saved = Assert.Single(await store.ReadAsync([sourceId], TestContext.Current.CancellationToken));
            Assert.Null(reversedClockVersion);
            Assert.Equal(SourceAuthenticationOutcome.Rejected, saved.Outcome);
            Assert.Equal(secondVersion, saved.Version);
            Assert.Equal(observedAt, saved.ObservedAt);
        }

        await using (var remove = new ShutterSpoolDbContext(fixture.Options))
            Assert.True((await new EfSourceConfigurationStore(remove).RemoveAsync(sourceId, 0,
                TestContext.Current.CancellationToken)).Removed);
        await using var finalRead = new ShutterSpoolDbContext(fixture.Options);
        Assert.Empty(await new EfSourceAuthenticationObservationStore(finalRead).ReadAsync([sourceId],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Snapshot_upsert_rejects_an_older_observation_within_the_same_postgres_microsecond()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Guid sourceId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Options))
        {
            await database.Database.MigrateAsync(TestContext.Current.CancellationToken);
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-submicro-auth-source" });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        long microsecondTicks = DateTimeOffset.UtcNow.UtcTicks / 10 * 10;
        DateTimeOffset newer = new(microsecondTicks + 9, TimeSpan.Zero);
        DateTimeOffset older = new(microsecondTicks + 1, TimeSpan.Zero);
        CancellationToken token = TestContext.Current.CancellationToken;

        await using (var first = new ShutterSpoolDbContext(fixture.Options))
            Assert.Equal(1, await new EfSourceAuthenticationObservationStore(first).RecordAsync(
                new(sourceId, SourceAuthenticationOutcome.Rejected, newer), token));
        await using (var second = new ShutterSpoolDbContext(fixture.Options))
            Assert.Null(await new EfSourceAuthenticationObservationStore(second).RecordAsync(
                new(sourceId, SourceAuthenticationOutcome.Succeeded, older), token));

        await using var read = new ShutterSpoolDbContext(fixture.Options);
        SourceAuthenticationSnapshot saved = Assert.Single(await new EfSourceAuthenticationObservationStore(read)
            .ReadAsync([sourceId], token));
        Assert.Equal(SourceAuthenticationOutcome.Rejected, saved.Outcome);
        Assert.Equal(new DateTimeOffset(microsecondTicks, TimeSpan.Zero), saved.ObservedAt);
        Assert.Equal(1, saved.Version);
    }
}
