using ShutterSpool.Application.Spool;
using ShutterSpool.Domain.Delivery;
using System.Security.Cryptography;

namespace ShutterSpool.Application.Delivery;

public sealed class DeliveryAdapterRegistry
{
    private readonly Dictionary<string, IDestinationDeliveryAdapter> _adapters;

    public DeliveryAdapterRegistry(IEnumerable<IDestinationDeliveryAdapter> adapters)
    {
        _adapters = new(StringComparer.OrdinalIgnoreCase);
        foreach (IDestinationDeliveryAdapter adapter in adapters)
        {
            if (string.IsNullOrWhiteSpace(adapter.Kind) || !_adapters.TryAdd(adapter.Kind, adapter))
                throw new InvalidOperationException($"Delivery adapter kind is blank or registered more than once: {adapter.Kind}");
        }
    }

    public IDestinationDeliveryAdapter? ForKind(string kind) => _adapters.GetValueOrDefault(kind);
}

public sealed class DeliveryProcessingService(IDeliveryWorkStore workStore, ICommittedOriginalReader originals,
    DeliveryAdapterRegistry adapters, DeliveryWorkerSettings settings, TimeProvider clock)
{
    public Guid? LastChangedCaptureId { get; private set; }

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken, Action<Guid>? afterClaim = null)
    {
        LastChangedCaptureId = null;
        ClaimedDelivery? work = await workStore.ClaimAsync(cancellationToken);
        if (work is null) return false;
        NotifyClaimed(afterClaim, work.Request.CaptureId);
        IDestinationDeliveryAdapter? adapter = adapters.ForKind(work.Request.DestinationKind);
        if (adapter is null)
        {
            if (work.Reconcile)
                await workStore.FinishReconciliationAsync(work,
                    new(DeliveryReconciliationKind.Uncertain, ErrorCode: "DESTINATION_KIND_UNSUPPORTED"), cancellationToken);
            else
                await workStore.FinishAsync(work,
                    new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "DESTINATION_KIND_UNSUPPORTED"), cancellationToken);
            LastChangedCaptureId = work.Request.CaptureId;
            return true;
        }
        if (work.Reconcile) await ReconcileAsync(work, adapter, cancellationToken);
        else await WriteAsync(work, adapter, cancellationToken);
        LastChangedCaptureId = work.Request.CaptureId;
        return true;
    }

    private static void NotifyClaimed(Action<Guid>? afterClaim, Guid captureId)
    {
        if (afterClaim is null || captureId == Guid.Empty) return;
        try { afterClaim(captureId); }
        catch (Exception) { /* A live hint must never affect durable delivery work. */ }
    }

    private async Task ReconcileAsync(ClaimedDelivery work, IDestinationDeliveryAdapter adapter, CancellationToken cancellationToken)
    {
        DeliveryReconciliation outcome;
        try
        {
            outcome = await workStore.TargetReservedAsync(work.Request, cancellationToken)
                ? new(DeliveryReconciliationKind.Conflict, ErrorCode: "TARGET_RESERVED")
                : await adapter.ReconcileAsync(work.Request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            outcome = new(DeliveryReconciliationKind.Uncertain, ErrorCode: "RECONCILIATION_FAILED");
        }
        await workStore.FinishReconciliationAsync(work, outcome, cancellationToken);
    }

    private async Task WriteAsync(ClaimedDelivery work, IDestinationDeliveryAdapter adapter, CancellationToken cancellationToken)
    {
        Stream content;
        try
        {
            content = await OpenVerifiedOriginalAsync(work.Asset, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            await workStore.FinishAsync(work, new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "ORIGINAL_UNAVAILABLE"), cancellationToken);
            return;
        }
        await using (content)
        {
            DeliveryResult result = await workStore.TargetReservedAsync(work.Request, cancellationToken)
                ? new(DeliveryResultKind.ConfigurationFailure, ErrorCode: "TARGET_RESERVED")
                : await WriteWithLeaseAsync(work, adapter, content, cancellationToken);
            await workStore.FinishAsync(work, result, cancellationToken);
        }
    }

    private async Task<DeliveryResult> WriteWithLeaseAsync(ClaimedDelivery work, IDestinationDeliveryAdapter adapter,
        Stream content, CancellationToken cancellationToken)
    {
        using var ownership = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task renewal = RenewUntilLostAsync(work, ownership);
        DeliveryResult result;
        try { result = await adapter.WriteAsync(work.Request, content, ownership.Token); }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            result = new(DeliveryResultKind.Uncertain, ErrorCode: "OWNERSHIP_LOST");
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            result = new(DeliveryResultKind.Uncertain, ErrorCode: "WRITE_OUTCOME_UNKNOWN");
        }
        finally
        {
            ownership.Cancel();
            try { await renewal; } catch (OperationCanceledException) { }
        }
        return result;
    }

    private async Task RenewUntilLostAsync(ClaimedDelivery work, CancellationTokenSource ownership)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.LeaseRenewalSeconds), clock, ownership.Token);
                if (!await workStore.RenewAsync(work.DeliveryId, work.ClaimToken, ownership.Token))
                {
                    ownership.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ownership.IsCancellationRequested) { }
        catch (Exception) { ownership.Cancel(); }
    }

    private async Task<Stream> OpenVerifiedOriginalAsync(DeliveryAsset asset, CancellationToken cancellationToken)
    {
        if (asset.State != "Spooled") throw new IOException("Original is not available for delivery.");
        Stream source = await originals.OpenReadAsync(asset.SpoolKey, cancellationToken);
        try
        {
            await VerifyOriginalAsync(source, asset, cancellationToken);
            if (source.CanSeek)
            {
                source.Position = 0;
                return source;
            }
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
        await source.DisposeAsync();
        return await originals.OpenReadAsync(asset.SpoolKey, cancellationToken);
    }

    private static async Task VerifyOriginalAsync(Stream source, DeliveryAsset asset, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long bytes = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
        {
            hash.AppendData(buffer, 0, read);
            bytes += read;
        }
        byte[] actual = hash.GetHashAndReset();
        if (bytes != asset.Size || !CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(asset.Sha256)))
            throw new IOException("Original failed its stored size or hash check.");
    }
}
