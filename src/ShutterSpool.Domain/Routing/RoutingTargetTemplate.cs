using System.Globalization;
using System.Text.RegularExpressions;

namespace ShutterSpool.Domain.Routing;

internal static partial class RoutingTargetTemplate
{
    private static bool IsCalendarComponent(string token) => token is "year" or "month" or "day" or "monthName" or "weekdayName";

    public static bool UsesKnownFacts(string template, IReadOnlySet<string> knownFacts)
    {
        string remainder = Placeholder().Replace(template, "");
        if (remainder.Contains('{') || remainder.Contains('}')) return false;
        return Placeholder().Matches(template).Cast<Match>().All(match => knownFacts.Contains(match.Groups[1].Value)
            || IsCalendarComponent(match.Groups[1].Value));
    }

    public static bool TryRender(string template, RoutingFacts facts, bool folder, out string rendered)
    {
        rendered = string.Empty;
        if (!folder && template.Contains("{collectionPath}", StringComparison.Ordinal)) return false;
        bool allFactsKnown = true;
        rendered = Placeholder().Replace(template, match =>
        {
            string? value = FormatFact(match.Groups[1].Value, facts);
            if (value is null)
            {
                allFactsKnown = false;
                return "";
            }
            return value;
        });

        if (!allFactsKnown || Placeholder().Replace(rendered, "").Contains('{') || rendered.Contains('}')
            || rendered.Contains('\\') || Path.IsPathRooted(rendered)) return false;
        if (folder && rendered.Length == 0) return true;
        if (!folder && rendered.Contains('/')) return false;
        return (folder ? rendered.Split('/') : [rendered]).All(IsSafeSegment);
    }

    private static string? FormatFact(string fact, RoutingFacts facts)
    {
        if (IsCalendarComponent(fact))
        {
            if (!facts.Values.TryGetValue("captureTime", out RoutingValue? recorded) || recorded?.Time is not DateTime date) return null;
            string format = fact switch { "year" => "yyyy", "month" => "MM", "day" => "dd", "monthName" => "MMMM", _ => "dddd" };
            return date.ToString(format, CultureInfo.InvariantCulture);
        }
        if (!facts.Values.TryGetValue(fact, out RoutingValue? value) || value is null) return null;
        if (fact == "captureTime" && value.Time is DateTime captureTime)
            return captureTime.ToString("yyyy-MM-dd'T'HH-mm-ss-fffffff", CultureInfo.InvariantCulture);
        if (fact == "tags" && value.Tags is { } tags)
        {
            string[] orderedTags = [.. tags.Where(tag => !string.IsNullOrWhiteSpace(tag))
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase).ThenBy(tag => tag, StringComparer.Ordinal)];
            return orderedTags.Length == 0 ? null : string.Join('+', orderedTags);
        }
        return value.Text ?? value.Number?.ToString(CultureInfo.InvariantCulture);
    }

    private static bool IsSafeSegment(string segment) =>
        segment.Length is > 0 and <= 255 && segment is not ("." or "..") && !segment.Any(char.IsControl)
        && segment.IndexOfAny(['?', '*', '"', '<', '>', '|', ':']) < 0
        && !segment.EndsWith('.') && !segment.EndsWith(' ') && !IsReservedDeviceName(segment);

    private static bool IsReservedDeviceName(string segment)
    {
        string name = segment.Split('.')[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';
    }

    [GeneratedRegex("\\{([A-Za-z][A-Za-z0-9]*)\\}")]
    private static partial Regex Placeholder();
}
