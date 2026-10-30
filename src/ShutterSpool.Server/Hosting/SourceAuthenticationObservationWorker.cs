using ShutterSpool.Application.Sources;
using ShutterSpool.Server.Api.Live;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Server.Hosting;

public sealed class SourceAuthenticationObservationWorker(IServiceScopeFactory scopes, LiveChangeNotifier changes,
    ILogger<SourceAuthenticationObservationWorker> logger) : BackgroundService, ISourceAuthenticationObservationRecorder
{
    private const int MaximumPendingSources = 256;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);
    private readonly Lock gate = new();
    private readonly Dictionary<(Guid Workspace, Guid Source), PendingObservation> pending = [];
    private readonly SemaphoreSlim signal = new(0, 1);
    private readonly HashSet<Guid> incomplete = [];
    private DateTimeOffset lastUnassignedActivity = DateTimeOffset.MinValue;

    public bool CoverageComplete
    {
        get { return IsCoverageComplete(Guid.Empty); }
    }
    public bool IsCoverageComplete(Guid workspace) { lock (gate) return !incomplete.Contains(workspace); }

    public void Record(SourceAuthenticationObservation observation) => Record(observation, null);

    public void Record(SourceAuthenticationObservation observation, WorkspaceDefinition? workspace)
    {
        var identity = (workspace?.Id ?? Guid.Empty, observation.SourceId);
        lock (gate)
        {
            if (pending.TryGetValue(identity, out PendingObservation? current))
            {
                if (observation.ObservedAt >= current.Observation.ObservedAt) pending[identity] = new(workspace, observation);
            }
            else if (pending.Count(pair => pair.Key.Workspace == identity.Item1) < MaximumPendingSources)
                pending.Add(identity, new(workspace, observation));
            else incomplete.Add(identity.Item1);

            if (signal.CurrentCount == 0) signal.Release();
        }
    }

    public void RecordUnassigned(DateTimeOffset observedAt)
    {
        bool publish;
        lock (gate)
        {
            publish = observedAt - lastUnassignedActivity >= CoalesceWindow;
            if (publish) lastUnassignedActivity = observedAt;
        }
        if (publish) changes.UnassignedAuthenticationActivity(observedAt);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await signal.WaitAsync(stoppingToken);
            await Task.Delay(CoalesceWindow, stoppingToken);
            PendingObservation[] batch;
            lock (gate)
            {
                batch = [.. pending.Values];
                pending.Clear();
            }

            foreach (PendingObservation pendingObservation in batch)
            {
                try
                {
                    await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                    if (pendingObservation.Workspace is not null)
                        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(pendingObservation.Workspace);
                    SourceAuthenticationObservation observation = pendingObservation.Observation;
                    ISourceAuthenticationObservationStore store = scope.ServiceProvider.GetRequiredService<ISourceAuthenticationObservationStore>();
                    LiveChangeNotifier tenantChanges = scope.ServiceProvider.GetService<LiveChangeNotifier>() ?? changes;
                    if (await store.RecordAsync(observation, stoppingToken) is long version)
                        tenantChanges.SourceAuthenticationChanged(observation.SourceId, observation.Outcome.ToString(),
                            observation.ObservedAt, version);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    lock (gate) incomplete.Add(pendingObservation.Workspace?.Id ?? Guid.Empty);
                    logger.LogDebug(exception, "Source authentication observation could not be saved; the saved snapshot remains authoritative.");
                }
            }
        }
    }
    private sealed record PendingObservation(WorkspaceDefinition? Workspace, SourceAuthenticationObservation Observation);
}

internal sealed class ScopedSourceAuthenticationRecorder(SourceAuthenticationObservationWorker worker,
    WorkspaceContext workspace, LiveChangeNotifier changes) : ISourceAuthenticationObservationRecorder
{
    public void Record(SourceAuthenticationObservation observation) => worker.Record(observation, workspace.Selected);
    public void RecordUnassigned(DateTimeOffset observedAt) => changes.UnassignedAuthenticationActivity(observedAt);
}
