using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Destinations.Local;
using ShutterSpool.Infrastructure.Destinations.WebDav;

namespace ShutterSpool.IntegrationTests;

public sealed class DestinationWorkspaceBoundaryTests
{
    [Fact]
    public void Capability_list_is_operator_owned_and_rejects_unsupported_kinds()
    {
        string[] configured = ["webdav"];
        var capabilities = new DestinationCapabilities(configured);
        configured[0] = "local";

        Assert.True(capabilities.IsEnabled("webdav"));
        Assert.False(capabilities.IsEnabled("local"));
        Assert.Throws<ArgumentException>(() => capabilities.EnsureEnabled("local"));
        Assert.Throws<ArgumentException>(() => new DestinationCapabilities(["unknown"]));
    }

    [Fact]
    public async Task Disabled_adapters_do_not_contact_the_provider_or_read_original_bytes()
    {
        var capabilities = new DestinationCapabilities([]);
        var webdav = new WebDavDestinationDeliveryAdapter(new RejectingClientFactory(), capabilities);
        var local = new LocalDestinationDeliveryAdapter(capabilities);
        var request = new DeliveryRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "original.raw", 1,
            new string('0', 64), "synthetic");

        Assert.Equal("DESTINATION_KIND_DISABLED", (await webdav.TestAsync("https://dav.example.test/", "user", "synthetic", CancellationToken.None)).ErrorCode);
        Assert.Equal(DeliveryResultKind.ConfigurationFailure, (await webdav.WriteAsync(request, Stream.Null, CancellationToken.None)).Kind);
        Assert.Equal(DeliveryReconciliationKind.Uncertain, (await webdav.ReconcileAsync(request, CancellationToken.None)).Kind);
        Assert.Equal("DESTINATION_KIND_DISABLED", (await local.WriteAsync(request, Stream.Null, CancellationToken.None)).ErrorCode);
        Assert.Equal("DESTINATION_KIND_DISABLED", (await local.ReconcileAsync(request, CancellationToken.None)).ErrorCode);
    }

    [Fact]
    public async Task Disabled_kinds_cannot_be_created_or_updated_but_saved_configuration_remains_readable()
    {
        var existing = new DestinationConfiguration(Guid.NewGuid(), "Saved", "local", true, 2,
            "synthetic-root", null, null, false);
        var store = new SavedDestinationStore(existing);
        var management = new DestinationManagementService(store, null!, new DestinationCapabilities(["webdav"]));

        await Assert.ThrowsAsync<ArgumentException>(() => management.CreateAsync("New", "local", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => management.UpdateNameAsync(existing.Id, 2, "Changed", CancellationToken.None));
        Assert.Equal(existing, await management.GetAsync(existing.Id, CancellationToken.None));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void Workspace_paths_and_browsing_cannot_leave_the_assigned_destination_subtree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"shutterspool-destination-boundary-{Guid.NewGuid():N}");
        string destinations = Path.Combine(root, "destinations");
        Directory.CreateDirectory(destinations);
        Directory.CreateDirectory(Path.Combine(destinations, "child"));
        Directory.CreateDirectory(Path.Combine(root, "originals"));
        var workspace = new WorkspaceContext(true);
        workspace.Bind(new(Guid.NewGuid(), "Synthetic", "synthetic", root, 1000, "synthetic"));
        try
        {
            Assert.True(LocalDestinationPaths.IsAllowed(Path.Combine(destinations, "child"), workspace));
            Assert.False(LocalDestinationPaths.IsAllowed(Path.Combine(root, "originals"), workspace));
            Assert.False(LocalDestinationPaths.IsAllowed(Path.Combine(destinations, "..", "originals"), workspace));
            LocalDirectoryListing listing = LocalDirectoryBrowser.Browse(null, CancellationToken.None, destinations).Listing!;
            Assert.Equal(destinations, listing.Path);
            Assert.Null(listing.ParentPath);
            Assert.Equal("child", Assert.Single(listing.Directories).Name);
            Assert.Null(LocalDirectoryBrowser.Browse(root, CancellationToken.None, destinations).Listing);
            Assert.Null(LocalDirectoryBrowser.Browse(Path.Combine(destinations, ".."), CancellationToken.None, destinations).Listing);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Enabled_tenancy_without_a_selected_workspace_fails_closed()
    {
        Assert.Throws<InvalidOperationException>(() => LocalDestinationPaths.AssignedRoot(new WorkspaceContext(true)));
    }

    [Fact]
    public async Task Delivery_and_reconciliation_reject_recorded_roots_outside_the_selected_workspace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"shutterspool-destination-recorded-{Guid.NewGuid():N}");
        var workspace = new WorkspaceContext(true);
        workspace.Bind(new(Guid.NewGuid(), "Synthetic", "synthetic", root, 1000, "synthetic"));
        var adapter = new LocalDestinationDeliveryAdapter(DestinationCapabilities.All, workspace);
        var request = new DeliveryRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "synthetic.raw", 1,
            new string('0', 64), "synthetic", Configuration: new LocalDestinationDeliveryConfiguration(Path.Combine(root, "originals")));

        Assert.Equal("UNSAFE_PATH", (await adapter.WriteAsync(request, Stream.Null, CancellationToken.None)).ErrorCode);
        Assert.Equal("UNSAFE_PATH", (await adapter.ReconcileAsync(request, CancellationToken.None)).ErrorCode);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Workspace_destination_paths_reject_redirected_ancestors()
    {
        string root = Path.Combine(Path.GetTempPath(), $"shutterspool-destination-link-{Guid.NewGuid():N}");
        string destinations = Path.Combine(root, "destinations");
        string outside = Path.Combine(root, "originals");
        Directory.CreateDirectory(destinations);
        Directory.CreateDirectory(outside);
        string link = Path.Combine(destinations, "redirected");
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            { Assert.Skip("This host does not permit creating a directory symlink."); }
            var workspace = new WorkspaceContext(true);
            workspace.Bind(new(Guid.NewGuid(), "Synthetic", "synthetic", root, 1000, "synthetic"));

            Assert.False(LocalDestinationPaths.IsAllowed(Path.Combine(link, "new-child"), workspace));
            Assert.Null(LocalDirectoryBrowser.Browse(link, CancellationToken.None, destinations).Listing);
            Assert.DoesNotContain(LocalDirectoryBrowser.Browse(null, CancellationToken.None, destinations).Listing!.Directories,
                entry => entry.Name == "redirected");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RejectingClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Provider I/O must not be attempted.");
    }

    private sealed class SavedDestinationStore(DestinationConfiguration existing) : IDestinationConfigurationStore
    {
        public int Writes { get; private set; }
        public Task CreateAsync(DestinationConfiguration destination, string? encryptedSecret, CancellationToken cancellationToken)
        { Writes++; return Task.CompletedTask; }
        public Task<DestinationConfiguration?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<DestinationConfiguration?>(existing);
        public Task<DestinationConfiguration[]> ListAsync(int limit, Guid? afterId, CancellationToken cancellationToken) => Task.FromResult(new[] { existing });
        public Task<DestinationConfigurationWrite> UpdateNameAsync(Guid id, long expectedRevision, string name, CancellationToken cancellationToken)
        { Writes++; return Task.FromResult(new DestinationConfigurationWrite(true, existing with { Name = name })); }
        public Task<DestinationRemovalWrite> RemoveAsync(Guid id, long expectedRevision, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
