using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using ShutterSpool.Application.Ingress;
using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Security;
using ShutterSpool.Server.Security;
using System.Security.Claims;
using System.Threading.Channels;
using ShutterSpool.Application.Workspaces;

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
public sealed class LiveChangesHub(IServiceScopeFactory scopes, IOptions<SecurityOptions> security, WorkspaceContext workspace) : Hub
{
    private static readonly TimeSpan GrantCheckInterval = TimeSpan.FromSeconds(30);

    public override async Task OnConnectedAsync()
    {
        if (workspace.Enabled)
        {
            if (Context.GetHttpContext()?.Items[typeof(WorkspaceDefinition)] is not WorkspaceDefinition selectedWorkspace)
            {
                Context.Abort();
                return;
            }
            workspace.Bind(selectedWorkspace);
        }
        if (!IsSecureOrigin(Context.GetHttpContext(), security.Value.AllowedOrigins))
        {
            Context.Abort();
            return;
        }

        string? issuer = Context.User?.FindFirstValue("iss");
        string? subject = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? Context.User?.FindFirstValue("sub");
        CancellationToken disconnected = Context.ConnectionAborted;
        Action abort = Context.Abort;
        WorkspaceDefinition? selected = workspace.Selected;
        WorkspaceMembership initial;
        WorkspaceViewerPolicy initialPolicy;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using AsyncServiceScope scope = scopes.CreateAsyncScope();
            if (selected is not null) scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(selected);
            IAccessGrantStore grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
            WorkspaceMembership? member = string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject)
                ? null : await grants.GetAsync(issuer, subject, timeout.Token);
            if (member?.IsActive != true) { abort(); return; }
            initial = member;
            initialPolicy = await grants.GetViewerPolicyAsync(timeout.Token);
        }
        catch (Exception) { abort(); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, initial.Role is WorkspaceRole.Owner or WorkspaceRole.Administrator
            ? LiveChangeNotifier.Group(workspace.Id ?? Guid.Empty) : LiveChangeNotifier.MemberGroup(workspace.Id ?? Guid.Empty), disconnected);
        _ = MonitorGrantAsync(issuer, subject, selected, initial, initialPolicy, disconnected, abort);
        await base.OnConnectedAsync();
    }

    private async Task MonitorGrantAsync(string? issuer, string? subject, WorkspaceDefinition? selected,
        WorkspaceMembership initial, WorkspaceViewerPolicy initialPolicy, CancellationToken disconnected, Action abort)
    {
        try
        {
            while (true)
            {
                await Task.Delay(GrantCheckInterval, disconnected);
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(disconnected);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                if (selected is not null)
                {
                    WorkspaceDefinition? current = await scope.ServiceProvider.GetRequiredService<IWorkspaceRegistry>().FindReadyAsync(selected.Id, timeout.Token);
                    if (current is null || current != selected) { abort(); return; }
                    scope.ServiceProvider.GetRequiredService<WorkspaceContext>().Bind(selected);
                }
                IAccessGrantStore grants = scope.ServiceProvider.GetRequiredService<IAccessGrantStore>();
                WorkspaceMembership? currentMember = await grants.GetAsync(issuer!, subject!, timeout.Token);
                WorkspaceViewerPolicy currentPolicy = await grants.GetViewerPolicyAsync(timeout.Token);
                if (currentMember?.IsActive != true || currentMember.Role != initial.Role || currentMember.Revision != initial.Revision
                    || currentPolicy != initialPolicy)
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

public sealed class LiveChangeBuffer(IHubContext<LiveChangesHub> hub, ILogger<LiveChangeNotifier> logger) : BackgroundService
{
    private const int Capacity = 256;
    private static readonly TimeSpan ActiveObservationLifetime = TimeSpan.FromSeconds(60);
    private readonly Lock _activityLock = new();
    private readonly Dictionary<(Guid Workspace, Guid Transfer), CameraTransferActivity> _receiving = [];
    private readonly Dictionary<(Guid Workspace, Guid CaptureId, Guid AssetId, PreviewProfile Profile), PreviewPreparationObservation> _preparingPreviews = [];
    private readonly Dictionary<Guid, long> activitySequences = [];
    private readonly HashSet<Guid> incompleteWorkspaces = [];
    private readonly Channel<LiveChange> _pending = Channel.CreateBounded<LiveChange>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly string _epoch = Guid.NewGuid().ToString("N");
    private int _resyncRequired;
    public string Epoch => _epoch;
    public bool ActivityCoverageComplete => IsActivityCoverageComplete(Guid.Empty);
    public bool IsActivityCoverageComplete(Guid workspace) { lock (_activityLock) return !incompleteWorkspaces.Contains(workspace); }

    public void CaptureChanged(Guid workspace, Guid captureId, string kind) => Publish(workspace, $"capture:{captureId}", "captureChanged", new CaptureChangedHint(captureId, kind));
    public void RetentionChanged(Guid workspace) => Publish(workspace, "retention", "resyncRequired", new ResyncRequiredHint(_epoch));

    public void PreviewReady(Guid workspace, Guid captureId, Guid assetId, string profile) =>
        Publish(workspace, $"preview:{captureId}:{assetId}:{profile}", "previewReady", new PreviewReadyHint(captureId, assetId, profile));

    public void SourceAuthenticationChanged(Guid workspace, Guid sourceId, string outcome, DateTimeOffset observedAt, long version) =>
        Publish(workspace, $"source-auth:{sourceId}", "sourceAuthenticationChanged", new SourceAuthenticationChangedHint(sourceId, outcome, observedAt, version));

    public void UnassignedAuthenticationActivity(Guid workspace, DateTimeOffset observedAt) =>
        Publish(workspace, "unassigned-auth", "unassignedAuthenticationActivity", new UnassignedAuthenticationActivityHint(observedAt));

    public void Preparing(Guid workspace, Guid captureId, Guid assetId, PreviewProfile profile, DateTimeOffset observedAt)
    {
        lock (_activityLock)
        {
            long version = activitySequences[workspace] = activitySequences.GetValueOrDefault(workspace) + 1;
            var identity = (workspace, captureId, assetId, profile);
            if (_preparingPreviews.ContainsKey(identity) || _preparingPreviews.Count(pair => pair.Key.Workspace == workspace) < Capacity)
                _preparingPreviews[identity] = new(observedAt, version);
            else
            {
                incompleteWorkspaces.Add(workspace);
                Interlocked.Exchange(ref _resyncRequired, 1);
                var oldest = _preparingPreviews.Where(pair => pair.Key.Workspace == workspace).MinBy(pair => pair.Value.ObservedAt).Key;
                if (_preparingPreviews[oldest].ObservedAt < observedAt)
                {
                    _preparingPreviews.Remove(oldest);
                    _preparingPreviews[identity] = new(observedAt, version);
                }
            }
            Publish(workspace, $"capture:{captureId}", "captureChanged",
                new CaptureChangedHint(captureId, "preview-preparing"));
        }
    }

    public void Finished(Guid workspace, Guid captureId, Guid assetId, PreviewProfile profile)
    {
        lock (_activityLock)
        {
            if (!_preparingPreviews.Remove((workspace, captureId, assetId, profile))) return;
            Publish(workspace, $"capture:{captureId}", "captureChanged",
                new CaptureChangedHint(captureId, "preview-finished"));
        }
    }

    public PreviewPreparationObservation? GetRecentPreviewPreparation(Guid workspace, Guid captureId, Guid assetId,
        PreviewProfile profile, DateTimeOffset now)
    {
        lock (_activityLock)
        {
            var identity = (workspace, captureId, assetId, profile);
            if (!_preparingPreviews.TryGetValue(identity, out PreviewPreparationObservation? observation)) return null;
            if (observation.ObservedAt < now - ActiveObservationLifetime)
            {
                _preparingPreviews.Remove(identity);
                return null;
            }
            return observation;
        }
    }

    public void Publish(Guid workspace, CameraTransferActivity activity)
    {
        lock (_activityLock)
        {
            activity = activity with { Version = activitySequences[workspace] = activitySequences.GetValueOrDefault(workspace) + 1 };
            if (activity.Phase == CameraTransferPhase.Receiving)
            {
                if (_receiving.ContainsKey((workspace, activity.TransferId)) || _receiving.Count(pair => pair.Key.Workspace == workspace) < Capacity)
                    _receiving[(workspace, activity.TransferId)] = activity;
                else
                {
                    incompleteWorkspaces.Add(workspace);
                    Interlocked.Exchange(ref _resyncRequired, 1);
                    var oldestId = _receiving.Where(pair => pair.Key.Workspace == workspace).MinBy(pair => pair.Value.ObservedAt).Key;
                    if (_receiving[oldestId].ObservedAt < activity.ObservedAt)
                    {
                        _receiving.Remove(oldestId);
                        _receiving[(workspace, activity.TransferId)] = activity;
                    }
                }
            }
            else _receiving.Remove((workspace, activity.TransferId));
            Publish(workspace, $"transfer:{activity.TransferId}", "sourceActivityChanged",
                new SourceActivityChangedHint(_epoch, activity.TransferId, activity.SourceId, activity.AssetId, activity.CaptureId,
                    activity.Phase.ToString(), activity.ObservedAt, activity.Version, activity.Accepted, activity.PreviewReady));
        }
    }

    public CameraTransferActivity? GetRecentReceiving(Guid workspace, Guid transferId, DateTimeOffset now)
    {
        lock (_activityLock)
        {
            if (!_receiving.TryGetValue((workspace, transferId), out CameraTransferActivity? activity)) return null;
            if (activity.ObservedAt < now - ActiveObservationLifetime)
            {
                _receiving.Remove((workspace, transferId));
                return null;
            }
            return activity;
        }
    }

    private void Publish(Guid workspace, string key, string eventName, object hint)
    {
        if (!_pending.Writer.TryWrite(new(workspace, $"{workspace:N}:{key}", eventName, hint))) Interlocked.Exchange(ref _resyncRequired, 1);
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
                }, async (item, token) =>
                {
                    await hub.Clients.Group(LiveChangeNotifier.Group(item.Workspace)).SendAsync(item.EventName, item.Payload, token);
                    await hub.Clients.Group(LiveChangeNotifier.MemberGroup(item.Workspace)).SendAsync("resyncRequired", new ResyncRequiredHint(_epoch), token);
                });
                if (Interlocked.Exchange(ref _resyncRequired, 0) != 0)
                    foreach (Guid workspace in latest.Values.Select(item => item.Workspace).Distinct())
                        await hub.Clients.Group(LiveChangeNotifier.Group(workspace)).SendAsync("resyncRequired", new ResyncRequiredHint(_epoch), timeout.Token);
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
                    foreach (Guid workspace in latest.Values.Select(item => item.Workspace).Distinct())
                        await hub.Clients.Group(LiveChangeNotifier.Group(workspace)).SendAsync("resyncRequired", new ResyncRequiredHint(_epoch), resyncTimeout.Token);
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

    private sealed record LiveChange(Guid Workspace, string Key, string EventName, object Payload);
}

public sealed class LiveChangeNotifier : ICameraTransferActivityPublisher, IPreviewActivityPublisher
{
    private readonly LiveChangeBuffer buffer;
    private readonly WorkspaceContext workspace;
    public LiveChangeNotifier(LiveChangeBuffer buffer, WorkspaceContext workspace)
    { this.buffer = buffer; this.workspace = workspace; }
    public LiveChangeNotifier(IHubContext<LiveChangesHub> hub, ILogger<LiveChangeNotifier> logger)
        : this(new LiveChangeBuffer(hub, logger), new WorkspaceContext(false)) { }
    private Guid Id => workspace.Id ?? Guid.Empty;
    public static string Group(Guid id) => $"workspace:{id:N}";
    public static string MemberGroup(Guid id) => $"workspace-members:{id:N}";
    public string Epoch => buffer.Epoch;
    public bool ActivityCoverageComplete => buffer.IsActivityCoverageComplete(Id);
    public void CaptureChanged(Guid id, string kind) => buffer.CaptureChanged(Id, id, kind);
    public void RetentionChanged() => buffer.RetentionChanged(Id);
    public void PreviewReady(Guid capture, Guid asset, string profile) => buffer.PreviewReady(Id, capture, asset, profile);
    public void SourceAuthenticationChanged(Guid source, string outcome, DateTimeOffset observed, long version) =>
        buffer.SourceAuthenticationChanged(Id, source, outcome, observed, version);
    public void UnassignedAuthenticationActivity(DateTimeOffset observed) => buffer.UnassignedAuthenticationActivity(Id, observed);
    public void Preparing(Guid capture, Guid asset, PreviewProfile profile, DateTimeOffset observed) => buffer.Preparing(Id, capture, asset, profile, observed);
    public void Finished(Guid capture, Guid asset, PreviewProfile profile) => buffer.Finished(Id, capture, asset, profile);
    public void Publish(CameraTransferActivity activity) => buffer.Publish(Id, activity);
    public CameraTransferActivity? GetRecentReceiving(Guid transfer, DateTimeOffset now) => buffer.GetRecentReceiving(Id, transfer, now);
    public PreviewPreparationObservation? GetRecentPreviewPreparation(Guid capture, Guid asset, PreviewProfile profile, DateTimeOffset now) =>
        buffer.GetRecentPreviewPreparation(Id, capture, asset, profile, now);
}
