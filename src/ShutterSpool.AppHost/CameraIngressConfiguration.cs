using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;

namespace ShutterSpool.AppHost;

internal sealed record CameraIngressProfile(string Name, string Edge, int Control, int Start, int End, string AuthSuffix)
{
    private const string PlainFtpLan = "PlainFtpLan";
    private const string LegacyFtps = "LegacyFtps";
    private static readonly CameraIngressProfile[] Defaults =
    [
        new("ModernFtps", "camera-edge", 2121, 2122, 2137, "auth"),
        new(LegacyFtps, "camera-edge-legacy", 2138, 2139, 2154, "legacy-auth"),
        new(PlainFtpLan, "camera-edge-plain", 2155, 2156, 2171, "plain-auth"),
    ];

    public bool RequiresTls => Name != PlainFtpLan;
    public bool AllowsLegacyCiphers => Name == LegacyFtps;
    public static IEnumerable<string> Names => Defaults.Select(profile => profile.Name);

    public static CameraIngressProfile[] ReadEnabled(IConfiguration configuration)
    {
        var enabled = new List<CameraIngressProfile>();
        foreach (var profile in Defaults)
        {
            var section = configuration.GetSection($"ShutterSpool:CameraIngress:Profiles:{profile.Name}");
            if (section.GetValue<bool>("Enabled"))
                enabled.Add(profile with
                {
                    Control = section.GetValue("ControlPort", profile.Control),
                    Start = section.GetValue("PassiveStart", profile.Start),
                    End = section.GetValue("PassiveEnd", profile.End),
                });
        }
        return enabled.ToArray();
    }
}

internal sealed class CameraIngressConfiguration
{
    public const string EdgeCallbackTrustPath = "/etc/sftpgo/callback-trust/root.pem";
    public const string EdgeCertificateDirectories = "/etc/ssl/certs:/etc/sftpgo/callback-trust";

    public int MaxConnections { get; }
    public int MaxPerHost { get; }
    public int IdleMinutes { get; }
    public int HookTimeout { get; }
    public long EdgeBufferBytes { get; }
    public string ListenAddress { get; }
    public string AdvertisedAddress { get; }
    public string CertificateDirectory { get; }
    public string CallbackCertificatePath { get; } = "";
    public string CallbackTrustPath { get; } = "";

    public CameraIngressConfiguration(IConfiguration configuration, CameraIngressProfile[] profiles, bool isPublishMode)
    {
        var section = configuration.GetSection("ShutterSpool:CameraIngress");
        MaxConnections = section.GetValue("MaxConnections", 16);
        MaxPerHost = section.GetValue("MaxPerHostConnections", 8);
        IdleMinutes = section.GetValue("IdleMinutes", 5);
        HookTimeout = section.GetValue("HookTimeoutSeconds", 60);
        EdgeBufferBytes = section.GetValue("EdgeBufferBytes", 1073741824L);
        ListenAddress = section["ListenAddress"] ?? section["AdvertisedAddress"] ?? "127.0.0.1";
        AdvertisedAddress = section["AdvertisedAddress"] ?? ListenAddress;
        if (MaxConnections is < 1 or > 128 || MaxPerHost < 1 || MaxPerHost > MaxConnections
            || IdleMinutes is < 1 or > 60 || HookTimeout is < 1 or > 300 || EdgeBufferBytes is < 1048576 or > 17179869184
            || !IsIpv4(ListenAddress) || !IsIpv4(AdvertisedAddress))
            throw new InvalidOperationException("Camera ingress needs a valid IPv4 address, control port and separate passive range of 2–64 ports.");
        ValidatePorts(profiles);

        CertificateDirectory = section["CertificateDirectory"] ?? "";
        string hostname = section["Hostname"] ?? "ingest.shutterspool.com";
        if (profiles.Any(profile => profile.RequiresTls))
        {
            if (string.IsNullOrWhiteSpace(CertificateDirectory))
                throw new InvalidOperationException("Explicit FTPS requires ShutterSpool:CameraIngress:CertificateDirectory containing server.pem and root.pem.");
            CertificateDirectory = Path.GetFullPath(CertificateDirectory);
            if (!isPublishMode)
                CameraIngressCertificate.Validate(Path.Combine(CertificateDirectory, "server.pem"),
                    Path.Combine(CertificateDirectory, "root.pem"), hostname);
        }

        if (isPublishMode) return;
        (CallbackCertificatePath, CallbackTrustPath) = ValidateDevelopmentCallback(configuration, section, CertificateDirectory);
    }

    private static (string CertificatePath, string TrustPath) ValidateDevelopmentCallback(
        IConfiguration configuration, IConfigurationSection section, string cameraCertificateDirectory)
    {
        if (!configuration.GetValue("ASPIRE_ENABLE_CONTAINER_TUNNEL", true))
            throw new InvalidOperationException("Camera ingress requires the Aspire container tunnel for private HTTPS callbacks.");
        string callbackHostname = configuration["AppHost:ContainerHostname"] ?? KnownHostNames.DefaultContainerTunnelHostName;
        return DevelopmentCallbackCertificate.Resolve(section["CallbackCertificateDirectory"], callbackHostname, cameraCertificateDirectory);
    }

    private static bool IsIpv4(string value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork;

    private static void ValidatePorts(CameraIngressProfile[] profiles)
    {
        var occupiedPorts = new HashSet<int>();
        foreach (var profile in profiles)
        {
            if (profile.Control is < 1024 or > 65535 || profile.Start is < 1024 or > 65534 || profile.End <= profile.Start
                || profile.End > 65535 || profile.End - profile.Start >= 64
                || !occupiedPorts.Add(profile.Control))
                throw new InvalidOperationException("Camera profiles need valid, disjoint control/passive ports with 2–64 passive ports each.");
            for (int port = profile.Start; port <= profile.End; port++)
                if (!occupiedPorts.Add(port))
                    throw new InvalidOperationException("Camera profile control/passive ports must not overlap.");
        }
    }
}
