using Microsoft.EntityFrameworkCore;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed partial class ShutterSpoolDbContext(DbContextOptions<ShutterSpoolDbContext> options) : DbContext(options)
{
    internal DbSet<IngestOperationEntity> Operations => Set<IngestOperationEntity>();
    internal DbSet<AssetEntity> Assets => Set<AssetEntity>();
    internal DbSet<CaptureEntity> Captures => Set<CaptureEntity>();
    internal DbSet<CaptureAssetEntity> CaptureAssets => Set<CaptureAssetEntity>();
    internal DbSet<PreviewPrewarmEntity> PreviewPrewarms => Set<PreviewPrewarmEntity>();
    internal DbSet<DestinationRoutingEntity> DestinationRoutings => Set<DestinationRoutingEntity>();
    internal DbSet<RoutedAssetEntity> RoutedAssets => Set<RoutedAssetEntity>();
    internal DbSet<RoutingDecisionEntity> RoutingDecisions => Set<RoutingDecisionEntity>();
    internal DbSet<DeliveryEntity> Deliveries => Set<DeliveryEntity>();
    internal DbSet<DeliveryAttemptEntity> DeliveryAttempts => Set<DeliveryAttemptEntity>();
    internal DbSet<RoutingActionEntity> RoutingActions => Set<RoutingActionEntity>();
    internal DbSet<AssetRemovalOperationEntity> AssetRemovalOperations => Set<AssetRemovalOperationEntity>();
    internal DbSet<RetentionPolicyEntity> RetentionPolicies => Set<RetentionPolicyEntity>();
    internal DbSet<RetentionPolicyHistoryEntity> RetentionPolicyHistory => Set<RetentionPolicyHistoryEntity>();
    internal DbSet<RetentionHistoryEntity> RetentionHistory => Set<RetentionHistoryEntity>();
    internal DbSet<RetentionPressureStateEntity> RetentionPressureStates => Set<RetentionPressureStateEntity>();

    internal DbSet<SourceEntity> Sources => Set<SourceEntity>();
    internal DbSet<CollectionEntity> Collections => Set<CollectionEntity>();
    internal DbSet<SourceCredentialEntity> SourceCredentials => Set<SourceCredentialEntity>();
    internal DbSet<SourceAuthenticationObservationEntity> SourceAuthenticationObservations => Set<SourceAuthenticationObservationEntity>();
    internal DbSet<CameraLoginEntity> CameraLogins => Set<CameraLoginEntity>();
    internal DbSet<CameraUploadAdmissionEntity> CameraUploadAdmissions => Set<CameraUploadAdmissionEntity>();
    internal DbSet<DestinationEntity> Destinations => Set<DestinationEntity>();
    internal DbSet<DestinationCredentialEntity> DestinationCredentials => Set<DestinationCredentialEntity>();
    internal DbSet<LocalDestinationEntity> LocalDestinations => Set<LocalDestinationEntity>();
    internal DbSet<WebDavDestinationEntity> WebDavDestinations => Set<WebDavDestinationEntity>();
    internal DbSet<AuthorizationGrant> AuthorizationGrants => Set<AuthorizationGrant>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureConfiguration(modelBuilder);
        ConfigureInvitations(modelBuilder);
        ConfigureQuota(modelBuilder);
        ConfigureProtections(modelBuilder);
        ConfigureErasure(modelBuilder);
        modelBuilder.Entity<IngestOperationEntity>(entity =>
        {
            entity.ToTable("ingest_operations");
            entity.HasKey(operation => operation.Id);
            entity.Property(operation => operation.OriginalName).HasMaxLength(1_024);
            entity.Property(operation => operation.PartialKey).HasMaxLength(1_024);
            entity.Property(operation => operation.FinalKey).HasMaxLength(1_024);
            entity.Property(operation => operation.Sha256).HasMaxLength(64);
            entity.Property(operation => operation.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(operation => operation.QuarantineReason).HasMaxLength(1_024);
        });
        modelBuilder.Entity<AssetEntity>(entity =>
        {
            entity.ToTable("assets");
            entity.HasKey(asset => asset.Id);
            entity.Property(asset => asset.OriginalName).HasMaxLength(1_024);
            entity.Property(asset => asset.SpoolKey).HasMaxLength(1_024);
            entity.Property(asset => asset.Sha256).HasMaxLength(64);
            entity.Property(asset => asset.State).HasConversion<string>().HasMaxLength(32);
            entity.Property(asset => asset.RetentionPolicyRevision).HasDefaultValue(1);
            entity.Property(asset => asset.MetadataState).HasMaxLength(32).HasDefaultValue("Pending");
            entity.Property(asset => asset.MetadataAttemptCount).HasDefaultValue(0);
            entity.Property(asset => asset.CaptureTimeUncertain).HasDefaultValue(false);
            entity.Property(asset => asset.ExtensionMismatch).HasDefaultValue(false);
            entity.Property(asset => asset.MetadataOutcome).HasMaxLength(32);
            entity.Property(asset => asset.DetectedMediaType).HasMaxLength(128);
            entity.Property(asset => asset.MetadataRole).HasMaxLength(32);
            entity.Property(asset => asset.CaptureTimeLocal).HasColumnType("timestamp without time zone");
            entity.Property(asset => asset.CaptureTimeTag).HasMaxLength(256);
            entity.Property(asset => asset.CameraMake).HasMaxLength(512);
            entity.Property(asset => asset.CameraMakeTag).HasMaxLength(256);
            entity.Property(asset => asset.CameraModel).HasMaxLength(512);
            entity.Property(asset => asset.CameraModelTag).HasMaxLength(256);
            entity.Property(asset => asset.CameraSerial).HasMaxLength(512);
            entity.Property(asset => asset.CameraSerialTag).HasMaxLength(256);
            entity.Property(asset => asset.MetadataReason).HasMaxLength(1_024);
            entity.HasIndex(asset => asset.SpoolKey).IsUnique();
            entity.HasIndex(asset => new { asset.State, asset.MetadataState, asset.MetadataLeaseUntil, asset.MetadataRetryAt, asset.IngestedAt });
        });
        modelBuilder.Entity<CaptureEntity>(entity =>
        {
            entity.ToTable("captures");
            entity.HasKey(capture => capture.Id);
            entity.Property(capture => capture.CollectionId).HasDefaultValue(ShutterSpool.Domain.Collections.CollectionName.BuiltInId);
            entity.Property(capture => capture.NormalizedStem).HasColumnType("text");
            entity.HasOne<CollectionEntity>().WithMany().HasForeignKey(capture => capture.CollectionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(capture => capture.ReleasedCollectionName).HasMaxLength(255);
            entity.Property(capture => capture.ReleasedCollectionPath).HasMaxLength(1_024);
            entity.HasIndex(capture => capture.CollectionId);
            entity.Property(capture => capture.Policy).HasConversion<string>().HasMaxLength(16).HasDefaultValue(SourceCurationMode.Delay).HasSentinel((SourceCurationMode)(-1));
            entity.Property(capture => capture.State).HasConversion<string>().HasMaxLength(32).HasDefaultValue(CaptureState.CURATING);
            entity.Ignore(capture => capture.Selection);
            entity.Ignore(capture => capture.ReleasedSelection);
            entity.Property(capture => capture.Revision).HasDefaultValue(0);
            entity.Property(capture => capture.Rating).HasDefaultValue(0);
            entity.Property(capture => capture.Label).HasMaxLength(16);
            entity.Property(capture => capture.Held).HasDefaultValue(false);
            entity.Property(capture => capture.IsFavorite).HasDefaultValue(false);
            entity.Property(capture => capture.NoDeliveryIntent).HasDefaultValue(false);
            entity.Property(capture => capture.FavoriteQualificationLostAt);
            entity.Property(capture => capture.RetentionPaused).HasDefaultValue(false);
            entity.Property(capture => capture.RetentionDisposition).HasMaxLength(16).HasDefaultValue("Active");
            entity.Property(capture => capture.TrashReason).HasMaxLength(16);
            entity.Property(capture => capture.RetentionPolicyRevision).HasDefaultValue(1);
            entity.Property(capture => capture.RetentionFactsDirty).HasDefaultValue(true);
            entity.HasIndex(capture => new { capture.RetentionDisposition, capture.RetentionFactsDirty, capture.FirstAcceptedAt, capture.Id })
                .HasDatabaseName("IX_captures_RetentionDisposition_RetentionFactsDirty_FirstAccep");
            entity.HasIndex(capture => new { capture.RetentionDisposition, capture.RetentionTier, capture.RetentionPressureEligibleAt, capture.Id })
                .HasDatabaseName("IX_captures_RetentionDisposition_RetentionTier_RetentionPressur")
                .HasFilter("\"RetentionDisposition\" = 'Active' AND NOT \"RetentionFactsDirty\"");
            entity.HasIndex(capture => new { capture.RetentionDisposition, capture.RetentionTier, capture.RetentionTrashEligibleAt, capture.Id })
                .HasDatabaseName("IX_captures_RetentionDisposition_RetentionTier_RetentionTrashEl")
                .HasFilter("\"RetentionDisposition\" = 'Active' AND NOT \"RetentionFactsDirty\"");
            entity.HasIndex(capture => new { capture.RetentionDisposition, capture.RetentionAttentionAt, capture.Id })
                .HasDatabaseName("IX_captures_retention_attention")
                .HasFilter("\"RetentionDisposition\" = 'Active' AND NOT \"RetentionFactsDirty\"");
            entity.Property(capture => capture.LastPrimaryIngestedAt).HasDefaultValue(DateTimeOffset.UnixEpoch);
            entity.Property(capture => capture.Tags).HasColumnType("text[]").HasDefaultValue(Array.Empty<string>());
            entity.Property(capture => capture.ReleasedLabel).HasMaxLength(16);
            entity.Property(capture => capture.FirstAcceptedAt).HasDefaultValue(DateTimeOffset.UnixEpoch);
            entity.Property(capture => capture.ReleasedTags).HasColumnType("text[]").HasDefaultValue(Array.Empty<string>());
            entity.Property(capture => capture.RoutingSnapshotJson).HasColumnType("jsonb");
            entity.HasIndex(capture => capture.SourceId);
            entity.HasIndex(capture => new { capture.Held, capture.ReleaseDueAt });
            entity.HasIndex(capture => new { capture.FirstAcceptedAt, capture.Id });
            entity.HasIndex(capture => new { capture.RetentionDisposition, capture.TrashEnteredAt, capture.Id });
        });
        modelBuilder.Entity<CaptureAssetEntity>(entity =>
        {
            entity.ToTable("capture_assets");
            entity.HasKey(asset => asset.AssetId);
            entity.Property(asset => asset.Role).HasMaxLength(16);
            entity.Property(asset => asset.GroupingReason).HasMaxLength(64);
            entity.Property(asset => asset.FavoriteQualificationLostAt);
            entity.HasOne<CaptureEntity>().WithMany(capture => capture.Assets).HasForeignKey(asset => asset.CaptureId);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(asset => asset.AssetId);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(asset => asset.RelatedAssetId);
            entity.HasIndex(asset => new { asset.CaptureId, asset.Role }).IsUnique()
                .HasFilter("\"IsPrimary\" = true AND \"Role\" IN ('RAW', 'JPEG')");
            entity.HasIndex(asset => asset.CaptureId);
            entity.HasIndex(asset => asset.RelatedAssetId);
        });
        modelBuilder.Entity<PreviewPrewarmEntity>(entity =>
        {
            entity.ToTable("preview_prewarms");
            entity.HasKey(job => new { job.AssetId, job.Profile });
            entity.Property(job => job.Profile).HasMaxLength(16);
            entity.Property(job => job.State).HasMaxLength(16);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(job => job.AssetId);
            entity.HasOne<CaptureEntity>().WithMany().HasForeignKey(job => job.CaptureId);
            entity.HasIndex(job => new { job.State, job.RetryAt, job.LeaseUntil });
        });
        modelBuilder.Entity<DestinationRoutingEntity>(entity =>
        {
            entity.ToTable("destination_routings");
            entity.HasKey(config => config.DestinationId);
            entity.Property(config => config.RulesJson).HasColumnType("jsonb");
        });
        modelBuilder.Entity<RoutedAssetEntity>(entity =>
        {
            entity.ToTable("routed_assets");
            entity.HasKey(asset => asset.AssetId);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(asset => asset.AssetId);
        });
        modelBuilder.Entity<RoutingDecisionEntity>(entity =>
        {
            entity.ToTable("routing_decisions");
            entity.HasKey(decision => decision.Id);
            entity.Property(decision => decision.Kind).HasConversion<string>().HasMaxLength(32);
            entity.Property(decision => decision.TargetPath).HasMaxLength(2_048);
            entity.Property(decision => decision.TargetPathKey).HasMaxLength(2_048);
            entity.Property(decision => decision.CollectionName).HasMaxLength(255);
            entity.Property(decision => decision.CollectionPath).HasMaxLength(1_024);
            entity.Property(decision => decision.Explanation).HasMaxLength(1_024);
            entity.Property(decision => decision.RuleSnapshotJson).HasColumnType("jsonb");
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(decision => decision.AssetId);
            entity.HasOne<CaptureEntity>().WithMany().HasForeignKey(decision => decision.CaptureId);
            entity.HasIndex(decision => new { decision.AssetId, decision.DestinationId, decision.Generation }).IsUnique();
            entity.HasIndex(decision => new { decision.AssetId, decision.DestinationId }).IsUnique().HasFilter("\"IsCurrent\" = true");
            entity.HasIndex(decision => new { decision.CaptureId, decision.CreatedAt, decision.Id });
            entity.HasIndex(decision => new { decision.DestinationId, decision.TargetPathKey }).IsUnique()
                .HasFilter("\"IsCurrent\" = true AND \"Kind\" = 'Matched'");
        });
        modelBuilder.Entity<DeliveryEntity>(entity =>
        {
            entity.ToTable("deliveries");
            entity.HasKey(delivery => delivery.Id);
            entity.Property(delivery => delivery.State).HasConversion<string>().HasMaxLength(16);
            entity.Property(delivery => delivery.TargetPath).HasMaxLength(2_048);
            entity.Property(delivery => delivery.RetryReason).HasMaxLength(1_024);
            entity.Property(delivery => delivery.ConfirmationEvidence).HasMaxLength(128);
            entity.Property(delivery => delivery.ConfirmedAt);
            entity.HasOne<RoutingDecisionEntity>().WithMany().HasForeignKey(delivery => delivery.DecisionId);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(delivery => delivery.SourceAssetId);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(delivery => delivery.AssetId);
            entity.HasIndex(delivery => delivery.DecisionId).IsUnique();
            entity.HasIndex(delivery => new { delivery.State, delivery.NextAttemptAt, delivery.LeaseUntil, delivery.CreatedAt });
            entity.HasIndex(delivery => new { delivery.AssetId, delivery.CreatedAt, delivery.Id });
        });
        modelBuilder.Entity<DeliveryAttemptEntity>(entity =>
        {
            entity.ToTable("delivery_attempts");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.WorkerId).HasMaxLength(128);
            entity.Property(attempt => attempt.Result).HasMaxLength(32);
            entity.Property(attempt => attempt.ErrorCode).HasMaxLength(64);
            entity.Property(attempt => attempt.ErrorMessage).HasMaxLength(1_024);
            entity.Property(attempt => attempt.ConfirmationEvidence).HasMaxLength(128);
            entity.Property(attempt => attempt.TraceId).HasMaxLength(128);
            entity.HasOne<DeliveryEntity>().WithMany().HasForeignKey(attempt => attempt.DeliveryId);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(attempt => attempt.AssetId);
            entity.HasIndex(attempt => new { attempt.DeliveryId, attempt.RetryNumber }).IsUnique();
            entity.HasIndex(attempt => new { attempt.DeliveryId, attempt.StartedAt, attempt.Id });
        });
        modelBuilder.Entity<RoutingActionEntity>(entity =>
        {
            entity.ToTable("routing_actions");
            entity.HasKey(action => action.Id);
            entity.Property(action => action.Kind).HasMaxLength(24);
            entity.Property(action => action.ProposedTarget).HasMaxLength(2_048);
            entity.Property(action => action.Fingerprint).HasMaxLength(128);
            entity.Property(action => action.Result).HasMaxLength(64);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(action => action.AssetId);
            entity.HasOne<RoutingDecisionEntity>().WithMany().HasForeignKey(action => action.DecisionId);
            entity.HasOne<RoutingDecisionEntity>().WithMany().HasForeignKey(action => action.SourceDecisionId);
            entity.HasOne<DeliveryEntity>().WithMany().HasForeignKey(action => action.SourceDeliveryId);
            entity.HasOne<DeliveryEntity>().WithMany().HasForeignKey(action => action.DeliveryId);
            entity.HasIndex(action => new { action.AssetId, action.DestinationId, action.CreatedAt });
            entity.HasIndex(action => new { action.AssetId, action.CreatedAt, action.Id });
        });
        modelBuilder.Entity<AssetRemovalOperationEntity>(entity =>
        {
            entity.ToTable("asset_removal_operations");
            entity.HasKey(operation => operation.AssetId);
            entity.HasOne<AssetEntity>().WithMany().HasForeignKey(operation => operation.AssetId);
            entity.HasOne<CaptureEntity>().WithMany().HasForeignKey(operation => operation.CaptureId);
            entity.Property(operation => operation.SpoolKey).HasMaxLength(1_024);
            // Preserve database defaults without changing EF's explicit-value insert behavior.
            entity.Property(operation => operation.CaptureId).HasDefaultValue(Guid.Empty).ValueGeneratedNever();
            entity.Property(operation => operation.ExpectedSize).HasDefaultValue(0L).ValueGeneratedNever();
            entity.Property(operation => operation.StorageMarker).HasMaxLength(32).HasDefaultValue("").ValueGeneratedNever();
            entity.Property(operation => operation.KnownTransientKeys).HasColumnType("text[]").HasDefaultValue(Array.Empty<string>()).ValueGeneratedNever();
            entity.Property(operation => operation.Scope).HasMaxLength(24);
            entity.Property(operation => operation.Reason).HasMaxLength(32);
            entity.Property(operation => operation.State).HasMaxLength(16);
            entity.Property(operation => operation.FailureCode).HasMaxLength(64);
            entity.Property(operation => operation.RemovedBytes).HasDefaultValue(0L);
            entity.HasIndex(operation => operation.OperationId).IsUnique();
            entity.HasIndex(operation => new { operation.State, operation.DueAt });
            entity.HasIndex(operation => new { operation.CaptureId, operation.State });
        });
        modelBuilder.Entity<RetentionPolicyEntity>(entity =>
        {
            entity.ToTable("retention_policy", table => table.HasCheckConstraint("ck_retention_policy_singleton", "\"Id\" = 1"));
            entity.HasKey(policy => policy.Id);
            entity.Property(policy => policy.PolicyJson).HasColumnType("jsonb");
            entity.Property(policy => policy.PreviewCacheJson).HasColumnType("jsonb")
                .HasDefaultValue("{\"cacheMode\":0,\"memoryCacheBytes\":67108864}").ValueGeneratedNever();
        });
        modelBuilder.Entity<RetentionHistoryEntity>(entity =>
        {
            entity.ToTable("retention_history");
            entity.HasKey(item => item.EventId);
            entity.Property(item => item.Kind).HasMaxLength(32);
            entity.Property(item => item.State).HasMaxLength(24);
            entity.Property(item => item.Blockers).HasColumnType("text[]");
            entity.HasIndex(item => new { item.CommittedAt, item.EventId });
            entity.HasIndex(item => new { item.EpisodeId, item.Revision }).IsUnique();
        });
        modelBuilder.Entity<RetentionPolicyHistoryEntity>(entity =>
        {
            entity.ToTable("retention_policy_history");
            entity.HasKey(item => item.Revision);
            entity.Property(item => item.PolicyJson).HasColumnType("jsonb");
            entity.Property(item => item.PreviewCacheJson).HasColumnType("jsonb")
                .HasDefaultValue("{\"cacheMode\":0,\"memoryCacheBytes\":67108864}").ValueGeneratedNever();
            entity.HasIndex(item => item.CommittedAt);
        });
        modelBuilder.Entity<RetentionPressureStateEntity>(entity =>
        {
            entity.ToTable("retention_pressure_state", table => table.HasCheckConstraint("ck_retention_pressure_state_singleton", "\"Id\" = 1"));
            entity.HasKey(state => state.Id);
            entity.Property(state => state.State).HasMaxLength(24);
        });
    }
}
