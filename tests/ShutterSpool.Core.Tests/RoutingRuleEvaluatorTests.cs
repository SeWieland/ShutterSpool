using ShutterSpool.Domain.Routing;

namespace ShutterSpool.Core.Tests;

public sealed class RoutingRuleEvaluatorTests
{
    [Fact]
    public void Collection_conditions_and_nested_folder_tokens_preserve_first_match()
    {
        RoutingRule[] rules =
        [
            new("other", "Other event", 0, true, [new("collectionPath", "equals", "Travel/Berlin")], "other", "{suppliedName}"),
            new("event", "Event", 1, true, [new("collectionName", "equals", "Berlin")], "photos/{collectionPath}", "{collectionName}-{suppliedName}"),
            new("fallback", "Fallback", 2, true, [], "all", "{suppliedName}"),
        ];
        RoutingFacts facts = new(new Dictionary<string, RoutingValue>
        { ["collectionName"] = "Berlin", ["collectionPath"] = "Events/Berlin", ["suppliedName"] = "frame.jpg" });

        Assert.Null(RoutingRuleEvaluator.Validate(rules));
        RoutingDecision decision = RoutingRuleEvaluator.Evaluate(rules, facts);
        Assert.Equal("event", decision.RuleId);
        Assert.Equal("photos/Events/Berlin/Berlin-frame.jpg", decision.TargetPath);
        Assert.Equal("fallback", RoutingRuleEvaluator.Evaluate(rules,
            new(new Dictionary<string, RoutingValue> { ["suppliedName"] = "frame.jpg" })).RuleId);
    }

