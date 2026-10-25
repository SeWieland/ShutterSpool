using ShutterSpool.Application.Retention;
using ShutterSpool.Domain.Retention;

namespace ShutterSpool.Infrastructure.Persistence;

internal static class CaptureRetentionSummaryBuilder
{
    internal static IReadOnlyDictionary<Guid, CaptureRetentionSummary> Build(
        CaptureEntity[] captures, DateTimeOffset now, RetentionSettings settings, RetentionSummaryEvidence evidence)
    {
        var result = new Dictionary<Guid, CaptureRetentionSummary>(captures.Length);
        foreach (CaptureEntity capture in captures)
        {
            RetentionMemberFact[] captureMembers = [.. evidence.Assets.MembersByCapture[capture.Id]];
            RetentionAssetDecision[] retention = [.. captureMembers.Select(member =>
                BuildAssetDecision(capture, member, settings, evidence))];
            RetentionOriginal[] originals = [.. captureMembers.Select(member => BuildOriginal(capture, member))];
            result[capture.Id] = BuildCaptureSummary(capture, captureMembers, retention, originals, now, evidence);
        }
        return result;
    }

    private static RetentionAssetDecision BuildAssetDecision(CaptureEntity capture, RetentionMemberFact member,
        RetentionSettings settings, RetentionSummaryEvidence evidence)
    {
        RetentionDeliveryEvidence deliveriesByRoute = evidence.Deliveries;
        Guid[] evidenceIds = EvidenceAssetIds(member);
        RetentionDeliveryFact[] deliveries = [.. evidenceIds.Distinct().Where(deliveriesByRoute.ByAsset.ContainsKey).SelectMany(assetId => deliveriesByRoute.ByAsset[assetId])];
        Guid[] intendedDecisionIds = [.. evidenceIds.Where(deliveriesByRoute.IntentIdsByAsset.ContainsKey)
            .SelectMany(assetId => deliveriesByRoute.IntentIdsByAsset[assetId]).Distinct()];
        bool intended = deliveries.Length > 0 || intendedDecisionIds.Length > 0;
        bool confirmed = intended && deliveries.All(IsConfirmedDelivery)
            && intendedDecisionIds.All(decisionId => IsConfirmedDecision(decisionId, deliveriesByRoute));
        DateTimeOffset? finalConfirmed = confirmed ? deliveries.Max(delivery => delivery.ConfirmedAt) : null;
        DateTimeOffset? protectedUntil = Max(capture.RestoreProtectedUntil, member.GalleryProtectedUntil);
        var facts = new RetentionAssetFacts
        {
            AcceptedAt = member.IngestedAt,
            FinalIntendedDeliveryConfirmedAt = finalConfirmed,
            FavoriteQualificationLostAt = member.FavoriteQualificationLostAt,
            HasDeliveryIntent = intended,
            HasPositiveConfirmationForEveryIntendedDelivery = confirmed,
            IsRecognizedReplay = member.GroupingReason == "recognized_replay",
            ExplicitNoDelivery = capture.NoDeliveryIntent,
            Favorite = capture.IsFavorite,
            Held = capture.Held,
            IsUnknownOrQuarantined = member.State != "Spooled" || member.RemovalState is not null,
            HasAvailableOriginal = member.State == "Spooled" && member.RemovalState is null,
            ProtectedUntil = protectedUntil,
        };
        RetentionSettings policy = evidence.Assets.Policies.GetValueOrDefault(member.RetentionPolicyRevision, settings);
        return RetentionPolicy.Evaluate(facts, policy.Confirmed, policy.NoDelivery, policy.Waiting, policy.HeldOrUnfinished);
    }

    private static bool IsConfirmedDecision(Guid decisionId, RetentionDeliveryEvidence evidence)
    {
        RetentionIntentFact decision = evidence.Intents.Single(item => item.Id == decisionId);
        bool hasDeliveries = evidence.ByDecision.TryGetValue(decisionId, out RetentionDeliveryFact[]? routeDeliveries)
            && routeDeliveries.Length > 0;
        bool allDeliveriesConfirmed = hasDeliveries && routeDeliveries!.All(IsConfirmedDelivery);
        bool sameTargetConfirmed = !hasDeliveries && decision.TargetPath is not null
            && evidence.Facts.Any(delivery => delivery.AssetId == decision.AssetId
                && delivery.DestinationId == decision.DestinationId && IsConfirmedDelivery(delivery)
                && string.Equals(delivery.TargetPath, decision.TargetPath, StringComparison.OrdinalIgnoreCase));
        return RetentionQualification.IsConfirmedIntent(decision.Kind, hasDeliveries, allDeliveriesConfirmed, sameTargetConfirmed);
    }

