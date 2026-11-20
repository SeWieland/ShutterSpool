using ShutterSpool.AppHost;

namespace ShutterSpool.IntegrationTests;

public sealed class AppHostImagePinTests
{
    private static readonly string Digest = new('a', 64);

    [Theory]
    [InlineData("registry.example:5000/postgres", "registry.example:5000/postgres")]
    [InlineData("postgres", "library/postgres")]
    public void Parses_tagged_images_and_preserves_registry_identity(string repository, string expectedRepository)
    {
        var pin = ImagePin.ParseOrNull($"{repository}:18.7-trixie@sha256:{Digest}");

        Assert.Equal(new ImagePin(expectedRepository, "18.7-trixie", Digest), pin);
    }

    [Theory]
    [InlineData("registry.example:5000/postgres", "")]
    [InlineData("postgres:-18.7", "")]
    [InlineData("postgres:18.7", "\n")]
    public void Rejects_references_without_a_valid_tag_and_exact_digest_end(string name, string suffix)
    {
        Assert.Null(ImagePin.ParseOrNull($"{name}@sha256:{Digest}{suffix}"));
    }
}
