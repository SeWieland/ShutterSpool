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
    public static string NormalizeStem(string name)
    {
        string basename = name.Replace('\\', '/');
        basename = basename[(basename.LastIndexOf('/') + 1)..];
        return Path.GetFileNameWithoutExtension(basename).Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }

    public static GroupingDecision Decide(GroupingAssetFacts incoming, IReadOnlyList<GroupingCaptureFacts> captures,
        IReadOnlyList<GroupingReplayFacts> priorTransfers)
    {
        var byId = captures.ToDictionary(capture => capture.Id);
        var replays = priorTransfers.Where(item => byId.TryGetValue(item.CaptureId, out GroupingCaptureFacts? capture)
                && !FactsConflict(incoming, item.Prior) && capture.Primaries.All(primary => !FactsConflict(incoming, primary)))
            .GroupBy(item => item.CaptureId).ToArray();
        if (replays.Length == 1)
            return new(replays[0].Key, replays[0].OrderBy(item => item.Prior.Id).First().Prior.Id, "recognized_replay");

        if (incoming.Role is not ("RAW" or "JPEG" or "OTHER")) return new(null, null, "unpaired_other");
        GroupingCaptureFacts[] complementary = [.. captures.Where(capture =>
            !capture.Primaries.Any(primary => (incoming.Role != "OTHER" && primary.Role == incoming.Role)
                || ((incoming.Role == "OTHER" || primary.Role == "OTHER")
                    && SameSuppliedName(primary.OriginalName, incoming.OriginalName)))
            && capture.Primaries.Any(primary => primary.Role is "RAW" or "JPEG" or "OTHER"))];
        GroupingCaptureFacts[] compatible = [.. complementary.Where(capture => CanPair(incoming, capture.Primaries))];
        GroupingCaptureFacts[] reciprocal = [.. compatible.Where(candidate => !captures.Where(capture => capture.Id != candidate.Id
                && !capture.Primaries.Any(primary => primary.Role == "OTHER"
                    && SameSuppliedName(primary.OriginalName, incoming.OriginalName)))
            .SelectMany(capture => capture.Primaries.Where(competing => competing.Role == incoming.Role))
            .Any(competing => CanPair(competing, candidate.Primaries)))];
        if (compatible.Length == 1 && reciprocal.Length == 1) return new(reciprocal[0].Id, null, "paired_complement");
        if (compatible.Length > 0) return new(null, null, "ambiguous_candidates");
        if (captures.Any(capture => capture.Primaries.Any(primary => (incoming.Role == "OTHER" || primary.Role == "OTHER")
            && SameSuppliedName(primary.OriginalName, incoming.OriginalName)))) return new(null, null, "variant_name_conflict");
        if (complementary.Any(capture => !HasCertainTime(incoming)
            || capture.Primaries.All(primary => !HasCertainTime(primary)))) return new(null, null, "missing_or_uncertain_time");
        return new(null, null, complementary.Length > 0 ? "conflicting_facts" : "no_complementary_candidate");
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
