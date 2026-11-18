using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Server.Api.Live;

namespace ShutterSpool.Server.Hosting;

internal sealed partial class CaptureGroupingWorker(IServiceScopeFactory scopes, ILogger<CaptureGroupingWorker> logger) : BackgroundService
{
    private readonly WorkspaceWorkerCursor tenants = new(scopes);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
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
                    CaptureGroupingService grouping = scope.ServiceProvider.GetRequiredService<CaptureGroupingService>();
                    processed = await grouping.ProcessOneAsync(stoppingToken);
                    if (grouping.LastChangedCaptureId is Guid captureId) changes.CaptureChanged(captureId, "capture");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogCaptureGroupingFailure(logger, exception.GetType().Name);
            }

            if (!processed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "Capture grouping pass failed ({ExceptionType}). Retrying.")]
    private static partial void LogCaptureGroupingFailure(ILogger logger, string exceptionType);
}