    [Fact]
    public void Collection_path_is_never_a_filename_token_even_for_a_single_segment()
    {
        RoutingRule rule = new("event", "Event", 0, true, [], "", "{collectionPath}.jpg");
        Assert.NotNull(RoutingRuleEvaluator.Validate([rule]));
        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([rule],
            new(new Dictionary<string, RoutingValue> { ["collectionPath"] = "Berlin" })).Kind);
    }

    [Theory]
    [InlineData(false, RoutingDecisionKind.Matched, "2026/10/09/October/Friday/frame.jpg")]
    [InlineData(true, RoutingDecisionKind.InvalidTarget, null)]
    public void Calendar_templates_use_only_trustworthy_recorded_components(bool uncertain, RoutingDecisionKind kind, string? target)
    {
        RoutingRule rule = new("calendar", "Calendar", 0, true, [], "{year}/{month}/{day}/{monthName}/{weekdayName}", "{suppliedName}");
        var facts = RoutingFactBuilder.Build(new(Guid.NewGuid(), 0, null, [], null),
            new("JPEG", "frame.jpg", 4, "image/jpeg", null, null, null, new DateTime(2026, 10, 9, 23, 59, 0), uncertain));
        Assert.Null(RoutingRuleEvaluator.Validate([rule]));
        RoutingDecision decision = RoutingRuleEvaluator.Evaluate([rule], facts);
        Assert.Equal(kind, decision.Kind);
        Assert.Equal(target, decision.TargetPath);
        var missing = RoutingFactBuilder.Build(new(Guid.NewGuid(), 0, null, [], null),
            new("JPEG", "frame.jpg", 4, "image/jpeg", null, null, null, null, false));
        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([rule], missing).Kind);
    }

    [Fact]
    public void First_matching_rule_wins_and_missing_facts_do_not_match()
    {
        RoutingRule[] rules =
        [
            new("unknown", "Unknown camera", 0, true, [new("cameraModel", "equals", new RoutingValue(Text: "X"))], "{cameraModel}", "{suppliedName}"),
            new("picked", "Picked files", 1, false, [new("selection", "equals", new RoutingValue(Text: "Picked")), new("tags", "contains", new RoutingValue(Text: "web"))], "{selection}", "{suppliedName}"),
            new("fallback", "All files", 2, true, [], "all", "{suppliedName}"),
        ];

        RoutingDecision decision = RoutingRuleEvaluator.Evaluate(rules,
            new RoutingFacts(new Dictionary<string, RoutingValue> { ["selection"] = "Picked", ["tags"] = new[] { "web" }, ["suppliedName"] = "frame.jpg" }));

        Assert.Equal("picked", decision.RuleId);
        Assert.False(decision.Required);
        Assert.Equal("Picked/frame.jpg", decision.TargetPath);
    }

    [Theory]
    [InlineData(RoutingConditionMode.And, false, RoutingDecisionKind.NoMatch)]
    [InlineData(RoutingConditionMode.Or, false, RoutingDecisionKind.Matched)]
    [InlineData(RoutingConditionMode.Or, true, RoutingDecisionKind.NoMatch)]
    public void Condition_mode_combines_the_whole_rule_before_negation(
        RoutingConditionMode mode, bool negate, RoutingDecisionKind expected)
    {
        RoutingRule rule = new("selected-or-web", "Selected or web", 0, true,
            [new("selection", "equals", new RoutingValue(Text: "Picked")), new("tags", "contains", new RoutingValue(Text: "web"))],
            "", "frame.jpg", Negate: negate, ConditionMode: mode);
        RoutingFacts facts = new(new Dictionary<string, RoutingValue> { ["selection"] = "Picked", ["tags"] = new[] { "print" } });

        Assert.Equal(expected, RoutingRuleEvaluator.Evaluate([rule], facts).Kind);
    }

    [Fact]
    public void An_empty_condition_set_remains_unconditional_in_either_mode()
    {
        RoutingRule rule = new("fallback", "Fallback", 0, true, [], "", "frame.jpg", ConditionMode: RoutingConditionMode.Or);

        Assert.Equal(RoutingDecisionKind.Matched,
            RoutingRuleEvaluator.Evaluate([rule], new RoutingFacts(new Dictionary<string, RoutingValue>())).Kind);
    }

    [Theory]
    [InlineData("web", "Picked", RoutingDecisionKind.NoMatch)]
    [InlineData("web", "Rejected", RoutingDecisionKind.Matched)]
    [InlineData("print", "Picked", RoutingDecisionKind.Matched)]
    [InlineData(null, "Picked", RoutingDecisionKind.Matched)]
    public void Negation_inverts_the_whole_rule_including_an_unknown_fact(string? tag, string selection, RoutingDecisionKind expected)
    {
        RoutingRule rule = new("not-web", "Not web", 0, true,
            [new("tags", "contains", new RoutingValue(Text: "web")), new("selection", "equals", new RoutingValue(Text: "Picked"))],
            "not-web", "frame.jpg", Negate: true);
        Dictionary<string, RoutingValue> values = new() { ["selection"] = selection };
        if (tag is not null) values["tags"] = new[] { tag };

        RoutingDecision decision = RoutingRuleEvaluator.Evaluate([rule], new RoutingFacts(values));

        Assert.Equal(expected, decision.Kind);
        if (expected == RoutingDecisionKind.Matched) Assert.Equal("not-web/frame.jpg", decision.TargetPath);
    }

    [Fact]
    public void Defaults_and_invalid_modes_are_explicit()
    {
        RoutingRule rule = new("or", "Either", 0, true, [], "", "frame.jpg");
        Assert.Equal(RoutingConditionMode.And, rule.ConditionMode);
        Assert.NotNull(RoutingRuleEvaluator.Validate([rule with { ConditionMode = (RoutingConditionMode)99 }]));
    }

    [Fact]
    public void Label_conditions_distinguish_known_absence_from_unknown()
    {
        RoutingRule Rule(string op, string? value) => new("label", "label", 0, false,
            [new("label", op, new(Text: value))], "", "frame.jpg");
        var absent = new RoutingFacts(new Dictionary<string, RoutingValue> { ["label"] = new() });
        var unknown = new RoutingFacts(new Dictionary<string, RoutingValue>());
        var red = new RoutingFacts(new Dictionary<string, RoutingValue> { ["label"] = new(Text: "Red") });

        Assert.Equal(RoutingDecisionKind.Matched, RoutingRuleEvaluator.Evaluate([Rule("absent", null)], absent).Kind);
        Assert.Equal(RoutingDecisionKind.NoMatch, RoutingRuleEvaluator.Evaluate([Rule("present", null)], absent).Kind);
        Assert.Equal(RoutingDecisionKind.NoMatch, RoutingRuleEvaluator.Evaluate([Rule("absent", null)], unknown).Kind);
        Assert.Equal(RoutingDecisionKind.Matched, RoutingRuleEvaluator.Evaluate([Rule("equals", "Red")], red).Kind);
        Assert.NotNull(RoutingRuleEvaluator.Validate([Rule("equals", "purple")]));
    }

    [Theory]
    [InlineData("../private", "{suppliedName}")]
    [InlineData("/absolute", "frame.jpg")]
    [InlineData("safe", "../outside.jpg")]
    public void Rejects_unsafe_rendered_paths(string folder, string filename)
    {
        RoutingRule rule = new("all", "All", 0, true, [], folder, filename);

        RoutingDecision decision = RoutingRuleEvaluator.Evaluate([rule],
            new RoutingFacts(new Dictionary<string, RoutingValue> { ["suppliedName"] = "frame.jpg" }));

        Assert.Equal(RoutingDecisionKind.InvalidTarget, decision.Kind);
    }

    [Fact]
    public void Empty_folder_targets_root_and_reserved_device_names_are_rejected()
    {
        RoutingRule rootRule = new("root", "Root", 0, true, [], "", "frame.jpg");
        RoutingRule deviceRule = new("device", "Device", 0, true, [], "", "CON.txt");

        Assert.Equal("frame.jpg", RoutingRuleEvaluator.Evaluate([rootRule], new RoutingFacts(new Dictionary<string, RoutingValue>())).TargetPath);
        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([deviceRule], new RoutingFacts(new Dictionary<string, RoutingValue>())).Kind);
    }

    [Fact]
    public void Source_and_known_local_capture_time_support_typed_conditions()
    {
        DateTime localTime = new(2026, 9, 27, 10, 30, 0, DateTimeKind.Unspecified);
        RoutingRule rule = new("camera", "Camera and time", 0, true,
        [
            new("source", "equals", new RoutingValue(Text: "source-id")),
            new("captureTime", "equals", new RoutingValue(Time: localTime)),
        ], "local", "{source}.jpg");

        RoutingDecision decision = RoutingRuleEvaluator.Evaluate([rule], new RoutingFacts(new Dictionary<string, RoutingValue>
            { ["source"] = "source-id", ["captureTime"] = localTime }));

        Assert.Equal(RoutingDecisionKind.Matched, decision.Kind);
        Assert.Equal("local/source-id.jpg", decision.TargetPath);
    }

    [Theory]
    [InlineData("?")]
    [InlineData("*")]
    [InlineData("\"")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("|")]
    public void Rendered_windows_invalid_filename_characters_are_rejected(string invalidCharacter)
    {
        RoutingRule rule = new("invalid", "Invalid filename", 0, true, [], "", $"frame{invalidCharacter}.raw");
        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([rule], new RoutingFacts(new Dictionary<string, RoutingValue>())).Kind);
    }

    [Fact]
    public void Rendered_segment_and_total_target_lengths_are_bounded()
    {
        RoutingFacts longName = new(new Dictionary<string, RoutingValue> { ["suppliedName"] = new string('x', 256) });
        RoutingRule filename = new("filename", "Long filename", 0, true, [], "", "{suppliedName}");
        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([filename], longName).Kind);

        RoutingFacts longFolder = new(new Dictionary<string, RoutingValue>
        {
            ["cameraMake"] = string.Join('/', Enumerable.Repeat(new string('x', 255), 8)),
        });
        RoutingRule target = new("target", "Long target", 0, true, [], "{cameraMake}", "file.raw");
        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([target], longFolder).Kind);
    }

    [Fact]
    public void Capture_time_and_tags_render_as_deterministic_safe_path_values()
    {
        RoutingFacts facts = new(new Dictionary<string, RoutingValue>
        {
            ["captureTime"] = new DateTime(2026, 9, 27, 10, 30, 0, DateTimeKind.Unspecified),
            ["tags"] = new[] { "zebra", "client", "alpha" },
        });
        RoutingRule rule = new("facts", "Safe facts", 0, true, [], "", "{captureTime}-{tags}.jpg");

        RoutingDecision result = RoutingRuleEvaluator.Evaluate([rule], facts);

        Assert.Equal(RoutingDecisionKind.Matched, result.Kind);
        Assert.Equal("2026-09-27T10-30-00-0000000-alpha+client+zebra.jpg", result.TargetPath);
    }

    [Fact]
    public void Tags_with_unsafe_path_characters_are_rejected_instead_of_rewritten()
    {
        RoutingFacts facts = new(new Dictionary<string, RoutingValue> { ["tags"] = new[] { "client/name", "safe" } });
        RoutingRule rule = new("tags", "Unsafe tags", 0, true, [], "", "{tags}.jpg");

        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([rule], facts).Kind);
    }

    [Fact]
    public void Empty_tag_fact_does_not_resolve_to_an_empty_or_guessed_path()
    {
        RoutingFacts facts = new(new Dictionary<string, RoutingValue> { ["tags"] = Array.Empty<string>() });
        RoutingRule rule = new("tags", "Empty tags", 0, true, [], "", "{tags}.jpg");

        Assert.Equal(RoutingDecisionKind.InvalidTarget, RoutingRuleEvaluator.Evaluate([rule], facts).Kind);
    }

}
