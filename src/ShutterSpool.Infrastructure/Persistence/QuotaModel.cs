using Microsoft.EntityFrameworkCore;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed partial class ShutterSpoolDbContext
{
    internal DbSet<SpoolQuotaSettingsEntity> SpoolQuotaSettings => Set<SpoolQuotaSettingsEntity>();
    internal DbSet<SpoolQuotaReservationEntity> SpoolQuotaReservations => Set<SpoolQuotaReservationEntity>();
    internal DbSet<SpoolQuotaObservationEntity> SpoolQuotaObservations => Set<SpoolQuotaObservationEntity>();

    private static void ConfigureQuota(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IngestOperationEntity>().HasIndex(operation => operation.Id)
            .HasFilter("\"State\" NOT IN ('Committed', 'Quarantined')");
        modelBuilder.Entity<SpoolQuotaSettingsEntity>(entity =>
        {
            entity.ToTable("spool_quota_settings", table => table.HasCheckConstraint("ck_spool_quota_settings_singleton", "\"Id\" = 1"));
            entity.HasKey(settings => settings.Id);
            entity.Property(settings => settings.IsActive).HasDefaultValue(false);
            entity.Property(settings => settings.StorageMarkerAdoptionAuthorized).HasDefaultValue(false);
        });
        modelBuilder.Entity<SpoolQuotaReservationEntity>(entity =>
        {
            entity.ToTable("spool_quota_reservations", table =>
            {
                table.HasCheckConstraint("ck_spool_quota_reservation_bytes",
                    "\"ReservedBytes\" > 0 AND \"WrittenBytes\" >= 0 AND \"WrittenBytes\" <= \"ReservedBytes\" AND \"FilesystemReservedBytes\" >= 0");
                table.HasCheckConstraint("ck_spool_quota_reservation_state", "\"State\" IN ('Active', 'Committed')");
                table.HasCheckConstraint("ck_spool_quota_reservation_category", "\"Category\" IN ('Spool', 'DiskPreview')");
            });
            entity.HasKey(reservation => reservation.OperationId);
            entity.Property(reservation => reservation.SpoolKeys).HasColumnType("text[]");
            entity.Property(reservation => reservation.State).HasDefaultValue("Active");
            entity.Property(reservation => reservation.Category).HasDefaultValue("Spool");
            entity.HasIndex(reservation => reservation.CreatedAt);
        });
        modelBuilder.Entity<SpoolQuotaObservationEntity>(entity =>
        {
            entity.ToTable("spool_quota_observations", table => table.HasCheckConstraint("ck_spool_quota_observations_singleton", "\"Id\" = 1"));
            entity.HasKey(observation => observation.Id);
            entity.Property(observation => observation.StorageMarker).HasMaxLength(32);
        });
    }
}
