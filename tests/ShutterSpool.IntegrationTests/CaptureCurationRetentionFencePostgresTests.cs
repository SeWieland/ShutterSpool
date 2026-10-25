using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Curation;
using ShutterSpool.Infrastructure.Persistence;
using System.Net;
using System.Net.Http.Json;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Trash_capture_cannot_be_curated_again()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            capture.Tags = ["before-trash"];
            capture.RetentionDisposition = "Trash";
            await database.SaveChangesAsync(token);
        }

        using var response = await fixture.Client.PutAsJsonAsync($"/management/captures/{captureId}/curation",
            new { expectedRevision = 0, rating = 5, label = (string?)null, tags = new[] { "after-trash" } }, token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal(new[] { "before-trash" }, (await verify.Captures.SingleAsync(item => item.Id == captureId, token)).Tags);
    }

    [Fact]
    public async Task Waiting_erasure_capture_cannot_be_curated_or_repopulated()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (var database = new ShutterSpoolDbContext(fixture.Database.Options))
        {
            CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
            capture.Tags = ["before-erasure"];
            AssetEntity asset = await database.Assets.SingleAsync(item => database.CaptureAssets
                .Any(member => member.CaptureId == captureId && member.AssetId == item.Id), token);
            database.PrivacyErasureOperations.Add(new PrivacyErasureOperationEntity
            { Id = Guid.NewGuid(), RequestedAt = now, State = "Pending" });
            Guid operationId = database.PrivacyErasureOperations.Local.Single().Id;
            database.PrivacyErasureAssets.Add(new PrivacyErasureAssetEntity
            {
                AssetId = asset.Id, CaptureId = captureId, OperationId = operationId, SpoolKey = asset.SpoolKey,
                ExpectedSize = asset.Size, StorageMarker = "synthetic-marker", RequestedAt = now, State = "Waiting",
            });
            await database.SaveChangesAsync(token);
        }

        using var response = await fixture.Client.PutAsJsonAsync($"/management/captures/{captureId}/curation",
            new { expectedRevision = 0, rating = 5, label = (string?)null, tags = new[] { "after-erasure" } }, token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await using var verify = new ShutterSpoolDbContext(fixture.Database.Options);
        Assert.Equal(new[] { "before-erasure" }, (await verify.Captures.SingleAsync(item => item.Id == captureId, token)).Tags);
    }

    [Fact]
    public async Task Release_due_worker_skips_paused_capture()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        CaptureEntity capture = await database.Captures.SingleAsync(item => item.Id == captureId, token);
        capture.Held = false;
        capture.ReleaseDueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        capture.RetentionPaused = true;
        capture.ReleasedAt = null;
        await database.SaveChangesAsync(token);

        bool released = await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System))
            .ReleaseDueOneAsync(DateTimeOffset.UtcNow, token);

        Assert.False(released);
        CaptureEntity paused = await database.Captures.SingleAsync(item => item.Id == captureId, token);
        Assert.Null(paused.ReleasedAt);

        var curation = new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System));
        Assert.False((await curation.ReleaseNowAsync(captureId, paused.Revision, token)).Succeeded);
        CaptureCurationState current = Assert.IsType<CaptureCurationState>(await curation.GetAsync(captureId, token));
        Assert.True((await curation.UpdateAsync(captureId, current.Revision,
            new CaptureCuration(3, "Blue", ["operator-note"]), token)).Succeeded);
        CaptureEntity afterEdit = await database.Captures.SingleAsync(item => item.Id == captureId, token);
        Assert.True(afterEdit.RetentionPaused);
        Assert.Null(afterEdit.ReleasedAt);
    }
}
