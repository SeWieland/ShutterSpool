using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Text.Json.Nodes;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Purged_capture_does_not_keep_global_curation_visibility_enabled()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            capture.RetentionDisposition = "Purged";
            capture.Held = false;
            capture.ReleasedAt = null;
            SourceEntity source = await database.Sources.SingleAsync(item => item.Id == capture.SourceId, token);
            source.CurationMode = ShutterSpool.Domain.Sources.SourceCurationMode.Immediate;
            await database.SaveChangesAsync(token);
        }

        JsonNode inbox = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/captures?limit=100", token))!;
        Assert.DoesNotContain(inbox["items"]!.AsArray(), item => item!["id"]!.GetValue<Guid>() == captureId);
        Assert.False(inbox["anyCaptureNeedsCuration"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Inbox_and_tag_suggestions_hide_waiting_erasure_before_metadata_redaction()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        Guid assetId;
        string spoolKey;
        long size;
        string storageMarker;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        const string privateTag = "m205-private-erasure-fixture-tag";
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            capture.Tags = [privateTag];
            AssetEntity asset = await database.Assets.SingleAsync(item => database.CaptureAssets
                .Any(member => member.CaptureId == captureId && member.AssetId == item.Id), token);
            assetId = asset.Id;
            spoolKey = asset.SpoolKey;
            size = asset.Size;
            storageMarker = await database.SpoolQuotaObservations.AsNoTracking()
                .Where(item => item.Id == 1).Select(item => item.StorageMarker!).SingleAsync(token);
            Guid operationId = Guid.NewGuid();
            database.PrivacyErasureOperations.Add(new PrivacyErasureOperationEntity
            {
                Id = operationId, RequestedAt = now, State = "Pending",
            });
            database.PrivacyErasureAssets.Add(new PrivacyErasureAssetEntity
            {
                AssetId = assetId, CaptureId = captureId, OperationId = operationId,
                SpoolKey = spoolKey, ExpectedSize = size, StorageMarker = storageMarker,
                KnownTransientKeys = [], RequestedAt = now, State = "Waiting",
            });
            await database.SaveChangesAsync(token);
        }

        using var detail = await fixture.Client.GetAsync($"/management/captures/{captureId}", token);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);

        JsonNode inbox = JsonNode.Parse(await fixture.Client.GetStringAsync("/management/captures?limit=100", token))!;
        Assert.DoesNotContain(inbox["items"]!.AsArray(), item => item!["id"]!.GetValue<Guid>() == captureId);

        JsonNode suggestions = JsonNode.Parse(await fixture.Client.GetStringAsync($"/management/tags?q={privateTag}", token))!;
        Assert.DoesNotContain(suggestions["tags"]!.AsArray(), item => item!.GetValue<string>() == privateTag);
    }
}
