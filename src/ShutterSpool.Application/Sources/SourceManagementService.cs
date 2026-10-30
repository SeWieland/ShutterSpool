using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Application.Sources;

public sealed record SourceProvisioning(Guid Id, string Name, SourceCurationMode CurationMode, int DelaySeconds,
    string Username, string VirtualRoot, byte[] SecretVerifier, SourceType SourceType, SourceConnectionProfile ConnectionProfile,
    Guid CollectionId);
public sealed record CreatedSource(Guid Id, string Username, string VirtualRoot, string Secret);
public sealed record SourcePolicy(Guid Id, SourceCurationMode CurationMode, int CurationDelaySeconds);
public sealed record SourceCredential(Guid SourceId, string Username, string VirtualRoot, byte[] SecretVerifier, long CredentialGeneration,
    bool IsIngressEnabled, SourceType SourceType, SourceConnectionProfile ConnectionProfile);
public sealed record SourceAccess(Guid SourceId, string VirtualRoot, long CredentialGeneration);
public sealed record SourceCredentialCheck(SourceAccess? Access, Guid? VerifiedButRejectedSourceId);
public enum SourceAuthenticationOutcome { Succeeded, Rejected }
public sealed record SourceAuthenticationObservation(Guid SourceId, SourceAuthenticationOutcome Outcome, DateTimeOffset ObservedAt);
public sealed record SourceAuthenticationSnapshot(Guid SourceId, SourceAuthenticationOutcome Outcome, DateTimeOffset ObservedAt, long Version);
public sealed record SourceIngressState(Guid SourceId, long CredentialGeneration, bool IsEnabled);
public sealed record SourceManagementConfiguration(Guid Id, string Name, SourceCurationMode CurationMode,
    int CurationDelaySeconds, string Username, long CredentialGeneration, bool IsIngressEnabled,
    long ConfigurationRevision, bool IsArchived, SourceType SourceType, SourceConnectionProfile ConnectionProfile,
    Guid CollectionId, string CollectionName);
public sealed record SourceConfigurationWrite(bool Applied, SourceManagementConfiguration? Current);
public sealed record SourceRemovalWrite(bool Applied, bool Removed, SourceManagementConfiguration? Current);
public sealed record SourceSecretWrite(bool Applied, SourceManagementConfiguration? Current, string? Secret);
public sealed record SourceManagementPage(SourceManagementConfiguration[] Items, string? NextCursor);
public sealed class UnchangedSourceCredentialException() : Exception("Choose a different username or password.");
public sealed class SourceUsernameConflictException() : Exception("Camera username is already in use.");

public sealed class CameraCredentialPolicy
{
    public const int MaximumLength = 128;
    public int MinimumLength { get; set; } = 12;
    public int GeneratedLength => Math.Max(20, MinimumLength);
    public bool WeakerProtection => MinimumLength < 12;

    public void Validate()
    {
        if (MinimumLength is < 1 or > MaximumLength)
            throw new InvalidOperationException("Camera credential minimum password length must be between 1 and 128.");
    }
}

public interface ISourceConfigurationStore
{
    Task<SourceManagementConfiguration[]> ListAsync(int limit, string? afterName, Guid? afterId, CancellationToken cancellationToken);
    Task<SourceManagementConfiguration?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<SourceConfigurationWrite> UpdateConfigurationAsync(Guid id, long expectedRevision, string name,
        SourceCurationMode mode, int delaySeconds, SourceType sourceType, SourceConnectionProfile connectionProfile,
        Guid collectionId, CancellationToken cancellationToken);
    Task<SourceConfigurationWrite> ReplaceCredentialAsync(Guid id, long expectedRevision, string username, byte[] verifier, CancellationToken cancellationToken);
    Task<SourceConfigurationWrite> SetIngressEnabledAsync(Guid id, long expectedRevision, bool enabled, CancellationToken cancellationToken);
    Task<SourceRemovalWrite> RemoveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken);
    Task CreateAsync(SourceProvisioning source, CancellationToken cancellationToken);
    Task<SourcePolicy?> UpdatePolicyAsync(Guid id, SourceCurationMode mode, int delaySeconds, CancellationToken cancellationToken);
    Task<bool> RegenerateAsync(Guid id, byte[] verifier, CancellationToken cancellationToken);
    Task<SourceIngressState?> SetIngressEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken);
    Task<SourceCredential?> FindCredentialAsync(string username, CancellationToken cancellationToken);
}

public interface ISourceAuthenticationObservationStore
{
    Task<IReadOnlyList<SourceAuthenticationSnapshot>> ReadAsync(IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken);
    Task<long?> RecordAsync(SourceAuthenticationObservation observation, CancellationToken cancellationToken);
}

