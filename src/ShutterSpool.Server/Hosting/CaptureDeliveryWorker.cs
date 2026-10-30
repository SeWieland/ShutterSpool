using ShutterSpool.Application.Delivery;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class CaptureDeliveryWorker(IServiceScopeFactory scopes, DeliveryWorkerSettings settings,
    IDeliveryWorkSignal signal, ILogger<CaptureDeliveryWorker> logger) : BackgroundService
{
    private readonly WorkspaceWorkerCursor tenants = new(scopes);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(RunAsync(stoppingToken), RunAsync(stoppingToken));

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                await using WorkspaceWorkerScope scope = await tenants.NextAsync(stoppingToken);
                LiveChangeNotifier changes = scope.ServiceProvider.GetRequiredService<LiveChangeNotifier>();
                DeliveryProcessingService delivery = scope.ServiceProvider.GetRequiredService<DeliveryProcessingService>();
                processed = await delivery.ProcessOneAsync(stoppingToken,
                    captureId => changes.CaptureChanged(captureId, "delivery"));
                if (delivery.LastChangedCaptureId is Guid captureId) changes.CaptureChanged(captureId, "delivery");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Delivery worker pass failed ({ExceptionType}).", exception.GetType().Name);
                processed = false;
            }
            if (!processed) await signal.WaitAsync(TimeSpan.FromSeconds(settings.ScanIntervalSeconds), stoppingToken);
        }
    }
}
