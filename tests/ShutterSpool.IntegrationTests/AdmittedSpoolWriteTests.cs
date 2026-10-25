using ShutterSpool.Application.Ingest;
using ShutterSpool.Domain.Assets;
using ShutterSpool.Infrastructure.Spool;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShutterSpool.IntegrationTests;

public sealed class AdmittedSpoolWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shutterspool-admitted-spool-tests", Guid.NewGuid().ToString("N"));
    public AdmittedSpoolWriteTests(ITestOutputHelper output) => this.output = output;

    private readonly ITestOutputHelper output;

    [Fact]
    public async Task Admitted_receive_continues_past_the_reserve_and_propagates_source_read_failures()
    {
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = _root, ReservedBytes = long.MaxValue });
        Guid sourceId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        IAdmittedSpool admitted = spool;

        await Assert.ThrowsAsync<IOException>(() => spool.WriteAsync(sourceId, assetId, new MemoryStream([7]), 3,
            TestContext.Current.CancellationToken));

        string receiveKey = admitted.GetReceiveKey(assetId);
        string partialKey = spool.GetPartialKey(sourceId, assetId);
        SpoolWrite receipt = await admitted.ReceiveAdmittedAsync(receiveKey, partialKey, new MemoryStream([1, 3, 5]), 3,
            TestContext.Current.CancellationToken);
        Assert.Equal(3, receipt.Size);
        Assert.Equal(new byte[] { 1, 3, 5 }, await File.ReadAllBytesAsync(Path.Combine(_root, receiveKey.Replace('/', Path.DirectorySeparatorChar)),
            TestContext.Current.CancellationToken));

        Guid failedAssetId = Guid.NewGuid();
        string failedReceiveKey = admitted.GetReceiveKey(failedAssetId);
        await Assert.ThrowsAsync<IOException>(() => admitted.ReceiveAdmittedAsync(failedReceiveKey,
            spool.GetPartialKey(sourceId, failedAssetId), new FailingStream(), 3, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(_root, failedReceiveKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Direct_admitted_receive_retains_incomplete_bytes_after_size_and_source_read_failures()
    {
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = _root, ReservedBytes = 0, BufferSize = 8 });
        var journal = new MemoryJournal();
        var ingest = new IngestService(journal, spool, TimeProvider.System);
        Guid sourceId = Guid.NewGuid();
        Guid oversizedOperation = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(() => ingest.ReceiveAdmittedAsync(new(sourceId, "oversized.raw",
            new ChunkedStream([1, 2, 3, 4], 2), 3, oversizedOperation), TestContext.Current.CancellationToken));

        IngestOperation oversized = (await journal.FindOperationAsync(oversizedOperation, TestContext.Current.CancellationToken))!;
        Assert.Equal(IngestOperationState.AwaitingSource, oversized.State);
        string oversizedPath = Path.Combine(_root, oversized.FinalKey!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(oversizedPath, TestContext.Current.CancellationToken));

        Guid failedReadOperation = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => ingest.ReceiveAdmittedAsync(new(sourceId, "failed-read.raw", new FailingStream(),
            3, failedReadOperation), TestContext.Current.CancellationToken));

        IngestOperation failedRead = (await journal.FindOperationAsync(failedReadOperation, TestContext.Current.CancellationToken))!;
        Assert.Equal(IngestOperationState.AwaitingSource, failedRead.State);
        string failedReadPath = Path.Combine(_root, failedRead.FinalKey!.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(failedReadPath));
        Assert.Empty(journal.Committed);
    }

    [Theory]
    [InlineData(ReceiveFailure.LaterWrite, 4)]
    [InlineData(ReceiveFailure.DurableFlush, 8)]
    public async Task Receive_output_failure_keeps_candidate_private_without_recording_a_receipt(ReceiveFailure failure, int retainedLength)
    {
        InjectedReceiveFileStream? output = null;
        var spool = new FileSystemSpool(new FileSystemSpoolOptions
        {
            RootPath = _root,
            ReservedBytes = 0,
            BufferSize = 4,
            ReceiveOutputFactory = path => output = new(path, failure),
        });
        var journal = new MemoryJournal();
        var ingest = new IngestService(journal, spool, TimeProvider.System);
        Guid operationId = Guid.NewGuid();
        byte[] bytes = [1, 2, 3, 4, 5, 6, 7, 8];

        await Assert.ThrowsAsync<IOException>(() => ingest.ReceiveAdmittedAsync(new(Guid.NewGuid(), "camera.raw",
            new MemoryStream(bytes), bytes.Length, operationId), TestContext.Current.CancellationToken));

        IngestOperation operation = (await journal.FindOperationAsync(operationId, TestContext.Current.CancellationToken))!;
        Assert.Equal(IngestOperationState.AwaitingSource, operation.State);
        Assert.Null(operation.Size);
        Assert.Null(operation.Sha256);
        Assert.Empty(journal.Committed);
        Assert.NotNull(output);
        Assert.True(output!.DurableFlushAttempted == (failure == ReceiveFailure.DurableFlush));
        string candidate = Path.Combine(_root, operation.FinalKey!.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(bytes[..retainedLength], await File.ReadAllBytesAsync(candidate, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Admitted_receive_writes_one_payload_and_finalization_does_not_read_or_copy_it()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Windows process I/O counters are required for this isolated observation.");
        if (Environment.GetEnvironmentVariable("SHUTTERSPOOL_RUN_CAMERA_IO_OBSERVATION") != "true")
            throw Xunit.Sdk.SkipException.ForSkip("Set SHUTTERSPOOL_RUN_CAMERA_IO_OBSERVATION=true and run this method alone.");

        Guid sourceId = Guid.NewGuid();
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = _root, ReservedBytes = 0 });
        var journal = new MemoryJournal();
        var ingest = new IngestService(journal, spool, TimeProvider.System);
        var allocatedBySize = new Dictionary<int, long>();
        long readDuring32MiBReceive = 0;
        long writtenDuring32MiBReceive = 0;
        long readDuring32MiBFinalize = 0;
        long writtenDuring32MiBFinalize = 0;

        foreach (int payloadSize in new[] { 1, 32, 60 }.Select(size => size * 1024 * 1024))
        {
            Guid operationId = Guid.NewGuid();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            IoCounters? beforeReceive = payloadSize == 32 * 1024 * 1024 ? ReadIoCounters() : null;

            long receivedSize = await ingest.ReceiveAdmittedAsync(new(sourceId, "camera.raw", new RepeatedByteStream(payloadSize),
                payloadSize, operationId), TestContext.Current.CancellationToken);
            allocatedBySize.Add(payloadSize, GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);

            IoCounters? afterReceive = payloadSize == 32 * 1024 * 1024 ? ReadIoCounters() : null;
            if (beforeReceive is IoCounters receiveBefore && afterReceive is IoCounters receiveAfter)
            {
                readDuring32MiBReceive = checked((long)(receiveAfter.ReadTransferBytes - receiveBefore.ReadTransferBytes));
                writtenDuring32MiBReceive = checked((long)(receiveAfter.WriteTransferBytes - receiveBefore.WriteTransferBytes));
            }
            Assert.Equal(payloadSize, receivedSize);

            IoCounters? beforeFinalize = payloadSize == 32 * 1024 * 1024 ? ReadIoCounters() : null;
            IngestResult result = await ingest.FinalizeAdmittedAsync(operationId, sourceId, "camera.raw", payloadSize,
                TestContext.Current.CancellationToken);
            IoCounters? afterFinalize = payloadSize == 32 * 1024 * 1024 ? ReadIoCounters() : null;
            if (beforeFinalize is IoCounters finalizeBefore && afterFinalize is IoCounters finalizeAfter)
            {
                readDuring32MiBFinalize = checked((long)(finalizeAfter.ReadTransferBytes - finalizeBefore.ReadTransferBytes));
                writtenDuring32MiBFinalize = checked((long)(finalizeAfter.WriteTransferBytes - finalizeBefore.WriteTransferBytes));
            }
            Assert.Equal(payloadSize, result.Asset.Size);
        }

        Assert.InRange(writtenDuring32MiBReceive, 32 * 1024 * 1024, 33 * 1024 * 1024);
        Assert.True(readDuring32MiBReceive < 8 * 1024 * 1024, $"Receive read {readDuring32MiBReceive} bytes from the spool.");
        Assert.True(readDuring32MiBFinalize < 8 * 1024 * 1024, "Finalization read the entire original payload.");
        Assert.True(writtenDuring32MiBFinalize < 8 * 1024 * 1024, "Finalization copied the original payload.");
        long allocationSpread = allocatedBySize.Values.Max() - allocatedBySize.Values.Min();
        Assert.True(allocationSpread < 2 * 1024 * 1024,
            $"Receive allocations varied by {allocationSpread} bytes across 1/32/60 MiB payloads.");
        output.WriteLine("Windows process I/O deltas for 32 MiB synthetic payload: receive read={0}, write={1}; finalize read={2}, write={3} bytes.",
            readDuring32MiBReceive, writtenDuring32MiBReceive, readDuring32MiBFinalize, writtenDuring32MiBFinalize);
        output.WriteLine("Managed receive allocations: 1 MiB={0}, 32 MiB={1}, 60 MiB={2} bytes.",
            allocatedBySize[1024 * 1024], allocatedBySize[32 * 1024 * 1024], allocatedBySize[60 * 1024 * 1024]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Injected source read failure."));
    }

    public enum ReceiveFailure { LaterWrite, DurableFlush }

    private sealed class InjectedReceiveFileStream(string path, ReceiveFailure failure) : FileStream(path, FileMode.CreateNew,
        FileAccess.Write, FileShare.None, 4, FileOptions.Asynchronous | FileOptions.WriteThrough)
    {
        private int successfulWrites;
        public bool DurableFlushAttempted { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (failure == ReceiveFailure.LaterWrite && successfulWrites == 1)
                throw new IOException("Injected failure on a later write to the open receive file.");
            successfulWrites++;
            return base.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush(bool flushToDisk)
        {
            if (flushToDisk)
            {
                DurableFlushAttempted = true;
                if (failure == ReceiveFailure.DurableFlush)
                    throw new IOException("Injected failure at the durable receive flush boundary.");
            }
            base.Flush(flushToDisk);
        }
    }

    private sealed class RepeatedByteStream(int remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int length = Math.Min(remaining, buffer.Length);
            buffer.Span[..length].Fill(0xA5);
            remaining -= length;
            return ValueTask.FromResult(length);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ChunkedStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }

    private sealed class MemoryJournal : IIngestJournal
    {
        private readonly Dictionary<Guid, IngestOperation> operations = [];
        public Dictionary<Guid, IngestResult> Committed { get; } = [];

        public Task<IngestResult?> FindCommittedResultAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Committed.GetValueOrDefault(operationId));

        public Task<IngestOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(operations.GetValueOrDefault(operationId));

        public Task<OriginalRemovalRecovery> GetOriginalRemovalRecoveryAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(new OriginalRemovalRecovery(false, []));

        public Task CreateAsync(IngestOperation operation, CancellationToken cancellationToken)
        {
            operations.Add(operation.Id, operation);
            return Task.CompletedTask;
        }

        public Task MarkPromotedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken)
        {
            operations[operationId] = operations[operationId] with { State = IngestOperationState.Promoted, FinalKey = write.FinalKey, Size = write.Size, Sha256 = write.Sha256 };
            return Task.CompletedTask;
        }

        public Task MarkReceivedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken)
        {
            operations[operationId] = operations[operationId] with { State = IngestOperationState.Received, FinalKey = write.FinalKey, Size = write.Size, Sha256 = write.Sha256 };
            return Task.CompletedTask;
        }

        public Task CommitAsync(Guid operationId, DateTimeOffset ingestedAt, CancellationToken cancellationToken)
        {
            IngestOperation operation = operations[operationId];
            operations[operationId] = operation with { State = IngestOperationState.Committed };
            Committed[operationId] = new(new Asset(operation.AssetId, operation.SourceId, operation.OriginalName,
                operation.FinalKey!, operation.Size!.Value, operation.Sha256!, ingestedAt, operation.Id));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IngestOperation>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IngestOperation>>([.. operations.Values]);

        public Task QuarantineAsync(Guid operationId, string reason, CancellationToken cancellationToken)
        {
            operations[operationId] = operations[operationId] with { State = IngestOperationState.Quarantined, QuarantineReason = reason };
            return Task.CompletedTask;
        }
    }

    private static IoCounters ReadIoCounters()
    {
        using Process process = Process.GetCurrentProcess();
        if (!GetProcessIoCounters(process.Handle, out IoCounters counters))
            throw new InvalidOperationException("Windows process I/O counters are unavailable.");
        return counters;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferBytes;
        public ulong WriteTransferBytes;
        public ulong OtherTransferBytes;
    }
}
