using ShutterSpool.Infrastructure.Composition;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Text.Json;

namespace ShutterSpool.Infrastructure.Workspaces;

internal sealed record WorkspaceManifest(string StorageRoot, long AllocatedBytes, string OwnerIssuer, string OwnerSubject)
{
    internal string ConnectionString { get; init; } = "";
    internal string? MigrationConnectionString { get; init; }

    internal static string CanonicalName(IConfiguration configuration, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name != name.Trim() || name.Contains(':'))
            throw new WorkspaceOperator.RefusedException("Use one workspace name from the canonical manifest.");
        return configuration.GetSection("ShutterSpool:Workspaces:Definitions").GetChildren()
            .SingleOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))?.Key
            ?? throw new WorkspaceOperator.RefusedException("The named workspace manifest is unavailable.");
    }

    internal static WorkspaceManifest Read(IConfiguration configuration, string name)
    {
        WorkspaceManifest manifest = configuration.GetSection($"ShutterSpool:Workspaces:Definitions:{name}").Get<WorkspaceManifest>()
            ?? throw new WorkspaceOperator.RefusedException("The named workspace manifest is required.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name != name.Trim()
            || !Path.IsPathFullyQualified(manifest.StorageRoot) || manifest.AllocatedBytes < 10
            || !Uri.TryCreate(manifest.OwnerIssuer, UriKind.Absolute, out Uri? issuer)
            || issuer.Scheme != "https" && (issuer.Scheme != "http" || !issuer.IsLoopback || configuration.GetValue<bool>("ShutterSpool:Deployment:Packaged"))
            || !string.Equals(manifest.OwnerIssuer.TrimEnd('/'), configuration["ShutterSpool:Security:Authority"]?.TrimEnd('/'), StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(manifest.OwnerSubject) || manifest.OwnerSubject.Length > 512)
            throw new WorkspaceOperator.RefusedException("The workspace manifest requires an allocated root and independently verified initial Owner from the configured OIDC issuer.");
        string connection = configuration.GetConnectionString($"Workspaces:{name}:Runtime")
            ?? throw new WorkspaceOperator.RefusedException("The named workspace runtime connection string is required.");
        _ = Target(connection);
        return manifest with
        {
            StorageRoot = Path.GetFullPath(manifest.StorageRoot), ConnectionString = connection,
            MigrationConnectionString = configuration.GetConnectionString($"Workspaces:{name}:Migration"),
        };
    }

    // Persist target identity and initial authority, never credentials or secret-file locations.
    internal string Proof => JsonSerializer.Serialize(new ProvisioningProof(StorageRoot, AllocatedBytes,
        Target(ConnectionString), OwnerIssuer, OwnerSubject));

    internal bool MatchesProof(string stored)
    {
        ProvisioningProof? proof = JsonSerializer.Deserialize<ProvisioningProof>(stored);
        return proof is not null && proof.StorageRoot == StorageRoot && proof.AllocatedBytes == AllocatedBytes
            && proof.OwnerIssuer == OwnerIssuer && proof.OwnerSubject == OwnerSubject
            && JsonSerializer.Deserialize<DatabaseTarget>(proof.RuntimeTarget) == ReadTarget(ConnectionString);
    }

    private sealed record ProvisioningProof(string StorageRoot, long AllocatedBytes, string RuntimeTarget, string OwnerIssuer, string OwnerSubject);
    private sealed record DatabaseTarget(string Host, int Port, string Database, string Username);
    internal static string Target(string connection) => JsonSerializer.Serialize(ReadTarget(connection));

    private static DatabaseTarget ReadTarget(string connection)
    {
        var target = new NpgsqlConnectionStringBuilder(connection);
        if (string.IsNullOrWhiteSpace(target.Host) || string.IsNullOrWhiteSpace(target.Database) || string.IsNullOrWhiteSpace(target.Username))
            throw new WorkspaceOperator.RefusedException("Workspace connections require an explicit host, database and role.");
        return new(target.Host, target.Port, target.Database, target.Username);
    }
    internal string MigrationConnection => ValidateMigrationConnection(MigrationConnectionString, ConnectionString);
    internal static string ValidateMigrationConnection(string? migrationConnection, string runtimeConnection)
    {
        var migration = new NpgsqlConnectionStringBuilder(migrationConnection ?? throw new WorkspaceOperator.RefusedException("Separate operator migration credentials are required."));
        var runtime = new NpgsqlConnectionStringBuilder(runtimeConnection);
        if (migration.Host != runtime.Host || migration.Port != runtime.Port || migration.Database != runtime.Database
            || migration.Username == runtime.Username || string.IsNullOrWhiteSpace(migration.Database) || string.IsNullOrWhiteSpace(runtime.Username))
            throw new WorkspaceOperator.RefusedException("Migration and restricted runtime credentials must target the same explicit database with different roles.");
        return migration.ConnectionString;
    }
}
