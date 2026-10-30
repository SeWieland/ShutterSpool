using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Npgsql;
using ShutterSpool.Application.Workspaces;
using ShutterSpool.Application.Security;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using ShutterSpool.Infrastructure.Workspaces;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Composition;

public static class WorkspaceOperator
{
    private sealed record Provisioning(string Name, string StorageIdentity, string StorageRoot, long AllocatedBytes,
        string ConnectionReference, string MigrationConnectionReference, string OwnerIssuer, string OwnerSubject);

    public static async Task RunAsync(string[] args, IConfiguration configuration, CancellationToken token)
    {
        if (!configuration.GetValue<bool>("ShutterSpool:Workspaces:Enabled"))
            throw new InvalidOperationException("Workspace operator commands require workspace mode.");
        await using var operationLock = new NpgsqlConnection(MigrationConnection(configuration, "shutterspool-control-migration", "shutterspool-control"));
        await operationLock.OpenAsync(token);
        await using var operationTransaction = await operationLock.BeginTransactionAsync(token);
        await using (var serial = new NpgsqlCommand("SELECT pg_advisory_xact_lock(721034250721)", operationLock, operationTransaction))
            await serial.ExecuteNonQueryAsync(token);
        if (args.Length >= 2 && Guid.TryParseExact(args[1], "D", out Guid accessWorkspace)
            && args[0] is "--workspace-access" or "--workspace-recover-owner" or "--workspace-recovery-notification")
        {
            await AccessAsync(args, configuration, accessWorkspace, token);
            return;
        }
        if (args is ["--workspace-control-init"])
        {
            await InitializeControlAsync(configuration, token);
            return;
        }
        if (args is ["--workspace-provision", var workspaceText, var operationText]
            && Guid.TryParseExact(workspaceText, "D", out Guid workspace) && workspace != Guid.Empty
            && Guid.TryParseExact(operationText, "D", out Guid operation) && operation != Guid.Empty)
        {
            using var writers = new WorkspaceWriterLease();
            foreach (WorkspaceDefinition existing in await new PostgresWorkspaceRegistry(configuration, new WorkspaceContext(true)).ListReadyAsync(token))
                writers.Acquire(existing);
            await ProvisionAsync(configuration, workspace, operation, token);
            return;
        }
        if (args is ["--workspace-upgrade"])
        {
            await UpgradeAsync(configuration, token);
            return;
        }
        if (args is ["--workspace-allocation", var allocationId, var expectedText, var nextText]
            && Guid.TryParseExact(allocationId, "D", out Guid selected)
            && long.TryParse(expectedText, out long expected) && long.TryParse(nextText, out long next) && next > 0)
        {
            await ChangeAllocationAsync(configuration, selected, expected, next, token);
            return;
        }
        throw new ArgumentException("Use --workspace-control-init, --workspace-provision, --workspace-upgrade, --workspace-allocation, --workspace-access <workspace GUID>, --workspace-recover-owner <workspace GUID> <operation GUID> <protected request-file path>, or --workspace-recovery-notification <workspace GUID> <operation GUID> <success|failed>.");
    }

