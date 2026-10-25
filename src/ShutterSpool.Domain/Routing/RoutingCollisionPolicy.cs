namespace ShutterSpool.Domain.Routing;

public sealed record RoutingPathClaim(Guid AssetId, Guid DestinationId, string TargetPath);

public static class RoutingCollisionPolicy
{
    public static IReadOnlyDictionary<int, string> FindConflicts(IReadOnlyList<RoutingPathClaim> candidates,
        IReadOnlyList<RoutingPathClaim> existing)
    {
        var conflicts = new Dictionary<int, string>();
        // ponytail: linear scans fit small Capture batches; index by destination/path if claimed targets grow large.
        for (int i = 0; i < candidates.Count; i++)
        {
            RoutingPathClaim candidate = candidates[i];
            if (candidates.Any(other => other.AssetId != candidate.AssetId && SameTarget(candidate, other)))
                conflicts[i] = "Multiple Assets resolve to the same destination path.";
            else if (existing.Any(other => other.AssetId != candidate.AssetId && SameTarget(candidate, other)))
                conflicts[i] = "Another Asset already uses the resolved destination path.";
        }
        return conflicts;
    }

    private static bool SameTarget(RoutingPathClaim left, RoutingPathClaim right) =>
        left.DestinationId == right.DestinationId
        && string.Equals(left.TargetPath, right.TargetPath, StringComparison.OrdinalIgnoreCase);
}
