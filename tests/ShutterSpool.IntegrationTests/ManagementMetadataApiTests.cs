using Microsoft.EntityFrameworkCore;
using ShutterSpool.Domain.Captures;
using ShutterSpool.Domain.Sources;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Signed_metadata_conflicts_and_postrelease_edits_keep_complete_frozen_values()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        string path = $"/management/captures/{id}/curation";
        using var update = await fixture.Client.PutAsJsonAsync(path,
            new { expectedRevision = 0, rating = -1, label = "Purple", tags = new[] { "  旅行,夏  ", "a,b" } }, token);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using var state = JsonDocument.Parse(await update.Content.ReadAsStringAsync(token));
        Assert.Equal("Rejected", state.RootElement.GetProperty("selection").GetString());
        int revision = state.RootElement.GetProperty("revision").GetInt32();
        using var release = await fixture.Client.PostAsJsonAsync($"/management/captures/{id}/release", new { expectedRevision = revision }, token);
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        using var released = JsonDocument.Parse(await release.Content.ReadAsStringAsync(token));
        revision = released.RootElement.GetProperty("revision").GetInt32();

        using var stale = await fixture.Client.PutAsJsonAsync(path,
            new { expectedRevision = 0, rating = 5, label = "Red", tags = new[] { "stale" } }, token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var conflict = JsonDocument.Parse(await stale.Content.ReadAsStringAsync(token));
        Assert.Equal(-1, conflict.RootElement.GetProperty("current").GetProperty("rating").GetInt32());
        Assert.Equal("Purple", conflict.RootElement.GetProperty("current").GetProperty("label").GetString());

        using var reset = await fixture.Client.PutAsJsonAsync(path,
            new { expectedRevision = revision, rating = 0, label = (string?)null, tags = Array.Empty<string>() }, token);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        await using var restarted = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity stored = await restarted.Captures.AsNoTracking().SingleAsync(item => item.Id == id, token);
        Assert.Equal(0, stored.Rating);
        Assert.Null(stored.Label);
        Assert.Empty(stored.Tags);
        Assert.Equal(-1, stored.ReleasedRating);
        Assert.Equal("Purple", stored.ReleasedLabel);
        Assert.Equal(new[] { "旅行,夏", "a,b" }, stored.ReleasedTags);
        Assert.NotNull(stored.ReleasedAt);
    }

    [Theory]
    [InlineData("{\"rating\":5,\"label\":null,\"tags\":[]}")]
    [InlineData("{\"expectedRevision\":0,\"rating\":0,\"label\":null,\"tags\":[],\"selection\":\"Picked\"}")]
    [InlineData("{\"expectedRevision\":0,\"rating\":-2,\"label\":null,\"tags\":[]}")]
    [InlineData("{\"expectedRevision\":0,\"rating\":6,\"label\":null,\"tags\":[]}")]
    [InlineData("{\"expectedRevision\":0,\"rating\":0,\"label\":\"None\",\"tags\":[]}")]
    public async Task Invalid_metadata_or_missing_revision_never_mutates_capture(string body)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PutAsync($"/management/captures/{id}/curation", content, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity stored = await database.Captures.SingleAsync(item => item.Id == id, token);
        Assert.Equal(0, stored.Revision);
        Assert.Equal(0, stored.Rating);
        Assert.Null(stored.Label);
        Assert.Empty(stored.Tags);
    }

    [Fact]
    public async Task Batch_curation_returns_committed_conflict_invalid_and_missing_states_without_merging()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] ids = await SeedMetadataCapturesAsync(fixture, 3);
        Guid missing = Guid.NewGuid();
        using var response = await fixture.Client.PostAsJsonAsync("/management/captures/batch/curation", new
        {
            items = new[]
            {
                new { id = ids[0], expectedRevision = 0, rating = -1, label = "Blue", tags = new[] { "a,b" } },
                new { id = ids[1], expectedRevision = 7, rating = 5, label = "Red", tags = new[] { "stale" } },
                new { id = ids[2], expectedRevision = 0, rating = 6, label = "Green", tags = Array.Empty<string>() },
                new { id = missing, expectedRevision = 0, rating = 3, label = "Yellow", tags = Array.Empty<string>() },
            },
        }, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var items = result.RootElement.GetProperty("items").EnumerateArray().ToDictionary(item => item.GetProperty("id").GetGuid());
        Assert.Equal(4, items.Count);
        Assert.Equal("committed", items[ids[0]].GetProperty("status").GetString());
        Assert.Equal("conflict", items[ids[1]].GetProperty("status").GetString());
        Assert.Equal(0, items[ids[1]].GetProperty("current").GetProperty("rating").GetInt32());
        Assert.Equal("invalid", items[ids[2]].GetProperty("status").GetString());
        Assert.Equal("not_found", items[missing].GetProperty("status").GetString());
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity[] states = await database.Captures.AsNoTracking().OrderBy(item => item.Id).ToArrayAsync(token);
        Assert.Single(states, item => item.Revision == 1 && item.Rating == -1 && item.Label == "Blue" && item.Tags.SequenceEqual(["a,b"]));
        Assert.Equal(2, states.Count(item => item.Revision == 0 && item.Rating == 0 && item.Label is null && item.Tags.Length == 0));
    }

    [Fact]
    public async Task Batch_curation_rejects_omitted_label_but_accepts_explicit_null_to_clear_it()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        await using (var seed = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await seed.Captures.SingleAsync(item => item.Id == id, token);
            capture.Label = "Blue";
            await seed.SaveChangesAsync(token);
        }

        using var omittedContent = new StringContent($"{{\"items\":[{{\"id\":\"{id:D}\",\"expectedRevision\":0,\"rating\":4,\"tags\":[]}}]}}",
            System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage omitted = await fixture.Client.PostAsync("/management/captures/batch/curation", omittedContent, token);
        Assert.Equal(HttpStatusCode.OK, omitted.StatusCode);
        using JsonDocument omittedBody = JsonDocument.Parse(await omitted.Content.ReadAsStringAsync(token));
        Assert.Equal("invalid", omittedBody.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        await using (var unchanged = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await unchanged.Captures.AsNoTracking().SingleAsync(item => item.Id == id, token);
            Assert.Equal(0, capture.Revision);
            Assert.Equal(0, capture.Rating);
            Assert.Equal("Blue", capture.Label);
        }

        using HttpResponseMessage clear = await fixture.Client.PostAsJsonAsync("/management/captures/batch/curation", new
        {
            items = new[] { new { id, expectedRevision = 0, rating = 4, label = (string?)null, tags = Array.Empty<string>() } },
        }, token);
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using JsonDocument cleared = JsonDocument.Parse(await clear.Content.ReadAsStringAsync(token));
        Assert.Equal("committed", cleared.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity stored = await verify.Captures.AsNoTracking().SingleAsync(item => item.Id == id, token);
        Assert.Equal(1, stored.Revision);
        Assert.Null(stored.Label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(2)]
    public async Task Malformed_batch_envelope_writes_nothing(int count)
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid id = (await SeedMetadataCapturesAsync(fixture, 1))[0];
        var items = Enumerable.Range(0, count).Select(index => new
        {
            id = count == 2 || index == 0 ? id : Guid.NewGuid(),
            expectedRevision = 0, rating = 5, label = "Red", tags = Array.Empty<string>()
        }).ToArray();
        using var response = await fixture.Client.PostAsJsonAsync("/management/captures/batch/curation", new { items }, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity stored = await database.Captures.SingleAsync(item => item.Id == id, token);
        Assert.Equal(0, stored.Revision);
        Assert.Equal(0, stored.Rating);
        Assert.Null(stored.Label);
    }

    [Fact]
    public async Task Capture_cursors_bind_filters_and_late_assets_do_not_move_the_page_anchor()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] ids = await SeedMetadataCapturesAsync(fixture, 3);
        using var first = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/captures?limit=1&rating=0", token));
        Assert.Equal(ids[2], first.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        string escaped = Uri.EscapeDataString(cursor);
        using var mismatched = await fixture.Client.GetAsync($"/management/captures?limit=1&rating=5&cursor={escaped}", token);
        Assert.Equal(HttpStatusCode.BadRequest, mismatched.StatusCode);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity older = await database.Captures.SingleAsync(item => item.Id == ids[0], token);
            older.LastPrimaryIngestedAt = DateTimeOffset.UtcNow.AddHours(1);
            await database.SaveChangesAsync(token);
        }
        using var next = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/captures?limit=2&rating=0&cursor={escaped}", token));
        Assert.Equal(new[] { ids[1], ids[0] }, next.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, next.RootElement.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Capture_start_at_begins_at_matching_capture_or_falls_back_to_first_match()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] ids = await SeedMetadataCapturesAsync(fixture, 3);

        using var matching = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?limit=2&rating=0&startAt={ids[1]}", token));
        Assert.Equal(new[] { ids[1], ids[0] }, matching.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()));

        using var oldest = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?limit=2&rating=0&oldestFirst=true&startAt={ids[1]}", token));
        Assert.Equal(new[] { ids[1], ids[2] }, oldest.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()));

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            DateTimeOffset acceptedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
            CaptureEntity[] captures = await database.Captures.ToArrayAsync(token);
            foreach (CaptureEntity capture in captures) capture.FirstAcceptedAt = acceptedAt;
            await database.SaveChangesAsync(token);
        }
        using var equalTimestampPage = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            "/management/captures?limit=3&rating=0", token));
        Guid[] ordered = equalTimestampPage.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        using var equalTimestampStart = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?limit=3&rating=0&startAt={ordered[1]}", token));
        Assert.Equal(ordered.Skip(1), equalTimestampStart.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()));

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity latest = await database.Captures.SingleAsync(item => item.Id == ids[2], token);
            latest.Rating = 5;
            await database.SaveChangesAsync(token);
        }
        using var missingMatch = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?limit=2&rating=5&startAt={ids[1]}", token));
        Assert.Equal(new[] { ids[2] }, missingMatch.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()));

        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity trashed = await database.Captures.SingleAsync(item => item.Id == ids[2], token);
            trashed.Rating = 0;
            trashed.RetentionDisposition = "Trash";
            await database.SaveChangesAsync(token);
        }
        using var currentEligiblePage = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            "/management/captures?limit=2&rating=0", token));
        Guid[] eligibleIds = currentEligiblePage.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()).ToArray();
        using var ineligible = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?limit=2&rating=0&startAt={ids[2]}", token));
        Assert.Equal(eligibleIds, ineligible.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid()));

        using var withCursor = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/captures?limit=1", token));
        string cursor = Uri.EscapeDataString(withCursor.RootElement.GetProperty("nextCursor").GetString()!);
        using var incompatible = await fixture.Client.GetAsync(
            $"/management/captures?cursor={cursor}&startAt={ids[1]}", token);
        Assert.Equal(HttpStatusCode.BadRequest, incompatible.StatusCode);
    }

    [Fact]
    public async Task Capture_filters_and_workspace_facts_keep_older_manual_work_discoverable()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] ids = await SeedMetadataCapturesAsync(fixture, 3);
        Guid sourceId;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity rejected = await database.Captures.SingleAsync(item => item.Id == ids[0], token);
            sourceId = rejected.SourceId;
            rejected.Rating = -1;
            rejected.Label = "Purple";
            rejected.Tags = ["旅行, portrait"];
            CaptureEntity released = await database.Captures.SingleAsync(item => item.Id == ids[2], token);
            released.Rating = 5;
            released.Held = false;
            released.ReleasedAt = DateTimeOffset.UtcNow;
            var source = await database.Sources.SingleAsync(item => item.Id == sourceId, token);
            source.IsArchived = true;
            await database.SaveChangesAsync(token);
        }
        using var page = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?sourceId={sourceId}&rating=-1&selection=Rejected&label=Purple&tag={Uri.EscapeDataString("旅行, portrait")}&released=false&held=true&attention=false", token));
        Assert.Equal(ids[0], page.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        Assert.False(page.RootElement.GetProperty("anySourceUsesCuration").GetBoolean());
        Assert.True(page.RootElement.GetProperty("anyCaptureNeedsCuration").GetBoolean());
        using var noLabel = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/captures?label=None&selection=Picked&released=true&held=false", token));
        Assert.Equal(ids[2], noLabel.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Capture_rating_ranges_and_any_labels_filter_before_paging_and_tag_suggestions_are_bounded()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] ids = await SeedMetadataCapturesAsync(fixture, 5);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            int[] ratings = [-1, 0, 1, 3, 5];
            for (int index = 0; index < 5; index++)
            {
                CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == ids[index], token);
                capture.Rating = ratings[index];
                capture.Label = index is 1 or 3 ? "Blue" : null;
                capture.Tags = [$"Travel-{index:00}"];
            }
            await database.SaveChangesAsync(token);
        }

        using var filtered = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            "/management/captures?limit=1&rating=0&ratingMaximum=3&labels=Red&labels=Blue", token));
        Assert.Equal(ids[3], filtered.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        using var unlabeled = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            "/management/captures?rating=-1&ratingMaximum=0&labels=None", token));
        Assert.Equal(ids[0], unlabeled.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        using var invalid = await fixture.Client.GetAsync("/management/captures?rating=2&ratingMaximum=1", token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var conflictingLabels = await fixture.Client.GetAsync(
            "/management/captures?label=Blue&labels=Blue", token);
        Assert.Equal(HttpStatusCode.BadRequest, conflictingLabels.StatusCode);

        using var suggestions = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/tags?q=travel", token));
        Assert.Equal(5, suggestions.RootElement.GetProperty("tags").GetArrayLength());
        Assert.Equal("Travel-00", suggestions.RootElement.GetProperty("tags")[0].GetString());
        using var empty = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/tags?q=", token));
        Assert.Empty(empty.RootElement.GetProperty("tags").EnumerateArray());

        Guid[] moreIds = [.. Enumerable.Range(0, 12).Select(_ => Guid.NewGuid())];
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity template = await database.Captures.SingleAsync(item => item.Id == ids[0], token);
            database.Captures.AddRange(moreIds.Select((id, index) => new CaptureEntity
            {
                Id = id, SourceId = template.SourceId, NormalizedStem = $"SYNTHETIC-TAG-{index}",
                Policy = SourceCurationMode.Manual, Held = true, State = CaptureState.CURATING,
                FirstAcceptedAt = template.FirstAcceptedAt.AddMinutes(index + 1),
                LastPrimaryIngestedAt = template.LastPrimaryIngestedAt.AddMinutes(index + 1), Tags = [$"Travel-extra-{index:00}"],
            }));
            await database.SaveChangesAsync(token);
        }
        using var bounded = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/tags?q=travel", token));
        Assert.Equal(12, bounded.RootElement.GetProperty("tags").GetArrayLength());
    }

    [Fact]
    public async Task Capture_source_and_workflow_multiselects_filter_before_paging_and_bind_cursors()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid[] ids = await SeedMetadataCapturesAsync(fixture, 4);
        using HttpResponseMessage sourceResponse = await fixture.Client.PostAsJsonAsync("/management/sources",
            new { name = $"Camera-{Guid.NewGuid():N}", sourceType = "CameraFtpUpload", connectionProfile = "ModernFtps" }, token);
        Assert.Equal(HttpStatusCode.Created, sourceResponse.StatusCode);
        using JsonDocument sourceJson = JsonDocument.Parse(await sourceResponse.Content.ReadAsStringAsync(token));
        Guid secondSourceId = sourceJson.RootElement.GetProperty("id").GetGuid();
        Guid waitingId = Guid.NewGuid(), heldId = Guid.NewGuid(), releasedId = Guid.NewGuid(), firstSourceId;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity[] captures = await database.Captures.Where(item => ids.Contains(item.Id)).OrderBy(item => item.FirstAcceptedAt).ToArrayAsync(token);
            firstSourceId = captures[0].SourceId;
            captures[0].Rating = 1;
            captures[3].Held = false;
            captures[3].ReleasedAt = DateTimeOffset.UtcNow;
            captures[3].Rating = 4;
            DateTimeOffset later = DateTimeOffset.UtcNow;
            database.Captures.AddRange(
                new CaptureEntity { Id = waitingId, SourceId = secondSourceId, NormalizedStem = "MULTI-WAITING", Policy = SourceCurationMode.Manual, Held = false, State = CaptureState.CURATING, Rating = 3, FirstAcceptedAt = later, LastPrimaryIngestedAt = later },
                new CaptureEntity { Id = releasedId, SourceId = secondSourceId, NormalizedStem = "MULTI-RELEASED", Policy = SourceCurationMode.Manual, Held = false, State = CaptureState.RELEASED, ReleasedAt = later, Rating = 3, FirstAcceptedAt = later.AddMilliseconds(-1), LastPrimaryIngestedAt = later.AddMilliseconds(-1) },
                new CaptureEntity { Id = heldId, SourceId = secondSourceId, NormalizedStem = "MULTI-HELD", Policy = SourceCurationMode.Manual, Held = true, State = CaptureState.CURATING, Rating = 2, FirstAcceptedAt = later.AddSeconds(-1), LastPrimaryIngestedAt = later.AddSeconds(-1) });
            await database.SaveChangesAsync(token);
        }

        using var waitingIncludesHeld = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?sourceIds={secondSourceId}&workflows=Waiting&rating=2&ratingMaximum=2", token));
        Assert.Equal(heldId, waitingIncludesHeld.RootElement.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        using var first = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?limit=1&sourceIds={firstSourceId}&sourceIds={secondSourceId}&sourceIds={firstSourceId}&workflows=Held&workflows=Released&rating=1&ratingMaximum=3", token));
        Assert.Equal(releasedId, first.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        string cursor = first.RootElement.GetProperty("nextCursor").GetString()!;
        using var reordered = JsonDocument.Parse(await fixture.Client.GetStringAsync(
            $"/management/captures?limit=2&sourceIds={secondSourceId}&sourceIds={firstSourceId}&sourceIds={secondSourceId}&workflows=Released&workflows=Held&workflows=Released&rating=1&ratingMaximum=3&cursor={Uri.EscapeDataString(cursor)}", token));
        Assert.Equal(heldId, reordered.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(ids[0], reordered.RootElement.GetProperty("items")[1].GetProperty("id").GetGuid());
        using var changed = await fixture.Client.GetAsync(
            $"/management/captures?sourceIds={secondSourceId}&workflows=Held&rating=1&ratingMaximum=3&cursor={Uri.EscapeDataString(cursor)}", token);
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
        using var ambiguous = await fixture.Client.GetAsync(
            $"/management/captures?sourceId={secondSourceId}&sourceIds={secondSourceId}", token);
        Assert.Equal(HttpStatusCode.BadRequest, ambiguous.StatusCode);
        using var invalidWorkflow = await fixture.Client.GetAsync("/management/captures?workflows=waiting", token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidWorkflow.StatusCode);
        foreach (string conflictingFilter in new[] { "released=false", "held=false", "attention=false" })
        {
            using var conflict = await fixture.Client.GetAsync($"/management/captures?workflows=Released&{conflictingFilter}", token);
            Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        }
        using var tooManySources = await fixture.Client.GetAsync("/management/captures?" +
            string.Join('&', Enumerable.Range(0, 31).Select(_ => $"sourceIds={Guid.NewGuid()}")), token);
        Assert.Equal(HttpStatusCode.BadRequest, tooManySources.StatusCode);
        using var tooManyWorkflows = await fixture.Client.GetAsync(
            "/management/captures?workflows=Waiting&workflows=Held&workflows=Released&workflows=Attention&workflows=Waiting", token);
        Assert.Equal(HttpStatusCode.BadRequest, tooManyWorkflows.StatusCode);
    }

    [Fact]
    public async Task Capture_detail_projects_authoritative_asset_size()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        var source = await CreateSourceAsync(fixture.Client, token);
        Guid captureId = Guid.NewGuid(), assetId = Guid.NewGuid();
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            database.Captures.Add(new CaptureEntity
            {
                Id = captureId, SourceId = source.Id, NormalizedStem = "SYNTHETIC-SIZE", Policy = SourceCurationMode.Manual,
                Held = true, State = CaptureState.CURATING, FirstAcceptedAt = now, LastPrimaryIngestedAt = now,
            });
            database.Assets.Add(new AssetEntity
            {
                Id = assetId, SourceId = source.Id, OriginalName = "synthetic.jpg", SpoolKey = "synthetic/size",
                Size = 12345, Sha256 = new string('0', 64), State = "Spooled", IngestedAt = now, RecoveryCorrelation = Guid.NewGuid(),
                CameraMake = "Synthetic Camera Co.", CameraModel = "Model X",
            });
            database.CaptureAssets.Add(new CaptureAssetEntity
            {
                AssetId = assetId, CaptureId = captureId, Role = "JPEG", IsPrimary = true, GroupingReason = "synthetic",
            });
            await database.SaveChangesAsync(token);
        }

        using JsonDocument detail = JsonDocument.Parse(await fixture.Client.GetStringAsync($"/management/captures/{captureId}", token));
        Assert.Equal(12345, detail.RootElement.GetProperty("assets")[0].GetProperty("size").GetInt64());
        using JsonDocument inbox = JsonDocument.Parse(await fixture.Client.GetStringAsync("/management/captures", token));
        JsonElement inboxAsset = inbox.RootElement.GetProperty("items")[0].GetProperty("assets")[0];
        Assert.Equal("Synthetic Camera Co.", inboxAsset.GetProperty("cameraMake").GetString());
        Assert.Equal("Model X", inboxAsset.GetProperty("cameraModel").GetString());
    }

    [Fact]
    public async Task Preview_rejects_out_of_range_dimension_before_reading_media()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = (await SeedMetadataCapturesAsync(fixture, 1))[0];

        using HttpResponseMessage response = await fixture.Client.GetAsync(
            $"/media/previews/{captureId}/grid?maximumDimension=63", token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty("maximumDimension", out _));
    }

    private static async Task<Guid[]> SeedMetadataCapturesAsync(ConfigurationApiFixture fixture, int count)
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var source = await CreateSourceAsync(fixture.Client, token);
        Guid[] ids = [.. Enumerable.Range(0, count).Select(_ => Guid.NewGuid())];
        DateTimeOffset anchor = DateTimeOffset.UtcNow.AddMinutes(-10);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        database.Captures.AddRange(ids.Select((id, index) => new CaptureEntity
        {
            Id = id, SourceId = source.Id, NormalizedStem = $"SYNTHETIC-{index}", Policy = SourceCurationMode.Manual,
            Held = true, State = CaptureState.CURATING, FirstAcceptedAt = anchor.AddMinutes(index), LastPrimaryIngestedAt = anchor.AddMinutes(index),
        }));
        await database.SaveChangesAsync(token);
        return ids;
    }
}
