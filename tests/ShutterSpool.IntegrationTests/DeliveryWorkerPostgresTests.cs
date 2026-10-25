using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShutterSpool.Application.Curation;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Routing;
using ShutterSpool.Application.Spool;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Destinations.Local;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Spool;
using System.Security.Cryptography;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed partial class DeliveryWorkerPostgresTests
{
    private static CaptureRoutingActionService CreateActions(ShutterSpoolDbContext database, ShutterSpool.Application.Spool.ICommittedOriginalReader originals) =>
        new(new CaptureRoutingWorkService(database, TimeProvider.System), new EfVerifiedOriginalSupplier(database, originals));

    [Fact]
    public async Task Local_destination_worker_delivers_verified_original_to_recorded_target()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(fixture.SpoolRoot, "destination");
        Directory.CreateDirectory(root);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        var configuration = new LocalDestinationConfigurationService(database);
        Assert.Equal("INVALID_ROOT", await configuration.ConfigureAsync(fixture.DestinationId, "C:\\" + '\0', token));
        Assert.Null(await configuration.ConfigureAsync(fixture.DestinationId, root, token));

        var processor = NewProcessor(database, fixture.Originals,
            [new LocalDestinationDeliveryAdapter()], TimeProvider.System, new DeliveryWorkerSettings());
        Assert.True(await processor.ProcessOneAsync(token));

        string target = Path.Combine(root, "folder", "synthetic.jpg");
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, "spool", fixture.AssetId.ToString("N")), token),
            await File.ReadAllBytesAsync(target, token));
        Assert.Equal("Delivered", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("ReadBackHash", await TextAsync(database, "SELECT \"ConfirmationEvidence\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("ROOT_LOCKED", await configuration.ConfigureAsync(fixture.DestinationId, Path.Combine(fixture.SpoolRoot, "another"), token));
    }

    [Fact]
    public async Task Resend_claim_renews_and_delivers_after_destination_is_reenabled()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        var initialAdapter = new ControlledAdapter(fixture.DestinationId, Task.CompletedTask);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.True(await NewProcessor(database, fixture.Originals, [initialAdapter], TimeProvider.System,
            new DeliveryWorkerSettings()).ProcessOneAsync(token));
        Assert.Equal("Delivered", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));

        Guid deliveredId = await DeliveryIdAsync(database, fixture.AssetId, token);
        CaptureRoutingActionService actions = CreateActions(database, fixture.Originals);
        RoutingActionPreview reevaluatePreview = await actions.PreviewReevaluateAsync(fixture.AssetId, fixture.DestinationId, token);
        Assert.Equal(RoutingActionStatus.Recorded, (await actions.ReevaluateAsync(fixture.AssetId, fixture.DestinationId,
            reevaluatePreview.ConfirmationToken, token)).Status);
        Assert.Equal("false", await TextAsync(database,
            "SELECT decision.\"IsCurrent\"::text FROM routing_decisions decision JOIN deliveries d ON d.\"DecisionId\" = decision.\"Id\" WHERE d.\"Id\" = (SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NULL)",
            fixture.AssetId, token));

        await new DestinationRoutingAdminService(database).SetAsync(new(fixture.DestinationId, false, []), token);
        RoutingActionPreview preview = await actions.PreviewResendAsync(deliveredId, token);
        Assert.Equal(RoutingActionStatus.Paused, (await actions.ResendAsync(deliveredId, preview.ConfirmationToken, token)).Status);
        await new DestinationRoutingAdminService(database).SetAsync(new(fixture.DestinationId, true, []), token);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resendAdapter = new ControlledAdapter(fixture.DestinationId, gate.Task)
        {
            WriteResult = _ => new(DeliveryResultKind.Uncertain, ErrorCode: "SYNTHETIC_UNCERTAIN"),
            Reconciliation = new(DeliveryReconciliationKind.NotPublished, SafeToRepeat: true),
        };
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 30, LeaseRenewalSeconds = 1, ScanIntervalSeconds = 1 };
        Task<bool> worker = NewProcessor(database, fixture.Originals, [resendAdapter], TimeProvider.System, settings).ProcessOneAsync(token);
        try
        {
            await resendAdapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            DateTimeOffset initialLease = await database.Deliveries.AsNoTracking()
                .Where(item => item.AssetId == fixture.AssetId && item.RepeatsDeliveryId != null)
                .Select(item => item.LeaseUntil!.Value).SingleAsync(token);
            using var renewalDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            renewalDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            while (!await database.Deliveries.AsNoTracking().AnyAsync(item => item.AssetId == fixture.AssetId
                && item.RepeatsDeliveryId != null && item.LeaseUntil > initialLease, renewalDeadline.Token))
                await Task.Delay(20, renewalDeadline.Token);
            Assert.False(await NewProcessor(database, fixture.Originals, [resendAdapter], TimeProvider.System, settings).ProcessOneAsync(token));

            gate.SetResult();
            Assert.True(await worker);
            Assert.Equal("Ambiguous", await TextAsync(database,
                "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NOT NULL", fixture.AssetId, token));
            Assert.True(await NewProcessor(database, fixture.Originals, [resendAdapter], TimeProvider.System, settings).ProcessOneAsync(token));
            Assert.Equal("Pending", await TextAsync(database,
                "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NOT NULL", fixture.AssetId, token));
            Assert.Equal("NotPublishedAfterRecovery", await TextAsync(database,
                "SELECT attempt.\"Result\" FROM delivery_attempts attempt JOIN deliveries d ON d.\"Id\" = attempt.\"DeliveryId\" WHERE d.\"AssetId\" = @asset AND d.\"RepeatsDeliveryId\" IS NOT NULL",
                fixture.AssetId, token));

            var finalAdapter = new ControlledAdapter(fixture.DestinationId, Task.CompletedTask);
            Assert.True(await NewProcessor(database, fixture.Originals, [finalAdapter], TimeProvider.System, settings).ProcessOneAsync(token));
            Assert.Equal("Delivered", await TextAsync(database,
                "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NOT NULL", fixture.AssetId, token));
            Assert.Equal("AcknowledgedCompleteWrite", await TextAsync(database,
                "SELECT \"ConfirmationEvidence\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NOT NULL", fixture.AssetId, token));
            Assert.Equal(initialAdapter.WrittenBytes, resendAdapter.WrittenBytes);
            Assert.Equal(initialAdapter.WrittenBytes, finalAdapter.WrittenBytes);
            Assert.Equal(2, await ScalarAsync(database,
                "SELECT count(*) FROM delivery_attempts attempt JOIN deliveries d ON d.\"Id\" = attempt.\"DeliveryId\" WHERE d.\"AssetId\" = @asset AND d.\"RepeatsDeliveryId\" IS NOT NULL",
                fixture.AssetId, token));
            Assert.Equal(1, await ScalarAsync(database,
                "SELECT count(*) FROM routing_actions WHERE \"Kind\" = 'Resend' AND \"DeliveryId\" = (SELECT \"Id\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NOT NULL)",
                fixture.AssetId, token));
        }
        finally
        {
            gate.TrySetResult();
            if (!worker.IsCompleted) await worker;
        }
    }

    [Fact]
    public async Task Reevaluate_during_historical_resend_keeps_positive_write_confirmation()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        var actions = CreateActions(database, fixture.Originals);
        Assert.True(await NewProcessor(database, fixture.Originals,
            [new ControlledAdapter(fixture.DestinationId, Task.CompletedTask)], TimeProvider.System,
            new DeliveryWorkerSettings()).ProcessOneAsync(token));
        Guid deliveredId = await DeliveryIdAsync(database, fixture.AssetId, token);
        RoutingActionPreview oldRoute = await actions.PreviewReevaluateAsync(fixture.AssetId, fixture.DestinationId, token);
        Assert.Equal(RoutingActionStatus.Recorded, (await actions.ReevaluateAsync(fixture.AssetId, fixture.DestinationId,
            oldRoute.ConfirmationToken, token)).Status);
        RoutingActionPreview resendPreview = await actions.PreviewResendAsync(deliveredId, token);
        Assert.Equal(RoutingActionStatus.Queued, (await actions.ResendAsync(deliveredId, resendPreview.ConfirmationToken, token)).Status);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ControlledAdapter(fixture.DestinationId, gate.Task, ignoreCancellation: true);
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 30, LeaseRenewalSeconds = 1, ScanIntervalSeconds = 1 };
        Task<bool> worker = NewProcessor(database, fixture.Originals, [adapter], TimeProvider.System, settings).ProcessOneAsync(token);
        try
        {
            await adapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await UseDifferentTargetAsync(fixture, token);
            RoutingActionPreview changedRoute = await actions.PreviewReevaluateAsync(fixture.AssetId, fixture.DestinationId, token);
            Assert.Equal("changed-folder/synthetic.jpg", changedRoute.ProposedTarget);
            Assert.Equal(RoutingActionStatus.Recorded, (await actions.ReevaluateAsync(fixture.AssetId, fixture.DestinationId,
                changedRoute.ConfirmationToken, token)).Status);
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            Assert.True(adapter.WriteToken.IsCancellationRequested);
            gate.SetResult();
            Assert.True(await worker);
            Assert.Equal("Delivered", await TextAsync(database,
                "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NOT NULL", fixture.AssetId, token));
            Assert.Equal("Confirmed", await TextAsync(database,
                "SELECT attempt.\"Result\" FROM delivery_attempts attempt JOIN deliveries d ON d.\"Id\" = attempt.\"DeliveryId\" WHERE d.\"AssetId\" = @asset AND d.\"RepeatsDeliveryId\" IS NOT NULL",
                fixture.AssetId, token));
            Assert.Equal("Pending", await TextAsync(database,
                "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"RepeatsDeliveryId\" IS NULL AND \"State\" = 'Pending'",
                fixture.AssetId, token));
        }
        finally
        {
            gate.TrySetResult();
            if (!worker.IsCompleted) await worker;
        }
    }

    [Fact]
    public async Task Local_destination_conflict_fails_without_changing_existing_file()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(fixture.SpoolRoot, "destination");
        string folder = Path.Combine(root, "folder");
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "synthetic.jpg");
        byte[] existing = [8, 8, 8];
        await File.WriteAllBytesAsync(target, existing, token);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.Null(await new LocalDestinationConfigurationService(database).ConfigureAsync(fixture.DestinationId, root, token));

        Assert.True(await NewProcessor(database, fixture.Originals,
            [new LocalDestinationDeliveryAdapter()], TimeProvider.System, new DeliveryWorkerSettings()).ProcessOneAsync(token));

        Assert.Equal("Failed", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("TARGET_CONFLICT", await TextAsync(database, "SELECT \"ErrorCode\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(existing, await File.ReadAllBytesAsync(target, token));
        Assert.Empty(Directory.GetFiles(folder, "*.partial"));
    }

    [Fact]
    public async Task Missing_mount_marker_blocks_repeat_until_reconciliation_after_recovery()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(fixture.SpoolRoot, "destination");
        Directory.CreateDirectory(root);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.Null(await new LocalDestinationConfigurationService(database).ConfigureAsync(fixture.DestinationId, root, token));
        string marker = Path.Combine(root, $".shutterspool-{fixture.DestinationId:N}.root");
        File.Delete(marker);
        Assert.Equal("MOUNT_IDENTITY_UNKNOWN", await new LocalDestinationConfigurationService(database)
            .ConfigureAsync(fixture.DestinationId, root, token));
        var settings = new DeliveryWorkerSettings();
        var adapter = new LocalDestinationDeliveryAdapter();

        Assert.True(await NewProcessor(database, fixture.Originals, [adapter], TimeProvider.System, settings).ProcessOneAsync(token));
        Assert.Equal("Ambiguous", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.False(File.Exists(Path.Combine(root, "folder", "synthetic.jpg")));

        LocalFileDelivery.ProvisionRoot(root, fixture.DestinationId);
        Assert.True(await NewProcessor(database, fixture.Originals, [adapter], TimeProvider.System, settings).ProcessOneAsync(token));
        Assert.Equal("Pending", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.True(await NewProcessor(database, fixture.Originals, [adapter], TimeProvider.System, settings).ProcessOneAsync(token));
        Assert.Equal("Delivered", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(2, await ScalarAsync(database, "SELECT count(*) FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
    }

    [Fact]
    public async Task Expired_local_attempt_after_publication_confirms_without_a_second_write()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(fixture.SpoolRoot, "destination");
        Directory.CreateDirectory(root);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.Null(await new LocalDestinationConfigurationService(database).ConfigureAsync(fixture.DestinationId, root, token));
        Guid deliveryId = await DeliveryIdAsync(database, fixture.AssetId, token);
        Guid attemptId = Guid.NewGuid();
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, "spool", fixture.AssetId.ToString("N")), token);
        var request = new DeliveryRequest(deliveryId, attemptId, fixture.AssetId, fixture.DestinationId,
            "folder/synthetic.jpg", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), "synthetic");
        await database.Database.ExecuteSqlInterpolatedAsync($"UPDATE deliveries SET \"State\" = 'Running', \"AttemptCount\" = 1, \"LeaseId\" = {Guid.NewGuid()}, \"LeaseUntil\" = {DateTimeOffset.UtcNow.AddMinutes(-1)} WHERE \"Id\" = {deliveryId}", token);
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO delivery_attempts ("Id", "DeliveryId", "AssetId", "RetryNumber", "WorkerId", "ClaimToken", "StartedAt", "TraceId")
            VALUES ({attemptId}, {deliveryId}, {fixture.AssetId}, 1, 'crashed-worker', {Guid.NewGuid()}, {DateTimeOffset.UtcNow.AddMinutes(-2)}, 'synthetic')
            """, token);
        Assert.Equal(DeliveryResultKind.Confirmed,
            (await LocalFileDelivery.WriteAsync(root, request, new MemoryStream(bytes), token)).Kind);

        Assert.True(await NewProcessor(database, fixture.Originals,
            [new LocalDestinationDeliveryAdapter()], TimeProvider.System, new DeliveryWorkerSettings()).ProcessOneAsync(token));

        Assert.Equal("Delivered", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("ConfirmedAfterRecovery", await TextAsync(database, "SELECT \"Result\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "folder", "synthetic.jpg"), token));
    }

    [Fact]
    public async Task Concurrent_workers_claim_once_and_persist_positive_confirmation()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ControlledAdapter(fixture.DestinationId, gate.Task);
        var clock = TimeProvider.System;
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 2, ScanIntervalSeconds = 1 };
        await using var firstDb = new ShutterSpoolDbContext(fixture.DbOptions);
        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<bool> first = NewProcessor(firstDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(workerCancellation.Token);
        Exception? testFailure = null;
        try
        {
            await adapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            DateTimeOffset initialLease = await LeaseUntilAsync(firstDb, fixture.AssetId, token);
            DateTimeOffset? renewedLease = null;
            DateTimeOffset renewalDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < renewalDeadline)
            {
                DateTimeOffset currentLease = await LeaseUntilAsync(firstDb, fixture.AssetId, token);
                if (currentLease > initialLease)
                {
                    renewedLease = currentLease;
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            }
            Assert.True(renewedLease > initialLease,
                "The delivery lease must advance while the destination write is still held open.");

            bool second;
            await using (var secondDb = new ShutterSpoolDbContext(fixture.DbOptions))
                second = await NewProcessor(secondDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token);
            Assert.False(second);

            gate.SetResult();
            Assert.True(await first);
            await using var verify = new ShutterSpoolDbContext(fixture.DbOptions);
            Assert.Equal("Delivered", await TextAsync(verify, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
            Assert.Equal("AcknowledgedCompleteWrite", await TextAsync(verify, "SELECT \"ConfirmationEvidence\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
            Assert.Equal("Confirmed", await TextAsync(verify, "SELECT \"Result\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
            Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
            Assert.Equal("COMPLETE", await TextAsync(verify, "SELECT \"State\" FROM captures WHERE \"Id\" = @capture", fixture.CaptureId, token, fixture.CaptureId));
            var history = new CaptureRoutingHistoryService(new CaptureRoutingHistoryStore(verify));
            RoutingDeliveryHistory delivery = Assert.Single((await history.GetDeliveriesAsync(fixture.CaptureId, new(), token))!.Items);
            Assert.Equal("AcknowledgedCompleteWrite", Assert.Single((await history.GetAttemptsAsync(delivery.Id, new(), token))!.Items).ConfirmationEvidence);
            Assert.Equal(1, adapter.WriteCount);
        }
        catch (Exception exception)
        {
            testFailure = exception;
            throw;
        }
        finally
        {
            gate.TrySetResult();
            if (testFailure is not null) workerCancellation.Cancel();
            try { await first; }
            catch when (testFailure is not null) { }
        }
    }

    [Fact]
    public async Task Claim_serializes_with_reevaluation_before_it_can_supersede_work()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        await UseDifferentTargetAsync(fixture, token);
        RoutingActionPreview preview;
        await using (var previewDb = new ShutterSpoolDbContext(fixture.DbOptions))
            preview = await CreateActions(previewDb, fixture.Originals)
                .PreviewReevaluateAsync(fixture.AssetId, fixture.DestinationId, token);

        await using var workerDb = new ShutterSpoolDbContext(fixture.DbOptions);
        await workerDb.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION m107_delay_attempt() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN PERFORM pg_advisory_xact_lock(hashtextextended('m107-attempt-barrier', 0)); RETURN NEW; END $$;
            CREATE TRIGGER m107_delay_attempt BEFORE INSERT ON delivery_attempts
                FOR EACH ROW EXECUTE FUNCTION m107_delay_attempt();
            """, token);
        await using var attemptBlocker = new ShutterSpoolDbContext(fixture.DbOptions);
        await using var attemptBarrier = await attemptBlocker.Database.BeginTransactionAsync(token);
        await attemptBlocker.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('m107-attempt-barrier', 0))", token);
        bool barrierReleased = false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ControlledAdapter(fixture.DestinationId, gate.Task);
        Task<bool> worker = NewProcessor(workerDb, fixture.Originals, [adapter], TimeProvider.System,
            new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 2, ScanIntervalSeconds = 1 }).ProcessOneAsync(token);
        try
        {
            Assert.True(await WaitForRoutingLockAsync(fixture.DbOptions, token), "Claim did not hold the routing lock while it persisted the attempt.");

            await using var routingDb = new ShutterSpoolDbContext(fixture.DbOptions);
            Task<RoutingActionPreview> reevaluate = CreateActions(routingDb, fixture.Originals)
                .ReevaluateAsync(fixture.AssetId, fixture.DestinationId, preview.ConfirmationToken, token);
            await Task.Delay(100, token);
            Assert.False(reevaluate.IsCompleted);

            await attemptBarrier.RollbackAsync(token);
            barrierReleased = true;
            await adapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(RoutingActionStatus.Recorded, (await reevaluate.WaitAsync(TimeSpan.FromSeconds(5), token)).Status);
            Assert.Equal("Running", await TextAsync(routingDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Running'", fixture.AssetId, token));
            Assert.Equal("false", await TextAsync(routingDb, "SELECT decision.\"IsCurrent\"::text FROM routing_decisions decision JOIN deliveries d ON d.\"DecisionId\" = decision.\"Id\" WHERE d.\"AssetId\" = @asset AND d.\"State\" = 'Running'", fixture.AssetId, token));
            gate.SetResult();
            Assert.True(await worker);
            Assert.Equal("Delivered", await TextAsync(routingDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Delivered'", fixture.AssetId, token));
        }
        finally
        {
            if (!barrierReleased) await attemptBarrier.RollbackAsync(CancellationToken.None);
            gate.TrySetResult();
            if (!worker.IsCompleted) await worker;
        }
    }

    [Fact]
    public async Task Expired_lease_with_live_write_stays_ambiguous_without_safe_reconciliation()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ControlledAdapter(fixture.DestinationId, gate.Task)
        {
            Reconciliation = new(DeliveryReconciliationKind.NotPublished),
        };
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 10, ScanIntervalSeconds = 1 };
        await using var firstDb = new ShutterSpoolDbContext(fixture.DbOptions);
        Task<bool> first = NewProcessor(firstDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token);
        await adapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        clock.Advance(TimeSpan.FromSeconds(16));

        await using (var recoveringDb = new ShutterSpoolDbContext(fixture.DbOptions))
            Assert.True(await NewProcessor(recoveringDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token));

        gate.SetResult();
        Assert.True(await first);
        await using var verify = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.Equal("Ambiguous", await TextAsync(verify, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("ReconciliationUncertain", await TextAsync(verify,
            "SELECT \"Result\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(1, await ScalarAsync(verify, "SELECT count(*) FROM delivery_attempts WHERE \"AssetId\" = @asset AND \"FinishedAt\" IS NOT NULL", fixture.AssetId, token));
        Assert.Equal(1, adapter.WriteCount);
        Assert.Equal(1, adapter.ReconcileCount);
    }

    [Fact]
    public async Task Transient_failure_waits_for_due_time_and_explicit_retry_is_idempotent()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        var adapter = new ControlledAdapter(fixture.DestinationId, Task.CompletedTask)
        {
            WriteResult = _ => new(DeliveryResultKind.TransientFailure, ErrorCode: "RATE_LIMITED", ErrorMessage: "password=hidden"),
        };
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 2, ScanIntervalSeconds = 1 };
        await using (var database = new ShutterSpoolDbContext(fixture.DbOptions))
            Assert.True(await NewProcessor(database, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token));

        await using var retryDb = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.Equal("RetryWait", await TextAsync(retryDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        DateTimeOffset dueAt = await retryDb.Database.SqlQuery<DateTimeOffset>(
            $"SELECT \"NextAttemptAt\" AS \"Value\" FROM deliveries WHERE \"AssetId\" = {fixture.AssetId}").SingleAsync(token);
        Assert.InRange(dueAt, clock.GetUtcNow().AddSeconds(9), clock.GetUtcNow().AddSeconds(11));
        Assert.Equal("The destination reported a temporary failure.", await TextAsync(retryDb,
            "SELECT \"ErrorMessage\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));

        adapter.WriteResult = _ => new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite);
        CaptureRoutingActionService work = CreateActions(retryDb, fixture.Originals);
        Assert.Equal(RoutingActionStatus.Queued, (await work.RetryAsync(await DeliveryIdAsync(retryDb, fixture.AssetId, token), token)).Status);
        Assert.Equal(RoutingActionStatus.AlreadyQueued, (await work.RetryAsync(await DeliveryIdAsync(retryDb, fixture.AssetId, token), token)).Status);
        Assert.Equal(1, await ScalarAsync(retryDb, "SELECT count(*) FROM routing_actions WHERE \"Kind\" = 'Retry' AND \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.True(await NewProcessor(retryDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token));
        Assert.Equal("Delivered", await TextAsync(retryDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(2, adapter.WriteCount);
    }

    [Fact]
    public async Task Expired_claim_reconciles_confirmed_write_without_sending_again()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        using var crashed = new CancellationTokenSource();
        var adapter = new ControlledAdapter(fixture.DestinationId, Task.CompletedTask)
        {
            OnWrite = () => crashed.Cancel(),
            Reconciliation = new(DeliveryReconciliationKind.Confirmed, DeliveryConfirmationEvidenceLevel.ProviderReceipt),
        };
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 2, ScanIntervalSeconds = 1 };
        await using (var firstDb = new ShutterSpoolDbContext(fixture.DbOptions))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewProcessor(firstDb, fixture.Originals, [adapter], clock, settings)
                .ProcessOneAsync(crashed.Token));
        clock.Advance(TimeSpan.FromSeconds(16));
        await using var recoveringDb = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.True(await NewProcessor(recoveringDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Delivered", await TextAsync(recoveringDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, TestContext.Current.CancellationToken));
        Assert.Equal("ConfirmedAfterRecovery", await TextAsync(recoveringDb, "SELECT \"Result\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, TestContext.Current.CancellationToken));
        Assert.Equal("ProviderReceipt", await TextAsync(recoveringDb, "SELECT \"ConfirmationEvidence\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, TestContext.Current.CancellationToken));
        Assert.Equal(1, adapter.WriteCount);
        Assert.Equal(1, adapter.ReconcileCount);
    }

    [Fact]
    public async Task Direct_uncertain_write_is_reconciled_once_and_finishes_its_attempt()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        var adapter = new ControlledAdapter(fixture.DestinationId, Task.CompletedTask)
        {
            WriteResult = _ => new(DeliveryResultKind.Uncertain, ErrorCode: "WRITE_OUTCOME_UNKNOWN"),
            Reconciliation = new(DeliveryReconciliationKind.Uncertain, ErrorCode: "STILL_UNKNOWN"),
        };
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        var processor = NewProcessor(database, fixture.Originals, [adapter], TimeProvider.System,
            new DeliveryWorkerSettings());

        Assert.True(await processor.ProcessOneAsync(token));
        Assert.Equal("Ambiguous", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("Uncertain", await TextAsync(database, "SELECT \"Result\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));

        Assert.True(await processor.ProcessOneAsync(token));
        Assert.Equal("Ambiguous", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("ReconciliationUncertain", await TextAsync(database, "SELECT \"Result\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM delivery_attempts WHERE \"AssetId\" = @asset AND \"FinishedAt\" IS NOT NULL", fixture.AssetId, token));
        Assert.False(await processor.ProcessOneAsync(token));
        Assert.Equal(1, adapter.WriteCount);
        Assert.Equal(1, adapter.ReconcileCount);
    }

    [Fact]
    public async Task Safe_not_published_reconciliation_finishes_old_attempt_before_retry()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        using var crashed = new CancellationTokenSource();
        var adapter = new ControlledAdapter(fixture.DestinationId, Task.CompletedTask)
        {
            Reconciliation = new(DeliveryReconciliationKind.NotPublished, SafeToRepeat: true),
            WriteResult = number => number == 1
                ? new(DeliveryResultKind.Uncertain, ErrorCode: "WRITE_INTERRUPTED")
                : new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite),
        };
        adapter.OnWrite = () => { if (adapter.WriteCount == 1) crashed.Cancel(); };
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 2, ScanIntervalSeconds = 1 };
        await using (var firstDb = new ShutterSpoolDbContext(fixture.DbOptions))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NewProcessor(firstDb, fixture.Originals, [adapter], clock, settings)
                .ProcessOneAsync(crashed.Token));

        clock.Advance(TimeSpan.FromSeconds(16));
        await using var recoveryDb = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.True(await NewProcessor(recoveryDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token));
        Assert.Equal("Pending", await TextAsync(recoveryDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal("NotPublishedAfterRecovery", await TextAsync(recoveryDb, "SELECT \"Result\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.True(await NewProcessor(recoveryDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token));
        Assert.Equal("Delivered", await TextAsync(recoveryDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(2, await ScalarAsync(recoveryDb, "SELECT count(*) FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, token));
        Assert.Equal(2, adapter.WriteCount);
    }

    [Fact]
    public async Task Disable_cancels_write_and_requires_safe_reconciliation_before_resume()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ControlledAdapter(fixture.DestinationId, gate.Task)
        {
            Reconciliation = new(DeliveryReconciliationKind.NotPublished, SafeToRepeat: true),
            WriteResult = _ => new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite),
        };
        var clock = TimeProvider.System;
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 1, ScanIntervalSeconds = 1 };
        await using var firstDb = new ShutterSpoolDbContext(fixture.DbOptions);
        Task<bool> first = NewProcessor(firstDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token);
        try
        {
            await adapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            await using (var disableDb = new ShutterSpoolDbContext(fixture.DbOptions))
                await new DestinationRoutingAdminService(disableDb).SetAsync(new(fixture.DestinationId, false, []), token);
            Assert.Equal("false", await TextAsync(firstDb, "SELECT \"Enabled\"::text FROM destinations WHERE \"Id\" = (SELECT \"DestinationId\" FROM deliveries WHERE \"AssetId\" = @asset)", fixture.AssetId, token));
            await WaitForCancellationAsync(adapter.WriteToken, token);
            Assert.True(adapter.WriteToken.IsCancellationRequested, "Renewal did not cancel the adapter token after destination disable.");
            Assert.True(await first);
            Assert.Equal("Ambiguous", await TextAsync(firstDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));

            Assert.True(await NewProcessor(firstDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token));
            Assert.Equal("Disabled", await TextAsync(firstDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
            Assert.Equal(1, adapter.WriteCount);

            await using (var enableDb = new ShutterSpoolDbContext(fixture.DbOptions))
                await new DestinationRoutingAdminService(enableDb).SetAsync(new(fixture.DestinationId, true, []), token);
            gate.SetResult();
            Assert.True(await NewProcessor(firstDb, fixture.Originals, [adapter], clock, settings).ProcessOneAsync(token));
            Assert.Equal("Delivered", await TextAsync(firstDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId, token));
            Assert.Equal(2, adapter.WriteCount);
        }
        finally
        {
            gate.TrySetResult();
            if (!first.IsCompleted) await first;
        }
    }

    [Fact]
    public async Task Reevaluate_during_write_cancels_it_and_blocks_new_write_while_ambiguous()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        await UseDifferentTargetAsync(fixture, token);
        RoutingActionPreview preview;
        await using (var previewDb = new ShutterSpoolDbContext(fixture.DbOptions))
            preview = await CreateActions(previewDb, fixture.Originals)
                .PreviewReevaluateAsync(fixture.AssetId, fixture.DestinationId, token);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ControlledAdapter(fixture.DestinationId, gate.Task);
        var settings = new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 1, ScanIntervalSeconds = 1 };
        await using var workerDb = new ShutterSpoolDbContext(fixture.DbOptions);
        Task<bool> worker = NewProcessor(workerDb, fixture.Originals, [adapter], TimeProvider.System, settings).ProcessOneAsync(token);
        try
        {
            await adapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

            await using var routingDb = new ShutterSpoolDbContext(fixture.DbOptions);
            RoutingActionPreview result = await CreateActions(routingDb, fixture.Originals)
                .ReevaluateAsync(fixture.AssetId, fixture.DestinationId, preview.ConfirmationToken, token);
            Assert.Equal(RoutingActionStatus.Recorded, result.Status);
            Assert.Equal("false", await TextAsync(routingDb, "SELECT decision.\"IsCurrent\"::text FROM routing_decisions decision JOIN deliveries d ON d.\"DecisionId\" = decision.\"Id\" WHERE d.\"AssetId\" = @asset AND d.\"State\" = 'Running'", fixture.AssetId, token));
            await WaitForCancellationAsync(adapter.WriteToken, token);
            Assert.True(adapter.WriteToken.IsCancellationRequested, "Renewal did not cancel the adapter token after the decision became noncurrent.");
            Assert.True(await worker);
            await using var recoveryDb = new ShutterSpoolDbContext(fixture.DbOptions);
            Assert.Equal("Ambiguous", await TextAsync(recoveryDb, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Ambiguous'", fixture.AssetId, token));
            Assert.Equal(1, await ScalarAsync(recoveryDb, "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Pending'", fixture.AssetId, token));

            Assert.True(await NewProcessor(recoveryDb, fixture.Originals, [adapter], TimeProvider.System, settings).ProcessOneAsync(token));
            Assert.Equal(1, adapter.WriteCount);
            Assert.Equal(1, adapter.ReconcileCount);
            Assert.Equal("ReconciliationUncertain", await TextAsync(recoveryDb,
                "SELECT attempt.\"Result\" FROM delivery_attempts attempt JOIN deliveries d ON d.\"Id\" = attempt.\"DeliveryId\" WHERE d.\"AssetId\" = @asset AND d.\"State\" = 'Ambiguous'", fixture.AssetId, token));
            Assert.Equal(0, await ScalarAsync(recoveryDb,
                "SELECT count(*) FROM deliveries d WHERE d.\"AssetId\" = @asset AND d.\"State\" = 'Pending' AND NOT EXISTS (SELECT 1 FROM deliveries active WHERE active.\"AssetId\" = d.\"AssetId\" AND active.\"DestinationId\" = d.\"DestinationId\" AND active.\"Id\" <> d.\"Id\" AND active.\"State\" IN ('Running', 'Ambiguous'))",
                fixture.AssetId, token));
            Assert.False(await NewProcessor(recoveryDb, fixture.Originals, [adapter], TimeProvider.System, settings).ProcessOneAsync(token));
        }
        finally
        {
            gate.TrySetResult();
            if (!worker.IsCompleted) await worker;
        }
    }

    [Fact]
    public async Task Reevaluate_during_write_blocks_same_target_until_remote_outcome_is_known()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        CancellationToken token = TestContext.Current.CancellationToken;
        RoutingActionPreview preview;
        await using (var previewDb = new ShutterSpoolDbContext(fixture.DbOptions))
            preview = await CreateActions(previewDb, fixture.Originals)
                .PreviewReevaluateAsync(fixture.AssetId, fixture.DestinationId, token);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adapter = new ControlledAdapter(fixture.DestinationId, gate.Task);
        await using var workerDb = new ShutterSpoolDbContext(fixture.DbOptions);
        Task<bool> worker = NewProcessor(workerDb, fixture.Originals, [adapter], TimeProvider.System,
            new DeliveryWorkerSettings { LeaseDurationSeconds = 15, LeaseRenewalSeconds = 1, ScanIntervalSeconds = 1 }).ProcessOneAsync(token);
        try
        {
            await adapter.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);

            await using var routingDb = new ShutterSpoolDbContext(fixture.DbOptions);
            RoutingActionPreview result = await CreateActions(routingDb, fixture.Originals)
                .ReevaluateAsync(fixture.AssetId, fixture.DestinationId, preview.ConfirmationToken, token);
            Assert.Equal(RoutingActionStatus.BlockedReconciliation, result.Status);
            Assert.Equal(1, await ScalarAsync(routingDb,
                "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Running'", fixture.AssetId, token));
            Assert.Equal(0, await ScalarAsync(routingDb,
                "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Pending'", fixture.AssetId, token));

            gate.SetResult();
            Assert.True(await worker);
            Assert.Equal(1, adapter.WriteCount);
            Assert.Equal(1, await ScalarAsync(routingDb,
                "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Delivered'", fixture.AssetId, token));
            Assert.Equal(0, await ScalarAsync(routingDb,
                "SELECT count(*) FROM deliveries WHERE \"AssetId\" = @asset AND \"State\" = 'Pending'", fixture.AssetId, token));
        }
        finally
        {
            gate.TrySetResult();
            if (!worker.IsCompleted) await worker;
        }
    }

    private static async Task UseDifferentTargetAsync(TestDatabase fixture, CancellationToken token)
    {
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        await new DestinationRoutingAdminService(database).SetAsync(new(fixture.DestinationId, true,
            [new("all", "All", 0, true, [], "changed-folder", "{suppliedName}")]), token);
    }

    private static async Task<bool> WaitForRoutingLockAsync(DbContextOptions<ShutterSpoolDbContext> options, CancellationToken token)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await using var database = new ShutterSpoolDbContext(options);
            bool acquired = await database.Database.SqlQuery<bool>(
                $"SELECT pg_try_advisory_xact_lock(hashtextextended('shutterspool-routing', 0)) AS \"Value\"").SingleAsync(token);
            if (!acquired) return true;
            await Task.Delay(20, token);
        }
        return false;
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellation, CancellationToken token)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = cancellation.Register(() => canceled.TrySetResult());
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
    }
    [Fact]
    public async Task Missing_adapter_records_visible_configuration_failure()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        var processor = NewProcessor(database, fixture.Originals, [],
            TimeProvider.System, new DeliveryWorkerSettings());
        Assert.True(await processor.ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Failed", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await ScalarAsync(database, "SELECT count(*) FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId,
            TestContext.Current.CancellationToken));
        Assert.Equal("DESTINATION_KIND_UNSUPPORTED", await TextAsync(database,
            "SELECT \"ErrorCode\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_verified_original_fails_before_destination_io()
    {
        await using TestDatabase fixture = await TestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        File.Delete(Path.Combine(fixture.SpoolRoot, $"spool/{fixture.AssetId:N}"));
        var adapter = new ControlledAdapter(fixture.DestinationId, Task.CompletedTask);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        Assert.True(await NewProcessor(database, fixture.Originals, [adapter], TimeProvider.System,
            new DeliveryWorkerSettings()).ProcessOneAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Failed", await TextAsync(database, "SELECT \"State\" FROM deliveries WHERE \"AssetId\" = @asset", fixture.AssetId,
            TestContext.Current.CancellationToken));
        Assert.Equal(0, adapter.WriteCount);
        Assert.Equal("ORIGINAL_UNAVAILABLE", await TextAsync(database,
            "SELECT \"ErrorCode\" FROM delivery_attempts WHERE \"AssetId\" = @asset", fixture.AssetId, TestContext.Current.CancellationToken));
    }

    private static async Task<Guid> DeliveryIdAsync(ShutterSpoolDbContext database, Guid assetId, CancellationToken token) =>
        await database.Database.SqlQuery<Guid>($"SELECT \"Id\" AS \"Value\" FROM deliveries WHERE \"AssetId\" = {assetId}").SingleAsync(token);

    private static async Task<DateTimeOffset> LeaseUntilAsync(ShutterSpoolDbContext database, Guid assetId,
        CancellationToken token) => await database.Database.SqlQuery<DateTimeOffset>(
            $"SELECT \"LeaseUntil\" AS \"Value\" FROM deliveries WHERE \"AssetId\" = {assetId}")
        .SingleAsync(token);

    private static async Task<int> ScalarAsync(ShutterSpoolDbContext database, string sql, Guid assetId, CancellationToken token, Guid? captureId = null)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        Add("asset", assetId);
        if (captureId is Guid capture) Add("capture", capture);
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(token);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
        void Add(string name, Guid id) { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = id; command.Parameters.Add(parameter); }
    }

    private static async Task<string> TextAsync(ShutterSpoolDbContext database, string sql, Guid assetId, CancellationToken token, Guid? captureId = null)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var asset = command.CreateParameter(); asset.ParameterName = "asset"; asset.Value = assetId; command.Parameters.Add(asset);
        if (captureId is Guid capture) { var parameter = command.CreateParameter(); parameter.ParameterName = "capture"; parameter.Value = capture; command.Parameters.Add(parameter); }
        if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(token);
        return Convert.ToString(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static DeliveryProcessingService NewProcessor(ShutterSpoolDbContext database, ICommittedOriginalReader originals,
        IEnumerable<IDestinationDeliveryAdapter> adapters, TimeProvider clock, DeliveryWorkerSettings settings)
    {
        var options = new DbContextOptionsBuilder<ShutterSpoolDbContext>()
            .UseNpgsql(database.Database.GetConnectionString()).Options;
        var protector = new ShutterSpool.Infrastructure.Security.SecretProtector(Microsoft.Extensions.Options.Options.Create(
            new ShutterSpool.Infrastructure.Security.CredentialProtectionOptions
            { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        return new(new EfDeliveryWorkProcessor(new TestDbFactory(options), clock, settings, protector), originals,
            new DeliveryAdapterRegistry(adapters), settings, clock);
    }

    private sealed class TestDbFactory(DbContextOptions<ShutterSpoolDbContext> options) : IDbContextFactory<ShutterSpoolDbContext>
    {
        public ShutterSpoolDbContext CreateDbContext() => new(options);
    }

    private sealed class ControlledAdapter(Guid destinationId, Task writeGate, bool ignoreCancellation = false) : IDestinationDeliveryAdapter
    {
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken WriteToken { get; private set; }
        public int WriteCount { get; private set; }
        public int ReconcileCount { get; private set; }
        public byte[]? WrittenBytes { get; private set; }
        public DeliveryReconciliation Reconciliation { get; set; } = new(DeliveryReconciliationKind.Uncertain);
        public Func<int, DeliveryResult> WriteResult { get; set; } = _ => new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite);
        public Action? OnWrite { get; set; }
        public string Kind => "local";

        public async Task<DeliveryResult> WriteAsync(DeliveryRequest request, Stream original, CancellationToken cancellationToken)
        {
            Assert.Equal(destinationId, request.DestinationId);
            WriteCount++;
            WriteToken = cancellationToken;
            WriteStarted.TrySetResult();
            using var bytes = new MemoryStream();
            await original.CopyToAsync(bytes, cancellationToken);
            WrittenBytes = bytes.ToArray();
            if (ignoreCancellation) await writeGate;
            else await writeGate.WaitAsync(cancellationToken);
            OnWrite?.Invoke();
            return WriteResult(WriteCount);
        }

        public Task<DeliveryReconciliation> ReconcileAsync(DeliveryRequest request, CancellationToken cancellationToken)
        {
            ReconcileCount++;
            return Task.FromResult(Reconciliation);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan duration) => current += duration;
    }

    private sealed class TestDatabase(string databaseName, NpgsqlConnection admin, string spoolRoot,
        DbContextOptions<ShutterSpoolDbContext> ingestOptions, Guid destinationId, Guid captureId, Guid assetId,
        ICommittedOriginalReader originals) : IAsyncDisposable
    {
        public Guid DestinationId { get; } = destinationId;
        public Guid CaptureId { get; } = captureId;
        public Guid AssetId { get; } = assetId;
        public string SpoolRoot { get; } = spoolRoot;
        public DbContextOptions<ShutterSpoolDbContext> DbOptions { get; } = ingestOptions;
        public ICommittedOriginalReader Originals { get; } = originals;

        public static async Task<TestDatabase> CreateAsync(CancellationToken token, string originalName = "synthetic.jpg")
        {
            string? adminConnection = Environment.GetEnvironmentVariable("SHUTTERSPOOL_INGEST_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(adminConnection)) throw SkipException.ForSkip("Set SHUTTERSPOOL_INGEST_TEST_CONNECTION to run the PostgreSQL integration check.");
            string databaseName = $"m107_{Guid.NewGuid():N}";
            var admin = new NpgsqlConnection(adminConnection);
            await admin.OpenAsync(token);
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync(token);
            string connection = new NpgsqlConnectionStringBuilder(adminConnection) { Database = databaseName }.ConnectionString;
            var ingest = new DbContextOptionsBuilder<ShutterSpoolDbContext>().UseNpgsql(connection).Options;
            Guid sourceId = Guid.NewGuid(), destinationId = Guid.NewGuid(), captureId = Guid.NewGuid(), assetId = Guid.NewGuid();
            string spoolRoot = Path.Combine(Path.GetTempPath(), $"shutterspool-{databaseName}");
            byte[] bytes = [4, 9, 2, 6, 1, 8, 3, 7, 5];
            string role = originalName.EndsWith(".ARW", StringComparison.OrdinalIgnoreCase) ? "RAW" : "JPEG";
            string mediaType = role == "RAW" ? "image/x-sony-arw" : "image/jpeg";
            string key = $"spool/{assetId:N}";
            try
            {
                await using (var database = new ShutterSpoolDbContext(ingest))
                {
                    await new DatabaseInitializer(database).InitializeAsync(spoolRoot, token);
                    database.Sources.Add(new SourceEntity { Id = sourceId, Name = "synthetic" });
                    database.SourceCredentials.Add(new SourceCredentialEntity
                        { SourceId = sourceId, Username = "synthetic", VirtualRoot = "sources/test", SecretVerifier = SHA256.HashData(bytes) });
                    database.Destinations.Add(new DestinationEntity { Id = destinationId, Name = "controlled", Kind = "local" });
                    await database.SaveChangesAsync(token);
                    Directory.CreateDirectory(Path.Combine(spoolRoot, "spool"));
                    await File.WriteAllBytesAsync(Path.Combine(spoolRoot, key), bytes, token);
                    string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    await database.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO assets ("Id", "SourceId", "OriginalName", "SpoolKey", "Size", "Sha256", "IngestedAt", "RecoveryCorrelation", "State", "MetadataState", "MetadataRole", "DetectedMediaType")
                        VALUES ({assetId}, {sourceId}, {originalName}, {key}, {bytes.Length}, {hash}, {DateTimeOffset.UtcNow}, {Guid.NewGuid()}, 'Spooled', 'Complete', {role}, {mediaType})
                        """, token);
                    await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO captures (\"Id\", \"SourceId\", \"NormalizedStem\", \"LastPrimaryIngestedAt\") VALUES ({captureId}, {sourceId}, 'SYNTHETIC', {DateTimeOffset.UtcNow})", token);
                    await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO capture_assets (\"AssetId\", \"CaptureId\", \"Role\", \"IsPrimary\", \"GroupingReason\") VALUES ({assetId}, {captureId}, {role}, true, 'controlled_test')", token);
                    await new DestinationRoutingAdminService(database).SetAsync(new(destinationId, true,
                        [new("all", "All", 0, true, [], "folder", "{suppliedName}")]), token);
                    Assert.True((await new CaptureCurationService(new EfCaptureCurationStore(database, TimeProvider.System)).ReleaseNowAsync(captureId, 0, token)).Succeeded);
                    Assert.True(await new EfCaptureRoutingProcessor(database).ProcessOneAsync(DateTimeOffset.UtcNow, token));
                }
                return new(databaseName, admin, spoolRoot, ingest, destinationId, captureId, assetId,
                    new FileSystemSpool(new FileSystemSpoolOptions { RootPath = spoolRoot, ReservedBytes = 0 }));
            }
            catch
            {
                Directory.Delete(spoolRoot, recursive: true);
                NpgsqlConnection.ClearAllPools();
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync(token);
                await admin.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Directory.Delete(SpoolRoot, recursive: true);
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await admin.DisposeAsync();
        }
    }
}
