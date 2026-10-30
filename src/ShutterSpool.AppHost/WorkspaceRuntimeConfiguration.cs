using Microsoft.Extensions.Configuration;

namespace ShutterSpool.AppHost;

internal static class WorkspaceRuntimeConfiguration
{
    internal static IReadOnlyDictionary<string, string> ForServer(IConfiguration configuration)
    {
        bool enabled = configuration.GetValue<bool>("ShutterSpool:Workspaces:Enabled");
        var values = new Dictionary<string, string> { ["ShutterSpool__Workspaces__Enabled"] = enabled ? "true" : "false" };
        if (!enabled) return values;
        values["ShutterSpool__Workspaces__UsableCapacityBytes"] = configuration["ShutterSpool:Workspaces:UsableCapacityBytes"]
            ?? throw new InvalidOperationException("Workspace mode requires usable deployment capacity.");
        string[] references = ["shutterspool-control", .. configuration.GetSection("ShutterSpool:Workspaces:Definitions").GetChildren()
            .Select(section => section["ConnectionReference"] ?? throw new InvalidOperationException("Each configured workspace requires its runtime connection reference."))];
        foreach (string reference in references.Distinct(StringComparer.Ordinal))
        {
            if (reference.Length is < 1 or > 128 || reference.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
                || reference.Contains("migration", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Workspace runtime connection references must be distinct from migration credentials.");
            string path = configuration[$"ConnectionStrings:{reference}File"]
                ?? throw new InvalidOperationException("AppHost workspace runtime connections require mounted secret file references.");
            values[$"ConnectionStrings__{reference}File"] = Path.GetFullPath(path);
        }
        foreach (string section in new[] { "ShutterSpool:Destinations:EnabledKinds", "ShutterSpool:Destinations:WebDavEgress:AllowedPrivateNetworks" })
            foreach (IConfigurationSection item in configuration.GetSection(section).GetChildren())
                if (item.Value is not null) values[item.Path.Replace(":", "__", StringComparison.Ordinal)] = item.Value;
        return values;
    }
}
