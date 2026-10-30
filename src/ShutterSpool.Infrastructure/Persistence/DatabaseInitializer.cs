using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class DatabaseInitializer(ShutterSpoolDbContext database, ShutterSpool.Application.Workspaces.WorkspaceContext? workspace = null)
{
    private const string KeyCheckValue = "ShutterSpool deployment key check v1";
    public async Task InitializeAsync(string spoolRoot, CancellationToken cancellationToken, Func<CancellationToken, Task>? recover = null,
        bool validateOnly = false)
    {
        if (!database.Database.IsRelational())
        {
            if (recover is not null) await recover(cancellationToken);
            return;
        }
        await using var startupLock = new NpgsqlConnection(database.Database.GetConnectionString());
        await startupLock.OpenAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock(721034250719)", startupLock))
            await command.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            if (validateOnly)
            {
                await Workspaces.WorkspaceRuntime.ValidateDatabaseAsync(database, cancellationToken, workspace?.Selected);
                if (recover is not null) await recover(cancellationToken);
            }
            else await InitializeLockedAsync(spoolRoot, cancellationToken, recover);
        }
        finally
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(721034250719)", startupLock);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task InitializeLockedAsync(string spoolRoot, CancellationToken cancellationToken, Func<CancellationToken, Task>? recover)
    {
        bool exists = await database.Database.CanConnectAsync(cancellationToken);
        string[] applied = exists ? [.. (await database.Database.GetAppliedMigrationsAsync(cancellationToken))] : [];
        string[] known = [.. database.Database.GetMigrations()];
        if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
            throw new InvalidOperationException("The application database does not use the supported initial migration baseline. Select a fresh application database and empty spool at separate locations. Preserve the existing database, spool, keys and identity state; startup does not convert or delete them.");
        if (applied.Length == 0)
        {
            if (exists && await database.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM pg_catalog.pg_class AS relation
                JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
                WHERE left(schema.nspname, 3) <> 'pg_' AND schema.nspname <> 'information_schema'
                    AND relation.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
                    AND NOT (schema.nspname = 'public' AND relation.relname = '__EFMigrationsHistory')
                """).AnyAsync(cancellationToken))
                throw new InvalidOperationException("The application database is not empty. Select a fresh database and empty spool.");
            if (Directory.Exists(spoolRoot) && Directory.EnumerateFileSystemEntries(spoolRoot).Any())
                throw new InvalidOperationException("A fresh application database requires an empty spool. Existing originals must remain with their original database.");
        }
        await database.Database.MigrateAsync(cancellationToken);
        if (recover is not null) await recover(cancellationToken);
    }

    public async Task ValidateSecretKeyAsync(ICredentialProtector protector, CancellationToken cancellationToken,
        Func<CancellationToken, Task>? proveLegacyKeyAsync = null)
    {
        string? encryptedCheck = await database.Database.SqlQuery<string>($"SELECT key_check AS \"Value\" FROM deployment_key_checks WHERE id = 1")
            .SingleOrDefaultAsync(cancellationToken);
        if (encryptedCheck is not null)
        {
            if (protector.Unprotect(encryptedCheck) != KeyCheckValue)
                throw new InvalidOperationException("The deployment encryption key does not match the key recorded for this database.");
            return;
        }
        string[] encryptedCredentials = [.. await database.DestinationCredentials.AsNoTracking()
            .Select(credential => credential.EncryptedSecret).ToListAsync(cancellationToken)];
        foreach (string encrypted in encryptedCredentials)
            _ = protector.Unprotect(encrypted);
        if (encryptedCredentials.Length == 0
            && await database.SourceCredentials.AsNoTracking().AnyAsync(cancellationToken))
        {
            if (proveLegacyKeyAsync is null)
                throw new InvalidOperationException("This existing Source-only database has no decryptable key evidence. Provide valid current suppression restore evidence before enrolling the packaged encryption key.");
            await proveLegacyKeyAsync(cancellationToken);
        }
        string marker = protector.Protect(KeyCheckValue);
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO deployment_key_checks (id, key_check) VALUES (1, {marker})", cancellationToken);
    }
}
