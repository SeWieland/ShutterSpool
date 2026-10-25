using ShutterSpool.Domain.Routing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShutterSpool.Infrastructure.Persistence;

internal static class RoutingRuleJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter<RoutingConditionMode>() },
    };

    public static RoutingRule[] ReadRules(string json) =>
        [.. (JsonSerializer.Deserialize<RuleRow[]>(json, Options) ?? []).Select(FromRow)];

    public static string WriteRules(IEnumerable<RoutingRule> rules) =>
        JsonSerializer.Serialize(rules.Select(ToRow).ToArray(), Options);

    public static DestinationRouting[] ReadSnapshot(string json) =>
        [.. (JsonSerializer.Deserialize<DestinationRow[]>(json, Options) ?? []).Select(row =>
            new DestinationRouting(row.DestinationId, row.Enabled, [.. row.Rules.Select(FromRow)]))];

    public static string WriteSnapshot(IEnumerable<DestinationRouting> snapshot) =>
        JsonSerializer.Serialize(snapshot.Select(item => new DestinationRow(item.DestinationId, item.Enabled,
            [.. item.Rules.Select(ToRow)])).ToArray(), Options);

    public static string WriteRule(RoutingRule rule) => JsonSerializer.Serialize(ToRow(rule), Options);

    private static RoutingRule FromRow(RuleRow row) => new(row.Id, row.Name, row.Order, row.Required,
        [.. row.Conditions.Select(condition => new RoutingCondition(condition.Fact, condition.Operator,
            ReadValue(condition.Fact, condition.Value)))], row.FolderTemplate, row.FilenameTemplate,
        row.Negate, row.ConditionMode);

    private static RuleRow ToRow(RoutingRule rule) => new(rule.Id, rule.Name, rule.Order, rule.Required,
        [.. rule.Conditions.Select(condition => new ConditionRow(condition.Fact, condition.Operator,
            WriteValue(condition.Value)))], rule.FolderTemplate, rule.FilenameTemplate,
        rule.Negate, rule.ConditionMode);

    private static RoutingValue ReadValue(string fact, JsonElement value)
    {
        if (fact is "rating" or "size" && value.TryGetDecimal(out decimal number)) return new(Number: number);
        if (fact == "captureTime" && value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime time))
            return new(Time: time);
        return value.ValueKind == JsonValueKind.String ? new(Text: value.GetString()) : new();
    }

    private static JsonElement WriteValue(RoutingValue value) => value.Text is not null
        ? JsonSerializer.SerializeToElement(value.Text)
        : value.Number is decimal number ? JsonSerializer.SerializeToElement(number)
        : value.Time is DateTime time ? JsonSerializer.SerializeToElement(time.ToString("O", CultureInfo.InvariantCulture))
        : JsonSerializer.SerializeToElement<object?>(null);

    private sealed record ConditionRow(string Fact, string Operator, JsonElement Value);
    private sealed record RuleRow(string Id, string Name, int Order, bool Required, ConditionRow[] Conditions,
        string FolderTemplate, string FilenameTemplate, bool Negate = false,
        RoutingConditionMode ConditionMode = RoutingConditionMode.And);
    private sealed record DestinationRow(Guid DestinationId, bool Enabled, RuleRow[] Rules);
}
