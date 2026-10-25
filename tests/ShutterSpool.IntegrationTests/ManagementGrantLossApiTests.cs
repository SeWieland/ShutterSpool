using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Source_and_destination_lists_page_through_bounded_ordered_queries()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        string longUnicodeName = new('漢', 256);
        foreach (string name in new[] { "Zulu camera", "Alpha camera", longUnicodeName })
        {
            using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/sources", new
            { name, sourceType = "CameraFtpUpload", connectionProfile = "ModernFtps" }, token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }
        foreach (string name in new[] { "Local Z", "Local A" })
        {
            using HttpResponseMessage created = await fixture.Client.PostAsJsonAsync("/management/destinations", new { name, kind = "local" }, token);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        using JsonDocument sourceFirst = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/sources?limit=1", token));
        Assert.Equal("Alpha camera", sourceFirst.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
        string sourceCursor = Uri.EscapeDataString(sourceFirst.RootElement.GetProperty("nextCursor").GetString()!);
        using JsonDocument sourceNext = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/sources?limit=1&cursor={sourceCursor}", token));
        Assert.Equal("Zulu camera", sourceNext.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
        string longCursor = Uri.EscapeDataString(sourceNext.RootElement.GetProperty("nextCursor").GetString()!);
        using JsonDocument sourceLast = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/sources?limit=1&cursor={longCursor}", token));
        Assert.Equal(longUnicodeName, sourceLast.RootElement.GetProperty("items")[0].GetProperty("name").GetString());

        using JsonDocument destinationFirst = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/destinations?limit=1", token));
        string destinationCursor = Uri.EscapeDataString(destinationFirst.RootElement.GetProperty("nextCursor").GetString()!);
        using JsonDocument destinationNext = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/destinations?limit=1&cursor={destinationCursor}", token));
        Guid firstId = destinationFirst.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        Guid nextId = destinationNext.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.NotEqual(firstId, nextId);
        Assert.Null(destinationNext.RootElement.GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task Batch_rechecks_management_grant_before_each_item_and_stops_mutating_after_revocation()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(revokeBatchAuthorization: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid();
        Guid[] captureIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic" });
            database.Captures.AddRange(captureIds.Select((id, index) => new CaptureEntity
            {
                Id = id,
                SourceId = sourceId,
                NormalizedStem = $"synthetic-{index}",
                FirstAcceptedAt = DateTimeOffset.UtcNow.AddMinutes(index),
                LastPrimaryIngestedAt = DateTimeOffset.UtcNow.AddMinutes(index),
            }));
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/management/captures/batch/curation", new
        {
            items = captureIds.Select(id => new { id, expectedRevision = 0, rating = 4, label = "Blue", tags = Array.Empty<string>() }),
        }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        JsonElement[] results = [.. body.RootElement.GetProperty("items").EnumerateArray()];
        Assert.Equal(new[] { "committed", "forbidden", "forbidden" }, results.Select(item => item.GetProperty("status").GetString()));
        Assert.All(results.Skip(1), item => Assert.Equal("MANAGEMENT_ACCESS_REVOKED", item.GetProperty("errorCode").GetString()));

        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity[] states = await verify.Captures.AsNoTracking().OrderBy(item => item.FirstAcceptedAt).ToArrayAsync(token);
        Assert.Equal(new[] { 1, 0, 0 }, states.Select(item => item.Revision));
        Assert.Equal(new[] { 4, 0, 0 }, states.Select(item => item.Rating));
    }

    [Fact]
    public async Task Batch_authorization_dependency_failure_returns_safe_503_after_preserving_prior_commits()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync(failBatchAuthorizationDependency: true);
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid();
        Guid[] captureIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic" });
            database.Captures.AddRange(captureIds.Select((id, index) => new CaptureEntity
            {
                Id = id,
                SourceId = sourceId,
                NormalizedStem = $"synthetic-{index}",
                FirstAcceptedAt = DateTimeOffset.UtcNow.AddMinutes(index),
                LastPrimaryIngestedAt = DateTimeOffset.UtcNow.AddMinutes(index),
            }));
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/management/captures/batch/curation", new
        {
            items = captureIds.Select(id => new { id, expectedRevision = 0, rating = 3, label = "Green", tags = Array.Empty<string>() }),
        }, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        string problem = await response.Content.ReadAsStringAsync(token);
        Assert.Contains("DEPENDENCY_UNAVAILABLE", problem, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic authorization dependency outage", problem, StringComparison.Ordinal);

        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity[] states = await verify.Captures.AsNoTracking().OrderBy(item => item.FirstAcceptedAt).ToArrayAsync(token);
        Assert.Equal(new[] { 1, 0, 0 }, states.Select(item => item.Revision));
        Assert.Equal(new[] { 3, 0, 0 }, states.Select(item => item.Rating));
    }
}
