namespace ShutterSpool.Application.Preview;

public sealed class PreviewPrewarmService(IPreviewPrewarmStore store, PreviewService previews, PreviewSettings settings, TimeProvider clock)
{
    public (Guid CaptureId, Guid AssetId, PreviewProfile Profile)? LastReadyPreview { get; private set; }

    public async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        LastReadyPreview = null;
        PreviewPrewarmWork? work = await store.ClaimAsync(settings, clock.GetUtcNow(), cancellationToken);
        if (work is null) return false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.GenerationQueueTimeoutSeconds + settings.GenerationTimeoutSeconds + 60));
        PreviewWarmResult result;
        try
        {
            result = await previews.WarmWithPublicationAsync(work.CaptureId, work.AssetId, work.Profile, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            result = new(PreviewWarmOutcome.Retry);
        }

        await store.FinishAsync(work, result.Outcome, clock.GetUtcNow(), cancellationToken);
        if (result.Outcome == PreviewWarmOutcome.Ready && result.Published)
            LastReadyPreview = (work.CaptureId, work.AssetId, work.Profile);
        return true;
    }
}
