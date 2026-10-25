namespace ShutterSpool.Application.Preview;

using ShutterSpool.Application.Spool;

public sealed record PreviewSource(Guid AssetId, string SpoolKey, long Size);
public sealed record PreviewResolution(bool CaptureExists, PreviewSource? Source, bool SourceUnavailable = false);
public sealed record PreviewSize(int Width, int Height);
public enum PreviewProfile { Grid, Detail }
public enum PreviewPrewarmMode { Off, GridOnly, Full }
public enum PreviewCacheMode { Disk, Memory }
public enum PreviewWarmOutcome { Ready, Retry, Invalid, Obsolete }
public sealed record PreviewWarmResult(PreviewWarmOutcome Outcome, bool Published = false);
public sealed class PreviewPermanentException(string message, Exception? inner = null) : IOException(message, inner) { }
public enum PreviewState { Missing, Pending, Unavailable, Ready }
public sealed record PreviewResult(PreviewState State, byte[]? Content = null, Guid? SourceAssetId = null, bool Published = false);

public interface IPreviewActivityPublisher
{
    void Preparing(Guid captureId, Guid assetId, PreviewProfile profile, DateTimeOffset observedAt);
    void Finished(Guid captureId, Guid assetId, PreviewProfile profile);
}

public interface IPreviewSourceResolver
{
    Task<PreviewResolution> ResolveAsync(Guid captureId, CancellationToken cancellationToken);
}

public interface IPreviewCache
{
    Task<byte[]?> ReadAsync(string key, PreviewSize size, CancellationToken cancellationToken);
    Task<bool> ContainsAsync(string key, PreviewSize size, CancellationToken cancellationToken);
    Task<bool> WriteAsync(string key, byte[] content, CancellationToken cancellationToken);
}

public interface IPreviewAssetCacheInvalidator
{
    Task RemoveAssetAsync(Guid assetId, CancellationToken cancellationToken);
}

public interface IPreviewGenerator
{
    Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, CancellationToken cancellationToken);
    Task<byte[]> GenerateAsync(PreviewSource source, PreviewSize size, IOriginalAssetLockLease sharedLease,
        CancellationToken cancellationToken) => GenerateAsync(source, size, cancellationToken);
}

public sealed record PreviewPrewarmWork(Guid AssetId, Guid CaptureId, PreviewProfile Profile, Guid LeaseId, int AttemptCount,
    PreviewSize Size);

