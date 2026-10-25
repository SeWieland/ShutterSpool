using Microsoft.EntityFrameworkCore;
using ShutterSpool.Infrastructure.Persistence;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace ShutterSpool.IntegrationTests;

public sealed partial class ConfigurationApiTests
{
    [Fact]
    public async Task Inactive_quota_does_not_report_unknown_accounting_as_blocked_ingest()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        JsonObject? capacity = await fixture.Client.GetFromJsonAsync<JsonObject>("/management/retention/capacity", token);
        Assert.True(capacity!["unknownAccounting"]!.GetValue<bool>());
        Assert.False(capacity["admissionBlocked"]!.GetValue<bool>());
        Assert.DoesNotContain(capacity["blockers"]!.AsArray(), item => item!.GetValue<string>() == "admission-blocked");
    }

    [Fact]
    public async Task Expired_history_cursor_reports_a_gap_instead_of_an_unbroken_empty_page()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        DateTimeOffset deletedAt = DateTimeOffset.UtcNow.AddDays(-40);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        RetentionPressureStateEntity pressure = await database.RetentionPressureStates.SingleAsync(token);
        pressure.HistoryPrunedBefore = deletedAt.AddDays(1);
        await database.SaveChangesAsync(token);
        string cursor = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{deletedAt.UtcDateTime.Ticks}:{Guid.NewGuid():N}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        JsonObject? history = await fixture.Client.GetFromJsonAsync<JsonObject>(
            $"/management/retention/history?cursor={cursor}", token);
        Assert.True(history!["hasGap"]!.GetValue<bool>());
        Assert.Empty(history["items"]!.AsArray());
    }

    [Fact]
    public async Task Capacity_counts_overlapping_promises_once_and_keeps_revoked_gallery_protected()
    {
        await using var fixture = await ConfigurationApiFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        Guid captureId = await SeedRetentionCaptureAsync(fixture, token);
        await using var database = new ShutterSpoolDbContext(fixture.Database.Options);
        Guid assetId = await database.CaptureAssets.Where(member => member.CaptureId == captureId)
            .Select(member => member.AssetId).SingleAsync(token);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var gallery = new GalleryProtectionEntity
        {
            Id = Guid.NewGuid(), CreatedAt = now, ExpiresAt = now.AddDays(1), AccessRevokedAt = now,
        };
        var hold = new PreservationHoldEntity
        {
            Id = Guid.NewGuid(), CreatedAt = now, ReviewAt = now.AddDays(-1),
            Authority = "synthetic", Reason = "synthetic preservation",
        };
        database.GalleryProtections.Add(gallery);
        database.GalleryProtectionAssets.Add(new() { ProtectionId = gallery.Id, AssetId = assetId });
        database.PreservationHolds.Add(hold);
        database.PreservationHoldAssets.Add(new() { HoldId = hold.Id, AssetId = assetId });
        await database.SaveChangesAsync(token);

        JsonObject? capacity = await fixture.Client.GetFromJsonAsync<JsonObject>("/management/retention/capacity", token);
        Assert.Equal(4L, capacity!["protectedBytes"]!.GetValue<long>());

        // Review time passing grants no deletion authority; explicit resolution does.
        hold.ResolvedAt = now;
        hold.ResolvedBy = "synthetic operator";
        hold.Resolution = "synthetic resolution";
        gallery.ExpiresAt = now.AddSeconds(-1);
        await database.SaveChangesAsync(token);
        capacity = await fixture.Client.GetFromJsonAsync<JsonObject>("/management/retention/capacity", token);
        Assert.Equal(0L, capacity!["protectedBytes"]!.GetValue<long>());
    }
}
