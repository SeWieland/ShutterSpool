using ShutterSpool.Domain.Captures;

namespace ShutterSpool.Core.Tests;

public sealed class CaptureVisibilityPolicyTests
{
    [Theory]
    [InlineData(false, false, false, false, -1, true)]
    [InlineData(true, true, true, false, 1, false)]
    [InlineData(true, true, true, true, 1, true)]
    [InlineData(true, true, true, true, -1, false)]
    [InlineData(true, false, true, false, -1, true)]
    [InlineData(true, false, false, true, 2, false)]
    public void Visibility_respects_viewer_scope(bool viewer, bool releasedOnly, bool active,
        bool released, int rating, bool expected)
    {
        var facts = new CaptureVisibilityFacts
        {
            IsViewer = viewer,
            ReleasedOnly = releasedOnly,
            IsActive = active,
            IsReleased = released,
            Rating = rating,
        };

        Assert.Equal(expected, CaptureVisibilityPolicy.IsVisible.Compile()(facts));
    }
}
