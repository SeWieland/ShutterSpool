using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Security;
using ShutterSpool.Server.Security;
using System.Security.Claims;
using System.Threading.Channels;

namespace ShutterSpool.Server.Api.Live;

public sealed record CaptureChangedHint(Guid CaptureId, string Kind);
public sealed record PreviewReadyHint(Guid CaptureId, Guid AssetId, string Profile);
public sealed record SourceAuthenticationChangedHint(Guid SourceId, string Outcome, DateTimeOffset ObservedAt, long Version);
public sealed record UnassignedAuthenticationActivityHint(DateTimeOffset ObservedAt);
public sealed record SourceActivityChangedHint(string Epoch, Guid TransferId, Guid SourceId, Guid? AssetId, Guid? CaptureId,
    string Phase, DateTimeOffset ObservedAt, long Version, bool Accepted, bool PreviewReady);
public sealed record ResyncRequiredHint(string Epoch);
public sealed record PreviewPreparationObservation(DateTimeOffset ObservedAt, long Version);

[Authorize(Policy = SecurityExtensions.ManagementAccessPolicy)]
public sealed class LiveChangesHub(IServiceScopeFactory scopes, IOptions<SecurityOptions> security) : Hub
{
    private static readonly TimeSpan GrantCheckInterval = TimeSpan.FromSeconds(30);

    public override async Task OnConnectedAsync()
    {
        if (!IsSecureOrigin(Context.GetHttpContext(), security.Value.AllowedOrigins))
        {
            Context.Abort();
            return;
        }

        string? issuer = Context.User?.FindFirstValue("iss");
        string? subject = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
        CancellationToken disconnected = Context.ConnectionAborted;
        Action abort = Context.Abort;
        _ = MonitorGrantAsync(issuer, subject, disconnected, abort);
        await base.OnConnectedAsync();
    }

