using ShutterSpool.Application.Security;
using System.Text;

namespace ShutterSpool.Application.Destinations;

public sealed record DestinationConfiguration(Guid Id, string Name, string Kind, bool Enabled,
    long ConfigurationRevision, string? LocalRootPath, string? WebDavCollectionUrl, string? WebDavUsername, bool SecretConfigured,
    bool IsArchived = false);
public sealed record DestinationConfigurationWrite(bool Applied, DestinationConfiguration? Current);
public sealed record DestinationRemovalWrite(bool Applied, bool Removed, DestinationConfiguration? Current);
public sealed record DestinationManagementPage(DestinationConfiguration[] Items, Guid? NextId);

public interface IDestinationConfigurationStore
{
    Task CreateAsync(DestinationConfiguration destination, string? encryptedSecret, CancellationToken cancellationToken);
    Task<DestinationConfiguration[]> ListAsync(int limit, Guid? afterId, CancellationToken cancellationToken);
    Task<DestinationConfiguration?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<DestinationConfigurationWrite> UpdateNameAsync(Guid id, long expectedRevision, string name, CancellationToken cancellationToken);
    Task<DestinationRemovalWrite> RemoveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken);
}

public sealed class DestinationManagementService(IDestinationConfigurationStore store, ICredentialProtector protector,
    DestinationCapabilities? capabilities = null)
{
    public async Task<DestinationConfiguration> CreateAsync(string name, string kind, string? secret, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 256) throw new ArgumentException("Provide a destination name of at most 256 characters.");
        if (kind is not ("local" or "webdav")) throw new ArgumentException("The destination kind is unsupported.");
        (capabilities ?? DestinationCapabilities.All).EnsureEnabled(kind);
        if (secret is not null && Encoding.UTF8.GetByteCount(secret) > 4_096) throw new ArgumentException("The credential exceeds its supported size.");
        var destination = new DestinationConfiguration(Guid.NewGuid(), name.Trim(), kind, true, 0, null, null, null, !string.IsNullOrEmpty(secret));
        await store.CreateAsync(destination, string.IsNullOrEmpty(secret) ? null : protector.Protect(secret), cancellationToken);
        return destination;
    }

    public Task<DestinationConfiguration?> GetAsync(Guid id, CancellationToken cancellationToken) => store.GetAsync(id, cancellationToken);
    public Task<DestinationRemovalWrite> RemoveAsync(Guid id, long expectedRevision, CancellationToken token) =>
        store.RemoveAsync(id, expectedRevision, token);
    public Task<DestinationConfiguration[]> ListAsync(int limit, Guid? afterId, CancellationToken cancellationToken) =>
        store.ListAsync(limit, afterId, cancellationToken);

    public async Task<DestinationConfigurationWrite> UpdateNameAsync(Guid id, long expectedRevision, string name,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 256)
            throw new ArgumentException("Provide a destination name of at most 256 characters.");
        if (await store.GetAsync(id, token) is { } current)
            (capabilities ?? DestinationCapabilities.All).EnsureEnabled(current.Kind);
        return await store.UpdateNameAsync(id, expectedRevision, name.Trim(), token);
    }
}
