using YamlDotNet.RepresentationModel;

namespace ShutterSpool.AppHost;

internal static class ImagePins
{
    public static IReadOnlyDictionary<string, ImagePin> Load()
    {
        string deployPath = Path.Combine(AppContext.BaseDirectory, "deploy");
        var pins = new Dictionary<string, ImagePin>();
        AddPins(pins, Path.Combine(deployPath, "images", "compose.yaml"), "camera-edge", "imaginary");
        foreach (string name in new[] { "postgres", "valkey", "keycloak" })
            AddPins(pins, Path.Combine(deployPath, "package", "components", $"compose.{name}.yaml"), name);
        return pins;
    }

    private static void AddPins(Dictionary<string, ImagePin> pins, string path, params string[] serviceNames)
    {
        using var reader = File.OpenText(path);
        var yaml = new YamlStream();
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var services = (YamlMappingNode)root.Children[new YamlScalarNode("services")];
        foreach (string name in serviceNames)
            pins.Add(name, Read(services, name, path));
    }

    private static ImagePin Read(YamlMappingNode services, string serviceName, string path)
    {
        if (!services.Children.TryGetValue(new YamlScalarNode(serviceName), out var serviceNode)
            || serviceNode is not YamlMappingNode service
            || !service.Children.TryGetValue(new YamlScalarNode("image"), out var imageNode)
            || imageNode is not YamlScalarNode { Value: string image }
            || ImagePin.ParseOrNull(image) is not ImagePin pin)
            throw new InvalidDataException($"{path} must define a tag and sha256-pinned image for service '{serviceName}'.");
        return pin;
    }
}
