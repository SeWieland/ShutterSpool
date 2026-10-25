namespace ShutterSpool.Server.Security;

public sealed class SecurityOptions
{
    public const string SectionName = "ShutterSpool:Security";

    public string? Authority { get; init; }

    public string? Audience { get; init; }

    public string? MetadataAddress { get; init; }

    public bool AllowInsecureMetadataAddress { get; init; }

    public string ClientId { get; init; } = "shutterspool-web";

    public string[] AllowedOrigins { get; init; } = [];

    public string AdministratorRole { get; init; } = "shutterspool-admin";

    public string AdministratorRoleClaimType { get; init; } = System.Security.Claims.ClaimsIdentity.DefaultRoleClaimType;

    public string? BootstrapIssuer { get; init; }

    public string? BootstrapSubject { get; init; }

    public string? EncryptionKey { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId)) throw new InvalidOperationException("The public OIDC client ID is required.");
        if (string.IsNullOrWhiteSpace(AdministratorRoleClaimType)) throw new InvalidOperationException("The administrator role claim type is required.");
        if (!string.IsNullOrWhiteSpace(MetadataAddress))
        {
            if (!Uri.TryCreate(MetadataAddress, UriKind.Absolute, out Uri? metadata)
                || (metadata.Scheme != Uri.UriSchemeHttps && metadata.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(metadata.UserInfo) || !string.IsNullOrEmpty(metadata.Query)
                || !string.IsNullOrEmpty(metadata.Fragment))
                throw new InvalidOperationException("The OIDC metadata address must be an absolute HTTP(S) URL without user info, query or fragment.");
            if (metadata.Scheme == Uri.UriSchemeHttp && !AllowInsecureMetadataAddress)
                throw new InvalidOperationException("An HTTP OIDC metadata address requires explicit AllowInsecureMetadataAddress=true on a private backend network.");
        }
        foreach (string origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) || uri.AbsolutePath != "/"
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || (uri.Scheme != "https" && !(uri.Scheme == "http" && IsLoopbackHost(uri.Host))))
                throw new InvalidOperationException("Live update origins must be HTTPS origins, except HTTP loopback origins.");
        }
    }

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address);
}
