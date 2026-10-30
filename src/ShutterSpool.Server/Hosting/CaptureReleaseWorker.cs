using ShutterSpool.Application.Curation;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class CaptureReleaseWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<CaptureReleaseWorker> logger) : BackgroundService
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
                CaptureCurationService curation = scope.ServiceProvider.GetRequiredService<CaptureCurationService>();
                processed = await curation.ReleaseDueOneAsync(clock.GetUtcNow(), stoppingToken);
                if (curation.LastReleasedCaptureId is Guid captureId) changes.CaptureChanged(captureId, "release");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning("Capture release pass failed; retrying.");
            }

            if (!processed) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