    private async Task MonitorGrantAsync(string? issuer, string? subject, CancellationToken disconnected, Action abort)
    {
        try
        {
            while (true)
            {
                await Task.Delay(GrantCheckInterval, disconnected);
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                if (!await scope.ServiceProvider.GetRequiredService<AccessGrantService>()
                    .IsAllowedAsync(issuer, subject, disconnected))
                {
                    abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { }
        catch (Exception) { abort(); }
    }

    private static bool IsSecureOrigin(HttpContext? context, string[] configuredOrigins)
    {
        if (context is null) return false;
        bool secure = context.Request.IsHttps || IsLoopbackHost(context.Request.Host.Host);
        string originHeader = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(originHeader))
            return secure && !context.WebSockets.IsWebSocketRequest;
        if (!Uri.TryCreate(originHeader, UriKind.Absolute, out Uri? origin)) return false;
        if (origin.AbsolutePath != "/" || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment) || !string.IsNullOrEmpty(origin.UserInfo)) return false;
        if (!secure || (origin.Scheme != "https" && !IsLoopbackHost(origin.Host))) return false;
        string normalizedOrigin = origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        string requestOrigin = $"{context.Request.Scheme}://{context.Request.Host}".TrimEnd('/');
        return string.Equals(normalizedOrigin, requestOrigin, StringComparison.OrdinalIgnoreCase)
            || configuredOrigins.Any(allowed => string.Equals(allowed.TrimEnd('/'), normalizedOrigin, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address);
}

public sealed class LiveChangeNotifier(IHubContext<LiveChangesHub> hub, ILogger<LiveChangeNotifier> logger) : BackgroundService,
    ICameraTransferActivityPublisher, IPreviewActivityPublisher
{
    private const int Capacity = 256;
    private static readonly TimeSpan ActiveObservationLifetime = TimeSpan.FromSeconds(60);
    private readonly Lock _activityLock = new();
    private readonly Dictionary<Guid, CameraTransferActivity> _receiving = [];
    private readonly Dictionary<(Guid CaptureId, Guid AssetId, PreviewProfile Profile), PreviewPreparationObservation> _preparingPreviews = [];
    private long _activitySequence;
    private int _activityCoverageComplete = 1;
    private readonly Channel<LiveChange> _pending = Channel.CreateBounded<LiveChange>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private int _resyncRequired;
    public string Epoch => _epoch;
    public bool ActivityCoverageComplete => Volatile.Read(ref _activityCoverageComplete) != 0;

    public void CaptureChanged(Guid captureId, string kind) => Publish($"capture:{captureId}", "captureChanged", new CaptureChangedHint(captureId, kind));
    public void RetentionChanged() => Publish("retention", "resyncRequired", new ResyncRequiredHint(_epoch));

    public void PreviewReady(Guid captureId, Guid assetId, string profile) =>
        Publish($"preview:{captureId}:{assetId}:{profile}", "previewReady", new PreviewReadyHint(captureId, assetId, profile));

    public void SourceAuthenticationChanged(Guid sourceId, string outcome, DateTimeOffset observedAt, long version) =>
        Publish($"source-auth:{sourceId}", "sourceAuthenticationChanged", new SourceAuthenticationChangedHint(sourceId, outcome, observedAt, version));

    public void UnassignedAuthenticationActivity(DateTimeOffset observedAt) =>
        Publish("unassigned-auth", "unassignedAuthenticationActivity", new UnassignedAuthenticationActivityHint(observedAt));

    public void Preparing(Guid captureId, Guid assetId, PreviewProfile profile, DateTimeOffset observedAt)
    {
        lock (_activityLock)
        {
            long version = ++_activitySequence;
            var identity = (captureId, assetId, profile);
            if (_preparingPreviews.ContainsKey(identity) || _preparingPreviews.Count < Capacity)
                _preparingPreviews[identity] = new(observedAt, version);
            else
            {
                Interlocked.Exchange(ref _activityCoverageComplete, 0);
                Interlocked.Exchange(ref _resyncRequired, 1);
                var oldest = _preparingPreviews.MinBy(pair => pair.Value.ObservedAt).Key;
                if (_preparingPreviews[oldest].ObservedAt < observedAt)
                {
                    _preparingPreviews.Remove(oldest);
                    _preparingPreviews[identity] = new(observedAt, version);
                }
            }
            Publish($"capture:{captureId}", "captureChanged",
                new CaptureChangedHint(captureId, "preview-preparing"));
        }
    }

    public void Finished(Guid captureId, Guid assetId, PreviewProfile profile)
    {
        lock (_activityLock)
        {
            if (!_preparingPreviews.Remove((captureId, assetId, profile))) return;
            Publish($"capture:{captureId}", "captureChanged",
                new CaptureChangedHint(captureId, "preview-finished"));
        }
    }

    public PreviewPreparationObservation? GetRecentPreviewPreparation(Guid captureId, Guid assetId,
        PreviewProfile profile, DateTimeOffset now)
    {
        lock (_activityLock)
        {
            var identity = (captureId, assetId, profile);
            if (!_preparingPreviews.TryGetValue(identity, out PreviewPreparationObservation? observation)) return null;
            if (observation.ObservedAt < now - ActiveObservationLifetime)
            {
                _preparingPreviews.Remove(identity);
                return null;
            }
            return observation;
        }
    }

    public void Publish(CameraTransferActivity activity)
    {
        lock (_activityLock)
        {
            activity = activity with { Version = ++_activitySequence };
            if (activity.Phase == CameraTransferPhase.Receiving)
            {
                if (_receiving.ContainsKey(activity.TransferId) || _receiving.Count < Capacity)
                    _receiving[activity.TransferId] = activity;
                else
                {
                    Interlocked.Exchange(ref _activityCoverageComplete, 0);
                    Interlocked.Exchange(ref _resyncRequired, 1);
                    Guid oldestId = _receiving.MinBy(pair => pair.Value.ObservedAt).Key;
                    if (_receiving[oldestId].ObservedAt < activity.ObservedAt)
                    {
                        _receiving.Remove(oldestId);
                        _receiving[activity.TransferId] = activity;
                    }
                }
            }
            else _receiving.Remove(activity.TransferId);
            Publish($"transfer:{activity.TransferId}", "sourceActivityChanged",
                new SourceActivityChangedHint(_epoch, activity.TransferId, activity.SourceId, activity.AssetId, activity.CaptureId,
                    activity.Phase.ToString(), activity.ObservedAt, activity.Version, activity.Accepted, activity.PreviewReady));
        }
    }

    public CameraTransferActivity? GetRecentReceiving(Guid transferId, DateTimeOffset now)
    {
        lock (_activityLock)
        {
            if (!_receiving.TryGetValue(transferId, out CameraTransferActivity? activity)) return null;
            if (activity.ObservedAt < now - ActiveObservationLifetime)
            {
                _receiving.Remove(transferId);
                return null;
            }
            return activity;
        }
    }

    private void Publish(string key, string eventName, object hint)
    {
        if (!_pending.Writer.TryWrite(new(key, eventName, hint))) Interlocked.Exchange(ref _resyncRequired, 1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await _pending.Reader.WaitToReadAsync(stoppingToken))
        {
            var latest = new Dictionary<string, LiveChange>(StringComparer.Ordinal);
            int remainingReads = Capacity;
            Drain(latest, ref remainingReads);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            Drain(latest, ref remainingReads);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await Parallel.ForEachAsync(latest.Values, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 16,
                    CancellationToken = timeout.Token,
                }, async (item, token) => await hub.Clients.All.SendAsync(item.EventName, item.Payload, token));
                if (Interlocked.Exchange(ref _resyncRequired, 0) != 0)
                    await hub.Clients.All.SendAsync("resyncRequired", new ResyncRequiredHint(_epoch), timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                Interlocked.Exchange(ref _resyncRequired, 1);
                logger.LogDebug("Live change hint could not be sent; clients reconcile authoritative state.");
                try
                {
                    using var resyncTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    resyncTimeout.CancelAfter(TimeSpan.FromSeconds(1));
                    await hub.Clients.All.SendAsync("resyncRequired", new ResyncRequiredHint(_epoch), resyncTimeout.Token);
                    Interlocked.Exchange(ref _resyncRequired, 0);
                }
                catch (Exception) { }
            }
        }
    }

    private void Drain(Dictionary<string, LiveChange> latest, ref int remainingReads)
    {
        while (remainingReads > 0 && _pending.Reader.TryRead(out LiveChange? item))
        {
            remainingReads--;
            latest[item.Key] = item;
        }
        if (remainingReads == 0 && _pending.Reader.TryPeek(out _))
            Interlocked.Exchange(ref _resyncRequired, 1);
    }

    private sealed record LiveChange(string Key, string EventName, object Payload);
}
