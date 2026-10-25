using ShutterSpool.Application.Spool;

namespace ShutterSpool.Application.Metadata;

public sealed class MetadataProcessingService(
    IAssetMetadataStore store,
    ICommittedOriginalReader originals,
    IMediaMetadataParser parser,
    TimeProvider timeProvider,
    TimeSpan? leaseRenewalInterval = null)
{
    private readonly TimeSpan _leaseRenewalInterval = leaseRenewalInterval ?? TimeSpan.FromMinutes(1);

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        MetadataWork? work = await store.ClaimAsync(now, cancellationToken);
        if (work is null) return false;

        using var stopRenewal = new CancellationTokenSource();
        using var leaseLost = new CancellationTokenSource();
        using var parseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseLost.Token);
        Task renewal = RenewLeaseAsync(work, leaseLost, stopRenewal.Token);
        AssetMetadata? result = null;
        bool failed = false;
        try
        {
            try
            {
                await using Stream original = await originals.OpenReadAsync(work.SpoolKey, parseCancellation.Token);
                result = await parser.ParseAsync(original, work.OriginalName, work.Size, parseCancellation.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
            {
            }
            catch (Exception)
            {
                failed = true;
            }
        }
        finally
        {
            stopRenewal.Cancel();
            await renewal;
        }

        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        if (leaseLost.IsCancellationRequested) return true;
        if (failed) await store.RetryAsync(work, timeProvider.GetUtcNow(), cancellationToken);
        else await store.CompleteAsync(work, result!, cancellationToken);
        return true;
    }

    private async Task RenewLeaseAsync(MetadataWork work, CancellationTokenSource leaseLost, CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(_leaseRenewalInterval, timeProvider, stoppingToken);
                if (!await store.RenewAsync(work, timeProvider.GetUtcNow(), stoppingToken))
                {
                    leaseLost.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch
        {
            leaseLost.Cancel();
            throw;
        }
    }
}
