using ShutterSpool.Application.Sources;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

public sealed class SourceAuthenticationObservationWorker(IServiceScopeFactory scopes, LiveChangeNotifier changes,
    ILogger<SourceAuthenticationObservationWorker> logger) : BackgroundService, ISourceAuthenticationObservationRecorder
{
    private const int MaximumPendingSources = 256;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);
    private readonly Lock gate = new();
    private readonly Dictionary<Guid, SourceAuthenticationObservation> pending = [];
    private readonly SemaphoreSlim signal = new(0, 1);
    private bool coverageComplete = true;
    private DateTimeOffset lastUnassignedActivity = DateTimeOffset.MinValue;

    public bool CoverageComplete
    {
        get { lock (gate) return coverageComplete; }
    }

    public void Record(SourceAuthenticationObservation observation)
    {
        lock (gate)
        {
            if (pending.TryGetValue(observation.SourceId, out SourceAuthenticationObservation? current))
            {
                if (observation.ObservedAt >= current.ObservedAt) pending[observation.SourceId] = observation;
            }
            else if (pending.Count < MaximumPendingSources) pending.Add(observation.SourceId, observation);
            else coverageComplete = false;

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
            SourceAuthenticationObservation[] batch;
            lock (gate)
            {
                batch = [.. pending.Values];
                pending.Clear();
            }

            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            ISourceAuthenticationObservationStore store = scope.ServiceProvider.GetRequiredService<ISourceAuthenticationObservationStore>();
            foreach (SourceAuthenticationObservation observation in batch)
            {
                try
                {
                    if (await store.RecordAsync(observation, stoppingToken) is long version)
                        changes.SourceAuthenticationChanged(observation.SourceId, observation.Outcome.ToString(),
                            observation.ObservedAt, version);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    lock (gate) coverageComplete = false;
                    logger.LogDebug(exception, "Source authentication observation could not be saved; the saved snapshot remains authoritative.");
                }
            }
        }
    }
}