    private static bool IsConfirmedDelivery(RetentionDeliveryFact delivery) =>
        delivery.State == ShutterSpool.Domain.Delivery.DeliveryState.Delivered && delivery.ConfirmedAt is not null;

    private static RetentionOriginal BuildOriginal(CaptureEntity capture, RetentionMemberFact member)
    {
        RetentionOriginalState state = member.RemovalState switch
        {
            "Removed" => RetentionOriginalState.Removed,
            "Authorized" or "Removing" => RetentionOriginalState.PendingRemoval,
            "Failed" => RetentionOriginalState.FailedRemoval,
            _ when member.State == "Spooled" => RetentionOriginalState.Available,
            _ => RetentionOriginalState.Missing,
        };
        return new(member.AssetId, member.Role, member.Size,
            Max(capture.RestoreProtectedUntil, member.GalleryProtectedUntil), state);
    }

    private static CaptureRetentionSummary BuildCaptureSummary(CaptureEntity capture,
        RetentionMemberFact[] members, RetentionAssetDecision[] retention, RetentionOriginal[] originals,
        DateTimeOffset now, RetentionSummaryEvidence evidence)
    {
        bool hasPreservationHold = members.Any(member => member.HasPreservationHold);
        RetentionCaptureDecision whole = RetentionPolicy.EvaluateCapture(retention, now);
        if (hasPreservationHold) whole = whole with { PressureEligibleAt = null, TrashEligibleAt = null, IsEligible = false };
        DateTimeOffset? protectedUntil = retention.Where(item => item.ProtectedUntil > now).Select(item => item.ProtectedUntil).Max();
        DateTimeOffset? intervalStartedAt = retention.Select(item => item.IntervalStartedAt).Max();
        RetentionTimeline? timeline = BuildTimeline(capture, whole, intervalStartedAt, now);
        string[] protections = BuildProtections(capture, members, hasPreservationHold, now);
        Guid[] memberIds = [.. members.Select(member => member.AssetId)];
        int routedCount = memberIds.Count(evidence.Routing.RoutedAssets.Contains);
        int noMatchCount = memberIds.Count(evidence.Routing.NoMatchAssets.Contains);
        bool hasDeliveryHistory = members.SelectMany(EvidenceAssetIds).Any(evidence.Deliveries.ByAsset.ContainsKey);
        string[] noDeliveryReasons = BuildNoDeliveryReasons(capture, memberIds, routedCount, noMatchCount,
            evidence.Routing.MatchedCaptures.Contains(capture.Id), hasDeliveryHistory,
            memberIds.Any(evidence.Routing.ErasedAssets.Contains));
        RetentionUrgency urgency = GetUrgency(capture, whole, intervalStartedAt, now);
        RetentionDisposition disposition = Enum.TryParse(capture.RetentionDisposition, out RetentionDisposition parsed)
            ? parsed : RetentionDisposition.Active;
        return new(now, capture.Revision, capture.RetentionPolicyRevision,
            disposition,
            capture.IsFavorite, whole.Tier, whole.PressureEligibleAt, whole.TrashEligibleAt, capture.RecoverableUntil,
            protectedUntil, whole.Reasons, protections, urgency, timeline, originals, capture.RetentionPaused,
            capture.RetentionPaused ? ["retention-transition"] : [], noDeliveryReasons.Length == 0, noDeliveryReasons);
    }

    private static RetentionTimeline? BuildTimeline(CaptureEntity capture, RetentionCaptureDecision whole,
        DateTimeOffset? intervalStartedAt, DateTimeOffset now) =>
        capture.RetentionDisposition is "Trash" or "Purging" or "Purged"
            ? new(RetentionTimelineKind.TrashRecovery, capture.TrashEnteredAt, capture.RecoverableUntil, now,
                capture.RetentionDisposition switch
                {
                    "Purging" => "Permanent removal is in progress",
                    "Purged" => "Original removal is complete",
                    _ => "Guaranteed Trash recovery window",
                })
            : whole.Tier is null ? null : new(
                whole.Tier == RetentionTier.HeldOrUnfinished ? RetentionTimelineKind.HeldOrBlocked : RetentionTimelineKind.NormalRetention,
                intervalStartedAt, whole.TrashEligibleAt, now,
                whole.Tier == RetentionTier.HeldOrUnfinished ? "Held or unfinished retention"
                    : whole.Tier == RetentionTier.ConfirmedFavorite ? "Favorite remains protected while all intended deliveries are confirmed"
                    : "Ordinary retention");

