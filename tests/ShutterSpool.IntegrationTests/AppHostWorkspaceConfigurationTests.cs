using Microsoft.Extensions.Configuration;
using ShutterSpool.AppHost;

namespace ShutterSpool.IntegrationTests;

public sealed class AppHostWorkspaceConfigurationTests
{
    [Fact]
    public void Runtime_forwarding_preserves_destination_capabilities_without_forwarding_operator_credentials()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:shutterspool-control-migration"] = "never-forward",
            ["ShutterSpool:Destinations:EnabledKinds:0"] = "webdav",
            ["ShutterSpool:Destinations:WebDavEgress:AllowedPrivateNetworks:0"] = "192.0.2.0/24",
        }).Build();
        IReadOnlyDictionary<string, string> forwarded = WorkspaceRuntimeConfiguration.ForServer(configuration);
        Assert.Equal("webdav", forwarded["ShutterSpool__Destinations__EnabledKinds__0"]);
        Assert.Equal("192.0.2.0/24", forwarded["ShutterSpool__Destinations__WebDavEgress__AllowedPrivateNetworks__0"]);
        Assert.Equal(2, forwarded.Count);
    }
}
