using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Core.Tests;

public sealed class RoutingCollisionPolicyTests
{
    [Fact]
    public void Rejects_same_batch_and_prior_claims_for_other_assets_case_insensitively()
    {
        Guid destination = Guid.NewGuid(), otherDestination = Guid.NewGuid();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), prior = Guid.NewGuid();
        RoutingPathClaim[] candidates =
        [
            new(first, destination, "RAW/Frame.CR3"),
            new(second, destination, "raw/frame.cr3"),
            new(first, otherDestination, "RAW/Frame.CR3"),
            new(second, otherDestination, "different.jpg"),
        ];
        RoutingPathClaim[] existing = [new(prior, otherDestination, "Different.JPG")];

        IReadOnlyDictionary<int, string> conflicts = RoutingCollisionPolicy.FindConflicts(candidates, existing);

        Assert.Equal("Multiple Assets resolve to the same destination path.", conflicts[0]);
        Assert.Equal(conflicts[0], conflicts[1]);
        Assert.False(conflicts.ContainsKey(2));
        Assert.Equal("Another Asset already uses the resolved destination path.", conflicts[3]);
    }

    [Fact]
    public void Own_successful_target_does_not_block_reuse_but_another_new_asset_does()
    {
        Guid destination = Guid.NewGuid(), first = Guid.NewGuid(), second = Guid.NewGuid();
        RoutingPathClaim previous = new(first, destination, "frame.jpg");
        Assert.Empty(RoutingCollisionPolicy.FindConflicts([previous], [previous]));
        Assert.Single(RoutingCollisionPolicy.FindConflicts([new(second, destination, "FRAME.JPG")], [previous]));
    }
}
