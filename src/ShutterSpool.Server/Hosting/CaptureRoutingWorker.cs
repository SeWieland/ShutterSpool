using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Routing;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class CaptureRoutingWorker(IServiceScopeFactory scopes, IDeliveryWorkSignal signal, ILogger<CaptureRoutingWorker> logger) : BackgroundService
{
    private readonly WorkspaceWorkerCursor tenants = new(scopes);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                await using WorkspaceWorkerScope scope = await tenants.NextAsync(stoppingToken);
                LiveChangeNotifier changes = scope.ServiceProvider.GetRequiredService<LiveChangeNotifier>();
                CaptureRoutingService routing = scope.ServiceProvider.GetRequiredService<CaptureRoutingService>();
                processed = await routing.ProcessOneAsync(stoppingToken);
                if (routing.LastChangedCaptureId is Guid captureId) changes.CaptureChanged(captureId, "routing");
                if (processed) await signal.NotifyAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Capture routing pass failed.");
                processed = false;
            }
            if (!processed) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
