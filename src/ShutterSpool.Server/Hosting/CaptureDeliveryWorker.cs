using ShutterSpool.Application.Delivery;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class CaptureDeliveryWorker(IServiceScopeFactory scopes, DeliveryWorkerSettings settings,
    IDeliveryWorkSignal signal, LiveChangeNotifier changes, ILogger<CaptureDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed;
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
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
