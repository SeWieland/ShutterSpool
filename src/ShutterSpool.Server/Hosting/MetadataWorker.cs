using ShutterSpool.Application.Metadata;

namespace ShutterSpool.Server.Hosting;

internal sealed partial class MetadataWorker(IServiceScopeFactory scopes, ILogger<MetadataWorker> logger) : BackgroundService
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
                    processed = await scope.ServiceProvider.GetRequiredService<MetadataProcessingService>().ProcessOneAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogMetadataProcessingFailure(logger, exception.GetType().Name);
            }

            if (!processed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    [LoggerMessage(EventId = 4003, Level = LogLevel.Warning, Message = "Metadata processing pass failed ({ExceptionType}). Retrying.")]
    private static partial void LogMetadataProcessingFailure(ILogger logger, string exceptionType);
}
