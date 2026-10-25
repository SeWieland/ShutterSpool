using ShutterSpool.Server.Api.Destinations;
using System.Net;
using System.Net.Http.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
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
