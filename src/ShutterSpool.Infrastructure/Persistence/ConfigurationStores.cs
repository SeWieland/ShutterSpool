using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Destinations;
using ShutterSpool.Application.Security;
using ShutterSpool.Application.Sources;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Domain.Collections;
using ShutterSpool.Infrastructure.Destinations.WebDav;
using System.Text.Json;
using ShutterSpool.Application.Workspaces;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfSourceConfigurationStore(ShutterSpoolDbContext database, IWorkspaceRegistry? registry = null,
    WorkspaceContext? workspace = null) : ISourceConfigurationStore
{
    public Task<SourceManagementConfiguration[]> ListAsync(int limit, string? afterName, Guid? afterId, CancellationToken token)
    {
        var query = from source in database.Sources.AsNoTracking()
            join credential in database.SourceCredentials.AsNoTracking() on source.Id equals credential.SourceId
            join collection in database.Collections.AsNoTracking() on source.CollectionId equals collection.Id
            where !source.IsArchived && (afterName == null || afterId == null || string.Compare(source.Name, afterName) > 0
                || (source.Name == afterName && source.Id.CompareTo(afterId.Value) > 0))
            orderby source.Name, source.Id
            select new SourceManagementConfiguration(source.Id, source.Name, source.CurationMode,
                source.CurationDelaySeconds, credential.Username, credential.CredentialGeneration,
                credential.IsIngressEnabled, source.ConfigurationRevision, source.IsArchived, source.SourceType, source.ConnectionProfile,
                source.CollectionId, collection.Name);
        return query.Take(limit + 1).ToArrayAsync(token);
    }

    public Task<SourceManagementConfiguration?> GetAsync(Guid id, CancellationToken token) => SourceQuery(id)
        .SingleOrDefaultAsync(token);

    public async Task<SourceConfigurationWrite> UpdateConfigurationAsync(Guid id, long expectedRevision,
        string name, SourceCurationMode mode, int delaySeconds, SourceType sourceType, SourceConnectionProfile connectionProfile,
        Guid collectionId, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockCollectionAssignmentsAsync(token);
        if (!await database.Collections.AnyAsync(item => item.Id == collectionId, token))
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token));
        }
        SourceCredentialEntity? credential = await LockCredentialAsync(id, token);
        SourceEntity? source = credential is null ? null : await LockSourceAsync(id, token);
        if (source is null || credential is null) return new(false, null);
        if (source.IsArchived || source.ConfigurationRevision != expectedRevision)
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token));
        }
        source.Name = name;
        source.CollectionId = collectionId;
        source.CurationMode = mode;
        source.CurationDelaySeconds = delaySeconds;
        if (source.SourceType != sourceType || source.ConnectionProfile != connectionProfile)
        {
            source.SourceType = sourceType;
            source.ConnectionProfile = connectionProfile;
            credential.CredentialGeneration = checked(credential.CredentialGeneration + 1);
        }
        source.ConfigurationRevision++;
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ConfigurationConflictException(); }
        await transaction.CommitAsync(token);
        return new(true, await GetAsync(id, token));
    }

    public async Task<SourceConfigurationWrite> ReplaceCredentialAsync(Guid id, long expectedRevision, string username, byte[] verifier,
        CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        SourceCredentialEntity? credential = await LockCredentialAsync(id, token);
        SourceEntity? source = credential is null ? null : await LockSourceAsync(id, token);
        if (source is null || credential is null) return new(false, null);
        if (source.IsArchived || source.ConfigurationRevision != expectedRevision)
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token));
        }
        if (credential.Username == username
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(verifier, credential.SecretVerifier))
            throw new UnchangedSourceCredentialException();
        if (registry is not null) await registry.RegisterIngressAsync(WorkspaceIngressHandle.Username(username), id, token);
        credential.Username = username;
        credential.SecretVerifier = verifier;
        credential.CredentialGeneration = checked(credential.CredentialGeneration + 1);
        source.ConfigurationRevision++;
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new SourceUsernameConflictException(); }
        await transaction.CommitAsync(token);
        return new(true, await GetAsync(id, token));
    }

    public async Task<SourceConfigurationWrite> SetIngressEnabledAsync(Guid id, long expectedRevision, bool enabled,
        CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        SourceCredentialEntity? credential = await LockCredentialAsync(id, token);
        SourceEntity? source = credential is null ? null : await LockSourceAsync(id, token);
        if (source is null || credential is null) return new(false, null);
        if (source.IsArchived || source.ConfigurationRevision != expectedRevision)
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token));
        }
        if (credential.IsIngressEnabled != enabled)
        {
            if (enabled && registry is not null) await registry.RegisterIngressAsync(WorkspaceIngressHandle.Username(credential.Username), id, token);
            credential.IsIngressEnabled = enabled;
            credential.CredentialGeneration = checked(credential.CredentialGeneration + 1);
            source.ConfigurationRevision++;
            await database.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return new(true, await GetAsync(id, token));
    }

    public async Task<SourceRemovalWrite> RemoveAsync(Guid id, long expectedRevision, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        SourceCredentialEntity? credential = await LockCredentialAsync(id, token);
        SourceEntity? source = credential is null ? null : await LockSourceAsync(id, token);
        if (source is null || credential is null) return new(false, false, null);
        if (source.ConfigurationRevision != expectedRevision)
        {
            await transaction.CommitAsync(token);
            return new(false, false, await GetAsync(id, token));
        }

        bool used = source.IsArchived || await database.CameraLogins.AnyAsync(item => item.SourceId == id, token)
            || await database.CameraUploadAdmissions.AnyAsync(item => item.SourceId == id, token)
            || await database.Operations.AnyAsync(item => item.SourceId == id, token)
            || await database.Assets.AnyAsync(item => item.SourceId == id, token)
            || await database.Captures.AnyAsync(item => item.SourceId == id, token);
        if (used)
        {
            if (!source.IsArchived)
            {
                source.IsArchived = true;
                credential.IsIngressEnabled = false;
                credential.CredentialGeneration = checked(credential.CredentialGeneration + 1);
                source.ConfigurationRevision++;
                await database.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return new(true, false, await GetAsync(id, token));
        }

        database.SourceCredentials.Remove(credential);
        database.Sources.Remove(source);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true, true, null);
    }

    public async Task CreateAsync(SourceProvisioning source, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockCollectionAssignmentsAsync(cancellationToken);
        if (!await database.Collections.AnyAsync(item => item.Id == source.CollectionId, cancellationToken))
            throw new ArgumentException("Choose an existing Collection.", nameof(source));
        var sourceEntity = new SourceEntity { Id = source.Id, Name = source.Name, CurationMode = source.CurationMode,
            CurationDelaySeconds = source.DelaySeconds, SourceType = source.SourceType, ConnectionProfile = source.ConnectionProfile,
            CollectionId = source.CollectionId };
        var credentialEntity = new SourceCredentialEntity
        {
            SourceId = source.Id, Username = source.Username, VirtualRoot = source.VirtualRoot, SecretVerifier = source.SecretVerifier,
            IsIngressEnabled = workspace?.Enabled != true,
        };
        database.Sources.Add(sourceEntity);
        database.SourceCredentials.Add(credentialEntity);
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres)
        {
            database.Entry(sourceEntity).State = EntityState.Detached;
            database.Entry(credentialEntity).State = EntityState.Detached;
            if (postgres.ConstraintName == "IX_source_credentials_Username")
                throw new SourceUsernameConflictException();
            throw new ConfigurationConflictException();
        }
        await transaction.CommitAsync(cancellationToken);
        if (workspace?.Enabled == true)
        {
            await transaction.DisposeAsync();
            // A failed control write leaves a visible disabled Source whose credentials can be repaired.
            await registry!.RegisterIngressAsync(WorkspaceIngressHandle.Username(source.Username), source.Id, cancellationToken);
            credentialEntity.IsIngressEnabled = true;
            await database.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<SourcePolicy?> UpdatePolicyAsync(Guid id, SourceCurationMode mode, int delaySeconds, CancellationToken cancellationToken)
    {
        SourceEntity? source = await database.Sources.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (source is null || source.IsArchived) return null;
        source.CurationMode = mode;
        source.CurationDelaySeconds = delaySeconds;
        source.ConfigurationRevision++;
        await database.SaveChangesAsync(cancellationToken);
        return new(source.Id, source.CurationMode, source.CurationDelaySeconds);
    }

    public async Task<bool> RegenerateAsync(Guid id, byte[] verifier, CancellationToken cancellationToken)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        SourceCredentialEntity? credential = await LockCredentialAsync(id, cancellationToken);
        if (credential is null) return false;
        SourceEntity source = await LockSourceAsync(id, cancellationToken) ?? throw new InvalidOperationException("Source disappeared during credential rotation.");
        if (source.IsArchived) return false;
        credential.SecretVerifier = verifier;
        credential.CredentialGeneration = checked(credential.CredentialGeneration + 1);
        source.ConfigurationRevision++;
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<SourceIngressState?> SetIngressEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        SourceCredentialEntity? credential = await LockCredentialAsync(id, cancellationToken);
        if (credential is null) return null;
        SourceEntity source = await LockSourceAsync(id, cancellationToken) ?? throw new InvalidOperationException("Source disappeared during ingress update.");
        if (source.IsArchived) return null;
        if (credential.IsIngressEnabled != enabled)
        {
            if (enabled && registry is not null) await registry.RegisterIngressAsync(WorkspaceIngressHandle.Username(credential.Username), id, cancellationToken);
            credential.IsIngressEnabled = enabled;
            credential.CredentialGeneration = checked(credential.CredentialGeneration + 1);
            source.ConfigurationRevision++;
            await database.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return new(id, credential.CredentialGeneration, credential.IsIngressEnabled);
    }

    private Task<SourceCredentialEntity?> LockCredentialAsync(Guid id, CancellationToken cancellationToken) => database.Database.IsRelational()
        ? database.SourceCredentials.FromSqlInterpolated($"SELECT * FROM source_credentials WHERE \"SourceId\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
        : database.SourceCredentials.SingleOrDefaultAsync(item => item.SourceId == id, cancellationToken);

    private Task<SourceEntity?> LockSourceAsync(Guid id, CancellationToken token) => database.Database.IsRelational()
        ? database.Sources.FromSqlInterpolated($"SELECT * FROM sources WHERE \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(token)
        : database.Sources.SingleOrDefaultAsync(item => item.Id == id, token);

    private IQueryable<SourceManagementConfiguration> SourceQuery(Guid? id = null, string? afterName = null, Guid? afterId = null) =>
        from source in database.Sources.AsNoTracking()
        join credential in database.SourceCredentials.AsNoTracking() on source.Id equals credential.SourceId
        join collection in database.Collections.AsNoTracking() on source.CollectionId equals collection.Id
        where (id == null || source.Id == id)
            && (afterName == null || afterId == null || string.Compare(source.Name, afterName) > 0
                || (source.Name == afterName && source.Id.CompareTo(afterId.Value) > 0))
        select new SourceManagementConfiguration(source.Id, source.Name, source.CurationMode,
            source.CurationDelaySeconds, credential.Username, credential.CredentialGeneration,
            credential.IsIngressEnabled, source.ConfigurationRevision, source.IsArchived, source.SourceType, source.ConnectionProfile,
            source.CollectionId, collection.Name);

    private Task<int> LockCollectionAssignmentsAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-collection-assignment', 0))", token);

    public Task<SourceCredential?> FindCredentialAsync(string username, CancellationToken cancellationToken) =>
        (from credential in database.SourceCredentials.AsNoTracking()
            join source in database.Sources.AsNoTracking() on credential.SourceId equals source.Id
            where credential.Username == username
            select new SourceCredential(credential.SourceId, credential.Username, credential.VirtualRoot,
                credential.SecretVerifier, credential.CredentialGeneration, credential.IsIngressEnabled, source.SourceType, source.ConnectionProfile))
        .SingleOrDefaultAsync(cancellationToken);
}

internal sealed class EfDestinationConfigurationStore(ShutterSpoolDbContext database, DestinationCapabilities? capabilities = null) : IDestinationConfigurationStore
{
    public async Task CreateAsync(DestinationConfiguration destination, string? encryptedSecret, CancellationToken cancellationToken)
    {
        (capabilities ?? DestinationCapabilities.All).EnsureEnabled(destination.Kind);
        database.Destinations.Add(new DestinationEntity
        {
            Id = destination.Id, Name = destination.Name, Kind = destination.Kind, Enabled = destination.Enabled,
        });
        if (encryptedSecret is not null)
            database.DestinationCredentials.Add(new DestinationCredentialEntity { DestinationId = destination.Id, EncryptedSecret = encryptedSecret });
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConfigurationConflictException();
        }
    }

    public Task<DestinationConfiguration[]> ListAsync(int limit, Guid? afterId, CancellationToken token)
    {
        var query = from destination in database.Destinations.AsNoTracking()
            join local in database.LocalDestinations.AsNoTracking() on destination.Id equals local.DestinationId into locals
            from local in locals.DefaultIfEmpty()
            join webdav in database.WebDavDestinations.AsNoTracking() on destination.Id equals webdav.DestinationId into webdavConfigs
            from webdav in webdavConfigs.DefaultIfEmpty()
            where !destination.IsArchived && (afterId == null || destination.Id.CompareTo(afterId.Value) > 0)
            orderby destination.Id
            select new DestinationConfiguration(destination.Id, destination.Name, destination.Kind, destination.Enabled,
                destination.ConfigurationRevision, local == null ? null : local.RootPath,
                webdav == null ? null : webdav.CollectionUrl, webdav == null ? null : webdav.Username,
                database.DestinationCredentials.Any(credential => credential.DestinationId == destination.Id), destination.IsArchived);
        return query.Take(limit + 1).ToArrayAsync(token);
    }

    public Task<DestinationConfiguration?> GetAsync(Guid id, CancellationToken cancellationToken) => DestinationQuery(id)
        .SingleOrDefaultAsync(cancellationToken);

    public async Task<DestinationConfigurationWrite> UpdateNameAsync(Guid id, long expectedRevision, string name,
        CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        DestinationEntity? destination = await database.Destinations.FromSqlInterpolated($"SELECT * FROM destinations WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(token);
        if (destination is null) return new(false, null);
        if (destination.IsArchived || destination.ConfigurationRevision != expectedRevision)
        {
            await transaction.CommitAsync(token);
            return new(false, await GetAsync(id, token));
        }
        destination.Name = name;
        destination.ConfigurationRevision++;
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw new ConfigurationConflictException(); }
        await transaction.CommitAsync(token);
        return new(true, await GetAsync(id, token));
    }

    public async Task<DestinationRemovalWrite> RemoveAsync(Guid id, long expectedRevision, CancellationToken token)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", token);
        DestinationEntity? destination = await database.Destinations.FromSqlInterpolated($"SELECT * FROM destinations WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(token);
        if (destination is null) return new(false, false, null);
        if (destination.ConfigurationRevision != expectedRevision)
        {
            await transaction.CommitAsync(token);
            return new(false, false, await GetAsync(id, token));
        }

        bool used = destination.IsArchived
            || await database.RoutingDecisions.AnyAsync(item => item.DestinationId == id, token)
            || await database.Deliveries.AnyAsync(item => item.DestinationId == id, token)
            || await database.RoutingActions.AnyAsync(item => item.DestinationId == id, token);
        if (!used)
        {
            string snapshotNeedle = JsonSerializer.Serialize(new[] { new { DestinationId = id } });
            used = await database.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM captures
                    WHERE "RoutingSnapshotJson" @> {snapshotNeedle}::jsonb
                ) AS "Value"
                """).SingleAsync(token);
        }
        if (used)
        {
            if (!destination.IsArchived)
            {
                destination.IsArchived = true;
                destination.Enabled = false;
                destination.ConfigurationRevision++;
                await database.Deliveries.Where(item => item.DestinationId == id
                    && (item.State == DeliveryState.Pending || item.State == DeliveryState.RetryWait))
                    .ExecuteUpdateAsync(updates => updates.SetProperty(item => item.State, DeliveryState.Disabled), token);
                await database.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
            return new(true, false, await GetAsync(id, token));
        }

        database.Destinations.Remove(destination);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(true, true, null);
    }

    private IQueryable<DestinationConfiguration> DestinationQuery(Guid? id = null, Guid? afterId = null) =>
        from destination in database.Destinations.AsNoTracking()
        join local in database.LocalDestinations.AsNoTracking() on destination.Id equals local.DestinationId into locals
        from local in locals.DefaultIfEmpty()
        join webdav in database.WebDavDestinations.AsNoTracking() on destination.Id equals webdav.DestinationId into webdavConfigs
        from webdav in webdavConfigs.DefaultIfEmpty()
        where (id == null || destination.Id == id) && (afterId == null || destination.Id.CompareTo(afterId.Value) > 0)
        select new DestinationConfiguration(destination.Id, destination.Name, destination.Kind, destination.Enabled,
            destination.ConfigurationRevision, local == null ? null : local.RootPath,
            webdav == null ? null : webdav.CollectionUrl, webdav == null ? null : webdav.Username,
            database.DestinationCredentials.Any(credential => credential.DestinationId == destination.Id), destination.IsArchived);
}

internal sealed class EfWebDavDestinationConfiguration(ShutterSpoolDbContext database,
    ShutterSpool.Application.Security.ICredentialProtector protector,
    WebDavDestinationDeliveryAdapter adapter, DestinationCapabilities? capabilities = null) : IWebDavDestinationConfiguration
{
    public Task<WebDavDestinationConfiguration?> GetAsync(Guid destinationId, CancellationToken cancellationToken) =>
        database.WebDavDestinations.AsNoTracking().Where(item => item.DestinationId == destinationId)
            .Select(item => new WebDavDestinationConfiguration(item.CollectionUrl, item.Username)).SingleOrDefaultAsync(cancellationToken);

    public async Task<string?> ConfigureAsync(Guid destinationId, string collectionUrl, string username, string password,
        CancellationToken cancellationToken)
        => await ConfigureCoreAsync(destinationId, null, collectionUrl, username, password, cancellationToken);

    public Task<string?> ConfigureAsync(Guid destinationId, long expectedRevision, string collectionUrl,
        string username, string? password, CancellationToken cancellationToken) =>
        ConfigureCoreAsync(destinationId, expectedRevision, collectionUrl, username, password, cancellationToken);

    private async Task<string?> ConfigureCoreAsync(Guid destinationId, long? expectedRevision, string collectionUrl,
        string username, string? password, CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled("webdav")) return "DESTINATION_TYPE_DISABLED";
        if (collectionUrl is null || !WebDavDestinationUris.TryCollection(collectionUrl, out Uri? normalized)) return "INVALID_COLLECTION_URL";
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Contains(':') || username.Any(char.IsControl)
            || password is not null && password.Length is 0 or > 4096) return "INVALID_CREDENTIALS";
        if (!await database.Destinations.AnyAsync(item => item.Id == destinationId && item.Kind == "webdav", cancellationToken)) return "DESTINATION_NOT_FOUND";
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);
        DestinationEntity? destination = await database.Destinations.FromSqlInterpolated($"SELECT * FROM destinations WHERE \"Id\" = {destinationId} AND \"Kind\" = 'webdav' FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (destination is null) return "DESTINATION_NOT_FOUND";
        if (destination.IsArchived) return "DESTINATION_ARCHIVED";
        if (expectedRevision is long expected && destination.ConfigurationRevision != expected) return "CONFIGURATION_CONFLICT";
        WebDavDestinationEntity? config = await database.WebDavDestinations.SingleOrDefaultAsync(item => item.DestinationId == destinationId, cancellationToken);
        bool identityChanged = config is null || config.CollectionUrl != normalized.AbsoluteUri || config.Username != username.Trim();
        if (identityChanged && await database.Deliveries.AnyAsync(item => item.DestinationId == destinationId, cancellationToken)) return "IDENTITY_LOCKED";
        DestinationCredentialEntity? credential = await database.DestinationCredentials.SingleOrDefaultAsync(item => item.DestinationId == destinationId, cancellationToken);
        if (password is null && credential is null) return "INVALID_CREDENTIALS";
        if (config is null) database.WebDavDestinations.Add(new() { DestinationId = destinationId, CollectionUrl = normalized.AbsoluteUri, Username = username.Trim() });
        else { config.CollectionUrl = normalized.AbsoluteUri; config.Username = username.Trim(); }
        if (password is not null)
        {
            string encrypted = protector.Protect(password);
            if (credential is null) database.DestinationCredentials.Add(new() { DestinationId = destinationId, EncryptedSecret = encrypted });
            else credential.EncryptedSecret = encrypted;
        }
        destination.ConfigurationRevision++;
        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return null;
    }

    public async Task<(bool Writable, string? ErrorCode)> TestAsync(Guid destinationId, CancellationToken cancellationToken)
    {
        (bool writable, string? error, _) = await TestWithRevisionAsync(destinationId, cancellationToken);
        return (writable, error);
    }

    public async Task<(bool Writable, string? ErrorCode, long TestedConfigurationRevision)> TestWithRevisionAsync(
        Guid destinationId, CancellationToken cancellationToken)
    {
        if (!(capabilities ?? DestinationCapabilities.All).IsEnabled("webdav"))
            return (false, "DESTINATION_TYPE_DISABLED", await database.Destinations.AsNoTracking()
                .Where(item => item.Id == destinationId).Select(item => item.ConfigurationRevision).SingleOrDefaultAsync(cancellationToken));
        var config = await (from destination in database.Destinations.AsNoTracking()
            join item in database.WebDavDestinations.AsNoTracking() on destination.Id equals item.DestinationId
            join credential in database.DestinationCredentials.AsNoTracking() on item.DestinationId equals credential.DestinationId
            where item.DestinationId == destinationId && destination.Kind == "webdav"
            select new { item.CollectionUrl, item.Username, credential.EncryptedSecret, destination.ConfigurationRevision, destination.IsArchived }).SingleOrDefaultAsync(cancellationToken);
        if (config?.IsArchived == true) return (false, "DESTINATION_ARCHIVED", config.ConfigurationRevision);
        if (config is null) return (false, "WEBDAV_NOT_CONFIGURED", await database.Destinations.AsNoTracking()
            .Where(item => item.Id == destinationId).Select(item => item.ConfigurationRevision).SingleOrDefaultAsync(cancellationToken));
        try
        {
            (bool writable, string? error) = await adapter.TestAsync(config.CollectionUrl, config.Username,
                protector.Unprotect(config.EncryptedSecret), cancellationToken);
            return (writable, error, config.ConfigurationRevision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return (false, "TEST_TIMED_OUT", config.ConfigurationRevision); }
        catch (Exception exception) when (exception is HttpRequestException or UriFormatException or System.Security.Cryptography.CryptographicException or IOException)
        { return (false, "CONNECTION_TEST_FAILED", config.ConfigurationRevision); }
    }
}

internal sealed partial class EfAccessGrantStore(ShutterSpoolDbContext database) : IAccessGrantStore
{
    public async Task<WorkspaceMembership?> GetAsync(string issuer, string subject, CancellationToken token)
    {
        AuthorizationGrant? grant = await database.AuthorizationGrants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Issuer == issuer && item.Subject == subject, token);
        return grant is null ? null : Membership(grant);
    }

    public async Task<WorkspaceMembership[]> ListAsync(CancellationToken token) =>
        [.. (await database.AuthorizationGrants.AsNoTracking().OrderBy(item => item.Subject).ThenBy(item => item.Id).ToArrayAsync(token))
            .Select(Membership)];

    public async Task<WorkspaceViewerPolicy> GetViewerPolicyAsync(CancellationToken token)
    {
        WorkspaceViewerPolicyEntity? row = await database.Set<WorkspaceViewerPolicyEntity>().AsNoTracking().SingleOrDefaultAsync(token);
        if (row is null && database.Database.IsRelational()) throw new InvalidOperationException("The workspace access policy is unavailable.");
        return row is null ? new(false, 0) : new(row.IncludeUnreleased, row.Revision);
    }

    public async Task<AccessChangeResult> ChangeAsync(AccessChange change, CancellationToken token)
    {
        if (change.OperationId == Guid.Empty || change.ExpectedRevision < 0)
            throw new ArgumentException("Provide an operation ID and current revision.");
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await LockAsync(token);
        string request = JsonSerializer.Serialize(change);
        WorkspaceAccessOperationEntity? recorded = await database.Set<WorkspaceAccessOperationEntity>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == change.OperationId, token);
        if (recorded is not null)
            return recorded.Request == request ? JsonSerializer.Deserialize<AccessChangeResult>(recorded.Result)!
                : new(change.OperationId, "conflict");
        // Always read actor and target inside the tenant serialization boundary.
        database.ChangeTracker.Clear();
        AuthorizationGrant? actor = await database.AuthorizationGrants.SingleOrDefaultAsync(item =>
            item.Issuer == change.ActorIssuer && item.Subject == change.ActorSubject && item.IsActive, token);
        AuthorizationGrant? target = change.Kind == "leave" ? actor
            : await database.AuthorizationGrants.SingleOrDefaultAsync(item => item.Id == change.TargetId, token);
        WorkspaceViewerPolicyEntity policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token);
        string beforeState = JsonSerializer.Serialize(new { Actor = actor is null ? null : Membership(actor),
            Target = target is null ? null : Membership(target), Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision), policy.AuthorityRevision });
        AccessChangeResult result;
        if (actor is null || !Enum.IsDefined(actor.Role)) result = new(change.OperationId, "denied");
        else if (change.Kind == "viewer-policy")
        {
            if (actor.Role is not (WorkspaceRole.Owner or WorkspaceRole.Administrator)) result = new(change.OperationId, "denied");
            else if (policy.Revision != change.ExpectedRevision) result = new(change.OperationId, "conflict", ViewerPolicy: new(policy.IncludeUnreleased, policy.Revision));
            else
            {
                policy.IncludeUnreleased = change.IncludeUnreleased;
                policy.Revision++;
                result = new(change.OperationId, "applied", ViewerPolicy: new(policy.IncludeUnreleased, policy.Revision));
            }
        }
        else if (target is null || !target.IsActive) result = new(change.OperationId, "notFound");
        else if (change.Kind != "leave" && (actor.Role is not (WorkspaceRole.Owner or WorkspaceRole.Administrator)
            || target.Role == WorkspaceRole.Owner && actor.Role != WorkspaceRole.Owner)) result = new(change.OperationId, "denied");
        else if (change.Kind == "promote-owner" && (actor.Role != WorkspaceRole.Owner || !change.Confirmed)) result = new(change.OperationId, "denied");
        else if (change.Kind is not ("role" or "promote-owner" or "remove" or "leave")
            || change.Kind == "role" && (change.Role is null or WorkspaceRole.Owner || !Enum.IsDefined(change.Role.Value))) result = new(change.OperationId, "denied");
        else if (target.Revision != change.ExpectedRevision) result = new(change.OperationId, "conflict", Membership(target));
        else if (target.Role == WorkspaceRole.Owner && change.Kind != "promote-owner"
            && !await database.AuthorizationGrants.AnyAsync(item => item.IsActive && item.Role == WorkspaceRole.Owner && item.Id != target.Id, token))
            result = new(change.OperationId, "lastOwner", Membership(target));
        else
        {
            if (change.Kind is "remove" or "leave") target.IsActive = false;
            else target.Role = change.Kind == "promote-owner" ? WorkspaceRole.Owner : change.Role!.Value;
            target.Revision++;
            result = new(change.OperationId, "applied", Membership(target));
        }
        if (result.Status == "applied") policy.AuthorityRevision++;
        database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = change.OperationId, Request = request, Result = JsonSerializer.Serialize(result),
            ActorIssuer = change.ActorIssuer, ActorSubject = change.ActorSubject, Kind = change.Kind,
            TargetId = target?.Id ?? change.TargetId, RecordedAt = DateTimeOffset.UtcNow,
            BeforeState = beforeState, AfterState = JsonSerializer.Serialize(new { Target = target is null ? null : Membership(target),
                Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision), policy.AuthorityRevision }),
        });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return result;
    }

    private Task<int> LockAsync(CancellationToken token) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-workspace-access', 0))", token);
    private static WorkspaceMembership Membership(AuthorizationGrant grant) =>
        new(grant.Id, grant.Issuer, grant.Subject, grant.Role, grant.Revision, grant.IsActive);

    public Task<bool> IsActiveAsync(string issuer, string subject, CancellationToken cancellationToken) =>
        database.AuthorizationGrants.AsNoTracking().AnyAsync(grant => grant.IsActive && grant.Issuer == issuer && grant.Subject == subject
            && (grant.Role == WorkspaceRole.Owner || grant.Role == WorkspaceRole.Administrator
                || grant.Role == WorkspaceRole.Curator || grant.Role == WorkspaceRole.Viewer), cancellationToken);

    internal async Task PrepareInitialOwnerAsync(Guid operation, string issuer, string subject,
        string? operatorIdentity, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(operatorIdentity) || operatorIdentity.Length > 512)
            throw new ArgumentException("Initial Owner setup requires a trusted host operator identity of at most 512 characters.");
        if (database.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Initial Owner setup must share the tenant provisioning transaction.");
        await LockAsync(token);
        WorkspaceViewerPolicyEntity policy = await database.Set<WorkspaceViewerPolicyEntity>().SingleAsync(token);
        if (policy.AuthorityRevision != 0 || await database.AuthorizationGrants.AnyAsync(token)
            || await database.Set<WorkspaceAccessOperationEntity>().AnyAsync(item => item.Id == operation, token))
            throw new InvalidOperationException("An uncompleted initial setup cannot replace existing workspace authority or its operation record.");
        var owner = new AuthorizationGrant { Issuer = issuer, Subject = subject, Role = WorkspaceRole.Owner, IsActive = true, IsBootstrap = true };
        string beforeState = JsonSerializer.Serialize(new { Target = (WorkspaceMembership?)null,
            Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision), policy.AuthorityRevision });
        database.AuthorizationGrants.Add(owner);
        policy.AuthorityRevision++;
        var result = new AccessChangeResult(operation, "applied", Membership(owner));
        database.Set<WorkspaceAccessOperationEntity>().Add(new()
        {
            Id = operation, Kind = "operator-setup", ActorIssuer = "host-operator", ActorSubject = operatorIdentity,
            TargetId = owner.Id, RecordedAt = DateTimeOffset.UtcNow,
            Request = JsonSerializer.Serialize(new { TargetIssuer = issuer, TargetSubject = subject, OperatorIdentity = operatorIdentity }),
            Result = JsonSerializer.Serialize(result), BeforeState = beforeState,
            AfterState = JsonSerializer.Serialize(new { Target = Membership(owner),
                Policy = new WorkspaceViewerPolicy(policy.IncludeUnreleased, policy.Revision), policy.AuthorityRevision }),
        });
    }

    public async Task BootstrapAsync(string issuer, string subject, CancellationToken cancellationToken)
    {
        await using var transaction = database.Database.IsRelational() ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        if (transaction is not null)
            await LockAsync(cancellationToken);
        var replaced = await database.AuthorizationGrants.Where(grant => grant.IsBootstrap && (grant.Issuer != issuer || grant.Subject != subject)).ToListAsync(cancellationToken);
        foreach (AuthorizationGrant grant in replaced) { grant.IsActive = false; grant.Revision++; }
        AuthorizationGrant? selected = await database.AuthorizationGrants.SingleOrDefaultAsync(grant => grant.Issuer == issuer && grant.Subject == subject, cancellationToken);
        if (selected is null) database.AuthorizationGrants.Add(new AuthorizationGrant { Issuer = issuer, Subject = subject, IsActive = true, IsBootstrap = true });
        else { selected.IsActive = true; selected.IsBootstrap = true; selected.Role = WorkspaceRole.Owner; selected.Revision++; }
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }
}
