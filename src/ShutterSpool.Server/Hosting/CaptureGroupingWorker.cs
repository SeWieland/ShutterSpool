using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class CaptureGroupingWorker(IServiceScopeFactory scopes, ILogger<CaptureGroupingWorker> logger) : BackgroundService
{
    private readonly WorkspaceWorkerCursor tenants = new(scopes);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed = false;
            try
            {
                await using WorkspaceWorkerScope scope = await tenants.NextAsync(stoppingToken);
                LiveChangeNotifier changes = scope.ServiceProvider.GetRequiredService<LiveChangeNotifier>();
                CaptureGroupingService grouping = scope.ServiceProvider.GetRequiredService<CaptureGroupingService>();
                processed = await grouping.ProcessOneAsync(stoppingToken);
                if (grouping.LastChangedCaptureId is Guid captureId) changes.CaptureChanged(captureId, "capture");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning("Capture grouping pass failed; retrying.");
            }

            if (!processed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
