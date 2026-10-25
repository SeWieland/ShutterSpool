using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Spool;
using ShutterSpool.Domain.Delivery;
using System.Security.Cryptography;

namespace ShutterSpool.Core.Tests;

public sealed class DeliveryProcessingServiceTests
{
    [Fact]
    public void Duplicate_provider_kinds_fail_registry_creation()
    {
        Assert.Throws<InvalidOperationException>(() => new DeliveryAdapterRegistry([new StubAdapter(), new StubAdapter()]));
    }

    [Fact]
    public async Task Unsupported_destination_kind_records_configuration_failure()
    {
        var store = new ControlledWorkStore();
        var service = new DeliveryProcessingService(store, new UnusedOriginalReader(),
            new DeliveryAdapterRegistry([]), new DeliveryWorkerSettings(), TimeProvider.System);

        Assert.True(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DeliveryResultKind.ConfigurationFailure, store.Result?.Kind);
        Assert.Equal("DESTINATION_KIND_UNSUPPORTED", store.Result?.ErrorCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Verification_leaves_complete_original_bytes_for_transfer(bool seekable)
    {
        byte[] bytes = [1, 2, 3];
        var store = new ControlledWorkStore("local", bytes);
        var adapter = new RecordingAdapter();
        var service = new DeliveryProcessingService(store, new OriginalReader(bytes, seekable),
            new DeliveryAdapterRegistry([adapter]), new DeliveryWorkerSettings(), TimeProvider.System);

        Assert.True(await service.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal(bytes, adapter.Transferred);
        Assert.Equal(DeliveryResultKind.Confirmed, store.Result?.Kind);
    }

    [Fact]
    public async Task Running_hint_fires_after_claim_and_a_failed_hint_does_not_block_delivery()
    {
        byte[] bytes = [1, 2, 3];
        var store = new ControlledWorkStore("local", bytes);
        var adapter = new BlockingAdapter();
        var service = new DeliveryProcessingService(store, new OriginalReader(bytes, seekable: true),
            new DeliveryAdapterRegistry([adapter]), new DeliveryWorkerSettings(), TimeProvider.System);
        Guid? hintedCapture = null;

        Task<bool> processing = service.ProcessOneAsync(TestContext.Current.CancellationToken, captureId =>
        {
            Assert.True(store.IsRunning);
            hintedCapture = captureId;
            throw new InvalidOperationException("synthetic notifier failure");
        });

        await adapter.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(store.CaptureId, hintedCapture);
        Assert.True(store.IsRunning);
        adapter.Complete.TrySetResult();

        Assert.True(await processing);
        Assert.Equal(DeliveryResultKind.Confirmed, store.Result?.Kind);
    }

    private sealed class ControlledWorkStore(string kind = "unknown", byte[]? bytes = null) : IDeliveryWorkStore
    {
        private readonly byte[] _bytes = bytes ?? [];
        public DeliveryResult? Result { get; private set; }
        public Guid CaptureId { get; } = Guid.NewGuid();
        public bool IsRunning { get; private set; }
        public Task<ClaimedDelivery?> ClaimAsync(CancellationToken cancellationToken)
        {
            IsRunning = true;
            return Task.FromResult<ClaimedDelivery?>(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false,
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "target", _bytes.Length, "", "trace", kind,
                    CaptureId: CaptureId),
                new("spool-key", _bytes.Length, Convert.ToHexString(SHA256.HashData(_bytes)), "Spooled")));
        }
        public Task<bool> RenewAsync(Guid deliveryId, Guid claimToken, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TargetReservedAsync(DeliveryRequest request, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task FinishAsync(ClaimedDelivery work, DeliveryResult result, CancellationToken cancellationToken)
        {
            Result = result;
            return Task.CompletedTask;
        }
        public Task FinishReconciliationAsync(ClaimedDelivery work, DeliveryReconciliation result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedOriginalReader : ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class OriginalReader(byte[] bytes, bool seekable) : ICommittedOriginalReader
    {
        public Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(seekable ? new MemoryStream(bytes, writable: false) : new NonSeekableStream(bytes));
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    private sealed class RecordingAdapter : IDestinationDeliveryAdapter
    {
        public string Kind => "local";
        public byte[]? Transferred { get; private set; }
        public async Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken)
        {
            using var copy = new MemoryStream();
            await original.CopyToAsync(copy, cancellationToken);
            Transferred = copy.ToArray();
            return new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.ReadBackHash);
        }
        public Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingAdapter : IDestinationDeliveryAdapter
    {
        public string Kind => "local";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Complete { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Complete.Task.WaitAsync(cancellationToken);
            return new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.ReadBackHash);
        }
        public Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubAdapter : IDestinationDeliveryAdapter
    {
        public string Kind => "local";
        public Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
