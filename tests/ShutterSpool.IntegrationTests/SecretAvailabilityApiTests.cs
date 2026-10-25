using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Source_creation_returns_redacted_unavailable_when_protection_key_is_invalid()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(invalidEncryptionKey: true);
        CancellationToken token = TestContext.Current.CancellationToken;

        using HttpResponseMessage create = await fixture.Client.PostAsJsonAsync("/management/sources",
            new { name = "Synthetic camera", sourceType = "CameraFtpUpload", connectionProfile = "ModernFtps" }, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        string problem = await create.Content.ReadAsStringAsync(token);
        Assert.Contains("DEPENDENCY_UNAVAILABLE", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-invalid-base64", problem, StringComparison.Ordinal);

        using JsonDocument sources = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/sources", token));
        Assert.Empty(sources.RootElement.GetProperty("items").EnumerateArray());
    }
}
