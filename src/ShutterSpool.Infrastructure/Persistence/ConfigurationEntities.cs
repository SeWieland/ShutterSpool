using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Domain.Collections;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class SourceEntity
{
    public Guid Id { get; init; }
    public Guid CollectionId { get; set; }
    public required string Name { get; set; }
    public long ConfigurationRevision { get; set; }
    public SourceCurationMode CurationMode { get; set; } = SourceCurationMode.Delay;
    public int CurationDelaySeconds { get; set; } = 3_600;
    public bool IsArchived { get; set; }
    public SourceType SourceType { get; set; }
    public SourceConnectionProfile ConnectionProfile { get; set; }
}

internal sealed class CollectionEntity
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public required string Identity { get; set; }
    public long Revision { get; set; }
    public bool IsBuiltIn { get; set; }
}

internal sealed class SourceCredentialEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid SourceId { get; init; }
    public required string Username { get; set; }
    public required string VirtualRoot { get; init; }
    public required byte[] SecretVerifier { get; set; }
    public long CredentialGeneration { get; set; } = 1;
    public bool IsIngressEnabled { get; set; } = true;
}

internal sealed class SourceAuthenticationObservationEntity
{
    public Guid SourceId { get; init; }
    public required string Outcome { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public long ObservedAtTicks { get; set; }
    public long Version { get; set; }
}

internal sealed class CameraLoginEntity
{
    public Guid Id { get; init; }
    public Guid SourceId { get; init; }
    public required string Username { get; init; }
    public required string VirtualRoot { get; init; }
    public long CredentialGeneration { get; init; }
    public SourceConnectionProfile ConnectionProfile { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

internal sealed class CameraUploadAdmissionEntity
{
    public Guid Id { get; init; }
    public Guid LoginId { get; init; }
    public Guid SourceId { get; init; }
    public required string Username { get; init; }
    public required string SessionId { get; init; }
    public required string VirtualPath { get; init; }
    public long AdmissionTimestamp { get; init; }
    public required CameraTransferState State { get; set; }
    public long MaximumUploadBytes { get; init; }
    public string? ReceiveKey { get; set; }
    public long? Size { get; set; }
    public long? CompletionTimestamp { get; set; }
    public Guid? AssetId { get; set; }
    public string? FailureReason { get; set; }
}

internal sealed class DestinationEntity
{
    public Guid Id { get; init; }
    public required string Name { get; set; }
    public string Kind { get; init; } = "local";
    public bool Enabled { get; set; } = true;
    public bool IsArchived { get; set; }
    public long ConfigurationRevision { get; set; }
}

internal sealed class DestinationCredentialEntity
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid DestinationId { get; init; }
    public required string EncryptedSecret { get; set; }
}

internal sealed class LocalDestinationEntity
{
    public Guid DestinationId { get; init; }
    public required string RootPath { get; set; }
}

internal sealed class WebDavDestinationEntity
{
    public Guid DestinationId { get; init; }
    public required string CollectionUrl { get; set; }
    public required string Username { get; set; }
}

internal sealed class AuthorizationGrant
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Issuer { get; init; }
    public required string Subject { get; init; }
    public bool IsActive { get; set; }
    public bool IsBootstrap { get; set; }
}

internal sealed partial class ShutterSpoolDbContext
{
    private static void ConfigureConfiguration(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CollectionEntity>(entity =>
        {
            entity.ToTable("collections");
            entity.HasKey(collection => collection.Id);
            entity.Property(collection => collection.Name).HasMaxLength(CollectionName.MaximumSegmentLength);
            entity.Property(collection => collection.Path).HasMaxLength(CollectionName.MaximumPathLength);
            entity.Property(collection => collection.Identity).HasMaxLength(CollectionName.MaximumPathLength);
            entity.HasIndex(collection => collection.Identity).IsUnique();
            entity.HasIndex(collection => collection.IsBuiltIn).IsUnique().HasFilter("\"IsBuiltIn\"");
        });
        modelBuilder.Entity<SourceEntity>(entity =>
        {
            entity.ToTable("sources");
            entity.HasKey(source => source.Id);
            entity.Property(source => source.CollectionId).HasDefaultValue(CollectionName.BuiltInId);
            entity.Property(source => source.Name).HasMaxLength(256);
            entity.HasIndex(source => source.Name).IsUnique();
            entity.Property(source => source.CurationMode).HasConversion<string>().HasMaxLength(16);
            entity.Property(source => source.ConfigurationRevision).HasDefaultValue(0L);
            entity.Property(source => source.IsArchived).HasDefaultValue(false);
            entity.Property(source => source.SourceType).HasConversion<string>().HasMaxLength(32);
            entity.Property(source => source.ConnectionProfile).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<CollectionEntity>().WithMany().HasForeignKey(source => source.CollectionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SourceCredentialEntity>(entity =>
        {
            entity.ToTable("source_credentials");
            entity.HasKey(credential => credential.Id);
            entity.HasIndex(credential => credential.SourceId).IsUnique();
            entity.HasIndex(credential => credential.Username).IsUnique();
            entity.Property(credential => credential.Username).HasMaxLength(256);
            entity.Property(credential => credential.VirtualRoot).HasMaxLength(1_024);
            entity.Property(credential => credential.SecretVerifier).HasMaxLength(32);
            entity.Property(credential => credential.CredentialGeneration).HasDefaultValue(1L);
            entity.Property(credential => credential.IsIngressEnabled).HasDefaultValue(true);
            entity.HasOne<SourceEntity>().WithOne().HasForeignKey<SourceCredentialEntity>(credential => credential.SourceId);
        });
        modelBuilder.Entity<SourceAuthenticationObservationEntity>(entity =>
        {
            entity.ToTable("source_authentication_observations");
            entity.HasKey(observation => observation.SourceId);
            entity.Property(observation => observation.Outcome).HasMaxLength(16);
            entity.HasOne<SourceEntity>().WithMany().HasForeignKey(observation => observation.SourceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<CameraLoginEntity>(entity =>
        {
            entity.ToTable("camera_logins");
            entity.HasKey(login => login.Id);
            entity.Property(login => login.Username).HasMaxLength(256);
            entity.Property(login => login.VirtualRoot).HasMaxLength(1_024);
            entity.Property(login => login.ConnectionProfile).HasConversion<string>().HasMaxLength(32);
            entity.HasOne<SourceEntity>().WithMany().HasForeignKey(login => login.SourceId);
            entity.HasIndex(login => login.SourceId);
        });
        modelBuilder.Entity<CameraUploadAdmissionEntity>(entity =>
        {
            entity.ToTable("camera_upload_admissions");
            entity.HasKey(admission => admission.Id);
            entity.Property(admission => admission.Username).HasMaxLength(256);
            entity.Property(admission => admission.SessionId).HasMaxLength(256);
            entity.Property(admission => admission.VirtualPath).HasMaxLength(1_024);
            entity.Property(admission => admission.State).HasMaxLength(24);
            entity.Property(admission => admission.State).HasConversion<string>();
            entity.Property(admission => admission.FailureReason).HasMaxLength(256);
            entity.Property(admission => admission.MaximumUploadBytes);
            entity.Property(admission => admission.ReceiveKey).HasMaxLength(1_024);
            entity.HasOne<CameraLoginEntity>().WithMany().HasForeignKey(admission => admission.LoginId);
            entity.HasOne<SourceEntity>().WithMany().HasForeignKey(admission => admission.SourceId);
            entity.Property(admission => admission.MaximumUploadBytes).HasDefaultValue(0L).ValueGeneratedNever();
            entity.HasIndex(admission => new { admission.LoginId, admission.SessionId, admission.VirtualPath, admission.AdmissionTimestamp }).IsUnique()
                .HasDatabaseName("IX_camera_upload_admissions_LoginId_SessionId_VirtualPath_Admis");
            entity.HasIndex(admission => admission.LoginId).IsUnique().HasFilter("\"State\" IN ('Admitted', 'Receiving', 'Received', 'Handoff')");
            entity.HasIndex(admission => new { admission.LoginId, admission.SessionId, admission.VirtualPath, admission.CompletionTimestamp })
                .HasDatabaseName("IX_camera_upload_admissions_LoginId_SessionId_VirtualPath_Compl");
        });
        modelBuilder.Entity<DestinationEntity>(entity =>
        {
            entity.ToTable("destinations");
            entity.HasKey(destination => destination.Id);
            entity.Property(destination => destination.Name).HasMaxLength(256);
            entity.Property(destination => destination.Kind).HasMaxLength(64);
            entity.Property(destination => destination.ConfigurationRevision).HasDefaultValue(0L);
            entity.Property(destination => destination.IsArchived).HasDefaultValue(false);
            entity.HasIndex(destination => destination.Name).IsUnique();
        });
        modelBuilder.Entity<DestinationCredentialEntity>(entity =>
        {
            entity.ToTable("destination_credentials");
            entity.HasKey(credential => credential.Id);
            entity.HasIndex(credential => credential.DestinationId).IsUnique();
            entity.Property(credential => credential.EncryptedSecret).HasMaxLength(8_192);
            entity.HasOne<DestinationEntity>().WithOne().HasForeignKey<DestinationCredentialEntity>(credential => credential.DestinationId);
        });
        modelBuilder.Entity<LocalDestinationEntity>(entity =>
        {
            entity.ToTable("local_destinations");
            entity.HasKey(settings => settings.DestinationId);
            entity.Property(settings => settings.RootPath).HasMaxLength(2_048);
            entity.HasOne<DestinationEntity>().WithOne().HasForeignKey<LocalDestinationEntity>(settings => settings.DestinationId);
        });
        modelBuilder.Entity<WebDavDestinationEntity>(entity =>
        {
            entity.ToTable("webdav_destinations");
            entity.HasKey(settings => settings.DestinationId);
            entity.Property(settings => settings.CollectionUrl).HasMaxLength(2_048);
            entity.Property(settings => settings.Username).HasMaxLength(256);
            entity.HasOne<DestinationEntity>().WithOne().HasForeignKey<WebDavDestinationEntity>(settings => settings.DestinationId);
        });
        modelBuilder.Entity<DestinationRoutingEntity>().HasOne<DestinationEntity>().WithOne()
            .HasForeignKey<DestinationRoutingEntity>(routing => routing.DestinationId);
        modelBuilder.Entity<AuthorizationGrant>(entity =>
        {
            entity.ToTable("authorization_grants");
            entity.HasKey(grant => grant.Id);
            entity.Property(grant => grant.Issuer).HasMaxLength(2_048);
            entity.Property(grant => grant.Subject).HasMaxLength(512);
            entity.HasIndex(grant => new { grant.Issuer, grant.Subject }).IsUnique();
        });
    }
}
