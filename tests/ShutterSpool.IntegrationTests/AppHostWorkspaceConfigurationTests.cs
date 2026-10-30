using Microsoft.Extensions.Configuration;
using ShutterSpool.AppHost;

namespace ShutterSpool.IntegrationTests;

public sealed class AppHostWorkspaceConfigurationTests
{
    [Fact]
    public void Runtime_forwarding_includes_explicit_mode_and_secret_file_references_but_no_operator_credentials_or_bootstrap()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ShutterSpool:Workspaces:Enabled"] = "true", ["ShutterSpool:Workspaces:UsableCapacityBytes"] = "1000",
            ["ShutterSpool:Workspaces:WritersStopped"] = "true",
            ["ShutterSpool:Workspaces:Definitions:one:ConnectionReference"] = "tenant-one",
            ["ShutterSpool:Workspaces:Definitions:one:MigrationConnectionReference"] = "tenant-one-migration",
            ["ShutterSpool:Workspaces:Definitions:one:OwnerSubject"] = "private-owner",
            ["ConnectionStrings:shutterspool-controlFile"] = Path.Combine(Path.GetTempPath(), "control.secret"),
            ["ConnectionStrings:tenant-oneFile"] = Path.Combine(Path.GetTempPath(), "tenant.secret"),
            ["ConnectionStrings:tenant-one-migration"] = "must-never-be-forwarded",
        }).Build();
        IReadOnlyDictionary<string, string> forwarded = WorkspaceRuntimeConfiguration.ForServer(configuration);
        Assert.Equal("true", forwarded["ShutterSpool__Workspaces__Enabled"]);
        Assert.Equal("1000", forwarded["ShutterSpool__Workspaces__UsableCapacityBytes"]);
        Assert.Equal(configuration["ConnectionStrings:tenant-oneFile"], forwarded["ConnectionStrings__tenant-oneFile"]);
        Assert.Equal(4, forwarded.Count);
        Assert.DoesNotContain(forwarded, item => item.Key.Contains("migration", StringComparison.OrdinalIgnoreCase)
            || item.Key.Contains("Owner", StringComparison.Ordinal) || item.Key.Contains("WritersStopped", StringComparison.Ordinal));
    }

    [Fact]
    public void Legacy_mode_is_explicit_and_does_not_forward_tenant_secrets()
    {
        IReadOnlyDictionary<string, string> forwarded = WorkspaceRuntimeConfiguration.ForServer(new ConfigurationBuilder().Build());
        Assert.Equal(new Dictionary<string, string> { ["ShutterSpool__Workspaces__Enabled"] = "false" }, forwarded);
    }
}
