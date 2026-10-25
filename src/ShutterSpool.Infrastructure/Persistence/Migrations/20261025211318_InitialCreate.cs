using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ShutterSpool.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Also reject adoption through direct EF tooling, which bypasses startup's spool check.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "__EFMigrationsHistory") OR EXISTS (
                        SELECT 1 FROM pg_catalog.pg_class AS relation
                        JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
                        WHERE left(schema.nspname, 3) <> 'pg_' AND schema.nspname <> 'information_schema'
                            AND relation.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
                            AND NOT (schema.nspname = 'public' AND relation.relname = '__EFMigrationsHistory')) THEN
                        RAISE EXCEPTION 'The initial migration requires a fresh application database. Preserve existing database, spool, keys and identity state; no conversion is supported.';
                    END IF;
                END $$;
                """);

            // Key custody is SQL-owned and intentionally outside the EF entity model.
            migrationBuilder.Sql("""
                CREATE TABLE deployment_key_checks (
                    id integer PRIMARY KEY CHECK (id = 1),
                    key_check text NOT NULL
                );
                """);
            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SpoolKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IngestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecoveryCorrelation = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RetentionPolicyRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    MetadataState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Pending"),
                    MetadataAttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    MetadataLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    MetadataLeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MetadataRetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MetadataOutcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    DetectedMediaType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MetadataRole = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CaptureTimeLocal = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CaptureTimeOffset = table.Column<TimeSpan>(type: "interval", nullable: true),
                    CaptureTimeFractionalDigits = table.Column<int>(type: "integer", nullable: true),
                    CaptureTimeTag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CaptureTimeUncertain = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CameraMake = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CameraMakeTag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CameraModel = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CameraModelTag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CameraSerial = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    CameraSerialTag = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExtensionMismatch = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    MetadataReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "authorization_grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsBootstrap = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_authorization_grants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "collections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Path = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Identity = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "destinations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ConfigurationRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "gallery_protections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccessRevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gallery_protections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ingest_operations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalName = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    PartialKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FinalKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    QuarantineReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ingest_operations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "preservation_holds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Authority = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Resolution = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preservation_holds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "privacy_erasure_assets",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpoolKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ExpectedSize = table.Column<long>(type: "bigint", nullable: false),
                    KnownTransientKeys = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    StorageMarker = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MetadataRedactedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DerivativesRemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    OriginalsRemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DerivativesErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OriginalsErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_erasure_assets", x => x.AssetId);
                });

            migrationBuilder.CreateTable(
                name: "privacy_erasure_export_assets",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    PageIndex = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpoolKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ExpectedSize = table.Column<long>(type: "bigint", nullable: false),
                    KnownTransientKeys = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    StorageMarker = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_erasure_export_assets", x => new { x.SnapshotId, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "privacy_erasure_exports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    PageSize = table.Column<int>(type: "integer", nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_erasure_exports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "privacy_erasure_import_assets",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    PageIndex = table.Column<int>(type: "integer", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpoolKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ExpectedSize = table.Column<long>(type: "bigint", nullable: false),
                    KnownTransientKeys = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    StorageMarker = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_erasure_import_assets", x => new { x.SnapshotId, x.Ordinal });
                });

            migrationBuilder.CreateTable(
                name: "privacy_erasure_imports",
                columns: table => new
                {
                    SnapshotId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordCount = table.Column<int>(type: "integer", nullable: false),
                    PageSize = table.Column<int>(type: "integer", nullable: false),
                    PageCount = table.Column<int>(type: "integer", nullable: false),
                    ManifestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReceivedPageCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_erasure_imports", x => x.SnapshotId);
                });

            migrationBuilder.CreateTable(
                name: "privacy_erasures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_privacy_erasures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "retention_history",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    UsedBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    PlannedReclamationBytes = table.Column<long>(type: "bigint", nullable: false),
                    PurgedBytes = table.Column<long>(type: "bigint", nullable: false),
                    Blockers = table.Column<string[]>(type: "text[]", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_history", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "retention_policy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PolicyJson = table.Column<string>(type: "jsonb", nullable: false),
                    PreviewCacheJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{\"cacheMode\":0,\"memoryCacheBytes\":67108864}"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_policy", x => x.Id);
                    table.CheckConstraint("ck_retention_policy_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "retention_policy_history",
                columns: table => new
                {
                    Revision = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolicyJson = table.Column<string>(type: "jsonb", nullable: false),
                    PreviewCacheJson = table.Column<string>(type: "jsonb", nullable: false, defaultValue: "{\"cacheMode\":0,\"memoryCacheBytes\":67108864}"),
                    CommittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_policy_history", x => x.Revision);
                });

            migrationBuilder.CreateTable(
                name: "retention_pressure_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EpisodeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HistoryPrunedBefore = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_pressure_state", x => x.Id);
                    table.CheckConstraint("ck_retention_pressure_state_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "spool_quota_observations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsKnown = table.Column<bool>(type: "boolean", nullable: false),
                    ActiveBytes = table.Column<long>(type: "bigint", nullable: false),
                    TrashBytes = table.Column<long>(type: "bigint", nullable: false),
                    PartialBytes = table.Column<long>(type: "bigint", nullable: false),
                    QuarantineBytes = table.Column<long>(type: "bigint", nullable: false),
                    DiskPreviewBytes = table.Column<long>(type: "bigint", nullable: false),
                    DriveTotalBytes = table.Column<long>(type: "bigint", nullable: true),
                    DriveAvailableBytes = table.Column<long>(type: "bigint", nullable: true),
                    FilesystemReserveBytes = table.Column<long>(type: "bigint", nullable: true),
                    StorageMarker = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    UnknownReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spool_quota_observations", x => x.Id);
                    table.CheckConstraint("ck_spool_quota_observations_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "spool_quota_reservations",
                columns: table => new
                {
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    WrittenBytes = table.Column<long>(type: "bigint", nullable: false),
                    FilesystemReservedBytes = table.Column<long>(type: "bigint", nullable: false),
                    SpoolKeys = table.Column<string[]>(type: "text[]", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false, defaultValue: "Active"),
                    Category = table.Column<string>(type: "text", nullable: false, defaultValue: "Spool"),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spool_quota_reservations", x => x.OperationId);
                    table.CheckConstraint("ck_spool_quota_reservation_bytes", "\"ReservedBytes\" > 0 AND \"WrittenBytes\" >= 0 AND \"WrittenBytes\" <= \"ReservedBytes\" AND \"FilesystemReservedBytes\" >= 0");
                    table.CheckConstraint("ck_spool_quota_reservation_category", "\"Category\" IN ('Spool', 'DiskPreview')");
                    table.CheckConstraint("ck_spool_quota_reservation_state", "\"State\" IN ('Active', 'Committed')");
                });

            migrationBuilder.CreateTable(
                name: "spool_quota_settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: true),
                    HighWatermarkBytes = table.Column<long>(type: "bigint", nullable: true),
                    RecoveryTargetBytes = table.Column<long>(type: "bigint", nullable: true),
                    StorageMarkerAdoptionAuthorized = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spool_quota_settings", x => x.Id);
                    table.CheckConstraint("ck_spool_quota_settings_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "routed_assets",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    EvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routed_assets", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_routed_assets_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "captures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000012")),
                    ReleasedCollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReleasedCollectionName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReleasedCollectionPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    NormalizedStem = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Rating = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Label = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Tags = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    Policy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Delay"),
                    DelaySeconds = table.Column<int>(type: "integer", nullable: true),
                    LastPrimaryIngestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValue: new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0))),
                    ReleaseDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Held = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IsFavorite = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    NoDeliveryIntent = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    FavoriteQualificationLostAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetentionPaused = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RetentionDisposition = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Active"),
                    TrashReason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    TrashEnteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecoverableUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RestoreProtectedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetentionPolicyRevision = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    RetentionFactsDirty = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    RetentionTier = table.Column<int>(type: "integer", nullable: true),
                    RetentionPressureEligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetentionTrashEligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetentionAttentionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "CURATING"),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedRevision = table.Column<int>(type: "integer", nullable: true),
                    ReleasedRating = table.Column<int>(type: "integer", nullable: true),
                    ReleasedLabel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ReleasedTags = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    FirstAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValue: new DateTimeOffset(new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0))),
                    RoutingSnapshotJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_captures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_captures_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000012")),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConfigurationRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CurationMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CurationDelaySeconds = table.Column<int>(type: "integer", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    SourceType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectionProfile = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sources_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "destination_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    EncryptedSecret = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_destination_credentials_destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "destination_routings",
                columns: table => new
                {
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RulesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_destination_routings", x => x.DestinationId);
                    table.ForeignKey(
                        name: "FK_destination_routings_destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "local_destinations",
                columns: table => new
                {
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RootPath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_local_destinations", x => x.DestinationId);
                    table.ForeignKey(
                        name: "FK_local_destinations_destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "webdav_destinations",
                columns: table => new
                {
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webdav_destinations", x => x.DestinationId);
                    table.ForeignKey(
                        name: "FK_webdav_destinations_destinations_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "destinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "gallery_protection_assets",
                columns: table => new
                {
                    ProtectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gallery_protection_assets", x => new { x.ProtectionId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_gallery_protection_assets_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gallery_protection_assets_gallery_protections_ProtectionId",
                        column: x => x.ProtectionId,
                        principalTable: "gallery_protections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "preservation_hold_assets",
                columns: table => new
                {
                    HoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preservation_hold_assets", x => new { x.HoldId, x.AssetId });
                    table.ForeignKey(
                        name: "FK_preservation_hold_assets_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_preservation_hold_assets_preservation_holds_HoldId",
                        column: x => x.HoldId,
                        principalTable: "preservation_holds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_removal_operations",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false, defaultValue: new Guid("00000000-0000-0000-0000-000000000000")),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SpoolKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ExpectedSize = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    StorageMarker = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: ""),
                    KnownTransientKeys = table.Column<string[]>(type: "text[]", nullable: false, defaultValue: new string[0]),
                    Scope = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PolicyRevision = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AuthorizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RemovedBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_removal_operations", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_asset_removal_operations_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_asset_removal_operations_captures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "captures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "capture_assets",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false),
                    RelatedAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupingReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FavoriteQualificationLostAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capture_assets", x => x.AssetId);
                    table.ForeignKey(
                        name: "FK_capture_assets_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_capture_assets_assets_RelatedAssetId",
                        column: x => x.RelatedAssetId,
                        principalTable: "assets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_capture_assets_captures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "captures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "preview_prewarms",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Profile = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedWidth = table.Column<int>(type: "integer", nullable: true),
                    CompletedHeight = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_preview_prewarms", x => new { x.AssetId, x.Profile });
                    table.ForeignKey(
                        name: "FK_preview_prewarms_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_preview_prewarms_captures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "captures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "routing_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CaptureId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CollectionName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CollectionPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RuleId = table.Column<string>(type: "text", nullable: true),
                    RuleName = table.Column<string>(type: "text", nullable: true),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    TargetPath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    TargetPathKey = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Explanation = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RuleSnapshotJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_routing_decisions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_routing_decisions_captures_CaptureId",
                        column: x => x.CaptureId,
                        principalTable: "captures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "camera_logins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    VirtualRoot = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    CredentialGeneration = table.Column<long>(type: "bigint", nullable: false),
                    ConnectionProfile = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camera_logins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camera_logins_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_authentication_observations",
                columns: table => new
                {
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_authentication_observations", x => x.SourceId);
                    table.ForeignKey(
                        name: "FK_source_authentication_observations_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "source_credentials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    VirtualRoot = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    SecretVerifier = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CredentialGeneration = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    IsIngressEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_credentials", x => x.Id);
                    table.ForeignKey(
                        name: "FK_source_credentials_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetPath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RetryReason = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmationEvidence = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RepeatsDeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_deliveries_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_deliveries_assets_SourceAssetId",
                        column: x => x.SourceAssetId,
                        principalTable: "assets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_deliveries_routing_decisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "routing_decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "camera_upload_admissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    VirtualPath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    AdmissionTimestamp = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    MaximumUploadBytes = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    ReceiveKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    Size = table.Column<long>(type: "bigint", nullable: true),
                    CompletionTimestamp = table.Column<long>(type: "bigint", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_camera_upload_admissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_camera_upload_admissions_camera_logins_LoginId",
                        column: x => x.LoginId,
                        principalTable: "camera_logins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_camera_upload_admissions_sources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "sources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RetryNumber = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    BytesSent = table.Column<long>(type: "bigint", nullable: true),
                    ConfirmationEvidence = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_delivery_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_delivery_attempts_deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "routing_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProposedTarget = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_routing_actions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_routing_actions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_routing_actions_deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "deliveries",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_routing_actions_deliveries_SourceDeliveryId",
                        column: x => x.SourceDeliveryId,
                        principalTable: "deliveries",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_routing_actions_routing_decisions_DecisionId",
                        column: x => x.DecisionId,
                        principalTable: "routing_decisions",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_routing_actions_routing_decisions_SourceDecisionId",
                        column: x => x.SourceDecisionId,
                        principalTable: "routing_decisions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_removal_operations_CaptureId_State",
                table: "asset_removal_operations",
                columns: new[] { "CaptureId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_removal_operations_OperationId",
                table: "asset_removal_operations",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_removal_operations_State_DueAt",
                table: "asset_removal_operations",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_SpoolKey",
                table: "assets",
                column: "SpoolKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_State_MetadataState_MetadataLeaseUntil_MetadataRetry~",
                table: "assets",
                columns: new[] { "State", "MetadataState", "MetadataLeaseUntil", "MetadataRetryAt", "IngestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_authorization_grants_Issuer_Subject",
                table: "authorization_grants",
                columns: new[] { "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camera_logins_SourceId",
                table: "camera_logins",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_camera_upload_admissions_LoginId",
                table: "camera_upload_admissions",
                column: "LoginId",
                unique: true,
                filter: "\"State\" IN ('Admitted', 'Receiving', 'Received', 'Handoff')");

            migrationBuilder.CreateIndex(
                name: "IX_camera_upload_admissions_LoginId_SessionId_VirtualPath_Admis",
                table: "camera_upload_admissions",
                columns: new[] { "LoginId", "SessionId", "VirtualPath", "AdmissionTimestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_camera_upload_admissions_LoginId_SessionId_VirtualPath_Compl",
                table: "camera_upload_admissions",
                columns: new[] { "LoginId", "SessionId", "VirtualPath", "CompletionTimestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_camera_upload_admissions_SourceId",
                table: "camera_upload_admissions",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_capture_assets_CaptureId",
                table: "capture_assets",
                column: "CaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_capture_assets_CaptureId_Role",
                table: "capture_assets",
                columns: new[] { "CaptureId", "Role" },
                unique: true,
                filter: "\"IsPrimary\" = true AND \"Role\" IN ('RAW', 'JPEG')");

            migrationBuilder.CreateIndex(
                name: "IX_capture_assets_RelatedAssetId",
                table: "capture_assets",
                column: "RelatedAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_captures_CollectionId",
                table: "captures",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_captures_FirstAcceptedAt_Id",
                table: "captures",
                columns: new[] { "FirstAcceptedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_captures_Held_ReleaseDueAt",
                table: "captures",
                columns: new[] { "Held", "ReleaseDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_captures_retention_attention",
                table: "captures",
                columns: new[] { "RetentionDisposition", "RetentionAttentionAt", "Id" },
                filter: "\"RetentionDisposition\" = 'Active' AND NOT \"RetentionFactsDirty\"");

            migrationBuilder.CreateIndex(
                name: "IX_captures_RetentionDisposition_RetentionFactsDirty_FirstAccep",
                table: "captures",
                columns: new[] { "RetentionDisposition", "RetentionFactsDirty", "FirstAcceptedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_captures_RetentionDisposition_RetentionTier_RetentionPressur",
                table: "captures",
                columns: new[] { "RetentionDisposition", "RetentionTier", "RetentionPressureEligibleAt", "Id" },
                filter: "\"RetentionDisposition\" = 'Active' AND NOT \"RetentionFactsDirty\"");

            migrationBuilder.CreateIndex(
                name: "IX_captures_RetentionDisposition_RetentionTier_RetentionTrashEl",
                table: "captures",
                columns: new[] { "RetentionDisposition", "RetentionTier", "RetentionTrashEligibleAt", "Id" },
                filter: "\"RetentionDisposition\" = 'Active' AND NOT \"RetentionFactsDirty\"");

            migrationBuilder.CreateIndex(
                name: "IX_captures_RetentionDisposition_TrashEnteredAt_Id",
                table: "captures",
                columns: new[] { "RetentionDisposition", "TrashEnteredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_captures_SourceId",
                table: "captures",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_collections_Identity",
                table: "collections",
                column: "Identity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_collections_IsBuiltIn",
                table: "collections",
                column: "IsBuiltIn",
                unique: true,
                filter: "\"IsBuiltIn\"");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_AssetId_CreatedAt_Id",
                table: "deliveries",
                columns: new[] { "AssetId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_DecisionId",
                table: "deliveries",
                column: "DecisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_SourceAssetId",
                table: "deliveries",
                column: "SourceAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_deliveries_State_NextAttemptAt_LeaseUntil_CreatedAt",
                table: "deliveries",
                columns: new[] { "State", "NextAttemptAt", "LeaseUntil", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_AssetId",
                table: "delivery_attempts",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_DeliveryId_RetryNumber",
                table: "delivery_attempts",
                columns: new[] { "DeliveryId", "RetryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_delivery_attempts_DeliveryId_StartedAt_Id",
                table: "delivery_attempts",
                columns: new[] { "DeliveryId", "StartedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_destination_credentials_DestinationId",
                table: "destination_credentials",
                column: "DestinationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_destinations_Name",
                table: "destinations",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gallery_protection_assets_AssetId",
                table: "gallery_protection_assets",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_gallery_protections_ExpiresAt",
                table: "gallery_protections",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ingest_operations_Id",
                table: "ingest_operations",
                column: "Id",
                filter: "\"State\" NOT IN ('Committed', 'Quarantined')");

            migrationBuilder.CreateIndex(
                name: "IX_preservation_hold_assets_AssetId",
                table: "preservation_hold_assets",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_preview_prewarms_CaptureId",
                table: "preview_prewarms",
                column: "CaptureId");

            migrationBuilder.CreateIndex(
                name: "IX_preview_prewarms_State_RetryAt_LeaseUntil",
                table: "preview_prewarms",
                columns: new[] { "State", "RetryAt", "LeaseUntil" });

            migrationBuilder.CreateIndex(
                name: "IX_privacy_erasure_assets_OperationId_State",
                table: "privacy_erasure_assets",
                columns: new[] { "OperationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_privacy_erasure_export_assets_SnapshotId_PageIndex_Ordinal",
                table: "privacy_erasure_export_assets",
                columns: new[] { "SnapshotId", "PageIndex", "Ordinal" });

            migrationBuilder.CreateIndex(
                name: "IX_privacy_erasure_exports_CreatedAt",
                table: "privacy_erasure_exports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_privacy_erasure_import_assets_SnapshotId_PageIndex_AssetId",
                table: "privacy_erasure_import_assets",
                columns: new[] { "SnapshotId", "PageIndex", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_privacy_erasures_State_RequestedAt",
                table: "privacy_erasures",
                columns: new[] { "State", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_retention_history_CommittedAt_EventId",
                table: "retention_history",
                columns: new[] { "CommittedAt", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_retention_history_EpisodeId_Revision",
                table: "retention_history",
                columns: new[] { "EpisodeId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_retention_policy_history_CommittedAt",
                table: "retention_policy_history",
                column: "CommittedAt");

            migrationBuilder.CreateIndex(
                name: "IX_routing_actions_AssetId_CreatedAt_Id",
                table: "routing_actions",
                columns: new[] { "AssetId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_routing_actions_AssetId_DestinationId_CreatedAt",
                table: "routing_actions",
                columns: new[] { "AssetId", "DestinationId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_routing_actions_DecisionId",
                table: "routing_actions",
                column: "DecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_routing_actions_DeliveryId",
                table: "routing_actions",
                column: "DeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_routing_actions_SourceDecisionId",
                table: "routing_actions",
                column: "SourceDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_routing_actions_SourceDeliveryId",
                table: "routing_actions",
                column: "SourceDeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_routing_decisions_AssetId_DestinationId",
                table: "routing_decisions",
                columns: new[] { "AssetId", "DestinationId" },
                unique: true,
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "IX_routing_decisions_AssetId_DestinationId_Generation",
                table: "routing_decisions",
                columns: new[] { "AssetId", "DestinationId", "Generation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_routing_decisions_CaptureId_CreatedAt_Id",
                table: "routing_decisions",
                columns: new[] { "CaptureId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_routing_decisions_DestinationId_TargetPathKey",
                table: "routing_decisions",
                columns: new[] { "DestinationId", "TargetPathKey" },
                unique: true,
                filter: "\"IsCurrent\" = true AND \"Kind\" = 'Matched'");

            migrationBuilder.CreateIndex(
                name: "IX_source_credentials_SourceId",
                table: "source_credentials",
                column: "SourceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_source_credentials_Username",
                table: "source_credentials",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sources_CollectionId",
                table: "sources",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_sources_Name",
                table: "sources",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_spool_quota_reservations_CreatedAt",
                table: "spool_quota_reservations",
                column: "CreatedAt");
            // Initial quota and retention state stays disabled and unreconciled until explicitly configured.
            migrationBuilder.InsertData(
                table: "spool_quota_settings",
                columns: ["Id", "Revision", "IsActive", "QuotaBytes", "HighWatermarkBytes", "RecoveryTargetBytes"],
                values: [1, 0, false, null, null, null]);

            migrationBuilder.InsertData(
                table: "spool_quota_observations",
                columns: ["Id", "ObservedAt", "IsKnown", "ActiveBytes", "TrashBytes", "PartialBytes", "QuarantineBytes", "DiskPreviewBytes", "DriveTotalBytes", "DriveAvailableBytes", "FilesystemReserveBytes", "UnknownReason"],
                values: [1, DateTimeOffset.MinValue, false, 0L, 0L, 0L, 0L, 0L, null, null, null, "not-yet-reconciled"]);

            migrationBuilder.InsertData(
                table: "retention_pressure_state",
                columns: ["Id", "EpisodeId", "Revision", "State", "StartedAt", "UpdatedAt"],
                values: [1, null, 0L, "Normal", null, DateTimeOffset.MinValue]);

            const string defaultPolicy = """{"revision":1,"automaticRemovalEnabled":false,"trashRecoveryWindow":"3.00:00:00","restoreProtection":"3.00:00:00","galleryMaximum":"30.00:00:00","confirmed":{"pressureAge":"01:00:00","ordinaryAge":"1.00:00:00"},"noDelivery":{"pressureAge":"1.00:00:00","ordinaryAge":"7.00:00:00"},"waiting":{"pressureAge":"1.00:00:00","ordinaryAge":"7.00:00:00"},"heldOrUnfinished":{"pressureAge":"14.00:00:00","ordinaryAge":"180.00:00:00"},"completedAttemptHistoryDays":90,"completedPressureHistoryDays":30}""";
            migrationBuilder.InsertData(
                table: "retention_policy",
                columns: ["Id", "Revision", "PolicyJson", "UpdatedAt"],
                values: [1, 1, defaultPolicy, DateTimeOffset.MinValue]);
            migrationBuilder.InsertData(
                table: "retention_policy_history",
                columns: ["Revision", "PolicyJson", "CommittedAt"],
                values: [1, defaultPolicy, DateTimeOffset.MinValue]);

            // Seed once; later operator edits must never be overwritten by model seed updates.
            migrationBuilder.Sql("""
                INSERT INTO collections ("Id", "Name", "Path", "Identity", "Revision", "IsBuiltIn")
                VALUES ('00000000-0000-0000-0000-000000000012', 'ShutterSpool', 'ShutterSpool', 'SHUTTERSPOOL', 0, true);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new InvalidOperationException("The initial database baseline cannot be downgraded. Restore complete matching database, spool, keys and identity state with its compatible application instead.");
        }
    }
}
