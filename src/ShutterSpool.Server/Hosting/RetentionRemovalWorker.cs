using ShutterSpool.Application.Retention;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class RetentionRemovalWorker(IServiceScopeFactory scopes, RetentionTelemetry telemetry, LiveChangeNotifier changes,
    ILogger<RetentionRemovalWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CapacityRefresh = TimeSpan.FromSeconds(45);
    private DateTimeOffset _lastCapacitySample = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAutomaticPass = DateTimeOffset.MinValue;

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
        await using AsyncServiceScope scope = scopes.CreateAsyncScope();
        RetentionRemovalService service = scope.ServiceProvider.GetRequiredService<RetentionRemovalService>();
        int erasuresExamined = await scope.ServiceProvider.GetRequiredService<PrivacyErasureService>()
            .ProcessPendingAsync(10, cancellationToken);
        _ = await service.PruneCompletedAttemptHistoryAsync(cancellationToken);
        _ = await service.RefreshRetentionFactsAsync(20, cancellationToken);
        RetentionRemovalPassResult result = await ProcessDueWorkAsync(service, cancellationToken);
        ReportProgress(result, erasuresExamined);
        await SampleCapacityAsync(scope.ServiceProvider, result, cancellationToken);
        if (result.Examined > 0 || DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromSeconds(1))
            telemetry.RecordPass(DateTimeOffset.UtcNow - startedAt, failed: false);
        await Task.Delay(result.Examined == 0 ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100), cancellationToken);
    }

    private async Task<RetentionRemovalPassResult> ProcessDueWorkAsync(RetentionRemovalService service,
        CancellationToken cancellationToken)
    {
        if (DateTimeOffset.UtcNow - _lastAutomaticPass < CapacityRefresh)
            return await service.ProcessPendingAsync(20, cancellationToken);
        RetentionRemovalPassResult result = await service.ProcessAutomaticTrashAsync(20, cancellationToken);
        _lastAutomaticPass = DateTimeOffset.UtcNow;
        return result;
    }

    private void ReportProgress(RetentionRemovalPassResult result, int erasuresExamined)
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

    private async Task SampleCapacityAsync(IServiceProvider services, RetentionRemovalPassResult result,
        CancellationToken cancellationToken)
    {
        if (result.Examined == 0 && DateTimeOffset.UtcNow - _lastCapacitySample < CapacityRefresh) return;
        ISpoolQuotaStore quota = services.GetRequiredService<ISpoolQuotaStore>();
        telemetry.RecordCapacity(await quota.GetSnapshotAsync(cancellationToken));
        _lastCapacitySample = DateTimeOffset.UtcNow;
    }
}
