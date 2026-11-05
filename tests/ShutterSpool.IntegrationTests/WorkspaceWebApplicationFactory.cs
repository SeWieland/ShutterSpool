using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Server.Security;

namespace ShutterSpool.IntegrationTests;

public sealed class WorkspaceWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private WorkspaceTenancyPostgresTests.WorkspaceFixture fixture = null!;
    public WorkspaceDefinition Workspace { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        fixture = await WorkspaceTenancyPostgresTests.WorkspaceFixture.CreateAsync(1);
        fixture.Configuration["ShutterSpool:Security:AllowedOrigins:0"] = "http://localhost";
        await fixture.ProvisionAsync();
        Workspace = Assert.Single(await fixture.RegistryAsync());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(fixture.Configuration.AsEnumerable()));
        builder.ConfigureLogging(logging =>
        {
            if (OperatingSystem.IsWindows()) logging.AddFilter<EventLogLoggerProvider>(_ => false);
        });
        builder.ConfigureTestServices(services => services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Workspace.StorageRoot, "test-host-keys")))
            .UseEphemeralDataProtectionProvider());
    }

    public HttpClient CreateWorkspaceClient(WebApplicationFactory<Program>? server = null)
    {
        HttpClient client = (server ?? this).CreateClient();
        client.DefaultRequestHeaders.Add(WorkspaceSelection.Header, Workspace.Id.ToString());
        return client;
    }

    public AsyncServiceScope CreateWorkspaceScope(WebApplicationFactory<Program> server) => fixture.Scope(server.Services, Workspace);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (fixture is not null) await fixture.DisposeAsync();
    }
}
