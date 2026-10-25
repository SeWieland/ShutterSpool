namespace ShutterSpool.Application.Security;

public interface IAccessGrantStore
{
    Task<bool> IsActiveAsync(string issuer, string subject, CancellationToken cancellationToken);
    Task BootstrapAsync(string issuer, string subject, CancellationToken cancellationToken);
}

public sealed class AccessGrantService(IAccessGrantStore store)
{
    public async Task<bool> IsAllowedAsync(string? issuer, string? subject, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)) return false;
        try { return await store.IsActiveAsync(issuer, subject, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return false; } // Unavailable authoritative state cannot grant access.
    }
}

public interface ICredentialProtector
{
    string CreateSecret();
    byte[] CreateVerifier(string secret);
    bool Verify(string secret, byte[] verifier);
    string Protect(string secret);
    string Unprotect(string protectedSecret);
}

public interface ISourceAccessAudit
{
    void Succeeded(Guid sourceId);
    void Failed();
}

public sealed class ConfigurationConflictException() : Exception("A configuration with that name already exists.");
