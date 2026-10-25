using ShutterSpool.Application.Ingest;
using ShutterSpool.Domain.Assets;
using ShutterSpool.Infrastructure.Composition;
using System.Reflection;
using System.Xml.Linq;

namespace ShutterSpool.ArchitectureTests;

public sealed class BoundaryTests
{
    private static readonly string Root = FindRoot();

    [Theory]
    [InlineData("Domain", "")]
    [InlineData("Application", "Domain")]
    [InlineData("Infrastructure", "Application,Domain")]
    [InlineData("Server", "Application,Infrastructure,ServiceDefaults")]
    [InlineData("ServiceDefaults", "")]
    [InlineData("AppHost", "Server")]
    public void Project_references_follow_the_layer_direction(string project, string allowed)
    {
        string path = Path.Combine(Root, "src", $"ShutterSpool.{project}", $"ShutterSpool.{project}.csproj");
        string[] references = [.. XDocument.Load(path).Descendants("ProjectReference")
            .Select(item => Path.GetFileNameWithoutExtension(item.Attribute("Include")!.Value.Replace('\\', '/'))
                .Replace("ShutterSpool.", "", StringComparison.Ordinal))];
        Assert.All(references, reference => Assert.Contains(reference, allowed.Split(',', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void Core_assemblies_have_no_adapter_transport_or_serialization_dependencies()
    {
        foreach (Assembly assembly in new[] { typeof(Asset).Assembly, typeof(IngestService).Assembly })
        {
            Assert.All(assembly.GetReferencedAssemblies(), reference =>
            {
                string name = reference.Name!;
                Assert.True(name == "ShutterSpool.Domain" || name == "System.Runtime" || name == "netstandard"
                    || name.StartsWith("System.", StringComparison.Ordinal), $"Unexpected core dependency: {name}");
                Assert.DoesNotContain("Json", name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("Http", name, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    [Fact]
    public void Infrastructure_exports_only_composition_and_ef_migrations()
    {
        var exported = typeof(InfrastructureServices).Assembly.GetExportedTypes();
        Assert.All(exported, type => Assert.True(type.Namespace == "ShutterSpool.Infrastructure.Composition"
            || type.Namespace == "ShutterSpool.Infrastructure.Persistence.Migrations", $"Public infrastructure type: {type.FullName}"));
        Assert.All(typeof(InfrastructureServices).Assembly.GetTypes().Where(type => type.Name.EndsWith("Entity", StringComparison.Ordinal)
            || type.Name.EndsWith("DbContext", StringComparison.Ordinal)), type => Assert.False(type.IsVisible));
    }

    [Fact]
    public void Server_uses_infrastructure_only_in_composition_and_never_uses_ef()
    {
        string server = Path.Combine(Root, "src", "ShutterSpool.Server");
        foreach (string file in Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("Microsoft.EntityFrameworkCore", source, StringComparison.Ordinal);
            if (!Path.GetRelativePath(server, file).StartsWith($"Composition{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                Assert.DoesNotContain("ShutterSpool.Infrastructure", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Http_dtos_do_not_expose_core_or_persistence_models()
    {
        Type[] dtos = [.. typeof(Program).Assembly.GetExportedTypes().Where(type => type.Namespace?.StartsWith("ShutterSpool.Server.Api.", StringComparison.Ordinal) == true
            && !type.IsAbstract)];
        Assert.NotEmpty(dtos);
        foreach (Type dto in dtos)
            foreach (PropertyInfo property in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                CheckWireType(property.PropertyType, $"{dto.Name}.{property.Name}");
    }

    private static void CheckWireType(Type type, string member)
    {
        string? assembly = type.Assembly.GetName().Name;
        Assert.False(assembly is "ShutterSpool.Domain" or "ShutterSpool.Application" or "ShutterSpool.Infrastructure", $"{member} exposes {type.FullName}");
        if (type.HasElementType) CheckWireType(type.GetElementType()!, member);
        foreach (Type argument in type.GenericTypeArguments) CheckWireType(argument, member);
    }

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ShutterSpool.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}
