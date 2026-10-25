using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Security;
using System.Security.Cryptography;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Security;

internal sealed class PrivacyErasureEvidenceCodec(ICredentialProtector protector) : IPrivacyErasureEvidenceCodec
{
    private const int MaximumPayloadLength = 1_000_000;
    private const string ManifestPrefix = "m205-erasure-manifest-v1:";
    private const string PagePrefix = "m205-erasure-page-v1:";
    private const string ManifestPurpose = "ShutterSpool:PrivacyErasure:Manifest:v1\0";
    private const string PagePurpose = "ShutterSpool:PrivacyErasure:Page:v1\0";

    public string ProtectManifest(PrivacyErasureSuppressionManifest manifest) =>
        Protect(ManifestPrefix, ManifestPurpose, manifest);

    public bool TryReadManifest(string payload, out PrivacyErasureSuppressionManifest? manifest) =>
        TryRead(ManifestPrefix, ManifestPurpose, payload, out manifest);

    public string ProtectPage(PrivacyErasureSuppressionPage page) => Protect(PagePrefix, PagePurpose, page);

    public bool TryReadPage(string payload, out PrivacyErasureSuppressionPage? page) =>
        TryRead(PagePrefix, PagePurpose, payload, out page);

    private string Protect<T>(string prefix, string purpose, T value) => prefix
        + protector.Protect(purpose + JsonSerializer.Serialize(value));

    private bool TryRead<T>(string prefix, string purpose, string? payload, out T? value) where T : class
    {
        value = null;
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > MaximumPayloadLength
            || !payload.StartsWith(prefix, StringComparison.Ordinal)) return false;
        try
        {
            string plaintext = protector.Unprotect(payload[prefix.Length..]);
            if (!plaintext.StartsWith(purpose, StringComparison.Ordinal)) return false;
            value = JsonSerializer.Deserialize<T>(plaintext[purpose.Length..]);
            return value is not null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        {
            return false;
        }
    }
}
