using ShutterSpool.Application.Metadata;

namespace ShutterSpool.Server.Hosting;

internal sealed class MetadataWorker(IServiceScopeFactory scopes, ILogger<MetadataWorker> logger) : BackgroundService
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
                processed = await scope.ServiceProvider.GetRequiredService<MetadataProcessingService>().ProcessOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                logger.LogWarning("Metadata processing pass failed; retrying.");
            }

            if (!processed) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
