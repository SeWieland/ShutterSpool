using ShutterSpool.Domain.Collections;

namespace ShutterSpool.Core.Tests;

public sealed class CollectionNameTests
{
    [Theory]
    [InlineData("/Berlin", "Berlin", "Berlin")]
    [InlineData("Events/Berlin", "Events/Berlin", "Berlin")]
    [InlineData("/Events/Berlin", "Events/Berlin", "Berlin")]
    public void Parses_optional_leading_slash_and_preserves_path(string input, string path, string name)
    {
        CollectionName parsed = CollectionName.Parse(input);

        Assert.Equal(path, parsed.Path);
        Assert.Equal(name, parsed.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("//Berlin")]
    [InlineData("Berlin/")]
    [InlineData("Events\\Berlin")]
    [InlineData("Events//Berlin")]
    [InlineData("Events/../Berlin")]
    [InlineData("Events/./Berlin")]
    [InlineData("C:/Berlin")]
    [InlineData("Events%2fBerlin")]
    [InlineData("Events%5CBerlin")]
    [InlineData("Events/{year}")]
    [InlineData("CON")]
    [InlineData("LPT1.jpg")]
    [InlineData("Events/ trailing")]
    [InlineData("Events/trailing ")]
    [InlineData("Events/trailing.")]
    [InlineData("Events/line\nbreak")]
    public void Rejects_names_that_are_not_safe_relative_paths(string input) =>
        Assert.Throws<ArgumentException>(() => CollectionName.Parse(input));

    [Fact]
    public void Normalizes_unicode_and_case_for_identity()
    {
        Assert.Equal(CollectionName.Parse("Café").Path, CollectionName.Parse("Cafe\u0301").Path);
        Assert.Equal(CollectionName.Parse("/Café").Identity, CollectionName.Parse("cafe\u0301").Identity);
    }

    [Fact]
    public void Applies_limits_to_canonical_path_and_each_segment_without_truncation()
    {
        string segment = new('x', 255);
        string maximum = string.Join('/', segment, segment, segment, new string('x', 254), "x");

        Assert.Equal(1024, maximum.Length);
        Assert.Equal(maximum, CollectionName.Parse("/" + maximum).Path);
        Assert.Throws<ArgumentException>(() => CollectionName.Parse(maximum + "x"));
        Assert.Throws<ArgumentException>(() => CollectionName.Parse(new string('x', 256)));
        Assert.Throws<ArgumentException>(() => CollectionName.Parse("Events/\uD800"));
    }
}
