using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ShutterSpool.AppHost;

internal static class WorkspaceRuntimeConfiguration
{
    internal static string PrepareDevelopment(IConfiguration configuration, int identityPort)
    {
        string directory = Path.GetFullPath(configuration["ShutterSpool:Development:RootPath"] ?? "../../.data/workspaces-dev");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "appsettings.json");
        if (File.Exists(path)) return directory;
        string subject = configuration["Parameters:shutterspool-administrator-subject"]
            ?? throw new InvalidOperationException("Set Parameters:shutterspool-administrator-subject before preparing the development workspace.");
        string root = Path.Combine(directory, "studio");
        Directory.CreateDirectory(root);
        var manifest = new
        {
            ShutterSpool = new
            {
                Workspaces = new
                {
                    UsableCapacityBytes = 10737418240L,
                    Definitions = new Dictionary<string, object>
                    {
                        ["studio"] = new { StorageRoot = root, AllocatedBytes = 10737418240L,
                            OwnerIssuer = $"http://localhost:{identityPort}/realms/shutterspool", OwnerSubject = subject },
                    },
                },
            },
        };
        using var output = new FileStream(path, FileMode.CreateNew);
        JsonSerializer.Serialize(output, manifest, new JsonSerializerOptions { WriteIndented = true });
        return directory;
    }

    internal static IReadOnlyDictionary<string, string> ForServer(IConfiguration configuration)
    {
        var values = new Dictionary<string, string>();
        foreach (string section in new[] { "ShutterSpool:Destinations:EnabledKinds", "ShutterSpool:Destinations:WebDavEgress:AllowedPrivateNetworks" })
            foreach (IConfigurationSection item in configuration.GetSection(section).GetChildren())
                if (item.Value is not null) values[item.Path.Replace(":", "__", StringComparison.Ordinal)] = item.Value;
        return values;
    }
}
