using ShutterSpool.Application.Workspaces;
using ShutterSpool.Server.Security;
using Microsoft.AspNetCore.Http;
using ShutterSpool.Infrastructure.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;

namespace ShutterSpool.IntegrationTests;

public sealed class WorkspaceScopeTests
{
    [Fact]
    public async Task Registry_is_available_before_selection_and_tenant_database_requires_its_bound_workspace()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:shutterspool-control"] = "Host=localhost;Database=control;Username=control_reader",
            ["ConnectionStrings:Workspaces:studio:Runtime"] = "Host=localhost;Database=studio;Username=studio_writer",
        }).Build();
        await using ServiceProvider provider = new ServiceCollection().AddLogging()
            .AddShutterSpoolInfrastructure(configuration).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var selected = new WorkspaceDefinition(Guid.NewGuid(), "studio", "volume-a", Path.GetTempPath(), 100);
        provider.GetRequiredService<WorkspaceInventory>().Freeze([selected]);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IWorkspaceRegistry registry = scope.ServiceProvider.GetRequiredService<IWorkspaceRegistry>();

        Assert.Equal(selected, await registry.FindReadyAsync(selected.Id, TestContext.Current.CancellationToken));
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>());
        scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(selected);

        ShutterSpoolDbContext tenant = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
        Assert.Equal("studio", tenant.Database.GetDbConnection().Database);
        await using WorkspaceControlDbContext control = await scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<WorkspaceControlDbContext>>().CreateDbContextAsync(TestContext.Current.CancellationToken);
        Assert.Equal("control", control.Database.GetDbConnection().Database);
    }

    [Fact]
    public void Workspace_scope_cannot_be_rebound_or_read_without_selection()
    {
        var context = new WorkspaceContext();
        Assert.Throws<InvalidOperationException>(() => context.Required);
        var workspace = new WorkspaceDefinition(Guid.NewGuid(), "Studio", "volume-a", "/workspace-a", 100);
        context.Bind(workspace);
        Assert.Same(workspace, context.Required);
        Assert.Throws<InvalidOperationException>(() => context.Bind(workspace with { Id = Guid.NewGuid() }));
    }

    [Fact]
    public void Provisioning_proof_compares_named_values_despite_outer_and_target_json_order()
    {
        var manifest = new WorkspaceManifest("/allocated/studio", 100, "https://issuer.example", "initial-owner")
        {
            ConnectionString = "Host=localhost;Port=5432;Database=studio;Username=runtime;Password=synthetic",
        };
        const string reordered = """
            {
              "OwnerSubject": "initial-owner",
              "RuntimeTarget": "{ \"Username\": \"runtime\", \"Database\": \"studio\", \"Port\": 5432, \"Host\": \"localhost\" }",
              "AllocatedBytes": 100,
              "OwnerIssuer": "https://issuer.example",
              "StorageRoot": "/allocated/studio"
            }
            """;
        Assert.True(manifest.MatchesProof(reordered));
        Assert.True(manifest.MatchesProof(manifest.Proof));
        Assert.False((manifest with { OwnerSubject = "replacement-owner" }).MatchesProof(reordered));
        Assert.False((manifest with { AllocatedBytes = 101 }).MatchesProof(reordered));
        Assert.False((manifest with { ConnectionString = "Host=localhost;Port=5432;Database=other;Username=runtime" }).MatchesProof(reordered));
    }

    [Theory]
    [InlineData("/management/captures", false)]
    [InlineData("/media/previews/x", false)]
    [InlineData("/live/changes", true)]
    [InlineData("/live/changes/negotiate", true)]
    [InlineData("/live/status", true)]
    public void Query_selection_is_limited_to_live_transports(string path, bool accepted)
    {
        var request = new DefaultHttpContext().Request;
        request.Path = path;
        Guid id = Guid.NewGuid();
        request.QueryString = new QueryString($"?workspace={id:D}");
        Assert.Equal(accepted, WorkspaceSelection.TryRead(request, out Guid selected));
        if (accepted) Assert.Equal(id, selected);
    }

    [Fact]
    public void Missing_duplicate_and_conflicting_selectors_fail_closed()
    {
        var request = new DefaultHttpContext().Request;
        request.Path = "/live/changes";
        Assert.False(WorkspaceSelection.TryRead(request, out _));
        Guid id = Guid.NewGuid();
        request.Headers[WorkspaceSelection.Header] = id.ToString("D");
        request.QueryString = new QueryString($"?workspace={Guid.NewGuid():D}");
        Assert.False(WorkspaceSelection.TryRead(request, out _));
        request.QueryString = new QueryString($"?workspace={id:D}");
        Assert.True(WorkspaceSelection.TryRead(request, out _));
        request.Headers.Append(WorkspaceSelection.Header, id.ToString("D"));
        Assert.False(WorkspaceSelection.TryRead(request, out _));
    }
}
