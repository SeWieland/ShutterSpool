using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Sources;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Server.Hosting;

namespace ShutterSpool.IntegrationTests;

public sealed class WorkspaceWorkerAndLiveTests
{
    [Fact]
    public async Task Shared_worker_cursor_rotates_and_skips_an_active_slow_tenant_with_bounded_scopes()
    {
        var registry = new Registry();
        await using ServiceProvider services = new ServiceCollection().AddScoped(_ => new WorkspaceContext(true))
            .AddSingleton<IWorkspaceRegistry>(registry).BuildServiceProvider();
        var cursor = new WorkspaceWorkerCursor(services.GetRequiredService<IServiceScopeFactory>());
        var seen = new List<Guid>();
        for (int pass = 0; pass < 10; pass++)
        {
            await using WorkspaceWorkerScope scope = await cursor.NextAsync(TestContext.Current.CancellationToken);
            seen.Add(scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Required.Id);
        }
        Assert.Equal(Enumerable.Range(0, 10).Select(index => registry.Workspaces[index % 2].Id), seen);
        await using WorkspaceWorkerScope slow = await cursor.NextAsync(TestContext.Current.CancellationToken);
        Guid slowId = slow.ServiceProvider.GetRequiredService<WorkspaceContext>().Required.Id;
        await using (WorkspaceWorkerScope fast = await cursor.NextAsync(TestContext.Current.CancellationToken))
        {
            Assert.NotEqual(slowId, fast.ServiceProvider.GetRequiredService<WorkspaceContext>().Required.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => cursor.NextAsync(TestContext.Current.CancellationToken));
        }
        await using WorkspaceWorkerScope nextFast = await cursor.NextAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(slowId, nextFast.ServiceProvider.GetRequiredService<WorkspaceContext>().Required.Id);
    }

    [Fact]
    public void Identical_transfer_and_preview_ids_have_separate_live_observations_and_versions()
    {
        var buffer = new LiveChangeBuffer(null!, NullLogger<LiveChangeNotifier>.Instance);
        var registry = new Registry();
        WorkspaceContext first = Context(registry.Workspaces[0]), second = Context(registry.Workspaces[1]);
        var a = new LiveChangeNotifier(buffer, first); var b = new LiveChangeNotifier(buffer, second);
        Guid common = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        a.Publish(new(common, common, null, null, CameraTransferPhase.Receiving, now, 0, false, false));
        a.Preparing(common, common, PreviewProfile.Grid, now);
        Assert.Null(b.GetRecentReceiving(common, now));
        Assert.Null(b.GetRecentPreviewPreparation(common, common, PreviewProfile.Grid, now));
        b.Publish(new(common, common, null, null, CameraTransferPhase.Receiving, now, 0, false, false));
        Assert.Equal(1, b.GetRecentReceiving(common, now)!.Version);
        Assert.Equal(1, a.GetRecentReceiving(common, now)!.Version);
        a.Finished(common, common, PreviewProfile.Grid);
        b.Preparing(common, common, PreviewProfile.Grid, now);
        Assert.Null(a.GetRecentPreviewPreparation(common, common, PreviewProfile.Grid, now));
        Assert.NotNull(b.GetRecentPreviewPreparation(common, common, PreviewProfile.Grid, now));
    }

    [Fact]
    public void Authentication_observation_overflow_does_not_change_another_workspaces_completeness()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var changes = new LiveChangeNotifier(null!, NullLogger<LiveChangeNotifier>.Instance);
        var worker = new SourceAuthenticationObservationWorker(services.GetRequiredService<IServiceScopeFactory>(), changes,
            NullLogger<SourceAuthenticationObservationWorker>.Instance);
        var registry = new Registry();
        for (int index = 0; index < 257; index++)
            worker.Record(new(Guid.NewGuid(), SourceAuthenticationOutcome.Succeeded, DateTimeOffset.UtcNow), registry.Workspaces[0]);
        worker.Record(new(Guid.NewGuid(), SourceAuthenticationOutcome.Succeeded, DateTimeOffset.UtcNow), registry.Workspaces[1]);
        Assert.False(worker.IsCoverageComplete(registry.Workspaces[0].Id));
        Assert.True(worker.IsCoverageComplete(registry.Workspaces[1].Id));
    }

    [Fact]
    public void Flooding_one_workspaces_live_preparations_cannot_evict_anothers_observation()
    {
        var buffer = new LiveChangeBuffer(null!, NullLogger<LiveChangeNotifier>.Instance);
        var registry = new Registry();
        var a = new LiveChangeNotifier(buffer, Context(registry.Workspaces[0]));
        var b = new LiveChangeNotifier(buffer, Context(registry.Workspaces[1]));
        Guid retained = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        b.Preparing(retained, retained, PreviewProfile.Grid, now);
        for (int index = 0; index < 300; index++)
        {
            Guid other = Guid.NewGuid();
            a.Preparing(other, other, PreviewProfile.Grid, now.AddTicks(index + 1));
        }
        Assert.NotNull(b.GetRecentPreviewPreparation(retained, retained, PreviewProfile.Grid, now.AddSeconds(1)));
        Assert.True(b.ActivityCoverageComplete);
        Assert.False(a.ActivityCoverageComplete);
    }

    private static WorkspaceContext Context(WorkspaceDefinition workspace) { var context = new WorkspaceContext(true); context.Bind(workspace); return context; }
    private sealed class Registry : IWorkspaceRegistry
    {
        public WorkspaceDefinition[] Workspaces { get; } = [new(Guid.NewGuid(), "A", "a", "a", 100, "a"), new(Guid.NewGuid(), "B", "b", "b", 100, "b")];
        public Task<WorkspaceDefinition[]> ListReadyAsync(CancellationToken token) => Task.FromResult(Workspaces);
        public Task<WorkspaceDefinition?> FindReadyAsync(Guid id, CancellationToken token) => Task.FromResult(Workspaces.SingleOrDefault(item => item.Id == id));
        public Task<WorkspaceIngressRoute?> FindIngressAsync(string handle, CancellationToken token) => Task.FromResult<WorkspaceIngressRoute?>(null);
        public Task RegisterIngressAsync(string handle, Guid source, CancellationToken token) => Task.CompletedTask;
    }
}
