using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Application.Security;
using ShutterSpool.Infrastructure.Composition;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Workspaces;

internal sealed class WorkspaceMaintenance(IConfiguration configuration)
{
    private string ControlMigration => WorkspaceManifest.ValidateMigrationConnection(
        configuration.GetConnectionString("shutterspool-control-migration"), PostgresWorkspaceRegistry.ControlConnection(configuration));

    internal async Task RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length >= 2 && args[0] is "--workspace-access" or "--workspace-recover-owner" or "--workspace-recovery-notification")
        {
            await AccessAsync(args, token);
            return;
        }
        // Serialize aggregate allocation decisions and resumable provisioning across operator processes.
        await using var serial = new NpgsqlConnection(ControlMigration);
        await serial.OpenAsync(token);
        await using var transaction = await serial.BeginTransactionAsync(token);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(721034250721)", serial, transaction))
            await command.ExecuteNonQueryAsync(token);
        await using WorkspaceControlDbContext control = PostgresWorkspaceRegistry.Database(ControlMigration);
        if (args is ["--workspace-control-init"])
        {
            await InitializeControlAsync(control, token);
            return;
        }
        using var writers = new WorkspaceWriterLease();
        WorkspaceDefinition[] ready = await new PostgresWorkspaceRegistry(configuration, new WorkspaceContext()).ListReadyAsync(token);
        foreach (WorkspaceDefinition workspace in ready) writers.Acquire(workspace);
        if (args is ["--workspace-provision", var name]) await ProvisionAsync(control, name, token);
        else if (args is ["--workspace-upgrade"]) await UpgradeAsync(ready, token);
        else if (args is ["--workspace-allocation", var selected, var expectedText, var nextText]
            && long.TryParse(expectedText, out long expected) && long.TryParse(nextText, out long next) && next >= 10)
            await ChangeAllocationAsync(control, ready, selected, expected, next, token);
        else throw new ArgumentException("Use --workspace-control-init, --workspace-provision <name>, --workspace-upgrade, --workspace-allocation <name> <expected bytes> <new bytes>, --workspace-access <name>, --workspace-recover-owner <name> <operation GUID> <protected request-file path>, or --workspace-recovery-notification <name> <operation GUID> <success|failed>.");
    }

    private async Task AccessAsync(string[] args, CancellationToken token)
    {
        string name = WorkspaceManifest.CanonicalName(configuration, args[1]);
        WorkspaceDefinition workspace = (await new PostgresWorkspaceRegistry(configuration, new WorkspaceContext()).ListReadyAsync(token))
            .SingleOrDefault(item => item.Name == name) ?? throw new WorkspaceOperator.RefusedException("The exact ready workspace is required.");
        WorkspaceManifest manifest = WorkspaceManifest.Read(configuration, name);
        await using ShutterSpoolDbContext database = CreateDatabase(manifest.MigrationConnection);
        await WorkspaceRuntime.ValidateIdentityAsync(database, workspace, token);
        var store = new EfAccessGrantStore(database);
        var display = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        display.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        if (args is ["--workspace-access", _])
        {
            Console.WriteLine(JsonSerializer.Serialize(await store.InspectAsync(token), display));
            return;
        }
        if (args.Length == 4 && Guid.TryParseExact(args[2], "D", out Guid operation) && operation != Guid.Empty)
        {
            if (args[0] == "--workspace-recover-owner")
            {
                string path = Path.GetFullPath(args[3]);
                if (!Path.IsPathFullyQualified(args[3]) || !File.Exists(path) || new FileInfo(path).Length > 16_384
                    || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Use a protected absolute recovery request-file path of at most 16 KiB.");
                WorkspaceOwnerRecovery recovery = JsonSerializer.Deserialize<WorkspaceOwnerRecovery>(await File.ReadAllTextAsync(path, token),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException("The recovery request is required.");
                if (!string.Equals(recovery.TargetIssuer, configuration["ShutterSpool:Security:Authority"]?.TrimEnd('/'), StringComparison.Ordinal))
                    throw new ArgumentException("The independently verified target must use the configured public OIDC issuer.");
                AccessChangeResult result = await store.RecoverOwnerAsync(operation, recovery, token);
                Console.WriteLine(JsonSerializer.Serialize(result, display));
                if (result.Status != "applied") throw new WorkspaceOperator.RefusedException("Recovery did not change grants; inspect current authority and reverify before a new operation.");
                return;
            }
            if (args[0] == "--workspace-recovery-notification")
            {
                await store.RecordRecoveryNotificationAsync(operation, args[3], token);
                return;
            }
        }
        throw new ArgumentException("Provide the exact workspace access command arguments.");
    }

    private async Task InitializeControlAsync(WorkspaceControlDbContext control, CancellationToken token)
    {
        string[] applied = [.. await control.Database.GetAppliedMigrationsAsync(token)];
        if (!applied.SequenceEqual(control.Database.GetMigrations().Take(applied.Length), StringComparer.Ordinal))
            throw new WorkspaceOperator.RefusedException("The existing control schema is incompatible; select a fresh control database without changing existing state.");
        if (applied.Length == 0 && await HasObjectsAsync(control, token))
            throw new WorkspaceOperator.RefusedException("Control initialization requires an empty database. Existing state is preserved.");
        await control.Database.MigrateAsync(token);
        string role = new NpgsqlConnectionStringBuilder(PostgresWorkspaceRegistry.ControlConnection(configuration)).Username!;
        string privileges = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA public TO {Quote(role)}; GRANT SELECT ON ALL TABLES IN SCHEMA public TO {Quote(role)}; GRANT INSERT ON ingress_routes TO {Quote(role)}";
        await control.Database.ExecuteSqlRawAsync(privileges, token);
    }

    private async Task ProvisionAsync(WorkspaceControlDbContext control, string name, CancellationToken token)
    {
        name = WorkspaceManifest.CanonicalName(configuration, name);
        WorkspaceManifest manifest = WorkspaceManifest.Read(configuration, name);
        string migration = manifest.MigrationConnection;
        WorkspaceRecord? record = await control.Workspaces.SingleOrDefaultAsync(item => item.Name == name, token);
        if (record is not null && !manifest.MatchesProof(record.Provisioning))
            throw new WorkspaceOperator.RefusedException("The named workspace conflicts with its recorded targets, allocation or initial Owner. Existing state is preserved.");
        if (record is null)
        {
            if (!Directory.Exists(manifest.StorageRoot) || Directory.EnumerateFileSystemEntries(manifest.StorageRoot).Any())
                throw new WorkspaceOperator.RefusedException("Provisioning requires an existing empty allocated storage root.");
            await using var empty = CreateDatabase(migration);
            if (await HasObjectsAsync(empty, token)) throw new WorkspaceOperator.RefusedException("Provisioning requires an empty tenant database.");
            int count = await control.Workspaces.CountAsync(token);
            long allocated = await control.Workspaces.SumAsync(item => item.AllocatedBytes, token);
            if (count >= PostgresWorkspaceRegistry.MaximumWorkspaces || checked(allocated + manifest.AllocatedBytes)
                > configuration.GetValue<long>("ShutterSpool:Workspaces:UsableCapacityBytes"))
                throw new WorkspaceOperator.RefusedException("The workspace count or total allocation exceeds the deployment budget.");
            record = new WorkspaceRecord
            {
                Id = Guid.NewGuid(), Name = name, StorageIdentity = Guid.NewGuid().ToString("N"), OperationId = Guid.NewGuid(),
                StorageRoot = manifest.StorageRoot, AllocatedBytes = manifest.AllocatedBytes,
                RuntimeTarget = WorkspaceManifest.Target(manifest.ConnectionString), Provisioning = manifest.Proof,
            };
            control.Workspaces.Add(record);
            await control.SaveChangesAsync(token);
        }
        WorkspaceDefinition definition = record.Definition;
        if (record.Ready)
        {
            WorkspaceRuntime.ValidateRoot(definition);
            await using var runtime = CreateDatabase(manifest.ConnectionString);
            await WorkspaceRuntime.ValidateDatabaseAsync(runtime, token, definition);
            return;
        }
        await using (ShutterSpoolDbContext database = CreateDatabase(migration))
        {
            var initializer = new DatabaseInitializer(database);
            await initializer.InitializeAsync(definition.StorageRoot, token);
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            WorkspaceIdentityEntity? identity = await database.WorkspaceIdentities.SingleOrDefaultAsync(token);
            if (identity is null)
            {
                identity = new WorkspaceIdentityEntity { Id = record.Id, StorageIdentity = record.StorageIdentity, OperationId = record.OperationId };
                database.WorkspaceIdentities.Add(identity);
            }
            if (identity.Id != record.Id || identity.StorageIdentity != record.StorageIdentity || identity.OperationId != record.OperationId)
                throw new WorkspaceOperator.RefusedException("The tenant database belongs to another workspace or provisioning operation.");
            if (!identity.BootstrapComplete)
            {
                await new EfAccessGrantStore(database).PrepareInitialOwnerAsync(record.OperationId, manifest.OwnerIssuer, manifest.OwnerSubject,
                    configuration[$"ShutterSpool:Workspaces:Definitions:{name}:SetupOperatorIdentity"], token);
                SpoolQuotaSettingsEntity quota = await database.SpoolQuotaSettings.SingleAsync(item => item.Id == 1, token);
                quota.IsActive = true; quota.QuotaBytes = definition.AllocatedBytes;
                quota.HighWatermarkBytes = Math.Max(1, definition.AllocatedBytes * 9 / 10);
                quota.RecoveryTargetBytes = definition.AllocatedBytes * 8 / 10;
                quota.StorageMarkerAdoptionAuthorized = true;
                identity.BootstrapComplete = true;
                await database.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            var protector = new SecretProtector(Options.Create(configuration.GetSection("ShutterSpool:Security").Get<CredentialProtectionOptions>() ?? new()));
            await initializer.ValidateSecretKeyAsync(protector, token, enroll: true);
            await GrantRuntimeAsync(database, manifest.ConnectionString, token);
        }
        string markerPath = Path.Combine(definition.StorageRoot, WorkspaceRuntime.Marker);
        if (!File.Exists(markerPath))
        {
            await using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(marker, new WorkspaceRuntime.StorageMarker(record.Id, record.StorageIdentity, record.OperationId), cancellationToken: token);
            marker.Flush(flushToDisk: true);
        }
        WorkspaceRuntime.ValidateRoot(definition);
        await using (var runtime = CreateDatabase(manifest.ConnectionString))
            await WorkspaceRuntime.ValidateDatabaseAsync(runtime, token, definition);
        WorkspaceDefinition[] established = await new PostgresWorkspaceRegistry(configuration, new WorkspaceContext()).ListReadyAsync(token);
        await InfrastructureServices.ValidateWorkspaceInventoryAsync(configuration, [.. established, definition], token);
        record.Ready = true;
        await control.SaveChangesAsync(token);
    }

    private async Task UpgradeAsync(WorkspaceDefinition[] ready, CancellationToken token)
    {
        foreach (WorkspaceDefinition workspace in ready)
        {
            WorkspaceManifest manifest = WorkspaceManifest.Read(configuration, workspace.Name);
            await using var database = CreateDatabase(manifest.MigrationConnection);
            string[] applied = [.. await database.Database.GetAppliedMigrationsAsync(token)];
            if (!applied.SequenceEqual(database.Database.GetMigrations().Take(applied.Length), StringComparer.Ordinal))
                throw new WorkspaceOperator.RefusedException("A tenant schema has an unsupported migration history.");
            await database.Database.MigrateAsync(token);
            await GrantRuntimeAsync(database, manifest.ConnectionString, token);
        }
    }

    private static Task GrantRuntimeAsync(ShutterSpoolDbContext database, string connection, CancellationToken token)
    {
        string role = new NpgsqlConnectionStringBuilder(connection).Username!;
        string privileges = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA public TO {Quote(role)}; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO {Quote(role)}; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO {Quote(role)}; REVOKE INSERT,UPDATE,DELETE ON workspace_identity,\"__EFMigrationsHistory\",deployment_key_checks FROM {Quote(role)}";
        return database.Database.ExecuteSqlRawAsync(privileges, token);
    }

    private async Task ChangeAllocationAsync(WorkspaceControlDbContext control, WorkspaceDefinition[] ready,
        string name, long expected, long next, CancellationToken token)
    {
        name = WorkspaceManifest.CanonicalName(configuration, name);
        WorkspaceRecord record = await control.Workspaces.SingleAsync(item => item.Name == name && item.Ready, token);
        WorkspaceManifest manifest = WorkspaceManifest.Read(configuration, name);
        if (manifest.AllocatedBytes != next) throw new WorkspaceOperator.RefusedException("Set the canonical manifest allocation to the new qualified allocation before applying it.");
        if (record.AllocatedBytes == next) return;
        if (record.AllocatedBytes != expected) throw new WorkspaceOperator.RefusedException("The assigned allocation changed; reread operator inventory.");
        if (checked(await control.Workspaces.Where(item => item.Id != record.Id).SumAsync(item => item.AllocatedBytes, token) + next)
            > configuration.GetValue<long>("ShutterSpool:Workspaces:UsableCapacityBytes"))
            throw new WorkspaceOperator.RefusedException("The new allocation exceeds usable deployment storage.");
        WorkspaceDefinition current = record.Definition;
        var context = new WorkspaceContext(); context.Bind(current);
        IConfiguration scoped = WorkspaceRuntime.Configure(configuration, context);
        await using var database = CreateDatabase(manifest.ConnectionString);
        await WorkspaceRuntime.ValidateDatabaseAsync(database, token, current);
        var quota = new EfSpoolQuotaStore(database, new Spool.FileSystemSpoolAccounting(scoped, TimeProvider.System), TimeProvider.System, context);
        var snapshot = await quota.GetSnapshotAsync(token);
        if (!snapshot.IsKnown || checked(snapshot.UsedBytes + snapshot.ReservedBytes) > next || snapshot.Settings.QuotaBytes > next)
            throw new WorkspaceOperator.RefusedException("The new allocation cannot exclude retained bytes, reservations or the saved workspace quota.");
        if (!(manifest with { AllocatedBytes = expected }).MatchesProof(record.Provisioning))
            throw new WorkspaceOperator.RefusedException("Allocation maintenance cannot change workspace targets or initial Owner.");
        record.AllocatedBytes = next;
        record.Provisioning = manifest.Proof;
        await control.SaveChangesAsync(token);
    }

    private static async Task<bool> HasObjectsAsync(DbContext database, CancellationToken token) => await database.Database.SqlQuery<int>($"""
        SELECT 1 AS "Value" FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace
        WHERE left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND r.relkind IN ('r','p','v','m','S','f')
        AND NOT(n.nspname='public' AND r.relname='__EFMigrationsHistory')
        """).AnyAsync(token);
    private static ShutterSpoolDbContext CreateDatabase(string connection) => new(new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options);
    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
