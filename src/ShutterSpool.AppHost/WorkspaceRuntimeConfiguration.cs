using Microsoft.Extensions.Configuration;

namespace ShutterSpool.AppHost;

internal static class WorkspaceRuntimeConfiguration
{
    internal static IReadOnlyDictionary<string, string> ForServer(IConfiguration configuration)
    {
        var values = new Dictionary<string, string>();
        foreach (string section in new[] { "ShutterSpool:Destinations:EnabledKinds", "ShutterSpool:Destinations:WebDavEgress:AllowedPrivateNetworks" })
            foreach (IConfigurationSection item in configuration.GetSection(section).GetChildren())
                if (item.Value is not null) values[item.Path.Replace(":", "__", StringComparison.Ordinal)] = item.Value;
        return values;
    }
}
