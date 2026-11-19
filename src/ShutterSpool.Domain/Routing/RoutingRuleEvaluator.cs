using ShutterSpool.Domain.Captures;
using System.IO.Enumeration;

namespace ShutterSpool.Domain.Routing;

public static class RoutingRuleEvaluator
{
    private const string EqualsOperator = "equals";
    private static readonly HashSet<string> StringFacts = new(StringComparer.Ordinal)
        { "source", "selection", "variant", "extension", "mime", "suppliedName", "cameraMake", "cameraModel", "cameraSerial", "collectionName", "collectionPath" };
    private static readonly HashSet<string> NumberFacts = new(StringComparer.Ordinal) { "rating", "size" };

    public static RoutingDecision Evaluate(IEnumerable<RoutingRule> rules, RoutingFacts facts)
    {
        foreach (RoutingRule rule in rules.OrderBy(rule => rule.Order))
        {
            if (!RuleMatches(rule, facts)) continue;
            if (!RoutingTargetTemplate.TryRender(rule.FolderTemplate, facts, folder: true, out string folder)
                || !RoutingTargetTemplate.TryRender(rule.FilenameTemplate, facts, folder: false, out string filename))
                return new(RoutingDecisionKind.InvalidTarget, rule.Id, rule.Required, null, "A target placeholder is missing or renders an unsafe path.");
            string target = folder.Length == 0 ? filename : $"{folder}/{filename}";
            if (target.Length > 2_048)
                return new(RoutingDecisionKind.InvalidTarget, rule.Id, rule.Required, null, "The rendered target exceeds the supported path length.");
            return new(RoutingDecisionKind.Matched, rule.Id, rule.Required, target,
                $"First matching{(rule.Negate ? " negated" : "")} rule '{rule.Name}'.");
        }
        return new(RoutingDecisionKind.NoMatch, null, false, null, "No enabled rule matched this Asset.");
    }

    private static bool RuleMatches(RoutingRule rule, RoutingFacts facts)
    {
        bool conditionsMatch = rule.Conditions.Length == 0 || (rule.ConditionMode switch
        {
            RoutingConditionMode.And => rule.Conditions.All(condition => Matches(condition, facts)),
            RoutingConditionMode.Or => rule.Conditions.Any(condition => Matches(condition, facts)),
            _ => false,
        });
        return rule.Negate ? !conditionsMatch : conditionsMatch;
    }

    public static string? Validate(RoutingRule[]? rules)
    {
        if (rules is null || rules.Length > 128 || rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count() != rules.Length
            || rules.Select(rule => rule.Order).Distinct().Count() != rules.Length) return "Rules require unique IDs and order values, with at most 128 rules.";
        foreach (RoutingRule rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 128
                || rule.Order < 0 || rule.Conditions is null || rule.Conditions.Length > 32
                || !Enum.IsDefined(rule.ConditionMode)
                || string.IsNullOrWhiteSpace(rule.FilenameTemplate) || rule.FilenameTemplate.Length > 255
                || rule.FolderTemplate is null || rule.FolderTemplate.Length > 1_024)
                return "A rule contains an invalid ID, name, order, condition list or target template.";
            if (rule.Conditions.Any(condition => !ValidCondition(condition)))
                return $"Rule '{rule.Name}' contains an unsupported or invalid condition.";
            if (!RoutingTargetTemplate.UsesKnownFacts(rule.FolderTemplate, KnownFacts)
                || !RoutingTargetTemplate.UsesKnownFacts(rule.FilenameTemplate, KnownFacts)
                || rule.FilenameTemplate.Contains("{collectionPath}", StringComparison.Ordinal))
                return $"Rule '{rule.Name}' uses an unknown template fact.";
        }
        return null;
    }

    private static bool ValidCondition(RoutingCondition condition)
    {
        if (condition.Value is null) return false;
        return condition.Fact switch
        {
            "label" => condition.Operator switch
            {
                EqualsOperator => CaptureCurationPolicy.IsCanonicalLabel(condition.Value.Text),
                "present" or "absent" => condition.Value is { Text: null, Number: null, Time: null, Tags: null },
                _ => false,
            },
            "tags" => condition.Operator == "contains" && condition.Value.Text is not null,
            "captureTime" => IsComparison(condition.Operator) && condition.Value.Time is not null,
            _ when NumberFacts.Contains(condition.Fact) => IsComparison(condition.Operator) && condition.Value.Number is not null,
            _ when StringFacts.Contains(condition.Fact) => condition.Value.Text is not null
                && (condition.Operator == EqualsOperator || condition.Fact == "suppliedName" && condition.Operator == "glob"),
            _ => false,
        };
    }

    private static bool IsComparison(string op) => op is EqualsOperator or "gt" or "gte" or "lt" or "lte";

    private static bool Matches(RoutingCondition condition, RoutingFacts facts)
    {
        if (!facts.Values.TryGetValue(condition.Fact, out RoutingValue? actual) || actual is null) return false;
        if (condition.Fact == "label") return condition.Operator switch
        {
            EqualsOperator => actual.Text is not null && string.Equals(actual.Text, condition.Value.Text, StringComparison.Ordinal),
            "present" => actual.Text is not null,
            "absent" => actual.Text is null,
            _ => false,
        };
        string? expectedString = condition.Value.Text;
        if (condition.Fact == "tags") return actual.Tags?.Contains(expectedString!, StringComparer.OrdinalIgnoreCase) ?? false;
        if (NumberFacts.Contains(condition.Fact) && actual.Number is decimal actualNumber && condition.Value.Number is decimal expectedNumber)
            return Compare(condition.Operator, actualNumber.CompareTo(expectedNumber));
        if (condition.Fact == "captureTime" && actual.Time is DateTime actualTime && condition.Value.Time is DateTime expectedTime)
            return Compare(condition.Operator, actualTime.CompareTo(expectedTime));
        if (actual.Text is not string actualText || expectedString is null) return false;
        return condition.Operator switch
        {
            EqualsOperator => string.Equals(actualText, expectedString, StringComparison.OrdinalIgnoreCase),
            "glob" => FileSystemName.MatchesSimpleExpression(expectedString, actualText, ignoreCase: true),
            _ => false,
        };
    }

    private static bool Compare(string op, int comparison) => op switch
    {
        EqualsOperator => comparison == 0,
        "gt" => comparison > 0,
        "gte" => comparison >= 0,
        "lt" => comparison < 0,
        "lte" => comparison <= 0,
        _ => false,
    };

    private static readonly HashSet<string> KnownFacts = StringFacts.Concat(NumberFacts).Append("tags").Append("label").Append("captureTime").ToHashSet(StringComparer.Ordinal);
}
