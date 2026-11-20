using System.Text.RegularExpressions;

namespace ShutterSpool.AppHost;

internal sealed partial record ImagePin(string Image, string Tag, string Sha256)
{
    private static readonly Regex PinnedImage = PinnedImagePattern();

    public static ImagePin? ParseOrNull(string reference)
    {
        if (!PinnedImage.IsMatch(reference)) return null;

        int digestSeparator = reference.LastIndexOf("@sha256:", StringComparison.Ordinal);
        int tagSeparator = reference.LastIndexOf(':', digestSeparator);
        string repository = reference[..tagSeparator];
        if (!repository.Contains('/')) repository = "library/" + repository;
        return new ImagePin(repository, reference[(tagSeparator + 1)..digestSeparator], reference[(digestSeparator + 8)..]);
    }

    [GeneratedRegex(@"^[^\s@]+:[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}@sha256:[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedImagePattern();
}