    private static string[] BuildProtections(CaptureEntity capture, RetentionMemberFact[] members,
        bool hasPreservationHold, DateTimeOffset now)
    {
        var protections = new List<string>(3);
        if (capture.RestoreProtectedUntil > now) protections.Add("restore-protection");
        if (members.Any(member => member.GalleryProtectedUntil > now)) protections.Add("gallery-promise");
        if (hasPreservationHold) protections.Add("preservation-hold");
        return [.. protections];
    }

    private static string[] BuildNoDeliveryReasons(CaptureEntity capture, Guid[] memberIds, int routedCount,
        int noMatchCount, bool hasMatchedIntent, bool hasDeliveryHistory, bool erasurePending)
    {
        var reasons = new List<string>(7);
        if (capture.RetentionDisposition != "Active") reasons.Add("capture-not-active");
        if (memberIds.Length == 0) reasons.Add("no-members");
        else if (routedCount != memberIds.Length) reasons.Add("members-not-routed");
        if (memberIds.Length > 0 && noMatchCount != memberIds.Length) reasons.Add("route-outcome-not-no-match");
        if (hasMatchedIntent) reasons.Add("matched-route-exists");
        if (hasDeliveryHistory) reasons.Add("delivery-history-exists");
        if (capture.NoDeliveryIntent) reasons.Add("already-recorded");
        if (erasurePending) reasons.Add("privacy-erasure-pending");
        return [.. reasons];
    }

    private static RetentionUrgency GetUrgency(CaptureEntity capture, RetentionCaptureDecision whole,
        DateTimeOffset? intervalStartedAt, DateTimeOffset now) =>
        RetentionPolicy.AttentionAt(whole.Tier, intervalStartedAt,
            capture.RetentionDisposition == "Trash" ? capture.RecoverableUntil : whole.TrashEligibleAt,
            trashRecovery: capture.RetentionDisposition == "Trash") is DateTimeOffset attentionAt && attentionAt <= now
            ? whole.Tier == RetentionTier.HeldOrUnfinished || capture.RetentionDisposition == "Trash"
                ? RetentionUrgency.Urgent : RetentionUrgency.Actionable
            : RetentionUrgency.Informational;

    private static Guid[] EvidenceAssetIds(RetentionMemberFact member) =>
        member.GroupingReason == "recognized_replay" && member.RelatedAssetId is Guid supplierAssetId
            ? [member.AssetId, supplierAssetId] : [member.AssetId];

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;
}

internal sealed record RetentionMemberFact(Guid CaptureId, Guid AssetId, Guid? RelatedAssetId,
    string Role, string GroupingReason, DateTimeOffset? FavoriteQualificationLostAt,
    long Size, DateTimeOffset IngestedAt, string State, int RetentionPolicyRevision, string? RemovalState,
    DateTimeOffset? GalleryProtectedUntil, bool HasPreservationHold);

internal sealed record RetentionDeliveryFact(Guid DecisionId, Guid AssetId, Guid DestinationId,
    string? TargetPath, ShutterSpool.Domain.Delivery.DeliveryState State, DateTimeOffset? ConfirmedAt);

internal sealed record RetentionIntentFact(Guid Id, Guid AssetId, Guid DestinationId, string? TargetPath,
    RoutingDecisionKindValue Kind);

internal sealed record RetentionAssetEvidence(IReadOnlyDictionary<int, RetentionSettings> Policies,
    ILookup<Guid, RetentionMemberFact> MembersByCapture);

internal sealed record RetentionDeliveryEvidence(RetentionDeliveryFact[] Facts, RetentionIntentFact[] Intents,
    IReadOnlyDictionary<Guid, RetentionDeliveryFact[]> ByAsset,
    IReadOnlyDictionary<Guid, Guid[]> IntentIdsByAsset,
    IReadOnlyDictionary<Guid, RetentionDeliveryFact[]> ByDecision);

internal sealed record RetentionCaptureRoutingEvidence(HashSet<Guid> RoutedAssets, HashSet<Guid> NoMatchAssets,
    HashSet<Guid> MatchedCaptures, HashSet<Guid> ErasedAssets);

internal sealed record RetentionSummaryEvidence(RetentionAssetEvidence Assets,
    RetentionDeliveryEvidence Deliveries, RetentionCaptureRoutingEvidence Routing);
