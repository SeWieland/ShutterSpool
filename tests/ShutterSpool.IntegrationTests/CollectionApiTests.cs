using Microsoft.EntityFrameworkCore;
using ShutterSpool.Domain.Collections;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Collection_search_cursor_is_bound_to_query_and_unauthorized_create_writes_nothing()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Collections.AddRange(
                new CollectionEntity { Id = Guid.NewGuid(), Name = "Berlin", Path = "Berlin", Identity = "BERLIN" },
                new CollectionEntity { Id = Guid.NewGuid(), Name = "Bergen", Path = "Bergen", Identity = "BERGEN" });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage page = await fixture.Client.GetAsync("/management/collections?q=Ber&limit=1", token);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        using JsonDocument pageBody = await JsonDocument.ParseAsync(await page.Content.ReadAsStreamAsync(token), cancellationToken: token);
        string cursor = pageBody.RootElement.GetProperty("nextCursor").GetString()!;
        using HttpResponseMessage wrongSearch = await fixture.Client.GetAsync(
            $"/management/collections?q=Berlin&limit=1&cursor={Uri.EscapeDataString(cursor)}", token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongSearch.StatusCode);

        using HttpResponseMessage unauthorized = await fixture.Server.CreateClient().PostAsJsonAsync(
            "/management/collections", new { name = "NoWrite" }, token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.False(await verify.Collections.AnyAsync(item => item.Path == "NoWrite", token));
    }

    [Fact]
    public async Task Collection_move_rejects_null_bulk_items_without_writes_and_returns_named_conflict_state()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid sourceId = Guid.NewGuid(), captureId = Guid.NewGuid(), collectionId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Collections.Add(new CollectionEntity
            { Id = collectionId, Name = "Berlin", Path = "Berlin", Identity = "BERLIN" });
            database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic-camera" });
            database.Captures.Add(new CaptureEntity
            { Id = captureId, SourceId = sourceId, CollectionId = CollectionName.BuiltInId, NormalizedStem = "frame" });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage malformed = await fixture.Client.PostAsJsonAsync("/management/captures/collection-moves",
            new { targetCollectionId = collectionId, items = new object?[] { null } }, token);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using HttpResponseMessage invalidEnvelope = await fixture.Client.PostAsJsonAsync("/management/captures/collection-moves",
            new { targetCollectionId = collectionId, items = new[]
            {
                new { captureId, expectedRevision = 0 }, new { captureId = Guid.Empty, expectedRevision = 0 },
            } }, token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidEnvelope.StatusCode);
        await using (var unchanged = new ShutterSpoolDbContext(fixture.Database.Options))
            Assert.Equal(CollectionName.BuiltInId, await unchanged.Captures.Where(item => item.Id == captureId)
                .Select(item => item.CollectionId).SingleAsync(token));

        using HttpResponseMessage stale = await fixture.Client.PutAsJsonAsync($"/management/captures/{captureId}/collection",
            new { expectedRevision = 4, collectionId }, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("application/problem+json", stale.Content.Headers.ContentType?.MediaType);
        using JsonDocument problem = await JsonDocument.ParseAsync(await stale.Content.ReadAsStreamAsync(token), cancellationToken: token);
        Assert.Equal(409, problem.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("CAPTURE_COLLECTION_REVISION_CONFLICT", problem.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(CollectionName.BuiltInId.ToString(), problem.RootElement.GetProperty("current")
            .GetProperty("currentCollection").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Collection_cursor_pages_valid_maximum_length_multibyte_paths()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        string segment = new('界', 255);
        string path = string.Join('/', segment, segment, segment, segment);
        string otherPath = path[..^1] + '海';
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            database.Collections.AddRange(
                new CollectionEntity { Id = Guid.NewGuid(), Name = segment, Path = path, Identity = path.ToUpperInvariant() },
                new CollectionEntity { Id = Guid.NewGuid(), Name = segment, Path = otherPath, Identity = otherPath.ToUpperInvariant() });
            await database.SaveChangesAsync(token);
        }

        using HttpResponseMessage first = await fixture.Client.GetAsync("/management/collections?q=界&limit=1", token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using JsonDocument page = await JsonDocument.ParseAsync(await first.Content.ReadAsStreamAsync(token), cancellationToken: token);
        string cursor = page.RootElement.GetProperty("nextCursor").GetString()!;
        Assert.True(cursor.Length > 2_048);
        using HttpResponseMessage next = await fixture.Client.GetAsync(
            $"/management/collections?q=界&limit=1&cursor={Uri.EscapeDataString(cursor)}", token);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }
}
