using ShutterSpool.Application.Preview;
using ShutterSpool.Application.Retention;
using ShutterSpool.Application.Spool;

namespace ShutterSpool.Core.Tests;

public sealed class PrivacyErasureServiceTests
{
    [Fact]
    public async Task Erasure_requires_an_explicit_finite_exact_selection_before_authorization()
    {
        var store = new ErasureStore();
        var service = Create(store, new List<string>());
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentException>(() => service.EraseAsync(
            new(Guid.NewGuid(), [new(captureId, 4, [assetId])], false), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.EraseAsync(
            new(Guid.NewGuid(), [new(captureId, 4, [assetId, assetId])], true), CancellationToken.None));

        Assert.Equal(0, store.BeginCalls);
    }

    [Fact]
    public async Task Unresolved_preservation_keeps_a_durable_pending_request_without_removing_bytes()
    {
        var order = new List<string>();
        var store = new ErasureStore { Order = order, BeginResult = new(PrivacyErasureStatus.Pending, "PRESERVATION_HOLD_ACTIVE", []) };
        var service = Create(store, order);

        PrivacyErasureResult result = await service.EraseAsync(
            new(Guid.NewGuid(), [new(Guid.NewGuid(), 2, [Guid.NewGuid()])], true), CancellationToken.None);

        Assert.Equal(PrivacyErasureStatus.Pending, result.Status);
        Assert.Equal("PRESERVATION_HOLD_ACTIVE", result.ErrorCode);
        Assert.Empty(order);
    }

    [Theory]
    [InlineData(PrivacyErasureStatus.Conflict, "REVISION_CONFLICT")]
    [InlineData(PrivacyErasureStatus.Blocked, "SPOOL_STORAGE_IDENTITY_UNKNOWN")]
    public async Task Refused_new_request_preserves_the_begin_status_instead_of_reporting_not_found(
        PrivacyErasureStatus status, string errorCode)
    {
        var store = new ErasureStore
        {
            BeginResult = new(status, errorCode, []),
        };
        PrivacyErasureResult result = await Create(store, [])
            .EraseAsync(new(Guid.NewGuid(), [new(Guid.NewGuid(), 1, [Guid.NewGuid()])], true), CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal(0, store.GetResultCalls);
    }

    [Fact]
    public async Task Exact_authorized_members_are_fenced_invalidated_removed_and_completed_in_order()
    {
        Guid captureId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        string key = $"spool/2026/10/{assetId:N}";
        var order = new List<string>();
        var store = new ErasureStore
        {
            Order = order,
            BeginResult = new(PrivacyErasureStatus.Pending, null,
                [new(captureId, assetId, key, 81, Guid.NewGuid().ToString("N"), ["incoming/receive/asset.partial"])])
        };
        var service = Create(store, order);

        PrivacyErasureResult result = await service.EraseAsync(
            new(Guid.NewGuid(), [new(captureId, 3, [assetId])], true), CancellationToken.None);

        Assert.Equal(PrivacyErasureStatus.Completed, result.Status);
        Assert.Equal(new[] { "redacted", "exclusive-lock", "derivatives", "originals" }, order);
        Assert.Equal(assetId, store.CompletedAssetId);
        Assert.Equal(key, store.RemovalRequest?.SpoolKey);
        Assert.Contains("incoming/receive/asset.partial", store.RemovalRequest!.KnownTransientKeys);
    }

    [Fact]
    public async Task Pending_operation_pages_keep_older_blocked_requests_discoverable_with_keyset_cursor()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var store = new ErasureStore
        {
            ListResults =
            [
                new(Guid.NewGuid(), PrivacyErasureStatus.Completed, now, 1, 1, 0, null),
                new(Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), PrivacyErasureStatus.Pending,
                    now.AddMinutes(-1), 1, 0, 1, "PRESERVATION_HOLD_ACTIVE"),
                new(Guid.Parse("11111111-1111-1111-1111-111111111111"), PrivacyErasureStatus.Pending,
                    now.AddMinutes(-2), 1, 0, 1, "PRESERVATION_HOLD_ACTIVE"),
            ],
        };
        var service = Create(store, []);

        PrivacyErasureOperationPage first = await service.ListAsync(1, true, null, CancellationToken.None);
        Assert.Equal(PrivacyErasureStatus.Pending, Assert.Single(first.Operations).Status);
        Assert.NotNull(first.NextCursor);
        PrivacyErasureOperationPage second = await service.ListAsync(1, true, first.NextCursor, CancellationToken.None);
        Assert.Equal(PrivacyErasureStatus.Pending, Assert.Single(second.Operations).Status);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, store.LastListMaximum);
        Assert.True(store.LastPendingOnly);
    }

    private static PrivacyErasureService Create(ErasureStore store, List<string> order) => new(store,
        new PreviewAssetUseGate(), new OriginalLock(order), new CacheInvalidator(order),
        new ExactRemoval(order, store), TimeProvider.System);

    private sealed class ErasureStore : IPrivacyErasureStore
    {
        public PrivacyErasureBeginResult BeginResult { get; init; } = new(PrivacyErasureStatus.Pending, null, []);
        public List<string>? Order { get; init; }
        public int BeginCalls { get; private set; }
        public int GetResultCalls { get; private set; }
        public Guid? CompletedAssetId { get; private set; }
        public ExactOriginalRemovalRequest? RemovalRequest { get; set; }
        public IReadOnlyList<PrivacyErasureOperationSummary> ListResults { get; init; } = [];
        public int LastListMaximum { get; private set; }
        public bool LastPendingOnly { get; private set; }

        public Task<PrivacyErasureBeginResult> BeginAsync(PrivacyErasureRequest request, DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            BeginCalls++;
            if (BeginResult.Work.Count > 0) Order?.Add("redacted");
            return Task.FromResult(BeginResult);
        }
        public Task<PrivacyErasurePreview> PreviewAsync(IReadOnlyList<PrivacyErasureSelection> selections,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<PrivacyErasureAssetWork>> GetPendingAsync(Guid operationId, int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult(BeginResult.Work);
        public Task<bool> MarkRemovingAsync(Guid operationId, Guid assetId, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task CompleteSurfaceAsync(Guid operationId, Guid assetId, string surface, DateTimeOffset now,
            CancellationToken cancellationToken)
        { if (surface == "originals") CompletedAssetId = assetId; return Task.CompletedTask; }
        public Task FailSurfaceAsync(Guid operationId, Guid assetId, string surface, string errorCode,
            DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<PrivacyErasureResult> GetResultAsync(Guid operationId, CancellationToken cancellationToken) =>
            CountResultAsync(operationId);
        private Task<PrivacyErasureResult> CountResultAsync(Guid operationId)
        {
            GetResultCalls++;
            return Task.FromResult(new PrivacyErasureResult(operationId,
                CompletedAssetId is not null ? PrivacyErasureStatus.Completed : BeginResult.Status,
                BeginResult.ErrorCode, []));
        }
        public Task<IReadOnlyList<PrivacyErasureOperationSummary>> ListAsync(int maximumCount, bool pendingOnly,
            DateTimeOffset? beforeRequestedAt, Guid? beforeOperationId, CancellationToken cancellationToken)
        {
            LastListMaximum = maximumCount;
            LastPendingOnly = pendingOnly;
            IEnumerable<PrivacyErasureOperationSummary> query = ListResults;
            if (pendingOnly) query = query.Where(item => item.Status != PrivacyErasureStatus.Completed);
            if (beforeRequestedAt is DateTimeOffset requestedAt && beforeOperationId is Guid operationId)
                query = query.Where(item => item.RequestedAt < requestedAt
                    || item.RequestedAt == requestedAt && item.OperationId.CompareTo(operationId) < 0);
            PrivacyErasureOperationSummary[] page = [.. query.OrderByDescending(item => item.RequestedAt)
                .ThenByDescending(item => item.OperationId).Take(maximumCount)];
            return Task.FromResult<IReadOnlyList<PrivacyErasureOperationSummary>>(page);
        }
        public Task<IReadOnlyList<Guid>> GetPendingOperationIdsAsync(Guid? afterOperationId, int maximumCount,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Guid>>([]);
        public Task<PrivacyErasureSuppressionManifest> CreateSuppressionExportAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PrivacyErasureSuppressionManifest?> GetSuppressionExportAsync(Guid snapshotId,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PrivacyErasureSuppressionPage?> GetSuppressionExportPageAsync(Guid snapshotId, int pageIndex,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PrivacyErasureImportStatus> BeginSuppressionReconciliationAsync(
            PrivacyErasureSuppressionManifest manifest, Guid expectedSnapshotId,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PrivacyErasureImportStatus> StageSuppressionPageAsync(PrivacyErasureSuppressionPage page,
            CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PrivacyErasureImportStatus> CompleteSuppressionReconciliationAsync(Guid snapshotId,
            CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class OriginalLock(List<string> order) : IOriginalAssetLock
    {
        public Task<IOriginalAssetLockLease> AcquireSharedAsync(Guid assetId, string storageKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IOriginalAssetLockLease> AcquireExclusiveAsync(Guid assetId, string storageKey, CancellationToken cancellationToken)
        {
            order.Add("exclusive-lock");
            return Task.FromResult<IOriginalAssetLockLease>(new Lease(assetId, storageKey));
        }
        public Task<bool> IsAvailableAsync(IOriginalAssetLockLease sharedLease, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Lease(Guid assetId, string storageKey) : IOriginalAssetLockLease
    {
        public Guid AssetId { get; } = assetId;
        public string StorageKey { get; } = storageKey;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CacheInvalidator(List<string> order) : IPreviewAssetCacheInvalidator
    {
        public Task RemoveAssetAsync(Guid assetId, CancellationToken cancellationToken)
        { order.Add("derivatives"); return Task.CompletedTask; }
    }

    private sealed class ExactRemoval(List<string> order, ErasureStore store) : IExactOriginalRemoval
    {
        public Task<ExactOriginalRemovalResult> RemoveAsync(ExactOriginalRemovalRequest request, CancellationToken cancellationToken)
        {
            order.Add("originals");
            store.RemovalRequest = request;
            return Task.FromResult(new ExactOriginalRemovalResult(ExactOriginalRemovalStatus.Removed,
                request.ExpectedSize));
        }
    }
}
