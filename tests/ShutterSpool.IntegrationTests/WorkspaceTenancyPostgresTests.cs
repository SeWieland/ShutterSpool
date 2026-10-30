using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Sources;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Media;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Workspaces;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Application.Preview;
using ShutterSpool.Server.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ShutterSpool.IntegrationTests;

public sealed partial class WorkspaceTenancyPostgresTests
{
    [Fact]
    public async Task Provisioning_is_explicit_idempotent_and_runtime_scopes_isolate_state_bytes_and_quota()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.ProvisionAsync();
        await fixture.ProvisionAsync();
        await using ServiceProvider provider = fixture.Services();
        await provider.InitializeShutterSpoolAsync(fixture.Configuration, token);
        Guid commonSource = Guid.NewGuid();
        Guid commonOperation = Guid.NewGuid();
        foreach (WorkspaceDefinition workspace in await fixture.RegistryAsync())
        {
            await using AsyncServiceScope scope = fixture.Scope(provider, workspace);
            ShutterSpoolDbContext database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            database.Sources.Add(new SourceEntity { Id = commonSource, Name = "Same name" });
            await database.SaveChangesAsync(token);
            IngestResult accepted = await scope.ServiceProvider.GetRequiredService<IngestService>().AcceptAsync(
                new(commonSource, "same.jpg", new MemoryStream([1, 2, 3]), 3, commonOperation), token);
            Assert.Equal(3, accepted.Asset.Size);
            Assert.Single(await database.Assets.ToArrayAsync(token));
            Assert.True(await scope.ServiceProvider.GetRequiredService<AccessGrantService>()
                .IsAllowedAsync("https://identity.example", workspace.Name, token));
            Assert.False(await scope.ServiceProvider.GetRequiredService<AccessGrantService>()
                .IsAllowedAsync("https://identity.example", "outsider", token));
            var builder = new NpgsqlConnectionStringBuilder(database.Database.GetConnectionString());
            Assert.Equal(4, builder.MaxPoolSize);
            Assert.Equal(0, builder.MinPoolSize);
            ISpoolQuotaStore quota = scope.ServiceProvider.GetRequiredService<ISpoolQuotaStore>();
            SpoolQuotaSettings current = await quota.GetSettingsAsync(token);
            await Assert.ThrowsAsync<ArgumentException>(() => quota.UpdateSettingsAsync(current.Revision,
                current with { QuotaBytes = workspace.AllocatedBytes + 1 }, token));
            await Assert.ThrowsAsync<ArgumentException>(() => quota.UpdateSettingsAsync(current.Revision,
                current with { IsActive = false }, token));
        }
        WorkspaceDefinition first = (await fixture.RegistryAsync())[0];
        await using (AsyncServiceScope scope = fixture.Scope(provider, first))
        {
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            AuthorizationGrant owner = await database.AuthorizationGrants.SingleAsync(token);
            owner.IsActive = false;
            await database.SaveChangesAsync(token);
        }
        await provider.InitializeShutterSpoolAsync(fixture.Configuration, token);
        await using (AsyncServiceScope scope = fixture.Scope(provider, first))
            Assert.False(await scope.ServiceProvider.GetRequiredService<AccessGrantService>()
                .IsAllowedAsync("https://identity.example", first.Name, token));
        await using (AsyncServiceScope unselected = provider.CreateAsyncScope())
            Assert.Throws<InvalidOperationException>(() => unselected.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>());
    }

    [Fact]
    public async Task Tenant_runtime_credentials_cannot_connect_to_another_database_and_wrong_root_or_schema_fails_closed()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        var own = new NpgsqlConnectionStringBuilder(fixture.Configuration.GetConnectionString(workspaces[0].ConnectionReference));
        own.Database = new NpgsqlConnectionStringBuilder(fixture.Configuration.GetConnectionString(workspaces[1].ConnectionReference)).Database;
        await using (var forbidden = new NpgsqlConnection(own.ConnectionString))
            await Assert.ThrowsAsync<PostgresException>(() => forbidden.OpenAsync(TestContext.Current.CancellationToken));
        await using ServiceProvider provider = fixture.Services();
        string markerPath = Path.Combine(workspaces[0].StorageRoot, WorkspaceRuntime.Marker);
        string marker = await File.ReadAllTextAsync(markerPath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(markerPath, marker.Replace(workspaces[0].Id.ToString(), Guid.NewGuid().ToString()), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken));
        await File.WriteAllTextAsync(markerPath, marker, TestContext.Current.CancellationToken);
        await using (var database = fixture.MigrationDatabase(0))
            await database.Database.ExecuteSqlRawAsync("DELETE FROM \"__EFMigrationsHistory\"", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Source_routing_rechecks_current_credentials_and_a_control_conflict_leaves_a_repairable_disabled_source()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        await using ServiceProvider provider = fixture.Services();
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        for (int index = 0; index < workspaces.Length; index++)
        {
            await using AsyncServiceScope scope = fixture.Scope(provider, workspaces[index]);
            var source = new SourceProvisioning(ids[index], "Same name", SourceCurationMode.Delay, 60, "shared-wire-login",
                $"sources/{ids[index]:N}", [1, 2, 3], SourceType.CameraFtpUpload, SourceConnectionProfile.ModernFtps,
                ShutterSpool.Domain.Collections.CollectionName.BuiltInId);
            ISourceConfigurationStore store = scope.ServiceProvider.GetRequiredService<ISourceConfigurationStore>();
            if (index == 0) await store.CreateAsync(source, TestContext.Current.CancellationToken);
            else
            {
                await Assert.ThrowsAsync<SourceUsernameConflictException>(() => store.CreateAsync(source, TestContext.Current.CancellationToken));
                Assert.False((await store.GetAsync(ids[index], TestContext.Current.CancellationToken))!.IsIngressEnabled);
                Assert.True((await store.ReplaceCredentialAsync(ids[index], 0, "repaired-wire-login", [3, 2, 1], TestContext.Current.CancellationToken)).Applied);
                Assert.True((await store.SetIngressEnabledAsync(ids[index], 1, true, TestContext.Current.CancellationToken)).Applied);
            }
        }
        var registry = new PostgresWorkspaceRegistry(fixture.Configuration, new WorkspaceContext(true));
        WorkspaceIngressRoute? route = await registry.FindIngressAsync(WorkspaceIngressHandle.Username("shared-wire-login"), TestContext.Current.CancellationToken);
        Assert.Equal(workspaces[0].Id, route!.Workspace.Id);
        Assert.Equal(ids[0], route.SourceId);
    }

    [Fact]
    public async Task Nonseekable_temporary_copy_reserves_and_releases_only_the_selected_allocation()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        await using ServiceProvider provider = fixture.Services();
        await provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken);
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        await using AsyncServiceScope scope = fixture.Scope(provider, workspaces[0]);
        var context = scope.ServiceProvider.GetRequiredService<WorkspaceContext>();
        var quota = scope.ServiceProvider.GetRequiredService<ISpoolQuotaStore>();
        await using (SeekableMedia copy = await SeekableMediaStream.OpenAsync(new Nonseekable([7, 8, 9]), 3, 3,
            TestContext.Current.CancellationToken, context, quota))
        {
            Assert.Equal(new byte[] { 7, 8, 9 }, Read(copy.Stream));
            Assert.Single(Directory.GetFiles(Path.Combine(workspaces[0].StorageRoot, "incoming", "media-temporary")));
            Assert.False(Directory.Exists(Path.Combine(workspaces[1].StorageRoot, "incoming", "media-temporary")));
            Assert.Equal(3, (await quota.GetSnapshotAsync(TestContext.Current.CancellationToken)).ReservedBytes);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(workspaces[0].StorageRoot, "incoming", "media-temporary")));
        Assert.Equal(0, (await quota.GetSnapshotAsync(TestContext.Current.CancellationToken)).ReservedBytes);
    }

    [Fact]
    public async Task Partial_provisioning_reconciles_the_same_targets_without_duplicate_grants_or_implicit_owner_changes()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await WorkspaceOperator.RunAsync(["--workspace-control-init"], fixture.Configuration, token);
        string original = fixture.Configuration.GetConnectionString("runtime1")!;
        fixture.Configuration["ConnectionStrings:runtime1"] = new NpgsqlConnectionStringBuilder(original) { Password = Guid.NewGuid().ToString("N") }.ConnectionString;
        await Assert.ThrowsAsync<PostgresException>(() => fixture.ProvisionWorkspaceAsync(0));
        Assert.Empty(await fixture.RegistryAsync());
        string ownerKey = $"ShutterSpool:Workspaces:Definitions:{fixture.WorkspaceId(0):D}:OwnerSubject";
        fixture.Configuration[ownerKey] = "different-owner";
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ProvisionWorkspaceAsync(0));
        fixture.Configuration[ownerKey] = "studio0";
        fixture.Configuration["ConnectionStrings:runtime1"] = original;
        await fixture.ProvisionAsync();
        await fixture.ProvisionAsync();
        await using var database = fixture.MigrationDatabase(0);
        Assert.Single(await database.AuthorizationGrants.ToArrayAsync(token));
        Assert.Equal("studio0", (await database.AuthorizationGrants.SingleAsync(token)).Subject);
    }

    [Fact]
    public async Task Maintenance_cannot_run_with_live_writers_and_allocation_changes_preserve_quota_and_media()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        fixture.Configuration["ShutterSpool:Workspaces:WritersStopped"] = "true";
        fixture.Configuration["ShutterSpool:Workspaces:UsableCapacityBytes"] = "3000000";
        WorkspaceDefinition workspace = (await fixture.RegistryAsync())[0];
        await using (ServiceProvider provider = fixture.Services())
        {
            await provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => WorkspaceOperator.RunAsync(["--workspace-upgrade"], fixture.Configuration, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(() => WorkspaceOperator.RunAsync(["--workspace-allocation", workspace.Id.ToString("D"), "1000000", "1500000"], fixture.Configuration, TestContext.Current.CancellationToken));
        }
        await WorkspaceOperator.RunAsync(["--workspace-upgrade"], fixture.Configuration, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => WorkspaceOperator.RunAsync(["--workspace-allocation", workspace.Id.ToString("D"), "1000000", "900000"], fixture.Configuration, TestContext.Current.CancellationToken));
        await WorkspaceOperator.RunAsync(["--workspace-allocation", workspace.Id.ToString("D"), "1000000", "1500000"], fixture.Configuration, TestContext.Current.CancellationToken);
        WorkspaceDefinition changed = (await fixture.RegistryAsync()).Single(item => item.Id == workspace.Id);
        Assert.Equal(1_500_000, changed.AllocatedBytes);
        Assert.Equal(workspace.StorageIdentity, changed.StorageIdentity);
        Assert.Equal(workspace.StorageRoot, changed.StorageRoot);
        await using ServiceProvider restarted = fixture.Services();
        await restarted.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken);
        await using AsyncServiceScope scope = fixture.Scope(restarted, changed);
        Assert.Equal(1_000_000, (await scope.ServiceProvider.GetRequiredService<ISpoolQuotaStore>().GetSettingsAsync(TestContext.Current.CancellationToken)).QuotaBytes);
    }

    [Fact]
    public async Task Existing_unowned_storage_is_preserved_and_never_attached_by_provisioning()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        string root = fixture.Configuration[$"ShutterSpool:Workspaces:Definitions:{fixture.WorkspaceId(0):D}:StorageRoot"]!;
        string original = Path.Combine(root, "unmanaged-original.jpg");
        await File.WriteAllBytesAsync(original, [1, 9, 8], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ProvisionAsync());
        Assert.Equal(new byte[] { 1, 9, 8 }, await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken));
        Assert.Empty(await fixture.RegistryAsync());
    }

    [Theory]
    [InlineData("Disk")]
    [InlineData("Memory")]
    public async Task Preview_bytes_with_identical_ids_are_partitioned_across_scopes_and_cache_loss(string mode)
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        fixture.Configuration["ShutterSpool:Workspaces:UsableCapacityBytes"] = "20000000";
        fixture.Configuration["ShutterSpool:Preview:CacheMode"] = mode;
        for (int index = 0; index < 2; index++)
            fixture.Configuration[$"ShutterSpool:Workspaces:Definitions:{fixture.WorkspaceId(index):D}:AllocatedBytes"] = "10000000";
        await fixture.ProvisionAsync();
        await using ServiceProvider provider = fixture.Services();
        await provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken);
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        Guid capture = Guid.NewGuid(), asset = Guid.NewGuid();
        string key = $"{capture:N}/{asset:N}/grid-480x480.webp";
        byte[] first = Convert.FromBase64String("UklGRi4AAABXRUJQVlA4ICIAAABwAQCdASoCAAMAAUAmJZQCdAFAAAD+/DeBV/fU6D4r4AAA");
        byte[] second = [.. first]; second[^1] = 1;
        byte[][] expected = [first, second];
        for (int index = 0; index < 2; index++)
        {
            await using AsyncServiceScope scope = fixture.Scope(provider, workspaces[index]);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IPreviewCache>().WriteAsync(key, expected[index], TestContext.Current.CancellationToken));
        }
        for (int index = 0; index < 2; index++)
        {
            await using AsyncServiceScope scope = fixture.Scope(provider, workspaces[index]);
            Assert.Equal(expected[index], await scope.ServiceProvider.GetRequiredService<IPreviewCache>().ReadAsync(key, new(480, 480), TestContext.Current.CancellationToken));
        }
        await using (AsyncServiceScope lost = fixture.Scope(provider, workspaces[0]))
            await lost.ServiceProvider.GetRequiredService<IPreviewAssetCacheInvalidator>().RemoveAssetAsync(asset, TestContext.Current.CancellationToken);
        await using (AsyncServiceScope recreate = fixture.Scope(provider, workspaces[0]))
        {
            IPreviewCache cache = recreate.ServiceProvider.GetRequiredService<IPreviewCache>();
            Assert.Null(await cache.ReadAsync(key, new(480, 480), TestContext.Current.CancellationToken));
            Assert.True(await cache.WriteAsync(key, first, TestContext.Current.CancellationToken));
        }
        await using AsyncServiceScope other = fixture.Scope(provider, workspaces[1]);
        Assert.Equal(second, await other.ServiceProvider.GetRequiredService<IPreviewCache>().ReadAsync(key, new(480, 480), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Wrong_connection_reference_is_rejected_before_querying_another_tenant_and_valid_scopes_remain_reusable()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        await using ServiceProvider provider = fixture.Services();
        await provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken);
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        string key = "ConnectionStrings:" + workspaces[0].ConnectionReference;
        string original = fixture.Configuration[key]!;
        fixture.Configuration[key] = fixture.Configuration.GetConnectionString(workspaces[1].ConnectionReference);
        await using (AsyncServiceScope wrong = fixture.Scope(provider, workspaces[0]))
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Assets.CountAsync(TestContext.Current.CancellationToken));
        fixture.Configuration[key] = original;
        foreach (WorkspaceDefinition workspace in workspaces)
        {
            await using AsyncServiceScope valid = fixture.Scope(provider, workspace);
            Assert.Equal(0, await valid.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Assets.CountAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Concurrent_tenant_admission_cannot_borrow_another_workspaces_unused_quota()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        await using ServiceProvider provider = fixture.Services();
        await provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken);
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        await using AsyncServiceScope first = fixture.Scope(provider, workspaces[0]);
        await using AsyncServiceScope second = fixture.Scope(provider, workspaces[1]);
        ISpoolQuotaStore a = first.ServiceProvider.GetRequiredService<ISpoolQuotaStore>();
        ISpoolQuotaStore b = second.ServiceProvider.GetRequiredService<ISpoolQuotaStore>();
        bool[] admitted = await Task.WhenAll(
            a.TryReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1_000_000, 0, ["spool/first"], 1_000_000), TestContext.Current.CancellationToken),
            b.TryReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1_000, 0, ["spool/second"], 1_000), TestContext.Current.CancellationToken));
        Assert.Equal(new[] { true, true }, admitted);
        Assert.False(await a.TryReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 1, 0, ["spool/extra"], 1), TestContext.Current.CancellationToken));
        Assert.Equal(1_000_000, (await a.GetSnapshotAsync(TestContext.Current.CancellationToken)).ReservedBytes);
        Assert.Equal(1_000, (await b.GetSnapshotAsync(TestContext.Current.CancellationToken)).ReservedBytes);
    }

    [Fact]
    public async Task Actual_metadata_worker_rotates_busy_tenants_skips_offline_tenants_and_resumes_durable_work_without_signals()
    {
        await using var fixture = await WorkspaceFixture.CreateAsync();
        await fixture.ProvisionAsync();
        await using ServiceProvider provider = fixture.Services();
        await provider.InitializeShutterSpoolAsync(fixture.Configuration, TestContext.Current.CancellationToken);
        WorkspaceDefinition[] workspaces = await fixture.RegistryAsync();
        Guid sourceId = Guid.NewGuid();
        byte[] jpeg = [0xff, 0xd8, 0xff, 0xe0, 0, 2, 0xff, 0xd9];
        for (int index = 0; index < 2; index++)
        {
            await using AsyncServiceScope scope = fixture.Scope(provider, workspaces[index]);
            var database = scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>();
            database.Sources.Add(new() { Id = sourceId, Name = "Synthetic worker camera" });
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
            for (int item = 0; item < (index == 0 ? 12 : 1); item++)
                await scope.ServiceProvider.GetRequiredService<IngestService>().AcceptAsync(new(sourceId, $"synthetic-{item}.jpg",
                    new MemoryStream(jpeg), jpeg.Length), TestContext.Current.CancellationToken);
        }
        await RunUntilAsync(workspaces[1], 1);
        int processedA = await CompletedAsync(workspaces[0], TestContext.Current.CancellationToken);
        Assert.InRange(processedA, 1, 11);
        await using (AsyncServiceScope scope = fixture.Scope(provider, workspaces[1]))
            await scope.ServiceProvider.GetRequiredService<IngestService>().AcceptAsync(new(sourceId, "second.jpg",
                new MemoryStream(jpeg), jpeg.Length), TestContext.Current.CancellationToken);
        string connectionKey = "ConnectionStrings:" + workspaces[0].ConnectionReference;
        string original = fixture.Configuration[connectionKey]!;
        fixture.Configuration[connectionKey] = new NpgsqlConnectionStringBuilder(original) { Password = "synthetic-unavailable-tenant-password" }.ConnectionString;
        try { await RunUntilAsync(workspaces[1], 2); }
        finally { fixture.Configuration[connectionKey] = original; }
        await RunUntilAsync(workspaces[0], processedA + 1);
        for (int index = 0; index < 2; index++)
        {
            await using AsyncServiceScope scope = fixture.Scope(provider, workspaces[index]);
            AssetEntity[] originals = await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Assets.ToArrayAsync(TestContext.Current.CancellationToken);
            Assert.Equal(index == 0 ? 12 : 2, originals.Length);
            Assert.All(originals, asset => { Assert.Equal(sourceId, asset.SourceId); Assert.Equal("Spooled", asset.State); });
        }

        async Task<int> CompletedAsync(WorkspaceDefinition workspace, CancellationToken token)
        {
            await using AsyncServiceScope scope = fixture.Scope(provider, workspace);
            return await scope.ServiceProvider.GetRequiredService<ShutterSpoolDbContext>().Assets.CountAsync(asset => asset.MetadataState == "Partial", token);
        }
        async Task RunUntilAsync(WorkspaceDefinition watched, int count)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var worker = new MetadataWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<MetadataWorker>.Instance);
            await worker.StartAsync(deadline.Token);
            try
            {
                while (await CompletedAsync(watched, deadline.Token) < count) await Task.Delay(50, deadline.Token);
            }
            finally { await worker.StopAsync(CancellationToken.None); }
        }
    }

    private static byte[] Read(Stream stream) { using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray(); }
    private sealed class Nonseekable(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }

    internal sealed class WorkspaceFixture : IAsyncDisposable
    {
        private readonly ArchitectureTestDatabase[] databases;
        private readonly string[] roles;
        private readonly Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        private readonly Guid[] operations = [Guid.NewGuid(), Guid.NewGuid()];
        private readonly string adminConnection;
        public IConfiguration Configuration { get; }
        private WorkspaceFixture(ArchitectureTestDatabase[] databases, string[] roles, string adminConnection, IConfiguration configuration)
        { this.databases = databases; this.roles = roles; this.adminConnection = adminConnection; Configuration = configuration; }

        public static async Task<WorkspaceFixture> CreateAsync()
        {
            var databases = new List<ArchitectureTestDatabase>();
            var createdRoles = new List<string>();
            string[] roles = ["ws_" + Guid.NewGuid().ToString("N"), "ws_" + Guid.NewGuid().ToString("N"), "ws_" + Guid.NewGuid().ToString("N")];
            string admin = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION")!;
            try
            {
                for (int index = 0; index < roles.Length; index++) databases.Add(await ArchitectureTestDatabase.CreateAsync());
                var values = new Dictionary<string, string?>
                {
                    ["ShutterSpool:Workspaces:Enabled"] = "true", ["ShutterSpool:Workspaces:UsableCapacityBytes"] = "2000000",
                    ["ShutterSpool:Security:Authority"] = "https://identity.example",
                    ["ShutterSpool:Security:EncryptionKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    ["ShutterSpool:Spool:ReservedBytes"] = "0",
                };
                await using var connection = new NpgsqlConnection(admin);
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                for (int index = 0; index < databases.Count; index++)
                {
                    string password = Guid.NewGuid().ToString("N");
                    await using var create = new NpgsqlCommand($"CREATE ROLE \"{roles[index]}\" LOGIN PASSWORD '{password}'", connection);
                    await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    createdRoles.Add(roles[index]);
                    await using var db = new ShutterSpoolDbContext(databases[index].Options);
                    var target = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
                    await using var restrict = new NpgsqlCommand($"REVOKE CONNECT,TEMPORARY ON DATABASE \"{target.Database}\" FROM PUBLIC; GRANT CONNECT ON DATABASE \"{target.Database}\" TO \"{roles[index]}\"", connection);
                    await restrict.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                    string reference = index == 0 ? "shutterspool-control" : "runtime" + index;
                    values["ConnectionStrings:" + reference + (index == 0 ? "-migration" : "-migration")] = target.ConnectionString;
                    target.Username = roles[index]; target.Password = password;
                    values["ConnectionStrings:" + reference] = target.ConnectionString;
                    Directory.CreateDirectory(databases[index].SpoolRoot);
                }
                var fixture = new WorkspaceFixture([.. databases], roles, admin, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
                for (int index = 0; index < 2; index++)
                {
                    string prefix = $"ShutterSpool:Workspaces:Definitions:{fixture.ids[index]:D}:";
                    fixture.Configuration[prefix + "Name"] = "studio" + index;
                    fixture.Configuration[prefix + "StorageIdentity"] = "synthetic-volume-" + fixture.ids[index].ToString("N");
                    fixture.Configuration[prefix + "StorageRoot"] = databases[index + 1].SpoolRoot;
                    fixture.Configuration[prefix + "AllocatedBytes"] = "1000000";
                    fixture.Configuration[prefix + "ConnectionReference"] = "runtime" + (index + 1);
                    fixture.Configuration[prefix + "MigrationConnectionReference"] = "runtime" + (index + 1) + "-migration";
                    fixture.Configuration[prefix + "OwnerIssuer"] = "https://identity.example";
                    fixture.Configuration[prefix + "OwnerSubject"] = "studio" + index;
                    fixture.Configuration[prefix + "SetupOperatorIdentity"] = "synthetic-setup-operator";
                }
                return fixture;
            }
            catch
            {
                // Cleanup must not mask the setup failure or stop at one failed resource.
                foreach (ArchitectureTestDatabase database in databases)
                {
                    try { await database.DisposeAsync(); }
                    catch (Exception) { }
                }
                try
                {
                    await using var connection = new NpgsqlConnection(admin);
                    await connection.OpenAsync(CancellationToken.None);
                    foreach (string role in createdRoles)
                    {
                        try
                        {
                            await using var drop = new NpgsqlCommand($"DROP ROLE \"{role}\"", connection);
                            await drop.ExecuteNonQueryAsync(CancellationToken.None);
                        }
                        catch (Exception) { }
                    }
                }
                catch (Exception) { }
                throw;
            }
        }
        public async Task ProvisionAsync()
        {
            await WorkspaceOperator.RunAsync(["--workspace-control-init"], Configuration, TestContext.Current.CancellationToken);
            for (int index = 0; index < ids.Length; index++)
                await WorkspaceOperator.RunAsync(["--workspace-provision", ids[index].ToString("D"), operations[index].ToString("D")], Configuration, TestContext.Current.CancellationToken);
        }
        public Guid WorkspaceId(int index) => ids[index];
        public Task ProvisionWorkspaceAsync(int index) => WorkspaceOperator.RunAsync(["--workspace-provision", ids[index].ToString("D"), operations[index].ToString("D")], Configuration, TestContext.Current.CancellationToken);
        public Task<WorkspaceDefinition[]> RegistryAsync() => new PostgresWorkspaceRegistry(Configuration, new WorkspaceContext(true)).ListReadyAsync(TestContext.Current.CancellationToken);
        public ServiceProvider Services() => new ServiceCollection().AddLogging().AddShutterSpoolInfrastructure(Configuration).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        public AsyncServiceScope Scope(IServiceProvider provider, WorkspaceDefinition workspace)
        { AsyncServiceScope scope = provider.CreateAsyncScope(); scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(workspace); return scope; }
        public ShutterSpoolDbContext MigrationDatabase(int index) => new(databases[index + 1].Options);
        public async ValueTask DisposeAsync()
        {
            foreach (ArchitectureTestDatabase database in databases) await database.DisposeAsync();
            await using var connection = new NpgsqlConnection(adminConnection);
            await connection.OpenAsync(CancellationToken.None);
            foreach (string role in roles)
            {
                await using var drop = new NpgsqlCommand($"DROP ROLE \"{role}\"", connection);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }
}
