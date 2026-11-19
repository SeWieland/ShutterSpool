using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Server.Hosting;

public enum CameraCallbackTransport
{
    ApplicationHttps,
    MeshMtls,
}

public sealed class CameraIngressSettings
{
    public bool Enabled { get; init; }
    public CameraCallbackTransport CallbackTransport { get; init; } = CameraCallbackTransport.ApplicationHttps;
    public string CallbackKey { get; init; } = "";
    public string CallbackBaseUrl { get; init; } = "";
    public string CallbackCertificatePath { get; init; } = "";
    public int InternalPort { get; init; } = 8081;
    public bool InternalListenOnAllInterfaces { get; init; }
    public long MaximumUploadBytes { get; init; } = 512L * 1024 * 1024;
    public int MaximumConcurrentTransfers { get; init; } = 16;
    public int MaximumSessionsPerSource { get; init; } = 2;
    public int UploadBandwidthKiBPerSecond { get; init; } = 8_192;
    public int TransferTimeoutSeconds { get; init; } = 600;
    public CameraIngressProfiles Profiles { get; init; } = new();
    public string Hostname { get; init; } = "ingest.shutterspool.com";

    public CameraIngressProfileSettings? GetProfileSettings(SourceConnectionProfile profile) => profile switch
    {
        SourceConnectionProfile.PlainFtpLan => Profiles.PlainFtpLan,
        SourceConnectionProfile.ModernFtps => Profiles.ModernFtps,
        SourceConnectionProfile.LegacyFtps => Profiles.LegacyFtps,
        _ => null,
    };

    public bool IsEnabled(SourceConnectionProfile profile) => Enabled && GetProfileSettings(profile)?.Enabled == true;

    public void Validate()
    {
        if (!Enabled) return;
        ValidateCallback();
        ValidateLimits();
        ValidateProfiles();
    }

    private void ValidateCallback()
    {
        if (CallbackKey.Length < 32) throw new InvalidOperationException("Camera ingress callback key must contain at least 32 characters.");
        string scheme = CallbackTransport switch
        {
            CameraCallbackTransport.ApplicationHttps => Uri.UriSchemeHttps,
            CameraCallbackTransport.MeshMtls => Uri.UriSchemeHttp,
            _ => throw new InvalidOperationException("Camera ingress callback transport is invalid."),
        };
        bool useMeshTransport = CallbackTransport == CameraCallbackTransport.MeshMtls;
        if (!Uri.TryCreate(CallbackBaseUrl.TrimEnd('/'), UriKind.Absolute, out Uri? callbackBase)
            || callbackBase.Scheme != scheme || callbackBase.Port < 1 || callbackBase.AbsolutePath != "/internal/camera"
            || callbackBase.UserInfo.Length != 0 || callbackBase.Query.Length != 0 || callbackBase.Fragment.Length != 0
            || (useMeshTransport && (!callbackBase.IsWellFormedOriginalString() || callbackBase.OriginalString.Contains('@'))))
            throw new InvalidOperationException($"Camera ingress callback base URL must be an {scheme.ToUpperInvariant()} URL pointing to /internal/camera.");

        if (!useMeshTransport) return;
        if (InternalListenOnAllInterfaces)
            throw new InvalidOperationException("Camera ingress mesh mTLS requires a loopback callback listener.");
        if (!string.IsNullOrEmpty(CallbackCertificatePath))
            throw new InvalidOperationException("Camera ingress mesh mTLS cannot set a callback certificate path.");
    }

    private void ValidateLimits()
    {
        if (InternalPort is < 1 or > 65_535 || MaximumUploadBytes <= 0
            || MaximumConcurrentTransfers is < 1 or > 64 || MaximumSessionsPerSource is < 1 or > 16
            || UploadBandwidthKiBPerSecond <= 0 || TransferTimeoutSeconds is < 30 or > 3_600
            || MaximumUploadBytes > long.MaxValue / (MaximumConcurrentTransfers * 2L))
            throw new InvalidOperationException("Camera ingress limits are invalid.");
    }

    private void ValidateProfiles()
    {
        if (string.IsNullOrWhiteSpace(Hostname) || Hostname.Contains('/') || Hostname.Contains(':'))
            throw new InvalidOperationException("Camera ingress hostname must be a host name without a scheme or port.");
        foreach (CameraIngressProfileSettings profile in new[] { Profiles.PlainFtpLan, Profiles.ModernFtps, Profiles.LegacyFtps })
            if (profile.Enabled && profile.ControlPort is < 1 or > 65_535)
                throw new InvalidOperationException("An enabled camera ingress profile requires a valid control port.");
    }
}

public sealed class CameraIngressProfiles
{
    public CameraIngressProfileSettings PlainFtpLan { get; init; } = new();
    public CameraIngressProfileSettings ModernFtps { get; init; } = new();
    public CameraIngressProfileSettings LegacyFtps { get; init; } = new();
}

public sealed class CameraIngressProfileSettings
{
    public bool Enabled { get; init; }
    public int ControlPort { get; init; }
    public string? CaRootPath { get; init; }
}
