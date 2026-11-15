using ShutterSpool.Infrastructure.Composition;
using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Security;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class DatabaseInitializer(ShutterSpoolDbContext database, ShutterSpool.Application.Workspaces.WorkspaceContext? workspace = null)
{
    private const string KeyCheckValue = "ShutterSpool deployment key check v1";
    public async Task InitializeAsync(string spoolRoot, Func<CancellationToken, Task>? recover = null,
        bool validateOnly = false, CancellationToken cancellationToken = default)
    {
        if (!database.Database.IsRelational())
        {
            if (recover is not null) await recover(cancellationToken);
            return;
        }
        if (validateOnly)
        {
            await Workspaces.WorkspaceRuntime.ValidateDatabaseAsync(database, workspace?.Selected, cancellationToken);
            if (recover is not null) await recover(cancellationToken);
        }
        else await InitializeLockedAsync(spoolRoot, recover, cancellationToken);
    }

    private async Task InitializeLockedAsync(string spoolRoot, Func<CancellationToken, Task>? recover, CancellationToken cancellationToken)
    {
        bool exists = await database.Database.CanConnectAsync(cancellationToken);
        string[] applied = exists ? [.. (await database.Database.GetAppliedMigrationsAsync(cancellationToken))] : [];
        string[] known = [.. database.Database.GetMigrations()];
        if (!applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
            throw new WorkspaceOperator.RefusedException("The application database does not use the supported initial migration baseline. Select a fresh application database and empty spool at separate locations. Preserve the existing database, spool, keys and identity state; startup does not convert or delete them.");
        if (applied.Length == 0)
        {
            if (exists && await database.Database.SqlQuery<int>($"""
                SELECT 1 AS "Value" FROM pg_catalog.pg_class AS relation
                JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
                WHERE left(schema.nspname, 3) <> 'pg_' AND schema.nspname <> 'information_schema'
                    AND relation.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
                    AND NOT (schema.nspname = 'public' AND relation.relname = '__EFMigrationsHistory')
                """).AnyAsync(cancellationToken))
                throw new WorkspaceOperator.RefusedException("The application database is not empty. Select a fresh database and empty spool.");
            if (Directory.Exists(spoolRoot) && Directory.EnumerateFileSystemEntries(spoolRoot).Any())
                throw new WorkspaceOperator.RefusedException("A fresh application database requires an empty spool. Existing originals must remain with their original database.");
        }
        await database.Database.MigrateAsync(cancellationToken);
        if (recover is not null) await recover(cancellationToken);
    }

    public async Task ValidateSecretKeyAsync(ICredentialProtector protector,
        bool enroll = false, CancellationToken cancellationToken = default)
    {
        string? encryptedCheck = await database.Database.SqlQuery<string>($"SELECT key_check AS \"Value\" FROM deployment_key_checks WHERE id = 1")
            .SingleOrDefaultAsync(cancellationToken);
        if (encryptedCheck is not null)
        {
            if (protector.Unprotect(encryptedCheck) != KeyCheckValue)
                throw new WorkspaceOperator.RefusedException("The deployment encryption key does not match the key recorded for this database.");
            return;
        }
        if (!enroll)
            throw new WorkspaceOperator.RefusedException("The workspace has no provisioned encryption-key check. Runtime startup cannot enroll an existing database.");
        if (await database.SourceCredentials.AnyAsync(cancellationToken) || await database.DestinationCredentials.AnyAsync(cancellationToken))
            throw new WorkspaceOperator.RefusedException("Encryption-key enrollment requires fresh provisioning before credentials exist.");
        string marker = protector.Protect(KeyCheckValue);
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO deployment_key_checks (id, key_check) VALUES (1, {marker})", cancellationToken);
    }
}
