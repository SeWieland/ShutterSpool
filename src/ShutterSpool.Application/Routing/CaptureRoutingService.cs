namespace ShutterSpool.Application.Routing;

public interface ICaptureRoutingStore
{
    Guid? LastChangedCaptureId => null;
    Task<bool> ProcessOneAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed class CaptureRoutingService(ICaptureRoutingStore store, TimeProvider clock)
{
    public Guid? LastChangedCaptureId => store.LastChangedCaptureId;
    public Task<bool> ProcessOneAsync(CancellationToken cancellationToken) => store.ProcessOneAsync(clock.GetUtcNow(), cancellationToken);
}
