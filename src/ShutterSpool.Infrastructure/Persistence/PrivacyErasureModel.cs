using Microsoft.EntityFrameworkCore;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed partial class ShutterSpoolDbContext
{
    internal DbSet<PrivacyErasureOperationEntity> PrivacyErasureOperations => Set<PrivacyErasureOperationEntity>();
    internal DbSet<PrivacyErasureAssetEntity> PrivacyErasureAssets => Set<PrivacyErasureAssetEntity>();
    internal DbSet<PrivacyErasureExportEntity> PrivacyErasureExports => Set<PrivacyErasureExportEntity>();
    internal DbSet<PrivacyErasureExportAssetEntity> PrivacyErasureExportAssets => Set<PrivacyErasureExportAssetEntity>();
    internal DbSet<PrivacyErasureImportEntity> PrivacyErasureImports => Set<PrivacyErasureImportEntity>();
    internal DbSet<PrivacyErasureImportAssetEntity> PrivacyErasureImportAssets => Set<PrivacyErasureImportAssetEntity>();

    internal static void ConfigureErasure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PrivacyErasureOperationEntity>(entity =>
        {
            entity.ToTable("privacy_erasures");
            entity.HasKey(operation => operation.Id);
            entity.Property(operation => operation.State).HasMaxLength(16);
            entity.Property(operation => operation.ErrorCode).HasMaxLength(64);
            entity.HasIndex(operation => new { operation.State, operation.RequestedAt });
        });
        modelBuilder.Entity<PrivacyErasureAssetEntity>(entity =>
        {
            entity.ToTable("privacy_erasure_assets");
            entity.HasKey(asset => asset.AssetId);
            entity.Property(asset => asset.SpoolKey).HasMaxLength(1_024);
            entity.Property(asset => asset.ExpectedSize).IsRequired();
            entity.Property(asset => asset.KnownTransientKeys).HasColumnType("text[]").HasDefaultValue(Array.Empty<string>());
            entity.Property(asset => asset.StorageMarker).HasMaxLength(32);
            entity.Property(asset => asset.State).HasMaxLength(16);
            entity.Property(asset => asset.ErrorCode).HasMaxLength(64);
            entity.Property(asset => asset.DerivativesErrorCode).HasMaxLength(64);
            entity.Property(asset => asset.OriginalsErrorCode).HasMaxLength(64);
            entity.HasIndex(asset => new { asset.OperationId, asset.State });
        });
        modelBuilder.Entity<PrivacyErasureExportEntity>(entity =>
        {
            entity.ToTable("privacy_erasure_exports");
            entity.HasKey(snapshot => snapshot.Id);
            entity.Property(snapshot => snapshot.ManifestHash).HasMaxLength(64);
            entity.HasIndex(snapshot => snapshot.CreatedAt);
        });
        modelBuilder.Entity<PrivacyErasureExportAssetEntity>(entity =>
        {
            entity.ToTable("privacy_erasure_export_assets");
            entity.HasKey(asset => new { asset.SnapshotId, asset.Ordinal });
            entity.Property(asset => asset.SpoolKey).HasMaxLength(1_024);
            entity.Property(asset => asset.ExpectedSize).IsRequired();
            entity.Property(asset => asset.KnownTransientKeys).HasColumnType("text[]").HasDefaultValue(Array.Empty<string>());
            entity.Property(asset => asset.StorageMarker).HasMaxLength(32);
            entity.HasIndex(asset => new { asset.SnapshotId, asset.PageIndex, asset.Ordinal });
        });
        modelBuilder.Entity<PrivacyErasureImportEntity>(entity =>
        {
            entity.ToTable("privacy_erasure_imports");
            entity.HasKey(snapshot => snapshot.SnapshotId);
            entity.Property(snapshot => snapshot.ManifestHash).HasMaxLength(64);
        });
        modelBuilder.Entity<PrivacyErasureImportAssetEntity>(entity =>
        {
            entity.ToTable("privacy_erasure_import_assets");
            entity.HasKey(asset => new { asset.SnapshotId, asset.Ordinal });
            entity.Property(asset => asset.SpoolKey).HasMaxLength(1_024);
            entity.Property(asset => asset.ExpectedSize).IsRequired();
            entity.Property(asset => asset.KnownTransientKeys).HasColumnType("text[]").HasDefaultValue(Array.Empty<string>());
            entity.Property(asset => asset.StorageMarker).HasMaxLength(32);
            entity.HasIndex(asset => new { asset.SnapshotId, asset.PageIndex, asset.AssetId }).IsUnique();
        });
    }
}