public interface ISourceAuthenticationObservationRecorder
{
    void Record(SourceAuthenticationObservation observation);
    void RecordUnassigned(DateTimeOffset observedAt);
}

public sealed class SourceManagementService(ISourceConfigurationStore store, ICredentialProtector protector, CameraCredentialPolicy policy,
    ShutterSpool.Application.Workspaces.WorkspaceContext? workspace = null)
{
    private static readonly string[] UsernamePrefixes =
    [
        "bold", "busy", "calm", "cool", "cozy", "cute", "deft", "deep",
        "fair", "fast", "fine", "flat", "free", "glad", "good", "hazy",
        "keen", "lazy", "low", "mild", "neat", "odd", "pale", "rare",
        "shy", "slow", "sly", "snug", "soft", "tiny", "warm", "wide",
    ];
    private static readonly string[] UsernamePhotoWords =
    [
        "beam", "blur", "bulb", "cast", "clip", "crop", "fade", "film",
        "fill", "gain", "glow", "grid", "haze", "iris", "jpeg", "lens",
        "look", "mask", "noir", "pano", "pose", "raw", "roll", "scan",
        "shot", "snap", "spot", "stop", "tint", "tone", "view", "zoom",
    ];

    public Task<SourceManagementConfiguration[]> ListAsync(int limit, string? afterName, Guid? afterId, CancellationToken token) =>
        store.ListAsync(limit, afterName, afterId, token);
    public Task<SourceManagementConfiguration?> GetAsync(Guid id, CancellationToken token) => store.GetAsync(id, token);
    public Task<SourceRemovalWrite> RemoveAsync(Guid id, long expectedRevision, CancellationToken token) =>
        store.RemoveAsync(id, expectedRevision, token);

    public async Task<CreatedSource> CreateAsync(string name, string mode, int? delaySeconds, CancellationToken cancellationToken,
        SourceType sourceType = SourceType.CameraFtpUpload, SourceConnectionProfile connectionProfile = SourceConnectionProfile.ModernFtps,
        string? requestedUsername = null, string? passwordMode = null, string? requestedPassword = null, Guid? collectionId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 256) throw new ArgumentException("Provide a source name of at most 256 characters.");
        (SourceCurationMode policy, int delay) = ParsePolicy(mode, delaySeconds);
        Guid id = Guid.NewGuid();
        string username = requestedUsername ?? (workspace?.Enabled == true ? $"camera-{Guid.NewGuid():N}" : GenerateUsername());
        ValidateUsername(username);
        string secret = ResolvePassword(passwordMode, requestedPassword);
        string root = $"sources/{Guid.NewGuid():N}";
        ValidateProfile(sourceType, connectionProfile);
        var provisioning = new SourceProvisioning(id, name.Trim(), policy, delay, username, root,
            protector.CreateVerifier(secret), sourceType, connectionProfile, collectionId ?? ShutterSpool.Domain.Collections.CollectionName.BuiltInId);
        for (int attempt = 0; ; attempt++)
        {
            try { await store.CreateAsync(provisioning, cancellationToken); break; }
            catch (SourceUsernameConflictException) when (requestedUsername is null && attempt < 4)
            {
                username = GenerateUsername();
                provisioning = provisioning with { Username = username };
            }
        }
        return new(id, username, root, secret);
    }

    public Task<SourcePolicy?> UpdatePolicyAsync(Guid id, string mode, int? delaySeconds, CancellationToken cancellationToken)
    {
        (SourceCurationMode policy, int delay) = ParsePolicy(mode, delaySeconds);
        return store.UpdatePolicyAsync(id, policy, delay, cancellationToken);
    }

    public async Task<SourceConfigurationWrite> UpdateConfigurationAsync(Guid id, long expectedRevision,
        string name, string mode, int? delaySeconds, CancellationToken token,
        SourceType sourceType = SourceType.CameraFtpUpload, SourceConnectionProfile connectionProfile = SourceConnectionProfile.ModernFtps,
        Guid? collectionId = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 256)
            throw new ArgumentException("Provide a source name of at most 256 characters.");
        (SourceCurationMode policy, int delay) = ParsePolicy(mode, delaySeconds);
        ValidateProfile(sourceType, connectionProfile);
        return await store.UpdateConfigurationAsync(id, expectedRevision, name.Trim(), policy, delay, sourceType, connectionProfile,
            collectionId ?? (await store.GetAsync(id, token))?.CollectionId ?? ShutterSpool.Domain.Collections.CollectionName.BuiltInId, token);
    }

    public async Task<SourceSecretWrite> RegenerateAsync(Guid id, long expectedRevision, CancellationToken token,
        string? username = null, string? passwordMode = null, string? requestedPassword = null)
    {
        SourceManagementConfiguration? current = await store.GetAsync(id, token);
        if (current is null) return new(false, null, null);
        string selectedUsername = username ?? current.Username;
        ValidateUsername(selectedUsername);
        string secret = ResolvePassword(passwordMode, requestedPassword);
        byte[] verifier = protector.CreateVerifier(secret);
        SourceConfigurationWrite result = await store.ReplaceCredentialAsync(id, expectedRevision, selectedUsername, verifier, token);
        return new(result.Applied, result.Current, result.Applied ? secret : null);
    }

    private string ResolvePassword(string? mode, string? password)
    {
        if (mode is not (null or "Generated" or "Custom")) throw new ArgumentException("Choose Generated or Custom.", "passwordMode");
        if (mode == "Custom")
        {
            if (password is null) throw new ArgumentException("A custom password is required.", "password");
            ValidatePassword(password);
            return password;
        }
        if (password is not null) throw new ArgumentException("Password is only valid with Custom mode.", "password");
        return GeneratePassword();
    }

    private void ValidatePassword(string password)
    {
        if (password.Length < policy.MinimumLength || password.Length > CameraCredentialPolicy.MaximumLength
            || password.Any(character => character is < '!' or > '~'))
            throw new ArgumentException($"Use {policy.MinimumLength} to {CameraCredentialPolicy.MaximumLength} non-space printable ASCII characters.", "password");
    }

    private string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return System.Security.Cryptography.RandomNumberGenerator.GetString(alphabet, policy.GeneratedLength);
    }

    private static string GenerateUsername()
    {
        return UsernamePrefixes[System.Security.Cryptography.RandomNumberGenerator.GetInt32(UsernamePrefixes.Length)]
            + UsernamePhotoWords[System.Security.Cryptography.RandomNumberGenerator.GetInt32(UsernamePhotoWords.Length)];
    }

    private static void ValidateUsername(string username)
    {
        if (username.Length is < 1 or > 64 || !char.IsAsciiLetterOrDigit(username[0])
            || username.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new ArgumentException("Use 1 to 64 ASCII letters, digits, underscores or hyphens, starting with a letter or digit.", "username");
    }

    public Task<SourceConfigurationWrite> SetIngressEnabledAsync(Guid id, long expectedRevision, bool enabled,
        CancellationToken token) => store.SetIngressEnabledAsync(id, expectedRevision, enabled, token);

    public Task<SourceIngressState?> SetIngressEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken) =>
        store.SetIngressEnabledAsync(id, enabled, cancellationToken);

    private static (SourceCurationMode Mode, int Delay) ParsePolicy(string? value, int? requestedDelay)
    {
        return SourceCurationPolicy.Parse(value, requestedDelay)
            ?? throw new ArgumentException("Provide Immediate, Delay or Manual; Delay accepts 1 to 31,536,000 seconds.");
    }

    private static void ValidateProfile(SourceType sourceType, SourceConnectionProfile connectionProfile)
    {
        if (sourceType != SourceType.CameraFtpUpload || !Enum.IsDefined(connectionProfile))
            throw new ArgumentException("The requested Source type or connection profile is unavailable.");
    }
}

