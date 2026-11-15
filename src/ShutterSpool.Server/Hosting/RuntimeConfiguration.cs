namespace ShutterSpool.Server.Hosting;

internal static class RuntimeConfiguration
{
    public static void ValidatePackaged(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("ShutterSpool:Deployment:Packaged")) return;

        string[] required =
        [
            "ConnectionStrings:shutterspool-control",
            "ShutterSpool:Security:Authority",
            "ShutterSpool:Security:Audience",
            "ShutterSpool:Security:EncryptionKey",
        ];
        if (required.Any(key => string.IsNullOrWhiteSpace(configuration[key])))
            throw new InvalidOperationException("Packaged deployment requires PostgreSQL, identity, encryption-key and spool configuration.");
        string authorityText = configuration["ShutterSpool:Security:Authority"]!;
        if (!Uri.TryCreate(authorityText, UriKind.Absolute, out Uri? authority) || authority.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(authority.UserInfo) || !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment))
            throw new InvalidOperationException("Packaged deployment requires an absolute HTTPS OIDC authority without user info, query or fragment.");
        byte[] key;
        try { key = Convert.FromBase64String(configuration["ShutterSpool:Security:EncryptionKey"]!); }
        catch (FormatException exception) { throw new InvalidOperationException("The packaged encryption key must be base64-encoded 32-byte data.", exception); }
        if (key.Length != 32) throw new InvalidOperationException("The packaged encryption key must be base64-encoded 32-byte data.");
        if ((configuration.GetSection("ShutterSpool:ForwardedHeaders:KnownNetworks").Get<string[]>() ?? []).Length == 0)
            throw new InvalidOperationException("Packaged deployment requires an explicit trusted proxy network.");
    }

}
