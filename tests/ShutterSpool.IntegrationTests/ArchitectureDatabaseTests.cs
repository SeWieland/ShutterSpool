using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Options;
using Npgsql;
using ShutterSpool.Infrastructure.Security;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Domain.Sources;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class ArchitectureDatabaseTests
{
    [Fact]
    public void Current_model_matches_the_saved_migration_snapshot()
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>()
            .UseNpgsql("Host=localhost;Database=shutterspool_model_check;Username=unused;Password=unused")
            .Options;
        using var database = new ShutterSpoolDbContext(options);
        IDesignTimeModel current = database.GetService<IDesignTimeModel>();
        IMigrationsAssembly assembly = database.GetService<IMigrationsAssembly>();
        IMigrationsModelDiffer differ = database.GetService<IMigrationsModelDiffer>();
        IModel snapshot = database.GetService<IModelRuntimeInitializer>().Initialize(assembly.ModelSnapshot!.Model, designTime: true);
        string[] differences = [.. differ.GetDifferences(snapshot.GetRelationalModel(), current.Model.GetRelationalModel()).Select(operation => operation.ToString() ?? operation.GetType().Name)];
        Assert.True(differences.Length == 0, string.Join(Environment.NewLine, differences));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Release_waits_for_routing_before_locking_capture(bool automatic)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        CancellationToken token = deadline.Token;
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var gate = new ShutterSpoolDbContext(fixture.Options);
        await new DatabaseInitializer(gate).InitializeAsync(fixture.SpoolRoot, token);
        Guid id = Guid.NewGuid();
        gate.Captures.Add(new CaptureEntity { Id = id, SourceId = Guid.NewGuid(), NormalizedStem = "synthetic", ReleaseDueAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        await gate.SaveChangesAsync(token);
        await using var transaction = await gate.Database.BeginTransactionAsync(token);
        await gate.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", token);
        await using var releaseContext = new ShutterSpoolDbContext(fixture.Options);
        var release = new EfCaptureCurationStore(releaseContext, TimeProvider.System);
        Task pending = automatic ? release.ReleaseDueOneAsync(DateTimeOffset.UtcNow, token) : release.ReleaseNowAsync(id, 0, token);
        try
        {
            while (!await gate.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND NOT granted
                    AND database = (SELECT oid FROM pg_database WHERE datname = current_database())) AS "Value"
                """).SingleAsync(token))
                await Task.Delay(20, token);
            // This fails with lock_not_available if release took the Capture row first.
            await gate.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM captures WHERE \"Id\" = {id} FOR UPDATE NOWAIT", token);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await pending;
        }
    }

    [Fact]
    public async Task Fresh_install_initializes_one_history_and_restart_keeps_originals()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await new DatabaseInitializer(database).InitializeAsync(fixture.SpoolRoot, TestContext.Current.CancellationToken);
        Assert.Equal("20261025211318_InitialCreate", Assert.Single(database.Database.GetMigrations()));
        Assert.Equal(database.Database.GetMigrations(), await database.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.False(database.Database.HasPendingModelChanges());
        Directory.CreateDirectory(fixture.SpoolRoot);
        string original = Path.Combine(fixture.SpoolRoot, "synthetic-original");
        await File.WriteAllBytesAsync(original, [1, 2, 3], TestContext.Current.CancellationToken);
        await new DatabaseInitializer(database).InitializeAsync(fixture.SpoolRoot, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_fresh_startups_serialize_schema_and_recovery()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var first = new ShutterSpoolDbContext(fixture.Options);
        await using var second = new ShutterSpoolDbContext(fixture.Options);
        var recoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRecoveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task FirstRecovery(CancellationToken _) { recoveryEntered.SetResult(); return releaseRecovery.Task; }
        Task SecondRecovery(CancellationToken _) { secondRecoveryEntered.SetResult(); return Task.CompletedTask; }

        Task firstStartup = new DatabaseInitializer(first).InitializeAsync(fixture.SpoolRoot, TestContext.Current.CancellationToken, FirstRecovery);
        await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Task secondStartup = new DatabaseInitializer(second).InitializeAsync(fixture.SpoolRoot, TestContext.Current.CancellationToken, SecondRecovery);
        Task completed = await Task.WhenAny(secondRecoveryEntered.Task, Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
        bool recoveryWasSerialized = !ReferenceEquals(secondRecoveryEntered.Task, completed);
        releaseRecovery.TrySetResult();
        await Task.WhenAll(firstStartup, secondStartup);

        Assert.True(recoveryWasSerialized, "A second startup entered recovery while the first startup held the database startup lock.");
        Assert.Equal(first.Database.GetMigrations(), await first.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.True(secondRecoveryEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task Packaged_startup_rejects_an_encryption_key_that_cannot_open_saved_credentials()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        var initializer = new DatabaseInitializer(database);
        CancellationToken token = TestContext.Current.CancellationToken;
        await initializer.InitializeAsync(fixture.SpoolRoot, token);
        var correct = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        Guid destination = Guid.NewGuid();
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO destinations (\"Id\", \"Name\", \"Kind\", \"Enabled\") VALUES ({destination}, 'synthetic-key-check', 'local', true)", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO destination_credentials (\"Id\", \"DestinationId\", \"EncryptedSecret\") VALUES ({Guid.NewGuid()}, {destination}, {correct.Protect("synthetic-secret")})", token);
        var wrong = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) }));

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => initializer.ValidateSecretKeyAsync(wrong, token));
    }

    [Fact]
    public async Task Key_check_marker_detects_a_changed_key_and_corruption_after_source_only_enrollment()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        var initializer = new DatabaseInitializer(database);
        CancellationToken token = TestContext.Current.CancellationToken;
        await initializer.InitializeAsync(fixture.SpoolRoot, token);
        var enrolled = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));
        var changed = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()) }));
        Guid sourceId = Guid.NewGuid();
        byte[] verifier = enrolled.CreateVerifier("synthetic-source-password");
        database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-source-key-check", SourceType = SourceType.CameraFtpUpload, ConnectionProfile = SourceConnectionProfile.ModernFtps });
        database.SourceCredentials.Add(new SourceCredentialEntity { SourceId = sourceId, Username = "synthetic-source-key-check", VirtualRoot = "/", SecretVerifier = verifier });
        await database.SaveChangesAsync(token);
        Assert.False(changed.Verify("synthetic-source-password", verifier));
        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.ValidateSecretKeyAsync(enrolled, token));
        Assert.False(await HasDeploymentKeyCheckAsync(database, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.ValidateSecretKeyAsync(enrolled, token,
            _ => Task.FromException(new InvalidOperationException("synthetic invalid suppression proof"))));
        Assert.False(await HasDeploymentKeyCheckAsync(database, token));

        bool proofRan = false;
        await initializer.ValidateSecretKeyAsync(enrolled, token, _ =>
        {
            proofRan = true;
            return Task.CompletedTask;
        });
        Assert.True(proofRan);
        Assert.True(await HasDeploymentKeyCheckAsync(database, token));

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => initializer.ValidateSecretKeyAsync(changed, token));
        await initializer.ValidateSecretKeyAsync(enrolled, token);
        string marker = await database.Database.SqlQuery<string>($"SELECT key_check AS \"Value\" FROM deployment_key_checks WHERE id = 1").SingleAsync(token);
        byte[] corrupted = Convert.FromBase64String(marker);
        corrupted[^1] ^= 1;
        string damagedMarker = Convert.ToBase64String(corrupted);
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE deployment_key_checks SET key_check = {damagedMarker} WHERE id = 1", token);

        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => initializer.ValidateSecretKeyAsync(enrolled, token));
    }

    private static Task<bool> HasDeploymentKeyCheckAsync(ShutterSpoolDbContext database, CancellationToken cancellationToken) =>
        database.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM deployment_key_checks WHERE id = 1) AS \"Value\"").SingleAsync(cancellationToken);

    [Theory]
    [InlineData("20261018225415_M216SourceConnectionProfiles")]
    [InlineData("20261023010000_M212Collections")]
    [InlineData("20990101000000_UnknownSchema")]
    public async Task Historical_or_unknown_history_is_rejected_without_recovery_or_changes(string migration)
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await database.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
            CREATE TABLE retained_state (id integer PRIMARY KEY, encrypted_key text NOT NULL);
            INSERT INTO retained_state VALUES (1, 'synthetic-encrypted-key');
            """, token);
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"__EFMigrationsHistory\" VALUES ({migration}, '10.0.4')", token);
        Directory.CreateDirectory(fixture.SpoolRoot);
        string original = Path.Combine(fixture.SpoolRoot, "synthetic-original");
        await File.WriteAllBytesAsync(original, [4, 5, 6], token);
        bool recovered = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseInitializer(database)
            .InitializeAsync(fixture.SpoolRoot, token, _ => { recovered = true; return Task.CompletedTask; }));

        Assert.False(recovered);
        Assert.Equal([migration], await database.Database.GetAppliedMigrationsAsync(token));
        Assert.Equal("synthetic-encrypted-key", await database.Database.SqlQuery<string>($"SELECT encrypted_key AS \"Value\" FROM retained_state WHERE id = 1").SingleAsync(token));
        Assert.Equal(2, await database.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_tables WHERE schemaname = 'public'").SingleAsync(token));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(original, token));
    }

    [Theory]
    [InlineData("CREATE VIEW retained_view AS SELECT 42 AS value")]
    [InlineData("CREATE SEQUENCE retained_sequence")]
    [InlineData("CREATE SCHEMA retained; CREATE TABLE retained.originals (id integer)")]
    [InlineData("CREATE SCHEMA pgdata; CREATE TABLE pgdata.originals (id integer)")]
    public async Task Unversioned_database_objects_are_rejected_before_initialization(string sql)
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await database.Database.ExecuteSqlRawAsync(sql, token);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseInitializer(database).InitializeAsync(fixture.SpoolRoot, token));

        Assert.False(await database.Database.SqlQuery<bool>($"SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL AS \"Value\"").SingleAsync(token));
        Assert.False(Directory.Exists(fixture.SpoolRoot));
    }

    [Fact]
    public async Task Direct_migration_rejects_historical_state_and_downgrade_preserves_current_state()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await new DatabaseInitializer(database).InitializeAsync(fixture.SpoolRoot, token);
        await database.Database.ExecuteSqlRawAsync("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = '20261023010000_M212Collections'", token);

        PostgresException rejection = await Assert.ThrowsAsync<PostgresException>(() => database.Database.MigrateAsync(token));
        Assert.Equal(PostgresErrorCodes.RaiseException, rejection.SqlState);
        Assert.Equal("20261023010000_M212Collections", Assert.Single(await database.Database.GetAppliedMigrationsAsync(token)));
        Assert.Single(await database.Collections.ToListAsync(token));

        await database.Database.ExecuteSqlRawAsync("UPDATE \"__EFMigrationsHistory\" SET \"MigrationId\" = '20261025211318_InitialCreate'", token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.GetService<IMigrator>().MigrateAsync("0", token));
        Assert.Equal("20261025211318_InitialCreate", Assert.Single(await database.Database.GetAppliedMigrationsAsync(token)));
        Assert.Single(await database.Collections.ToListAsync(token));
    }

    [Fact]
    public async Task Initial_schema_preserves_key_custody_and_collection_constraints()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        await new DatabaseInitializer(database).InitializeAsync(fixture.SpoolRoot, token);

        PostgresException invalidKeyId = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync(
            "INSERT INTO deployment_key_checks (id, key_check) VALUES (2, 'synthetic')", token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, invalidKeyId.SqlState);
        await database.Database.ExecuteSqlRawAsync("INSERT INTO deployment_key_checks (id, key_check) VALUES (1, 'synthetic')", token);
        PostgresException duplicateKey = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync(
            "INSERT INTO deployment_key_checks (id, key_check) VALUES (1, 'replacement')", token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicateKey.SqlState);

        PostgresException secondBuiltIn = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync("""
            INSERT INTO collections ("Id", "Name", "Path", "Identity", "Revision", "IsBuiltIn")
            VALUES ('10000000-0000-0000-0000-000000000000', 'Second', 'Second', 'SECOND', 0, true)
            """, token));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, secondBuiltIn.SqlState);
        Guid source = Guid.NewGuid();
        database.Sources.Add(new SourceEntity { Id = source, Name = "synthetic" });
        await database.SaveChangesAsync(token);
        PostgresException referencedCollection = await Assert.ThrowsAsync<PostgresException>(() => database.Database.ExecuteSqlRawAsync(
            "DELETE FROM collections", token));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, referencedCollection.SqlState);
    }

    [Fact]
    public async Task Fresh_install_seeds_safe_quota_and_retention_state_once()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        CancellationToken token = TestContext.Current.CancellationToken;
        var initializer = new DatabaseInitializer(database);
        await initializer.InitializeAsync(fixture.SpoolRoot, token);

        SpoolQuotaSettingsEntity settings = Assert.Single(await database.SpoolQuotaSettings.ToListAsync(token));
        Assert.False(settings.IsActive);
        Assert.False(settings.StorageMarkerAdoptionAuthorized);
        Assert.Null(settings.QuotaBytes);
        SpoolQuotaObservationEntity observation = Assert.Single(await database.SpoolQuotaObservations.ToListAsync(token));
        Assert.False(observation.IsKnown);
        Assert.Equal("not-yet-reconciled", observation.UnknownReason);
        Assert.Null(observation.StorageMarker);
        Assert.Equal("Normal", Assert.Single(await database.RetentionPressureStates.ToListAsync(token)).State);
        RetentionPolicyEntity policy = Assert.Single(await database.RetentionPolicies.ToListAsync(token));
        using var json = System.Text.Json.JsonDocument.Parse(policy.PolicyJson);
        Assert.False(json.RootElement.GetProperty("automaticRemovalEnabled").GetBoolean());
        Assert.Equal(1, policy.Revision);
        Assert.Equal(policy.PolicyJson, Assert.Single(await database.RetentionPolicyHistory.ToListAsync(token)).PolicyJson);

        settings.Revision = 4;
        await database.SaveChangesAsync(token);
        await initializer.InitializeAsync(fixture.SpoolRoot, token);
        database.ChangeTracker.Clear();
        Assert.Equal(4, Assert.Single(await database.SpoolQuotaSettings.ToListAsync(token)).Revision);
        Assert.Single(await database.RetentionPolicyHistory.ToListAsync(token));
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_schema_is_rejected_before_any_new_tables_are_created(bool migrationHistory)
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await database.Database.ExecuteSqlRawAsync("CREATE TABLE legacy_originals (id integer PRIMARY KEY)", TestContext.Current.CancellationToken);
        if (migrationHistory)
            await database.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "__EFMigrationsHistory" ("MigrationId" varchar(150) PRIMARY KEY, "ProductVersion" varchar(32) NOT NULL);
                INSERT INTO "__EFMigrationsHistory" VALUES ('20260918204126_InitialIngest', '10.0.4');
                """, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseInitializer(database)
            .InitializeAsync(fixture.SpoolRoot, TestContext.Current.CancellationToken));
        Assert.False(await database.Database.SqlQuery<bool>($"SELECT to_regclass('public.assets') IS NOT NULL AS \"Value\"")
            .SingleAsync(TestContext.Current.CancellationToken));
        Assert.True(await database.Database.SqlQuery<bool>($"SELECT to_regclass('public.legacy_originals') IS NOT NULL AS \"Value\"")
            .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fresh_database_rejects_an_existing_spool_without_changing_either()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Directory.CreateDirectory(fixture.SpoolRoot);
        string original = Path.Combine(fixture.SpoolRoot, "synthetic-original");
        await File.WriteAllBytesAsync(original, [4, 5, 6], TestContext.Current.CancellationToken);
        await using var database = new ShutterSpoolDbContext(fixture.Options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DatabaseInitializer(database)
            .InitializeAsync(fixture.SpoolRoot, TestContext.Current.CancellationToken));
        Assert.Empty(await database.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken));
    }
}

// Each fixture owns only its randomly named database and temporary spool directory.
internal sealed class ArchitectureTestDatabase(string name, NpgsqlConnection admin, DbContextOptions<ShutterSpoolDbContext> options) : IAsyncDisposable
{
    public DbContextOptions<ShutterSpoolDbContext> Options { get; } = options;
    public string SpoolRoot { get; } = Path.Combine(Path.GetTempPath(), name);

    public static async Task<ArchitectureTestDatabase> CreateAsync()
    {
        string? connection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
        {
            if (Environment.GetEnvironmentVariable("SHUTTERSPOOL_REQUIRE_POSTGRES_TESTS") == "true")
                throw new InvalidOperationException("The required PostgreSQL test connection is missing.");
            throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION for PostgreSQL integration checks.");
        }
        string name = $"m004_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnection(connection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new(name, admin, new DbContextOptionsBuilder<ShutterSpoolDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder(connection) { Database = name }.ConnectionString).Options);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
        await admin.DisposeAsync();
        if (Directory.Exists(SpoolRoot)) Directory.Delete(SpoolRoot, recursive: true);
    }
}
