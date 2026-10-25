using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Local_folder_browse_returns_directory_contract_and_validation_errors()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        string root = Path.Combine(Path.GetTempPath(), $"shutterspool-browse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "child"));
        await File.WriteAllTextAsync(Path.Combine(root, "original.raw"), "synthetic", TestContext.Current.CancellationToken);
        try
        {
            using HttpResponseMessage response = await fixture.Client.GetAsync(
                $"/management/destinations/local-folders?path={Uri.EscapeDataString($"  \"{root}\"  ")}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            JsonElement listing = document.RootElement;
            Assert.Equal(Path.GetFullPath(root), listing.GetProperty("path").GetString());
            Assert.Equal("child", listing.GetProperty("directories")[0].GetProperty("name").GetString());
            Assert.False(listing.GetProperty("truncated").GetBoolean());
            Assert.False(listing.TryGetProperty("files", out _));

            using HttpResponseMessage invalid = await fixture.Client.GetAsync(
                "/management/destinations/local-folders?path=relative", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using JsonDocument problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Contains("absolute directory path", problem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("absolute directory path", problem.RootElement.GetProperty("errors").GetProperty("path")[0].GetString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Local_root_setup_reports_typed_validation_and_saves_a_quoted_existing_path()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), $"shutterspool-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations",
                new { name = "Synthetic local", kind = "local" }, token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using JsonDocument destination = JsonDocument.Parse(await created.Content.ReadAsStringAsync(token));
            Guid id = destination.RootElement.GetProperty("id").GetGuid();

            using HttpResponseMessage invalid = await fixture.Client.PutAsJsonAsync($"/management/destinations/{id}/local",
                new { expectedRevision = 0, rootPath = "relative" }, token);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using JsonDocument problem = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(token));
            Assert.Contains("absolute directory path", problem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("INVALID_ROOT", problem.RootElement.GetProperty("errors").GetProperty("rootPath")[0].GetString());

            string missingRoot = Path.Combine(Path.GetTempPath(), $"shutterspool-missing-{Guid.NewGuid():N}");
            using HttpResponseMessage unavailable = await fixture.Client.PutAsJsonAsync($"/management/destinations/{id}/local",
                new { expectedRevision = 0, rootPath = missingRoot }, token);
            Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
            using JsonDocument unavailableProblem = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync(token));
            Assert.Contains("must already exist", unavailableProblem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("permission", unavailableProblem.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("ROOT_UNAVAILABLE", unavailableProblem.RootElement.GetProperty("errors").GetProperty("rootPath")[0].GetString());
            Assert.False(Directory.Exists(missingRoot));

            using HttpResponseMessage configured = await fixture.Client.PutAsJsonAsync($"/management/destinations/{id}/local",
                new { expectedRevision = 0, rootPath = $"  \"{root}\"  " }, token);
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
            using JsonDocument saved = JsonDocument.Parse(await configured.Content.ReadAsStringAsync(token));
            Assert.Equal(Path.GetFullPath(root), saved.RootElement.GetProperty("rootPath").GetString());
            Assert.True(File.Exists(Path.Combine(root, $".shutterspool-{id:N}.root")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
