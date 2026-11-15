using Microsoft.Extensions.Configuration;
using ShutterSpool.Server.Hosting;
using ShutterSpool.Server.Security;

namespace ShutterSpool.IntegrationTests;

public sealed class RuntimeConfigurationTests
{
    [Fact]
    public void Packaged_configuration_rejects_missing_authoritative_and_identity_settings()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Deployment:Packaged"] = "true",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => RuntimeConfiguration.ValidatePackaged(configuration));
    }

    [Theory]
    [InlineData("http://identity.example.invalid/realms/shutterspool")]
    [InlineData("https://user@identity.example.invalid/realms/shutterspool")]
    [InlineData("https://identity.example.invalid/realms/shutterspool?query=true")]
    public void Packaged_configuration_rejects_an_unsafe_oidc_authority(string authority)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Deployment:Packaged"] = "true",
            ["ConnectionStrings:shutterspool-control"] = "Host=db;Database=app;Username=app;Password=synthetic",
            ["ShutterSpool:Security:Authority"] = authority,
            ["ShutterSpool:Security:Audience"] = "synthetic-audience",
            ["ShutterSpool:Security:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            ["ShutterSpool:Spool:RootPath"] = "/var/lib/shutterspool/spool",
            ["ShutterSpool:ForwardedHeaders:KnownNetworks:0"] = "172.29.70.0/24",
        }).Build();

        Assert.Throws<InvalidOperationException>(() => RuntimeConfiguration.ValidatePackaged(configuration));
    }

    [Theory]
    [InlineData("http://keycloak:8080/realms/shutterspool/.well-known/openid-configuration")]
    [InlineData("https://user@keycloak/metadata")]
    [InlineData("https://keycloak/metadata?query=true")]
    public void Security_options_reject_http_metadata_without_opt_in_and_malformed_metadata(string metadataAddress)
    {
        SecurityOptions options = new()
        {
            Authority = "https://identity.example.invalid/realms/shutterspool",
            Audience = "shutterspool-server",
            MetadataAddress = metadataAddress,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Security_options_accept_private_http_metadata_only_with_explicit_opt_in()
    {
        SecurityOptions options = new()
        {
            Authority = "https://identity.example.invalid/realms/shutterspool",
            Audience = "shutterspool-server",
            MetadataAddress = "http://keycloak:8080/realms/shutterspool/.well-known/openid-configuration",
            AllowInsecureMetadataAddress = true,
        };

        options.Validate();
    }
}
