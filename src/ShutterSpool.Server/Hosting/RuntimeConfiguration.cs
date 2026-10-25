namespace ShutterSpool.Server.Hosting;

internal static class RuntimeConfiguration
{
    public static void LoadFileSecrets(IConfiguration configuration)
    {
        Load(configuration, "ConnectionStrings:shutterspoolFile", "ConnectionStrings:shutterspool");
        Load(configuration, "ConnectionStrings:valkeyFile", "ConnectionStrings:valkey");
        Load(configuration, "ShutterSpool:Security:EncryptionKeyFile", "ShutterSpool:Security:EncryptionKey");
        Load(configuration, "ShutterSpool:CameraIngress:CallbackKeyFile", "ShutterSpool:CameraIngress:CallbackKey");
    }

    public static void ValidatePackaged(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("ShutterSpool:Deployment:Packaged")) return;

        string[] required =
        [
            "ConnectionStrings:shutterspool",
            "ShutterSpool:Security:Authority",
            "ShutterSpool:Security:Audience",
            "ShutterSpool:Security:BootstrapIssuer",
            "ShutterSpool:Security:BootstrapSubject",
            "ShutterSpool:Security:EncryptionKey",
            "ShutterSpool:Spool:RootPath",
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

    private static void Load(IConfiguration configuration, string pathKey, string valueKey)
    {
        string? path = configuration[pathKey];
        if (string.IsNullOrWhiteSpace(path)) return;
        string value = File.ReadAllText(path).TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"The configured secret file '{pathKey}' is empty.");
        configuration[valueKey] = value;
    }
}
