using ShutterSpool.Application.Preview;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed partial class PreviewPrewarmWorker(IServiceScopeFactory scopes, ILogger<PreviewPrewarmWorker> logger) : BackgroundService
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
                await using WorkspaceWorkerScope? scope = await tenants.NextAsync(stoppingToken);
                if (scope is not null)
                {
                    LiveChangeNotifier changes = scope.ServiceProvider.GetRequiredService<LiveChangeNotifier>();
                    PreviewPrewarmService previews = scope.ServiceProvider.GetRequiredService<PreviewPrewarmService>();
                    processed = await previews.ProcessOneAsync(stoppingToken);
                    if (previews.LastReadyPreview is { } ready)
                        changes.PreviewReady(ready.CaptureId, ready.AssetId, ready.Profile.ToString().ToLowerInvariant());
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogPreviewPrewarmingFailure(logger, exception.GetType().Name);
            }

            if (!processed) await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    [LoggerMessage(EventId = 6001, Level = LogLevel.Warning, Message = "Preview prewarming pass failed ({ExceptionType}). Retrying.")]
    private static partial void LogPreviewPrewarmingFailure(ILogger logger, string exceptionType);
}
