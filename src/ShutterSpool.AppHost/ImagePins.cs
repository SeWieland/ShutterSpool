using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace ShutterSpool.AppHost;

internal sealed record ImagePin(string Image, string Tag, string Sha256);

internal static partial class ImagePins
{
    private static readonly Regex PinnedImage = PinnedImagePattern();

    public static IReadOnlyDictionary<string, ImagePin> Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "deploy", "images", "compose.yaml");
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var services = (YamlMappingNode)root.Children[new YamlScalarNode("services")];
        return new Dictionary<string, ImagePin>
        {
            ["camera-edge"] = Read(services, "camera-edge", path),
            ["imaginary"] = Read(services, "imaginary", path),
        };
    }

    private static ImagePin Read(YamlMappingNode services, string serviceName, string path)
    {
        if (!services.Children.TryGetValue(new YamlScalarNode(serviceName), out var serviceNode)
            || serviceNode is not YamlMappingNode service
            || !service.Children.TryGetValue(new YamlScalarNode("image"), out var imageNode)
            || imageNode is not YamlScalarNode image
            || !PinnedImage.IsMatch(image.Value ?? ""))
            throw new InvalidDataException($"{path} must define a tag and sha256-pinned image for service '{serviceName}'.");
        string reference = image.Value!;
        int digestSeparator = reference.LastIndexOf("@sha256:", StringComparison.Ordinal);
        int tagSeparator = reference.LastIndexOf(':', digestSeparator);
        return new ImagePin(reference[..tagSeparator], reference[(tagSeparator + 1)..digestSeparator], reference[(digestSeparator + 8)..]);
    }

    [GeneratedRegex("^[^\\s@]+:[^\\s@]+@sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedImagePattern();
}
