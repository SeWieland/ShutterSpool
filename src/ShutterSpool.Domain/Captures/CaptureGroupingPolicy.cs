using System.Text;

namespace ShutterSpool.Domain.Captures;

public sealed record GroupingAssetFacts(Guid Id, string OriginalName, string Sha256, long Size, string Role,
    string? CameraModel, string? CameraSerial, DateTime? CaptureTimeLocal, TimeSpan? CaptureTimeOffset,
    int? CaptureTimeFractionalDigits, bool CaptureTimeUncertain);
public sealed record GroupingCaptureFacts(Guid Id, IReadOnlyList<GroupingAssetFacts> Primaries);
public sealed record GroupingReplayFacts(Guid CaptureId, GroupingAssetFacts Prior);
public sealed record GroupingDecision(Guid? CaptureId, Guid? RelatedAssetId, string Reason);

public static class CaptureGroupingPolicy
{
    private const string OtherRole = "OTHER";

    public static string NormalizeStem(string name)
    {
        string basename = name.Replace('\\', '/');
        basename = basename[(basename.LastIndexOf('/') + 1)..];
        return Path.GetFileNameWithoutExtension(basename).Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }

    public static GroupingDecision Decide(GroupingAssetFacts incoming, IReadOnlyList<GroupingCaptureFacts> captures,
        IReadOnlyList<GroupingReplayFacts> priorTransfers)
    {
        GroupingDecision? replay = RecognizeReplay(incoming, captures, priorTransfers);
        if (replay is not null) return replay;

        if (incoming.Role is not ("RAW" or "JPEG" or OtherRole)) return new(null, null, "unpaired_other");
        GroupingCaptureFacts[] complementary = [.. captures.Where(capture => IsComplementary(incoming, capture))];
        GroupingCaptureFacts[] compatible = [.. complementary.Where(capture => CanPair(incoming, capture.Primaries))];
        if (compatible is [var candidate] && !HasCompetingPrimary(incoming, candidate, captures))
            return new(candidate.Id, null, "paired_complement");
        if (compatible.Length > 0) return new(null, null, "ambiguous_candidates");
        if (captures.Any(capture => capture.Primaries.Any(primary => VariantNameConflicts(incoming, primary))))
            return new(null, null, "variant_name_conflict");
        if (complementary.Any(capture => !HasCertainTime(incoming)
            || capture.Primaries.All(primary => !HasCertainTime(primary)))) return new(null, null, "missing_or_uncertain_time");
        return new(null, null, complementary.Length > 0 ? "conflicting_facts" : "no_complementary_candidate");
    }

    private static GroupingDecision? RecognizeReplay(GroupingAssetFacts incoming, IReadOnlyList<GroupingCaptureFacts> captures,
        IReadOnlyList<GroupingReplayFacts> priorTransfers)
    {
        var byId = captures.ToDictionary(capture => capture.Id);
        var replays = priorTransfers.Where(item => byId.TryGetValue(item.CaptureId, out GroupingCaptureFacts? capture)
                && !FactsConflict(incoming, item.Prior) && capture.Primaries.All(primary => !FactsConflict(incoming, primary)))
            .GroupBy(item => item.CaptureId).ToArray();
        if (replays is not [var group]) return null;
        return new(group.Key, group.Min(item => item.Prior.Id), "recognized_replay");
    }

    private static bool VariantNameConflicts(GroupingAssetFacts incoming, GroupingAssetFacts primary) =>
        (incoming.Role == OtherRole || primary.Role == OtherRole) && SameSuppliedName(primary.OriginalName, incoming.OriginalName);

    private static bool IsComplementary(GroupingAssetFacts incoming, GroupingCaptureFacts capture) =>
        !capture.Primaries.Any(primary => (incoming.Role != OtherRole && primary.Role == incoming.Role)
            || VariantNameConflicts(incoming, primary))
        && capture.Primaries.Any(primary => primary.Role is "RAW" or "JPEG" or OtherRole);

    private static bool HasCompetingPrimary(GroupingAssetFacts incoming, GroupingCaptureFacts candidate,
        IReadOnlyList<GroupingCaptureFacts> captures)
    {
        foreach (GroupingCaptureFacts capture in captures)
        {
            if (capture.Id == candidate.Id || capture.Primaries.Any(primary =>
                primary.Role == OtherRole && SameSuppliedName(primary.OriginalName, incoming.OriginalName))) continue;
            if (capture.Primaries.Any(primary => primary.Role == incoming.Role && CanPair(primary, candidate.Primaries)))
                return true;
        }
        return false;
    }

    private static bool CanPair(GroupingAssetFacts asset, IReadOnlyList<GroupingAssetFacts> primaries) =>
        primaries.Any(primary => CompatibleTime(asset, primary)) && primaries.All(primary => !FactsConflict(asset, primary));

    private static bool FactsConflict(GroupingAssetFacts left, GroupingAssetFacts right) =>
        Conflicts(left.CameraModel, right.CameraModel) || Conflicts(left.CameraSerial, right.CameraSerial)
        || (HasCertainTime(left) && HasCertainTime(right) && (left.CaptureTimeOffset is null) == (right.CaptureTimeOffset is null)
            && !TimesEqual(left, right));

    private static bool CompatibleTime(GroupingAssetFacts left, GroupingAssetFacts right) =>
        HasCertainTime(left) && HasCertainTime(right)
        && (left.CaptureTimeOffset is null) == (right.CaptureTimeOffset is null) && TimesEqual(left, right);

    private static bool HasCertainTime(GroupingAssetFacts facts) => facts.CaptureTimeLocal is not null && !facts.CaptureTimeUncertain;

    private static bool TimesEqual(GroupingAssetFacts left, GroupingAssetFacts right)
    {
        long leftTicks = left.CaptureTimeLocal!.Value.Ticks - (left.CaptureTimeOffset?.Ticks ?? 0);
        long rightTicks = right.CaptureTimeLocal!.Value.Ticks - (right.CaptureTimeOffset?.Ticks ?? 0);
        int precision = Math.Clamp(Math.Min(left.CaptureTimeFractionalDigits ?? 0, right.CaptureTimeFractionalDigits ?? 0), 0, 7);
        long unit = TimeSpan.TicksPerSecond;
        for (int digit = 0; digit < precision; digit++) unit /= 10;
        return leftTicks / unit == rightTicks / unit;
    }

    private static bool Conflicts(string? left, string? right) => left is not null && right is not null
        && !string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool SameSuppliedName(string left, string right) => string.Equals(NormalizeSuppliedName(left),
        NormalizeSuppliedName(right), StringComparison.Ordinal);

    private static string NormalizeSuppliedName(string name)
    {
        string basename = name.Replace('\\', '/');
        basename = basename[(basename.LastIndexOf('/') + 1)..];
        return basename.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }
}
