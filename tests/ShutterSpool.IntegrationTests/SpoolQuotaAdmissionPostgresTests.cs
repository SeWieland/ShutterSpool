using Microsoft.EntityFrameworkCore;
using ShutterSpool.Application.Retention;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed class SpoolQuotaAdmissionPostgresTests
{
    [Fact]
    public async Task Inactive_quota_keeps_a_bounded_reservation_when_inventory_is_unknown()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Directory.CreateDirectory(fixture.SpoolRoot);
        var accounting = new FileSystemSpoolAccounting(fixture.SpoolRoot, Path.Combine(fixture.SpoolRoot, "previews"),
            reserveBytes: 0, reservePercent: null, TimeProvider.System);
        await using var context = new ShutterSpoolDbContext(fixture.Options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        context.Assets.Add(new AssetEntity
        {
            Id = Guid.NewGuid(), SourceId = Guid.NewGuid(), OriginalName = "legacy.raw", SpoolKey = "spool/legacy.raw",
            Size = 1, Sha256 = new string('b', 64), State = "Spooled", IngestedAt = DateTimeOffset.UtcNow,
            RecoveryCorrelation = Guid.NewGuid(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var quota = new EfSpoolQuotaStore(context, accounting, TimeProvider.System);
        SpoolQuotaSnapshot snapshot = await quota.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.IsKnown);
        Assert.False(snapshot.Settings.IsActive);
        Guid unusedId = Guid.NewGuid();
        Assert.True(await quota.TryReserveAsync(new(unusedId, Guid.NewGuid(), 32 * 1024 * 1024, 0,
            [$"spool/2026/10/{Guid.NewGuid():N}"], 32 * 1024 * 1024), TestContext.Current.CancellationToken));

        SpoolQuotaSnapshot afterReservation = await quota.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.False(afterReservation.IsKnown);
        Assert.Equal(32 * 1024 * 1024, afterReservation.ReservedBytes);
        await quota.ReleaseUnadmittedReservationAsync(unusedId, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await quota.GetSnapshotAsync(TestContext.Current.CancellationToken)).ReservedBytes);

        Guid admittedId = Guid.NewGuid();
        Guid admittedSourceId = Guid.NewGuid();
        Guid admittedLoginId = Guid.NewGuid();
        context.Sources.Add(new SourceEntity
        {
            Id = admittedSourceId,
            Name = $"quota-admission-{admittedSourceId:N}",
        });
        context.CameraLogins.Add(new CameraLoginEntity
        {
            Id = admittedLoginId,
            SourceId = admittedSourceId,
            Username = "synthetic",
            VirtualRoot = "/",
            CredentialGeneration = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        Assert.True(await quota.TryReserveAsync(new(admittedId, Guid.NewGuid(), 64, 0,
            ["spool/2026/10/admitted"], 64), TestContext.Current.CancellationToken));
        context.CameraUploadAdmissions.Add(new CameraUploadAdmissionEntity
        {
            Id = admittedId, LoginId = admittedLoginId, SourceId = admittedSourceId, Username = "synthetic",
            SessionId = "session", VirtualPath = "/admitted.raw", AdmissionTimestamp = 1,
            State = ShutterSpool.Application.Ingress.CameraTransferState.Admitted,
            MaximumUploadBytes = 64, ReceiveKey = "spool/2026/10/admitted",
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await quota.ReleaseUnadmittedReservationAsync(admittedId, TestContext.Current.CancellationToken);
        Assert.Equal(64, (await quota.GetSnapshotAsync(TestContext.Current.CancellationToken)).ReservedBytes);
    }

    [Fact]
    public async Task Existing_root_marker_requires_acknowledgement_and_exact_reconciliation()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.SpoolRoot, "spool"));
        Directory.CreateDirectory(Path.Combine(fixture.SpoolRoot, "incoming"));
        Directory.CreateDirectory(Path.Combine(fixture.SpoolRoot, "quarantine"));
        var accounting = new FileSystemSpoolAccounting(fixture.SpoolRoot, Path.Combine(fixture.SpoolRoot, "previews"),
            reserveBytes: 0, reservePercent: null, TimeProvider.System);
        Guid assetId = Guid.NewGuid(), sourceId = Guid.NewGuid();
        string spoolKey = $"spool/2026/10/{assetId:N}";
        string physicalPath = Path.Combine(fixture.SpoolRoot, spoolKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        await File.WriteAllBytesAsync(physicalPath, [1, 2, 3], TestContext.Current.CancellationToken);
        string untrackedKey = $"spool/2026/10/{Guid.NewGuid():N}";
        string untrackedPath = Path.Combine(fixture.SpoolRoot, untrackedKey.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllBytesAsync(untrackedPath, [4], TestContext.Current.CancellationToken);

        await using var context = new ShutterSpoolDbContext(fixture.Options);
        await context.Database.MigrateAsync(TestContext.Current.CancellationToken);
        context.Assets.Add(new AssetEntity
        {
            Id = assetId, SourceId = sourceId, OriginalName = "legacy.raw", SpoolKey = spoolKey,
            Size = 3, Sha256 = new string('b', 64), State = "Spooled", IngestedAt = DateTimeOffset.UtcNow,
            RecoveryCorrelation = Guid.NewGuid(),
        });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        var quota = new EfSpoolQuotaStore(context, accounting, TimeProvider.System);

        Assert.False((await quota.GetSnapshotAsync(TestContext.Current.CancellationToken)).IsKnown);
        Assert.False(File.Exists(Path.Combine(fixture.SpoolRoot, ".shutterspool-root.marker")));

        SpoolQuotaSettings settings = await quota.GetSettingsAsync(TestContext.Current.CancellationToken);
        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            Assert.NotNull(await quota.StageSettingsUpdateAsync(settings.Revision,
                new(settings.Revision, true, 100, 80, 50), authorizeStorageMarkerAdoption: true,
                cancellationToken: TestContext.Current.CancellationToken));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.False((await quota.GetSnapshotAsync(TestContext.Current.CancellationToken)).IsKnown);
        Assert.False(File.Exists(Path.Combine(fixture.SpoolRoot, ".shutterspool-root.marker")));
        File.Delete(untrackedPath);

        SpoolQuotaSnapshot adopted = await quota.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.True(adopted.IsKnown);
        Assert.Equal(3, adopted.UsedBytes);
        string marker = await File.ReadAllTextAsync(Path.Combine(fixture.SpoolRoot, ".shutterspool-root.marker"),
            TestContext.Current.CancellationToken);
        Assert.Equal(32, marker.Length);

        File.Delete(Path.Combine(fixture.SpoolRoot, ".shutterspool-root.marker"));
        Assert.False((await quota.GetSnapshotAsync(TestContext.Current.CancellationToken)).IsKnown);
    }

    [Fact]
    public async Task Reservations_serialize_at_the_cap_and_committed_asset_replaces_reservation_once()
    {
        await using var fixture = await ArchitectureTestDatabase.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.SpoolRoot, "spool"));
        Directory.CreateDirectory(Path.Combine(fixture.SpoolRoot, "incoming"));
        Directory.CreateDirectory(Path.Combine(fixture.SpoolRoot, "quarantine"));
        var accounting = new FileSystemSpoolAccounting(fixture.SpoolRoot, Path.Combine(fixture.SpoolRoot, "previews"),
            reserveBytes: 0, reservePercent: null, TimeProvider.System);
        await using (var setup = new ShutterSpoolDbContext(fixture.Options))
        {
            await setup.Database.MigrateAsync(TestContext.Current.CancellationToken);
            var scanner = new EfSpoolQuotaStore(setup, accounting, TimeProvider.System);
            _ = await scanner.GetSnapshotAsync(TestContext.Current.CancellationToken);
            SpoolQuotaSettings settings = await scanner.GetSettingsAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(await scanner.UpdateSettingsAsync(settings.Revision,
                new(settings.Revision, true, 100, 80, 50), TestContext.Current.CancellationToken));
        }

        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        await using var firstContext = new ShutterSpoolDbContext(fixture.Options);
        await using var secondContext = new ShutterSpoolDbContext(fixture.Options);
        var firstStore = new EfSpoolQuotaStore(firstContext, accounting, TimeProvider.System);
        var secondStore = new EfSpoolQuotaStore(secondContext, accounting, TimeProvider.System);
        Task<bool> first = firstStore.TryReserveAsync(new(firstId, Guid.NewGuid(), 60, 0, [], 1), TestContext.Current.CancellationToken);
        Task<bool> second = secondStore.TryReserveAsync(new(secondId, Guid.NewGuid(), 60, 0, [], 1), TestContext.Current.CancellationToken);

        bool[] admitted = await Task.WhenAll(first, second);

        Assert.Single(admitted, value => value);
        Assert.Single(admitted, value => !value);
        Guid winner = admitted[0] ? firstId : secondId;
        await (admitted[0] ? firstStore : secondStore).ReleaseReservationAsync(winner, TestContext.Current.CancellationToken);

        Guid assetId = Guid.NewGuid(), operationId = Guid.NewGuid();
        string spoolKey = $"spool/{assetId:N}.original";
        byte[] media = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        await using var context = new ShutterSpoolDbContext(fixture.Options);
        var quota = new EfSpoolQuotaStore(context, accounting, TimeProvider.System);
        SpoolQuotaSnapshot before = await quota.GetSnapshotAsync(TestContext.Current.CancellationToken);
        Assert.True(await quota.TryReserveAsync(new(operationId, assetId, 60, 0, [spoolKey], 60), TestContext.Current.CancellationToken));
        string physicalPath = Path.Combine(fixture.SpoolRoot, spoolKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
        await File.WriteAllBytesAsync(physicalPath, media, TestContext.Current.CancellationToken);
        string partialKey = $"incoming/{Guid.NewGuid():N}/{assetId:N}.partial";
        string partialPath = Path.Combine(fixture.SpoolRoot, partialKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, media, TestContext.Current.CancellationToken);
        Assert.True(await quota.UpdateReservationAsync(operationId, assetId, [partialKey, spoolKey], 60, media.Length * 2, 60,
            TestContext.Current.CancellationToken));
        await using (var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
        {
            context.Assets.Add(new AssetEntity
            {
                Id = assetId, SourceId = Guid.NewGuid(), OriginalName = "synthetic.raw", SpoolKey = spoolKey,
                Size = media.Length, Sha256 = new string('a', 64), State = "Spooled", IngestedAt = DateTimeOffset.UtcNow,
                RecoveryCorrelation = operationId,
            });
            context.Operations.Add(new IngestOperationEntity
            {
                Id = operationId, AssetId = assetId, SourceId = Guid.NewGuid(), OriginalName = "synthetic.raw",
                PartialKey = partialKey, FinalKey = spoolKey, State = "Committed", Size = media.Length,
                Sha256 = new string('a', 64),
            });
            await quota.CompleteReservationAsync(operationId, TestContext.Current.CancellationToken);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            await transaction.CommitAsync(TestContext.Current.CancellationToken);
        }

        SpoolQuotaSnapshot after = await quota.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.True(before.IsKnown);
        Assert.True(after.IsKnown);
        Assert.Equal(media.Length, after.ActiveBytes);
        Assert.Equal(media.Length, after.PartialBytes);
        Assert.Equal(media.Length * 2, after.UsedBytes);
        Assert.Equal(0, after.ReservedBytes);
        Assert.False(await quota.TryReserveAsync(new(Guid.NewGuid(), Guid.NewGuid(), 91, 0, [], 1), TestContext.Current.CancellationToken));
    }
}