public interface IPreviewPrewarmStore
{
    Task<PreviewPrewarmWork?> ClaimAsync(PreviewSettings settings, DateTimeOffset now, CancellationToken cancellationToken);
    Task<bool> FinishAsync(PreviewPrewarmWork work, PreviewWarmOutcome outcome, DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed class PreviewSettings
{
    public int GridWidth { get; set; } = 480;
    public int GridHeight { get; set; } = 480;
    public int DetailWidth { get; set; } = 2048;
    public int DetailHeight { get; set; } = 2048;
    public int MaxConcurrentGenerations { get; set; } = 32;
    public int GenerationQueueTimeoutSeconds { get; set; } = 60;
    public int GenerationTimeoutSeconds { get; set; } = 90;
    public PreviewPrewarmMode PrewarmMode { get; set; } = PreviewPrewarmMode.GridOnly;
    public PreviewCacheMode CacheMode { get; set; } = PreviewCacheMode.Disk;
    public long MemoryCacheBytes { get; set; } = 64L * 1024 * 1024;

    public void Validate()
    {
        foreach (int dimension in new[] { GridWidth, GridHeight, DetailWidth, DetailHeight })
        {
            if (dimension is < 64 or > 4096) throw new InvalidOperationException("Preview dimensions must be between 64 and 4096 pixels.");
        }
        if (MaxConcurrentGenerations is < 1 or > 128) throw new InvalidOperationException("Preview concurrency must be between 1 and 128.");
        if (GenerationQueueTimeoutSeconds is < 1 or > 600 || GenerationTimeoutSeconds is < 1 or > 600)
            throw new InvalidOperationException("Preview timeouts must be between 1 and 600 seconds.");
        if (!Enum.IsDefined(PrewarmMode)) throw new InvalidOperationException("Invalid preview prewarm mode.");
        if (!Enum.IsDefined(CacheMode)) throw new InvalidOperationException("Invalid preview cache mode.");
        if (MemoryCacheBytes is <= 0 or > PreviewCacheSettings.MaximumMemoryCacheBytes)
            throw new InvalidOperationException("Preview memory cache budget must be between 1 byte and 1 GiB.");
    }
}

public sealed class PreviewService(IPreviewSourceResolver sources, IPreviewCache cache, IPreviewGenerator generator,
    PreviewSettings settings, IPreviewActivityPublisher? activity = null, PreviewAssetUseGate? useGate = null,
    IOriginalAssetLock? assetLock = null)
{
    public async Task<PreviewResult> GetAsync(Guid captureId, PreviewProfile profile, CancellationToken cancellationToken,
        int? maximumDimension = null)
    {
        if (maximumDimension is < 64 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        PreviewSize size = maximumDimension is int dimension ? new(dimension, dimension) : SizeFor(profile);

        try
        {
            PreviewResolution resolution = await sources.ResolveAsync(captureId, cancellationToken);
            if (!resolution.CaptureExists) return new(PreviewState.Missing);
            if (resolution.Source is null) return new(resolution.SourceUnavailable ? PreviewState.Unavailable : PreviewState.Pending);

            using IDisposable? use = useGate?.TryEnter(resolution.Source.AssetId);
            if (useGate is not null && use is null) return new(PreviewState.Unavailable);
            await using IOriginalAssetLockLease? sharedLease = assetLock is null ? null
                : await assetLock.AcquireSharedAsync(resolution.Source.AssetId, resolution.Source.SpoolKey, cancellationToken);
            if (sharedLease is not null)
            {
                if (!await assetLock!.IsAvailableAsync(sharedLease, cancellationToken)) return new(PreviewState.Unavailable);
                PreviewResolution? changed = await RevalidateSourceAsync(captureId, resolution.Source, cancellationToken);
                if (changed is not null) return ResolveGetSourceChange(changed);
            }
            CachedPreview cached = await EnsureCachedAsync(captureId, resolution.Source, profile, size, sharedLease, cancellationToken);
            return new(PreviewState.Ready, cached.Content, resolution.Source.AssetId, cached.Published);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(PreviewState.Unavailable);
        }
    }

    public async Task<PreviewWarmOutcome> WarmAsync(Guid captureId, Guid expectedAssetId, PreviewProfile profile,
        CancellationToken cancellationToken) =>
        (await WarmWithPublicationAsync(captureId, expectedAssetId, profile, cancellationToken)).Outcome;

    public async Task<PreviewWarmResult> WarmWithPublicationAsync(Guid captureId, Guid expectedAssetId,
        PreviewProfile profile, CancellationToken cancellationToken)
    {
        PreviewSize size = SizeFor(profile);
        try
        {
            PreviewResolution resolution = await sources.ResolveAsync(captureId, cancellationToken);
            if (!resolution.CaptureExists || resolution.Source?.AssetId != expectedAssetId)
                return new(resolution.SourceUnavailable ? PreviewWarmOutcome.Retry : PreviewWarmOutcome.Obsolete);
            using IDisposable? use = useGate?.TryEnter(expectedAssetId);
            if (useGate is not null && use is null) return new(PreviewWarmOutcome.Retry);
            await using IOriginalAssetLockLease? sharedLease = assetLock is null ? null
                : await assetLock.AcquireSharedAsync(expectedAssetId, resolution.Source.SpoolKey, cancellationToken);
            if (sharedLease is not null)
            {
                if (!await assetLock!.IsAvailableAsync(sharedLease, cancellationToken)) return new(PreviewWarmOutcome.Obsolete);
                PreviewResolution? changed = await RevalidateSourceAsync(captureId, resolution.Source, cancellationToken);
                if (changed is not null) return ResolveWarmSourceChange(changed);
            }
            CachedPreview cached = await EnsureCachedAsync(captureId, resolution.Source, profile, size, sharedLease, cancellationToken);
            return new(PreviewWarmOutcome.Ready, cached.Published);
        }
        catch (PreviewPermanentException)
        {
            return new(PreviewWarmOutcome.Invalid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(PreviewWarmOutcome.Retry);
        }
    }

    private async Task<PreviewResolution?> RevalidateSourceAsync(Guid captureId, PreviewSource source,
        CancellationToken cancellationToken)
    {
        PreviewResolution current = await sources.ResolveAsync(captureId, cancellationToken);
        return current.CaptureExists && current.Source?.AssetId == source.AssetId
            && current.Source.SpoolKey == source.SpoolKey ? null : current;
    }

    private static PreviewResult ResolveGetSourceChange(PreviewResolution current)
    {
        if (!current.CaptureExists) return new(PreviewState.Missing);
        return new(current.Source is null && current.SourceUnavailable ? PreviewState.Unavailable : PreviewState.Pending);
    }

    private static PreviewWarmResult ResolveWarmSourceChange(PreviewResolution current) =>
        new(current.SourceUnavailable ? PreviewWarmOutcome.Retry : PreviewWarmOutcome.Obsolete);

    private PreviewSize SizeFor(PreviewProfile profile) => profile switch
    {
        PreviewProfile.Grid => new(settings.GridWidth, settings.GridHeight),
        PreviewProfile.Detail => new(settings.DetailWidth, settings.DetailHeight),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private async Task<CachedPreview> EnsureCachedAsync(Guid captureId, PreviewSource source, PreviewProfile profile,
        PreviewSize size, IOriginalAssetLockLease? sharedLease, CancellationToken cancellationToken)
    {
        string key = $"{captureId:N}/{source.AssetId:N}/{profile.ToString().ToLowerInvariant()}-{size.Width}x{size.Height}.webp";
        byte[]? content = await cache.ReadAsync(key, size, cancellationToken);
        if (content is not null) return new(content, false);

        activity?.Preparing(captureId, source.AssetId, profile, DateTimeOffset.UtcNow);
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task renewals = activity is null ? Task.CompletedTask : RenewActivityAsync(captureId, source.AssetId, profile, renewal.Token);
        try
        {
            content = sharedLease is null
                ? await generator.GenerateAsync(source, size, cancellationToken)
                : await generator.GenerateAsync(source, size, sharedLease, cancellationToken);
            bool published = await cache.WriteAsync(key, content, cancellationToken);
            return new(content, published);
        }
        finally
        {
            renewal.Cancel();
            try { await renewals; }
            catch (OperationCanceledException) when (renewal.IsCancellationRequested) { }
            activity?.Finished(captureId, source.AssetId, profile);
        }
    }

    private async Task RenewActivityAsync(Guid captureId, Guid assetId, PreviewProfile profile, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            activity!.Preparing(captureId, assetId, profile, DateTimeOffset.UtcNow);
    }

    private sealed record CachedPreview(byte[] Content, bool Published);
}