    private static async Task AccessAsync(string[] args, IConfiguration configuration, Guid id, CancellationToken token)
    {
        WorkspaceDefinition workspace = await new PostgresWorkspaceRegistry(configuration, new WorkspaceContext(true)).FindReadyAsync(id, token)
            ?? throw new InvalidOperationException("The exact ready workspace is required.");
        WorkspaceRuntime.ValidateRoot(workspace);
        Provisioning provisioning = ReadProvisioning(configuration, id);
        await using ShutterSpoolDbContext database = CreateDatabase(MigrationConnection(configuration,
            provisioning.MigrationConnectionReference, workspace.ConnectionReference));
        Guid tenantId = await database.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM workspace_identity").SingleAsync(token);
        if (tenantId != id || !(await database.Database.GetAppliedMigrationsAsync(token)).SequenceEqual(database.Database.GetMigrations()))
            throw new InvalidOperationException("The tenant identity or schema does not match the selected workspace.");
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
                if (result.Status != "applied") throw new InvalidOperationException("Recovery did not change grants; inspect current authority and reverify before a new operation.");
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

    private static async Task InitializeControlAsync(IConfiguration configuration, CancellationToken token)
    {
        await using var connection = new NpgsqlConnection(MigrationConnection(configuration, "shutterspool-control-migration", "shutterspool-control"));
        await connection.OpenAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(721034250720)", connection, transaction))
            await command.ExecuteNonQueryAsync(token);
        await using (var existing = new NpgsqlCommand("SELECT to_regclass('public.workspace_control_version') IS NOT NULL", connection, transaction))
        {
            if ((bool)(await existing.ExecuteScalarAsync(token))!)
            {
                await using var version = new NpgsqlCommand("SELECT version FROM workspace_control_version WHERE id=1", connection, transaction);
                if (Convert.ToInt32(await version.ExecuteScalarAsync(token)) != 1)
                    throw new InvalidOperationException("The control schema requires a supported explicit upgrade.");
                return;
            }
        }
        await using (var nonempty = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace WHERE left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND r.relkind IN ('r','p','v','m','S','f'))", connection, transaction))
            if ((bool)(await nonempty.ExecuteScalarAsync(token))!) throw new InvalidOperationException("Control initialization requires an empty database.");
        await using (var schema = new NpgsqlCommand("""
            CREATE TABLE workspace_control_version(id integer PRIMARY KEY CHECK(id=1), version integer NOT NULL);
            INSERT INTO workspace_control_version VALUES(1,1);
            CREATE TABLE workspaces(id uuid PRIMARY KEY, name text NOT NULL, storage_identity text NOT NULL UNIQUE,
                storage_root text NOT NULL UNIQUE, allocated_bytes bigint NOT NULL CHECK(allocated_bytes>0),
                connection_reference text NOT NULL UNIQUE, operation_id uuid NOT NULL UNIQUE,
                provisioning jsonb NOT NULL, ready boolean NOT NULL DEFAULT false);
            CREATE TABLE ingress_routes(handle text PRIMARY KEY CHECK(length(handle)<=128),
                workspace_id uuid NOT NULL REFERENCES workspaces(id), source_id uuid NOT NULL);
            REVOKE CREATE ON SCHEMA public FROM PUBLIC;
            REVOKE ALL ON ALL TABLES IN SCHEMA public FROM PUBLIC;
            """, connection, transaction)) await schema.ExecuteNonQueryAsync(token);
        string runtimeRole = new NpgsqlConnectionStringBuilder(PostgresWorkspaceRegistry.ControlConnection(configuration)).Username!;
        await using (var grant = new NpgsqlCommand($"GRANT USAGE ON SCHEMA public TO {Quote(runtimeRole)}; GRANT SELECT ON workspaces,workspace_control_version,ingress_routes TO {Quote(runtimeRole)}; GRANT INSERT,UPDATE ON ingress_routes TO {Quote(runtimeRole)}", connection, transaction))
            await grant.ExecuteNonQueryAsync(token);
        await transaction.CommitAsync(token);
    }

    private static async Task ProvisionAsync(IConfiguration configuration, Guid id, Guid operation, CancellationToken token)
    {
        Provisioning provision = ReadProvisioning(configuration, id);
        var definition = new WorkspaceDefinition(id, provision.Name, provision.StorageIdentity,
            Path.GetFullPath(provision.StorageRoot), provision.AllocatedBytes, provision.ConnectionReference);
        string proof = JsonSerializer.Serialize(provision);
        string migration = MigrationConnection(configuration, provision.MigrationConnectionReference, provision.ConnectionReference);
        await using var control = new NpgsqlConnection(MigrationConnection(configuration, "shutterspool-control-migration", "shutterspool-control"));
        await control.OpenAsync(token);
        await using (var transaction = await control.BeginTransactionAsync(token))
        {
            await using (var serial = new NpgsqlCommand("SELECT pg_advisory_xact_lock(721034250720)", control, transaction))
                await serial.ExecuteNonQueryAsync(token);
            await using var existing = new NpgsqlCommand("SELECT operation_id,provisioning::text,ready FROM workspaces WHERE id=@id", control, transaction);
            existing.Parameters.AddWithValue("id", id);
            bool found;
            await using (var reader = await existing.ExecuteReaderAsync(token))
            {
                found = await reader.ReadAsync(token);
                if (found)
                {
                    if (reader.GetGuid(0) != operation || JsonSerializer.Deserialize<Provisioning>(reader.GetString(1)) != provision)
                        throw new InvalidOperationException("The provisioning operation conflicts with its recorded targets or initial Owner.");
                    if (reader.GetBoolean(2)) { WorkspaceRuntime.ValidateRoot(definition); return; }
                }
            }
            if (!found)
            {
                if (!Directory.Exists(definition.StorageRoot) || Directory.EnumerateFileSystemEntries(definition.StorageRoot).Any())
                    throw new InvalidOperationException("Provisioning requires an existing empty allocated storage root.");
                await using var database = CreateDatabase(migration);
                if (await database.Database.SqlQuery<int>($"""
                    SELECT 1 AS "Value" FROM pg_class r JOIN pg_namespace n ON n.oid=r.relnamespace
                    WHERE left(n.nspname,3)<>'pg_' AND n.nspname<>'information_schema' AND r.relkind IN ('r','p','v','m','S','f')
                    """).AnyAsync(token)) throw new InvalidOperationException("Provisioning requires an empty tenant database.");
                await using var allocation = new NpgsqlCommand("SELECT count(*),coalesce(sum(allocated_bytes),0)::bigint FROM workspaces", control, transaction);
                await using (var reader = await allocation.ExecuteReaderAsync(token))
                {
                    await reader.ReadAsync(token);
                    if (reader.GetInt64(0) >= PostgresWorkspaceRegistry.MaximumWorkspaces
                        || checked(reader.GetInt64(1) + definition.AllocatedBytes) > configuration.GetValue<long>("ShutterSpool:Workspaces:UsableCapacityBytes"))
                        throw new InvalidOperationException("The workspace count or total allocation exceeds the deployment budget.");
                }
                await using var insert = new NpgsqlCommand("INSERT INTO workspaces(id,name,storage_identity,storage_root,allocated_bytes,connection_reference,operation_id,provisioning) VALUES(@id,@name,@identity,@root,@bytes,@connection,@operation,@proof::jsonb)", control, transaction);
                insert.Parameters.AddWithValue("id", id); insert.Parameters.AddWithValue("name", definition.Name);
                insert.Parameters.AddWithValue("identity", definition.StorageIdentity); insert.Parameters.AddWithValue("root", definition.StorageRoot);
                insert.Parameters.AddWithValue("bytes", definition.AllocatedBytes); insert.Parameters.AddWithValue("connection", definition.ConnectionReference);
                insert.Parameters.AddWithValue("operation", operation); insert.Parameters.AddWithValue("proof", proof);
                await insert.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        await using (ShutterSpoolDbContext database = CreateDatabase(migration))
        {
            var initializer = new DatabaseInitializer(database);
            await initializer.InitializeAsync(definition.StorageRoot, token);
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            await database.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS workspace_identity(id uuid PRIMARY KEY, storage_identity text NOT NULL,
                    operation_id uuid NOT NULL, bootstrap_complete boolean NOT NULL DEFAULT false)
                """, token);
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO workspace_identity(id,storage_identity,operation_id) VALUES({id},{definition.StorageIdentity},{operation}) ON CONFLICT DO NOTHING
                """, token);
            Guid identity = await database.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM workspace_identity").SingleAsync(token);
            if (identity != id) throw new InvalidOperationException("The tenant database belongs to another workspace.");
            bool complete = await database.Database.SqlQuery<bool>($"SELECT bootstrap_complete AS \"Value\" FROM workspace_identity WHERE id={id}").SingleAsync(token);
            if (!complete)
            {
                await new EfAccessGrantStore(database).PrepareInitialOwnerAsync(operation, provision.OwnerIssuer, provision.OwnerSubject,
                    configuration[$"ShutterSpool:Workspaces:Definitions:{id:D}:SetupOperatorIdentity"], token);
                SpoolQuotaSettingsEntity quota = await database.SpoolQuotaSettings.SingleAsync(item => item.Id == 1, token);
                quota.IsActive = true; quota.QuotaBytes = definition.AllocatedBytes;
                quota.HighWatermarkBytes = Math.Max(1, definition.AllocatedBytes * 9 / 10);
                quota.RecoveryTargetBytes = definition.AllocatedBytes * 8 / 10;
                quota.StorageMarkerAdoptionAuthorized = true;
                await database.SaveChangesAsync(token);
                await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE workspace_identity SET bootstrap_complete=true WHERE id={id}", token);
            }
            await transaction.CommitAsync(token);
            var protector = new SecretProtector(Options.Create(configuration.GetSection("ShutterSpool:Security").Get<CredentialProtectionOptions>() ?? new()));
            await initializer.ValidateSecretKeyAsync(protector, token);
            string runtimeRole = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(provision.ConnectionReference)).Username!;
            string privileges = $"REVOKE CREATE ON SCHEMA public FROM PUBLIC; GRANT USAGE ON SCHEMA public TO {Quote(runtimeRole)}; GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO {Quote(runtimeRole)}; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO {Quote(runtimeRole)}; REVOKE INSERT,UPDATE,DELETE ON workspace_identity,\"__EFMigrationsHistory\",deployment_key_checks FROM {Quote(runtimeRole)}";
            await database.Database.ExecuteSqlRawAsync(privileges, token);
        }
        string markerPath = Path.Combine(definition.StorageRoot, WorkspaceRuntime.Marker);
        if (!File.Exists(markerPath))
        {
            await using var marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(marker, new WorkspaceRuntime.StorageMarker(id, definition.StorageIdentity, operation), cancellationToken: token);
            marker.Flush(flushToDisk: true);
        }
        WorkspaceRuntime.ValidateRoot(definition);
        await using (ShutterSpoolDbContext runtime = CreateDatabase(configuration.GetConnectionString(provision.ConnectionReference)!))
            await WorkspaceRuntime.ValidateDatabaseAsync(runtime, token, definition);
        WorkspaceDefinition[] established = await new PostgresWorkspaceRegistry(configuration, new WorkspaceContext(true)).ListReadyAsync(token);
        await InfrastructureServices.ValidateWorkspaceInventoryAsync(configuration, [.. established, definition], token);
        await using var ready = new NpgsqlCommand("UPDATE workspaces SET ready=true WHERE id=@id AND operation_id=@operation", control);
        ready.Parameters.AddWithValue("id", id); ready.Parameters.AddWithValue("operation", operation);
        await ready.ExecuteNonQueryAsync(token);
    }

    private static async Task UpgradeAsync(IConfiguration configuration, CancellationToken token)
    {
        if (!configuration.GetValue<bool>("ShutterSpool:Workspaces:WritersStopped"))
            throw new InvalidOperationException("Upgrades require stopped writers and an eligible consistent recovery set.");
        await InitializeControlAsync(configuration, token);
        var registry = new PostgresWorkspaceRegistry(configuration, new WorkspaceContext(true));
        WorkspaceDefinition[] workspaces = await registry.ListReadyAsync(token);
        using var writers = new WorkspaceWriterLease();
        foreach (WorkspaceDefinition workspace in workspaces) writers.Acquire(workspace);
        foreach (WorkspaceDefinition workspace in workspaces)
        {
            WorkspaceRuntime.ValidateRoot(workspace);
            Provisioning provisioning = ReadProvisioning(configuration, workspace.Id);
            await using ShutterSpoolDbContext database = CreateDatabase(MigrationConnection(configuration, provisioning.MigrationConnectionReference, workspace.ConnectionReference));
            string[] applied = [.. await database.Database.GetAppliedMigrationsAsync(token)];
            if (!applied.SequenceEqual(database.Database.GetMigrations().Take(applied.Length), StringComparer.Ordinal))
                throw new InvalidOperationException("A tenant schema has an unsupported migration history.");
            await database.Database.MigrateAsync(token);
            string runtimeRole = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(workspace.ConnectionReference)).Username!;
            string privileges = $"GRANT SELECT,INSERT,UPDATE,DELETE ON ALL TABLES IN SCHEMA public TO {Quote(runtimeRole)}; GRANT USAGE,SELECT ON ALL SEQUENCES IN SCHEMA public TO {Quote(runtimeRole)}; REVOKE INSERT,UPDATE,DELETE ON workspace_identity,\"__EFMigrationsHistory\",deployment_key_checks FROM {Quote(runtimeRole)}";
            await database.Database.ExecuteSqlRawAsync(privileges, token);
        }
    }

