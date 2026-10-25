using Microsoft.Extensions.Configuration;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Application.Spool;
using ShutterSpool.Infrastructure.Spool;
using System.Globalization;

namespace ShutterSpool.IntegrationTests;

public sealed class FileSystemExactOriginalRemovalTests
{
    [Fact]
    public async Task Removes_only_authorized_original_and_frozen_transient_keys_and_retries_idempotently()
    {
        using var fixture = new RemovalFixture();
        Guid assetId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        string spoolKey = SpoolKey(assetId);
        string transientKey = $"incoming/{sourceId:N}/{assetId:N}.partial";
        fixture.Write(spoolKey, [1, 2, 3, 4]);
        fixture.Write(transientKey, [5, 6, 7]);
        string unrelated = fixture.Write($"spool/2026/10/{Guid.NewGuid():N}", [8, 9]);
        var removal = fixture.CreateAdapter();
        var request = fixture.Request(assetId, spoolKey, 4, [transientKey]);

        ExactOriginalRemovalResult result = await removal.RemoveAsync(request, TestContext.Current.CancellationToken);
        ExactOriginalRemovalResult retry = await removal.RemoveAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ExactOriginalRemovalStatus.Removed, result.Status);
        Assert.Equal(7, result.RemovedBytes);
        Assert.Equal(ExactOriginalRemovalStatus.AlreadyAbsent, retry.Status);
        Assert.Equal(0, retry.RemovedBytes);
        Assert.False(File.Exists(Path.Combine(fixture.Root, spoolKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(fixture.Root, transientKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".shutterspool-root.marker")));
    }

