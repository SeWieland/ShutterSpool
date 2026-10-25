using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using ShutterSpool.Application.Metadata;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfAssetMetadataStore(ShutterSpoolDbContext database, TimeSpan? leaseDuration = null) : IAssetMetadataStore
{
    private readonly TimeSpan _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5);
    private const int MaximumAttempts = 5;

    public async Task<MetadataWork?> ClaimAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE assets
            SET "MetadataState" = 'Failed',
                "MetadataOutcome" = 'Failed',
                "MetadataReason" = 'Metadata retry limit reached after an expired lease.',
                "MetadataLeaseId" = NULL,
                "MetadataLeaseUntil" = NULL,
                "MetadataRetryAt" = NULL
            WHERE "State" = 'Spooled'
              AND "MetadataState" = 'Processing'
              AND "MetadataLeaseUntil" <= {now}
              AND "MetadataAttemptCount" >= {MaximumAttempts}
              AND NOT EXISTS (SELECT 1 FROM privacy_erasure_assets AS erased WHERE erased."AssetId" = assets."Id")
            """, cancellationToken);

        Guid leaseId = Guid.NewGuid();
        int claimed = await database.Database.ExecuteSqlInterpolatedAsync($"""
            WITH candidate AS (
                SELECT "Id"
                FROM assets
                WHERE "State" = 'Spooled'
                  AND (("MetadataState" = 'Pending' AND ("MetadataRetryAt" IS NULL OR "MetadataRetryAt" <= {now}))
                    OR ("MetadataState" = 'Processing' AND "MetadataLeaseUntil" <= {now}))
                  AND "MetadataAttemptCount" < {MaximumAttempts}
                  AND NOT EXISTS (SELECT 1 FROM privacy_erasure_assets AS erased WHERE erased."AssetId" = assets."Id")
                ORDER BY "IngestedAt", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE assets AS asset
            SET "MetadataState" = 'Processing',
                "MetadataAttemptCount" = asset."MetadataAttemptCount" + 1,
                "MetadataLeaseId" = {leaseId},
                "MetadataLeaseUntil" = {now + _leaseDuration},
                "MetadataRetryAt" = NULL
            FROM candidate
            WHERE asset."Id" = candidate."Id"
            """, cancellationToken);
        if (claimed == 0) return null;

        AssetEntity asset = await database.Assets.AsNoTracking().SingleAsync(asset => asset.MetadataLeaseId == leaseId, cancellationToken);
        return new MetadataWork(asset.Id, asset.SpoolKey, asset.OriginalName, asset.Size, leaseId);
    }

    public async Task<bool> RenewAsync(MetadataWork work, DateTimeOffset now, CancellationToken cancellationToken) =>
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE assets
            SET "MetadataLeaseUntil" = {now + _leaseDuration}
            WHERE "Id" = {work.AssetId}
              AND "State" = 'Spooled'
              AND "MetadataState" = 'Processing'
              AND "MetadataLeaseId" = {work.LeaseId}
              AND "MetadataLeaseUntil" > {now}
              AND NOT EXISTS (SELECT 1 FROM privacy_erasure_assets AS erased WHERE erased."AssetId" = assets."Id")
            """, cancellationToken) == 1;

    public async Task CompleteAsync(MetadataWork work, AssetMetadata metadata, CancellationToken cancellationToken)
    {
        DateTime? captureTime = metadata.CaptureTime is { } time
            ? DateTime.SpecifyKind(time.Local, DateTimeKind.Unspecified)
            : null;
        var captureTimeParameter = new NpgsqlParameter("captureTime", NpgsqlDbType.Timestamp)
        {
            Value = (object?)captureTime ?? DBNull.Value,
        };

        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE assets
            SET "MetadataState" = {metadata.Outcome.ToString()},
                "MetadataOutcome" = {metadata.Outcome.ToString()},
                "DetectedMediaType" = {Limit(metadata.DetectedMediaType, 128)},
                "MetadataRole" = {Limit(metadata.Role, 32)},
                "CaptureTimeLocal" = {captureTimeParameter},
                "CaptureTimeOffset" = {metadata.CaptureTime?.Offset},
                "CaptureTimeFractionalDigits" = {metadata.CaptureTime?.FractionalDigits},
                "CaptureTimeTag" = {Limit(metadata.CaptureTime?.Tag, 256)},
                "CaptureTimeUncertain" = {metadata.CaptureTime?.IsUncertain ?? false},
                "CameraMake" = {Limit(metadata.CameraMake?.Value, 512)},
                "CameraMakeTag" = {Limit(metadata.CameraMake?.Tag, 256)},
                "CameraModel" = {Limit(metadata.CameraModel?.Value, 512)},
                "CameraModelTag" = {Limit(metadata.CameraModel?.Tag, 256)},
                "CameraSerial" = {Limit(metadata.CameraSerial?.Value, 512)},
                "CameraSerialTag" = {Limit(metadata.CameraSerial?.Tag, 256)},
                "ExtensionMismatch" = {metadata.ExtensionMismatch},
                "MetadataReason" = {Limit(metadata.Reason, 1024)},
                "MetadataLeaseId" = NULL,
                "MetadataLeaseUntil" = NULL,
                "MetadataRetryAt" = NULL
            WHERE "Id" = {work.AssetId}
              AND "State" = 'Spooled'
              AND "MetadataState" = 'Processing'
              AND "MetadataLeaseId" = {work.LeaseId}
              AND NOT EXISTS (SELECT 1 FROM privacy_erasure_assets AS erased WHERE erased."AssetId" = assets."Id")
            """, cancellationToken);
    }

    public async Task RetryAsync(MetadataWork work, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE assets
            SET "MetadataState" = CASE WHEN "MetadataAttemptCount" >= {MaximumAttempts} THEN 'Failed' ELSE 'Pending' END,
                "MetadataOutcome" = CASE WHEN "MetadataAttemptCount" >= {MaximumAttempts} THEN 'Failed' ELSE NULL END,
                "MetadataReason" = CASE WHEN "MetadataAttemptCount" >= {MaximumAttempts} THEN 'Metadata retry limit reached.' ELSE 'Metadata processing will retry.' END,
                "MetadataRetryAt" = CASE "MetadataAttemptCount"
                    WHEN 1 THEN {now.AddSeconds(5)}
                    WHEN 2 THEN {now.AddSeconds(30)}
                    WHEN 3 THEN {now.AddMinutes(2)}
                    WHEN 4 THEN {now.AddMinutes(5)}
                    ELSE NULL
                END,
                "MetadataLeaseId" = NULL,
                "MetadataLeaseUntil" = NULL
            WHERE "Id" = {work.AssetId}
              AND "State" = 'Spooled'
              AND "MetadataState" = 'Processing'
              AND "MetadataLeaseId" = {work.LeaseId}
              AND NOT EXISTS (SELECT 1 FROM privacy_erasure_assets AS erased WHERE erased."AssetId" = assets."Id")
            """, cancellationToken);
    }

    private static string? Limit(string? value, int length) => value is { Length: > 0 } && value.Length > length ? value[..length] : value;
}
