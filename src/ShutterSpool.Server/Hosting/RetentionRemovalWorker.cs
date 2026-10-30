using ShutterSpool.Application.Retention;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class RetentionRemovalWorker(IServiceScopeFactory scopes, RetentionTelemetry telemetry, ILogger<RetentionRemovalWorker> logger) : BackgroundService
{
    private readonly WorkspaceWorkerCursor tenants = new(scopes);

    private static readonly TimeSpan CapacityRefresh = TimeSpan.FromSeconds(45);
    private readonly Dictionary<Guid, DateTimeOffset> capacitySamples = [];
    private readonly Dictionary<Guid, DateTimeOffset> automaticPasses = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            try
            {
                await RunPassAsync(startedAt, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                telemetry.RecordOperation(RetentionOperation.Failure, 0, 1);
                telemetry.RecordPass(DateTimeOffset.UtcNow - startedAt, failed: true);
                logger.LogWarning("Retention removal pass failed; authorized cleanup will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunPassAsync(DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        await using WorkspaceWorkerScope scope = await tenants.NextAsync(cancellationToken);
        LiveChangeNotifier changes = scope.ServiceProvider.GetRequiredService<LiveChangeNotifier>();
        RetentionRemovalService service = scope.ServiceProvider.GetRequiredService<RetentionRemovalService>();
        int erasuresExamined = await scope.ServiceProvider.GetRequiredService<PrivacyErasureService>()
            .ProcessPendingAsync(10, cancellationToken);
        _ = await service.PruneCompletedAttemptHistoryAsync(cancellationToken);
        _ = await service.RefreshRetentionFactsAsync(20, cancellationToken);
        Guid tenantId = scope.ServiceProvider.GetRequiredService<ShutterSpool.Application.Workspaces.WorkspaceContext>().Id ?? Guid.Empty;
        RetentionRemovalPassResult result = await ProcessDueWorkAsync(service, tenantId, cancellationToken);
        ReportProgress(result, erasuresExamined, changes);
        await SampleCapacityAsync(scope.ServiceProvider, result, tenantId, cancellationToken);
        if (result.Examined > 0 || DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromSeconds(1))
            telemetry.RecordPass(DateTimeOffset.UtcNow - startedAt, failed: false);
        await Task.Delay(result.Examined == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100), cancellationToken);
    }

    private async Task<RetentionRemovalPassResult> ProcessDueWorkAsync(RetentionRemovalService service, Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - automaticPasses.GetValueOrDefault(tenantId) < CapacityRefresh)
            return await service.ProcessPendingAsync(20, cancellationToken);
        RetentionRemovalPassResult result = await service.ProcessAutomaticTrashAsync(20, cancellationToken);
        automaticPasses[tenantId] = DateTimeOffset.UtcNow;
        return result;
    }

    private void ReportProgress(RetentionRemovalPassResult result, int erasuresExamined, LiveChangeNotifier changes)
    {
        if (result.Completed > 0)
            telemetry.RecordOperation(RetentionOperation.Purge, result.RemovedBytes, result.Completed);
        if (result.Selected > 0)
            telemetry.RecordOperation(RetentionOperation.Selection, result.SelectedBytes, result.Selected);
        if (result.Failed > 0)
            telemetry.RecordOperation(RetentionOperation.Failure, 0, result.Failed);
        if (result.Examined > result.Completed + result.Failed)
            telemetry.RecordOperation(RetentionOperation.Retry, 0, result.Examined - result.Completed - result.Failed);
        if (result.Selected > 0 || result.Completed > 0 || erasuresExamined > 0)
            changes.RetentionChanged();
    }

    private async Task SampleCapacityAsync(IServiceProvider services, RetentionRemovalPassResult result, Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (result.Examined == 0 && DateTimeOffset.UtcNow - capacitySamples.GetValueOrDefault(tenantId) < CapacityRefresh) return;
        ISpoolQuotaStore quota = services.GetRequiredService<ISpoolQuotaStore>();
        telemetry.RecordCapacity(await quota.GetSnapshotAsync(cancellationToken));
        capacitySamples[tenantId] = DateTimeOffset.UtcNow;
    }
}
