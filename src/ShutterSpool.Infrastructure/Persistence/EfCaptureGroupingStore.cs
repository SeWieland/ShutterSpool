using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ShutterSpool.Application.CaptureGrouping;
using ShutterSpool.Application.Preview;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Sources;

namespace ShutterSpool.Infrastructure.Persistence;

internal sealed class EfCaptureGroupingStore(ShutterSpoolDbContext database) : ICaptureGroupingStore
{
    public Guid? LastChangedCaptureId { get; private set; }

    public async Task<bool> GroupOneAsync(CancellationToken cancellationToken)
    {
        LastChangedCaptureId = null;
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await LockRoutingAsync(cancellationToken);
        await LockCollectionAssignmentsAsync(cancellationToken);
        AssetEntity? asset = await database.Assets.FromSqlRaw("""
            SELECT * FROM assets
            WHERE "State" = 'Spooled'
              AND "MetadataState" IN ('Complete', 'Partial', 'Unsupported', 'Failed')
              AND NOT EXISTS (SELECT 1 FROM capture_assets WHERE "AssetId" = assets."Id")
              AND NOT EXISTS (SELECT 1 FROM privacy_erasure_assets erased WHERE erased."AssetId" = assets."Id")
            ORDER BY "IngestedAt", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """).AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return false;
        }

        // The selection snapshot may predate another worker's membership commit if it
        // reached this unlocked Asset tuple afterward. Recheck under the row lock.
        if (await database.CaptureAssets.AsNoTracking().AnyAsync(member => member.AssetId == asset.Id, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        string stem = CaptureGroupingPolicy.NormalizeStem(asset.OriginalName);
        // Grouping locks routing, then Asset, source/stem and Captures.
        await LockStemAsync(asset.SourceId, stem, transaction, cancellationToken);
        await LockCapturesAsync(asset.SourceId, stem, cancellationToken);

        CaptureEntity[] captures = await database.Captures
            .Where(capture => capture.SourceId == asset.SourceId && capture.NormalizedStem == stem
                && capture.RetentionDisposition == "Active"
                && !database.CaptureAssets.Any(member => member.CaptureId == capture.Id
                    && database.PrivacyErasureAssets.Any(erased => erased.AssetId == member.AssetId)))
            .Include(capture => capture.Assets)
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);
        Dictionary<Guid, AssetEntity[]> primaryEvidence = await LoadPrimaryEvidenceAsync(captures, cancellationToken);
        string role = asset.MetadataRole is "RAW" or "JPEG" ? asset.MetadataRole : "OTHER";
        (SourceCurationMode Policy, int DelaySeconds, Guid CollectionId) sourcePolicy = await LoadSourcePolicyAsync(asset.SourceId, cancellationToken);

        GroupingReplayFacts[] priorTransfers = await LoadPriorTransfersAsync(asset, cancellationToken);
        GroupingDecision decision = CaptureGroupingPolicy.Decide(Facts(asset, role),
            captures.Select(capture => new GroupingCaptureFacts(capture.Id,
                primaryEvidence[capture.Id].Select(primary => Facts(primary, primary.MetadataRole is "RAW" or "JPEG" ? primary.MetadataRole : "OTHER")).ToArray())).ToArray(),
            priorTransfers);
        Guid? changedCaptureId = decision.CaptureId;
        if (decision.RelatedAssetId is Guid relatedAssetId)
        {
            database.CaptureAssets.Add(new CaptureAssetEntity
            {
                AssetId = asset.Id,
                CaptureId = decision.CaptureId!.Value,
                Role = role,
                RelatedAssetId = relatedAssetId,
                GroupingReason = "recognized_replay",
            });
        }
        else
        {
            CaptureEntity? match = decision.CaptureId is Guid matchId ? captures.Single(capture => capture.Id == matchId) : null;

            if (match is null)
            {
                match = new CaptureEntity
                {
                    Id = Guid.NewGuid(),
                    SourceId = asset.SourceId,
                    CollectionId = sourcePolicy.CollectionId,
                    NormalizedStem = stem,
                    Policy = sourcePolicy.Policy,
                    DelaySeconds = sourcePolicy.Policy == SourceCurationMode.Delay ? sourcePolicy.DelaySeconds : null,
                    FirstAcceptedAt = asset.IngestedAt,
                    LastPrimaryIngestedAt = asset.IngestedAt,
                    Held = CaptureCurationPolicy.DefaultHeld(sourcePolicy.Policy),
                    State = CaptureState.CURATING,
                };
                match.ReleaseDueAt = CaptureCurationPolicy.DueAt(match.Policy, match.DelaySeconds, asset.IngestedAt);
                database.Captures.Add(match);
            }
            else
            {
                CaptureEntity waitingCapture = await database.Captures.SingleAsync(capture => capture.Id == match.Id, cancellationToken);
                if (waitingCapture.ReleasedAt is null)
                {
                    waitingCapture.LastPrimaryIngestedAt = Max(waitingCapture.LastPrimaryIngestedAt, asset.IngestedAt);
                    waitingCapture.ReleaseDueAt = CaptureCurationPolicy.DueAt(waitingCapture.Policy, waitingCapture.DelaySeconds,
                        waitingCapture.LastPrimaryIngestedAt);
                }
            }

            database.CaptureAssets.Add(new CaptureAssetEntity
            {
                AssetId = asset.Id,
                CaptureId = match.Id,
                Role = role,
                IsPrimary = true,
                GroupingReason = decision.Reason,
            });
            changedCaptureId = match.Id;
            if (role == "JPEG")
            {
                foreach (PreviewProfile profile in Enum.GetValues<PreviewProfile>())
                    database.PreviewPrewarms.Add(new PreviewPrewarmEntity
                    {
                        AssetId = asset.Id,
                        CaptureId = match.Id,
                        Profile = profile.ToString(),
                    });
            }
        }

        if (changedCaptureId is Guid selectedCaptureId)
        {
            CaptureEntity? affected = database.Captures.Local.SingleOrDefault(capture => capture.Id == selectedCaptureId);
            if (affected is not null) affected.RetentionFactsDirty = true;
            else await database.Captures.Where(capture => capture.Id == selectedCaptureId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(capture => capture.RetentionFactsDirty, true),
                    cancellationToken);
        }

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LastChangedCaptureId = changedCaptureId;
        return true;
    }

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private async Task<(SourceCurationMode Policy, int DelaySeconds, Guid CollectionId)> LoadSourcePolicyAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        SourceEntity? source = await database.Sources.FromSqlInterpolated($"SELECT * FROM sources WHERE \"Id\" = {sourceId} FOR SHARE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (source is null) throw new InvalidOperationException("Capture grouping requires an existing Source.");
        SourceCurationMode policy = source.CurationMode;
        int delay = source.CurationDelaySeconds;
        Guid collectionId = source.CollectionId;
        if (!Enum.IsDefined(policy) || delay <= 0 || delay > 31_536_000)
            throw new InvalidOperationException("The Source has an invalid curation policy.");
        if (!await database.Collections.AnyAsync(item => item.Id == collectionId, cancellationToken))
            throw new InvalidOperationException("Capture grouping requires the Source's configured Collection.");
        return (policy, delay, collectionId);
    }

