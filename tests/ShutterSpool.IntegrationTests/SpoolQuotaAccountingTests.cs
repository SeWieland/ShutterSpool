using ShutterSpool.Application.Retention;
using ShutterSpool.Infrastructure.Spool;

namespace ShutterSpool.IntegrationTests;

public sealed class SpoolQuotaAccountingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "shutterspool-quota-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Accounting_reports_exact_managed_files_and_stops_at_its_scan_bound()
    {
        Directory.CreateDirectory(Path.Combine(root, "spool"));
        Directory.CreateDirectory(Path.Combine(root, "incoming"));
        Directory.CreateDirectory(Path.Combine(root, "quarantine"));
        await File.WriteAllBytesAsync(Path.Combine(root, "spool", "original"), [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(root, "incoming", "partial"), [4, 5], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(root, "quarantine", "unknown"), [6], TestContext.Current.CancellationToken);
        string previews = Path.Combine(root, "previews");
        Directory.CreateDirectory(previews);
        await File.WriteAllBytesAsync(Path.Combine(previews, "derivative"), [7, 8, 9, 10], TestContext.Current.CancellationToken);
        string marker = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(Path.Combine(root, ".shutterspool-root.marker"), marker, TestContext.Current.CancellationToken);

        var accounting = new FileSystemSpoolAccounting(root, previews, null, null, TimeProvider.System, maximumFiles: 4);

        var inventory = await accounting.ObserveAsync(new HashSet<string>(), marker, false, TestContext.Current.CancellationToken);

        Assert.True(inventory.IsKnown);
        Assert.Equal(4, inventory.Files.Count);
        Assert.Equal(3, inventory.Files.Single(file => file.Category == SpoolQuotaFileCategory.Spool).SizeBytes);
        Assert.Equal(2, inventory.Files.Single(file => file.Category == SpoolQuotaFileCategory.Incoming).SizeBytes);
        Assert.Equal(1, inventory.Files.Single(file => file.Category == SpoolQuotaFileCategory.Quarantine).SizeBytes);
        Assert.Equal(4, inventory.Files.Single(file => file.Category == SpoolQuotaFileCategory.DiskPreview).SizeBytes);
    }

    [Fact]
    public async Task Accounting_fails_closed_when_the_bounded_scan_cannot_finish()
    {
        Directory.CreateDirectory(Path.Combine(root, "spool"));
        await File.WriteAllBytesAsync(Path.Combine(root, "spool", "one"), [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(root, "spool", "two"), [2], TestContext.Current.CancellationToken);

        var accounting = new FileSystemSpoolAccounting(root, Path.Combine(root, "previews"), null, null, TimeProvider.System, maximumFiles: 1);

        var inventory = await accounting.ObserveAsync(new HashSet<string>(), null, true, TestContext.Current.CancellationToken);

        Assert.False(inventory.IsKnown);
        Assert.Empty(inventory.Files);
    }

    [Fact]
    public async Task Accounting_does_not_treat_a_missing_spool_root_as_an_empty_volume()
    {
        var accounting = new FileSystemSpoolAccounting(root, Path.Combine(root, "previews"), 0, null, TimeProvider.System);

        var inventory = await accounting.ObserveAsync(new HashSet<string>(), null, true, TestContext.Current.CancellationToken);

        Assert.False(inventory.IsKnown);
        Assert.Null(inventory.DriveAvailableBytes);
    }

    [Fact]
    public async Task Accounting_binds_the_observation_to_the_original_spool_root_marker()
    {
        Directory.CreateDirectory(root);
        var accounting = new FileSystemSpoolAccounting(root, Path.Combine(root, "previews"), 0, null, TimeProvider.System);

        SpoolQuotaInventory initial = await accounting.ObserveAsync(new HashSet<string>(), null, true,
            TestContext.Current.CancellationToken);

        Assert.True(initial.IsKnown);
        Assert.Matches("^[0-9a-f]{32}$", initial.StorageMarker);
        Assert.True(await accounting.EstablishStorageMarkerAsync(initial.StorageMarker!, TestContext.Current.CancellationToken));
        File.Delete(Path.Combine(root, ".shutterspool-root.marker"));

        SpoolQuotaInventory lostMount = await accounting.ObserveAsync(new HashSet<string>(), initial.StorageMarker, false,
            TestContext.Current.CancellationToken);

        Assert.False(lostMount.IsKnown);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