    [Fact]
    public async Task Wrong_marker_or_original_size_blocks_removal_without_touching_bytes()
    {
        using var fixture = new RemovalFixture();
        Guid assetId = Guid.NewGuid();
        string spoolKey = SpoolKey(assetId);
        string target = fixture.Write(spoolKey, [1, 2, 3, 4]);
        var removal = fixture.CreateAdapter();

        ExactOriginalRemovalResult markerMismatch = await removal.RemoveAsync(
            fixture.Request(assetId, spoolKey, 4, [], Guid.NewGuid().ToString("N")), TestContext.Current.CancellationToken);
        ExactOriginalRemovalResult sizeMismatch = await removal.RemoveAsync(
            fixture.Request(assetId, spoolKey, 5, []), TestContext.Current.CancellationToken);

        Assert.Equal(ExactOriginalRemovalStatus.Blocked, markerMismatch.Status);
        Assert.Equal("storage_marker_mismatch", markerMismatch.FailureCode);
        Assert.Equal(ExactOriginalRemovalStatus.Blocked, sizeMismatch.Status);
        Assert.Equal("original_size_mismatch", sizeMismatch.FailureCode);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_marker_is_not_reported_as_verified_absence()
    {
        using var fixture = new RemovalFixture();
        File.Delete(Path.Combine(fixture.Root, ".shutterspool-root.marker"));
        var removal = fixture.CreateAdapter();
        Guid assetId = Guid.NewGuid();

        ExactOriginalRemovalResult result = await removal.RemoveAsync(
            fixture.Request(assetId, SpoolKey(assetId), 4, []), TestContext.Current.CancellationToken);

        Assert.Equal(ExactOriginalRemovalStatus.Blocked, result.Status);
        Assert.Equal("storage_marker_unavailable", result.FailureCode);
    }

    [Fact]
    public async Task Invalid_transient_identity_is_preflighted_before_any_file_is_removed()
    {
        using var fixture = new RemovalFixture();
        Guid assetId = Guid.NewGuid();
        string spoolKey = SpoolKey(assetId);
        string target = fixture.Write(spoolKey, [1, 2, 3, 4]);
        string anotherAssetKey = $"incoming/{Guid.NewGuid():N}/{Guid.NewGuid():N}.partial";
        var removal = fixture.CreateAdapter();

        ExactOriginalRemovalResult result = await removal.RemoveAsync(
            fixture.Request(assetId, spoolKey, 4, [anotherAssetKey]), TestContext.Current.CancellationToken);

        Assert.Equal(ExactOriginalRemovalStatus.Blocked, result.Status);
        Assert.Equal("storage_key_asset_mismatch", result.FailureCode);
        Assert.True(File.Exists(target));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("")]
    public async Task Spool_write_keys_are_culture_invariant_and_removable(string cultureName)
    {
        using var fixture = new RemovalFixture();
        CultureInfo priorCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = cultureName.Length == 0
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(cultureName);
        try
        {
            var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = fixture.Root, ReservedBytes = 0 });
            var removal = fixture.CreateAdapter();
            Guid sourceId = Guid.NewGuid();
            Guid writtenAssetId = Guid.NewGuid();
            byte[] writtenBytes = [1, 2, 3];
            SpoolWrite written = await spool.WriteAsync(sourceId, writtenAssetId, new MemoryStream(writtenBytes),
                writtenBytes.Length, TestContext.Current.CancellationToken);
            Assert.Equal(4, written.FinalKey.Split('/').Length);

            ExactOriginalRemovalResult writtenResult = await removal.RemoveAsync(
                fixture.Request(writtenAssetId, written.FinalKey, written.Size, [written.PartialKey]),
                TestContext.Current.CancellationToken);

            Guid receivedAssetId = Guid.NewGuid();
            IAdmittedSpool admitted = spool;
            string receiveKey = admitted.GetReceiveKey(receivedAssetId);
            SpoolWrite received = await admitted.ReceiveAdmittedAsync(receiveKey, spool.GetPartialKey(sourceId, receivedAssetId),
                new MemoryStream([4, 5, 6, 7]), 4, TestContext.Current.CancellationToken);
            Assert.Equal(4, received.FinalKey.Split('/').Length);

            ExactOriginalRemovalResult receivedResult = await removal.RemoveAsync(
                fixture.Request(receivedAssetId, received.FinalKey, received.Size, [received.PartialKey]),
                TestContext.Current.CancellationToken);

            Assert.Equal(ExactOriginalRemovalStatus.Removed, writtenResult.Status);
            Assert.Equal(writtenBytes.Length * 2, writtenResult.RemovedBytes);
            Assert.Equal(ExactOriginalRemovalStatus.Removed, receivedResult.Status);
            Assert.Equal(4, receivedResult.RemovedBytes);
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    [Fact]
    public async Task Removes_legacy_dot_date_key_only_for_its_exact_asset_identity()
    {
        using var fixture = new RemovalFixture();
        Guid assetId = Guid.NewGuid();
        string legacyKey = $"spool/2026.10/{assetId:N}";
        string target = fixture.Write(legacyKey, [1, 2, 3]);
        var removal = fixture.CreateAdapter();

        ExactOriginalRemovalResult mismatch = await removal.RemoveAsync(
            fixture.Request(Guid.NewGuid(), legacyKey, 3, []), TestContext.Current.CancellationToken);
        Assert.Equal(ExactOriginalRemovalStatus.Blocked, mismatch.Status);
        Assert.Equal("storage_key_asset_mismatch", mismatch.FailureCode);
        Assert.True(File.Exists(target));

        ExactOriginalRemovalResult authorized = await removal.RemoveAsync(
            fixture.Request(assetId, legacyKey, 3, []), TestContext.Current.CancellationToken);

        Assert.Equal(ExactOriginalRemovalStatus.Removed, authorized.Status);
        Assert.Equal(3, authorized.RemovedBytes);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Linked_spool_parent_blocks_without_deleting_the_external_target()
    {
        using var fixture = new RemovalFixture();
        Guid assetId = Guid.NewGuid();
        string yearPath = Path.Combine(fixture.Root, "spool", "2026");
        string outside = Path.Combine(fixture.BasePath, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "10"));
        string outsideTarget = Path.Combine(outside, "10", assetId.ToString("N"));
        await File.WriteAllBytesAsync(outsideTarget, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(yearPath)!);
        try
        {
            Directory.CreateSymbolicLink(yearPath, outside);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            throw Xunit.Sdk.SkipException.ForSkip("This platform does not allow creating a synthetic directory link.");
        }

        try
        {
            ExactOriginalRemovalResult result = await fixture.CreateAdapter().RemoveAsync(
                fixture.Request(assetId, SpoolKey(assetId), 4, []), TestContext.Current.CancellationToken);

            Assert.Equal(ExactOriginalRemovalStatus.Blocked, result.Status);
            Assert.Equal("storage_path_crosses_link", result.FailureCode);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(outsideTarget, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(yearPath);
        }
    }

    private static string SpoolKey(Guid assetId) => $"spool/2026/10/{assetId:N}";

    private sealed class RemovalFixture : IDisposable
    {
        private const string Marker = "c4a21c0c949e441c8a77defb9446018b";
        public string BasePath { get; } = Path.Combine(Path.GetTempPath(), $"m205-removal-{Guid.NewGuid():N}");
        public string Root => Path.Combine(BasePath, "spool-root");

        public RemovalFixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, ".shutterspool-root.marker"), Marker);
        }

        public string Write(string key, byte[] bytes)
        {
            string path = Path.Combine(Root, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public FileSystemExactOriginalRemoval CreateAdapter() => new(Root);

        public ExactOriginalRemovalRequest Request(Guid assetId, string spoolKey, long size,
            IReadOnlyList<string> transients, string? marker = null) =>
            new(assetId, spoolKey, size, marker ?? Marker, transients);

        public void Dispose()
        {
            if (Directory.Exists(BasePath)) Directory.Delete(BasePath, recursive: true);
        }
    }
}
