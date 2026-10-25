using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Application.Security;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Destinations.WebDav;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;

namespace ShutterSpool.IntegrationTests;

public sealed partial class DeliveryWorkerPostgresTests
{
    [Fact]
    public async Task WebDav_lost_move_recovery_persists_provider_receipt_and_fences_old_completion()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestDatabase fixture = await TestDatabase.CreateAsync(token);
        var protector = WebDavProtector();
        await SeedWebDavAsync(fixture, protector, token);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new EfDeliveryWorkProcessor(new TestDbFactory(fixture.DbOptions), clock,
            new DeliveryWorkerSettings { LeaseDurationSeconds = 10 }, protector);
        ClaimedDelivery old = (await store.ClaimAsync(token))!;
        string target = WebDavDestinationUris.Child(new Uri("https://dav.example.test/camera/"), old.Request.TargetPath.Split('/')).AbsolutePath;
        var handler = new WebDavDestinationAdapterTests.FakeDavHandler { LoseMoveResponseTarget = target };
        var adapter = new WebDavDestinationDeliveryAdapter(new WebDavDestinationAdapterTests.FakeHttpClientFactory(handler));
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, old.Asset.SpoolKey), token);
        DeliveryResult lost = await adapter.WriteAsync(old.Request, new MemoryStream(original), token);
        Assert.Equal(DeliveryResultKind.Uncertain, lost.Kind);

        clock.Advance(TimeSpan.FromSeconds(11));
        ClaimedDelivery current = (await store.ClaimAsync(token))!;
        Assert.True(current.Reconcile);
        DeliveryReconciliation recovered = await adapter.ReconcileAsync(current.Request, token);
        Assert.Equal(DeliveryReconciliationKind.Confirmed, recovered.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.ProviderReceipt, recovered.Evidence);
        await store.FinishReconciliationAsync(current, recovered, token);
        await store.FinishAsync(old, new(DeliveryResultKind.Confirmed, DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite), token);

        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        DeliveryEntity delivered = await database.Deliveries.AsNoTracking().SingleAsync(token);
        Assert.Equal(DeliveryState.Delivered, delivered.State);
        Assert.Equal("ProviderReceipt", delivered.ConfirmationEvidence);
        Assert.Equal(1, await database.DeliveryAttempts.CountAsync(token));
        Assert.Single(handler.Operations, item => item.Method == "MOVE" && !item.Path.Contains("probe-", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Operations, item => item.Method == "GET" && item.Path == target);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, old.Asset.SpoolKey), token));
    }

    [Fact]
    public async Task WebDav_late_move_after_expiry_preserves_ambiguity_and_fences_old_finish_despite_late_hash_evidence()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestDatabase fixture = await TestDatabase.CreateAsync(token);
        var protector = WebDavProtector();
        await SeedWebDavAsync(fixture, protector, token);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new EfDeliveryWorkProcessor(new TestDbFactory(fixture.DbOptions), clock,
            new DeliveryWorkerSettings { LeaseDurationSeconds = 10 }, protector);
        ClaimedDelivery old = (await store.ClaimAsync(token))!;
        var moveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new WebDavDestinationAdapterTests.FakeDavHandler { Intercept = async request =>
        {
            if (request.Method.Method == "MOVE" && !request.RequestUri!.AbsolutePath.Contains("probe-", StringComparison.Ordinal))
            {
                moveStarted.TrySetResult();
                await releaseMove.Task.WaitAsync(token); // The provider can finish an accepted MOVE after a lease is lost.
            }
            return null;
        } };
        var adapter = new WebDavDestinationDeliveryAdapter(new WebDavDestinationAdapterTests.FakeHttpClientFactory(handler));
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, old.Asset.SpoolKey), token);
        Task<DeliveryResult> pending = adapter.WriteAsync(old.Request, new MemoryStream(original), token);
        try
        {
            await moveStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            clock.Advance(TimeSpan.FromSeconds(11));
            ClaimedDelivery current = (await store.ClaimAsync(token))!;
            Assert.True(current.Reconcile);
            int writes = handler.Operations.Count(item => item.Method is "PUT" or "MOVE");
            DeliveryReconciliation missing = await adapter.ReconcileAsync(current.Request, token);
            Assert.Equal(DeliveryReconciliationKind.Uncertain, missing.Kind);
            Assert.Equal("ABSENCE_NOT_AUTHORITATIVE", missing.ErrorCode);
            await store.FinishReconciliationAsync(current, missing, token);
            await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
            Assert.Equal(DeliveryState.Ambiguous, (await database.Deliveries.AsNoTracking().SingleAsync(token)).State);
            Assert.Equal(writes, handler.Operations.Count(item => item.Method is "PUT" or "MOVE"));

            releaseMove.TrySetResult();
            DeliveryResult late = await pending;
            Assert.Equal(DeliveryResultKind.Confirmed, late.Kind);
            await store.FinishAsync(old, late, token);
            Assert.Equal(DeliveryState.Ambiguous, (await database.Deliveries.AsNoTracking().SingleAsync(token)).State);
            Assert.Null(await store.ClaimAsync(token));
            DeliveryReconciliation confirmed = await adapter.ReconcileAsync(current.Request, token);
            Assert.Equal(DeliveryReconciliationKind.Confirmed, confirmed.Kind);
            Assert.Equal(DeliveryConfirmationEvidenceLevel.ProviderReceipt, confirmed.Evidence);
            // Completed reconciliation no longer owns a claim; late evidence cannot rewrite durable history.
            await store.FinishReconciliationAsync(current, confirmed, token);
            DeliveryEntity ambiguous = await database.Deliveries.AsNoTracking().SingleAsync(token);
            Assert.Equal(DeliveryState.Ambiguous, ambiguous.State);
            Assert.Null(ambiguous.ConfirmationEvidence);
            Assert.Equal(1, await database.DeliveryAttempts.CountAsync(token));
            Assert.Equal(writes, handler.Operations.Count(item => item.Method is "PUT" or "MOVE"));
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, old.Asset.SpoolKey), token));
        }
        finally
        {
            releaseMove.TrySetResult();
            await pending;
        }
    }

    [Fact]
    public async Task WebDav_identity_change_waits_for_work_creation_then_rejects_the_new_identity()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestDatabase fixture = await TestDatabase.CreateAsync(token);
        var protector = WebDavProtector();
        await SeedWebDavAsync(fixture, protector, token);
        await using var writer = new ShutterSpoolDbContext(fixture.DbOptions);
        DeliveryEntity delivery = await writer.Deliveries.SingleAsync(token);
        await writer.Deliveries.ExecuteDeleteAsync(token);
        writer.ChangeTracker.Clear();
        await using var transaction = await writer.Database.BeginTransactionAsync(token);
        await writer.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('shutterspool-routing', 0))", token);
        await using var configurationDatabase = new ShutterSpoolDbContext(fixture.DbOptions);
        var configuration = new EfWebDavDestinationConfiguration(configurationDatabase, protector,
            new WebDavDestinationDeliveryAdapter(new WebDavDestinationAdapterTests.FakeHttpClientFactory(new())));
        Task<string?> edit = configuration.ConfigureAsync(fixture.DestinationId, "https://dav.example.test/other/", "other-user", "new-secret", token);
        Assert.True(await WaitForBlockedRoutingCommandAsync(fixture.DbOptions, token));
        writer.Deliveries.Add(delivery);
        await writer.SaveChangesAsync(token);
        await transaction.CommitAsync(token);

        Assert.Equal("IDENTITY_LOCKED", await edit);
        WebDavDestinationEntity saved = await writer.WebDavDestinations.AsNoTracking().SingleAsync(token);
        Assert.Equal("https://dav.example.test/camera/", saved.CollectionUrl);
        Assert.Equal("operator", saved.Username);
        Assert.Equal("synthetic-app-password", protector.Unprotect((await writer.DestinationCredentials.AsNoTracking().SingleAsync(token)).EncryptedSecret));
    }

    [Fact]
    public async Task WebDav_claim_keeps_one_credential_snapshot_while_replacement_waits_for_routing_lock()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestDatabase fixture = await TestDatabase.CreateAsync(token);
        var protector = WebDavProtector();
        await SeedWebDavAsync(fixture, protector, token);
        var blocking = new BlockingCredentialProtector(protector, token);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var store = new EfDeliveryWorkProcessor(new TestDbFactory(fixture.DbOptions), clock,
            new DeliveryWorkerSettings { LeaseDurationSeconds = 10 }, blocking);
        Task<ClaimedDelivery?> claiming = Task.Run(() => store.ClaimAsync(token), token);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        var configuration = new EfWebDavDestinationConfiguration(database, protector,
            new WebDavDestinationDeliveryAdapter(new WebDavDestinationAdapterTests.FakeHttpClientFactory(new())));
        Task<string?> replacement = configuration.ConfigureAsync(fixture.DestinationId, "https://dav.example.test/camera/", "operator", "replacement-app-password", token);
        try
        {
            Assert.True(await WaitForBlockedRoutingCommandAsync(fixture.DbOptions, token));
            blocking.Release.TrySetResult();
            ClaimedDelivery old = (await claiming)!;
            Assert.Null(await replacement);
            Assert.Equal("synthetic-app-password", Assert.IsType<WebDavDestinationDeliveryConfiguration>(old.Request.Configuration).Password);
            clock.Advance(TimeSpan.FromSeconds(11));
            ClaimedDelivery current = (await store.ClaimAsync(token))!;
            Assert.True(current.Reconcile);
            Assert.Equal("replacement-app-password", Assert.IsType<WebDavDestinationDeliveryConfiguration>(current.Request.Configuration).Password);
            Assert.Equal("synthetic-app-password", Assert.IsType<WebDavDestinationDeliveryConfiguration>(old.Request.Configuration).Password);
        }
        finally { blocking.Release.TrySetResult(); await claiming; await replacement; }
    }

    private static SecretProtector WebDavProtector() => new(Options.Create(new CredentialProtectionOptions
        { EncryptionKey = Convert.ToBase64String(new byte[32]) }));

    private static async Task SeedWebDavAsync(TestDatabase fixture, ICredentialProtector protector, CancellationToken token)
    {
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        await database.Destinations.ExecuteUpdateAsync(update => update.SetProperty(item => item.Kind, "webdav"), token);
        database.WebDavDestinations.Add(new() { DestinationId = fixture.DestinationId,
            CollectionUrl = "https://dav.example.test/camera/", Username = "operator" });
        database.DestinationCredentials.Add(new() { DestinationId = fixture.DestinationId,
            EncryptedSecret = protector.Protect("synthetic-app-password") });
        await database.SaveChangesAsync(token);
    }

    private static async Task<bool> WaitForBlockedRoutingCommandAsync(DbContextOptions<ShutterSpoolDbContext> options, CancellationToken token)
    {
        await using var observer = new ShutterSpoolDbContext(options);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            bool blocked = await observer.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database()
                    AND wait_event_type = 'Lock' AND query LIKE '%pg_advisory_xact_lock%') AS "Value"
                """).SingleAsync(token);
            if (blocked) return true;
            await Task.Delay(20, token);
        }
        return false;
    }

    private sealed class BlockingCredentialProtector(ICredentialProtector inner, CancellationToken token) : ICredentialProtector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string CreateSecret() => inner.CreateSecret();
        public byte[] CreateVerifier(string secret) => inner.CreateVerifier(secret);
        public bool Verify(string secret, byte[] verifier) => inner.Verify(secret, verifier);
        public string Protect(string secret) => inner.Protect(secret);
        public string Unprotect(string secret)
        {
            Started.TrySetResult();
            Release.Task.WaitAsync(TimeSpan.FromSeconds(10), token).GetAwaiter().GetResult();
            return inner.Unprotect(secret);
        }
    }
}
