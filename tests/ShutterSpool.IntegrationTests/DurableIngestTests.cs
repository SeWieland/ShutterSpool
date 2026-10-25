using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Ingest;
using ShutterSpool.Domain.Assets;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed class DurableIngestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shutterspool-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Checks_admission_before_a_new_spool_root_exists()
    {
        var spool = CreateSpool();

        await spool.CheckAdmissionAsync(1, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Accepts_an_independent_asset_only_after_its_bytes_are_spooled()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);
        byte[] bytes = [1, 2, 3, 4];

        IngestResult first = await service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new MemoryStream(bytes)), TestContext.Current.CancellationToken);
        IngestResult second = await service.AcceptAsync(new IngestRequest(first.Asset.SourceId, "camera.jpg", new MemoryStream(bytes)), TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Asset.Id, second.Asset.Id);
        Assert.Equal(IngestOperationState.Committed, journal.Operations[first.Asset.RecoveryCorrelation].State);
        Assert.True(File.Exists(Path.Combine(_root, first.Asset.SpoolKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a", first.Asset.Sha256);
    }

    [Fact]
    public async Task Records_the_spool_adapters_partial_key_before_streaming()
    {
        var journal = new MemoryJournal();
        var service = new IngestService(journal, new OpaqueKeySpool(), TimeProvider.System);

        await Assert.ThrowsAsync<IOException>(() => service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new MemoryStream([1])), TestContext.Current.CancellationToken));

        Assert.Equal("provider-partial-key", Assert.Single(journal.Operations.Values).PartialKey);
    }

    [Fact]
    public async Task Does_not_commit_when_the_spool_returns_a_different_partial_key()
    {
        var journal = new MemoryJournal();
        var service = new IngestService(journal, new OpaqueKeySpool(returnDifferentKey: true), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new MemoryStream([1])), TestContext.Current.CancellationToken));

        Assert.Equal(IngestOperationState.Receiving, Assert.Single(journal.Operations.Values).State);
    }

    [Fact]
    public async Task Recovers_a_promoted_file_when_the_journal_update_was_interrupted()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal { FailPromotion = true };
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);
        Guid sourceId = Guid.NewGuid();

        await Assert.ThrowsAsync<IOException>(() => service.AcceptAsync(new IngestRequest(sourceId, "camera.jpg", new MemoryStream([1, 2, 3])), TestContext.Current.CancellationToken));
        journal.FailPromotion = false;
        await service.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IngestOperationState.Committed, Assert.Single(journal.Operations.Values).State);
    }

    [Fact]
    public async Task Recovery_does_not_replay_or_quarantine_an_asset_with_authorized_original_removal()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var spool = CreateSpool();
        Guid assetId = Guid.NewGuid();
        Guid sourceId = Guid.NewGuid();
        var operation = new IngestOperation(Guid.NewGuid(), assetId, sourceId, "synthetic.jpg",
            spool.GetPartialKey(sourceId, assetId), IngestOperationState.Receiving);
        await journal.CreateAsync(operation, TestContext.Current.CancellationToken);
        SpoolWrite promoted = await spool.WriteAsync(sourceId, assetId, new MemoryStream([1, 2, 3]), null,
            TestContext.Current.CancellationToken);
        await journal.MarkPromotedAsync(operation.Id, promoted, TestContext.Current.CancellationToken);
        journal.RemovalAuthorizedAssetIds.Add(assetId);

        await new IngestService(journal, spool, TimeProvider.System).RecoverAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, promoted.FinalKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(IngestOperationState.Promoted, journal.Operations[operation.Id].State);
    }

    [Fact]
    public async Task Does_not_acknowledge_a_stream_that_fails_while_writing()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);

        await Assert.ThrowsAsync<IOException>(() => service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new FailingStream()), TestContext.Current.CancellationToken));

        Assert.Equal(IngestOperationState.Receiving, Assert.Single(journal.Operations.Values).State);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), path => !path.EndsWith(".partial", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rejects_a_declared_maximum_upload_that_would_breach_the_reserve()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);

        await Assert.ThrowsAsync<IOException>(() => service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new MemoryStream([1]), long.MaxValue), TestContext.Current.CancellationToken));

        Assert.Empty(journal.Operations);
    }

    [Fact]
    public async Task Quarantines_untracked_spool_bytes_instead_of_deleting_them()
    {
        Directory.CreateDirectory(Path.Combine(_root, "spool", "2026", "09"));
        string orphan = Path.Combine(_root, "spool", "2026", "09", Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(orphan, [1], TestContext.Current.CancellationToken);

        await CreateSpool().QuarantineUntrackedAsync(new HashSet<string>(), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(orphan));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_root, "quarantine"), "*.reason", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Restores_an_operator_reason_for_interrupted_quarantine()
    {
        string quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        string orphan = Path.Combine(quarantine, $"{Guid.NewGuid():N}-{Guid.NewGuid():N}-orphan");
        await File.WriteAllBytesAsync(orphan, [1, 2, 3], TestContext.Current.CancellationToken);

        await CreateSpool().QuarantineUntrackedAsync(new HashSet<string>(), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(orphan, TestContext.Current.CancellationToken));
        Assert.Contains("reason unavailable", await File.ReadAllTextAsync($"{orphan}.reason", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Does_not_confuse_quarantined_media_named_reason_with_a_sidecar()
    {
        string quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        string media = Path.Combine(quarantine, $"{Guid.NewGuid():N}-{Guid.NewGuid():N}-camera.reason");
        await File.WriteAllBytesAsync(media, [1, 2], TestContext.Current.CancellationToken);

        await CreateSpool().QuarantineUntrackedAsync(new HashSet<string>(), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], await File.ReadAllBytesAsync(media, TestContext.Current.CancellationToken));
        Assert.True(File.Exists($"{media}.reason"));
    }

    [Fact]
    public async Task Restores_a_reason_for_legacy_media_named_media_reason()
    {
        string quarantine = Path.Combine(_root, "quarantine");
        Directory.CreateDirectory(quarantine);
        string media = Path.Combine(quarantine, $"{Guid.NewGuid():N}-{Guid.NewGuid():N}-camera.media.reason");
        await File.WriteAllBytesAsync(media, [1, 2], TestContext.Current.CancellationToken);

        await CreateSpool().QuarantineUntrackedAsync(new HashSet<string>(), TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], await File.ReadAllBytesAsync(media, TestContext.Current.CancellationToken));
        Assert.True(File.Exists($"{media}.reason"));
    }

    [Fact]
    public async Task Restores_a_committed_final_from_its_known_partial_after_a_lost_promotion()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);
        IngestResult result = await service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new MemoryStream([1, 2])), TestContext.Current.CancellationToken);
        string finalPath = Path.Combine(_root, result.Asset.SpoolKey.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(finalPath);

        await service.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(finalPath));
        Assert.Equal(IngestOperationState.Committed, journal.Operations[result.Asset.RecoveryCorrelation].State);
    }

    [Fact]
    public async Task Does_not_acknowledge_an_upload_that_exceeds_its_declared_maximum()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);

        await Assert.ThrowsAsync<IOException>(() => service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new MemoryStream([1, 2]), 1), TestContext.Current.CancellationToken));

        Assert.Equal(IngestOperationState.Receiving, Assert.Single(journal.Operations.Values).State);
    }

    [Fact]
    public async Task Rejects_a_reparse_point_before_writing_media()
    {
        string outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_root, "incoming"), outside);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw SkipException.ForSkip($"Directory symlinks are unavailable: {error.Message}");
        }

        await Assert.ThrowsAsync<IOException>(() => CreateSpool().WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new MemoryStream([1]), null, TestContext.Current.CancellationToken));

        Assert.Empty(Directory.EnumerateFiles(outside, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Rejects_keys_outside_the_spool_root_and_never_overwrites_a_collision()
    {
        Directory.CreateDirectory(_root);
        var spool = CreateSpool();
        var operation = new IngestOperation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "camera.jpg", "../outside", IngestOperationState.Receiving, "../outside");
        await Assert.ThrowsAsync<InvalidOperationException>(() => spool.FindFinalAsync(operation, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => spool.FindFinalAsync(operation with { FinalKey = Path.GetFullPath(Path.Combine(_root, "..", "outside")) }, TestContext.Current.CancellationToken));

        Guid sourceId = Guid.NewGuid();
        Guid assetId = Guid.NewGuid();
        SpoolWrite first = await spool.WriteAsync(sourceId, assetId, new MemoryStream([1]), null, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => spool.WriteAsync(sourceId, assetId, new MemoryStream([2]), null, TestContext.Current.CancellationToken));
        Assert.Equal([1], await File.ReadAllBytesAsync(Path.Combine(_root, first.FinalKey.Replace('/', Path.DirectorySeparatorChar)), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_a_case_variant_sibling_root_on_case_sensitive_hosts()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_root);
        string sibling = Path.GetFullPath(Path.Combine(_root, "..", Path.GetFileName(_root).ToUpperInvariant(), "outside"));
        var operation = new IngestOperation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "camera.jpg", "unused", IngestOperationState.Promoted, sibling);

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateSpool().FindFinalAsync(operation, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Accepts_a_spool_root_with_a_trailing_separator()
    {
        Directory.CreateDirectory(_root);
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = _root + Path.DirectorySeparatorChar, ReservedBytes = 0 });

        SpoolWrite write = await spool.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), new MemoryStream([1]), null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, write.FinalKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Quarantines_a_truncated_promotion_instead_of_committing_it()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal { FailPromotion = true };
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);
        Guid source = Guid.NewGuid();
        await Assert.ThrowsAsync<IOException>(() => service.AcceptAsync(new IngestRequest(source, "camera.jpg", new MemoryStream([1, 2, 3])), TestContext.Current.CancellationToken));
        IngestOperation operation = Assert.Single(journal.Operations.Values);
        string final = Directory.EnumerateFiles(Path.Combine(_root, "spool"), "*", SearchOption.AllDirectories).Single();
        await File.WriteAllBytesAsync(final, [1], TestContext.Current.CancellationToken);

        journal.FailPromotion = false;
        await service.RecoverAsync(TestContext.Current.CancellationToken);

        Assert.Equal(IngestOperationState.Quarantined, journal.Operations[operation.Id].State);
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_root, "quarantine"), "*.reason", SearchOption.TopDirectoryOnly).Count());
    }

    [Fact]
    public async Task Streams_with_the_configured_bounded_buffer()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);

        await service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "camera.jpg", new MaxReadStream(64 * 1024)), TestContext.Current.CancellationToken);

        Assert.Single(journal.Operations);
    }

    [Fact]
    public async Task Concurrent_same_name_transfers_remain_independent_assets()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);
        Guid source = Guid.NewGuid();

        IngestResult[] results = await Task.WhenAll(
            service.AcceptAsync(new IngestRequest(source, "camera.jpg", new MemoryStream([1, 2])), TestContext.Current.CancellationToken),
            service.AcceptAsync(new IngestRequest(source, "camera.jpg", new MemoryStream([1, 2])), TestContext.Current.CancellationToken));

        Assert.NotEqual(results[0].Asset.Id, results[1].Asset.Id);
        Assert.Equal(2, journal.Operations.Count);
    }

    [Fact]
    public async Task Replaying_a_known_operation_returns_its_committed_asset_without_reading_reused_bytes()
    {
        Directory.CreateDirectory(_root);
        var journal = new MemoryJournal();
        var service = new IngestService(journal, CreateSpool(), TimeProvider.System);
        var operationId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();

        IngestResult first = await service.AcceptAsync(new(sourceId, "camera.jpg", new MemoryStream([1, 2]), OperationId: operationId), TestContext.Current.CancellationToken);
        IngestResult replay = await service.AcceptAsync(new(sourceId, "camera.jpg", new FailingStream(), OperationId: operationId), TestContext.Current.CancellationToken);

        Assert.Equal(first.Asset, replay.Asset);
        Assert.Single(journal.Operations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptAsync(new(Guid.NewGuid(), "other.jpg", new MemoryStream([3]), OperationId: operationId), TestContext.Current.CancellationToken));
        string finalPath = Path.Combine(_root, first.Asset.SpoolKey.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(finalPath);
        string partialPath = Path.Combine(_root, $"incoming/{sourceId:N}/{first.Asset.Id:N}.partial".Replace('/', Path.DirectorySeparatorChar));
        File.Delete(partialPath);
        await Assert.ThrowsAsync<IOException>(() => service.AcceptAsync(new(sourceId, "camera.jpg", new MemoryStream([1, 2]), OperationId: operationId), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Fixed_reserve_wins_over_percentage_reserve()
    {
        Directory.CreateDirectory(_root);
        var spool = new FileSystemSpool(new FileSystemSpoolOptions { RootPath = _root, ReservedBytes = long.MaxValue, ReservedPercent = 0 });

        await Assert.ThrowsAsync<IOException>(() => spool.CheckAdmissionAsync(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PostgreSql_journal_and_filesystem_spool_commit_an_asset_together()
    {
        string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
        string databaseName = $"m101_{Guid.NewGuid():N}";
        var testConnection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnection);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        try
        {
            var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(testConnection).Options;
            await using var database = new ShutterSpoolDbContext(options);
            await new DatabaseInitializer(database).InitializeAsync(_root, TestContext.Current.CancellationToken);
            var service = new IngestService(new EfIngestJournal(database), CreateSpool(), TimeProvider.System);

            IngestResult result = await service.AcceptAsync(new IngestRequest(Guid.NewGuid(), "synthetic.jpg", new MemoryStream([7, 8, 9])), TestContext.Current.CancellationToken);
            Guid recoveringAssetId = Guid.NewGuid();
            Guid recoveringSourceId = Guid.NewGuid();
            var recoveringOperation = new IngestOperation(Guid.NewGuid(), recoveringAssetId, recoveringSourceId, "interrupted.jpg", $"incoming/{recoveringSourceId:N}/{recoveringAssetId:N}.partial", IngestOperationState.Receiving);
            var journal = new EfIngestJournal(database);
            var spool = CreateSpool();
            await journal.CreateAsync(recoveringOperation, TestContext.Current.CancellationToken);
            SpoolWrite promoted = await spool.WriteAsync(recoveringSourceId, recoveringAssetId, new MemoryStream([4, 5, 6]), null, TestContext.Current.CancellationToken);
            await journal.MarkPromotedAsync(recoveringOperation.Id, promoted, TestContext.Current.CancellationToken);

            await database.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var count = database.Database.GetDbConnection().CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM assets";
            Assert.Equal(1L, (long)(await count.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            Assert.True(File.Exists(Path.Combine(_root, result.Asset.SpoolKey.Replace('/', Path.DirectorySeparatorChar))));
            await database.DisposeAsync();

            await using var restartedDatabase = new ShutterSpoolDbContext(options);
            await new IngestService(new EfIngestJournal(restartedDatabase), CreateSpool(), TimeProvider.System).RecoverAsync(TestContext.Current.CancellationToken);
            await restartedDatabase.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await using var restartedCount = restartedDatabase.Database.GetDbConnection().CreateCommand();
            restartedCount.CommandText = "SELECT COUNT(*) FROM assets";
            Assert.Equal(2L, (long)(await restartedCount.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);

            Guid duplicateAssetId = Guid.NewGuid();
            var duplicateOperation = new IngestOperation(Guid.NewGuid(), duplicateAssetId, result.Asset.SourceId, "duplicate.jpg", $"incoming/{result.Asset.SourceId:N}/{duplicateAssetId:N}.partial", IngestOperationState.Receiving);
            await using (var failingDatabase = new ShutterSpoolDbContext(options))
            {
                var failingJournal = new EfIngestJournal(failingDatabase);
                await failingJournal.CreateAsync(duplicateOperation, TestContext.Current.CancellationToken);
                await failingJournal.MarkPromotedAsync(duplicateOperation.Id, new SpoolWrite(duplicateOperation.PartialKey, result.Asset.SpoolKey, result.Asset.Size, result.Asset.Sha256), TestContext.Current.CancellationToken);
                await Assert.ThrowsAsync<DbUpdateException>(() => failingJournal.CommitAsync(duplicateOperation.Id, TimeProvider.System.GetUtcNow(), TestContext.Current.CancellationToken));
            }

            await using var inspectedDatabase = new ShutterSpoolDbContext(options);
            IngestOperation stored = Assert.Single(await new EfIngestJournal(inspectedDatabase).GetAllAsync(TestContext.Current.CancellationToken), operation => operation.Id == duplicateOperation.Id);
            Assert.Equal(IngestOperationState.Promoted, stored.State);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private FileSystemSpool CreateSpool() => new(new FileSystemSpoolOptions { RootPath = _root, ReservedBytes = 0 });

    private sealed class OpaqueKeySpool(bool returnDifferentKey = false) : ISpool
    {
        public string GetPartialKey(Guid sourceId, Guid assetId) => "provider-partial-key";
        public Task CheckAdmissionAsync(long? maximumUploadBytes, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<SpoolWrite> WriteAsync(Guid sourceId, Guid assetId, Stream content, long? maximumUploadBytes, CancellationToken cancellationToken) => returnDifferentKey
            ? Task.FromResult(new SpoolWrite("different-key", "final-key", 1, "00"))
            : throw new IOException("Injected transfer failure.");
        public Task<SpoolWrite?> FindFinalAsync(IngestOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task QuarantineAsync(IngestOperation operation, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task QuarantineUntrackedAsync(IReadOnlySet<string> knownKeys, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MemoryJournal : IIngestJournal
    {
        public Dictionary<Guid, IngestOperation> Operations { get; } = [];
        public Dictionary<Guid, IngestResult> Committed { get; } = [];
        public bool FailPromotion { get; set; }
        public HashSet<Guid> RemovalAuthorizedAssetIds { get; } = [];

        public Task<IngestResult?> FindCommittedResultAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Committed.GetValueOrDefault(operationId));

        public Task<IngestOperation?> FindOperationAsync(Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(Operations.GetValueOrDefault(operationId));

        public Task<OriginalRemovalRecovery> GetOriginalRemovalRecoveryAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult(new OriginalRemovalRecovery(RemovalAuthorizedAssetIds.Contains(assetId), []));

        public Task CreateAsync(IngestOperation operation, CancellationToken cancellationToken)
        {
            Operations.Add(operation.Id, operation);
            return Task.CompletedTask;
        }

        public Task MarkPromotedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken)
        {
            if (FailPromotion) throw new IOException("Injected journal failure.");
            Operations[operationId] = Operations[operationId] with { State = IngestOperationState.Promoted, FinalKey = write.FinalKey, Size = write.Size, Sha256 = write.Sha256 };
            return Task.CompletedTask;
        }

        public Task MarkReceivedAsync(Guid operationId, SpoolWrite write, CancellationToken cancellationToken)
        {
            Operations[operationId] = Operations[operationId] with { State = IngestOperationState.Received, FinalKey = write.FinalKey, Size = write.Size, Sha256 = write.Sha256 };
            return Task.CompletedTask;
        }

        public Task CommitAsync(Guid operationId, DateTimeOffset ingestedAt, CancellationToken cancellationToken)
        {
            IngestOperation operation = Operations[operationId];
            Operations[operationId] = operation with { State = IngestOperationState.Committed };
            Committed[operationId] = new(new Asset(operation.AssetId, operation.SourceId, operation.OriginalName,
                operation.FinalKey!, operation.Size!.Value, operation.Sha256!, ingestedAt, operation.Id));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<IngestOperation>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<IngestOperation>>([.. Operations.Values]);

        public Task QuarantineAsync(Guid operationId, string reason, CancellationToken cancellationToken)
        {
            Operations[operationId] = Operations[operationId] with { State = IngestOperationState.Quarantined, QuarantineReason = reason };
            return Task.CompletedTask;
        }
    }

    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Injected stream failure.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("Injected stream failure."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MaxReadStream(int maximumRead) : MemoryStream([1, 2, 3])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length > maximumRead) throw new IOException("Read buffer exceeded its limit.");
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
}