    private async Task<GroupingReplayFacts[]> LoadPriorTransfersAsync(AssetEntity asset, CancellationToken cancellationToken)
    {
        var prior = await (
            from membership in database.CaptureAssets.AsNoTracking()
            join previous in database.Assets.AsNoTracking() on membership.AssetId equals previous.Id
            join capture in database.Captures.AsNoTracking() on membership.CaptureId equals capture.Id
            where previous.SourceId == asset.SourceId
                && previous.OriginalName == asset.OriginalName
                && previous.Sha256 == asset.Sha256
                && previous.Size == asset.Size
                && !database.PrivacyErasureAssets.Any(erased => erased.AssetId == previous.Id)
                && capture.RetentionDisposition == "Active"
                && !database.CaptureAssets.Any(otherMember => otherMember.CaptureId == capture.Id
                    && database.PrivacyErasureAssets.Any(erased => erased.AssetId == otherMember.AssetId))
            select new { membership.CaptureId, membership.Role, Previous = previous }
        ).ToArrayAsync(cancellationToken);
        return [.. prior.Select(item => new GroupingReplayFacts(item.CaptureId, Facts(item.Previous, item.Role)))];
    }

    private async Task<Dictionary<Guid, AssetEntity[]>> LoadPrimaryEvidenceAsync(CaptureEntity[] captures, CancellationToken cancellationToken)
    {
        var ids = captures.SelectMany(capture => capture.Assets.Where(member => member.IsPrimary).Select(member => member.AssetId)).ToArray();
        AssetEntity[] assets = ids.Length == 0
            ? []
            : await database.Assets.AsNoTracking().Where(asset => ids.Contains(asset.Id)).ToArrayAsync(cancellationToken);
        var byId = assets.ToDictionary(asset => asset.Id);
        return captures.ToDictionary(capture => capture.Id,
            capture => capture.Assets.Where(member => member.IsPrimary).Select(member => byId[member.AssetId]).ToArray());
    }

    private static GroupingAssetFacts Facts(AssetEntity asset, string role) => new(asset.Id, asset.OriginalName,
        asset.Sha256, asset.Size, role, asset.CameraModel, asset.CameraSerial, asset.CaptureTimeLocal,
        asset.CaptureTimeOffset, asset.CaptureTimeFractionalDigits, asset.CaptureTimeUncertain);

    private static async Task LockStemAsync(Guid sourceId, string stem, IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = transaction.GetDbTransaction().Connection!.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "lock_key";
        parameter.Value = $"{sourceId:N}:{stem}";
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private async Task LockCapturesAsync(Guid sourceId, string stem, CancellationToken cancellationToken)
    {
        _ = await database.Database.SqlQuery<Guid>($"""
            SELECT "Id" FROM captures
            WHERE "SourceId" = {sourceId} AND "NormalizedStem" = {stem} AND "RetentionDisposition" = 'Active'
              AND NOT EXISTS (SELECT 1 FROM capture_assets member JOIN privacy_erasure_assets erased
                  ON erased."AssetId" = member."AssetId" WHERE member."CaptureId" = captures."Id") FOR UPDATE
            """).ToArrayAsync(cancellationToken);
    }

    private Task<int> LockRoutingAsync(CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", cancellationToken);

    private Task<int> LockCollectionAssignmentsAsync(CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync(
        "SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-collection-assignment', 0))", cancellationToken);

}
