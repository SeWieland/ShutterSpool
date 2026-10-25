using ShutterSpool.Application.Preview;

namespace ShutterSpool.Core.Tests;

public sealed class PreviewAssetUseGateTests
{
    [Fact]
    public async Task Fence_stops_new_use_and_waits_for_generation_and_publication_to_finish()
    {
        var gate = new PreviewAssetUseGate();
        Guid assetId = Guid.NewGuid();
        IDisposable activeUse = gate.TryEnter(assetId)!;

        Task<IDisposable> pendingFence = gate.FenceAsync(assetId, TestContext.Current.CancellationToken);
        Assert.False(pendingFence.IsCompleted);
        Assert.Null(gate.TryEnter(assetId));

        activeUse.Dispose();
        using IDisposable fence = await pendingFence;
        Assert.Null(gate.TryEnter(assetId));

        fence.Dispose();
        using IDisposable resumedUse = Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(assetId));
    }

    [Fact]
    public async Task Cancelled_fence_reopens_the_asset_gate()
    {
        var gate = new PreviewAssetUseGate();
        Guid assetId = Guid.NewGuid();
        using IDisposable activeUse = gate.TryEnter(assetId)!;
        using var cancellation = new CancellationTokenSource();
        Task<IDisposable> pendingFence = gate.FenceAsync(assetId, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pendingFence);
        using IDisposable laterUse = Assert.IsAssignableFrom<IDisposable>(gate.TryEnter(assetId));
    }
}
