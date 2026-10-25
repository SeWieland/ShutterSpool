using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShutterSpool.Infrastructure.Destinations.Local;
using ShutterSpool.Infrastructure.Destinations.WebDav;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using System.Diagnostics;
using System.Security.Cryptography;

namespace ShutterSpool.IntegrationTests;

public sealed partial class DeliveryWorkerPostgresTests
{
    [Fact]
    public async Task Nextcloud_server_process_restart_preserves_backoff_and_recovers_without_valkey()
    {
        ProviderProfile profile = ReadProviderProfile();
        string? container = Environment.GetEnvironmentVariable("SHUTTERSPOOL_NEXTCLOUD_CONTAINER");
        if (container is null || !container.StartsWith("m201-", StringComparison.Ordinal)
            || !container.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw Xunit.Sdk.SkipException.ForSkip("Set SHUTTERSPOOL_NEXTCLOUD_CONTAINER to a disposable m201-* container.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestDatabase fixture = await TestDatabase.CreateAsync(token);
        using var clients = new NextcloudClients();
        string root = await CreateProviderCollectionAsync(profile, clients, token);
        string key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions { EncryptionKey = key }));
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        AssetEntity asset = await database.Assets.SingleAsync(token);
        database.Operations.Add(new IngestOperationEntity
        {
            Id = asset.RecoveryCorrelation, AssetId = asset.Id, SourceId = asset.SourceId,
            OriginalName = asset.OriginalName, PartialKey = "incoming/" + asset.Id.ToString("N"),
            FinalKey = asset.SpoolKey, State = "Committed", Size = asset.Size, Sha256 = asset.Sha256,
        });
        await database.SaveChangesAsync(token);
        string localRoot = Path.Combine(fixture.SpoolRoot, "local");
        Directory.CreateDirectory(localRoot);
        Assert.Null(await new LocalDestinationConfigurationService(database).ConfigureAsync(fixture.DestinationId, localRoot, token));
        Guid remote = Guid.NewGuid();
        database.Destinations.Add(new DestinationEntity { Id = remote, Name = "synthetic-runtime-webdav", Kind = "webdav" });
        await database.SaveChangesAsync(token);
        Assert.Null(await new EfWebDavDestinationConfiguration(database, protector, new WebDavDestinationDeliveryAdapter(clients))
            .ConfigureAsync(remote, root, profile.Username, profile.Password, token));
        await new DestinationRoutingAdminService(database).SetAsync(new(remote, true,
            [new("all", "All", 0, true, [], "runtime", "{suppliedName}")]), token);
        var actions = CreateActions(database, fixture.Originals);
        var preview = await actions.PreviewReevaluateAsync(fixture.AssetId, remote, token);
        Assert.Equal(ShutterSpool.Application.Routing.RoutingActionStatus.Recorded,
            (await actions.ReevaluateAsync(fixture.AssetId, remote, preview.ConfirmationToken, token)).Status);
        Process? server = null;
        try
        {
            await ProviderContainerAsync("stop", container, token);
            server = StartAcceptanceServer(database.Database.GetConnectionString()!, fixture.SpoolRoot, key);
            await WaitForDeliveryStateAsync(database, remote, "RetryWait", server, token);
            Assert.Equal("Delivered", await DeliveryStateAsync(database, fixture.DestinationId, token));
            DateTimeOffset due = await database.Database.SqlQuery<DateTimeOffset>($"SELECT \"NextAttemptAt\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token);
            await StopAcceptanceServerAsync(server);
            server.Dispose();
            server = StartAcceptanceServer(database.Database.GetConnectionString()!, fixture.SpoolRoot, key);
            Assert.Equal(due, await database.Database.SqlQuery<DateTimeOffset>($"SELECT \"NextAttemptAt\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token));
            await ProviderContainerAsync("start", container, token);
            if (DateTimeOffset.UtcNow < due)
            {
                Assert.Equal("RetryWait", await DeliveryStateAsync(database, remote, token));
                Assert.Equal(1, await database.Database.SqlQuery<int>($"SELECT \"AttemptCount\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token));
            }
            await WaitForDeliveryStateAsync(database, remote, "Delivered", server, token);
            Assert.Equal("AcknowledgedCompleteWrite", await database.Database.SqlQuery<string>($"SELECT \"ConfirmationEvidence\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token));
            Assert.Equal(1, await database.Database.SqlQuery<int>($"SELECT \"AttemptCount\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {fixture.DestinationId}").SingleAsync(token));
            byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, asset.SpoolKey), token);
            Assert.Equal(asset.Sha256, Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant());
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(localRoot, "folder", asset.OriginalName), token));
        }
        finally
        {
            if (server is not null) { await StopAcceptanceServerAsync(server); server.Dispose(); }
            await ProviderContainerAsync("start", container, CancellationToken.None);
        }
    }

    private static Process StartAcceptanceServer(string connection, string spoolRoot, string key)
    {
        string tests = typeof(DeliveryWorkerPostgresTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(tests)!,
        };
        foreach (string argument in new[] { "exec", "--runtimeconfig", Path.ChangeExtension(tests, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(tests, ".deps.json"), typeof(Program).Assembly.Location,
            "--urls", "http://127.0.0.1:0" }) start.ArgumentList.Add(argument);
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__shutterspool"] = connection;
        start.Environment["ConnectionStrings__valkey"] = "127.0.0.1:1,abortConnect=false,connectRetry=0";
        start.Environment["ShutterSpool__Spool__RootPath"] = spoolRoot;
        start.Environment["ShutterSpool__Spool__ReservedBytes"] = "0";
        start.Environment["ShutterSpool__Security__EncryptionKey"] = key;
        start.Environment["ShutterSpool__Security__Authority"] = "https://issuer.example.test";
        start.Environment["ShutterSpool__Security__Audience"] = "shutterspool-server";
        start.Environment["ShutterSpool__CameraIngress__Enabled"] = "false";
        start.Environment["ShutterSpool__Preview__PrewarmMode"] = "Off";
        start.Environment["ShutterSpool__DeliveryWorker__ScanIntervalSeconds"] = "1";
        start.Environment["Logging__LogLevel__Default"] = "Critical";
        start.Environment["Logging__LogLevel__Microsoft"] = "Critical";
        Process process = Process.Start(start) ?? throw new IOException("The controlled Server process could not start.");
        // Drain diagnostics without returning paths, credentials or provider bodies to test output.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task StopAcceptanceServerAsync(Process server)
    {
        if (!server.HasExited) server.Kill(); // Only this test's isolated child: simulate abrupt process loss.
        await server.WaitForExitAsync();
    }

    private static Task<string> DeliveryStateAsync(ShutterSpoolDbContext database, Guid destination, CancellationToken token) =>
        database.Database.SqlQuery<string>($"SELECT \"State\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {destination}").SingleAsync(token);

    private static async Task WaitForDeliveryStateAsync(ShutterSpoolDbContext database, Guid destination, string expected,
        Process server, CancellationToken token)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(70);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Assert.False(server.HasExited, "The isolated Server exited before reaching the expected durable delivery state.");
            if (await DeliveryStateAsync(database, destination, token) == expected) return;
            await Task.Delay(100, token);
        }
        Assert.Equal(expected, await DeliveryStateAsync(database, destination, token));
    }
}
