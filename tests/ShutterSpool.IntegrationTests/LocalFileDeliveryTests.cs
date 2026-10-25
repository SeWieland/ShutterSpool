using ShutterSpool.Application.Delivery;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Destinations.Local;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ShutterSpool.IntegrationTests;

public sealed class LocalFileDeliveryTests
{
    [Fact]
    public void Browses_only_sorted_visible_directories_without_mutating_the_parent()
    {
        using var fixture = new LocalRoot();
        Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "zebra"));
        Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "alpha"));
        File.WriteAllText(System.IO.Path.Combine(fixture.Path, "photo.raw"), "synthetic");
        string[] before = Directory.GetFileSystemEntries(fixture.Path).Order().ToArray();

        LocalDirectoryListingResult result = LocalDirectoryBrowser.Browse(fixture.Path, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(["alpha", "zebra"], result.Listing!.Directories.Select(entry => entry.Name));
        Assert.Equal(before, Directory.GetFileSystemEntries(fixture.Path).Order());
        Assert.False(result.Listing.Truncated);
    }

    [Fact]
    public void Browsing_rejects_relative_and_missing_paths_with_readable_errors()
    {
        Assert.Equal("Enter an absolute directory path.", LocalDirectoryBrowser.Browse("relative", CancellationToken.None).Error);
        Assert.Equal("The directory does not exist or cannot be read.",
            LocalDirectoryBrowser.Browse(System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None).Error);
    }

    [Fact]
    public void Browsing_allows_the_filesystem_root_even_when_it_has_system_attributes()
    {
        string root = System.IO.Path.GetPathRoot(System.IO.Path.GetTempPath())!;

        LocalDirectoryListingResult result = LocalDirectoryBrowser.Browse(root, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(System.IO.Path.GetFullPath(root), result.Listing!.Path);
    }

    [Fact]
    public void Browsing_excludes_reparse_directories()
    {
        using var fixture = new LocalRoot();
        string target = System.IO.Path.Combine(fixture.Path, "real");
        Directory.CreateDirectory(target);
        try { Directory.CreateSymbolicLink(System.IO.Path.Combine(fixture.Path, "linked"), target); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { Assert.Skip("This host does not permit creating a directory symlink."); }

        LocalDirectoryListingResult result = LocalDirectoryBrowser.Browse(fixture.Path, CancellationToken.None);

        Assert.Equal(["real"], result.Listing!.Directories.Select(entry => entry.Name));
        Assert.NotNull(LocalDirectoryBrowser.Browse(System.IO.Path.Combine(fixture.Path, "linked"), CancellationToken.None).Error);
    }

    [Fact]
    public void Browsing_reports_truncation_after_two_hundred_directories()
    {
        using var fixture = new LocalRoot();
        for (int index = 0; index < 205; index++)
            Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, $"folder-{index:D3}"));

        LocalDirectoryListing listing = LocalDirectoryBrowser.Browse(fixture.Path, CancellationToken.None).Listing!;

        Assert.Equal(200, listing.Directories.Count);
        Assert.True(listing.Truncated);
    }

    [Fact]
    public async Task Writes_complete_original_and_confirms_by_read_back()
    {
        using var fixture = new LocalRoot();
        byte[] bytes = [1, 4, 9, 16, 25];
        DeliveryRequest request = Request(fixture, "nested/original.raw", bytes);

        DeliveryResult result = await LocalFileDelivery.WriteAsync(fixture.Path, request, new MemoryStream(bytes), TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryResultKind.Confirmed, result.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.ReadBackHash, result.Evidence);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(System.IO.Path.Combine(fixture.Path, "nested", "original.raw"), TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(System.IO.Path.Combine(fixture.Path, "nested"), "*.partial"));
    }

    [Fact]
    public async Task Identical_occupied_target_confirms_without_replacement_but_different_content_conflicts()
    {
        using var fixture = new LocalRoot();
        byte[] bytes = [4, 2, 8];
        string target = System.IO.Path.Combine(fixture.Path, "original.raw");
        await File.WriteAllBytesAsync(target, bytes, TestContext.Current.CancellationToken);
        DateTime before = File.GetLastWriteTimeUtc(target);

        DeliveryResult identical = await LocalFileDelivery.WriteAsync(fixture.Path, Request(fixture, "original.raw", bytes),
            new MemoryStream(bytes), TestContext.Current.CancellationToken);
        DeliveryResult conflict = await LocalFileDelivery.WriteAsync(fixture.Path, Request(fixture, "original.raw", [8, 2, 4]),
            new MemoryStream([8, 2, 4]), TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryResultKind.Confirmed, identical.Kind);
        Assert.Equal(DeliveryResultKind.ConfigurationFailure, conflict.Kind);
        Assert.Equal("TARGET_CONFLICT", conflict.ErrorCode);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Equal(before, File.GetLastWriteTimeUtc(target));
    }

    [Theory]
    [InlineData("../escape.raw")]
    [InlineData("/absolute.raw")]
    [InlineData("nested\\escape.raw")]
    [InlineData("nested/../escape.raw")]
    [InlineData("CON")]
    [InlineData("nested/aux.raw")]
    public async Task Rejects_unsafe_target_without_writing(string target)
    {
        using var fixture = new LocalRoot();
        byte[] bytes = [1];
        DeliveryResult result = await LocalFileDelivery.WriteAsync(fixture.Path, Request(fixture, target, bytes),
            new MemoryStream(bytes), TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Single(Directory.GetFiles(fixture.Path, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Reconciliation_of_missing_final_and_abandoned_attempt_file_is_safe_to_repeat()
    {
        using var fixture = new LocalRoot();
        byte[] bytes = [1, 3, 5];
        DeliveryRequest request = Request(fixture, "original.raw", bytes);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(fixture.Path, $".shutterspool-{request.AttemptId:N}.partial"), [1], TestContext.Current.CancellationToken);

        DeliveryReconciliation result = await LocalFileDelivery.ReconcileAsync(fixture.Path, request, TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryReconciliationKind.NotPublished, result.Kind);
        Assert.True(result.SafeToRepeat);
        Assert.Single(Directory.GetFiles(fixture.Path));
    }

    [Fact]
    public async Task Missing_root_marker_keeps_recovery_ambiguous()
    {
        using var fixture = new LocalRoot();
        DeliveryRequest request = Request(fixture, "original.raw", [1, 2]);
        File.Delete(System.IO.Path.Combine(fixture.Path, $".shutterspool-{request.DestinationId:N}.root"));

        DeliveryReconciliation result = await LocalFileDelivery.ReconcileAsync(fixture.Path, request, TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryReconciliationKind.Uncertain, result.Kind);
        Assert.False(result.SafeToRepeat);
    }

    [Fact]
    public async Task Reconciliation_conflicts_on_different_final_without_replacing_it()
    {
        using var fixture = new LocalRoot();
        DeliveryRequest request = Request(fixture, "original.raw", [1, 2, 3]);
        await File.WriteAllBytesAsync(System.IO.Path.Combine(fixture.Path, "original.raw"), [9, 8, 7], TestContext.Current.CancellationToken);

        DeliveryReconciliation result = await LocalFileDelivery.ReconcileAsync(fixture.Path, request, TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryReconciliationKind.Conflict, result.Kind);
        Assert.Equal("TARGET_CONFLICT", result.ErrorCode);
        Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(System.IO.Path.Combine(fixture.Path, "original.raw"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_target_creation_wins_without_being_replaced()
    {
        using var fixture = new LocalRoot();
        byte[] bytes = [1, 2, 3];
        DeliveryRequest request = Request(fixture, "original.raw", bytes);
        using var original = new GatedReadStream(bytes);
        Task<DeliveryResult> write = LocalFileDelivery.WriteAsync(fixture.Path, request, original, TestContext.Current.CancellationToken);
        await original.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        string target = System.IO.Path.Combine(fixture.Path, "original.raw");
        byte[] competing = [9, 8, 7];
        try
        {
            await File.WriteAllBytesAsync(target, competing, TestContext.Current.CancellationToken);
        }
        finally { original.Release(); }

        Assert.Equal(DeliveryResultKind.ConfigurationFailure, (await write).Kind);
        Assert.Equal(competing, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.Path, "*.partial"));
    }

    [Fact]
    public async Task Marker_loss_during_streaming_never_confirms_a_delivery()
    {
        using var fixture = new LocalRoot();
        byte[] bytes = [1, 2, 3];
        DeliveryRequest request = Request(fixture, "original.raw", bytes);
        using var original = new GatedReadStream(bytes);
        Task<DeliveryResult> write = LocalFileDelivery.WriteAsync(fixture.Path, request, original, TestContext.Current.CancellationToken);
        await original.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        File.Delete(System.IO.Path.Combine(fixture.Path, $".shutterspool-{request.DestinationId:N}.root"));
        original.Release();

        DeliveryResult result = await write;

        Assert.Equal(DeliveryResultKind.Uncertain, result.Kind);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "original.raw")));
    }

    [Fact]
    public async Task Unreadable_final_stays_ambiguous_during_reconciliation()
    {
        using var fixture = new LocalRoot();
        byte[] bytes = [1, 2, 3];
        DeliveryRequest request = Request(fixture, "original.raw", bytes);
        string target = System.IO.Path.Combine(fixture.Path, "original.raw");
        await File.WriteAllBytesAsync(target, bytes, TestContext.Current.CancellationToken);
        using var occupied = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        DeliveryReconciliation result = await LocalFileDelivery.ReconcileAsync(fixture.Path, request, TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryReconciliationKind.Uncertain, result.Kind);
        Assert.False(result.SafeToRepeat);
    }

    [Fact]
    public async Task Partial_source_failure_never_exposes_a_final_file()
    {
        using var fixture = new LocalRoot();
        DeliveryRequest request = Request(fixture, "original.raw", [1, 2, 3]);
        await using var original = new FailingReadStream();

        DeliveryResult result = await LocalFileDelivery.WriteAsync(fixture.Path, request, original, TestContext.Current.CancellationToken);

        Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "original.raw")));
        Assert.Single(Directory.GetFiles(fixture.Path));
    }

    [Fact]
    public async Task Full_destination_retries_without_exposing_a_final_file()
    {
        string? root = Environment.GetEnvironmentVariable("SHUTTERSPOOL_LOCAL_CAPACITY_TEST_ROOT");
        if (root is null) throw Xunit.Sdk.SkipException.ForSkip("Set SHUTTERSPOOL_LOCAL_CAPACITY_TEST_ROOT to a disposable filesystem smaller than 8 MiB.");
        if (!File.Exists(System.IO.Path.Combine(root, ".shutterspool-capacity-test")) || new DriveInfo(root).TotalSize > 8 * 1024 * 1024)
            throw Xunit.Sdk.SkipException.ForSkip("The capacity root must have a test sentinel and a total size no larger than 8 MiB.");
        using var fixture = new LocalRoot(root);
        DeliveryRequest request = Request(fixture, "original.raw", [1, 2, 3]);
        string filler = System.IO.Path.Combine(fixture.Path, "capacity-filler");
        bool full = false;
        try
        {
            try
            {
                await using var output = new FileStream(filler, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] block = new byte[64 * 1024];
                for (int index = 0; index < 128; index++) await output.WriteAsync(block, TestContext.Current.CancellationToken);
                await output.FlushAsync(TestContext.Current.CancellationToken);
            }
            catch (IOException exception) when (exception.HResult == 28
                || exception.HResult == unchecked((int)0x80070070)
                || exception.HResult == unchecked((int)0x80070027)) { full = true; }
            Assert.True(full, "The configured test filesystem did not fill within 8 MiB.");
            Assert.Equal(0, new DriveInfo(root).AvailableFreeSpace);

            DeliveryResult result = await LocalFileDelivery.WriteAsync(fixture.Path, request,
                new MemoryStream([1, 2, 3]), TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryResultKind.TransientFailure, result.Kind);
            Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "original.raw")));
        }
        finally { if (File.Exists(filler)) File.Delete(filler); }
    }

    [Fact]
    public async Task Permission_loss_blocks_delivery_without_a_final_file()
    {
        if (!OperatingSystem.IsLinux()) throw Xunit.Sdk.SkipException.ForSkip("This check uses Unix directory permissions.");
        using var fixture = new LocalRoot();
        DeliveryRequest request = Request(fixture, "original.raw", [1, 2, 3]);
        UnixFileMode originalMode = File.GetUnixFileMode(fixture.Path);
        try
        {
            File.SetUnixFileMode(fixture.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            string probe = System.IO.Path.Combine(fixture.Path, "permission-probe");
            try
            {
                await File.WriteAllBytesAsync(probe, [1], TestContext.Current.CancellationToken);
                File.Delete(probe);
                throw Xunit.Sdk.SkipException.ForSkip("This test process can bypass Unix directory permissions.");
            }
            catch (UnauthorizedAccessException) { }

            DeliveryResult result = await LocalFileDelivery.WriteAsync(fixture.Path, request,
                new MemoryStream([1, 2, 3]), TestContext.Current.CancellationToken);

            Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
            Assert.Equal("PERMISSION_DENIED", result.ErrorCode);
            Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, "original.raw")));
        }
        finally { File.SetUnixFileMode(fixture.Path, originalMode); }
    }

    [Fact]
    public async Task Symlinked_directory_is_rejected_before_writing()
    {
        using var fixture = new LocalRoot();
        string outside = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shutterspool-outside-{Guid.NewGuid():N}");
        string link = System.IO.Path.Combine(fixture.Path, "linked");
        Directory.CreateDirectory(outside);
        try
        {
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                if (!OperatingSystem.IsWindows()) throw Xunit.Sdk.SkipException.ForSkip("This host cannot create a test directory symlink.");
                using var junction = Process.Start(new ProcessStartInfo("cmd.exe")
                {
                    ArgumentList = { "/c", "mklink", "/J", link, outside },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (junction is null) throw Xunit.Sdk.SkipException.ForSkip("This host cannot create a test junction.");
                await junction.WaitForExitAsync(TestContext.Current.CancellationToken);
                if (junction.ExitCode != 0) throw Xunit.Sdk.SkipException.ForSkip("This host cannot create a test junction.");
            }
            DeliveryRequest request = Request(fixture, "linked/original.raw", [1]);
            Assert.Equal("UNSAFE_PATH", LocalFileDelivery.TestRoot(link, request.DestinationId).ErrorCode);
            DeliveryResult result = await LocalFileDelivery.WriteAsync(fixture.Path, request, new MemoryStream([1]), TestContext.Current.CancellationToken);
            Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
            Assert.Empty(Directory.GetFiles(outside));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static DeliveryRequest Request(LocalRoot fixture, string target, byte[] bytes)
    {
        Guid destinationId = Guid.NewGuid();
        LocalFileDelivery.ProvisionRoot(fixture.Path, destinationId);
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), destinationId, target, bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "test");
    }

    private sealed class LocalRoot : IDisposable
    {
        public string Path { get; }
        public LocalRoot(string? root = null)
        {
            Path = System.IO.Path.Combine(root ?? Environment.GetEnvironmentVariable("SHUTTERSPOOL_LOCAL_MOUNT_TEST_ROOT")
                ?? System.IO.Path.GetTempPath(), $"shutterspool-local-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class FailingReadStream : MemoryStream
    {
        private int reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (reads++ > 0) throw new IOException("Controlled source read failure.");
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }
    }

    private sealed class GatedReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => gate.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
