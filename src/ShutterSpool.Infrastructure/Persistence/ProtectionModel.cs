using Microsoft.EntityFrameworkCore;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed partial class ShutterSpoolDbContext
{
    internal DbSet<GalleryProtectionEntity> GalleryProtections => Set<GalleryProtectionEntity>();
    internal DbSet<GalleryProtectionAssetEntity> GalleryProtectionAssets => Set<GalleryProtectionAssetEntity>();
    internal DbSet<PreservationHoldEntity> PreservationHolds => Set<PreservationHoldEntity>();
    internal DbSet<PreservationHoldAssetEntity> PreservationHoldAssets => Set<PreservationHoldAssetEntity>();

    internal static void ConfigureProtections(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GalleryProtectionEntity>(entity =>
        {
            entity.ToTable("gallery_protections");
            entity.HasKey(protection => protection.Id);
            entity.Property(protection => protection.Revision).HasDefaultValue(0);
            entity.HasIndex(protection => protection.ExpiresAt);
        });
        modelBuilder.Entity<GalleryProtectionAssetEntity>(entity =>
        {
            entity.ToTable("gallery_protection_assets");
            entity.HasKey(member => new { member.ProtectionId, member.AssetId });
            entity.HasOne<GalleryProtectionEntity>().WithMany().HasForeignKey(member => member.ProtectionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(member => member.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(member => member.AssetId);
        });
        modelBuilder.Entity<PreservationHoldEntity>(entity =>
        {
            entity.ToTable("preservation_holds");
            entity.HasKey(hold => hold.Id);
            entity.Property(hold => hold.Revision).HasDefaultValue(0);
            entity.Property(hold => hold.Authority).HasMaxLength(512).IsRequired();
            entity.Property(hold => hold.Reason).HasMaxLength(2_048).IsRequired();
            entity.Property(hold => hold.ResolvedBy).HasMaxLength(512);
            entity.Property(hold => hold.Resolution).HasMaxLength(2_048);
        });
        modelBuilder.Entity<PreservationHoldAssetEntity>(entity =>
        {
            entity.ToTable("preservation_hold_assets");
            entity.HasKey(member => new { member.HoldId, member.AssetId });
            entity.HasOne<PreservationHoldEntity>().WithMany().HasForeignKey(member => member.HoldId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(member => member.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(member => member.AssetId);
        });
    }
}
