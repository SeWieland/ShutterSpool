using Microsoft.Extensions.Configuration;
using ShutterSpool.AppHost;

namespace ShutterSpool.IntegrationTests;

public sealed class AppHostWorkspaceConfigurationTests
{
    [Fact]
    public void Development_configuration_prepares_one_workspace_once_without_replacing_existing_state()
    {
        string root = Path.Combine(Path.GetTempPath(), "shutterspool-apphost-" + Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ShutterSpool:Development:RootPath"] = root,
                ["Parameters:shutterspool-administrator-subject"] = "verified-subject",
            }).Build();
            Assert.Equal(root, WorkspaceRuntimeConfiguration.PrepareDevelopment(configuration, 8082));
            var manifest = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "appsettings.json")).Build();
            Assert.Equal("verified-subject", manifest["ShutterSpool:Workspaces:Definitions:studio:OwnerSubject"]);
            Assert.Equal("http://localhost:8082/realms/shutterspool", manifest["ShutterSpool:Workspaces:Definitions:studio:OwnerIssuer"]);
            Assert.True(Directory.Exists(manifest["ShutterSpool:Workspaces:Definitions:studio:StorageRoot"]));
            File.WriteAllText(Path.Combine(root, "appsettings.json"), "preserve-existing-configuration");
            WorkspaceRuntimeConfiguration.PrepareDevelopment(configuration, 9999);
            Assert.Equal("preserve-existing-configuration", File.ReadAllText(Path.Combine(root, "appsettings.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

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
