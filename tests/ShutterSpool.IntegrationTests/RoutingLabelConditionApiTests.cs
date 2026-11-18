using ShutterSpool.Domain.Routing;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Server.Api.Destinations;
using ShutterSpool.Server.Api.Routing;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public void Stored_and_API_routing_values_preserve_their_representation_precedence()
    {
        DateTime time = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        RoutingRule rule = new("mixed", "Mixed values", 0, true,
            [new("source", "equals", new(Text: "camera", Number: 7, Time: time)),
             new("source", "equals", new(Text: "camera", Time: time)),
             new("captureTime", "equals", new(Time: time)),
             new("tags", "contains", new(Tags: ["tag"]))], "", "photo.jpg");

        using JsonDocument stored = JsonDocument.Parse(RoutingRuleJson.WriteRule(rule));
        JsonElement conditions = stored.RootElement.GetProperty("Conditions");
        Assert.Equal("camera", conditions[0].GetProperty("Value").GetString());
        Assert.Equal("camera", conditions[1].GetProperty("Value").GetString());
        Assert.Equal("2026-01-02T03:04:05.0000000Z", conditions[2].GetProperty("Value").GetString());
        Assert.Equal(JsonValueKind.Null, conditions[3].GetProperty("Value").ValueKind);

        RoutingConditionDto[] response = RoutingRuleDto.From(rule).Conditions;
        Assert.Equal(7m, response[0].Value.GetDecimal());
        Assert.Equal("2026-01-02T03:04:05Z", response[1].Value.GetString());
        Assert.Equal("2026-01-02T03:04:05Z", response[2].Value.GetString());
        Assert.Equal(JsonValueKind.Null, response[3].Value.ValueKind);

        RoutingRule missingValue = rule with { Conditions = [new("label", "present", null!)] };
        Assert.Throws<NullReferenceException>(() => RoutingRuleJson.WriteRule(missingValue));
    }

    [Theory]
    [InlineData("present", "5")]
    [InlineData("present", "true")]
    [InlineData("present", "{\"value\":\"Red\"}")]
    [InlineData("present", "[\"Red\"]")]
    [InlineData("absent", "5")]
    [InlineData("absent", "true")]
    [InlineData("absent", "{\"value\":\"Red\"}")]
    [InlineData("absent", "[\"Red\"]")]
    public async Task Label_presence_conditions_reject_non_null_non_string_values(string operation, string value)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations",
            new { name = "Synthetic local", kind = "local" }, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DestinationResponse destination = (await created.Content.ReadFromJsonAsync<DestinationResponse>(token))!;
        string body = $$"""{"expectedRevision":0,"enabled":true,"rules":[{"id":"label","name":"Label","order":0,"required":true,"conditions":[{"fact":"label","operator":"{{operation}}","value":{{value}}}],"folderTemplate":"","filenameTemplate":"{suppliedName}"}]}""";
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using HttpResponseMessage rejected = await fixture.Client.PutAsync($"/management/destinations/{destination.Id}/routing", content, token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using HttpResponseMessage current = await fixture.Client.GetAsync($"/management/destinations/{destination.Id}/routing", token);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        string currentBody = await current.Content.ReadAsStringAsync(token);
        Assert.Contains("\"configurationRevision\":0", currentBody, StringComparison.Ordinal);
        Assert.Contains("\"rules\":[]", currentBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("present")]
    [InlineData("absent")]
    public async Task Label_presence_conditions_accept_null_as_no_comparison_value(string operation)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations",
            new { name = "Synthetic local", kind = "local" }, token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        DestinationResponse destination = (await created.Content.ReadFromJsonAsync<DestinationResponse>(token))!;
        string body = $$"""{"expectedRevision":0,"enabled":true,"rules":[{"id":"label","name":"Label","order":0,"required":true,"conditions":[{"fact":"label","operator":"{{operation}}","value":null}],"folderTemplate":"","filenameTemplate":"{suppliedName}"}]}""";

        using HttpResponseMessage accepted = await fixture.Client.PutAsync($"/management/destinations/{destination.Id}/routing",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }
}
