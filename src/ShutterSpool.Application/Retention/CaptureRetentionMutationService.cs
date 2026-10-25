namespace ShutterSpool.Application.Retention;

public sealed class CaptureRetentionMutationService(ICaptureRetentionMutationStore store, TimeProvider clock)
{
    public Task<RetentionOperationResult> TrashAsync(Guid captureId, int expectedRevision, string reason,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || expectedRevision < 0 || reason is not ("manual" or "age" or "pressure"))
            throw new ArgumentException("The retention request is invalid.");
        return store.TrashAsync(captureId, expectedRevision, reason, clock.GetUtcNow(), cancellationToken);
    }

    public Task<RetentionOperationResult> RestoreAsync(Guid captureId, int expectedRevision,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || expectedRevision < 0)
            throw new ArgumentException("The retention request is invalid.");
        return store.RestoreAsync(captureId, expectedRevision, clock.GetUtcNow(), cancellationToken);
    }

    public Task<RetentionOperationResult> ResumeAsync(Guid captureId, int expectedRevision,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || expectedRevision < 0)
            throw new ArgumentException("The retention request is invalid.");
        return store.ResumeAsync(captureId, expectedRevision, cancellationToken);
    }

    public Task<RetentionOperationResult> RecordNoDeliveryIntentAsync(Guid captureId, int expectedRevision,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty || expectedRevision < 0)
            throw new ArgumentException("The retention request is invalid.");
        return store.RecordNoDeliveryIntentAsync(captureId, expectedRevision, clock.GetUtcNow(), cancellationToken);
    }
}
