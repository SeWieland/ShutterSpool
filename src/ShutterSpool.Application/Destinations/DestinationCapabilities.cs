namespace ShutterSpool.Application.Destinations;

// Deployment configuration, never a tenant-editable setting.
public sealed class DestinationCapabilities
{
    public static DestinationCapabilities All { get; } = new(["local", "webdav"]);
    public IReadOnlyList<string> EnabledKinds { get; }

    public DestinationCapabilities(string[] enabledKinds)
    {
        if (enabledKinds.Any(kind => kind is not ("local" or "webdav")))
            throw new ArgumentException("The deployment contains an unsupported destination kind.");
        EnabledKinds = Array.AsReadOnly(enabledKinds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    public bool IsEnabled(string kind) => EnabledKinds.Contains(kind, StringComparer.Ordinal);

    public void EnsureEnabled(string kind)
    {
        if (!IsEnabled(kind)) throw new ArgumentException("This destination kind is unavailable in this deployment.");
    }
}
