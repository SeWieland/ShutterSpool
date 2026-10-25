using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Spool;

namespace ShutterSpool.Core.Tests;

public sealed class RetentionRemovalServiceTests
{
    [Fact]
    public async Task Removal_waits_for_active_preview_use_then_invalidates_before_removing_original()
    {
        Guid assetId = Guid.NewGuid();
        string spoolKey = $"spool/2026/10/{assetId:N}";
        var store = new FakeRemovalStore(new AuthorizedAssetRemoval(Guid.NewGuid(), Guid.NewGuid(), assetId,
            spoolKey, 128, Guid.NewGuid().ToString("N"), []));
        var order = new List<string>();
        var cache = new FakeCacheInvalidator(order);
        var exactRemoval = new FakeExactRemoval(order);
        var originalLock = new FakeOriginalLock(order);
        var quota = new FakeQuotaStore(KnownSnapshot());
        var gate = new PreviewAssetUseGate();
        using IDisposable activePreview = gate.TryEnter(assetId)!;
        var service = new RetentionRemovalService(store, quota, new FakeSettingsStore(), gate, originalLock, cache, exactRemoval,
            TimeProvider.System, new FakeRetentionControllerLock());

        Task<RetentionRemovalPassResult> pass = service.ProcessPendingAsync(10, CancellationToken.None);
        await WaitUntilAsync(() =>
        {
            using IDisposable? probe = gate.TryEnter(assetId);
            return probe is null;
        });
        Assert.Empty(order);
        activePreview.Dispose();

        RetentionRemovalPassResult result = await pass.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Completed);
        Assert.Equal(128, result.RemovedBytes);
        Assert.Equal(new[] { "exclusive-lock", "cache", "original" }, order);
        Assert.Equal(1, store.CompletedAssets);
        Assert.Equal(1, store.FinalizedCaptures);
    }

    [Fact]
    public async Task Unknown_accounting_blocks_manual_delete_before_durable_authorization()
    {
        var store = new FakeRemovalStore(new AuthorizedAssetRemoval(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "spool/2026/10/00000000000000000000000000000000", 128, Guid.NewGuid().ToString("N"), []));
        var service = new RetentionRemovalService(store, new FakeQuotaStore(KnownSnapshot() with { IsKnown = false }),
            new FakeSettingsStore(),
            new PreviewAssetUseGate(), new FakeOriginalLock([]), new FakeCacheInvalidator([]),
            new FakeExactRemoval([]), TimeProvider.System, new FakeRetentionControllerLock());

        RetentionOperationResult result = await service.DeletePermanentlyAsync(Guid.NewGuid(), 4, true,
            CancellationToken.None);

        Assert.Equal(RetentionOperationStatus.Blocked, result.Status);
        Assert.Equal("SPOOL_ACCOUNTING_UNKNOWN", result.ErrorCode);
        Assert.Equal(0, store.Authorizations);
    }

    [Fact]
    public async Task Permanent_delete_requires_explicit_confirmation()
    {
        var store = new FakeRemovalStore(new AuthorizedAssetRemoval(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "spool/2026/10/00000000000000000000000000000000", 128, Guid.NewGuid().ToString("N"), []));
        var service = new RetentionRemovalService(store, new FakeQuotaStore(KnownSnapshot()), new FakeSettingsStore(),
            new PreviewAssetUseGate(), new FakeOriginalLock([]), new FakeCacheInvalidator([]),
            new FakeExactRemoval([]), TimeProvider.System, new FakeRetentionControllerLock());

        await Assert.ThrowsAsync<ArgumentException>(() => service.DeletePermanentlyAsync(Guid.NewGuid(), 1, false,
            CancellationToken.None));

        Assert.Equal(0, store.Authorizations);
    }

    [Fact]
    public async Task Pressure_credit_excludes_preserved_trash_from_finite_reclamation_bytes()
    {
        SpoolQuotaSnapshot snapshot = PressureSnapshot(used: 100, active: 20, trash: 80);
        var store = new FakeRemovalStore(pendingReclamationBytes: 20);
        var service = CreateAutomaticService(store, new FakeQuotaStore(snapshot), automaticRemovalEnabled: true);

        await service.ProcessAutomaticTrashAsync(10, TestContext.Current.CancellationToken);

        Assert.True(store.LastSelectionUnderPressure);
        Assert.Equal(10, store.LastSelectionMaximumBytes);
        Assert.True(store.PressureEpisodeActive);
    }

    [Fact]
    public async Task Cache_reclaim_below_high_watermark_keeps_pressure_until_target_and_admission_are_ready()
    {
        SpoolQuotaSnapshot highOccupancy = PressureSnapshot(used: 100, active: 100);
        SpoolQuotaSnapshot afterCacheReclaim = PressureSnapshot(used: 85, active: 85);
        var quota = new FakeQuotaStore(highOccupancy, highOccupancy, afterCacheReclaim, afterCacheReclaim);
        var store = new FakeRemovalStore();
        var cache = new FakeCacheReclaimer(new(15, 1, false));
        var service = CreateAutomaticService(store, quota, automaticRemovalEnabled: true, cacheReclaimer: cache);

        await service.ProcessAutomaticTrashAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(30, cache.RequestedBytes);
        Assert.True(store.LastSelectionUnderPressure);
        Assert.Equal(15, store.LastSelectionMaximumBytes);
        Assert.True(store.PressureEpisodeActive);

        var nextTick = CreateAutomaticService(store, new FakeQuotaStore(afterCacheReclaim), automaticRemovalEnabled: true);
        await nextTick.ProcessAutomaticTrashAsync(10, TestContext.Current.CancellationToken);

        Assert.True(store.LastSelectionUnderPressure);
        Assert.Equal(15, store.LastSelectionMaximumBytes);
        Assert.True(store.PressureEpisodeActive);

        SpoolQuotaSnapshot blockedAtTarget = PressureSnapshot(used: 70, active: 70,
            admissionBlockedReason: "filesystem_reserve");
        var blockedTick = CreateAutomaticService(store, new FakeQuotaStore(blockedAtTarget), automaticRemovalEnabled: true);
        await blockedTick.ProcessAutomaticTrashAsync(10, TestContext.Current.CancellationToken);
        Assert.True(store.PressureEpisodeActive);

        var recoveredTick = CreateAutomaticService(store, new FakeQuotaStore(PressureSnapshot(used: 70, active: 70)),
            automaticRemovalEnabled: true);
        await recoveredTick.ProcessAutomaticTrashAsync(10, TestContext.Current.CancellationToken);
        Assert.False(store.PressureEpisodeActive);
    }

    [Fact]
    public async Task Busy_controller_lease_skips_the_entire_automatic_pass()
    {
        var store = new FakeRemovalStore();
        var quota = new FakeQuotaStore(KnownSnapshot());
        var service = new RetentionRemovalService(store, quota, new FakeSettingsStore(automaticRemovalEnabled: true),
            new PreviewAssetUseGate(), new FakeOriginalLock([]), new FakeCacheInvalidator([]),
            new FakeExactRemoval([]), TimeProvider.System, new FakeRetentionControllerLock(acquired: false));

        RetentionRemovalPassResult result = await service.ProcessAutomaticTrashAsync(10,
            TestContext.Current.CancellationToken);

        Assert.Equal(new RetentionRemovalPassResult(0, 0, 0, 0), result);
        Assert.Equal(0, store.CapacityObservations);
        Assert.Equal(0, store.PressureEpisodeReads);
        Assert.Equal(0, quota.SnapshotReads);
    }

    private static RetentionRemovalService CreateAutomaticService(FakeRemovalStore store, FakeQuotaStore quota,
        bool automaticRemovalEnabled, IPreviewCacheReclaimer? cacheReclaimer = null) =>
        new(store, quota, new FakeSettingsStore(automaticRemovalEnabled), new PreviewAssetUseGate(),
            new FakeOriginalLock([]), new FakeCacheInvalidator([]), new FakeExactRemoval([]), TimeProvider.System,
            new FakeRetentionControllerLock(), cacheReclaimer);

    private static SpoolQuotaSnapshot PressureSnapshot(long used, long active, long trash = 0,
        string? admissionBlockedReason = null) =>
        new(new(1, true, 200, 90, 70), DateTimeOffset.UtcNow, true, used, 0, active, trash,
            0, 0, 0, 1_000, 900, null, Math.Max(0, used - 70), admissionBlockedReason);

    private static SpoolQuotaSnapshot KnownSnapshot() => new(new(1, true, 1024, 900, 700),
        DateTimeOffset.UtcNow, true, 100, 0, 100, 0, 0, 0, 0, 1024, 900, 100, 0, null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FakeRemovalStore(AuthorizedAssetRemoval? pending = null, long pendingReclamationBytes = 0) : IRetentionRemovalStore
    {
        public int Authorizations { get; private set; }
        public int CompletedAssets { get; private set; }
        public int FinalizedCaptures { get; private set; }
        public bool PressureEpisodeActive { get; private set; }
        public int CapacityObservations { get; private set; }
        public int PressureEpisodeReads { get; private set; }
        public bool LastSelectionUnderPressure { get; private set; }
        public long LastSelectionMaximumBytes { get; private set; }

        public Task RecordCapacityObservationAsync(SpoolQuotaSnapshot snapshot, RetentionRemovalPassResult pass,
            bool automaticRemovalEnabled, DateTimeOffset now, CancellationToken cancellationToken)
        {
            CapacityObservations++;
            if (!snapshot.IsKnown) return Task.CompletedTask;
            long occupancy = snapshot.UsedBytes + snapshot.ReservedBytes;
            if (PressureEpisodeActive && snapshot.Settings.RecoveryTargetBytes is long target
                && occupancy <= target && snapshot.AdmissionBlockedReason is null)
                PressureEpisodeActive = false;
            else if (!PressureEpisodeActive && snapshot.Settings.IsActive
                && snapshot.Settings.HighWatermarkBytes is long high && occupancy >= high)
                PressureEpisodeActive = true;
            return Task.CompletedTask;
        }
        public Task<int> PruneCompletedAttemptHistoryAsync(DateTimeOffset cutoff, int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<int> RefreshRetentionFactsAsync(int maximumCount, DateTimeOffset now,
            CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<bool> IsPressureEpisodeActiveAsync(CancellationToken cancellationToken) =>
            ReadPressureEpisodeAsync();

        private Task<bool> ReadPressureEpisodeAsync()
        {
            PressureEpisodeReads++;
            return Task.FromResult(PressureEpisodeActive);
        }
        public Task<RetentionSelectionPassResult> SelectEligibleActiveAsync(int maximumCount, long maximumBytes,
            bool underPressure, DateTimeOffset now, CancellationToken cancellationToken)
        {
            LastSelectionUnderPressure = underPressure;
            LastSelectionMaximumBytes = maximumBytes;
            return Task.FromResult(new RetentionSelectionPassResult(0, 0, 0));
        }

        public Task<RetentionRemovalAuthorization> AuthorizeCaptureAsync(Guid captureId, int? expectedRevision,
            RetentionRemovalReason reason, DateTimeOffset now, CancellationToken cancellationToken)
        {
            Authorizations++;
            return Task.FromResult(new RetentionRemovalAuthorization(
                new(RetentionOperationStatus.Pending, null), pending is null ? [] : [pending]));
        }

        public Task<IReadOnlyList<AuthorizedAssetRemoval>> GetPendingAsync(int maximumCount, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AuthorizedAssetRemoval>>(pending is null ? [] : [pending]);
        public Task<IReadOnlyList<RetentionRemovalCandidate>> GetDueTrashCandidatesAsync(int maximumCount, DateTimeOffset now, bool underPressure, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RetentionRemovalCandidate>>([]);
        public Task<long> GetPendingReclamationBytesAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(pendingReclamationBytes);

        public Task<bool> MarkRemovingAsync(Guid operationId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CompleteAssetAsync(Guid operationId, long removedBytes, DateTimeOffset now, CancellationToken cancellationToken)
        { CompletedAssets++; return Task.CompletedTask; }
        public Task FailAssetAsync(Guid operationId, string failureCode, DateTimeOffset now, long partiallyRemovedBytes,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FinalizeCaptureAsync(Guid captureId, DateTimeOffset now, CancellationToken cancellationToken)
        { FinalizedCaptures++; return Task.CompletedTask; }
        public Task<CaptureRetentionSummary?> GetSummaryAsync(Guid captureId, DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult<CaptureRetentionSummary?>(null);
    }

    private sealed class FakeQuotaStore(params SpoolQuotaSnapshot[] snapshots) : ISpoolQuotaStore
    {
        private readonly Queue<SpoolQuotaSnapshot> _snapshots = new(snapshots);
        private SpoolQuotaSnapshot? _current;
        public int SnapshotReads { get; private set; }
        public Task<SpoolQuotaSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            SnapshotReads++;
            if (_snapshots.TryDequeue(out SpoolQuotaSnapshot? next)) _current = next;
            return Task.FromResult(_current ?? throw new InvalidOperationException("A quota snapshot is required."));
        }
        public Task<SpoolQuotaSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_current?.Settings ?? snapshots[0].Settings);
        public Task<SpoolQuotaSettings?> StageSettingsUpdateAsync(int expectedRevision, SpoolQuotaSettings settings, bool authorizeStorageMarkerAdoption, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SpoolQuotaSettings?> UpdateSettingsAsync(int expectedRevision, SpoolQuotaSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> TryReserveAsync(SpoolQuotaReservation reservation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UpdateReservationAsync(Guid operationId, Guid? assetId, IReadOnlyList<string> spoolKeys, long reservedBytes, long writtenBytes, long filesystemReservedBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> MarkReservationRetainedAsync(Guid operationId, long actualBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseUnadmittedReservationAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReleaseReservationAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ReconcileReservationsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeRetentionControllerLock(bool acquired = true) : IRetentionControllerLock
    {
        public ValueTask<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable?>(acquired ? NoopAsyncDisposable.Instance : null);

        private sealed class NoopAsyncDisposable : IAsyncDisposable
        {
            public static NoopAsyncDisposable Instance { get; } = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeSettingsStore(bool automaticRemovalEnabled = false) : IRetentionSettingsStore
    {
        private readonly RetentionSettingsDocument _document = new(new ShutterSpool.Domain.Retention.RetentionSettings
            { AutomaticRemovalEnabled = automaticRemovalEnabled }, new(),
            new(PreviewCacheMode.Disk, 64L * 1024 * 1024), new(PreviewCacheMode.Disk, 64L * 1024 * 1024), false);
        public Task<RetentionSettingsDocument> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_document);
        public Task<RetentionSettingsImpact> PreviewAsync(ShutterSpool.Domain.Retention.RetentionSettings settings, SpoolQuotaSettings quota, PreviewCacheSettings previewCache, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RetentionSettingsUpdateResult> UpdateAsync(int expectedSettingsRevision, int expectedQuotaRevision, ShutterSpool.Domain.Retention.RetentionSettings settings, SpoolQuotaSettings quota, PreviewCacheSettings previewCache, bool acknowledgeImpact, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeCacheReclaimer(PreviewCacheReclaimResult result) : IPreviewCacheReclaimer
    {
        public long RequestedBytes { get; private set; }
        public Task<PreviewCacheReclaimResult> ReclaimAsync(long maximumBytes, int maximumEntries,
            CancellationToken cancellationToken)
        {
            RequestedBytes = maximumBytes;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeOriginalLock(List<string> order) : IOriginalAssetLock
    {
        public Task<IOriginalAssetLockLease> AcquireSharedAsync(Guid assetId, string storageKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IOriginalAssetLockLease> AcquireExclusiveAsync(Guid assetId, string storageKey, CancellationToken cancellationToken)
        {
            order.Add("exclusive-lock");
            return Task.FromResult<IOriginalAssetLockLease>(new FakeLease(assetId, storageKey));
        }
        public Task<bool> IsAvailableAsync(IOriginalAssetLockLease sharedLease, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeLease(Guid assetId, string storageKey) : IOriginalAssetLockLease
    {
        public Guid AssetId { get; } = assetId;
        public string StorageKey { get; } = storageKey;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCacheInvalidator(List<string> order) : IPreviewAssetCacheInvalidator
    {
        public Task RemoveAssetAsync(Guid assetId, CancellationToken cancellationToken)
        { order.Add("cache"); return Task.CompletedTask; }
    }

    private sealed class FakeExactRemoval(List<string> order) : IExactOriginalRemoval
    {
        public Task<ExactOriginalRemovalResult> RemoveAsync(ExactOriginalRemovalRequest request, CancellationToken cancellationToken)
        { order.Add("original"); return Task.FromResult(new ExactOriginalRemovalResult(ExactOriginalRemovalStatus.Removed, request.ExpectedSize)); }
    }
}
