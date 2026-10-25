using ShutterSpool.Domain.Delivery;

namespace ShutterSpool.Application.Delivery;

public sealed record DeliveryRequest(Guid DeliveryId, Guid AttemptId, Guid AssetId, Guid DestinationId, string TargetPath,
    long Size, string Sha256, string TraceId, string DestinationKind = "local", DestinationDeliveryConfiguration? Configuration = null,
    Guid CaptureId = default);

public abstract record DestinationDeliveryConfiguration;
public sealed record LocalDestinationDeliveryConfiguration(string? RootPath) : DestinationDeliveryConfiguration;
public sealed record WebDavDestinationDeliveryConfiguration(string? CollectionUrl, string? Username, string? Password) : DestinationDeliveryConfiguration;

// The worker's provider boundary: adapters supply write and recovery evidence, not durable job state.
public interface IDestinationDeliveryAdapter
{
    string Kind { get; }
    // Adapter error code/message values must be structured, bounded and redacted before they cross this port.
    Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken);
    // NotPublished is safe only when the adapter also proves the old operation cannot publish later, or overlap is safe.
    Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken);
}

public sealed record ClaimedDelivery(Guid DeliveryId, Guid AttemptId, Guid ClaimToken, bool Reconcile,
    DeliveryRequest Request, DeliveryAsset Asset);
public sealed record DeliveryAsset(string SpoolKey, long Size, string Sha256, string State);

public interface IDeliveryWorkStore
{
    Task<ClaimedDelivery?> ClaimAsync(CancellationToken cancellationToken);
    Task<bool> RenewAsync(Guid deliveryId, Guid claimToken, CancellationToken cancellationToken);
    Task<bool> TargetReservedAsync(DeliveryRequest request, CancellationToken cancellationToken);
    Task FinishAsync(ClaimedDelivery work, DeliveryResult result, CancellationToken cancellationToken);
    Task FinishReconciliationAsync(ClaimedDelivery work, DeliveryReconciliation result, CancellationToken cancellationToken);
}

public interface ILocalDestinationConfiguration
{
    Task<LocalDirectoryListingResult> BrowseAsync(string? path, CancellationToken cancellationToken);
    Task<string?> GetRootAsync(Guid destinationId, CancellationToken cancellationToken);
    Task<string?> ConfigureAsync(Guid destinationId, string root, CancellationToken cancellationToken);
    Task<string?> ConfigureAsync(Guid destinationId, long expectedRevision, string root, CancellationToken cancellationToken);
    Task<(bool Writable, string? ErrorCode)> TestAsync(Guid destinationId, CancellationToken cancellationToken);
    Task<(bool Writable, string? ErrorCode, long TestedConfigurationRevision)> TestWithRevisionAsync(Guid destinationId, CancellationToken cancellationToken);
}

public sealed record LocalDirectoryEntry(string Name, string Path);
public sealed record LocalDirectoryListing(string? Path, string? ParentPath, IReadOnlyList<LocalDirectoryEntry> Directories, bool Truncated);
public sealed record LocalDirectoryListingResult(LocalDirectoryListing? Listing, string? Error);

public sealed record WebDavDestinationConfiguration(string CollectionUrl, string Username);
public interface IWebDavDestinationConfiguration
{
    Task<WebDavDestinationConfiguration?> GetAsync(Guid destinationId, CancellationToken cancellationToken);
    Task<string?> ConfigureAsync(Guid destinationId, string collectionUrl, string username, string password, CancellationToken cancellationToken);
    Task<string?> ConfigureAsync(Guid destinationId, long expectedRevision, string collectionUrl, string username, string? password, CancellationToken cancellationToken);
    Task<(bool Writable, string? ErrorCode)> TestAsync(Guid destinationId, CancellationToken cancellationToken);
    Task<(bool Writable, string? ErrorCode, long TestedConfigurationRevision)> TestWithRevisionAsync(Guid destinationId, CancellationToken cancellationToken);
}

public interface IDeliveryWorkSignal
{
    ValueTask NotifyAsync(CancellationToken cancellationToken);
    ValueTask WaitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