    private static async Task ChangeAllocationAsync(IConfiguration configuration, Guid id, long expected, long next, CancellationToken token)
    {
        if (!configuration.GetValue<bool>("ShutterSpool:Workspaces:WritersStopped"))
            throw new InvalidOperationException("Allocation changes require stopped writers and requalified physical storage.");
        var registry = new PostgresWorkspaceRegistry(configuration, new WorkspaceContext(true));
        WorkspaceDefinition[] workspaces = await registry.ListReadyAsync(token);
        WorkspaceDefinition current = workspaces.SingleOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The selected workspace is unavailable.");
        using var writers = new WorkspaceWriterLease();
        foreach (WorkspaceDefinition workspace in workspaces) writers.Acquire(workspace);
        if (current.AllocatedBytes == next) return;
        if (current.AllocatedBytes != expected) throw new InvalidOperationException("The assigned allocation changed; reread operator inventory.");
        if (checked(workspaces.Where(item => item.Id != id).Sum(item => item.AllocatedBytes) + next)
            > configuration.GetValue<long>("ShutterSpool:Workspaces:UsableCapacityBytes"))
            throw new InvalidOperationException("The new allocation exceeds usable deployment storage.");
        var context = new WorkspaceContext(true); context.Bind(current);
        IConfiguration scoped = WorkspaceRuntime.Configure(configuration, context);
        await using ShutterSpoolDbContext database = CreateDatabase(scoped.GetConnectionString("shutterspool")!);
        await WorkspaceRuntime.ValidateDatabaseAsync(database, token, current);
        var quota = new EfSpoolQuotaStore(database, new Spool.FileSystemSpoolAccounting(scoped, TimeProvider.System), TimeProvider.System, context);
        ShutterSpool.Application.Retention.SpoolQuotaSnapshot snapshot = await quota.GetSnapshotAsync(token);
        if (!snapshot.IsKnown || checked(snapshot.UsedBytes + snapshot.ReservedBytes) > next || snapshot.Settings.QuotaBytes > next)
            throw new InvalidOperationException("The new allocation cannot exclude retained bytes, reservations or the saved workspace quota.");
        await using var control = new NpgsqlConnection(MigrationConnection(configuration, "shutterspool-control-migration", "shutterspool-control"));
        await control.OpenAsync(token);
        await using var update = new NpgsqlCommand("UPDATE workspaces SET allocated_bytes=@next WHERE id=@id AND allocated_bytes=@expected AND ready", control);
        update.Parameters.AddWithValue("next", next); update.Parameters.AddWithValue("id", id); update.Parameters.AddWithValue("expected", expected);
        if (await update.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("The workspace allocation changed concurrently.");
    }

    private static Provisioning ReadProvisioning(IConfiguration configuration, Guid id)
    {
        Provisioning provision = configuration.GetSection($"ShutterSpool:Workspaces:Definitions:{id:D}").Get<Provisioning>()
            ?? throw new InvalidOperationException("The exact workspace provisioning definition is required.");
        if (string.IsNullOrWhiteSpace(provision.Name) || provision.Name.Length > 256 || string.IsNullOrWhiteSpace(provision.StorageIdentity)
            || !Path.IsPathFullyQualified(provision.StorageRoot) || provision.AllocatedBytes < 10
            || !Uri.TryCreate(provision.OwnerIssuer, UriKind.Absolute, out Uri? issuer)
            || issuer.Scheme != "https" && (issuer.Scheme != "http" || !issuer.IsLoopback || configuration.GetValue<bool>("ShutterSpool:Deployment:Packaged"))
            || !string.Equals(provision.OwnerIssuer.TrimEnd('/'), configuration["ShutterSpool:Security:Authority"]?.TrimEnd('/'), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(provision.OwnerSubject) || provision.OwnerSubject.Length > 512)
            throw new InvalidOperationException("The workspace definition and independently verified initial Owner from the exact configured OIDC issuer are required.");
        return provision;
    }

    private static string MigrationConnection(IConfiguration configuration, string migrationReference, string runtimeReference)
    {
        var migration = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(migrationReference)
            ?? throw new InvalidOperationException("Separate operator migration credentials are required."));
        var runtime = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString(runtimeReference)
            ?? throw new InvalidOperationException("The runtime credential reference is required."));
        if (migration.Host != runtime.Host || migration.Port != runtime.Port || migration.Database != runtime.Database
            || migration.Username == runtime.Username || string.IsNullOrWhiteSpace(migration.Database) || string.IsNullOrWhiteSpace(runtime.Username))
            throw new InvalidOperationException("Migration and restricted runtime credentials must target the same explicit database with different roles.");
        return migration.ConnectionString;
    }

    private static ShutterSpoolDbContext CreateDatabase(string connection) => new(new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options);
    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
}
