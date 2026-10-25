using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Core.Tests;

public sealed class RoutingFactBuilderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Facts_use_supplied_curation_and_only_certain_camera_time(bool uncertain)
    {
        Guid source = Guid.NewGuid();
        var cameraTime = new DateTime(2026, 9, 29, 12, 30, 0, DateTimeKind.Utc);
        var facts = RoutingFactBuilder.Build(new(source, 4, "Purple", ["print"], CaptureSelection.Picked),
            new("Jpeg", @"camera\private\frame.jpg", 512, "image/jpeg", null, "Synthetic", null, cameraTime, uncertain)).Values;

        Assert.Equal(source.ToString("D"), facts["source"].Text);
        Assert.Equal("frame.jpg", facts["suppliedName"].Text);
        Assert.Equal("jpg", facts["extension"].Text);
        Assert.Equal(512m, facts["size"].Number);
        Assert.Equal(4m, facts["rating"].Number);
        Assert.Equal("Picked", facts["selection"].Text);
        Assert.Equal(["print"], facts["tags"].Tags);
        Assert.Equal("Purple", facts["label"].Text);
        Assert.False(facts.ContainsKey("cameraMake"));
        Assert.False(facts.ContainsKey("collectionName"));
        Assert.False(facts.ContainsKey("collectionPath"));
        Assert.Equal(!uncertain, facts.ContainsKey("captureTime"));
        if (!uncertain) Assert.Equal(DateTimeKind.Unspecified, facts["captureTime"].Time!.Value.Kind);
    }

    [Fact]
    public void Collection_facts_use_the_supplied_context_without_reconstructing_a_default()
    {
        var facts = RoutingFactBuilder.Build(new(Guid.NewGuid(), null, null, [], null, "Berlin", "Events/Berlin"),
            new("JPEG", "frame.jpg", 1, null, null, null, null, null, false)).Values;
        Assert.Equal("Berlin", facts["collectionName"].Text);
        Assert.Equal("Events/Berlin", facts["collectionPath"].Text);
    }
}
