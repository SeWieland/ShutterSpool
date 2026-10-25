using ShutterSpool.Application.Preview;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed class PreviewPrewarmWorker(IServiceScopeFactory scopes, LiveChangeNotifier changes,
    ILogger<PreviewPrewarmWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool processed = false;
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
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
