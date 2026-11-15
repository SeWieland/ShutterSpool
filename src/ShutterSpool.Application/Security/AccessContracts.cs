using ShutterSpool.Domain.Security;

namespace ShutterSpool.Application.Security;

public interface IAccessGrantStore
{
    Task<bool> IsActiveAsync(string issuer, string subject, CancellationToken cancellationToken);
    Task<WorkspaceMembership?> GetAsync(string issuer, string subject, CancellationToken cancellationToken);
    Task<WorkspaceMembership[]> ListAsync(CancellationToken cancellationToken);
    Task UpdateDisplayNameAsync(string issuer, string subject, string? displayName, CancellationToken cancellationToken);
    Task<WorkspaceViewerPolicy> GetViewerPolicyAsync(CancellationToken cancellationToken);
    Task<AccessChangeResult> ChangeAsync(AccessChange change, CancellationToken cancellationToken);
}

public enum AccessChangeKind { Role, PromoteOwner, Remove, Leave, ViewerPolicy }
public sealed record WorkspaceMembership(Guid Id, string Issuer, string Subject, WorkspaceRole Role, long Revision, bool IsActive, string? DisplayName = null);
public sealed record WorkspaceViewerPolicy(bool IncludeUnreleased, long Revision);
public sealed record AccessChange(Guid OperationId, string ActorIssuer, string ActorSubject, Guid TargetId,
    long ExpectedRevision, AccessChangeKind Kind, WorkspaceRole? Role = null, bool Confirmed = false,
    bool IncludeUnreleased = false);
public sealed record AccessChangeResult(Guid OperationId, string Status, WorkspaceMembership? Membership = null,
    WorkspaceViewerPolicy? ViewerPolicy = null);
public sealed record SafeDelivery(Guid Id, Guid AssetId, string State, bool CanRetry);
public interface IWorkspaceMediaScope
{
    Task<bool> CanReadCaptureAsync(Guid captureId, CancellationToken token);
    Task<SafeDelivery[]?> GetDeliveriesAsync(Guid captureId, CancellationToken token);
}

// Set only by current request authorization; background work retains its durable tenant authority.
public sealed class WorkspaceAccessContext
{
    public WorkspaceMembership? Membership { get; set; }
    public WorkspaceViewerPolicy? ViewerPolicy { get; set; }
    public bool IsViewer => Membership?.Role == WorkspaceRole.Viewer;
    public bool ReleasedOnly => IsViewer && ViewerPolicy?.IncludeUnreleased != true;
    public bool IsAdministrator => Membership is not null && WorkspaceAccessPolicy.CanAdminister(Membership.Role);
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
