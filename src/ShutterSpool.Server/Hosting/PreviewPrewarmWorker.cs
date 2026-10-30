using ShutterSpool.Application.Preview;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class PreviewPrewarmWorker(IServiceScopeFactory scopes, ILogger<PreviewPrewarmWorker> logger) : BackgroundService
{
    private readonly WorkspaceWorkerCursor tenants = new(scopes);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(RunAsync(stoppingToken), RunAsync(stoppingToken));

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed = false;
            try
            {
                await using WorkspaceWorkerScope scope = await tenants.NextAsync(stoppingToken);
                LiveChangeNotifier changes = scope.ServiceProvider.GetRequiredService<LiveChangeNotifier>();
                PreviewPrewarmService previews = scope.ServiceProvider.GetRequiredService<PreviewPrewarmService>();
                processed = await previews.ProcessOneAsync(stoppingToken);
                if (previews.LastReadyPreview is { } ready)
                    changes.PreviewReady(ready.CaptureId, ready.AssetId, ready.Profile.ToString().ToLowerInvariant());
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning("Preview prewarming pass failed; retrying.");
            }

            if (!processed) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
