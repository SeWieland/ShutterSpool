#:property PublishAot=false
#:property RestorePackagesWithLockFile=false
#:property UseAppHost=false

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

if (args is not [var depsFile, var packageDirectory, var fallbackDirectory, var outputDirectory])
{
    Console.Error.WriteLine("Usage: collect-dotnet-notices <publish.deps.json> <NuGet package directory> <fallback directory> <output directory>");
    return 1;
}

try
{
    using var deps = JsonDocument.Parse(File.ReadAllText(depsFile));
    using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Join(fallbackDirectory, "manifest.json")));
    var fallbacks = manifest.RootElement.EnumerateArray()
        .SelectMany(group => group.GetProperty("packages").EnumerateArray()
            .Select(package => (Package: package.GetString()!, Documents: group.GetProperty("documents"))))
        .ToDictionary(entry => entry.Package, entry => entry.Documents, StringComparer.OrdinalIgnoreCase);
    var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
    var libraries = deps.RootElement.GetProperty("libraries");
    var index = new List<string> { "ShutterSpool bundled .NET dependencies", "" };
    var documents = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    foreach (var library in deps.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
    {
        if (libraries.GetProperty(library.Name).GetProperty("type").GetString() != "package" || !HasRuntimeAssets(library.Value))
        {
            continue;
        }

        try
        {
            var identity = library.Name.ToLowerInvariant().Split('/');
            if (identity.Length != 2 || !identity.All(IsSafeName))
            {
                throw new InvalidDataException("Invalid package identity.");
            }

            var archivePath = Path.Join(packageDirectory, identity[0], identity[1], $"{identity[0]}.{identity[1]}.nupkg");
            using var archive = ZipFile.OpenRead(archivePath);
            var nuspec = archive.Entries.Single(entry => entry.FullName.Equals($"{identity[0]}.nuspec", StringComparison.OrdinalIgnoreCase));
            using var nuspecStream = nuspec.Open();
            var specification = XDocument.Load(nuspecStream);
            var ns = specification.Root!.Name.Namespace;
            var metadata = specification.Root.Element(ns + "metadata")!;
            var license = metadata.Element(ns + "license");
            var declaredLicense = (string?)license?.Attribute("type") == "file" ? license.Value : null;
            var declaredFile = declaredLicense is null ? null : archive.GetEntry(declaredLicense);
            if (declaredLicense is not null && declaredFile is null)
            {
                throw new InvalidDataException($"Declared license file '{declaredLicense}' is missing from the NuGet archive.");
            }

            index.Add(library.Name);
            if (metadata.Element(ns + "copyright") is { Value: var copyright } && !string.IsNullOrWhiteSpace(copyright))
            {
                index.Add($"  {copyright}");
            }

            var hasLicense = false;
            foreach (var entry in archive.Entries.OrderBy(entry => entry.FullName, StringComparer.Ordinal))
            {
                var isLicense = entry == declaredFile || IsLicenseFile(entry.Name);
                if (!isLicense && !Regex.IsMatch(entry.Name, @"^(?:third[ ._-]*party[ ._-]*)?(?:notices?|copyrights?|licenses?)[-_ .]?(?:[a-z0-9]+[.])?(?:txt|md|html?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    continue;
                }

                using var stream = entry.Open();
                using var contents = new MemoryStream();
                stream.CopyTo(contents);
                RecordDocument(entry.Name, contents.ToArray(), $"NuGet {library.Name}:{entry.FullName}");
                hasLicense |= isLicense;
            }

            if (fallbacks.TryGetValue(library.Name, out var upstreamDocuments))
            {
                foreach (var file in upstreamDocuments.EnumerateArray())
                {
                    var name = file.GetProperty("file").GetString()!;
                    if (!IsSafeName(name))
                    {
                        throw new InvalidDataException($"Invalid upstream license or notice name '{name}'.");
                    }

                    RecordDocument(name, File.ReadAllBytes(Path.Join(fallbackDirectory, name)), file.GetProperty("source").GetString()!);
                    hasLicense |= file.GetProperty("license").GetBoolean();
                }
            }

            if (!hasLicense)
            {
                throw new InvalidDataException("No license text was found. Add the upstream license and applicable notice files to tools/dotnet-notices/manifest.json.");
            }

            index.Add("");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException)
        {
            throw new InvalidDataException($"{library.Name}: {exception.Message}", exception);
        }
    }

    if (documents.Count == 0)
    {
        throw new InvalidDataException($"No bundled NuGet packages found in '{depsFile}'.");
    }

    Directory.CreateDirectory(Path.Join(outputDirectory, "documents"));
    foreach (var (path, contents) in documents)
    {
        File.WriteAllBytes(Path.Join(outputDirectory, path), contents);
    }

    File.WriteAllText(Path.Join(outputDirectory, "INDEX.txt"), string.Join('\n', index));
    return 0;

    void RecordDocument(string name, byte[] contents, string source)
    {
        using var reader = new StreamReader(new MemoryStream(contents));
        var text = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(source) ||
            !text.Any(character => !char.IsWhiteSpace(character) && !char.IsControl(character) && character != '\uFEFF'))
        {
            throw new InvalidDataException($"Invalid license or notice document '{name}'.");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(contents));
        var safeName = Regex.Replace(name, @"[^A-Za-z0-9._+-]+", "-").TrimEnd('.');
        var path = $"documents/{hash}-{safeName}";
        documents.TryAdd(path, contents);
        index.Add($"  {path} ({source})");
    }
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static bool HasRuntimeAssets(JsonElement package)
    => package.EnumerateObject().Any(kind => kind.Name is "runtime" or "native" or "resources" or "runtimeTargets"
        && kind.Value.EnumerateObject().Any(asset => Path.GetFileName(asset.Name) != "_._"));

static bool IsSafeName(string name)
    => Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9._+-]*$");

static bool IsLicenseFile(string name)
    => Regex.IsMatch(name, @"^(?:licen[sc]e|copying)(?:\.(?:txt|md|html?))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
