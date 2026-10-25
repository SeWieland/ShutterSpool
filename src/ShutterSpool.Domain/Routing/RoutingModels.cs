namespace ShutterSpool.Domain.Routing;

// Values cross the transport boundary as one of these explicit types; JSON is parsed in the adapter.
public sealed record RoutingValue(string? Text = null, decimal? Number = null, DateTime? Time = null, IReadOnlyList<string>? Tags = null)
{
    public static implicit operator RoutingValue(string value) => new(Text: value);
    public static implicit operator RoutingValue(int value) => new(Number: value);
    public static implicit operator RoutingValue(long value) => new(Number: value);
    public static implicit operator RoutingValue(DateTime value) => new(Time: value);
    public static implicit operator RoutingValue(string[] value) => new(Tags: value);
}

public sealed record RoutingCondition(string Fact, string Operator, RoutingValue Value);

public enum RoutingConditionMode { And, Or }

public sealed record RoutingRule(string Id, string Name, int Order, bool Required, RoutingCondition[] Conditions,
    string FolderTemplate, string FilenameTemplate, bool Negate = false,
    RoutingConditionMode ConditionMode = RoutingConditionMode.And);
public sealed record DestinationRouting(Guid DestinationId, bool Enabled, RoutingRule[] Rules, long ConfigurationRevision = 0);
public sealed record RoutingFacts(IReadOnlyDictionary<string, RoutingValue> Values);
public enum RoutingDecisionKind { Matched, NoMatch, InvalidTarget, DestinationDisabled }
public sealed record RoutingDecision(RoutingDecisionKind Kind, string? RuleId, bool Required, string? TargetPath, string Explanation);
