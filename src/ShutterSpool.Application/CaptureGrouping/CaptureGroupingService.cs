namespace ShutterSpool.Application.CaptureGrouping;

public interface ICaptureGroupingStore
{
    Guid? LastChangedCaptureId => null;
    Task<bool> GroupOneAsync(CancellationToken cancellationToken);
}

public sealed class CaptureGroupingService(ICaptureGroupingStore store)
{
    public Guid? LastChangedCaptureId => store.LastChangedCaptureId;
    public Task<bool> ProcessOneAsync(CancellationToken cancellationToken) => store.GroupOneAsync(cancellationToken);
}
