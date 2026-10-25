using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Server.Hosting;
using ShutterSpool.Application.Sources;

namespace ShutterSpool.Server.Api.Sources;

public sealed record SourceProfileCapability(string Profile, bool Enabled, string? Hostname, int? ControlPort,
    bool CaRootAvailable, string? CaRootIdentity, string? CaRootFingerprint, string? CaRootReferenceUrl, string? SetupError);
public sealed record SourcePasswordPolicyResponse(int MinimumLength, int MaximumLength, int GeneratedLength, bool WeakerProtection);
public sealed record SourceCapabilitiesResponse(SourceProfileCapability[] Profiles, SourcePasswordPolicyResponse PasswordPolicy);

public static class SourceCapabilityEndpoints
{
    private const string IsrgRootInformation = "https://letsencrypt.org/certificates/#root-cas";
    private const string IsrgRootX1Fingerprint = "96BCEC06264976F37460779ACF28C5A7CFE8A3C0AAE11A8FFCEE05C0BDDF08C6";

    public static RouteGroupBuilder MapSourceCapabilityEndpoints(this RouteGroupBuilder management)
    {
        management.MapGet("/source-capabilities", (CameraIngressSettings settings, CameraCredentialPolicy policy) =>
                Results.Ok(new SourceCapabilitiesResponse(Enum.GetValues<SourceConnectionProfile>()
                    .Select(profile => Describe(settings, profile)).ToArray(),
                    new(policy.MinimumLength, CameraCredentialPolicy.MaximumLength, policy.GeneratedLength, policy.WeakerProtection))))
            .WithSummary("Read deployment-enabled Source connection profiles")
            .Produces<SourceCapabilitiesResponse>();
        management.MapGet("/source-capabilities/{profile}/ca-root", (string profile, CameraIngressSettings settings) =>
            {
                if (!Enum.TryParse(profile, ignoreCase: false, out SourceConnectionProfile selected) || !Enum.IsDefined(selected)
                    || selected == SourceConnectionProfile.PlainFtpLan) return Results.NotFound();
                if (!settings.IsEnabled(selected) || settings.GetProfileSettings(selected) is not { } profileSettings) return Results.NotFound();
                (string? pem, _) = ReadRoot(profileSettings);
                return pem is null ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The configured public CA root is unavailable or invalid.")
                    : Results.Content(pem, "application/x-pem-file");
            })
            .WithSummary("Download the configured public CA root for an FTPS profile")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-pem-file")
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return management;
    }

    private static SourceProfileCapability Describe(CameraIngressSettings settings, SourceConnectionProfile profile)
    {
        bool enabled = settings.IsEnabled(profile);
        CameraIngressProfileSettings? endpoint = settings.GetProfileSettings(profile);
        if (!enabled) return new(profile.ToString(), false, null, null, false, null, null, null, null);
        if (endpoint is null) return new(profile.ToString(), false, null, null, false, null, null, null, null);
        if (profile == SourceConnectionProfile.PlainFtpLan)
            return new(profile.ToString(), true, settings.Hostname, endpoint.ControlPort, false, null, null, null, null);
        (string? pem, string? error) = ReadRoot(endpoint);
        if (pem is null) return new(profile.ToString(), true, settings.Hostname, endpoint.ControlPort, false, null, null, null, error);
        using X509Certificate2 cert = X509Certificate2.CreateFromPem(pem);
        string fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData));
        string? reference = fingerprint == IsrgRootX1Fingerprint ? IsrgRootInformation : null;
        return new(profile.ToString(), true, settings.Hostname, endpoint.ControlPort, true, cert.Subject,
            fingerprint, reference, null);
    }

    private static (string? Pem, string? Error) ReadRoot(CameraIngressProfileSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.CaRootPath)) return (null, "No public CA root is configured for this profile.");
        try
        {
            using var file = new FileStream(settings.CaRootPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 131_072) return (null, "The configured public CA root is missing or exceeds the size limit.");
            byte[] bytes = new byte[131_073];
            int count = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count is 0 or > 131_072) return (null, "The configured public CA root is missing or exceeds the size limit.");
            string pem = new UTF8Encoding(false, true).GetString(bytes, 0, count);
            if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal)) return (null, "The configured CA file contains private key material.");
            if (pem.Split("-----BEGIN CERTIFICATE-----", StringSplitOptions.None).Length != 2
                || pem.Split("-----END CERTIFICATE-----", StringSplitOptions.None).Length != 2)
                return (null, "Configure exactly one public CA root certificate.");
            using X509Certificate2 cert = X509Certificate2.CreateFromPem(pem);
            X509BasicConstraintsExtension? constraints = cert.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
            if (constraints?.CertificateAuthority != true || !string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal)
                || cert.NotBefore.ToUniversalTime() > DateTime.UtcNow || cert.NotAfter.ToUniversalTime() < DateTime.UtcNow)
                return (null, "The configured certificate is not a self-issued CA root.");
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(cert);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (!chain.Build(cert)) return (null, "The configured certificate is not a valid self-signed CA root.");
            return (cert.ExportCertificatePem(), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or DecoderFallbackException or NotSupportedException)
        {
            return (null, "The configured public CA root is invalid or cannot be read.");
        }
    }
}
