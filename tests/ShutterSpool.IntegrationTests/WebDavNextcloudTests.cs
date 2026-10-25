using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShutterSpool.Application.Delivery;
using ShutterSpool.Domain.Delivery;
using ShutterSpool.Infrastructure.Destinations.WebDav;
using ShutterSpool.Infrastructure.Persistence;
using ShutterSpool.Infrastructure.Security;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit.Sdk;

namespace ShutterSpool.IntegrationTests;

public sealed partial class DeliveryWorkerPostgresTests
{
    [Fact]
    public async Task Nextcloud_hostname_mismatch_requires_correction_before_delivery()
    {
        ProviderProfile profile = ReadProviderProfile();
        var url = new UriBuilder(profile.Url) { Host = "mismatch.example.test" };
        if (new Uri(profile.Url).Host != "localhost")
            throw SkipException.ForSkip("The hostname mismatch probe requires the controlled localhost certificate profile.");
        using var clients = new NextcloudClients(new Uri(profile.Url).Port);
        byte[] bytes = [1, 2, 3];
        var request = new DeliveryRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "synthetic.jpg", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "synthetic", "webdav",
            new WebDavDestinationDeliveryConfiguration(url.Uri.AbsoluteUri, profile.Username, profile.Password));
        using var content = new MemoryStream(bytes);
        DeliveryResult result = await new WebDavDestinationDeliveryAdapter(clients).WriteAsync(request, content, TestContext.Current.CancellationToken);
        Assert.Equal(DeliveryResultKind.ConfigurationFailure, result.Kind);
        Assert.Equal("TLS_VALIDATION_FAILED", result.ErrorCode);
    }

    [Theory]
    [InlineData("synthetic.jpg")]
    [InlineData("synthetic.ARW")]
    public async Task Nextcloud_outage_keeps_local_confirmation_and_remote_due_time_across_worker_restart(string originalName)
    {
        ProviderProfile profile = ReadProviderProfile();
        string? container = Environment.GetEnvironmentVariable("SHUTTERSPOOL_NEXTCLOUD_CONTAINER");
        if (container is null || !container.StartsWith("m201-", StringComparison.Ordinal)
            || !container.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw SkipException.ForSkip("Set SHUTTERSPOOL_NEXTCLOUD_CONTAINER to an explicitly disposable m201-* container.");
        CancellationToken token = TestContext.Current.CancellationToken;
        await using TestDatabase fixture = await TestDatabase.CreateAsync(token, originalName);
        using var clients = new NextcloudClients();
        string root = await CreateProviderCollectionAsync(profile, clients, token);
        var protector = new SecretProtector(Options.Create(new CredentialProtectionOptions
            { EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }));
        await using var database = new ShutterSpoolDbContext(fixture.DbOptions);
        var local = new ShutterSpool.Infrastructure.Destinations.Local.LocalDestinationDeliveryAdapter();
        string localRoot = Path.Combine(fixture.SpoolRoot, "local");
        Directory.CreateDirectory(localRoot);
        Assert.Null(await new ShutterSpool.Infrastructure.Destinations.Local.LocalDestinationConfigurationService(database)
            .ConfigureAsync(fixture.DestinationId, localRoot, token));
        Guid remote = Guid.NewGuid();
        database.Destinations.Add(new DestinationEntity { Id = remote, Name = "synthetic-webdav", Kind = "webdav" });
        await database.SaveChangesAsync(token);
        var configuration = new EfWebDavDestinationConfiguration(database, protector, new WebDavDestinationDeliveryAdapter(clients));
        Assert.Null(await configuration.ConfigureAsync(remote, root, profile.Username, profile.Password, token));
        Assert.True((await configuration.TestAsync(remote, token)).Writable);
        await new DestinationRoutingAdminService(database).SetAsync(new(remote, true,
            [new("all", "All", 0, true, [], "nested", "{suppliedName}")]), token);
        var actions = CreateActions(database, fixture.Originals);
        var preview = await actions.PreviewReevaluateAsync(fixture.AssetId, remote, token);
        Assert.Equal(ShutterSpool.Application.Routing.RoutingActionStatus.Recorded,
            (await actions.ReevaluateAsync(fixture.AssetId, remote, preview.ConfirmationToken, token)).Status);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var adapter = new WebDavDestinationDeliveryAdapter(clients);
        DeliveryProcessingService Worker() => new(new EfDeliveryWorkProcessor(new TestDbFactory(fixture.DbOptions), clock,
            new DeliveryWorkerSettings(), protector), fixture.Originals, new DeliveryAdapterRegistry([local, adapter]), new DeliveryWorkerSettings(), clock);
        try
        {
            await ProviderContainerAsync("stop", container, token);
            Assert.True(await Worker().ProcessOneAsync(token));
            Assert.Equal("Delivered", await database.Database.SqlQuery<string>($"SELECT \"State\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {fixture.DestinationId}").SingleAsync(token));
            Assert.True(await Worker().ProcessOneAsync(token));
            Assert.Equal("RetryWait", await database.Database.SqlQuery<string>($"SELECT \"State\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token));
            var due = await database.Database.SqlQuery<DateTimeOffset>($"SELECT \"NextAttemptAt\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token);
            Assert.False(await Worker().ProcessOneAsync(token));
            Assert.Equal(due, await database.Database.SqlQuery<DateTimeOffset>($"SELECT \"NextAttemptAt\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token));
            Assert.Equal("Delivered", await database.Database.SqlQuery<string>($"SELECT \"State\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {fixture.DestinationId}").SingleAsync(token));
        }
        finally { await ProviderContainerAsync("start", container, CancellationToken.None); }
        // Container start returns before Apache's listener. This bounded wait is provider readiness, not delivery retry acceleration.
        await Task.Delay(TimeSpan.FromSeconds(3), token);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await Worker().ProcessOneAsync(token));
        Assert.Equal("Delivered", await database.Database.SqlQuery<string>($"SELECT \"State\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token));
        Assert.Equal("AcknowledgedCompleteWrite", await database.Database.SqlQuery<string>($"SELECT \"ConfirmationEvidence\" AS \"Value\" FROM deliveries WHERE \"DestinationId\" = {remote}").SingleAsync(token));
        Assert.Equal(1, await database.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM delivery_attempts a JOIN deliveries d ON d.\"Id\" = a.\"DeliveryId\" WHERE d.\"DestinationId\" = {fixture.DestinationId}").SingleAsync(token));
        Assert.Null(await configuration.ConfigureAsync(remote, root, profile.Username, profile.Password, token));
        Assert.Equal("IDENTITY_LOCKED", await configuration.ConfigureAsync(remote, profile.Url, profile.Username, profile.Password, token));
        byte[] original = await File.ReadAllBytesAsync(Path.Combine(fixture.SpoolRoot, "spool", fixture.AssetId.ToString("N")), token);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(localRoot, "folder", originalName), token));
    }

    private static async Task ProviderContainerAsync(string operation, string container, CancellationToken token)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(operation);
        if (operation == "stop") { start.ArgumentList.Add("--time"); start.ArgumentList.Add("2"); }
        start.ArgumentList.Add(container);
        using Process process = Process.Start(start) ?? throw new IOException("Could not run the controlled provider lifecycle command.");
        await process.WaitForExitAsync(token);
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task Nextcloud_staged_publication_preserves_conflicts_and_verifies_large_nonseekable_original()
    {
        ProviderProfile profile = ReadProviderProfile();
        CancellationToken token = TestContext.Current.CancellationToken;
        using var clients = new NextcloudClients();
        var adapter = new WebDavDestinationDeliveryAdapter(clients);
        string root = await CreateProviderCollectionAsync(profile, clients, token);
        var configuration = new WebDavDestinationDeliveryConfiguration(root, profile.Username, profile.Password);
        using var bytes = new PatternOriginal(16 * 1024 * 1024);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(bytes, token)).ToLowerInvariant();
        bytes.Rewind();
        var request = new DeliveryRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "nested/space ü/100% synthetic.ARW", bytes.Size, hash, "synthetic", "webdav", configuration);

        DeliveryResult result = await adapter.WriteAsync(request, bytes, token);
        Assert.Equal(DeliveryResultKind.Confirmed, result.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.AcknowledgedCompleteWrite, result.Evidence);
        Assert.Equal(hash, clients.LastUploadHash?.ToLowerInvariant());
        Assert.Equal(0, clients.LargeDownloads);
        Assert.True(bytes.LargestRead <= 64 * 1024, "Uploads must read original bytes with bounded buffers.");
        using HttpClient independent = clients.CreateClient("webdav");
        using var get = ProviderRequest(HttpMethod.Get, new Uri(root + "nested/space%20%C3%BC/100%25%20synthetic.ARW"), profile);
        using HttpResponseMessage response = await independent.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes.Size, response.Content.Headers.ContentLength);
        await using Stream observed = await response.Content.ReadAsStreamAsync(token);
        Assert.Equal(hash, Convert.ToHexString(await SHA256.HashDataAsync(observed, token)).ToLowerInvariant());
        int downloads = clients.LargeDownloads;
        DeliveryReconciliation recovered = await adapter.ReconcileAsync(request, token);
        Assert.Equal(DeliveryReconciliationKind.Confirmed, recovered.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.ProviderReceipt, recovered.Evidence);
        Assert.Equal(downloads, clients.LargeDownloads);

        using var different = new MemoryStream([7, 8, 9]);
        DeliveryResult conflict = await adapter.WriteAsync(request with { AttemptId = Guid.NewGuid(), Size = 3,
            Sha256 = Convert.ToHexString(SHA256.HashData([7, 8, 9])).ToLowerInvariant() }, different, token);
        Assert.Equal(DeliveryResultKind.ConfigurationFailure, conflict.Kind);
        Assert.Equal(DeliveryReconciliationKind.Confirmed, (await adapter.ReconcileAsync(request, token)).Kind);
        using var jpeg = new MemoryStream([0xff, 0xd8, 0xff, 0xd9]);
        Assert.Equal(DeliveryResultKind.Confirmed, (await adapter.WriteAsync(request with { AttemptId = Guid.NewGuid(),
            TargetPath = "nested/synthetic.JPG", Size = jpeg.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(jpeg.ToArray())).ToLowerInvariant() }, jpeg, token)).Kind);
    }

    [Fact]
    public async Task Nextcloud_lost_move_response_recovers_by_checksum_without_media_download()
    {
        ProviderProfile profile = ReadProviderProfile();
        CancellationToken token = TestContext.Current.CancellationToken;
        using var clients = new NextcloudClients { LoseNextMediaMoveResponse = true };
        string root = await CreateProviderCollectionAsync(profile, clients, token);
        byte[] bytes = new byte[16 * 1024];
        RandomNumberGenerator.Fill(bytes);
        var request = new DeliveryRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "synthetic-lost-response.ARW", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), "synthetic", "webdav",
            new WebDavDestinationDeliveryConfiguration(root, profile.Username, profile.Password));
        var adapter = new WebDavDestinationDeliveryAdapter(clients);
        using var content = new MemoryStream(bytes);

        Assert.Equal(DeliveryResultKind.Uncertain, (await adapter.WriteAsync(request, content, token)).Kind);
        DeliveryReconciliation recovered = await adapter.ReconcileAsync(request, token);
        Assert.Equal(DeliveryReconciliationKind.Confirmed, recovered.Kind);
        Assert.Equal(DeliveryConfirmationEvidenceLevel.ProviderReceipt, recovered.Evidence);
        Assert.Equal(0, clients.LargeDownloads);

        using HttpClient independent = clients.CreateClient("webdav");
        using var get = ProviderRequest(HttpMethod.Get, new Uri(root + request.TargetPath), profile);
        using HttpResponseMessage response = await independent.SendAsync(get, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync(token));
    }

    private static ProviderProfile ReadProviderProfile()
    {
        string? path = Environment.GetEnvironmentVariable("SHUTTERSPOOL_NEXTCLOUD_PROFILE");
        if (string.IsNullOrWhiteSpace(path)) throw SkipException.ForSkip("Set SHUTTERSPOOL_NEXTCLOUD_PROFILE for controlled HTTPS Nextcloud acceptance.");
        return JsonSerializer.Deserialize<ProviderProfile>(File.ReadAllText(path), new JsonSerializerOptions
            { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Provider profile is missing.");
    }

    private static async Task<string> CreateProviderCollectionAsync(ProviderProfile profile, NextcloudClients clients, CancellationToken token)
    {
        string root = profile.Url.TrimEnd('/') + "/m201-" + Guid.NewGuid().ToString("N") + "/";
        using HttpClient client = clients.CreateClient("webdav");
        using var create = ProviderRequest(new HttpMethod("MKCOL"), new Uri(root), profile);
        using HttpResponseMessage response = await client.SendAsync(create, token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return root;
    }

    private static HttpRequestMessage ProviderRequest(HttpMethod method, Uri url, ProviderProfile profile)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(profile.Username + ":" + profile.Password)));
        return request;
    }

    private sealed record ProviderProfile(string Url, string Username, string Password);

    private sealed class NextcloudClients : IHttpClientFactory, IDisposable
    {
        private readonly SocketsHttpHandler _handler = new() { AllowAutoRedirect = false, UseProxy = false };
        private readonly TransferObserver _observer;
        public string? LastUploadHash { get; private set; }
        public int LargeDownloads { get; private set; }
        public bool LoseNextMediaMoveResponse { get; set; }
        public NextcloudClients(int? loopbackPort = null)
        {
            _observer = new(this, _handler);
            if (loopbackPort is not int port) return;
            // Pin the controlled socket while preserving normal TLS hostname validation for the mismatching URL.
            _handler.ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(IPAddress.Loopback, port, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            };
        }
        public HttpClient CreateClient(string name) => new(_observer, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        public void Dispose() => _observer.Dispose();

        private sealed class TransferObserver(NextcloudClients owner, HttpMessageHandler inner) : DelegatingHandler(inner)
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                HttpResponseMessage response = await base.SendAsync(request, token);
                if (request.Method == HttpMethod.Put && request.Headers.Contains("X-Hash"))
                    owner.LastUploadHash = response.Headers.TryGetValues("X-Hash-SHA256", out var hashes) ? hashes.Single() : null;
                if (request.Method == HttpMethod.Get && response.IsSuccessStatusCode && response.Content.Headers.ContentLength > 1024)
                    owner.LargeDownloads++;
                if (owner.LoseNextMediaMoveResponse && request.Method.Method == "MOVE"
                    && !request.RequestUri!.AbsolutePath.Contains("probe-", StringComparison.Ordinal) && response.IsSuccessStatusCode)
                {
                    owner.LoseNextMediaMoveResponse = false;
                    response.Dispose();
                    throw new HttpRequestException("Synthetic lost MOVE response after server completion.");
                }
                return response;
            }
        }
    }

    // Repeated synthetic bytes provide large streaming evidence without allocating a payload-sized array.
    private sealed class PatternOriginal(long size) : Stream
    {
        private long offset;
        public long Size => size;
        public int LargestRead { get; private set; }
        public void Rewind() => offset = 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count)
        {
            LargestRead = Math.Max(LargestRead, count);
            int read = (int)Math.Min(count, size - offset);
            for (int index = 0; index < read; index++) buffer[start + index] = (byte)((offset + index) % 251);
            offset += read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long position, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long length) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
}
