using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class CaptureGroupingWorker(IServiceScopeFactory scopes, LiveChangeNotifier changes,
    ILogger<CaptureGroupingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed = false;
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
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