public sealed class SourceCredentialVerifier(ISourceConfigurationStore store, ICredentialProtector protector, ISourceAccessAudit audit)
{
    public async Task<SourceAccess?> VerifyAsync(string username, string secret, CancellationToken cancellationToken,
        SourceConnectionProfile connectionProfile = SourceConnectionProfile.ModernFtps) =>
        (await VerifyAttemptAsync(username, secret, cancellationToken, connectionProfile)).Access;

    public async Task<SourceCredentialCheck> VerifyAttemptAsync(string username, string secret, CancellationToken cancellationToken,
        SourceConnectionProfile connectionProfile = SourceConnectionProfile.ModernFtps)
    {
        SourceCredential? credential = await store.FindCredentialAsync(username, cancellationToken);
        if (credential is null || !protector.Verify(secret, credential.SecretVerifier))
        {
            audit.Failed();
            return new(null, null);
        }
        if (!credential.IsIngressEnabled)
        {
            audit.Failed();
            return new(null, credential.SourceId);
        }
        if (credential.ConnectionProfile != connectionProfile)
        {
            audit.Failed();
            return new(null, credential.SourceId);
        }
        return new(new(credential.SourceId, credential.VirtualRoot, credential.CredentialGeneration), null);
    }

    public void RecordSucceeded(Guid sourceId) => audit.Succeeded(sourceId);
}
