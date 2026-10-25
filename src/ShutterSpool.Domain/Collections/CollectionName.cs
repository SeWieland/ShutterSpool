using System.Text;

namespace ShutterSpool.Domain.Collections;

public sealed record CollectionName(string Path, string Name)
{
    public const string BuiltInPath = "ShutterSpool";
    public static readonly Guid BuiltInId = Guid.Parse("00000000-0000-0000-0000-000000000012");
    public const int MaximumPathLength = 1_024;
    public const int MaximumSegmentLength = 255;

    public string Identity => Path.ToUpperInvariant();

    public static CollectionName Parse(string? input)
    {
        if (string.IsNullOrEmpty(input)) throw Invalid();
        string path;
        try { path = input.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw Invalid(); }
        if (path.StartsWith('/')) path = path[1..];
        if (path.Length is 0 or > MaximumPathLength || path.Contains('\\')
            || path.Contains('{') || path.Contains('}') || path.Contains(':')
            || path.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)) throw Invalid();

        string[] segments = path.Split('/');
        if (segments.Any(segment => segment.Length is 0 or > MaximumSegmentLength
            || segment is "." or ".."
            || char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1])
            || segment.Any(char.IsControl)
            || segment.IndexOfAny(['?', '*', '"', '<', '>', '|']) >= 0
            || segment.EndsWith('.') || IsReservedDeviceName(segment))) throw Invalid();
        return new(path, segments[^1]);
    }

    private static bool IsReservedDeviceName(string segment)
    {
        string name = segment.Split('.')[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';
    }

    private static ArgumentException Invalid() => new(
        "Use a safe relative Collection name with nonempty segments of at most 255 characters.", "name");
}
