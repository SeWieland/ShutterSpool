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
        (SourceCurationMode Policy, int DelaySeconds, Guid CollectionId) sourcePolicy = await LoadSourcePolicyAsync(asset.SourceId, cancellationToken);

        GroupingReplayFacts[] priorTransfers = await LoadPriorTransfersAsync(asset, cancellationToken);
        GroupingAssetFacts incoming = Facts(asset);
        GroupingDecision decision = CaptureGroupingPolicy.Decide(incoming,
            captures.Select(capture => new GroupingCaptureFacts(capture.Id,
                primaryEvidence[capture.Id].Select(primary => Facts(primary)).ToArray())).ToArray(),
            priorTransfers);
        Guid changedCaptureId = decision.RelatedAssetId is Guid priorId
            ? AddReplay(incoming, decision.CaptureId!.Value, priorId, decision.Reason)
            : await AddPrimaryAsync(asset, incoming.Role, stem, sourcePolicy, decision, cancellationToken);
        await MarkRetentionFactsDirtyAsync(changedCaptureId, cancellationToken);

        await database.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LastChangedCaptureId = changedCaptureId;
        return true;
    }

    private Guid AddReplay(GroupingAssetFacts incoming, Guid captureId, Guid priorId, string reason)
    {
        database.CaptureAssets.Add(new CaptureAssetEntity
        {
            AssetId = incoming.Id,
            CaptureId = captureId,
            Role = incoming.Role,
            RelatedAssetId = priorId,
            GroupingReason = reason,
        });
        return captureId;
    }

    private async Task<Guid> AddPrimaryAsync(AssetEntity asset, string role, string stem,
        (SourceCurationMode Policy, int DelaySeconds, Guid CollectionId) sourcePolicy,
        GroupingDecision decision, CancellationToken cancellationToken)
    {
        CaptureEntity capture;
        if (decision.CaptureId is Guid matchId)
        {
            capture = await database.Captures.SingleAsync(item => item.Id == matchId, cancellationToken);
            if (capture.ReleasedAt is null)
            {
                capture.LastPrimaryIngestedAt = Max(capture.LastPrimaryIngestedAt, asset.IngestedAt);
                capture.ReleaseDueAt = CaptureCurationPolicy.DueAt(capture.Policy, capture.DelaySeconds, capture.LastPrimaryIngestedAt);
            }
        }
        else
        {
            capture = new CaptureEntity
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
            capture.ReleaseDueAt = CaptureCurationPolicy.DueAt(capture.Policy, capture.DelaySeconds, asset.IngestedAt);
            database.Captures.Add(capture);
        }
        database.CaptureAssets.Add(new CaptureAssetEntity
        {
            AssetId = asset.Id,
            CaptureId = capture.Id,
            Role = role,
            IsPrimary = true,
            GroupingReason = decision.Reason,
        });
        if (role == "JPEG")
        {
            foreach (PreviewProfile profile in Enum.GetValues<PreviewProfile>())
                database.PreviewPrewarms.Add(new PreviewPrewarmEntity
                {
                    AssetId = asset.Id,
                    CaptureId = capture.Id,
                    Profile = profile.ToString(),
                });
        }
        return capture.Id;
    }

    private async Task MarkRetentionFactsDirtyAsync(Guid captureId, CancellationToken cancellationToken)
    {
        CaptureEntity? affected = database.Captures.Local.SingleOrDefault(capture => capture.Id == captureId);
        if (affected is not null) affected.RetentionFactsDirty = true;
        else await database.Captures.Where(capture => capture.Id == captureId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(capture => capture.RetentionFactsDirty, true), cancellationToken);
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

    private static GroupingAssetFacts Facts(AssetEntity asset) =>
        Facts(asset, asset.MetadataRole is "RAW" or "JPEG" ? asset.MetadataRole : "OTHER");

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
